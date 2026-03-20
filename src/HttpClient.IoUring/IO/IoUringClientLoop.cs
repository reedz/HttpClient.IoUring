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
    private readonly ConcurrentDictionary<ulong, PooledCompletion> _pendingOps = new();
    private volatile bool _stopping;
    private ulong _nextOpId;

    // Eventfd for signaling the IO loop from worker threads (e.g., shutdown).
    private readonly int _eventFd;
    private readonly ulong[] _eventFdReadBuf;
    private readonly ulong[] _eventFdWriteBuf;

    public IoUringClientLoop(IoUringTransportOptions options)
    {
        _ring = new Ring.Ring(options.RingSize);

        if (options.MaxRegisteredFiles > 0)
            _ring.InitFileTable(options.MaxRegisteredFiles);

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
        _pendingOps[opId] = completion;
    }

    /// <summary>
    /// Submits all pending SQEs to the kernel. Safe to call from any thread.
    /// </summary>
    internal void Submit() => _ring.Submit();

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
            foreach (var kvp in _pendingOps)
            {
                if (_pendingOps.TryRemove(kvp.Key, out var completion))
                    completion.SetException(new ObjectDisposedException(nameof(IoUringClientLoop)));
            }
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

            if (_pendingOps.TryRemove(cqe.UserData, out var completion))
            {
                completion.SetResult(cqe.Res);
            }
        }
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
        Libc.close(_eventFd);
    }
}
