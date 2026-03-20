using System.Runtime.InteropServices;

namespace HttpClient.IoUring.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct SockaddrIn
{
    public ushort Family;   // AF_INET = 2
    public ushort Port;     // network byte order
    public uint Addr;       // IPv4 address, network byte order
    public ulong Zero;      // padding

    public static ushort HostToNetworkOrder(ushort value) =>
        BitConverter.IsLittleEndian
            ? (ushort)((value >> 8) | (value << 8))
            : value;

    public static uint HostToNetworkOrder(uint value) =>
        BitConverter.IsLittleEndian
            ? (uint)((value >> 24) |
                     ((value >> 8) & 0xFF00) |
                     ((value << 8) & 0xFF0000) |
                     (value << 24))
            : value;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SockaddrIn6
{
    public ushort Family;     // AF_INET6 = 10
    public ushort Port;       // network byte order
    public uint FlowInfo;
    public fixed byte Addr[16]; // IPv6 address
    public uint ScopeId;
}

internal static class AddressFamily
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 10;
    public const int SOCK_STREAM = 1;
}
