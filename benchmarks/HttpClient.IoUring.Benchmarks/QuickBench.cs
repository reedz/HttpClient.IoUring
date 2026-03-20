using System.Diagnostics;
using System.Net;
using System.Net.Http;
using HttpClient.IoUring.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace HttpClient.IoUring.Benchmarks;

/// <summary>
/// Quick stress benchmark (non-BDN) that runs sustained load and reports throughput.
/// Usage: dotnet run -c Release -- --quick [duration_seconds] [concurrency]
/// </summary>
public static class QuickBench
{
    public static async Task RunAsync(string[] args)
    {
        int durationSec = args.Length > 0 ? int.Parse(args[0]) : 10;
        int concurrency = args.Length > 1 ? int.Parse(args[1]) : 64;

        Console.WriteLine($"QuickBench: duration={durationSec}s concurrency={concurrency}");
        Console.WriteLine();

        // Start server.
        int port = GetRandomPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
        var server = builder.Build();
        server.MapGet("/", () => "OK");
        await server.StartAsync();
        string url = $"http://127.0.0.1:{port}/";

        // Socket baseline.
        Console.WriteLine("=== Default SocketsHttpHandler ===");
        using (var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency })
        using (var client = new System.Net.Http.HttpClient(handler))
        {
            await client.GetStringAsync(url); // warmup
            await RunLoad(client, url, durationSec, concurrency);
        }

        Console.WriteLine();

        // io_uring transport.
        Console.WriteLine("=== SocketsHttpHandler + io_uring ===");
        using (var handler = new SocketsHttpHandler { MaxConnectionsPerServer = concurrency })
        using (var transport = handler.UseIoUring())
        using (var client = new System.Net.Http.HttpClient(handler))
        {
            await client.GetStringAsync(url); // warmup
            await RunLoad(client, url, durationSec, concurrency);
        }

        await server.StopAsync();
        await server.DisposeAsync();
    }

    private static async Task RunLoad(
        System.Net.Http.HttpClient client, string url, int durationSec, int concurrency)
    {
        long requests = 0;
        long errors = 0;
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));

        var sw = Stopwatch.StartNew();

        var workers = new Task[concurrency];
        for (int i = 0; i < concurrency; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        await client.GetStringAsync(url, cts.Token);
                        Interlocked.Increment(ref requests);
                    }
                    catch (OperationCanceledException) { break; }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            });
        }

        await Task.WhenAll(workers);
        sw.Stop();

        double rps = requests / sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  Duration:  {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Requests:  {requests:N0}");
        Console.WriteLine($"  Errors:    {errors:N0}");
        Console.WriteLine($"  Req/sec:   {rps:N0}");
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
