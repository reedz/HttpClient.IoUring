using System.Net.Http;
using FluentAssertions;
using HttpClient.IoUring.Extensions;
using HttpClient.IoUring.Tests.Helpers;
using Xunit;

namespace HttpClient.IoUring.Tests.Integration;

/// <summary>
/// Basic HTTP request integration tests using the io_uring transport.
/// </summary>
public class BasicRequestTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private System.Net.Http.HttpClient _client = null!;
    private IoUringTransport _transport = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var handler = new SocketsHttpHandler();
        _transport = handler.UseIoUring();
        _client = new System.Net.Http.HttpClient(handler);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _transport.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Get_ReturnsOk()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Hello from io_uring test server!");
    }

    [Fact]
    public async Task Get_StatusCode_Returns404()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/status/404");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_EchoBody()
    {
        var content = new StringContent("Hello io_uring!", System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Hello io_uring!");
    }

    [Fact]
    public async Task Put_EchoBody()
    {
        var content = new StringContent("Updated data", System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PutAsync($"{_server.HttpBaseUrl}/echo", content);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Updated data");
    }

    [Fact]
    public async Task Delete_Resource()
    {
        var response = await _client.DeleteAsync($"{_server.HttpBaseUrl}/resource/42");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("42");
    }

    [Fact]
    public async Task Get_LargePayload_64KB()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/large/64");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var data = await response.Content.ReadAsByteArrayAsync();
        data.Length.Should().Be(64 * 1024);
    }

    [Fact]
    public async Task Get_LargePayload_1MB()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/large/1024");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var data = await response.Content.ReadAsByteArrayAsync();
        data.Length.Should().Be(1024 * 1024);
    }

    [Fact]
    public async Task Post_LargeBody()
    {
        var payload = new string('X', 100_000);
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "text/plain");
        var response = await _client.PostAsync($"{_server.HttpBaseUrl}/echo", content);
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be(payload);
    }

    [Fact]
    public async Task Get_ChunkedResponse()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/chunked");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("chunk 0");
        body.Should().Contain("chunk 4");
    }

    [Fact]
    public async Task Get_Redirect()
    {
        var response = await _client.GetAsync($"{_server.HttpBaseUrl}/redirect/3");
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Redirect complete");
    }

    [Fact]
    public async Task MultipleSequentialRequests_ReuseConnection()
    {
        for (int i = 0; i < 10; i++)
        {
            var response = await _client.GetAsync($"{_server.HttpBaseUrl}/");
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task ConcurrentRequests_SameHost()
    {
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => _client.GetAsync($"{_server.HttpBaseUrl}/"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(System.Net.HttpStatusCode.OK));
    }
}
