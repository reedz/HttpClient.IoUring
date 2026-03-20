using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HttpClient.IoUring.Native;
using AddressFamily = HttpClient.IoUring.Native.AddressFamily;

namespace HttpClient.IoUring.IO;

/// <summary>
/// Creates outbound TCP connections using <c>IORING_OP_CONNECT</c>.
/// Resolves DNS, creates a socket, submits the connect SQE, and returns an <see cref="IoUringStream"/>.
/// </summary>
internal sealed class IoUringConnector
{
    private readonly IoUringClientLoop _loop;
    private readonly TimeSpan _connectTimeout;

    public IoUringConnector(IoUringClientLoop loop, TimeSpan connectTimeout)
    {
        _loop = loop;
        _connectTimeout = connectTimeout;
    }

    /// <summary>
    /// Resolves <paramref name="host"/> and connects to <paramref name="port"/> via io_uring.
    /// Tries IPv6 first, falls back to IPv4 (simple Happy Eyeballs).
    /// </summary>
    public async ValueTask<IoUringStream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        if (addresses.Length == 0)
            throw new SocketException((int)SocketError.HostNotFound);

        // Sort: IPv6 first, then IPv4.
        Array.Sort(addresses, (a, b) =>
            (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 0 : 1)
            - (b.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 0 : 1));

        Exception? lastException = null;

        foreach (var addr in addresses)
        {
            try
            {
                return await ConnectToAddressAsync(addr, port, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostNotFound);
    }

    private async ValueTask<IoUringStream> ConnectToAddressAsync(
        IPAddress address, int port, CancellationToken cancellationToken)
    {
        int af = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? AddressFamily.AF_INET6
            : AddressFamily.AF_INET;

        int fd = Libc.socket(af, AddressFamily.SOCK_STREAM, 0);
        if (fd < 0)
            throw new SocketException(Marshal.GetLastPInvokeError());

        try
        {
            // TCP_NODELAY
            SetTcpNoDelay(fd);

            // Build sockaddr and submit CONNECT.
            int result;
            if (af == AddressFamily.AF_INET)
                result = await ConnectIpv4Async(fd, address, port);
            else
                result = await ConnectIpv6Async(fd, address, port);

            if (result < 0)
            {
                int errno = -result;
                throw new SocketException(ErrnoToSocketError(errno));
            }

            // Register fd for IOSQE_FIXED_FILE.
            int fileIndex = -1;
            lock (_loop.Ring.SubmitLock)
            {
                if (_loop.Ring.HasRegisteredFiles)
                    fileIndex = _loop.Ring.RegisterFd(fd);
            }

            return new IoUringStream(fd, fileIndex, _loop);
        }
        catch
        {
            Libc.close(fd);
            throw;
        }
    }

    private unsafe ValueTask<int> ConnectIpv4Async(int fd, IPAddress address, int port)
    {
        var sockaddr = new SockaddrIn
        {
            Family = (ushort)AddressFamily.AF_INET,
            Port = SockaddrIn.HostToNetworkOrder((ushort)port),
        };

        // sin_addr must be in network byte order (big-endian in memory).
        // TryWriteBytes gives [127, 0, 0, 1] for 127.0.0.1 — that IS network byte order.
        // Memcpy into the uint field to preserve the byte layout.
        Span<byte> addrBytes = stackalloc byte[4];
        address.TryWriteBytes(addrBytes, out _);
        fixed (byte* src = addrBytes)
        {
            Buffer.MemoryCopy(src, &sockaddr.Addr, 4, 4);
        }

        return SubmitConnect(fd, (byte*)&sockaddr, sizeof(SockaddrIn));
    }

    private unsafe ValueTask<int> ConnectIpv6Async(int fd, IPAddress address, int port)
    {
        var sockaddr = new SockaddrIn6
        {
            Family = (ushort)AddressFamily.AF_INET6,
            Port = SockaddrIn.HostToNetworkOrder((ushort)port),
            ScopeId = (uint)address.ScopeId,
        };

        Span<byte> addrBytes = stackalloc byte[16];
        address.TryWriteBytes(addrBytes, out _);
        for (int i = 0; i < 16; i++)
            sockaddr.Addr[i] = addrBytes[i];

        return SubmitConnect(fd, (byte*)&sockaddr, sizeof(SockaddrIn6));
    }

    private unsafe ValueTask<int> SubmitConnect(int fd, byte* sockaddrPtr, int sockaddrLen)
    {
        var completion = PooledCompletion.Rent();
        ulong opId;
        bool submitted = false;

        lock (_loop.Ring.SubmitLock)
        {
            opId = _loop.AllocateOpId();
            _loop.RegisterPending(opId, completion);

            if (_loop.Ring.TryGetSqe(out IoUringSqe* sqe))
            {
                sqe->Opcode = IoUringConstants.IORING_OP_CONNECT;
                sqe->Fd = fd;
                sqe->AddrOrSpliceOffIn = (ulong)sockaddrPtr;
                sqe->OffOrAddr2 = (ulong)sockaddrLen;
                sqe->UserData = opId;
                submitted = true;
            }
        }

        if (!submitted)
        {
            completion.SetResult(-IoUringConstants.EAGAIN);
            return completion.AsValueTask();
        }

        _loop.Submit();
        return completion.AsValueTask();
    }

    private static unsafe void SetTcpNoDelay(int fd)
    {
        int one = 1;
        Libc.setsockopt(fd, IoUringConstants.IPPROTO_TCP, IoUringConstants.TCP_NODELAY,
            (nint)Unsafe.AsPointer(ref one), sizeof(int));
    }

    private static int ErrnoToSocketError(int errno) => errno switch
    {
        111 => (int)SocketError.ConnectionRefused,
        110 => (int)SocketError.TimedOut,
        113 => (int)SocketError.HostUnreachable,
        101 => (int)SocketError.NetworkUnreachable,
        104 => (int)SocketError.ConnectionReset,
        _ => errno,
    };
}
