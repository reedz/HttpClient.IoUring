using System.Net;
using System.Net.Http;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using HttpClient.IoUring.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace HttpClient.IoUring.Benchmarks;

/// <summary>
/// Compares throughput (requests/sec) of io_uring vs default socket transport
/// at various concurrency levels.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ThroughputBenchmark
{
    private WebApplication _server = null!;
    private string _baseUrl = null!;

    private System.Net.Http.HttpClient _socketClient = null!;
    private System.Net.Http.HttpClient _iouringClient = null!;
    private IoUringTransport _transport = null!;

    [Params(1, 16, 64, 128, 256)]
    public int Concurrency { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        // Start embedded Kestrel server.
        int port = GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        _server = builder.Build();
        _server.MapGet("/", () => "OK");
        await _server.StartAsync();
        _baseUrl = $"http://127.0.0.1:{port}/";

        // Default socket handler.
        var socketHandler = new SocketsHttpHandler { MaxConnectionsPerServer = Concurrency };
        _socketClient = new System.Net.Http.HttpClient(socketHandler);

        // io_uring handler.
        var iouringHandler = new SocketsHttpHandler { MaxConnectionsPerServer = Concurrency };
        _transport = iouringHandler.UseIoUring();
        _iouringClient = new System.Net.Http.HttpClient(iouringHandler);

        // Warm up.
        await _socketClient.GetStringAsync(_baseUrl);
        await _iouringClient.GetStringAsync(_baseUrl);
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

    [Benchmark(Baseline = true), BenchmarkCategory("GET")]
    public async Task Socket_GET()
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < Concurrency; i++)
            tasks[i] = _socketClient.GetStringAsync(_baseUrl);
        await Task.WhenAll(tasks);
    }

    [Benchmark, BenchmarkCategory("GET")]
    public async Task IoUring_GET()
    {
        var tasks = new Task[Concurrency];
        for (int i = 0; i < Concurrency; i++)
            tasks[i] = _iouringClient.GetStringAsync(_baseUrl);
        await Task.WhenAll(tasks);
    }

    private static int GetRandomPort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
