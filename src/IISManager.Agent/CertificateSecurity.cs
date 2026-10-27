using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace IISManager.Agent;

internal static class CertificateSecurity
{
    public static string Normalize(string thumbprint) => thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    public static X509Certificate2 LoadServerCertificate(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) throw new InvalidOperationException("SERVER_CERTIFICATE_REQUIRED");
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, Normalize(thumbprint), validOnly: true);
        if (matches.Count != 1 || !matches[0].HasPrivateKey || !HasEku(matches[0], "1.3.6.1.5.5.7.3.1"))
            throw new InvalidOperationException("SERVER_CERTIFICATE_INVALID");
        return matches[0];
    }

    public static bool IsAllowedClient(X509Certificate2 certificate, string[] allowed)
    {
        if (certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            !allowed.Any(item => Normalize(item) == Normalize(certificate.Thumbprint)) ||
            !HasEku(certificate, "1.3.6.1.5.5.7.3.2")) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        return chain.Build(certificate);
    }

    private static bool HasEku(X509Certificate2 certificate, string oid) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(usage => usage.Value == oid));
}
