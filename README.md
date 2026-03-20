# HttpClient.IoUring

[![NuGet](https://img.shields.io/nuget/v/HttpClient.IoUring.svg)](https://www.nuget.org/packages/HttpClient.IoUring)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A high-performance **io_uring** transport for [`SocketsHttpHandler`](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler) that replaces the default socket I/O with Linux's `io_uring` interface.

## Features

- **Drop-in replacement** — one `UseIoUring()` call replaces the socket transport
- **Batched syscalls** — a single `io_uring_enter` submits CONNECT + SEND + RECV across all connections
- **Zero-copy send** (`SEND_ZC`) for payloads >4KB — avoids kernel buffer copy
- **Registered file descriptors** (`IOSQE_FIXED_FILE`) — kernel skips fd lookup per SQE
- **Async connect** via `IORING_OP_CONNECT` — fully non-blocking connection establishment
- **Full SocketsHttpHandler features** — HTTP/1.1, HTTP/2, HTTP/3, TLS, connection pooling, redirects, decompression — all work automatically
- Targets **net8.0**, **net9.0**, **net10.0**

## Requirements

- Linux with kernel **5.1+** (for io_uring support)
- Kernel **6.0+** recommended (for zero-copy send, buffer rings)
- .NET 8 or later

## Installation

```bash
dotnet add package HttpClient.IoUring
```

## Quick Start

```csharp
using HttpClient.IoUring.Extensions;

var handler = new SocketsHttpHandler();
using var transport = handler.UseIoUring(options =>
{
    options.RingSize = 256;
});

using var client = new HttpClient(handler);
var response = await client.GetAsync("https://api.example.com/data");
```

### Factory Method

```csharp
using var handler = IoUringTransport.CreateHandler(options =>
{
    options.RingSize = 256;
});
using var client = new HttpClient(handler);
```

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  HttpClient                                                  │
│   new HttpClient(handler)                                    │
│         │                                                    │
│   SocketsHttpHandler (HTTP/1.1+2+3, pooling, TLS, etc.)     │
│    └── ConnectCallback = IoUringTransport.ConnectAsync        │
│              │                                                │
│         IoUringTransport                                     │
│          ├── IoUringConnector (IORING_OP_CONNECT + DNS)      │
│          ├── IoUringStream (RECV/SEND via io_uring)           │
│          └── IoUringClientLoop (shared Ring, IO thread)       │
│               └── Ring → SQ/CQ → io_uring_enter              │
└─────────────────────────────────────────────────────────────┘
```

### How it works

All `IoUringStream` instances share a single io_uring Ring. When multiple connections read/write concurrently, their SQEs are batched into a single `io_uring_enter` syscall:

```
Thread A: conn1.ReadAsync()  → enqueue RECV SQE
Thread B: conn2.WriteAsync() → enqueue SEND SQE
Thread C: conn3.Connect()    → enqueue CONNECT SQE
                                    │
IO Thread: io_uring_enter(3 SQEs) ←─┘  ← one syscall for 3 operations
```

## Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `RingSize` | 256 | SQ/CQ ring depth (power of two) |
| `ConnectTimeout` | 30s | TCP connect timeout |
| `EnableZeroCopySend` | false | Use SEND_ZC for large payloads |
| `ZeroCopySendThreshold` | 4096 | Minimum payload size for zero-copy |
| `MaxRegisteredFiles` | 256 | Registered fd table size (0 to disable) |

## io_uring Features Used

| Feature | Kernel | Purpose |
|---------|--------|---------|
| `IORING_OP_CONNECT` | 5.1+ | Async TCP connect |
| `IORING_OP_RECV` | 5.1+ | Receive response data |
| `IORING_OP_SEND` | 5.1+ | Send request data |
| `IORING_OP_SEND_ZC` | 6.0+ | Zero-copy send (opt-in) |
| `IORING_OP_CLOSE` | 5.6+ | Async close |
| `IORING_REGISTER_FILES` | 5.1+ | Registered file descriptors |

## Project Structure

```
├── src/HttpClient.IoUring/         ← NuGet library (net8.0/net9.0/net10.0)
├── tests/HttpClient.IoUring.Tests/ ← xunit tests
├── benchmarks/                      ← BenchmarkDotNet comparisons
└── samples/SampleApp/               ← Minimal usage example
```

## License

MIT
