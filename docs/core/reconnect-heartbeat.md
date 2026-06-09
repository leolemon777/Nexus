# Reconnect and Heartbeat

> Last updated: 2026-06-09

## Overview

Nexus provides two guard components for connection reliability: `AutoReconnectGuard` for automatic reconnection after disconnection, and `HeartbeatGuard` for periodic connection health checks. Both are external components that don't modify the device client itself.

## AutoReconnectGuard

### What It Does

`AutoReconnectGuard` monitors a `TcpDeviceBase` connection and automatically reconnects when the connection drops, using exponential backoff.

### Quick Start

```csharp
using Nexus;
using Nexus.Modbus;

var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
client.Connect();

// Attach reconnect guard
using var guard = new AutoReconnectGuard(client);
guard.OnReconnected += () => Console.WriteLine("Reconnected!");
guard.OnReconnectFailed += (err) => Console.WriteLine($"Reconnect failed: {err}");
guard.Start();

// Guard will auto-reconnect if the TCP connection drops
```

### Configuration

| Property | Default | Description |
|----------|---------|-------------|
| `MaxRetries` | 10 | Maximum reconnect attempts (0 = unlimited). |
| `BaseDelayMs` | 1000 | Initial retry delay in milliseconds. |
| `MaxDelayMs` | 30000 | Maximum retry delay cap (30 seconds). |
| `BackoffMultiplier` | 2.0 | Exponential backoff multiplier. |

**Retry delay formula**: `min(BaseDelayMs * BackoffMultiplier^attempt, MaxDelayMs)`

Example: BaseDelayMs=1000, BackoffMultiplier=2.0 → 1s, 2s, 4s, 8s, 16s, 30s (capped), 30s, ...

### Events

| Event | When |
|-------|------|
| `OnReconnecting(int attempt)` | About to attempt reconnect (attempt number). |
| `OnReconnected` | Reconnect succeeded. |
| `OnReconnectFailed(string error)` | All retries exhausted. |

### Lifecycle

```csharp
// Start monitoring
guard.Start();

// Stop monitoring (e.g., before intentional disconnect)
guard.Stop();

// Dispose stops monitoring and releases resources
guard.Dispose();
```

### Important Notes

1. `AutoReconnectGuard` works only with `TcpDeviceBase` subclasses (TCP clients).
2. Do NOT call `guard.Start()` before `client.Connect()` — the initial connection must succeed first.
3. Call `guard.Stop()` before intentional `client.Disconnect()` to prevent unwanted reconnect attempts.
4. The guard does **not** create a new client instance — it calls `client.Connect()` on the same instance.

## HeartbeatGuard

### What It Does

`HeartbeatGuard` periodically sends a heartbeat request to verify the connection is alive. If consecutive heartbeats fail, it raises an event so the application can take action (e.g., trigger reconnect).

### Quick Start

```csharp
using Nexus;
using Nexus.Modbus;

var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
client.Connect();

// Attach heartbeat: use a lightweight read as heartbeat
using var heartbeat = new HeartbeatGuard(
    client,
    async () => await Task.Run(() => client.ReadInt16("40001")),
    NullLogger.Instance);

heartbeat.OnHeartbeatOk += () => { /* connection alive */ };
heartbeat.OnHeartbeatFailed += (count, err) => Console.WriteLine($"Heartbeat failed {count}x: {err}");
heartbeat.Start();

// Heartbeat runs every 30 seconds by default
```

### Configuration

| Property | Default | Description |
|----------|---------|-------------|
| `IntervalMs` | 30000 | Heartbeat interval in milliseconds (30 seconds). |
| `MaxConsecutiveFailures` | 3 | Consecutive failures before triggering `OnHeartbeatFailed`. |
| `TimeoutMs` | 5000 | Per-heartbeat timeout (5 seconds). |

### Choosing a Heartbeat Operation

| Protocol | Good Heartbeat | Why |
|----------|---------------|-----|
| Modbus TCP | `ReadInt16("40001")` | Single register read, minimal overhead. |
| Siemens S7 | `ReadInt16("DB1.DBW0")` | Fast DB read. |
| Mitsubishi MC3E | `ReadInt16("D0")` | Single register. |
| Omron FINS | `ReadInt16("D0")` | DM read. |
| Allen-Bradley CIP | `ReadInt32("HeartbeatTag")` | DINT read. |

Choose a register/tag that:
- Is always available (controller-scoped, not program-scoped).
- Is read-only or a scratch register.
- Is a single word/DINT (minimizes wire overhead).

### Combining Reconnect and Heartbeat

```csharp
var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
client.Connect();

using var heartbeat = new HeartbeatGuard(
    client,
    () => Task.Run(() => client.ReadInt16("40001")),
    NullLogger.Instance);

using var reconnect = new AutoReconnectGuard(client);

// Heartbeat failure → trigger reconnect
heartbeat.OnHeartbeatFailed += (count, err) =>
{
    Console.WriteLine($"Heartbeat lost, triggering reconnect...");
    reconnect.TriggerReconnect();
};

heartbeat.Start();
reconnect.Start();
```

## Best Practices

1. **Always set a timeout** on the client itself — guards don't override client timeout.
2. **Use reconnect for TCP**, not for serial — serial reconnection usually requires physical intervention.
3. **Don't start guards until after initial connection succeeds**.
4. **Stop reconnect guard before intentional disconnect** to prevent auto-reconnect loops.
5. **Log guard events** for field diagnostics — they are the first indicator of network issues.
