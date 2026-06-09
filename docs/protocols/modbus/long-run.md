# Modbus Long-Run and Stability Notes

> Last updated: 2026-06-09

## Overview

Long-run validation confirms that Nexus Modbus clients can sustain hours of continuous operation without memory leaks, connection degradation, or unhandled exceptions. This document defines the long-run test approach and acceptance criteria.

## Long-Run Test Types

### 1. Sustained Polling (1 Hour)

**Purpose**: Verify no memory leak, no performance degradation, no silent connection loss.

| Parameter | Value |
|-----------|-------|
| Duration | ≥ 1 hour |
| Polling interval | 1 second |
| Operation | `ReadInt16("40001")` |
| Target | `ModbusTcpServer` virtual server |
| Acceptance | 0 unhandled exceptions, memory growth < 10MB |

**Expected results:**
- Success count ≈ 3600 (one per second for 1 hour)
- Failure count: 0 (against virtual server)
- No `ObjectDisposedException`, `SocketException`, or `NullReferenceException`
- Final `GC.GetTotalMemory()` within 10MB of start value

### 2. Reconnect Stress (100 Cycles)

**Purpose**: Verify connection lifecycle does not leak sockets or streams.

| Parameter | Value |
|-----------|-------|
| Cycles | 100 |
| Per cycle | Connect → Read 1 register → Disconnect → Dispose |
| Target | `ModbusTcpServer` virtual server |
| Acceptance | 0 leaked connections, all reads succeed |

**Expected results:**
- All 100 reads return `IsSuccess == true`
- `netstat` (or equivalent) shows no orphaned TCP connections
- No `SocketException: Only one usage of each socket address is allowed`

### 3. Network Interruption Recovery

**Purpose**: Verify client recovers gracefully when network is interrupted mid-session.

| Parameter | Value |
|-----------|-------|
| Duration | 10 minutes |
| Interruptions | Server stop/start every 2 minutes (3 interruptions) |
| Client mode | Persistent connection with auto-reconnect |
| Acceptance | Client recovers within timeout after each interruption |

**Expected results:**
- Before interruption: reads succeed
- During interruption: reads fail with clear timeout message
- After server restart: reads succeed again within reconnect window
- No unrecoverable state requiring client recreation

### 4. Multi-Register Throughput (Sustained)

**Purpose**: Verify batch operations sustain throughput without degradation.

| Parameter | Value |
|-----------|-------|
| Duration | 30 minutes |
| Operation | Write 10 registers → Read 10 registers → Verify |
| Interval | 500ms (2 ops/sec)
| Acceptance | All read-back values match written values |

## How to Run

### Manual Long-Run Test

```csharp
using System;
using System.Diagnostics;
using Nexus.Modbus;

// Sustained polling test
using var server = new ModbusTcpServer();
server.Start(5502);

using var client = new ModbusTcpClient("127.0.0.1", 5502, station: 1);
client.Connect();

int successCount = 0;
int failCount = 0;
long startMem = GC.GetTotalMemory(true);
var sw = Stopwatch.StartNew();

// Run for 1 hour
while (sw.Elapsed < TimeSpan.FromHours(1))
{
    var result = client.ReadInt16("40001");
    if (result.IsSuccess)
        successCount++;
    else
        failCount++;

    Thread.Sleep(1000); // 1 second interval

    // Progress report every 5 minutes
    if (successCount % 300 == 0)
    {
        long currentMem = GC.GetTotalMemory(false);
        Console.WriteLine($"[{sw.Elapsed:hh\\:mm\\:ss}] OK={successCount} FAIL={failCount} " +
                          $"Mem={currentMem / 1024}KB");
    }
}

long endMem = GC.GetTotalMemory(true);
Console.WriteLine($"\n=== Results ===");
Console.WriteLine($"Duration: {sw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine($"Success: {successCount}, Failed: {failCount}");
Console.WriteLine($"Memory: {startMem / 1024}KB → {endMem / 1024}KB (Δ{(endMem - startMem) / 1024}KB)");
```

### Reconnect Stress Test

```csharp
using Nexus.Modbus;

for (int i = 0; i < 100; i++)
{
    using var server = new ModbusTcpServer();
    server.Start(5502);

    using var client = new ModbusTcpClient("127.0.0.1", 5502, station: 1);
    client.Connect();

    var read = client.ReadInt16("40001");
    if (!read.IsSuccess)
        Console.WriteLine($"Cycle {i}: FAILED - {read.Message}");

    client.Disconnect();
    server.Dispose();
}
Console.WriteLine("100 connect/read/disconnect cycles completed.");
```

## Acceptance Criteria Summary

| Test | Duration/Cycles | Key Metric | Pass Threshold |
|------|-----------------|------------|----------------|
| Sustained polling | 1 hour | Success rate | > 99.9% (against virtual server: 100%) |
| Reconnect stress | 100 cycles | All reads succeed | 100% |
| Network interruption | 10 min / 3 interruptions | Recovery after each | Yes |
| Multi-register throughput | 30 min | Data integrity | 100% match |

## Known Stability Considerations

### TCP Time-Wait

In short-connection mode, rapid connect/disconnect cycles may exhaust ephemeral ports due to TCP TIME-WAIT state. For high-frequency operations, use persistent connection mode (`SetPersistentConnection()`).

### Serial Port Locking

For RTU/ASCII serial clients, the serial port is a shared resource. Concurrent reads from multiple threads on the same `ISerialPort` are serialized by the internal lock in `SerialDeviceBase`.

### Virtual Server Limitations

`ModbusTcpServer` runs on `TcpListener` and processes one client at a time per listener socket. For multi-client throughput testing, start multiple server instances on different ports.

### GC Pressure

Each Modbus operation allocates byte arrays for the request and response frames. Under sustained high-frequency polling (>100 ops/sec), this creates GC pressure. Monitor `GC.GetTotalMemory()` and Gen2 collections during long-run tests.

## Future Work

- [ ] Implement automated long-run test as `dotnet test` integration test with [Timeout] attribute
- [ ] Add `AutoReconnectGuard` integration test for network interruption scenario
- [ ] Measure and document RTU long-run performance at various baud rates
- [ ] Profile memory allocations with `dotnet-trace` during sustained polling
- [ ] Test concurrent multi-client access to `ModbusTcpServer`
