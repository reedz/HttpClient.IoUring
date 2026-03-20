using System.Net;
using System.Net.Http;
using BenchmarkDotNet.Attributes;
using HttpClient.IoUring.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace HttpClient.IoUring.Benchmarks;

/// <summary>
/// Measures per-request allocations: Gen0/Gen1/Gen2 collections and bytes allocated.
/// </summary>
[MemoryDiagnoser]
public class AllocationBenchmark
{
    private WebApplication _server = null!;
    private string _baseUrl = null!;

    private System.Net.Http.HttpClient _socketClient = null!;
    private System.Net.Http.HttpClient _iouringClient = null!;
    private IoUringTransport _transport = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        int port = GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        _server = builder.Build();
        _server.MapGet("/small", () => "OK");
        _server.MapGet("/medium", () => new string('x', 1024));
        await _server.StartAsync();
        _baseUrl = $"http://127.0.0.1:{port}";

        var socketHandler = new SocketsHttpHandler();
        _socketClient = new System.Net.Http.HttpClient(socketHandler);

        var iouringHandler = new SocketsHttpHandler();
        _transport = iouringHandler.UseIoUring();
        _iouringClient = new System.Net.Http.HttpClient(iouringHandler);

        // Warm up connections.
        await _socketClient.GetStringAsync($"{_baseUrl}/small");
        await _iouringClient.GetStringAsync($"{_baseUrl}/small");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _socketClient.Dispose();
        _iouringClient.Dispose();
        _transport.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
    }

    [Benchmark(Baseline = true)]
    public async Task<string> Socket_SmallResponse() =>
        await _socketClient.GetStringAsync($"{_baseUrl}/small");

    [Benchmark]
    public async Task<string> IoUring_SmallResponse() =>
        await _iouringClient.GetStringAsync($"{_baseUrl}/small");

    [Benchmark]
    public async Task<string> Socket_1KBResponse() =>
        await _socketClient.GetStringAsync($"{_baseUrl}/medium");

    [Benchmark]
    public async Task<string> IoUring_1KBResponse() =>
        await _iouringClient.GetStringAsync($"{_baseUrl}/medium");

    private static int GetRandomPort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
