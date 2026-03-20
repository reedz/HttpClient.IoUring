using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentAssertions;
using HttpClient.IoUring.IO;
using HttpClient.IoUring.Native;
using HttpClient.IoUring.Ring;
using Xunit;
using Xunit.Abstractions;
using RingClass = HttpClient.IoUring.Ring.Ring;

namespace HttpClient.IoUring.Tests.Unit;

/// <summary>
/// Low-level tests for buffer ring recv, bypassing the HttpClient/Stream layers.
/// </summary>
public class BufferRingTests
{
    private readonly ITestOutputHelper _output;

    public BufferRingTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public unsafe void BufferRing_Registration_Succeeds()
    {
        using var ring = new RingClass(32);
        using var bufRing = new ProvidedBufferRing(ring.Fd, bgid: 0, ringEntries: 16, bufferSize: 4096);
        bufRing.GroupId.Should().Be(0);
        bufRing.BufferSize.Should().Be(4096);
    }

    [Fact]
    public unsafe void BufferRing_GetBuffer_ReturnsCorrectSize()
    {
        using var ring = new RingClass(32);
        using var bufRing = new ProvidedBufferRing(ring.Fd, bgid: 0, ringEntries: 16, bufferSize: 128);
        var buf = bufRing.GetBuffer(0);
        buf.Length.Should().Be(128);
        var buf5 = bufRing.GetBuffer(5);
        buf5.Length.Should().Be(128);
    }

    [Fact]
    public unsafe void BufferRing_SingleShot_Recv_Works()
    {
        // Create a connected socket pair via TCP loopback.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        clientSocket.Connect(IPAddress.Loopback, port);
        var serverSocket = listener.AcceptSocket();
        listener.Stop();

        int clientFd = (int)clientSocket.Handle;

        // Send data.
        serverSocket.Send("HELLO_BUFRING"u8.ToArray());

        using var ring = new RingClass(32);

        // Manually set up buffer ring using mmap for BOTH ring and buffers (matching C test).
        int nbufs = 16;
        int bufSize = 4096;
        nuint brSize = (nuint)(nbufs * sizeof(IoUringBuf));
        brSize = (brSize + 4095) & ~(nuint)4095;

        nint brMem = Libc.mmap(nint.Zero, brSize,
            IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
            0x22 /* MAP_PRIVATE | MAP_ANONYMOUS */, -1, 0);

        nint bufsMem = Libc.mmap(nint.Zero, (nuint)(nbufs * bufSize),
            IoUringConstants.PROT_READ | IoUringConstants.PROT_WRITE,
            0x22, -1, 0);

        _output.WriteLine($"brMem=0x{brMem:X}, bufsMem=0x{bufsMem:X}");

        // Register buffer ring.
        var reg = new IoUringBufReg
        {
            RingAddr = (ulong)brMem,
            RingEntries = (uint)nbufs,
            Bgid = 0,
        };
        int ret = IoUringNative.IoUringRegister(ring.Fd,
            IoUringConstants.IORING_REGISTER_PBUF_RING,
            (nint)Unsafe.AsPointer(ref reg), 1);
        _output.WriteLine($"Register: {ret}");
        ret.Should().Be(0);

        // Add buffers.
        var bufs = (IoUringBuf*)brMem;
        ushort tail = 0;
        for (int i = 0; i < nbufs; i++)
        {
            int idx = tail & (nbufs - 1);
            bufs[idx].Addr = (ulong)(bufsMem + i * bufSize);
            bufs[idx].Len = (uint)bufSize;
            bufs[idx].Bid = (ushort)i;
            tail++;
        }
        Volatile.Write(ref *(ushort*)(brMem + 14), tail);
        _output.WriteLine($"Tail committed: {tail}");
        _output.WriteLine($"buf[0].Addr=0x{bufs[0].Addr:X}");

        // Submit RECV with buffer selection.
        lock (ring.SubmitLock)
        {
            ring.TryGetSqe(out IoUringSqe* sqe);
            sqe->Opcode = IoUringConstants.IORING_OP_RECV;
            sqe->Fd = clientFd;
            sqe->Len = (uint)bufSize;
            sqe->Flags = IoUringConstants.IOSQE_BUFFER_SELECT;
            sqe->BufIndexOrGroup = 0;
            sqe->UserData = 42;
        }

        ring.SubmitAndWait(1);
        ring.TryPeekCompletion(out var cqe);
        ring.AdvanceCompletion();

        _output.WriteLine($"CQE: res={cqe.Res} flags=0x{cqe.Flags:X}");

        cqe.Res.Should().BeGreaterThan(0, $"errno={-cqe.Res}");

        bool hasBuffer = (cqe.Flags & IoUringConstants.IORING_CQE_F_BUFFER) != 0;
        hasBuffer.Should().BeTrue();

        ushort bufferId = (ushort)(cqe.Flags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);
        var data = new Span<byte>((void*)(bufsMem + bufferId * bufSize), cqe.Res);
        var str = System.Text.Encoding.UTF8.GetString(data);
        _output.WriteLine($"Received: '{str}'");
        str.Should().Be("HELLO_BUFRING");

        // Cleanup.
        var unreg = new IoUringBufReg { Bgid = 0 };
        IoUringNative.IoUringRegister(ring.Fd,
            IoUringConstants.IORING_UNREGISTER_PBUF_RING,
            (nint)Unsafe.AsPointer(ref unreg), 1);
        Libc.munmap(bufsMem, (nuint)(nbufs * bufSize));
        Libc.munmap(brMem, brSize);

        clientSocket.Close();
        serverSocket.Close();
    }
}

// Append size check
public partial class BufferRingTests_Sizes
{
    [Fact]
    public void StructSizes_MatchKernel()
    {
        System.Runtime.InteropServices.Marshal.SizeOf<IoUringBufReg>().Should().Be(40, "io_uring_buf_reg is 40 bytes");
        System.Runtime.InteropServices.Marshal.SizeOf<IoUringBuf>().Should().Be(16, "io_uring_buf is 16 bytes");
    }
}
