using System.Net;
using System.Net.Http;
using FluentAssertions;
using HttpClient.IoUring.Extensions;
using HttpClient.IoUring.Tests.Helpers;
using Xunit;

namespace HttpClient.IoUring.Tests.Stress;

/// <summary>
/// Connection churn tests: rapid connect/close cycles.
/// </summary>
public class ConnectionChurnStressTest : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task RapidConnectionChurn_50Connections()
    {
        // Single transport but force new connections each time via short lifetime.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(1),
            MaxConnectionsPerServer = 1,
        };
        using var transport = handler.UseIoUring();
        using var client = new System.Net.Http.HttpClient(handler);

        for (int i = 0; i < 50; i++)
        {
            var response = await client.GetAsync($"{_server.HttpBaseUrl}/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await Task.Delay(5); // let the pool expire the connection
        }
    }

    [Fact]
    public async Task ConcurrentConnectionChurn_20Parallel()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            // Each creates its own transport + ring.
            var handler = new SocketsHttpHandler();
            using var transport = handler.UseIoUring(o => o.RingSize = 16);
            using var client = new System.Net.Http.HttpClient(handler);

            var response = await client.GetAsync($"{_server.HttpBaseUrl}/");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }).ToArray();

        await Task.WhenAll(tasks);
    }
}
