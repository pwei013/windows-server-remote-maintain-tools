using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;

internal static class OperationHistoryWireSelfTests
{
    private const string ApiBase = "https://synthetic.example.invalid/openapi";
    private const string AppId = "SYNTHETIC-APP-ID";
    private const string Token = "SYNTHETIC-OPERATOR-TOKEN";
    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " exceptionType=" + exception.GetType().Name); }
        }
        foreach (var bits in new[] { 1024, 2048 })
            await Check($"cloud wire {bits}-bit RSA signed create and readback preserve Chinese operator", async () =>
            {
                using var rsa = RSA.Create(bits); var expected = Snapshot(); Dictionary<string, object?>? saved = null;
                using var repository = Repository(rsa, async (request, cancellation) =>
                {
                    using var payload = await Payload(request, rsa, cancellation); var root = payload.RootElement;
                    var method = request.RequestUri!.Segments[^2].Trim('/'); CheckEnvelope(request, root, method);
                    if (method == ZeroCodeOperationHistoryRepository.CreateMethod)
                    {
                        Require(root.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(
                            ZeroCodeOperationHistoryRepository.Fields(expected).Keys.Concat(["timeStamp", "sign"])));
                        saved = ZeroCodeOperationHistoryRepository.Fields(expected);
                        Require(root.GetProperty(ZeroCodeOperationHistoryRepository.OperatorNameField).GetString() == "合成操作人张三");
                        Require(!root.ToString().Contains(Token, StringComparison.Ordinal));
                        return Reply(new { ResultCode = "200", ReturnValue = "synthetic-mutation-receipt" });
                    }
                    Require(method == ZeroCodeOperationHistoryRepository.QueryMethod && saved is not null);
                    Require(root.GetProperty(ZeroCodeOperationHistoryRepository.OperationField).GetString() == expected.OperationId.ToString("D"));
                    saved!["primaryId"] = "123";
                    return Reply(new { ResultCode = "200", ReturnValue = new[] { saved }, TotalCount = "1" });
                });
                await repository.CreateAsync(expected, Token, default);
                var stored = await repository.FindAsync(expected.OperationId, Token, default);
                Require(stored?.PrimaryId == "123" && stored.Snapshot == expected);
            });
        await Check("cloud update sends precise primary key and only mutable fields, including error clearing", async () =>
        {
            using var rsa = RSA.Create(2048); var calls = 0;
            using var repository = Repository(rsa, async (request, cancellation) =>
            {
                calls++; using var payload = await Payload(request, rsa, cancellation); var root = payload.RootElement;
                CheckEnvelope(request, root, ZeroCodeOperationHistoryRepository.UpdateMethod);
                Require(root.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals([
                    "primaryId", ZeroCodeOperationHistoryRepository.StateField, ZeroCodeOperationHistoryRepository.UpdatedField,
                    ZeroCodeOperationHistoryRepository.ErrorField, "timeStamp", "sign"]));
                Require(root.GetProperty("primaryId").GetString() == "123");
                Require(root.GetProperty(ZeroCodeOperationHistoryRepository.ErrorField).GetString() == "");
                return Reply(new { ResultCode = "200" });
            });
            await repository.UpdateAsync("123", Snapshot() with { ErrorCode = null }, Token, default);
            await Reject(() => repository.UpdateAsync("", Snapshot(), Token, default), "OPERATION_HISTORY_PRIMARY_ID_INVALID");
            Require(calls == 1);
        });
        await Check("cloud query rejects duplicates, truncated pages, missing fields and timezone corruption", async () =>
        {
            using var rsa = RSA.Create(2048); var row = ZeroCodeOperationHistoryRepository.Fields(Snapshot()); row["primaryId"] = "123";
            object reply = new { ResultCode = "200", ReturnValue = new[] { row }, TotalCount = 1 };
            using var repository = Repository(rsa, (_, _) => Task.FromResult(Reply(reply)));
            Require((await repository.FindAsync(Snapshot().OperationId, Token, default))?.Snapshot == Snapshot());
            reply = new { ResultCode = "200", ReturnValue = new[] { row, row }, TotalCount = 2 };
            await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_DUPLICATE");
            reply = new { ResultCode = "200", ReturnValue = new[] { row }, TotalCount = 201 };
            await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_UNAVAILABLE");
            reply = new { ResultCode = "200", ReturnValue = new[] { row }, TotalCount = 1 };
            row.Remove(ZeroCodeOperationHistoryRepository.OperatorNameField);
            await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_UNAVAILABLE");
            row[ZeroCodeOperationHistoryRepository.OperatorNameField] = "合成操作人张三";
            row[ZeroCodeOperationHistoryRepository.AcceptedField] = "2026-10-13T08:00:00";
            await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_UNAVAILABLE");
        });
        await Check("cloud broad query pages beyond 200 and detects duplicate exact IDs on later pages", async () =>
        {
            using var rsa = RSA.Create(2048); var duplicate = false; var pages = new List<int>();
            var rows = Enumerable.Range(1, 201).Select(index =>
            {
                var row = ZeroCodeOperationHistoryRepository.Fields(Snapshot() with { OperationId = Guid.NewGuid() });
                row["primaryId"] = index.ToString(CultureInfo.InvariantCulture); return row;
            }).ToArray();
            rows[200][ZeroCodeOperationHistoryRepository.OperationField] = Snapshot().OperationId.ToString("D");
            using var repository = Repository(rsa, async (request, cancellation) =>
            {
                using var payload = await Payload(request, rsa, cancellation);
                var page = payload.RootElement.GetProperty("pageNumber").GetInt32(); pages.Add(page);
                if (duplicate) rows[0][ZeroCodeOperationHistoryRepository.OperationField] = Snapshot().OperationId.ToString("D");
                return Reply(new { ResultCode = "200", ReturnValue = rows.Skip((page - 1) * 200).Take(200).ToArray(), TotalCount = rows.Length });
            });
            Require((await repository.FindAsync(Snapshot().OperationId, Token, default))?.PrimaryId == "201" && pages.SequenceEqual([1, 2]));
            duplicate = true; await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_DUPLICATE");
        });
        await Check("cloud query exact identifier filtering never adopts an unrelated record", async () =>
        {
            using var rsa = RSA.Create(2048); var wrong = ZeroCodeOperationHistoryRepository.Fields(Snapshot() with { OperationId = Guid.NewGuid() });
            wrong["primaryId"] = "456";
            using var repository = Repository(rsa, (_, _) => Task.FromResult(Reply(new { ResultCode = "200", ReturnValue = new[] { wrong }, TotalCount = 1 })));
            Require(await repository.FindAsync(Snapshot().OperationId, Token, default) is null);
        });
        await Check("cloud business, authentication and malformed responses use safe error codes", async () =>
        {
            using var rsa = RSA.Create(2048); var mode = 0;
            using var repository = Repository(rsa, (_, _) => Task.FromResult(mode switch
            {
                0 => Reply(new { ResultCode = "500", ResultMsg = "SYNTHETIC_PRIVATE_REMOTE_MESSAGE" }),
                1 => Reply(new { ResultCode = "401" }),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SYNTHETIC_PRIVATE_INVALID_JSON") }
            }));
            await Reject(() => repository.CreateAsync(Snapshot(), Token, default), "OPERATION_HISTORY_UNAVAILABLE");
            mode = 1; await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "APP_USER_SESSION_EXPIRED");
            mode = 2; await Reject(() => repository.FindAsync(Snapshot().OperationId, Token, default), "OPERATION_HISTORY_UNAVAILABLE");
        });
        await Check("cloud client field codes match the independently read platform contract", async () =>
        {
            using var contract = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "docs", "operation-history-platform-contract.json")));
            Require(contract.RootElement.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("code").GetString()).ToHashSet()
                .SetEquals(ZeroCodeOperationHistoryRepository.Fields(Snapshot()).Keys));
            Require(contract.RootElement.GetProperty("apis").EnumerateArray().Select(api => api.GetProperty("method").GetString()).ToHashSet()
                .SetEquals([ZeroCodeOperationHistoryRepository.QueryMethod, ZeroCodeOperationHistoryRepository.CreateMethod, ZeroCodeOperationHistoryRepository.UpdateMethod]));
        });
        System.Console.WriteLine($"Operation history wire self-tests: {checks} checks, {failures} failures.");
        return failures == 0 ? 0 : 1;
    }

    private static OperationHistorySnapshot Snapshot() => new(Guid.Parse("12345678-1234-1234-1234-123456789abc"), "demo",
        OperationTypes.Start, "合成网站", OperationStates.Succeeded, "synthetic-user", "合成操作人张三", "127.0.0.1",
        new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 10, 13, 8, 1, 0, TimeSpan.FromHours(8)), new string('A', 64), "SYNTHETIC_ERROR");
    private static ZeroCodeOperationHistoryRepository Repository(RSA rsa, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) =>
        new(new ConsoleOptions { SiteAnnotations = new SiteAnnotationZeroCodeOptions
        { OpenApiBaseUrl = ApiBase, OpenApiAppId = AppId, PublicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) } }, true, new Handler(handle));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken); }
    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static async Task<JsonDocument> Payload(HttpRequestMessage request, RSA rsa, CancellationToken cancellation)
    {
        var encoded = JsonSerializer.Deserialize<string>(await request.Content!.ReadAsStringAsync(cancellation))!;
        var bytes = Convert.FromHexString(encoded); using var plain = new MemoryStream();
        for (var i = 0; i < bytes.Length; i += rsa.KeySize / 8)
            plain.Write(rsa.Decrypt(bytes.AsSpan(i, rsa.KeySize / 8).ToArray(), RSAEncryptionPadding.Pkcs1));
        return JsonDocument.Parse(plain.ToArray());
    }
    private static void CheckEnvelope(HttpRequestMessage request, JsonElement root, string method)
    {
        Require(request.RequestUri!.AbsoluteUri == ApiBase + "/" + method + "/" + AppId && request.Method == HttpMethod.Post);
        Require(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Token);
        var time = root.GetProperty("timeStamp").GetInt64(); Require(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - time) <= 10);
        Require(root.GetProperty("sign").GetString() == Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(AppId + method + time.ToString(CultureInfo.InvariantCulture)))));
    }
    private static async Task Reject(Func<Task> action, string code)
    {
        try { await action(); }
        catch (OperationHistoryException exception) when (exception.Code == code && exception.Message == code) { return; }
        throw new InvalidOperationException("TEST_EXPECTED_SAFE_FAILURE");
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("TEST_ASSERTION_FAILED"); }
}
