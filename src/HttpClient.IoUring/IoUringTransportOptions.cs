namespace HttpClient.IoUring;

/// <summary>
/// Configuration options for the io_uring transport.
/// </summary>
public sealed class IoUringTransportOptions
{
    /// <summary>SQ/CQ ring depth. Must be a power of two. Default: 256.</summary>
    public uint RingSize { get; set; } = 256;

    /// <summary>Connect timeout. Default: 30 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enable zero-copy send (SEND_ZC) for payloads above <see cref="ZeroCopySendThreshold"/>.
    /// Requires kernel 6.0+. Default: false (will be enabled in a future version).
    /// </summary>
    public bool EnableZeroCopySend { get; set; }

    /// <summary>Minimum payload size for zero-copy send. Default: 4096 bytes.</summary>
    public int ZeroCopySendThreshold { get; set; } = 4096;

    /// <summary>
    /// Number of entries in the provided buffer ring for recv operations.
    /// Eliminates per-recv memory pinning. Set to 0 to disable. Must be a power of two. Default: 64.
    /// </summary>
    public int BufferRingSize { get; set; } = 64;

    /// <summary>Size of each buffer in the buffer ring. Default: 4096 bytes.</summary>
    public int BufferRingBufferSize { get; set; } = 4096;

    /// <summary>
    /// Maximum number of registered file descriptors.
    /// Set to 0 to disable registered files. Default: 256.
    /// </summary>
    public int MaxRegisteredFiles { get; set; } = 256;
}
