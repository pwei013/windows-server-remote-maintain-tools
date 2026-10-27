using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IISManager.Core;

namespace IISManager.Agent.Backends;

internal sealed record HttpSysCertificateRecord(string Kind, string IpAddress, int Port, string HostName,
    string Thumbprint, string StoreName, Guid ApplicationId, uint CertificateCheckMode,
    uint RevocationFreshness, uint RevocationTimeout, uint Flags, string? CtlIdentifier, string? CtlStore);

/// <summary>Read-only HTTP Server API enumeration; no netsh parsing or HTTP.sys writes.</summary>
[SupportedOSPlatform("windows")]
internal static class HttpSysCertificateReader
{
    private static readonly object Gate = new();
    internal static HttpSysCertificateRecord[] Read()
    {
        lock (Gate)
        {
            var status = HttpInitialize(new HttpVersion { Major = 2, Minor = 0 }, 2, IntPtr.Zero);
            if (status != 0) throw new OperationException("binding.httpSysUnavailable", "无法初始化 HTTP.sys 配置只读查询，拒绝 HTTPS 写入。");
            try
            {
                var records = new List<HttpSysCertificateRecord>();
                Enumerate<SslQuery>(1, token => new SslQuery { Query = 1, Token = token }, output =>
                {
                    var value = Marshal.PtrToStructure<SslSet>(output);
                    var address = ReadAddress(value.Address);
                    records.Add(Record("ip", address.Ip, address.Port, "", value.Parameters));
                });
                Enumerate<SniQuery>(5, token => new SniQuery { Query = 1, Key = new SniKey { Address = new SocketStorage { Padding = new byte[112] } }, Token = token }, output =>
                {
                    var value = Marshal.PtrToStructure<SniSet>(output);
                    records.Add(Record("sni", "*", ReadPort(value.Key.Address), Marshal.PtrToStringUni(value.Key.Host) ?? "", value.Parameters));
                });
                Enumerate<CcsQuery>(6, token => new CcsQuery { Query = 1, Address = new SocketStorage { Padding = new byte[112] }, Token = token }, output =>
                {
                    var value = Marshal.PtrToStructure<CcsSet>(output);
                    records.Add(Record("ccs", "*", ReadPort(value.Address), "", value.Parameters));
                });
                return records.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Port)
                    .ThenBy(x => x.IpAddress, StringComparer.Ordinal).ThenBy(x => x.HostName, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally { HttpTerminate(2, IntPtr.Zero); }
        }
    }

    private static void Enumerate<T>(int configurationId, Func<uint, T> query, Action<IntPtr> read) where T : struct
    {
        var inputSize = Marshal.SizeOf<T>();
        var input = Marshal.AllocHGlobal(inputSize);
        try
        {
            for (uint token = 0; token < 4096; token++)
            {
                Marshal.StructureToPtr(query(token), input, false);
                var status = HttpQueryServiceConfiguration(IntPtr.Zero, configurationId, input, inputSize, IntPtr.Zero, 0, out var length, IntPtr.Zero);
                if (status == 259) return; // ERROR_NO_MORE_ITEMS
                if (status != 122 || length <= 0 || length > 1024 * 1024)
                    throw new OperationException("binding.httpSysUnavailable", "无法完整枚举 HTTP.sys 证书记录，拒绝 HTTPS 写入。");
                var output = Marshal.AllocHGlobal(length);
                try
                {
                    status = HttpQueryServiceConfiguration(IntPtr.Zero, configurationId, input, inputSize, output, length, out _, IntPtr.Zero);
                    if (status != 0) throw new OperationException("binding.httpSysChanged", "HTTP.sys 证书记录查询期间发生变化，请重新预检。");
                    read(output);
                }
                finally { Marshal.FreeHGlobal(output); }
            }
            throw new OperationException("binding.httpSysLimit", "HTTP.sys 记录数量超过首版支持范围，拒绝 HTTPS 写入。");
        }
        finally { Marshal.FreeHGlobal(input); }
    }

    private static HttpSysCertificateRecord Record(string kind, string ip, int port, string host, SslParameters p)
    {
        if (p.HashLength > 128 || (p.HashLength > 0 && p.Hash == IntPtr.Zero))
            throw new OperationException("binding.httpSysInvalid", "HTTP.sys 证书记录格式无效。");
        var hash = new byte[p.HashLength];
        if (hash.Length > 0) Marshal.Copy(p.Hash, hash, 0, hash.Length);
        return new(kind, ip, port, host.ToLowerInvariant(), Convert.ToHexString(hash),
            (Marshal.PtrToStringUni(p.Store) ?? "MY").ToUpperInvariant(), p.ApplicationId,
            p.CheckMode, p.RevocationFreshness, p.RevocationTimeout, p.Flags,
            Marshal.PtrToStringUni(p.CtlIdentifier), Marshal.PtrToStringUni(p.CtlStore));
    }
    private static int ReadPort(SocketStorage storage) => ((storage.NetworkPort & 0xFF) << 8) | (storage.NetworkPort >> 8);
    private static (string Ip, int Port) ReadAddress(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) throw new OperationException("binding.httpSysInvalid", "HTTP.sys IP 记录无效。");
        var family = Marshal.ReadInt16(pointer);
        var size = family == 2 ? 16 : family == 23 ? 28 : 0;
        if (size == 0) throw new OperationException("binding.httpSysInvalid", "HTTP.sys 地址族不受支持。");
        var raw = new byte[size];
        Marshal.Copy(pointer, raw, 0, size);
        var address = new IPAddress(raw.AsSpan(family == 2 ? 4 : 8, family == 2 ? 4 : 16));
        return (BackendRules.NormalizeIp(address.ToString()), (raw[2] << 8) | raw[3]);
    }

    [StructLayout(LayoutKind.Sequential)] private struct HttpVersion { internal ushort Major, Minor; }
    [StructLayout(LayoutKind.Sequential)] private struct SocketStorage
    {
        // SOCKADDR_STORAGE requires 8-byte alignment, including inside CCS/SNI query structures.
        internal ushort Family, NetworkPort;
        internal uint Address;
        internal long Alignment;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)] internal byte[] Padding;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SslQuery { internal int Query; internal IntPtr Address; internal uint Token; }
    [StructLayout(LayoutKind.Sequential)] private struct SniKey { internal SocketStorage Address; internal IntPtr Host; }
    [StructLayout(LayoutKind.Sequential)] private struct SniQuery { internal int Query; internal SniKey Key; internal uint Token; }
    [StructLayout(LayoutKind.Sequential)] private struct CcsQuery { internal int Query; internal SocketStorage Address; internal uint Token; }
    [StructLayout(LayoutKind.Sequential)] private struct SslSet { internal IntPtr Address; internal SslParameters Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct SniSet { internal SniKey Key; internal SslParameters Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct CcsSet { internal SocketStorage Address; internal SslParameters Parameters; }
    [StructLayout(LayoutKind.Sequential)] private struct SslParameters
    {
        internal uint HashLength;
        internal IntPtr Hash;
        internal Guid ApplicationId;
        internal IntPtr Store;
        internal uint CheckMode, RevocationFreshness, RevocationTimeout;
        internal IntPtr CtlIdentifier, CtlStore;
        internal uint Flags;
    }
    [DllImport("httpapi.dll")] private static extern uint HttpInitialize(HttpVersion version, uint flags, IntPtr reserved);
    [DllImport("httpapi.dll")] private static extern uint HttpTerminate(uint flags, IntPtr reserved);
    [DllImport("httpapi.dll")] private static extern uint HttpQueryServiceConfiguration(IntPtr service, int configuration,
        IntPtr input, int inputLength, IntPtr output, int outputLength, out int returnLength, IntPtr overlapped);
}
