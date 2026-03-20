# HttpClient.IoUring — Copilot Instructions

## Project Overview

This is an io_uring-backed transport for `SocketsHttpHandler` that replaces the default socket I/O layer via `ConnectCallback`. It uses Linux's `io_uring` for batched syscalls, zero-copy send, and buffer rings.

## Architecture

- `IoUringTransport` — Singleton that owns the shared Ring and IO loop, provides the `ConnectCallback`
- `IoUringStream` — `Stream` subclass: `ReadAsync` → RECV SQE, `WriteAsync` → SEND SQE
- `IoUringClientLoop` — Dedicated IO thread: `SubmitAndWait` + `ProcessCompletions`
- `IoUringConnector` — `IORING_OP_CONNECT` with DNS + timeout linking
- `Ring/` — io_uring ring wrapper (shared with Kestrel.Transport.IoUring)
- `Native/` — Linux syscall bindings (shared with Kestrel.Transport.IoUring)

## Code Style

- Use file-scoped namespaces
- `AllowUnsafeBlocks` is enabled — use `unsafe` for Ring/SQE/CQE manipulation
- Use `Volatile.Read`/`Volatile.Write` for memory ordering (not `Interlocked` for ring head/tail)
- Use pooled `IValueTaskSource` for zero-alloc async completions
- Target net8.0/net9.0/net10.0

## Testing

- Use xunit + FluentAssertions
- Integration tests use embedded Kestrel as the test server
- Run tests: `dotnet test`
- Run benchmarks: `dotnet run -c Release --project benchmarks/HttpClient.IoUring.Benchmarks`

## Build

```bash
dotnet build
dotnet test
dotnet pack src/HttpClient.IoUring -c Release -o ./artifacts
```
