using System.Net;
using System.Net.Http;
using FluentAssertions;
using HttpClient.IoUring.Extensions;
using HttpClient.IoUring.Tests.Helpers;
using Xunit;

namespace HttpClient.IoUring.Tests.Integration;

/// <summary>
/// Tests for SEND_ZC (zero-copy send) functionality.
/// Exercises the two-CQE flow: initial completion + NOTIF for buffer release.
/// </summary>
public class SendZeroCopyTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private System.Net.Http.HttpClient _client = null!;
    private IoUringTransport _transport = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var handler = new SocketsHttpHandler();
        _transport = handler.UseIoUring(o =>
        {
            o.EnableZeroCopySend = true;
            o.ZeroCopySendThreshold = 1024; // low threshold to exercise ZC path
        });
        _client = new System.Net.Http.HttpClient(handler);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _transport.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task SendZC_SmallGet_Works()
    {
        // Small GET — request headers are ~200 bytes, below threshold → regular SEND.
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Hello from io_uring test server!");
    }

    [Fact]
    public async Task SendZC_LargePost_Works()
    {
        // 10KB body → above 1KB threshold → should use SEND_ZC.
        var payload = new string('Z', 10_000);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be(payload);
    }

    [Fact]
    public async Task SendZC_100KB_Post_Works()
    {
        // 100KB body — exercises SEND_ZC with potential partial writes.
        var payload = new string('A', 100_000);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be(payload);
    }

    [Fact]
    public async Task SendZC_ConcurrentRequests()
    {
        // Multiple concurrent large POSTs with SEND_ZC.
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var payload = new string((char)('A' + i % 26), 5000);
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
            var response = await _client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Be(payload);
        }).ToArray();

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SendZC_SequentialRequests_NewConnections()
    {
        // Sequential large POSTs with SEND_ZC, each with a new connection.
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(1),
        };
        using var transport = handler.UseIoUring(o =>
        {
            o.EnableZeroCopySend = true;
            o.ZeroCopySendThreshold = 1024;
        });
        using var client = new System.Net.Http.HttpClient(handler);

        for (int i = 0; i < 3; i++)
        {
            var payload = new string((char)('A' + i), 5000);
            var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
            var response = await client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Be(payload);
            await Task.Delay(10); // Let pool expire connection
        }
    }
}
