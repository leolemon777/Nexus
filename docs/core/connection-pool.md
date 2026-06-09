# Connection Pool

> Last updated: 2026-06-09

## Overview

`ConnectionPool<T>` provides thread-safe, keyed connection pooling for `IReadWriteDevice` instances. It supports per-key pooling, idle timeout cleanup, and concurrency limits.

## Quick Start

```csharp
using Nexus;
using Nexus.Modbus;

var pool = new ConnectionPool<ModbusTcpClient>(
    deviceFactory: () => new ModbusTcpClient("192.168.1.100", 502, station: 1),
    maxPoolSize: 5,
    idleTimeout: TimeSpan.FromMinutes(5));

// Rent a connection
OperateResult<ModbusTcpClient> rent = pool.Rent("plc-1");
if (rent.IsSuccess)
{
    var client = rent.Content;
    try
    {
        var value = client.ReadInt16("40001");
        Console.WriteLine(value.Content);
    }
    finally
    {
        pool.Return("plc-1", client);
    }
}
```

## Configuration

| Parameter | Default | Description |
|-----------|---------|-------------|
| `deviceFactory` | required | Factory function to create new device instances. |
| `maxPoolSize` | 5 | Maximum connections per key. |
| `idleTimeout` | 5 minutes | Time before idle connections are cleaned up. |
| `cleanupInterval` | same as idleTimeout | How often the cleanup timer runs. |

## API Reference

### Rent

```csharp
OperateResult<T> Rent(string key)
```

Gets or creates a connection for the given key. If an idle connection exists, it's reused. If not, a new one is created via the factory. Returns `OperateResult<T>` — check `IsSuccess` before using.

### Return

```csharp
void Return(string key, T device)
```

Returns a connection to the pool for reuse. The connection should still be in a usable state.

### Dispose

```csharp
void Dispose()
```

Disposes all pooled connections and stops the cleanup timer.

## Lifecycle

```
Rent("plc-1")  →  Factory creates client  →  Connect()  →  Use  →  Return("plc-1", client)
Rent("plc-1")  →  Reuses existing client  →  Use  →  Return("plc-1", client)
  ... idle timeout expires ...
Cleanup         →  Disconnect + Dispose idle client
```

## Thread Safety

- `Rent` and `Return` are thread-safe (uses `ConcurrentDictionary` + `ConcurrentStack`).
- `SemaphoreSlim` limits concurrent access to `maxPoolSize` per key.
- If `maxPoolSize` connections are active, `Rent` blocks until one is returned.

## Important Notes

1. The factory function is called on-demand — connections are **lazy-created**, not pre-created.
2. The pool does **not** call `Connect()` automatically — the factory should return a connected client, or the caller should connect after renting.
3. Idle cleanup calls `Disconnect()` and `Dispose()` on expired connections.
4. Connections are not health-checked on rent — use `HeartbeatGuard` if health verification is needed.

## Example: Multi-PLC Pool

```csharp
var pool = new ConnectionPool<ModbusTcpClient>(
    deviceFactory: () =>
    {
        var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
        client.Connect();
        return client;
    },
    maxPoolSize: 3);

// Multiple threads can share the pool
OperateResult<ModbusTcpClient> rent = pool.Rent("main-plc");
if (rent.IsSuccess)
{
    try
    {
        rent.Content.ReadInt16("40001");
    }
    finally
    {
        pool.Return("main-plc", rent.Content);
    }
}

pool.Dispose(); // Clean up all connections
```

## Limitations

- Currently exists in Nexus.Core but is **not yet consumed** by any protocol client internally.
- No built-in health check on rent — wrap with `HeartbeatGuard` if needed.
- No automatic reconnect of pooled connections — use `AutoReconnectGuard` per connection if needed.
