using System.Net;
using System.Net.Http;
using FluentAssertions;
using HttpClient.IoUring.Extensions;
using HttpClient.IoUring.Tests.Helpers;
using Xunit;

namespace HttpClient.IoUring.Tests.Stress;

/// <summary>
/// High-concurrency stress tests to verify stability under load.
/// </summary>
public class HighConcurrencyStressTest : IAsyncLifetime
{
    private TestServer _server = null!;
    private System.Net.Http.HttpClient _client = null!;
    private IoUringTransport _transport = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 256,
        };
        _transport = handler.UseIoUring(o => o.RingSize = 1024);
        _client = new System.Net.Http.HttpClient(handler);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _transport.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentRequests_100()
    {
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => _client.GetAsync($"{_server.HttpBaseUrl}/"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK));
    }

    [Fact]
    public async Task ConcurrentRequests_200()
    {
        var tasks = Enumerable.Range(0, 200)
            .Select(_ => _client.GetAsync($"{_server.HttpBaseUrl}/"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK));
    }

    [Fact]
    public async Task SustainedLoad_5Seconds()
    {
        long requests = 0;
        long errors = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var workers = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var response = await _client.GetAsync($"{_server.HttpBaseUrl}/", cts.Token);
                    if (response.IsSuccessStatusCode)
                        Interlocked.Increment(ref requests);
                    else
                        Interlocked.Increment(ref errors);
                }
                catch (OperationCanceledException) { break; }
                catch { Interlocked.Increment(ref errors); }
            }
        })).ToArray();

        await Task.WhenAll(workers);

        requests.Should().BeGreaterThan(100, "should complete many requests in 5 seconds");
        errors.Should().Be(0, "no errors during sustained load");
    }

    [Fact]
    public async Task MixedRequestSizes()
    {
        var tasks = new List<Task<HttpResponseMessage>>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(_client.GetAsync($"{_server.HttpBaseUrl}/"));
            tasks.Add(_client.GetAsync($"{_server.HttpBaseUrl}/large/1"));
            tasks.Add(_client.PostAsync($"{_server.HttpBaseUrl}/echo",
                new StringContent(new string('A', 1000))));
        }

        var responses = await Task.WhenAll(tasks);
        responses.Should().AllSatisfy(r =>
            r.StatusCode.Should().Be(HttpStatusCode.OK));
    }
}
