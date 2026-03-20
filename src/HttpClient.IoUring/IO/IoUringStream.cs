using System.Buffers;
using System.Runtime.InteropServices;
using HttpClient.IoUring.Native;

namespace HttpClient.IoUring.IO;

/// <summary>
/// A <see cref="Stream"/> backed by io_uring RECV/SEND operations.
/// Returned by <see cref="IoUringConnector"/> after a successful CONNECT.
/// <para>
/// Each <see cref="ReadAsync(Memory{byte}, CancellationToken)"/> submits an <c>IORING_OP_RECV</c> SQE.
/// Each <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/> submits one or more <c>IORING_OP_SEND</c> SQEs.
/// </para>
/// </summary>
internal sealed class IoUringStream : Stream
{
    private readonly int _socketFd;
    private readonly int _fileIndex; // -1 if not registered
    private readonly IoUringClientLoop _loop;
    private readonly bool _enableZeroCopySend;
    private readonly int _zeroCopySendThreshold;
    private volatile bool _disposed;

    public IoUringStream(int socketFd, int fileIndex, IoUringClientLoop loop,
        bool enableZeroCopySend = false, int zeroCopySendThreshold = 4096)
    {
        _socketFd = socketFd;
        _fileIndex = fileIndex;
        _loop = loop;
        _enableZeroCopySend = enableZeroCopySend;
        _zeroCopySendThreshold = zeroCopySendThreshold;
    }

    public override bool CanRead => !_disposed;
    public override bool CanWrite => !_disposed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use ReadAsync for io_uring streams.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("Use WriteAsync for io_uring streams.");

    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Flush() { }

    public override Task FlushAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // ── ReadAsync ──

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return 0;

        // Buffer ring path: kernel selects buffer, IO loop copies into caller's buffer.
        if (_loop.HasBufferRing)
        {
            int result = await SubmitRecvWithBufRing(buffer);
            if (result < 0)
            {
                int errno = -result;
                if (errno is 125 or 104 or 32 or 105 /* ENOBUFS */)
                    return 0;
                throw new IOException($"io_uring RECV failed with errno {errno} ({GetErrorName(errno)})");
            }
            return result;
        }

        // Standard path: pin caller's buffer for kernel DMA.
        var handle = buffer.Pin();
        try
        {
            int result = await SubmitRecv(handle, buffer.Length);

            if (result < 0)
            {
                int errno = -result;
                if (errno is 125 or 104 or 32)
                    return 0;
                throw new IOException($"io_uring RECV failed with errno {errno} ({GetErrorName(errno)})");
            }

            return result;
        }
        finally
        {
            handle.Dispose();
        }
    }

    private unsafe ValueTask<int> SubmitRecvWithBufRing(Memory<byte> callerBuffer)
    {
        var completion = PooledCompletion.Rent();
        ulong opId;
        bool submitted = false;

        lock (_loop.Ring.SubmitLock)
        {
            opId = _loop.AllocateOpId();
            _loop.RegisterRecvWithBufRing(opId, completion, callerBuffer);

            if (_loop.Ring.TryGetSqe(out IoUringSqe* sqe))
            {
                sqe->Opcode = IoUringConstants.IORING_OP_RECV;
                // Use raw fd (not registered) for buffer ring recv to avoid flag conflicts.
                sqe->Fd = _socketFd;
                sqe->AddrOrSpliceOffIn = 0;
                sqe->Len = (uint)_loop.BufferRingBufferSize;
                sqe->Flags = IoUringConstants.IOSQE_BUFFER_SELECT;
                sqe->BufIndexOrGroup = IoUringClientLoop.RECV_BUF_GROUP_ID;
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

    private unsafe ValueTask<int> SubmitRecv(MemoryHandle handle, int length)
    {
        var completion = PooledCompletion.Rent();
        ulong opId;
        bool submitted = false;

        lock (_loop.Ring.SubmitLock)
        {
            opId = _loop.AllocateOpId();
            _loop.RegisterPending(opId, completion);

            if (!_loop.Ring.TryGetSqe(out IoUringSqe* sqe))
            {
                // SQ full: flush pending and retry once.
                _loop.Ring.FlushAndEnter();
                
                _loop.Ring.TryGetSqe(out sqe);
            }

            if (sqe != null)
            {
                sqe->Opcode = IoUringConstants.IORING_OP_RECV;
                SetFd(sqe);
                sqe->AddrOrSpliceOffIn = (ulong)handle.Pointer;
                sqe->Len = (uint)length;
                sqe->UserData = opId;
                submitted = true;
            }
        }

        if (!submitted)
        {
            _loop.RegisterPending(opId, null!);
            completion.SetResult(-IoUringConstants.EAGAIN);
            return completion.AsValueTask();
        }

        _loop.Submit();
        return completion.AsValueTask();
    }

    // ── WriteAsync ──

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length == 0)
            return;

        int offset = 0;
        while (offset < buffer.Length)
        {
            var slice = buffer.Slice(offset);
            var handle = slice.Pin();
            bool useZc = _enableZeroCopySend && slice.Length >= _zeroCopySendThreshold;
            try
            {
                int sent = await SubmitSend(handle, slice.Length, useZc);

                if (sent <= 0)
                {
                    int errno = sent == 0 ? 32 /* EPIPE */ : -sent;
                    throw new IOException($"io_uring SEND failed with errno {errno} ({GetErrorName(errno)})");
                }

                offset += sent;

                // For SEND_ZC: wait for NOTIF CQE before unpinning memory.
                // The NOTIF opId was stored by SubmitSend; the IO loop completes it
                // when the kernel releases the buffer.
                if (useZc && _pendingZcNotifCompletion != null)
                {
                    await _pendingZcNotifCompletion.AsValueTask();
                    _pendingZcNotifCompletion = null;
                }
            }
            finally
            {
                handle.Dispose();
            }
        }
    }

    // Zero-copy notification tracking (one outstanding ZC send at a time per stream).
    private PooledCompletion? _pendingZcNotifCompletion;

    private unsafe ValueTask<int> SubmitSend(MemoryHandle handle, int length, bool zeroCopy)
    {
        var completion = PooledCompletion.Rent();
        ulong opId;
        bool submitted = false;

        lock (_loop.Ring.SubmitLock)
        {
            opId = _loop.AllocateOpId();
            _loop.RegisterPending(opId, completion);

            if (!_loop.Ring.TryGetSqe(out IoUringSqe* sqe))
            {
                _loop.Ring.FlushAndEnter();
                
                _loop.Ring.TryGetSqe(out sqe);
            }

            if (sqe != null)
            {
                sqe->Opcode = zeroCopy
                    ? IoUringConstants.IORING_OP_SEND_ZC
                    : IoUringConstants.IORING_OP_SEND;
                SetFd(sqe);
                sqe->AddrOrSpliceOffIn = (ulong)handle.Pointer;
                sqe->Len = (uint)length;
                sqe->UserData = opId;
                submitted = true;

                if (zeroCopy)
                {
                    var notifCompletion = PooledCompletion.Rent();
                    _loop.RegisterZcNotif(opId, notifCompletion);
                    _pendingZcNotifCompletion = notifCompletion;
                }
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

    // ── Fd helpers ──

    private unsafe void SetFd(IoUringSqe* sqe)
    {
        if (_fileIndex >= 0)
        {
            sqe->Fd = _fileIndex;
            sqe->Flags |= IoUringConstants.IOSQE_FIXED_FILE;
        }
        else
        {
            sqe->Fd = _socketFd;
        }
    }

    // ── Dispose ──

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (_fileIndex >= 0)
        {
            lock (_loop.Ring.SubmitLock)
            {
                _loop.Ring.UnregisterFd(_fileIndex);
            }
        }

        Libc.close(_socketFd);
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
        await default(ValueTask);
    }

    // ── Error names ──

    private static string GetErrorName(int errno) => errno switch
    {
        1 => "EPERM",
        4 => "EINTR",
        11 => "EAGAIN",
        32 => "EPIPE",
        104 => "ECONNRESET",
        110 => "ETIMEDOUT",
        111 => "ECONNREFUSED",
        113 => "EHOSTUNREACH",
        125 => "ECANCELED",
        _ => $"E{errno}",
    };
}
