using System.Runtime.CompilerServices;

namespace HttpClient.IoUring.IO;

/// <summary>
/// Lock-free slot array for mapping operation IDs to completions.
/// Much faster than ConcurrentDictionary for the io_uring completion dispatch hot path.
/// Slot index is derived from the operation ID modulo capacity.
/// </summary>
internal sealed class CompletionSlots
{
    private readonly Slot[] _slots;
    private readonly int _mask;

    public CompletionSlots(int capacity)
    {
        // Round up to power of two.
        int size = 1;
        while (size < capacity) size <<= 1;
        _slots = new Slot[size];
        _mask = size - 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(ulong opId, PooledCompletion completion)
    {
        int idx = (int)(opId & (uint)_mask);
        ref var slot = ref _slots[idx];
        // Spin if the slot is occupied (extremely rare — would require ring depth overflow).
        while (Interlocked.CompareExchange(ref slot.OpId, (long)opId, 0) != 0)
            Thread.SpinWait(1);
        Volatile.Write(ref slot.Completion, completion);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(ulong opId, out PooledCompletion? completion)
    {
        int idx = (int)(opId & (uint)_mask);
        ref var slot = ref _slots[idx];

        if (Volatile.Read(ref slot.OpId) == (long)opId)
        {
            completion = Volatile.Read(ref slot.Completion);
            Volatile.Write(ref slot.Completion, null);
            Volatile.Write(ref slot.OpId, 0); // release slot
            return completion != null;
        }

        completion = null;
        return false;
    }

    /// <summary>Iterates over all occupied slots. Used for shutdown cleanup only.</summary>
    public void DrainAll(Action<PooledCompletion> action)
    {
        for (int i = 0; i < _slots.Length; i++)
        {
            ref var slot = ref _slots[i];
            var completion = Interlocked.Exchange(ref slot.Completion, null);
            if (completion != null)
            {
                Volatile.Write(ref slot.OpId, 0);
                action(completion);
            }
        }
    }

    private struct Slot
    {
        public long OpId;              // 0 = empty
        public PooledCompletion? Completion;
    }
}
