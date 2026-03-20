using FluentAssertions;
using Xunit;

namespace HttpClient.IoUring.Tests.Unit;

public class IoUringTransportTests
{
    [Fact]
    public void IsSupported_ReturnsTrue_OnLinux()
    {
        // We're on a Linux system with kernel 6.x — should be supported.
        IoUringTransport.IsSupported.Should().BeTrue();
    }

    [Fact]
    public void Transport_CanBeCreated_WithDefaultOptions()
    {
        using var transport = new IoUringTransport();
        transport.Should().NotBeNull();
    }

    [Fact]
    public void Transport_CanBeCreated_WithCustomOptions()
    {
        var options = new IoUringTransportOptions
        {
            RingSize = 128,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxRegisteredFiles = 64,
        };
        using var transport = new IoUringTransport(options);
        transport.Should().NotBeNull();
    }

    [Fact]
    public void CreateHandler_ReturnsConfiguredHandler()
    {
        using var handler = IoUringTransport.CreateHandler(options =>
        {
            options.RingSize = 64;
        });
        handler.Should().NotBeNull();
        handler.Should().BeOfType<System.Net.Http.SocketsHttpHandler>();
    }
}
