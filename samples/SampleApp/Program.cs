using System.Net.Http;
using HttpClient.IoUring;
using HttpClient.IoUring.Extensions;

// Check if io_uring is available on this system.
if (!IoUringTransport.IsSupported)
{
    Console.WriteLine("io_uring is not supported on this system.");
    Console.WriteLine("Requires Linux kernel 5.1+ with io_uring enabled.");
    return;
}

// Option A: Extension method (recommended)
var handler = new SocketsHttpHandler();
using var transport = handler.UseIoUring(options =>
{
    options.RingSize = 256;
});

using var client = new System.Net.Http.HttpClient(handler);

// Make a simple GET request.
Console.WriteLine("Fetching https://httpbin.org/get ...");
try
{
    var response = await client.GetAsync("https://httpbin.org/get");
    Console.WriteLine($"Status: {response.StatusCode}");
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine(body[..Math.Min(body.Length, 500)]);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}

// Option B: Factory method
using var handler2 = IoUringTransport.CreateHandler(options =>
{
    options.RingSize = 128;
});
using var client2 = new System.Net.Http.HttpClient(handler2);

Console.WriteLine("\nFetching https://httpbin.org/ip ...");
try
{
    var response = await client2.GetStringAsync("https://httpbin.org/ip");
    Console.WriteLine(response);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
