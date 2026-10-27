using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using IISManager.Setup.Core;

namespace IISManager.Setup.Backend;

internal sealed record CertificateEvidence(string Thumbprint, string Usage, string KeyPath, string KeySddl);

internal static class SetupCertificates
{
    internal const string ServerUsage = "1.3.6.1.5.5.7.3.1";
    internal const string ClientUsage = "1.3.6.1.5.5.7.3.2";
    internal static SetupCertificate[] List()
    {
        using var store = Open();
        var certificates = store.Certificates;
        try
        {
            return certificates.Cast<X509Certificate2>().OrderBy(x => x.Thumbprint, StringComparer.Ordinal).Select(cert => new SetupCertificate(
                cert.Thumbprint, cert.GetNameInfo(X509NameType.SimpleName, false), Names(cert), new DateTimeOffset(cert.NotAfter.ToUniversalTime()),
                HasUsage(cert, ServerUsage), HasUsage(cert, ClientUsage), cert.HasPrivateKey)).ToArray();
        }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    internal static CertificateEvidence[] Validate(SetupPlan plan)
    {
        var results = new List<CertificateEvidence> { ValidateOne(plan.ServerCertificateThumbprint, ServerUsage, plan.HostName) };
        if (plan.Component == SetupComponent.Console)
            results.Add(ValidateOne(plan.ClientCertificateThumbprint, ClientUsage, null));
        return results.DistinctBy(x => x.KeyPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static CertificateEvidence ValidateOne(string thumbprint, string usage, string? host)
    {
        using var store = Open();
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        try
        {
            if (matches.Count != 1) throw new SetupException("CERTIFICATE_NOT_UNIQUE", "LocalMachine/My 中未找到唯一选定证书。");
            var certificate = matches[0];
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now)
                throw new SetupException("CERTIFICATE_NOT_VALID", "选定证书必须在有效期内并具有已有私钥。");
            if (!HasUsage(certificate, usage)) throw new SetupException("CERTIFICATE_EKU_INVALID", "选定证书缺少明确要求的服务器或客户端 EKU。");
            if (host is not null && !certificate.MatchesHostname(host, allowWildcards: true, allowCommonName: false))
                throw new SetupException("CERTIFICATE_HOST_MISMATCH", "服务器证书 SAN 与访问域名或 IP 不匹配。");
            using var chain = new X509Chain(useMachineContext: true);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
            chain.ChainPolicy.VerificationTime = now;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));
            if (!chain.Build(certificate)) throw new SetupException("CERTIFICATE_CHAIN_UNVERIFIED", "证书信任链或在线撤销校验未通过；请由操作员准备信任和网络，不降低校验。");
            var key = KeyPath(certificate);
            SetupFileSecurity.RejectReparseChain(key, false);
            return new(certificate.Thumbprint, usage, key, SetupFileSecurity.ReadSddl(key));
        }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }

    private static string KeyPath(X509Certificate2 certificate)
    {
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is RSACng rsaCng) return CngPath(rsaCng.Key);
        if (rsa is RSACryptoServiceProvider csp)
        {
            var info = csp.CspKeyContainerInfo;
            if (!info.MachineKeyStore || info.HardwareDevice || info.Removable || string.IsNullOrWhiteSpace(info.UniqueKeyContainerName))
                throw new SetupException("CERTIFICATE_KEY_PROVIDER_UNSUPPORTED", "仅支持已有本机软件 CAPI/CNG 私钥；硬件、用户或未识别提供者请操作员先准备。");
            return CheckedKeyName(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "RSA", "MachineKeys"), info.UniqueKeyContainerName);
        }
        using var ecdsa = certificate.GetECDsaPrivateKey();
        if (ecdsa is ECDsaCng ecCng) return CngPath(ecCng.Key);
        throw new SetupException("CERTIFICATE_KEY_PROVIDER_UNSUPPORTED", "私钥提供者不在首版可证明范围，拒绝猜测密钥路径或扩大权限。");
    }
    private static string CngPath(CngKey key)
    {
        if (!key.IsMachineKey || key.Provider != CngProvider.MicrosoftSoftwareKeyStorageProvider || string.IsNullOrWhiteSpace(key.UniqueName))
            throw new SetupException("CERTIFICATE_KEY_PROVIDER_UNSUPPORTED", "仅支持已有本机 Microsoft 软件密钥存储提供者。");
        return CheckedKeyName(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Crypto", "Keys"), key.UniqueName);
    }
    private static string CheckedKeyName(string root, string name)
    {
        if (Path.GetFileName(name) != name || name.Any(c => char.IsControl(c) || ":*?\"<>|/\\".Contains(c)))
            throw new SetupException("CERTIFICATE_KEY_PATH_UNVERIFIED", "私钥文件名无法核实。");
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) throw new SetupException("CERTIFICATE_KEY_PATH_UNVERIFIED", "所选私钥的精确软件密钥文件不存在，拒绝猜测路径。");
        return path;
    }
    private static bool HasUsage(X509Certificate2 certificate, string usage) => certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
        .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == usage));
    private static X509Store Open()
    {
        var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); return store; }
        catch { store.Dispose(); throw; }
    }
    private static string[] Names(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions.Where(x => x.Oid?.Value == "2.5.29.17"))
        {
            try
            {
                var sequence = new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadSequence();
                while (sequence.HasData)
                {
                    var tag = sequence.PeekTag();
                    if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                        names.Add(sequence.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 2)));
                    else if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                    {
                        var bytes = sequence.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 7));
                        if (bytes.Length is 4 or 16) names.Add(new IPAddress(bytes).ToString());
                    }
                    else sequence.ReadEncodedValue();
                }
            }
            catch (AsnContentException) { } // List only; strict selection validation rejects malformed names.
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
