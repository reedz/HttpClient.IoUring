using System.Net;
using System.Net.Http;
using System.Net.Security;
using FluentAssertions;
using HttpClient.IoUring.Extensions;
using HttpClient.IoUring.Tests.Helpers;
using Xunit;

namespace HttpClient.IoUring.Tests.Integration;

/// <summary>
/// TLS/HTTPS integration tests — verifies SslStream wraps IoUringStream correctly.
/// </summary>
public class HttpsTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync(enableHttps: true);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Https_Get_WithSelfSignedCert()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        using var transport = handler.UseIoUring();
        using var client = new System.Net.Http.HttpClient(handler);

        var response = await client.GetAsync($"{_server.HttpsBaseUrl}/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("Hello from io_uring test server!");
    }

    [Fact]
    public async Task Https_Post_WithSelfSignedCert()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        using var transport = handler.UseIoUring();
        using var client = new System.Net.Http.HttpClient(handler);

        var content = new StringContent("TLS payload", System.Text.Encoding.UTF8, "text/plain");
        var response = await client.PostAsync($"{_server.HttpsBaseUrl}/echo", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("TLS payload");
    }

    [Fact]
    public async Task Https_MultipleConcurrentRequests()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        using var transport = handler.UseIoUring();
        using var client = new System.Net.Http.HttpClient(handler);

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.GetAsync($"{_server.HttpsBaseUrl}/"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK));
    }

    [Fact]
    public async Task Https_LargePayload()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }
        };
        using var transport = handler.UseIoUring();
        using var client = new System.Net.Http.HttpClient(handler);

        var response = await client.GetAsync($"{_server.HttpsBaseUrl}/large/64");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = await response.Content.ReadAsByteArrayAsync();
        data.Length.Should().Be(64 * 1024);
    }
}
