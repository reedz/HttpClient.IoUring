using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HttpClient.IoUring.Tests.Helpers;

/// <summary>
/// Embedded Kestrel test server for integration tests.
/// Starts an HTTP (and optionally HTTPS) server on a random port.
/// </summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string HttpBaseUrl { get; }
    public string? HttpsBaseUrl { get; }
    public X509Certificate2? Certificate { get; }

    private TestServer(WebApplication app, string httpBaseUrl, string? httpsBaseUrl, X509Certificate2? cert)
    {
        _app = app;
        HttpBaseUrl = httpBaseUrl;
        HttpsBaseUrl = httpsBaseUrl;
        Certificate = cert;
    }

    public static async Task<TestServer> StartAsync(bool enableHttps = false)
    {
        int httpPort = GetRandomPort();
        int? httpsPort = enableHttps ? GetRandomPort() : null;
        X509Certificate2? cert = enableHttps ? GenerateSelfSignedCert() : null;

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, httpPort);
            if (httpsPort.HasValue && cert != null)
            {
                options.Listen(IPAddress.Loopback, httpsPort.Value, listenOptions =>
                {
                    listenOptions.UseHttps(cert);
                });
            }
        });

        var app = builder.Build();

        // Endpoints for testing
        app.MapGet("/", () => "Hello from io_uring test server!");

        app.MapGet("/echo-headers", (HttpContext ctx) =>
        {
            var headers = ctx.Request.Headers
                .Select(h => $"{h.Key}: {h.Value}")
                .OrderBy(h => h);
            return Results.Ok(string.Join("\n", headers));
        });

        app.MapPost("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text(body, "text/plain");
        });

        app.MapPut("/echo", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Text(body, "text/plain");
        });

        app.MapDelete("/resource/{id}", (int id) => Results.Ok(new { deleted = id }));

        app.MapGet("/status/{code}", (int code) => Results.StatusCode(code));

        app.MapGet("/delay/{ms}", async (int ms) =>
        {
            await Task.Delay(ms);
            return Results.Ok(new { delayed = ms });
        });

        app.MapGet("/large/{sizeKb}", (int sizeKb) =>
        {
            var data = new byte[sizeKb * 1024];
            Random.Shared.NextBytes(data);
            return Results.Bytes(data, "application/octet-stream");
        });

        app.MapGet("/redirect/{count}", (int count, HttpContext ctx) =>
        {
            if (count <= 0)
                return Results.Ok("Redirect complete");

            return Results.Redirect($"/redirect/{count - 1}");
        });

        app.MapGet("/chunked", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/plain";
            for (int i = 0; i < 5; i++)
            {
                await ctx.Response.WriteAsync($"chunk {i}\n");
                await ctx.Response.Body.FlushAsync();
            }
        });

        await app.StartAsync();

        string httpBaseUrl = $"http://127.0.0.1:{httpPort}";
        string? httpsBaseUrl = httpsPort.HasValue ? $"https://127.0.0.1:{httpsPort}" : null;

        return new TestServer(app, httpBaseUrl, httpsBaseUrl, cert);
    }

    private static int GetRandomPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, true));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddDnsName("localhost");
        request.CertificateExtensions.Add(sanBuilder.Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        // Export and re-import to get a cert with private key that works on Linux.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, "test"),
            "test",
            X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Certificate?.Dispose();
    }
}
