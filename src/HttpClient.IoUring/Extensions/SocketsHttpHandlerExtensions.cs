using System.Net.Http;

namespace HttpClient.IoUring.Extensions;

/// <summary>
/// Extension methods for configuring <see cref="SocketsHttpHandler"/> to use io_uring transport.
/// </summary>
public static class SocketsHttpHandlerExtensions
{
    /// <summary>
    /// Replaces the socket I/O layer with io_uring for all connections made by this handler.
    /// Sets <see cref="SocketsHttpHandler.ConnectCallback"/> to an io_uring-backed implementation.
    /// </summary>
    /// <param name="handler">The handler to configure.</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The <see cref="IoUringTransport"/> instance (dispose when the handler is no longer needed).</returns>
    /// <exception cref="PlatformNotSupportedException">Thrown if io_uring is not supported.</exception>
    public static IoUringTransport UseIoUring(
        this SocketsHttpHandler handler,
        Action<IoUringTransportOptions>? configure = null)
    {
        if (!IoUringTransport.IsSupported)
            throw new PlatformNotSupportedException(
                "io_uring is not supported on this system. " +
                "Requires Linux kernel 5.1+ with io_uring enabled.");

        var options = new IoUringTransportOptions();
        configure?.Invoke(options);

        var transport = new IoUringTransport(options);
        handler.ConnectCallback = transport.ConnectAsync;
        return transport;
    }
}
