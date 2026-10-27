using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IISManager.Contracts;
using IISManager.Core;

namespace IISManager.Agent.Backends;

internal sealed record TcpListenerRecord(string IpAddress, int Port, uint ProcessId, int AddressFamily);

/// <summary>Read-only native owner-PID listener evidence. Never probes by binding or changing a socket.</summary>
[SupportedOSPlatform("windows")]
internal static class TcpListenerGuard
{
    internal static TcpListenerRecord[] Read()
    {
        var values = new List<TcpListenerRecord>();
        ReadTable<TcpRowV4, TcpTableV4>(2, row =>
        {
            if (row.State != 2) throw Unverified();
            var address = new IPAddress(BitConverter.GetBytes(row.LocalAddress));
            values.Add(new(Normalize(address), Port(row.LocalPort), row.ProcessId, 2));
        });
        ReadTable<TcpRowV6, TcpTableV6>(23, row =>
        {
            if (row.State != 2 || row.LocalAddress is not { Length: 16 }) throw Unverified();
            var address = new IPAddress(row.LocalAddress, BinaryPrimitives.ReverseEndianness(row.LocalScope));
            values.Add(new(Normalize(address), Port(row.LocalPort), row.ProcessId, 23));
        });
        return values.OrderBy(x => x.Port).ThenBy(x => x.IpAddress, StringComparer.Ordinal)
            .ThenBy(x => x.AddressFamily).ThenBy(x => x.ProcessId).ToArray();
    }

    internal static void Check(BindingConfig requested, TcpListenerRecord[] listeners, IisSnapshot snapshot, List<ApiError> errors)
    {
        foreach (var listener in listeners.Where(x => x.Port == requested.Port && Overlaps(x.IpAddress, requested.IpAddress)))
        {
            if (listener.ProcessId != 4)
            {
                errors.Add(new("binding.tcpListenerConflict", "此地址/端口已有非 HTTP.sys TCP 监听进程，拒绝创建绑定。"));
                continue;
            }
            // PID 4 alone does not establish ownership by IIS. Only a read-back IIS binding on
            // the same protocol and endpoint permits HTTP.sys sharing. TLS evidence is checked separately.
            var known = snapshot.Sites.SelectMany(x => x.Bindings).Any(existing =>
                existing.Protocol == requested.Protocol && existing.Port == listener.Port &&
                Overlaps(existing.IpAddress, listener.IpAddress) && Overlaps(existing.IpAddress, requested.IpAddress));
            if (!known) errors.Add(new("binding.httpSysListenerUnverified", "PID 4 的此监听端点未对应已读回的 IIS 同协议绑定，拒绝未知占用。"));
        }
    }

    private static bool Overlaps(string left, string right)
    {
        static string Canonical(string ip) => ip == "*" ? "*" : IPAddress.TryParse(ip, out var value) ? Normalize(value) : ip;
        return BackendRules.AddressesOverlap(Canonical(left), Canonical(right));
    }
    private static string Normalize(IPAddress address) => address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
        ? "*" : address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
    private static int Port(uint networkPort)
    {
        var value = (int)(((networkPort & 0xFF) << 8) | ((networkPort >> 8) & 0xFF));
        if (value is < 1 or > 65535) throw Unverified();
        return value;
    }
    private static OperationException Unverified() => new("binding.tcpListenersUnverified", "IPv4/IPv6 TCP 监听表未能完整核实，拒绝创建站点。", 503);
    private static void ReadTable<TRow, TTable>(int family, Action<TRow> read) where TRow : struct where TTable : struct
    {
        var length = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref length, true, family, 3, 0);
        if (status != 122 || length < 4 || length > 8 * 1024 * 1024) throw Unverified();
        var allocatedLength = length;
        var buffer = Marshal.AllocHGlobal(allocatedLength);
        try
        {
            status = GetExtendedTcpTable(buffer, ref length, true, family, 3, 0);
            if (status != 0 || length > allocatedLength || length < 4) throw Unverified();
            var count = unchecked((uint)Marshal.ReadInt32(buffer));
            var offset = Marshal.OffsetOf<TTable>("FirstRow").ToInt32();
            var rowSize = Marshal.SizeOf<TRow>();
            if (count > 65536 || (long)offset + (long)count * rowSize > length) throw Unverified();
            for (var i = 0; i < count; i++) read(Marshal.PtrToStructure<TRow>(IntPtr.Add(buffer, checked(offset + i * rowSize))));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TcpRowV4
    {
        internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TcpTableV4 { internal uint Count; internal TcpRowV4 FirstRow; }
    [StructLayout(LayoutKind.Sequential)] private struct TcpRowV6
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] LocalAddress;
        internal uint LocalScope, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] internal byte[] RemoteAddress;
        internal uint RemoteScope, RemotePort, State, ProcessId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TcpTableV6 { internal uint Count; internal TcpRowV6 FirstRow; }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool ordered,
        int addressFamily, int tableClass, uint reserved);
}
