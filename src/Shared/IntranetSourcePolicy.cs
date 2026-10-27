using System.Net;
using System.Net.Sockets;

namespace IISManager.Networking;

internal static class IntranetSourcePolicy
{
    internal const string PrivateNetwork = "PrivateNetwork";
    internal const string Allowlist = "Allowlist";

    internal static bool IsValidMode(string? mode) => mode is PrivateNetwork or Allowlist;

    internal static string[] DefaultNetworks() =>
    [
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8",
        "169.254.0.0/16", "100.64.0.0/10", "::1/128", "fc00::/7", "fe80::/10"
    ];

    internal static bool IsPrivateSource(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 10 || bytes[0] == 127 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (IPAddress.IsLoopback(address) || (bytes[0] & 0xfe) == 0xfc ||
                bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
    }
}
