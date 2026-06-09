# Siemens S7 Reconnect and Heartbeat

> Last updated: 2026-06-09

## Overview

S7 communication uses a multi-step handshake (TPKT → COTP connect → S7 setup). When the connection drops, reconnecting requires the full handshake sequence. This document provides guidance for reconnect and heartbeat patterns with `SiemensS7Client`.

## Connection Lifecycle

S7 connections have three phases:

1. **TPKT Connect** — TCP connection to port 102
2. **COTP Connect** — ISO-on-TCP connection request (includes TSAP routing)
3. **S7 Setup** — S7 communication setup (includes PLC type negotiation)

`SiemensS7Client.Connect()` handles all three steps internally. After disconnect (planned or network failure), the full sequence must repeat.

## Auto-Reconnect Pattern

### Using AutoReconnectGuard

```csharp
using Nexus;
using Nexus.Siemens;

var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10");
client.Connect();

using var guard = new AutoReconnectGuard(client)
{
    MaxRetries = 0,        // Unlimited retries
    BaseDelayMs = 2000,    // Start at 2 seconds
    MaxDelayMs = 60000,    // Cap at 60 seconds
    BackoffMultiplier = 2.0
};

guard.OnReconnecting += attempt => Console.WriteLine($"S7 reconnect attempt {attempt}...");
guard.OnReconnected += () => Console.WriteLine("S7 reconnected successfully");
guard.OnReconnectFailed += err => Console.WriteLine($"S7 reconnect failed: {err}");

guard.Start();
```

### Why S7 Needs Longer Delays

S7 PLCs may have protection mechanisms:
- **Connection protection level** in TIA Portal limits concurrent connections
- **Keep-alive timeout** on PLC side may hold the session for 30-60 seconds after disconnect
- **Maximum connections** (S7-1200: 3-8, S7-1500: 12-64) — if all slots are occupied, new connections are refused

**Recommendation**: Use `BaseDelayMs = 2000` and `MaxDelayMs = 60000` for S7, longer than Modbus defaults.

### S7-Specific Considerations

1. **Rack/Slot must be correct** — the COTP TSAP is derived from Rack and Slot. An incorrect Rack/Slot causes COTP rejection.
2. **PLC protection level** — check TIA Portal → Properties → Protection → Connection mechanism. "Permit access with PUT/GET" must be enabled.
3. **Optimized block access** — S7-1200Plus/S7-1500Plus use optimized DB access. Standard DB reads may fail on optimized blocks. Use `S7_1200`/`S7_1500` model for standard (non-optimized) DBs.

## Heartbeat Pattern

### Using HeartbeatGuard

```csharp
using var heartbeat = new HeartbeatGuard(
    client,
    () => Task.Run(() =>
    {
        // Use a lightweight read as heartbeat
        // Reading a known DB word is the most common approach
        return client.ReadInt16("DB1.DBW0");
    }),
    NullLogger.Instance)
{
    IntervalMs = 30000,   // Check every 30 seconds
    MaxConsecutiveFailures = 3,
    TimeoutMs = 5000
};

heartbeat.OnHeartbeatOk += () => { /* S7 connection alive */ };
heartbeat.OnHeartbeatFailed += (count, err) =>
{
    Console.WriteLine($"S7 heartbeat failed {count}x: {err}");
    // Optionally trigger reconnect
};

heartbeat.Start();
```

### Choosing a Heartbeat Register

| Option | Address | Pros | Cons |
|--------|---------|------|------|
| Known DB word | `DB1.DBW0` | Always available if DB1 exists | Must ensure DB1 exists |
| M-area word | `MW0` | Always exists | M-area may be used by program |
| System clock | — | No impact on user data | Not a simple read API |

**Recommendation**: Reserve a dedicated DB word (e.g., `DB1.DBW0`) as a heartbeat register. Ensure the DB exists and the word is not used by the PLC program.

## Persistent vs Short Connection

### S7 Persistent Connection (Recommended)

```csharp
var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10");
client.SetPersistentConnection(); // Keep TCP connection alive between operations
client.Connect();
```

S7 benefits strongly from persistent connections because:
- The 3-step handshake adds significant latency (50-200ms)
- PLC connection slots are limited; frequent connect/disconnect wastes slots
- Keep-alive at the TCP level detects disconnections early

### Short Connection (Avoid for S7)

```csharp
// Each operation: connect → handshake → read/write → disconnect
// S7 handshake overhead makes this very slow (~100-200ms per operation)
var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10");
// Without SetPersistentConnection(), each SendAndReceive reconnects
```

**Recommendation**: Always use `SetPersistentConnection()` for S7 unless there's a specific reason not to.

## Timeout Configuration

```csharp
var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10", port: 102, timeout: 5000);
```

| Scenario | Timeout | Notes |
|----------|---------|-------|
| Local network | 3000-5000ms | Normal |
| WAN/VPN | 10000-15000ms | Higher latency |
| Heavy PLC load | 10000ms | PLC may respond slowly during scan cycle |

## Error Recovery Flowchart

```
Operation fails
  ├─ IsSuccess = false?
  │   ├─ "Connection refused" → PLC may be offline, start reconnect guard
  │   ├─ "COTP rejected" → Check Rack/Slot and protection level
  │   ├─ "S7 error: 0xXX" → Check S7 error code, may be address error
  │   └─ Timeout → Network issue, heartbeat guard will detect
  │
  └─ HeartbeatGuard.OnHeartbeatFailed
      └─ Trigger AutoReconnectGuard
          ├─ Reconnect succeeds → Resume operations
          └─ Reconnect fails (MaxRetries) → Alert operator
```
