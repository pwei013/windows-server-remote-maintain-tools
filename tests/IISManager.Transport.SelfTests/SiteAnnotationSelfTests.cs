using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IISManager.Console;
using IISManager.Contracts;

internal static class SiteAnnotationSelfTests
{
    private const string Server = "synthetic-server";
    private const string OtherServer = "synthetic-server-2";
    private const string SiteName = "Portal.Web";
    private const long SiteId = 11;
    private const string ApiBase = "https://synthetic.example.invalid/openapi";
    private const string AppId = "SYNTHETIC-APP-ID";
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> RunAsync()
    {
        var checks = 0; var failures = 0;
        async Task Check(string name, Func<Task> assertion)
        {
            checks++;
            try { await assertion(); System.Console.WriteLine("PASS " + name); }
            catch (Exception exception) { failures++; System.Console.WriteLine("FAIL " + name + " type=" + exception.GetType().Name); }
        }

        await Check("annotation normalization and exact limits", () =>
        {
            Require(SiteAnnotationRules.TryNormalize(Request() with { Alias = "  业务门户  ", Tags = ["  API ", "api", "", "生产", "生产"] }, out var normalized));
            Require(normalized.Alias == "业务门户" && normalized.Tags.SequenceEqual(["API", "生产"]));
            Require(SiteAnnotationRules.TryNormalize(Request() with { Alias = "", Tags = [] }, out _));
            Require(SiteAnnotationRules.TryNormalize(Request() with { Alias = new string('中', 100), Tags = Enumerable.Range(0, 20).Select(index => new string('文', 38) + index.ToString("00", CultureInfo.InvariantCulture)).ToArray() }, out _));
            Require(SiteAnnotationRules.TryNormalize(Request() with { ExpectedVersion = new string('A', 64) }, out _));
            foreach (var invalid in new[]
            {
                Request() with { SiteId = 0 }, Request() with { SiteId = -1 }, Request() with { SiteName = " " },
                Request() with { SiteName = new string('a', 256) }, Request() with { SiteName = "site\n" },
                Request() with { Alias = null! }, Request() with { Alias = new string('a', 101) },
                Request() with { Alias = "alias\t" }, Request() with { Alias = "\u007f" }, Request() with { Alias = "\u0085" },
                Request() with { Tags = null! }, Request() with { Tags = [null!] }, Request() with { Tags = ["tag\n"] },
                Request() with { Tags = [new string('a', 41)] }, Request() with { Tags = Enumerable.Range(0, 21).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToArray() },
                Request() with { ExpectedVersion = null! }, Request() with { ExpectedVersion = "short" }, Request() with { ExpectedVersion = new string('Z', 64) }
            }) Require(!SiteAnnotationRules.TryNormalize(invalid, out _));
            return Task.CompletedTask;
        });

        await Check("site identity hashes separate tuple boundaries names ids and servers", () =>
        {
            var keys = new[]
            {
                SiteAnnotationRules.SiteKey("ab", 1, "c"), SiteAnnotationRules.SiteKey("a", 1, "bc"),
                SiteAnnotationRules.SiteKey(Server, SiteId, SiteName), SiteAnnotationRules.SiteKey(OtherServer, SiteId, SiteName),
                SiteAnnotationRules.SiteKey(Server, SiteId + 1, SiteName), SiteAnnotationRules.SiteKey(Server, SiteId, "portal.web"),
                SiteAnnotationRules.SiteKey(Server, SiteId, "旧站点名"), SiteAnnotationRules.SiteKey("a\"b", 1, "c")
            };
            Require(keys.Distinct(StringComparer.Ordinal).Count() == keys.Length);
            Require(keys.All(key => key.Length == 64 && key.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F')));
            Require(keys[2] == SiteAnnotationRules.SiteKey(Server, SiteId, SiteName));
            return Task.CompletedTask;
        });

        await Check("application paths and keys preserve site keys and separate every identity", () =>
        {
            var siteKey = SiteAnnotationRules.SiteKey(Server, SiteId, SiteName);
            var key = SiteAnnotationRules.SiteKey(Server, SiteId, SiteName, "/api");
            Require(siteKey.Length == 64 && key.Length == 128 && key.StartsWith(siteKey, StringComparison.Ordinal));
            Require(new[] { key, siteKey, SiteAnnotationRules.SiteKey(Server, SiteId, SiteName, "/api/v2"),
                SiteAnnotationRules.SiteKey(OtherServer, SiteId, SiteName, "/api"),
                SiteAnnotationRules.SiteKey(Server, SiteId + 1, SiteName, "/api"),
                SiteAnnotationRules.SiteKey(Server, SiteId, "Other.Web", "/api") }.Distinct().Count() == 6);
            Require(SiteAnnotationRules.TryNormalize(Request() with { ApplicationPath = "/应用 一/v2" }, out _));
            foreach (var path in new[] { null, "/", "api", "//api", "/api/", "/api/..", "/api/./v2", "/api\\v2", "/api?x", "/api#x", "/api\n", "/" + new string('a', 2048) })
                Require(!SiteAnnotationRules.TryNormalize(Request() with { ApplicationPath = path! }, out _));
            return Task.CompletedTask;
        });

        await Check("applications save and read independently of their site and other servers", async () =>
        {
            var repository = new MemoryRepository(); var service = ApplicationService(repository);
            await service.SaveAsync(Server, Request(), default);
            var request = Request() with { ApplicationPath = "/api", Alias = "接口", Tags = ["应用"] };
            var saved = await service.SaveAsync(Server, request, default);
            await service.SaveAsync(Server, request with { ApplicationPath = "/api/v2", Alias = "二级接口" }, default);
            await service.SaveAsync(OtherServer, request with { Alias = "另一台" }, default);
            await Reject(() => service.SaveAsync(Server, request, default), "SITE_ANNOTATION_CONFLICT", 409);
            var updated = await service.SaveAsync(Server, request with { Alias = "", Tags = [], ExpectedVersion = saved.Version }, default);
            Require(updated.ApplicationPath == "/api" && updated.Alias == "" && updated.Tags.Length == 0);
            var entries = (await service.GetAsync(Server, default)).Entries;
            Require(entries.Length == 3 && entries.Single(entry => entry.ApplicationPath == "").Alias == "业务门户");
            Require(entries.Single(entry => entry.ApplicationPath == "/api").Version == updated.Version);
            Require(entries.Single(entry => entry.ApplicationPath == "/api/v2").Alias == "二级接口");
            Require((await service.GetAsync(OtherServer, default)).Entries.Single().Alias == "另一台");
            Require(repository.Creates == 4 && repository.Updates == 1);
        });

        await Check("removed renamed or wrong-site applications never write and stale entries are hidden", async () =>
        {
            var repository = new MemoryRepository(); var service = ApplicationService(repository);
            await service.SaveAsync(Server, Request() with { ApplicationPath = "/api" }, default);
            var missing = ApplicationService(repository, [new SiteTopology { Id = SiteId + 1, Name = SiteName, Applications = [new() { Path = "/api" }] }]);
            var before = repository.Mutations;
            await Reject(() => missing.SaveAsync(Server, Request() with { ApplicationPath = "/api" }, default), "SITE_TARGET_CHANGED", 409);
            Require((await missing.GetAsync(Server, default)).Entries.Length == 0 && repository.Mutations == before);
            await Reject(() => service.SaveAsync(Server, Request() with { ApplicationPath = "/missing" }, default), "SITE_TARGET_CHANGED", 409);
            await Reject(() => Service(repository).SaveAsync(Server, Request() with { ApplicationPath = "/api" }, default), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            Require(repository.Mutations == before);
        });

        await Check("same application path on two sites has independent persisted records", async () =>
        {
            var repository = new MemoryRepository();
            var sites = new[] { new SiteConfig { Id = SiteId, Name = SiteName }, new SiteConfig { Id = SiteId + 1, Name = "Other.Web" } };
            var service = new SiteAnnotationService(repository, _ => true, (_, _) => Task.FromResult(Snapshot(sites)),
                (_, _) => Task.FromResult(new SiteTopologySnapshot(DateTimeOffset.UtcNow,
                    sites.Select(site => new SiteTopology { Id = site.Id, Name = site.Name, Applications = [new() { Path = "/api" }] }).ToArray(), [])));
            var first = await service.SaveAsync(Server, Request() with { ApplicationPath = "/api", Alias = "第一站点" }, default);
            var second = await service.SaveAsync(Server, Request() with { SiteId = SiteId + 1, SiteName = "Other.Web", ApplicationPath = "/api", Alias = "第二站点" }, default);
            var read = await service.GetAsync(Server, default);
            Require(read.Entries.Length == 2 && read.Entries.Single(entry => entry.SiteId == SiteId).Alias == first.Alias &&
                read.Entries.Single(entry => entry.SiteId == SiteId + 1).Alias == second.Alias);
            Require(repository.Rows.Select(row => row.SiteKey).Distinct().Count() == 2);
        });

        await Check("application write timeouts recover by reading once and never replay", async () =>
        {
            foreach (var persist in new[] { true, false })
            {
                var repository = new MemoryRepository { FailMutation = true, PersistMutation = persist };
                var service = ApplicationService(repository); var request = Request() with { ApplicationPath = "/api" };
                if (persist) Require((await service.SaveAsync(Server, request, default)).ApplicationPath == "/api");
                else await Reject(() => service.SaveAsync(Server, request, default), "SITE_ANNOTATION_OUTCOME_UNKNOWN", 502);
                Require(repository.Mutations == 1 && repository.Trace.SequenceEqual(["query", "create", "query"]));
            }
        });

        await Check("application keys round trip through the existing platform fields with exact topology validation", async () =>
        {
            using var rsa = RSA.Create(1024);
            var row = Stored("app") with { SiteKey = SiteAnnotationRules.SiteKey(Server, SiteId, SiteName, "/api") };
            using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(QueryReply([WireRow(row)], 1))));
            var result = await ApplicationService(repository).GetAsync(Server, default);
            Require(result.Entries.Single().ApplicationPath == "/api");
            var forged = row with { SiteKey = SiteAnnotationRules.SiteKey(Server, SiteId, SiteName, "/not-current") };
            using var staleRepository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(QueryReply([WireRow(forged)], 1))));
            Require((await ApplicationService(staleRepository).GetAsync(Server, default)).Entries.Length == 0);
        });

        await Check("application saves use existing encrypted create and update contracts with the full 128-character key", async () =>
        {
            using var rsa = RSA.Create(1024); StoredSiteAnnotation? stored = null; var mutations = 0;
            var key = SiteAnnotationRules.SiteKey(Server, SiteId, SiteName, "/api");
            using var repository = Repository(rsa, new FixtureHandler(async (request, cancellation) =>
            {
                using var plain = await Payload(request, rsa, cancellation); var root = plain.RootElement;
                var method = request.RequestUri!.Segments[^2].Trim('/'); CheckEnvelope(request, root, method);
                if (method == ZeroCodeSiteAnnotationRepository.QueryMethod)
                {
                    Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString() == key);
                    return QueryReply(stored is null ? [] : new[] { WireRow(stored) }, stored is null ? 0 : 1);
                }
                mutations++;
                var alias = root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!;
                var tags = JsonSerializer.Deserialize<string[]>(root.GetProperty(ZeroCodeSiteAnnotationRepository.TagsField).GetString()!)!;
                if (method == ZeroCodeSiteAnnotationRepository.CreateMethod)
                {
                    Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString() == key && key.Length == 128);
                    Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.SiteNameField).GetString() == SiteName);
                    stored = new StoredSiteAnnotation("app-primary", key, Server, SiteId, SiteName, alias, tags);
                }
                else
                {
                    Require(method == ZeroCodeSiteAnnotationRepository.UpdateMethod && root.GetProperty("primaryId").GetString() == "app-primary");
                    stored = stored! with { Alias = alias, Tags = tags };
                }
                return MutationReply("app-primary");
            }));
            var service = ApplicationService(repository); var draft = Request() with { ApplicationPath = "/api" };
            var created = await service.SaveAsync(Server, draft, default);
            var updated = await service.SaveAsync(Server, draft with { Alias = "更新应用", ExpectedVersion = created.Version }, default);
            Require(updated.ApplicationPath == "/api" && updated.Alias == "更新应用" && mutations == 2);
        });

        await Check("annotation JSON requires explicit target alias tags and version", () =>
        {
            var fields = new Dictionary<string, object?>
            {
                ["siteId"] = SiteId, ["siteName"] = SiteName, ["alias"] = "", ["tags"] = Array.Empty<string>(), ["expectedVersion"] = ""
            };
            var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            Require(JsonSerializer.Deserialize<SaveSiteAnnotationRequest>(JsonSerializer.Serialize(fields), serializer)!.Tags.Length == 0);
            foreach (var field in fields.Keys)
            {
                var incomplete = fields.Where(pair => pair.Key != field).ToDictionary();
                var rejected = false;
                try { _ = JsonSerializer.Deserialize<SaveSiteAnnotationRequest>(JsonSerializer.Serialize(incomplete), serializer); }
                catch (JsonException) { rejected = true; }
                Require(rejected);
            }
            return Task.CompletedTask;
        });

        await Check("disabled empty options reject reads and saves before HTTP", async () =>
        {
            var calls = 0;
            using var repository = new ZeroCodeSiteAnnotationRepository(new SiteAnnotationZeroCodeOptions(), new FixtureHandler((_, _) =>
            {
                calls++; throw new InvalidOperationException("TEST_HTTP_MUST_NOT_RUN");
            }));
            var service = Service(repository);
            await Reject(() => service.GetAsync(Server, CancellationToken.None), "SITE_ANNOTATIONS_NOT_CONFIGURED", 503);
            await Reject(() => service.SaveAsync(Server, Request(), CancellationToken.None), "SITE_ANNOTATIONS_NOT_CONFIGURED", 503);
            Require(calls == 0);
        });

        await Check("enabled invalid options fail closed", () =>
        {
            foreach (var options in new[]
            {
                Options(application: 1002), Options(timeout: 0), Options(timeout: 121),
                Options(baseUrl: "http://synthetic.example.invalid/openapi"), Options(baseUrl: "https://user:password@synthetic.example.invalid/openapi"),
                Options(baseUrl: ApiBase + "?value=1"), Options(baseUrl: ApiBase + "#fragment"),
                Options(appId: " "), Options(appId: "invalid value"), Options(publicKeyBase64: " "), Options(publicKeyBase64: new string('A', 8193))
            })
            {
                try { options.Validate(); throw new InvalidOperationException("TEST_EXPECTED_CONFIGURATION_REJECTION"); }
                catch (InvalidOperationException exception) when (exception.Message == "SITE_ANNOTATIONS_CONFIGURATION_INVALID") { }
            }
            return Task.CompletedTask;
        });

        await Check("inline Base64 SPKI and PKCS1 keys use production configuration and encrypted HTTP", async () =>
        {
            using var rsa = RSA.Create(2048);
            foreach (var encoded in new[] { rsa.ExportSubjectPublicKeyInfo(), rsa.ExportRSAPublicKey() })
            {
                var calls = 0;
                var key = Convert.ToBase64String(encoded);
                using var repository = new ZeroCodeSiteAnnotationRepository(Options(publicKeyBase64: "\n" + key + "\n"), new FixtureHandler(async (request, cancellation) =>
                {
                    calls++;
                    using var payload = await Payload(request, rsa, cancellation);
                    CheckEnvelope(request, payload.RootElement, ZeroCodeSiteAnnotationRepository.QueryMethod);
                    return QueryReply([], 0);
                }));
                Require((await repository.QueryAsync(Server, null, CancellationToken.None)).Length == 0 && calls == 1);
            }
        });

        await Check("inline public key rejects paths PEM private keys malformed DER and trailing data before HTTP", () =>
        {
            using var rsa = RSA.Create(2048);
            var calls = 0;
            foreach (var invalid in new[]
            {
                @"D:\OpsKeys\public.pem", "not-base64!", rsa.ExportSubjectPublicKeyInfoPem(),
                Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), Convert.ToBase64String(rsa.ExportRSAPrivateKey()),
                Convert.ToBase64String([1, 2, 3]), Convert.ToBase64String([.. rsa.ExportSubjectPublicKeyInfo(), 0])
            })
            {
                try
                {
                    using var repository = new ZeroCodeSiteAnnotationRepository(Options(publicKeyBase64: invalid), new FixtureHandler((_, _) =>
                    {
                        calls++; throw new InvalidOperationException("TEST_HTTP_MUST_NOT_RUN");
                    }));
                    throw new InvalidOperationException("TEST_EXPECTED_PUBLIC_KEY_REJECTION");
                }
                catch (InvalidOperationException exception) when (exception.Message == "SITE_ANNOTATIONS_PUBLIC_KEY_INVALID") { }
            }
            Require(calls == 0);
            return Task.CompletedTask;
        });

        await Check("GET isolates server id reused ids and old site names", async () =>
        {
            var repository = new MemoryRepository();
            repository.Rows.AddRange([Stored("current"), Stored("other-server", OtherServer), Stored("old-name", name: "Previous.Web"), Stored("other-id", id: SiteId + 1)]);
            var service = Service(repository);
            var current = await service.GetAsync(Server, CancellationToken.None);
            Require(current.ServerId == Server && current.Entries.Length == 1 && current.Entries[0].SiteName == SiteName);
            var other = await service.GetAsync(OtherServer, CancellationToken.None);
            Require(other.ServerId == OtherServer && other.Entries.Length == 1);
            Require(repository.Mutations == 0);
        });

        await Check("unknown server and duplicate identities do not mutate", async () =>
        {
            var repository = new MemoryRepository(); var service = Service(repository);
            await Reject(() => service.GetAsync("not-configured", CancellationToken.None), "SERVER_NOT_FOUND", 404);
            Require(repository.Queries == 0);
            repository.Rows.AddRange([Stored("first"), Stored("second")]);
            await Reject(() => service.GetAsync(Server, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            await Reject(() => service.SaveAsync(Server, Request(), CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            Require(repository.Mutations == 0);
        });

        await Check("create saves once and independently reads the persisted version", async () =>
        {
            var repository = new MemoryRepository(); var service = Service(repository);
            var saved = await service.SaveAsync(Server, Request() with { Alias = "  门户  ", Tags = ["  中文标签  ", "API", "api"] }, CancellationToken.None);
            Require(saved.Alias == "门户" && saved.Tags.SequenceEqual(["中文标签", "API"]) && saved.Version.Length == 64);
            Require(repository.Creates == 1 && repository.Updates == 0 && repository.Trace.SequenceEqual(["query", "create", "query"]));
            var read = await service.GetAsync(Server, CancellationToken.None);
            Require(read.Entries.Single().Version == saved.Version && read.Entries.Single().Alias == saved.Alias);
            Require(repository.Mutations == 1 && repository.MutationsWereUncancelable);
        });

        await Check("explicit empty save creates a version while equal content never writes again", async () =>
        {
            var repository = new MemoryRepository(); var service = Service(repository);
            var empty = await service.SaveAsync(Server, Request() with { Alias = "", Tags = [] }, CancellationToken.None);
            Require(empty.Version.Length == 64 && empty.Alias == "" && empty.Tags.Length == 0 && repository.Creates == 1);
            var equal = await service.SaveAsync(Server, Request() with { Alias = "  ", Tags = [""], ExpectedVersion = empty.Version }, CancellationToken.None);
            Require(equal.Version == empty.Version && repository.Mutations == 1);
        });

        await Check("update preserves original identity and rejects stale versions before writing", async () =>
        {
            var repository = new MemoryRepository(); var original = Stored("existing"); repository.Rows.Add(original); var service = Service(repository);
            await Reject(() => service.SaveAsync(Server, Request() with { ExpectedVersion = new string('0', 64) }, CancellationToken.None), "SITE_ANNOTATION_CONFLICT", 409);
            Require(repository.Mutations == 0);
            var version = SiteAnnotationRules.Entry(original).Version;
            var saved = await service.SaveAsync(Server, Request() with { Alias = "更新门户", ExpectedVersion = version }, CancellationToken.None);
            Require(repository.Creates == 0 && repository.Updates == 1 && saved.Version != version);
            var record = repository.Rows.Single(); Require(record.PrimaryId == "existing" && record.SiteId == SiteId && record.SiteName == SiteName && record.ServerId == Server);
            await Reject(() => service.SaveAsync(Server, Request() with { ExpectedVersion = version }, CancellationToken.None), "SITE_ANNOTATION_CONFLICT", 409);
            Require(repository.Mutations == 1);
        });

        await Check("changed removed or renamed IIS targets never write", async () =>
        {
            foreach (var sites in new[] { Array.Empty<SiteConfig>(), new[] { new SiteConfig { Id = SiteId, Name = "Recreated.Web" } }, new[] { new SiteConfig { Id = SiteId + 1, Name = SiteName } } })
            {
                var repository = new MemoryRepository(); var service = Service(repository, sites);
                await Reject(() => service.SaveAsync(Server, Request(), CancellationToken.None), "SITE_TARGET_CHANGED", 409);
                Require(repository.Queries == 0 && repository.Mutations == 0);
            }
        });

        await Check("invalid requests are rejected before repository or snapshot work", async () =>
        {
            var repository = new MemoryRepository(); var snapshots = 0;
            var service = new SiteAnnotationService(repository, _ => true, (_, _) => { snapshots++; return Task.FromResult(Snapshot()); });
            await Reject(() => service.SaveAsync(Server, Request() with { Tags = ["invalid\n"] }, CancellationToken.None), "SITE_ANNOTATION_INVALID", 400);
            Require(repository.Mutations == 0 && repository.Queries == 0 && snapshots == 0);
        });

        await Check("failed mutation recovers only through a matching read without replay", async () =>
        {
            var repository = new MemoryRepository { FailMutation = true }; var service = Service(repository);
            var recovered = await service.SaveAsync(Server, Request(), CancellationToken.None);
            Require(recovered.Version.Length == 64 && repository.Mutations == 1 && repository.Trace.SequenceEqual(["query", "create", "query"]));
        });

        await Check("uncommitted or unreadable mutations remain unknown after exactly one attempt", async () =>
        {
            foreach (var repository in new[]
            {
                new MemoryRepository { FailMutation = true, PersistMutation = false },
                new MemoryRepository { FailMutation = true, FailReadsAfterMutation = true },
                new MemoryRepository { PersistMutation = false },
                new MemoryRepository { FailReadsAfterMutation = true }
            })
            {
                await Reject(() => Service(repository).SaveAsync(Server, Request(), CancellationToken.None), "SITE_ANNOTATION_OUTCOME_UNKNOWN", 502);
                Require(repository.Mutations == 1 && repository.Queries == 2 && repository.Trace.SequenceEqual(["query", "create", "query"]));
            }
        });

        foreach (var bits in new[] { 1024, 2048 })
        {
            await Check("RSA " + bits + " segmented UTF8 roundtrip and uppercase ciphertext", () =>
            {
                using var rsa = RSA.Create(bits);
                var plain = "header-" + string.Concat(Enumerable.Repeat("中文标签😀é跨块", 120)) + "-tail";
                var encrypted = ZeroCodeSiteAnnotationRepository.Encrypt(plain, rsa.ExportSubjectPublicKeyInfoPem());
                Require(encrypted.Length > bits / 8 * 2 && encrypted.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'));
                Require(Decrypt(rsa, encrypted, validateEachBlock: true) == plain);
                Require(Decrypt(rsa, ZeroCodeSiteAnnotationRepository.Encrypt("", rsa.ExportSubjectPublicKeyInfoPem()), true) == "");
                return Task.CompletedTask;
            });

            await Check("RSA " + bits + " signed wire paths fields timestamp and Chinese JSON tags", async () =>
            {
                using var rsa = RSA.Create(bits); var rows = new List<StoredSiteAnnotation>(); var trace = new List<string>();
                using var repository = Repository(rsa, new FixtureHandler(async (request, cancellation) =>
                {
                    using var payload = await Payload(request, rsa, cancellation); var root = payload.RootElement;
                    var method = request.RequestUri!.Segments[^2].Trim('/'); trace.Add(method); CheckEnvelope(request, root, method);
                    if (method == ZeroCodeSiteAnnotationRepository.QueryMethod)
                    {
                        Require(Names(root).SetEquals([ZeroCodeSiteAnnotationRepository.ServerField, ZeroCodeSiteAnnotationRepository.KeyField, "pageNumber", "pageSize", "timeStamp", "sign"]));
                        Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.ServerField).GetString() == Server && root.GetProperty("pageNumber").GetInt32() == 1 && root.GetProperty("pageSize").GetInt32() == 200);
                        Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString() == SiteAnnotationRules.SiteKey(Server, SiteId, SiteName));
                        return QueryReply(rows.Select(WireRow), rows.Count);
                    }
                    var tagsJson = root.GetProperty(ZeroCodeSiteAnnotationRepository.TagsField).GetString()!;
                    var tags = JsonSerializer.Deserialize<string[]>(tagsJson)!;
                    Require(tagsJson.Contains('中') || tagsJson.Contains('文'));
                    Require(!tagsJson.Contains("\\u", StringComparison.Ordinal));
                    if (method == ZeroCodeSiteAnnotationRepository.CreateMethod)
                    {
                        Require(Names(root).SetEquals([ZeroCodeSiteAnnotationRepository.KeyField, ZeroCodeSiteAnnotationRepository.ServerField, ZeroCodeSiteAnnotationRepository.SiteIdField, ZeroCodeSiteAnnotationRepository.SiteNameField, ZeroCodeSiteAnnotationRepository.AliasField, ZeroCodeSiteAnnotationRepository.TagsField, "timeStamp", "sign"]));
                        Require(root.GetProperty(ZeroCodeSiteAnnotationRepository.ServerField).GetString() == Server && root.GetProperty(ZeroCodeSiteAnnotationRepository.SiteIdField).GetInt64() == SiteId && root.GetProperty(ZeroCodeSiteAnnotationRepository.SiteNameField).GetString() == SiteName);
                        rows.Add(new StoredSiteAnnotation("synthetic-primary", root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString()!, Server, SiteId, SiteName, root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!, tags));
                    }
                    else
                    {
                        Require(method == ZeroCodeSiteAnnotationRepository.UpdateMethod);
                        Require(Names(root).SetEquals(["primaryId", ZeroCodeSiteAnnotationRepository.AliasField, ZeroCodeSiteAnnotationRepository.TagsField, "timeStamp", "sign"]));
                        Require(root.GetProperty("primaryId").GetString() == "synthetic-primary");
                        rows[0] = rows[0] with { Alias = root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!, Tags = tags };
                    }
                    return MutationReply("synthetic-primary");
                }));
                var service = Service(repository); var tags = Enumerable.Range(0, 20).Select(index => new string('中', 38) + index.ToString("00", CultureInfo.InvariantCulture)).ToArray();
                var created = await service.SaveAsync(Server, Request() with { Alias = new string('门', 100), Tags = tags }, CancellationToken.None);
                Require(created.Tags.SequenceEqual(tags));
                var updated = await service.SaveAsync(Server, Request() with { Alias = "更新业务门户", Tags = ["中文标签", "财务 \"报表\""], ExpectedVersion = created.Version }, CancellationToken.None);
                Require(updated.Tags.SequenceEqual(["中文标签", "财务 \"报表\""]) && updated.Version != created.Version);
                Require(trace.SequenceEqual([ZeroCodeSiteAnnotationRepository.QueryMethod, ZeroCodeSiteAnnotationRepository.CreateMethod, ZeroCodeSiteAnnotationRepository.QueryMethod, ZeroCodeSiteAnnotationRepository.QueryMethod, ZeroCodeSiteAnnotationRepository.UpdateMethod, ZeroCodeSiteAnnotationRepository.QueryMethod]));
            });
        }

        foreach (var persist in new[] { false, true })
            await Check("HTTP mutation timeout once with " + (persist ? "read recovery" : "unknown outcome"), async () =>
            {
                using var rsa = RSA.Create(1024); var rows = new List<StoredSiteAnnotation>(); var trace = new List<string>();
                using var repository = Repository(rsa, new FixtureHandler(async (request, cancellation) =>
                {
                    using var payload = await Payload(request, rsa, cancellation); var root = payload.RootElement; var method = request.RequestUri!.Segments[^2].Trim('/'); trace.Add(method);
                    if (method == ZeroCodeSiteAnnotationRepository.QueryMethod) return QueryReply(rows.Select(WireRow), rows.Count);
                    Require(method == ZeroCodeSiteAnnotationRepository.CreateMethod);
                    if (persist) rows.Add(new StoredSiteAnnotation("timeout-primary", root.GetProperty(ZeroCodeSiteAnnotationRepository.KeyField).GetString()!, Server, SiteId, SiteName, root.GetProperty(ZeroCodeSiteAnnotationRepository.AliasField).GetString()!, JsonSerializer.Deserialize<string[]>(root.GetProperty(ZeroCodeSiteAnnotationRepository.TagsField).GetString()!)!));
                    throw new OperationCanceledException("SYNTHETIC_TIMEOUT");
                }));
                if (persist) Require((await Service(repository).SaveAsync(Server, Request(), CancellationToken.None)).Version.Length == 64);
                else await Reject(() => Service(repository).SaveAsync(Server, Request(), CancellationToken.None), "SITE_ANNOTATION_OUTCOME_UNKNOWN", 502);
                Require(trace.SequenceEqual([ZeroCodeSiteAnnotationRepository.QueryMethod, ZeroCodeSiteAnnotationRepository.CreateMethod, ZeroCodeSiteAnnotationRepository.QueryMethod]));
            });

        await Check("remote HTTP 200 business failure and missing mutation primary id fail closed", async () =>
        {
            using var rsa = RSA.Create(1024);
            foreach (var body in new object[]
            {
                new Dictionary<string, object?> { ["ResultCode"] = "500", ["Message"] = "SYNTHETIC_REMOTE_TEXT", ["ReturnValue"] = new { primaryId = "ignored" } },
                new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = new { } },
                new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = new { primaryId = "" } },
                new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = Array.Empty<object>() }
            })
            {
                using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(Reply(body))));
                await Reject(() => repository.CreateAsync(Stored(""), CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
                await Reject(() => repository.UpdateAsync(Stored("existing"), CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            }
        });

        await Check("query exact identity filtering and numeric string fields", async () =>
        {
            using var rsa = RSA.Create(1024); var matching = Stored("matching");
            var matchingWire = WireRow(matching); matchingWire[ZeroCodeSiteAnnotationRepository.SiteIdField] = SiteId.ToString(CultureInfo.InvariantCulture);
            using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(QueryReply([matchingWire, WireRow(Stored("other", OtherServer)), WireRow(Stored("old", name: "Old.Web"))], "3"))));
            var exact = await repository.QueryAsync(Server, matching.SiteKey, CancellationToken.None);
            Require(exact.Length == 1 && exact[0].PrimaryId == "matching" && exact[0].Tags.SequenceEqual(matching.Tags));
        });

        await Check("missing record primary id or invalid tags and identity fail closed", async () =>
        {
            using var rsa = RSA.Create(1024);
            foreach (var field in new[] { "primaryId", ZeroCodeSiteAnnotationRepository.TagsField, ZeroCodeSiteAnnotationRepository.KeyField })
            {
                var row = WireRow(Stored("current"));
                if (field == "primaryId") row.Remove(field);
                else row[field] = field == ZeroCodeSiteAnnotationRepository.TagsField ? "[broken-json" : new string('0', 64);
                using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(QueryReply([row], 1))));
                await Reject(() => repository.QueryAsync(Server, null, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            }
        });

        await Check("malformed missing and contradictory pagination fail closed", async () =>
        {
            using var rsa = RSA.Create(1024); var row = WireRow(Stored("current"));
            foreach (var body in new object[]
            {
                new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = new[] { row } },
                new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = new { }, ["TotalCount"] = 1 },
                QueryBody([], 1), QueryBody([row], 0), QueryBody([row], -1), QueryBody([row], 10001), QueryBody([row], "invalid"),
                QueryBody([row, row], 2), QueryBody(Enumerable.Range(0, 201).Select(index => WireRow(Stored("p" + index, id: index + 1))), 201)
            })
            {
                using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(Reply(body))));
                await Reject(() => repository.QueryAsync(Server, null, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            }
        });

        await Check("changing totals across pages fail closed after bounded reads", async () =>
        {
            using var rsa = RSA.Create(1024); var calls = 0;
            using var repository = Repository(rsa, new FixtureHandler((_, _) =>
            {
                calls++;
                return Task.FromResult(calls == 1 ? QueryReply(Enumerable.Range(0, 200).Select(index => WireRow(Stored("p" + index, id: index + 1))), 201) : QueryReply([WireRow(Stored("last", id: 201))], 202));
            }));
            await Reject(() => repository.QueryAsync(Server, null, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503); Require(calls == 2);
        });

        await Check("non JSON and HTTP transport failure never expose response text", async () =>
        {
            using var rsa = RSA.Create(1024);
            foreach (var response in new[]
            {
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SYNTHETIC_NON_JSON") },
                new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("SYNTHETIC_REMOTE_TEXT") }
            })
            {
                using var repository = Repository(rsa, new FixtureHandler((_, _) => Task.FromResult(response)));
                await Reject(() => repository.QueryAsync(Server, null, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            }
        });

        await Check("response body timeout remains bounded after successful headers", async () =>
        {
            using var rsa = RSA.Create(1024);
            using var repository = new ZeroCodeSiteAnnotationRepository(Options(timeout: 1, publicKeyBase64: Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())),
                new FixtureHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StreamContent(new BlockingStream()) })));
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            await Reject(() => repository.QueryAsync(Server, null, CancellationToken.None), "SITE_ANNOTATIONS_UNAVAILABLE", 503);
            Require(elapsed.Elapsed < TimeSpan.FromSeconds(4));
        });

        System.Console.WriteLine($"Site annotations: {checks - failures}/{checks} PASS");
        return failures == 0 ? 0 : 1;
    }

    private static SaveSiteAnnotationRequest Request() => new() { SiteId = SiteId, SiteName = SiteName, Alias = "业务门户", Tags = ["生产", "API"] };
    private static StoredSiteAnnotation Stored(string primary, string server = Server, long id = SiteId, string name = SiteName) => new(primary, SiteAnnotationRules.SiteKey(server, id, name), server, id, name, "业务门户", ["生产", "API"]);
    private static IisSnapshot Snapshot(SiteConfig[]? sites = null) => new(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero), [], sites ?? [new SiteConfig { Id = SiteId, Name = SiteName }]);
    private static SiteAnnotationService Service(ISiteAnnotationRepository repository, SiteConfig[]? sites = null) => new(repository, server => server is Server or OtherServer, (_, _) => Task.FromResult(Snapshot(sites)));
    private static SiteAnnotationService ApplicationService(ISiteAnnotationRepository repository, SiteTopology[]? sites = null) =>
        new(repository, server => server is Server or OtherServer, (_, _) => Task.FromResult(Snapshot()),
            (_, _) => Task.FromResult(new SiteTopologySnapshot(DateTimeOffset.UtcNow, sites ??
                [new SiteTopology { Id = SiteId, Name = SiteName, Applications = [new() { Path = "/" }, new() { Path = "/api" }, new() { Path = "/api/v2" }] }], [])));
    private static SiteAnnotationZeroCodeOptions Options(int application = 1001, int timeout = 30, string? baseUrl = null, string? appId = null, string? publicKeyBase64 = null) => new()
    {
        Enabled = true, ApplicationId = application, TimeoutSeconds = timeout, OpenApiBaseUrl = baseUrl ?? ApiBase,
        OpenApiAppId = appId ?? AppId, PublicKeyBase64 = publicKeyBase64 ?? "AA=="
    };
    private static ZeroCodeSiteAnnotationRepository Repository(RSA rsa, HttpMessageHandler handler) => new(Options(publicKeyBase64: Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())), handler);
    private static HashSet<string> Names(JsonElement root) => root.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private static void CheckEnvelope(HttpRequestMessage request, JsonElement root, string method)
    {
        Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == ApiBase + "/" + method + "/" + AppId);
        Require(request.Content?.Headers.ContentType is { MediaType: "application/json", CharSet: "utf-8" });
        var timestamp = root.GetProperty("timeStamp").GetInt64();
        Require(timestamp > 1_000_000_000 && timestamp < 10_000_000_000 && Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp) <= 10);
        var sign = root.GetProperty("sign").GetString();
        Require(sign == Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(AppId + method + timestamp.ToString(CultureInfo.InvariantCulture)))));
    }

    private static async Task<JsonDocument> Payload(HttpRequestMessage request, RSA rsa, CancellationToken cancellation)
    {
        var outer = await request.Content!.ReadAsStringAsync(cancellation);
        var encrypted = JsonSerializer.Deserialize<string>(outer) ?? throw new InvalidOperationException("TEST_ENCRYPTED_BODY_REQUIRED");
        Require(encrypted.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F'));
        return JsonDocument.Parse(Decrypt(rsa, encrypted, true));
    }

    private static string Decrypt(RSA rsa, string encrypted, bool validateEachBlock)
    {
        var cipher = Convert.FromHexString(encrypted); var blockSize = rsa.KeySize / 8;
        Require(cipher.Length % blockSize == 0); using var plain = new MemoryStream(); var utf8 = new UTF8Encoding(false, true);
        for (var offset = 0; offset < cipher.Length; offset += blockSize)
        {
            var bytes = rsa.Decrypt(cipher.AsSpan(offset, blockSize).ToArray(), RSAEncryptionPadding.Pkcs1);
            if (validateEachBlock) _ = utf8.GetString(bytes);
            plain.Write(bytes); CryptographicOperations.ZeroMemory(bytes);
        }
        return utf8.GetString(plain.ToArray());
    }

    private static Dictionary<string, object?> WireRow(StoredSiteAnnotation stored) => new()
    {
        ["primaryId"] = stored.PrimaryId, [ZeroCodeSiteAnnotationRepository.KeyField] = stored.SiteKey,
        [ZeroCodeSiteAnnotationRepository.ServerField] = stored.ServerId, [ZeroCodeSiteAnnotationRepository.SiteIdField] = stored.SiteId,
        [ZeroCodeSiteAnnotationRepository.SiteNameField] = stored.SiteName, [ZeroCodeSiteAnnotationRepository.AliasField] = stored.Alias,
        [ZeroCodeSiteAnnotationRepository.TagsField] = JsonSerializer.Serialize(stored.Tags, Json), ["lastModifiedAt"] = stored.ModifiedAt
    };
    private static Dictionary<string, object?> QueryBody(IEnumerable<object> rows, object total) => new() { ["ResultCode"] = "200", ["ReturnValue"] = rows.ToArray(), ["TotalCount"] = total };
    private static HttpResponseMessage QueryReply(IEnumerable<object> rows, object total) => Reply(QueryBody(rows, total));
    private static HttpResponseMessage MutationReply(string primary) => Reply(new Dictionary<string, object?> { ["ResultCode"] = "200", ["ReturnValue"] = new { primaryId = primary } });
    private static HttpResponseMessage Reply(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    private static async Task Reject(Func<Task> action, string code, int status)
    {
        try { await action(); }
        catch (SiteAnnotationException exception) when (exception.Code == code && exception.StatusCode == status)
        {
            Require(exception.Message == code && !exception.SafeMessage.Contains("SYNTHETIC", StringComparison.Ordinal)); return;
        }
        throw new InvalidOperationException("TEST_EXPECTED_SITE_ANNOTATION_REJECTION");
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("TEST_SITE_ANNOTATION_ASSERTION"); }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, cancellationToken);
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private sealed class MemoryRepository : ISiteAnnotationRepository
    {
        public List<StoredSiteAnnotation> Rows { get; } = [];
        public List<string> Trace { get; } = [];
        public bool PersistMutation { get; init; } = true;
        public bool FailMutation { get; init; }
        public bool FailReadsAfterMutation { get; init; }
        public int Queries { get; private set; }
        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public int Mutations => Creates + Updates;
        public bool MutationsWereUncancelable { get; private set; } = true;

        public Task<StoredSiteAnnotation[]> QueryAsync(string serverId, string? siteKey, CancellationToken cancellation)
        {
            Queries++; Trace.Add("query");
            if (FailReadsAfterMutation && Mutations > 0) throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
            // Broad results deliberately exercise the service's exact identity boundary.
            return Task.FromResult(Rows.Select(row => row with { Tags = row.Tags.ToArray() }).ToArray());
        }
        public Task CreateAsync(StoredSiteAnnotation entry, CancellationToken cancellation) => Mutate(entry, false, cancellation);
        public Task UpdateAsync(StoredSiteAnnotation entry, CancellationToken cancellation) => Mutate(entry, true, cancellation);
        private Task Mutate(StoredSiteAnnotation entry, bool update, CancellationToken cancellation)
        {
            Trace.Add(update ? "update" : "create"); if (update) Updates++; else Creates++;
            MutationsWereUncancelable &= !cancellation.CanBeCanceled;
            if (PersistMutation)
            {
                var persisted = entry with { PrimaryId = update ? entry.PrimaryId : "synthetic-created-" + Creates, Tags = entry.Tags.ToArray() };
                if (update) Rows[Rows.FindIndex(row => row.PrimaryId == entry.PrimaryId)] = persisted; else Rows.Add(persisted);
            }
            if (FailMutation) throw new SiteAnnotationException("SITE_ANNOTATIONS_UNAVAILABLE");
            return Task.CompletedTask;
        }
    }
}
