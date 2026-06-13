# Connection Pool

> Last updated: 2026-06-11

## Overview

`ConnectionPool<T>` provides thread-safe, keyed connection pooling for `IReadWriteDevice` instances. It supports per-key pooling, idle timeout cleanup, optional health checks, and concurrency limits.

## Quick Start

```csharp
using Nexus;
using Nexus.Modbus;

var pool = new ConnectionPool<ModbusTcpClient>(
    deviceFactory: () =>
    {
        var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
        client.SetPersistentConnection();
        return client;
    },
    maxPoolSize: 5,
    idleTimeout: TimeSpan.FromMinutes(5));

// Acquire a connection
var client = pool.Acquire("plc-1");
try
{
    var value = client.ReadInt16("40001");
    Console.WriteLine(value.Content);
}
finally
{
    pool.Release("plc-1", client);
}
```

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `deviceFactory` | required | Factory function to create new device instances. |
| `maxPoolSize` | 5 | Maximum connections per key. |
| `idleTimeout` | 5 minutes | Time before idle connections are cleaned up. |
| `cleanupInterval` | same as idleTimeout | How often the cleanup timer runs. |
| `healthCheck` | null | Optional `Func<T, bool>` delegate for connection health verification. |

## API Reference

### Acquire / AcquireAsync

```csharp
T Acquire(string key)
Task<T> AcquireAsync(string key, CancellationToken ct = default)
```

Gets or creates a connection for the given key. If an idle connection exists and passes the health check, it's reused. If not, a new one is created via the factory. Respects `maxPoolSize` concurrency limit via `SemaphoreSlim`.

### Release / ReleaseAsync

```csharp
void Release(string key, T device)
Task ReleaseAsync(string key, T device)
```

Returns a connection to the pool for reuse. If the pool is full or the device is disconnected, the device is disposed.

### Remove / Clear / Dispose

```csharp
void Remove(string key)   // Remove all connections for a specific key
void Clear()              // Remove all connections from all keys
void Dispose()            // Stop cleanup timer and dispose all connections
```

## Health Check

When a `healthCheck` delegate is provided, `Acquire`/`AcquireAsync` will execute it on idle connections before returning them. Connections that fail the health check are **disposed immediately** and not returned to the caller.

```csharp
var pool = new ConnectionPool<ModbusTcpClient>(
    deviceFactory: () =>
    {
        var client = new ModbusTcpClient("192.168.1.100", 502);
        client.SetPersistentConnection();
        return client;
    },
    maxPoolSize: 5,
    healthCheck: device =>
    {
        // Simple health check: read a known register
        var result = device.ReadInt16("40001");
        return result.IsSuccess;
    });
```

**Important**: The health check delegate should be fast and non-blocking. Exceptions in the health check are caught and treated as "unhealthy" (returns false).

## Protocol-Specific Pool Wrappers

36 protocol-specific pool wrappers are provided. Each wraps the generic `ConnectionPool<T>` with protocol-appropriate defaults:

### Usage

```csharp
var pool = new ModbusTcpConnectionPool(
    ip: "192.168.1.100",
    port: 502,
    station: 1,
    maxPoolSize: 3);

// Read via pool
var result = pool.ReadInt16("40001");
Console.WriteLine(result.Content);

pool.Dispose();
```

### Available Wrappers

| Protocol | Wrapper Class |
|----------|---------------|
| Modbus TCP | `ModbusTcpConnectionPool` |
| Modbus RTU over TCP | `ModbusRtuOverTcpConnectionPool` |
| Modbus ASCII over TCP | `ModbusAsciiOverTcpConnectionPool` |
| Siemens S7 | `SiemensS7ConnectionPool` |
| Siemens FetchWrite | `SiemensFetchWriteConnectionPool` |
| Mitsubishi MC3E Binary | `Mc3EBinaryConnectionPool` |
| Mitsubishi A1E | `MelsecA1EConnectionPool` |
| Omron FINS TCP | `FinsTcpConnectionPool` |
| Omron HostLink | `OmronHostLinkConnectionPool` |
| AllenBradley CIP | `AllenBradleyCipConnectionPool` |
| AllenBradley PCCC | `PcccConnectionPool` |
| Beckhoff ADS | `BeckhoffAdsConnectionPool` |
| Keyence KV | `KeyenceKvConnectionPool` |
| LS Electric XGT | `LsXgtTcpConnectionPool` |
| Panasonic Mewtocol | — (not yet) |
| Delta DVP | `DeltaDvpConnectionPool` |
| Inovance Easy | `InovanceEasyConnectionPool` |
| Fatek FBs | `FatekConnectionPool` |
| Fuji SPH | `FujiSphConnectionPool` |
| GeSrtp | `GeSrtpConnectionPool` |
| Schneider | `SchneiderConnectionPool` |
| Yaskawa Memobus | `MemobusConnectionPool` |
| Yokogawa | `YokogawaConnectionPool` |
| Xinje | `XinjeConnectionPool` |
| Dnp3 | `Dnp3ConnectionPool` |
| IEC 61850 | `Iec61850ConnectionPool` |
| SECS/HSMS | `SecsHsmsConnectionPool` |
| Kuka EKI | `KukaEkiConnectionPool` |
| RKC Temperature | `RkcTemperatureConnectionPool` |
| Toledo | `ToledoConnectionPool` |
| Robot Efort | `EfortConnectionPool` |
| Robot Fanuc | `FanucRobotConnectionPool` |
| Robot Kuka | `KukaTcpConnectionPool` |
| Robot Yamaha | `YamahaRcxConnectionPool` |
| Robot Yaskawa | `Yrc1000ConnectionPool` |

## Lifecycle

```
Acquire("plc-1")  →  Factory creates client  →  Connect()  →  Use  →  Release("plc-1", client)
Acquire("plc-1")  →  Pop idle client  →  Health check ✓  →  Use  →  Release("plc-1", client)
Acquire("plc-1")  →  Pop idle client  →  Health check ✗  →  Dispose  →  Factory creates new
  ... idle timeout expires ...
Cleanup            →  Disconnect + Dispose idle client
```

## Thread Safety

- `Acquire` and `Release` are thread-safe (uses `ConcurrentDictionary` + `ConcurrentStack`).
- `SemaphoreSlim` limits concurrent access to `maxPoolSize` per key.
- If `maxPoolSize` connections are active, `Acquire` blocks until one is returned.

## Important Notes

1. The factory function is called on-demand — connections are **lazy-created**, not pre-created.
2. The pool calls `Connect()` on the factory-created device before returning it.
3. Idle cleanup calls `Dispose()` on expired connections.
4. Health check (if configured) runs on idle connections during `Acquire`.
5. Unhealthy connections are **disposed and replaced**, never returned to the caller.
