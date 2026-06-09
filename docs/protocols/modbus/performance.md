# Modbus Performance Notes

> Last updated: 2026-06-09

## Overview

This document defines the performance measurement approach for `Nexus.Modbus`. Performance claims must be backed by reproducible benchmarks.

## Benchmark Targets

### Latency (Round-Trip Time)

| Metric | Target | Measurement |
|--------|--------|-------------|
| Single register read (TCP, localhost) | < 5 ms | `ReadInt16("40001")` |
| Single register write (TCP, localhost) | < 5 ms | `Write("40001", (short)1)` |
| Batch 10 registers (TCP, localhost) | < 8 ms | `ReadInt16s("40001", 10)` or batch |
| Single register read (virtual server) | < 2 ms | Against `ModbusTcpServer` |

### Throughput

| Metric | Target | Measurement |
|--------|--------|-------------|
| Sequential reads/sec (TCP) | > 200 ops/sec | Single-thread, localhost |
| Sequential writes/sec (TCP) | > 200 ops/sec | Single-thread, localhost |
| Parallel reads/sec (TCP, 4 connections) | > 500 ops/sec | Multi-connection |

### Connection Lifecycle

| Metric | Target | Measurement |
|--------|--------|-------------|
| Connect + first read | < 50 ms | TCP, localhost |
| Connect/disconnect cycle | < 20 ms | No data exchange |

## Benchmark Methodology

### Environment

- .NET 8.0 runtime
- `ModbusTcpServer` virtual server on localhost
- Default settings: Station 1, Timeout 5000ms
- Release build (`dotnet build -c Release`)

### How to Run

```bash
# Using dotnet run with benchmark project (to be created)
dotnet run --project tests/Nexus.Modbus.Benchmarks

# Or manual stopwatch approach
dotnet test tests/Nexus.Modbus.Tests --filter "Performance"
```

### Manual Measurement Pattern

```csharp
using System.Diagnostics;
using Nexus.Modbus;

using var server = new ModbusTcpServer();
server.Start(5502);

using var client = new ModbusTcpClient("127.0.0.1", 5502, station: 1);
client.Connect();

// Warmup
for (int i = 0; i < 100; i++) client.ReadInt16("40001");

// Measure
var sw = Stopwatch.StartNew();
int iterations = 1000;
for (int i = 0; i < iterations; i++)
    client.ReadInt16("40001");
sw.Stop();

Console.WriteLine($"{iterations} reads in {sw.ElapsedMilliseconds}ms");
Console.WriteLine($"Average: {(double)sw.ElapsedMilliseconds / iterations:F3}ms per read");
Console.WriteLine($"Throughput: {iterations / sw.Elapsed.TotalSeconds:F0} reads/sec");
```

## Known Performance Characteristics

### TCP vs RTU

- **TCP**: Each operation is a complete TCP round-trip. In short-connection mode, this includes TCP connect/disconnect overhead.
- **RTU**: Serial baud rate is the primary bottleneck. At 9600 baud, a typical 8-byte request + 8-byte response takes ~20ms just for transmission.
- **Persistent connection**: Use `SetPersistentConnection()` for high-throughput scenarios to avoid TCP handshake per operation.

### Thread Safety

- All Modbus TCP clients use an internal lock (`_lock` in `TcpDeviceBase`) to serialize concurrent operations on the same connection.
- For maximum throughput with concurrent reads, use multiple `ModbusTcpClient` instances (connection pooling).

### Address Parsing Overhead

- Address parsing (`ModbusAddressParser`) is O(1) for standard 5-digit addresses.
- Parsing adds negligible overhead compared to network round-trip time.

## Performance Anti-Patterns to Avoid

1. **Creating a new client per read**: Connection overhead dominates. Reuse client instances.
2. **`.Result` / `.Wait()` in async context**: May cause thread pool starvation under load.
3. **Reading single registers in a loop**: Use batch read (`ReadInt16s`, `BatchRead`) where possible to amortize round-trip overhead.
4. **Ignoring byte order**: Wrong byte order does not cause errors, only wrong values — hard to detect without reference data.

## Future Work

- [ ] Create `Nexus.Modbus.Benchmarks` project with BenchmarkDotNet
- [ ] Measure RTU performance with virtual serial port
- [ ] Compare short-connection vs persistent-connection mode
- [ ] Profile memory allocations under sustained load
- [ ] Test UDP throughput vs TCP
