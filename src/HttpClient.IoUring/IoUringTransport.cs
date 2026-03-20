using System.Net.Http;
using HttpClient.IoUring.IO;
using HttpClient.IoUring.Ring;

namespace HttpClient.IoUring;

/// <summary>
/// Manages a shared io_uring ring and IO loop for all outbound HTTP connections.
/// Use <see cref="Extensions.SocketsHttpHandlerExtensions.UseIoUring"/> to wire this
/// as the <see cref="SocketsHttpHandler.ConnectCallback"/>.
/// </summary>
public sealed class IoUringTransport : IDisposable
{
    private readonly IoUringTransportOptions _options;
    private IoUringClientLoop? _loop;
    private IoUringConnector? _connector;
    private readonly object _initLock = new();
    private bool _disposed;

    /// <summary>Gets a value indicating whether io_uring is supported on this system.</summary>
    public static bool IsSupported => Ring.Ring.IsSupported;

    /// <summary>Initializes a new transport with default options.</summary>
    public IoUringTransport() : this(new IoUringTransportOptions()) { }

    /// <summary>Initializes a new transport with the specified options.</summary>
    public IoUringTransport(IoUringTransportOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Creates a <see cref="SocketsHttpHandler"/> pre-configured with io_uring transport.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Action<IoUringTransportOptions>? configure = null)
    {
        var options = new IoUringTransportOptions();
        configure?.Invoke(options);

        var transport = new IoUringTransport(options);
        var handler = new SocketsHttpHandler();
        handler.ConnectCallback = transport.ConnectAsync;
        return handler;
    }

    /// <summary>
    /// The <see cref="SocketsHttpHandler.ConnectCallback"/> delegate.
    /// Creates an io_uring-backed <see cref="Stream"/> for the connection.
    /// </summary>
    internal async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        EnsureInitialized();
        return await _connector!.ConnectAsync(
            context.DnsEndPoint.Host,
            context.DnsEndPoint.Port,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureInitialized()
    {
        if (_loop != null) return;
        lock (_initLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop != null) return;

            if (!IsSupported)
                throw new PlatformNotSupportedException(
                    "io_uring is not supported on this system. " +
                    "Requires Linux kernel 5.1+ with io_uring enabled.");

            _loop = new IoUringClientLoop(_options);
            _connector = new IoUringConnector(_loop, _options);
        }
    }

    /// <summary>Releases all io_uring resources and stops the IO loop.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loop?.Dispose();
    }
}
