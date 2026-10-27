#Requires -Version 5.1
# Read-only: queries one page; never saves annotations or changes configuration.
# Run in a fresh Windows PowerShell process. Do not enable transcription/debug tracing.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConsoleRoot,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')][string]$ServerId = '1',
    [ValidateSet('Production', 'Development', 'Staging')][string]$EnvironmentName = 'Production',
    [switch]$Direct
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskStage = 'Configuration'
$taskClient = $null
$taskHandler = $null
$taskCancellation = $null
$taskRequest = $null
$taskResponse = $null
$taskStream = $null
$taskBytes = $null
$taskSummary = [ordered]@{
    Result = 'Unverified'; Stage = $taskStage; ConfigSource = 'FilesAndCurrentProcessEnvironment'
    CheckedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    ProxyMode = $(if ($Direct) { 'Direct' } else { 'System' })
    ServerId = $ServerId; QueryPage = 1; HttpStatus = $null; BusinessCode = $null
    MessageCategory = $null; MessageFields = $null; KnownMessageTerms = $null; AuthenticationScheme = 'Absent'
    ReturnValueIsArray = $false; TotalCountValid = $false
    FirstPageCount = $null; ElapsedMs = $null; ExceptionType = $null; HResult = $null
}

function Read-Property {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $taskProperty = $Object.PSObject.Properties[$Name]
    if ($null -eq $taskProperty) { return $null }
    return ,($taskProperty.Value)
}

function Read-MessageDiagnostic {
    param($Object)
    $taskText = ''
    $taskFields = @()
    foreach ($taskName in @('Message', 'ResultMessage', 'ResultMsg', 'Msg', 'ErrorMessage', 'ErrorMsg')) {
        $taskValue = Read-Property $Object $taskName
        if ($taskValue -is [string]) { $taskText += ' ' + $taskValue; $taskFields += $taskName }
    }
    # Output only constants from these lists, never the remote message or credentials.
    $taskTerms = @()
    foreach ($taskTerm in @('签名', '时间戳', '超时', '有效期', 'RSA', '解密', '公钥', '密钥', 'AppID', '应用',
        '接口', '未发布', '不存在', '无效', '认证', '授权', '鉴权', '权限', '未登录', '登录', '令牌', 'Token',
        '非法', '验证', '校验', '请求', '失败', '拒绝', '格式', '完整', '数据', '参数', '错误')) {
        if ($taskText.IndexOf($taskTerm, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $taskTerms += $taskTerm }
    }
    $taskCategory = 'Unclassified'
    if ($taskText -match 'time.?stamp|时间戳|请求过期|请求有效期') { $taskCategory = 'Timestamp' }
    elseif ($taskText -match 'signature|签名') { $taskCategory = 'Signature' }
    elseif ($taskText -match 'decrypt|解密|RSA|公钥') { $taskCategory = 'Encryption' }
    elseif ($taskText -match 'App.?ID|应用不存在|应用标识|身份验证|认证|鉴权|授权|权限|登录|令牌|token|login|unauthori|forbidden') { $taskCategory = 'ApplicationAuthentication' }
    elseif ($taskText -match '接口不存在|接口未发布|未找到接口|method.*not.*found') { $taskCategory = 'ApiPublication' }
    elseif ($taskText -match '参数|parameter') { $taskCategory = 'Parameters' }
    elseif ($taskText -match '验证|校验|validation') { $taskCategory = 'RequestValidation' }
    return [pscustomobject]@{ Category = $taskCategory; Fields = ($taskFields -join ','); Terms = ($taskTerms -join ',') }
}

try {
    if (-not [IO.Path]::IsPathRooted($ConsoleRoot) -or -not (Test-Path -LiteralPath $ConsoleRoot -PathType Container)) {
        throw [ArgumentException]::new('CONSOLE_ROOT_INVALID')
    }
    $taskOptions = @{}
    $taskBaseExists = $false
    foreach ($taskFile in @('appsettings.json', ('appsettings.' + $EnvironmentName + '.json'), 'appsettings.Local.json')) {
        $taskPath = Join-Path $ConsoleRoot $taskFile
        if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { continue }
        if ($taskFile -eq 'appsettings.json') { $taskBaseExists = $true }
        if ((Get-Item -LiteralPath $taskPath).Length -gt 1048576) { throw [ArgumentException]::new('CONFIG_TOO_LARGE') }
        $taskConfig = Get-Content -LiteralPath $taskPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $taskSection = Read-Property (Read-Property $taskConfig 'Console') 'SiteAnnotations'
        if ($null -ne $taskSection) {
            foreach ($taskProperty in $taskSection.PSObject.Properties) { $taskOptions[$taskProperty.Name] = $taskProperty.Value }
        }
    }
    if (-not $taskBaseExists) { throw [ArgumentException]::new('BASE_CONFIG_MISSING') }
    foreach ($taskName in @('Enabled', 'ApplicationId', 'OpenApiBaseUrl', 'OpenApiAppId', 'PublicKeyBase64', 'TimeoutSeconds')) {
        $taskOverride = [Environment]::GetEnvironmentVariable('Console__SiteAnnotations__' + $taskName)
        if ($null -ne $taskOverride) { $taskOptions[$taskName] = $taskOverride }
    }
    $taskEnabled = $false
    if (-not $taskOptions.ContainsKey('Enabled') -or -not [bool]::TryParse([string]$taskOptions['Enabled'], [ref]$taskEnabled) -or -not $taskEnabled) {
        $taskSummary.Result = 'NotConfigured'
    }
    else {
        $taskUri = $null
        $taskBase = [string]$taskOptions['OpenApiBaseUrl']
        $taskAppId = [string]$taskOptions['OpenApiAppId']
        $taskKey = [string]$taskOptions['PublicKeyBase64']
        $taskTimeout = 30
        if ($taskOptions.ContainsKey('TimeoutSeconds')) { $taskTimeout = [int]$taskOptions['TimeoutSeconds'] }
        if (($taskOptions.ContainsKey('ApplicationId') -and [int]$taskOptions['ApplicationId'] -ne 1001) -or
            -not [Uri]::TryCreate($taskBase, [UriKind]::Absolute, [ref]$taskUri) -or $taskUri.Scheme -ne 'https' -or
            $taskUri.UserInfo.Length -ne 0 -or $taskUri.Query.Length -ne 0 -or $taskUri.Fragment.Length -ne 0 -or
            [string]::IsNullOrWhiteSpace($taskAppId) -or $taskAppId.Length -gt 256 -or $taskAppId -match '\s|[\x00-\x1f\x7f]' -or
            [string]::IsNullOrWhiteSpace($taskKey) -or $taskKey.Length -gt 8192 -or $taskTimeout -lt 1 -or $taskTimeout -gt 120) {
            throw [ArgumentException]::new('ANNOTATION_CONFIG_INVALID')
        }
        if ($null -ne ('IisManagerAnnotationQueryCrypto' -as [type])) { throw [InvalidOperationException]::new('FRESH_PROCESS_REQUIRED') }
        $taskStage = 'Encryption'
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
public static class IisManagerAnnotationQueryCrypto
{
    private sealed class Der
    {
        private readonly byte[] data; private int offset;
        public Der(byte[] value) { data = value; }
        public bool End { get { return offset == data.Length; } }
        public byte Peek() { if (End) throw new CryptographicException(); return data[offset]; }
        public byte[] Value(byte tag)
        {
            if (Peek() != tag) throw new CryptographicException(); offset++;
            if (End) throw new CryptographicException(); int length = data[offset++];
            if ((length & 128) != 0)
            {
                int count = length & 127; length = 0;
                if (count < 1 || count > 3 || offset + count > data.Length || data[offset] == 0) throw new CryptographicException();
                for (int i = 0; i < count; i++) length = checked(length * 256 + data[offset++]);
                if (length < 128) throw new CryptographicException();
            }
            if (length > data.Length - offset) throw new CryptographicException();
            byte[] result = new byte[length]; Buffer.BlockCopy(data, offset, result, 0, length); offset += length; return result;
        }
        public Der Sequence() { return new Der(Value(48)); }
        public byte[] Integer()
        {
            byte[] value = Value(2);
            if (value.Length == 0 || (value[0] & 128) != 0) throw new CryptographicException();
            if (value[0] != 0) return value;
            if (value.Length < 2 || (value[1] & 128) == 0) throw new CryptographicException();
            byte[] result = new byte[value.Length - 1]; Buffer.BlockCopy(value, 1, result, 0, result.Length); return result;
        }
    }
    public static string Encrypt(string text, string key)
    {
        Der outer = new Der(Convert.FromBase64String(key)); Der sequence = outer.Sequence();
        if (!outer.End) throw new CryptographicException();
        if (sequence.Peek() == 48)
        {
            Der algorithm = sequence.Sequence(); byte[] oid = algorithm.Value(6);
            byte[] expected = new byte[] {42,134,72,134,247,13,1,1,1};
            if (oid.Length != expected.Length) throw new CryptographicException();
            for (int i = 0; i < oid.Length; i++) if (oid[i] != expected[i]) throw new CryptographicException();
            if (!algorithm.End && algorithm.Value(5).Length != 0) throw new CryptographicException();
            if (!algorithm.End) throw new CryptographicException();
            byte[] bits = sequence.Value(3);
            if (!sequence.End || bits.Length < 2 || bits[0] != 0) throw new CryptographicException();
            byte[] encoded = new byte[bits.Length - 1]; Buffer.BlockCopy(bits, 1, encoded, 0, encoded.Length);
            Der inner = new Der(encoded); sequence = inner.Sequence(); if (!inner.End) throw new CryptographicException();
        }
        RSAParameters parameters = new RSAParameters { Modulus = sequence.Integer(), Exponent = sequence.Integer() };
        if (!sequence.End) throw new CryptographicException();
        using (RSA rsa = RSA.Create())
        using (MemoryStream result = new MemoryStream())
        {
            rsa.ImportParameters(parameters); if (rsa.KeySize < 1024) throw new CryptographicException();
            byte[] plain = Encoding.UTF8.GetBytes(text); int blockSize = rsa.KeySize / 8 - 11;
            for (int offset = 0; offset < plain.Length;)
            {
                int length = Math.Min(blockSize, plain.Length - offset);
                if (offset + length < plain.Length) while (length > 0 && (plain[offset + length] & 192) == 128) length--;
                byte[] block = new byte[length]; Buffer.BlockCopy(plain, offset, block, 0, length);
                byte[] encrypted = rsa.Encrypt(block, RSAEncryptionPadding.Pkcs1); result.Write(encrypted, 0, encrypted.Length); offset += length;
            }
            return BitConverter.ToString(result.ToArray()).Replace("-", "");
        }
    }
}
'@
        $taskTimestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $taskMd5 = [Security.Cryptography.MD5]::Create()
        try { $taskSign = [BitConverter]::ToString($taskMd5.ComputeHash([Text.Encoding]::UTF8.GetBytes($taskAppId + 'QuerySiteAnnotations' + $taskTimestamp.ToString([Globalization.CultureInfo]::InvariantCulture)))).Replace('-', '') }
        finally { $taskMd5.Dispose() }
        $taskPlain = @{ fUWUQIBIANHAO = $ServerId; pageNumber = 1; pageSize = 200; timeStamp = $taskTimestamp; sign = $taskSign } | ConvertTo-Json -Compress
        $taskEncrypted = [IisManagerAnnotationQueryCrypto]::Encrypt($taskPlain, $taskKey)
        $taskBody = ConvertTo-Json -InputObject $taskEncrypted -Compress
        $taskStage = 'Transport'
        Add-Type -AssemblyName System.Net.Http
        $taskHandler = New-Object Net.Http.HttpClientHandler
        $taskHandler.AllowAutoRedirect = $false
        if ($Direct) { $taskHandler.UseProxy = $false }
        $taskClient = [Net.Http.HttpClient]::new($taskHandler)
        $taskClient.Timeout = [TimeSpan]::FromSeconds($taskTimeout)
        $taskCancellation = New-Object Threading.CancellationTokenSource
        $taskCancellation.CancelAfter([TimeSpan]::FromSeconds($taskTimeout))
        $taskEndpoint = [Uri]::new($taskBase.TrimEnd('/') + '/QuerySiteAnnotations/' + [Uri]::EscapeDataString($taskAppId))
        $taskRequest = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Post, $taskEndpoint)
        $taskRequest.Content = [Net.Http.StringContent]::new($taskBody, [Text.Encoding]::UTF8, 'application/json')
        $taskWatch = [Diagnostics.Stopwatch]::StartNew()
        $taskResponse = $taskClient.SendAsync($taskRequest, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $taskCancellation.Token).GetAwaiter().GetResult()
        $taskSummary.HttpStatus = [int]$taskResponse.StatusCode
        if ($taskResponse.Headers.Contains('WWW-Authenticate')) {
            $taskSummary.AuthenticationScheme = 'PresentUnrecognized'
            $taskAuth = @($taskResponse.Headers.GetValues('WWW-Authenticate')) -join ' '
            foreach ($taskScheme in @('Bearer', 'Basic', 'Digest', 'Negotiate', 'NTLM')) {
                if ($taskAuth -match ('(?i)^' + $taskScheme + '(\s|$)')) { $taskSummary.AuthenticationScheme = $taskScheme; break }
            }
        }
        $taskStage = 'ResponseBody'
        if ($null -ne $taskResponse.Content.Headers.ContentLength -and $taskResponse.Content.Headers.ContentLength -gt 4194304) {
            throw [InvalidOperationException]::new('RESPONSE_TOO_LARGE')
        }
        $taskStream = $taskResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $taskBytes = New-Object IO.MemoryStream
        $taskBuffer = New-Object byte[] 8192
        while (($taskRead = $taskStream.ReadAsync($taskBuffer, 0, $taskBuffer.Length, $taskCancellation.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($taskBytes.Length + $taskRead -gt 4194304) { throw [InvalidOperationException]::new('RESPONSE_TOO_LARGE') }
            $taskBytes.Write($taskBuffer, 0, $taskRead)
        }
        $taskSummary.ElapsedMs = $taskWatch.ElapsedMilliseconds
        $taskStage = 'ResponseJson'
        try { $taskReply = [Text.Encoding]::UTF8.GetString($taskBytes.ToArray()) | ConvertFrom-Json }
        catch { $taskReply = $null }
        if ($null -eq $taskReply) {
            $taskSummary.Result = $(if ($taskResponse.IsSuccessStatusCode) { 'NonJsonResponse' } else { 'RemoteHttpRejected' })
        }
        else {
            $taskStage = 'ResponseSchema'
            $taskCode = [string](Read-Property $taskReply 'ResultCode')
            $taskSummary.BusinessCode = $(if ($taskCode -match '^[0-9]{1,8}$') { $taskCode } else { 'Unrecognized' })
            $taskMessage = Read-MessageDiagnostic $taskReply
            $taskSummary.MessageCategory = $taskMessage.Category
            $taskSummary.MessageFields = $taskMessage.Fields
            $taskSummary.KnownMessageTerms = $taskMessage.Terms
            $taskRows = Read-Property $taskReply 'ReturnValue'
            $taskSummary.ReturnValueIsArray = $taskRows -is [array]
            $taskTotal = [long]0
            $taskSummary.TotalCountValid = [long]::TryParse([string](Read-Property $taskReply 'TotalCount'), [ref]$taskTotal) -and $taskTotal -ge 0 -and $taskTotal -le 10000
            if ($taskSummary.ReturnValueIsArray) { $taskSummary.FirstPageCount = @($taskRows).Count }
            if (-not $taskResponse.IsSuccessStatusCode) { $taskSummary.Result = 'RemoteHttpRejected' }
            elseif ($taskCode -ne '200') { $taskSummary.Result = 'RemoteBusinessRejected' }
            elseif (-not $taskSummary.ReturnValueIsArray -or -not $taskSummary.TotalCountValid -or
                $taskSummary.FirstPageCount -gt 200 -or $taskSummary.FirstPageCount -gt $taskTotal -or
                ($taskSummary.FirstPageCount -eq 0 -and $taskTotal -gt 0)) { $taskSummary.Result = 'ResponseSchemaMismatch' }
            else { $taskSummary.Result = 'FirstPageEnvelopeValid' }
        }
    }
}
catch {
    $taskLeaf = $_.Exception
    for ($taskIndex = 0; $taskIndex -lt 16 -and $null -ne $taskLeaf.InnerException; $taskIndex++) { $taskLeaf = $taskLeaf.InnerException }
    $taskSummary.ExceptionType = $taskLeaf.GetType().Name
    $taskSummary.HResult = ('0x{0:X8}' -f $taskLeaf.HResult)
}
finally {
    $taskSummary.Stage = $taskStage
    foreach ($taskDisposable in @($taskStream, $taskBytes, $taskResponse, $taskRequest, $taskCancellation, $taskClient, $taskHandler)) {
        if ($null -ne $taskDisposable) { $taskDisposable.Dispose() }
    }
}
[pscustomobject]$taskSummary | Format-List
if ($taskSummary.Result -in @('FirstPageEnvelopeValid', 'NotConfigured')) { exit 0 }
exit 1
