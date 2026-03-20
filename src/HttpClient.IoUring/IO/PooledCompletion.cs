using System.Collections.Concurrent;
using System.Threading.Tasks.Sources;

namespace HttpClient.IoUring.IO;

/// <summary>
/// Pooled <see cref="IValueTaskSource{TResult}"/> for io_uring operation completions.
/// Avoids allocating per-operation. Automatically returned to the pool when consumed.
/// </summary>
internal sealed class PooledCompletion : IValueTaskSource<int>
{
    private static readonly ConcurrentQueue<PooledCompletion> Pool = new();

    private ManualResetValueTaskSourceCore<int> _core;

    private PooledCompletion()
    {
        _core.RunContinuationsAsynchronously = true;
    }

    public static PooledCompletion Rent()
    {
        if (Pool.TryDequeue(out var item))
        {
            item._core.Reset();
            return item;
        }
        return new PooledCompletion();
    }

    public ValueTask<int> AsValueTask() => new(this, _core.Version);

    public short Version => _core.Version;

    public void SetResult(int result) => _core.SetResult(result);

    public void SetException(Exception ex) => _core.SetException(ex);

    int IValueTaskSource<int>.GetResult(short token)
    {
        var result = _core.GetResult(token);
        Pool.Enqueue(this);
        return result;
    }

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) =>
        _core.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _core.OnCompleted(continuation, state, token, flags);
}
