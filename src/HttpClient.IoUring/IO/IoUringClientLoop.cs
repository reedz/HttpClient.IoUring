using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HttpClient.IoUring.Native;
using HttpClient.IoUring.Ring;

namespace HttpClient.IoUring.IO;

/// <summary>
/// Central IO loop that owns a shared <see cref="Ring.Ring"/> and processes completions
/// from all <see cref="IoUringStream"/> instances on a dedicated thread.
/// </summary>
internal sealed class IoUringClientLoop : IDisposable
{
    private readonly Ring.Ring _ring;
    private readonly Thread _ioThread;
    private readonly CompletionSlots _pendingOps;
    // SEND_ZC: maps opId → notif completion. Populated on initial CQE (F_MORE), consumed on NOTIF CQE.
    private readonly CompletionSlots _zcNotifCompletions;
    // SEND_ZC: maps opId → notif completion (pre-registered before SQE submit).
    private readonly CompletionSlots _zcNotifPending;
    private volatile bool _stopping;
    private ulong _nextOpId;

    // Buffer ring for recv operations (optional).
    private readonly ProvidedBufferRing? _bufferRing;
    internal const ushort RECV_BUF_GROUP_ID = 0;

    // Eventfd for signaling the IO loop from worker threads (e.g., shutdown).
    private readonly int _eventFd;
    private readonly ulong[] _eventFdReadBuf;
    private readonly ulong[] _eventFdWriteBuf;

    public IoUringClientLoop(IoUringTransportOptions options)
    {
        _ring = new Ring.Ring(options.RingSize);
        // Slot capacity = ring size * 2 to handle concurrent operations.
        int slotCapacity = (int)options.RingSize * 4;
        _pendingOps = new CompletionSlots(slotCapacity);
        _zcNotifCompletions = new CompletionSlots(slotCapacity);
        _zcNotifPending = new CompletionSlots(slotCapacity);

        if (options.MaxRegisteredFiles > 0)
            _ring.InitFileTable(options.MaxRegisteredFiles);

        // Set up provided buffer ring for recv operations.
        if (options.BufferRingSize > 0)
        {
            try
            {
                _bufferRing = new ProvidedBufferRing(
                    _ring.Fd, RECV_BUF_GROUP_ID,
                    options.BufferRingSize, options.BufferRingBufferSize);
            }
            catch
            {
                // Buffer rings require kernel 5.19+. Fall back to per-recv pinning.
                _bufferRing = null;
            }
        }

        // Set up eventfd for IO loop wakeup.
        _eventFd = Libc.eventfd(0, 0x800 /* EFD_NONBLOCK */);
        if (_eventFd < 0)
            throw new InvalidOperationException($"eventfd failed: {Marshal.GetLastPInvokeError()}");

        _eventFdReadBuf = GC.AllocateUninitializedArray<ulong>(1, pinned: true);
        _eventFdWriteBuf = GC.AllocateUninitializedArray<ulong>(1, pinned: true);
        _eventFdWriteBuf[0] = 1UL;

        // Submit initial eventfd READ.
        SubmitEventFdRead();
        _ring.Submit();

        _ioThread = new Thread(RunIoLoop)
        {
            Name = "IoUring-HttpClient-IO",
            IsBackground = true,
        };
        _ioThread.Start();
    }

    internal Ring.Ring Ring => _ring;

    /// <summary>Whether a provided buffer ring is available for recv operations.</summary>
    internal bool HasBufferRing => _bufferRing != null;

    /// <summary>Gets the buffer ring buffer size (for calculating copy lengths).</summary>
    internal int BufferRingBufferSize => _bufferRing?.BufferSize ?? 0;

    /// <summary>
    /// Allocates a unique operation ID for an SQE's UserData field.
    /// Must be called under <see cref="Ring.Ring.SubmitLock"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ulong AllocateOpId()
    {
        // Skip reserved sentinel values.
        ulong id;
        do
        {
            id = ++_nextOpId;
        }
        while (id == IoUringConstants.EVENTFD_USER_DATA || id == IoUringConstants.TIMEOUT_USER_DATA);
        return id;
    }

    /// <summary>Registers a pending completion for the given operation ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RegisterPending(ulong opId, PooledCompletion completion)
    {
        _pendingOps.Set(opId, completion);
    }

    /// <summary>
    /// Submits all pending SQEs to the kernel. Safe to call from any thread.
    /// </summary>
    internal void Submit() => _ring.Submit();

    /// <summary>
    /// Registers a zero-copy send notification completion.
    /// The NOTIF CQE reuses the same user_data as the initial SEND_ZC CQE.
    /// </summary>
    internal void RegisterZcNotif(ulong sendOpId, PooledCompletion notifCompletion)
    {
        _zcNotifPending.Set(sendOpId, notifCompletion);
    }

    private void RunIoLoop()
    {
        try
        {
            while (!_stopping)
            {
                try
                {
                    _ring.SubmitAndWait(1);
                    ProcessCompletions();
                }
                catch when (_stopping)
                {
                    break;
                }
                catch (InvalidOperationException)
                {
                    if (_stopping) break;
                    Thread.Sleep(1);
                }
            }
        }
        finally
        {
            _pendingOps.DrainAll(c =>
                c.SetException(new ObjectDisposedException(nameof(IoUringClientLoop))));
        }
    }

    private void ProcessCompletions()
    {
        while (_ring.TryPeekCompletion(out var cqe))
        {
            _ring.AdvanceCompletion();

            if (cqe.UserData == IoUringConstants.EVENTFD_USER_DATA)
            {
                if (!_stopping)
                    SubmitEventFdRead();
                continue;
            }

            bool isNotif = (cqe.Flags & IoUringConstants.IORING_CQE_F_NOTIF) != 0;

            if (isNotif)
            {
                // SEND_ZC NOTIF: kernel released the buffer.
                if (_zcNotifCompletions.TryRemove(cqe.UserData, out var notifCompletion))
                    notifCompletion!.SetResult(0);
                continue;
            }

            // Check for buffer ring selection.
            bool hasBuffer = (cqe.Flags & IoUringConstants.IORING_CQE_F_BUFFER) != 0;
            if (hasBuffer && _bufferRing != null && cqe.Res > 0)
            {
                ushort bufferId = (ushort)(cqe.Flags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);
                if (_pendingRecvWithBufRing.TryRemove(cqe.UserData, out var recvCtx))
                {
                    int bytesToCopy = Math.Min(cqe.Res, recvCtx.CallerBuffer.Length);
                    var src = _bufferRing.GetBuffer(bufferId).Slice(0, bytesToCopy);
                    src.CopyTo(recvCtx.CallerBuffer.Span);
                    _bufferRing.RecycleBuffer(bufferId);
                    recvCtx.Completion.SetResult(bytesToCopy);
                }
                else
                {
                    _bufferRing.RecycleBuffer(bufferId);
                }
                continue;
            }
            // Buffer ring recv that returned error or EOF — recycle buffer if present.
            if (hasBuffer && _bufferRing != null && cqe.Res <= 0)
            {
                ushort bufferId = (ushort)(cqe.Flags >> IoUringConstants.IORING_CQE_BUFFER_SHIFT);
                _bufferRing.RecycleBuffer(bufferId);
            }

            if (_pendingOps.TryRemove(cqe.UserData, out var completion))
            {
                bool hasMore = (cqe.Flags & IoUringConstants.IORING_CQE_F_MORE) != 0;

                if (hasMore && _zcNotifPending.TryRemove(cqe.UserData, out var pendingNotif))
                {
                    _zcNotifCompletions.Set(cqe.UserData, pendingNotif!);
                }

                completion!.SetResult(cqe.Res);
            }
            // Also check buffer ring recv ops that failed.
            else if (_pendingRecvWithBufRing.TryRemove(cqe.UserData, out var failedRecv))
            {
                failedRecv.Completion.SetResult(cqe.Res);
            }
        }
    }

    // Buffer ring recv tracking: maps opId → (completion, callerBuffer) for data copy on CQE.
    private readonly ConcurrentDictionary<ulong, RecvBufRingContext> _pendingRecvWithBufRing = new();

    internal readonly struct RecvBufRingContext
    {
        public readonly PooledCompletion Completion;
        public readonly Memory<byte> CallerBuffer;

        public RecvBufRingContext(PooledCompletion completion, Memory<byte> callerBuffer)
        {
            Completion = completion;
            CallerBuffer = callerBuffer;
        }
    }

    internal void RegisterRecvWithBufRing(ulong opId, PooledCompletion completion, Memory<byte> callerBuffer)
    {
        _pendingRecvWithBufRing[opId] = new RecvBufRingContext(completion, callerBuffer);
    }

    private unsafe void SubmitEventFdRead()
    {
        lock (_ring.SubmitLock)
        {
            if (_ring.TryGetSqe(out IoUringSqe* sqe))
            {
                sqe->Opcode = IoUringConstants.IORING_OP_READ;
                sqe->Fd = _eventFd;
                sqe->AddrOrSpliceOffIn = (ulong)(nint)Unsafe.AsPointer(ref _eventFdReadBuf[0]);
                sqe->Len = sizeof(ulong);
                sqe->UserData = IoUringConstants.EVENTFD_USER_DATA;
            }
        }
    }

    /// <summary>Wakes the IO loop by writing to eventfd.</summary>
    internal unsafe void WakeIoLoop()
    {
        Libc.write(_eventFd, Unsafe.AsPointer(ref _eventFdWriteBuf[0]), sizeof(ulong));
    }

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;

        WakeIoLoop();

        if (_ioThread.IsAlive)
            _ioThread.Join(TimeSpan.FromSeconds(5));

        _ring.Dispose();
        _bufferRing?.Dispose();
        Libc.close(_eventFd);
    }
}
