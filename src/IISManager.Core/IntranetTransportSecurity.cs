using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using IISManager.Networking;

namespace IISManager.Core;

public static class IntranetTransportSecurity
{
    public const string Https = "Https";
    public const string HttpToken = "HttpToken";
    public const string TokenHeader = "X-IISManager-Token";
    public const string PrivateNetwork = IntranetSourcePolicy.PrivateNetwork;
    public const string Allowlist = IntranetSourcePolicy.Allowlist;

    public static bool IsTransportModeValid(string? mode) => mode is Https or HttpToken;
    public static bool IsSourceAccessModeValid(string? mode) => IntranetSourcePolicy.IsValidMode(mode);
    public static bool IsPrivateSource(IPAddress? source) => IntranetSourcePolicy.IsPrivateSource(source);
    public static string[] DefaultPrivateNetworks() => IntranetSourcePolicy.DefaultNetworks();

    public static bool IsValidAccessToken(string? token) =>
        token is { Length: 64 } && token.All(Uri.IsHexDigit);

    public static bool FixedTimeTokenEquals(string? supplied, string? expected)
    {
        if (!IsValidAccessToken(supplied) || !IsValidAccessToken(expected)) return false;
        var actualBytes = Convert.FromHexString(supplied!);
        var expectedBytes = Convert.FromHexString(expected!);
        try { return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(actualBytes);
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
    }

    public static bool IsValidTokenHeader(IEnumerable<string?>? values, string? expected)
    {
        if (values is null) return false;
        var supplied = values.Take(2).ToArray();
        return supplied.Length == 1 && FixedTimeTokenEquals(supplied[0], expected);
    }

    // The allowlist is deliberately independent of forwarded HTTP headers and DNS.
    public static bool TryParseNetwork(string? value, out Network? network)
    {
        network = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('/');
        if (parts.Length > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address)) return false;
        var bits = address.GetAddressBytes().Length * 8;
        var prefix = bits;
        if (parts.Length == 2 && (parts[1].Length is < 1 or > 3 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > bits)) return false;
        if (address.IsIPv4MappedToIPv6)
        {
            if (prefix <= 96) return false;
            address = address.MapToIPv4();
            prefix -= 96;
        }
        if (prefix == 0) return false;
        network = new Network(address, prefix);
        return true;
    }

    public static Network[] ParseRestrictedNetworks(IEnumerable<string>? values)
    {
        if (values is null) throw new InvalidOperationException("HTTP_CLIENT_NETWORKS_REQUIRED");
        var result = new List<Network>();
        foreach (var value in values)
        {
            if (!TryParseNetwork(value, out var network))
                throw new InvalidOperationException("HTTP_CLIENT_NETWORK_INVALID");
            result.Add(network!);
        }
        if (result.Count == 0) throw new InvalidOperationException("HTTP_CLIENT_NETWORKS_REQUIRED");
        return result.ToArray();
    }

    public sealed class Network
    {
        public IPAddress Address { get; }
        public int Prefix { get; }

        internal Network(IPAddress address, int prefix) { Address = address; Prefix = prefix; }

        public bool Contains(IPAddress? candidate)
        {
            if (candidate is null) return false;
            if (candidate.IsIPv4MappedToIPv6) candidate = candidate.MapToIPv4();
            var source = candidate.GetAddressBytes();
            var target = Address.GetAddressBytes();
            if (source.Length != target.Length) return false;
            for (var i = 0; i < Prefix; i++)
                if ((source[i / 8] & (1 << (7 - i % 8))) != (target[i / 8] & (1 << (7 - i % 8)))) return false;
            return true;
        }
    }
}
