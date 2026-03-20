# Benchmarks

Comparing `SocketsHttpHandler` with default socket transport vs io_uring transport (`UseIoUring()`).

## Environment

- **CPU**: AMD EPYC 9V74 (2 vCPU)
- **OS**: Linux 6.17
- **Runtime**: .NET 10
- **Benchmark**: Sustained load for 5 seconds per scenario, simple GET / returning "OK"
- **Server**: Embedded Kestrel on loopback

## Throughput (requests/sec)

| Concurrency | Socket | io_uring | Improvement |
|-------------|--------|----------|-------------|
| 1 | 9,146 | **15,841** | **+73%** |
| 16 | 16,717 | **24,325** | **+46%** |
| 64 | 19,312 | **25,696** | **+33%** |
| 128 | 20,932 | **27,716** | **+32%** |
| 256 | 20,499 | **26,778** | **+31%** |

io_uring's advantage is largest at low concurrency (73% at single-threaded!) because the batched syscall model reduces per-request overhead. At higher concurrency, both transports benefit from connection pooling, but io_uring maintains a consistent 30%+ advantage.

## Why io_uring is faster

1. **Batched syscalls**: Multiple connections' CONNECT/SEND/RECV are submitted in a single `io_uring_enter`
2. **No per-operation memory allocation**: Pooled `IValueTaskSource` completions
3. **Registered file descriptors**: Kernel skips fd lookup per SQE
4. **Single IO thread**: Avoids thread pool scheduling overhead for I/O completions

## Methodology

- QuickBench stress test: N worker tasks making sequential GET requests for 5 seconds
- Both transports use `SocketsHttpHandler` with `MaxConnectionsPerServer` = concurrency level
- Server and client on the same machine (loopback), measuring transport overhead not network latency
- Zero errors in all runs

