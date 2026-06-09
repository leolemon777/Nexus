# IReadWriteDevice Direct Implementation Audit

> Last updated: 2026-06-09

## Overview

13 clients implement `IReadWriteDevice` directly instead of inheriting from `TcpDeviceBase`, `SerialDeviceBase`, or `UdpDeviceBase`. This audit classifies each and recommends action.

## Classification

### Category A: Should Migrate to TcpDeviceBase (7 clients)

These clients manage their own TCP connection lifecycle and reimplement base class features (locking, events, auto-connect, disconnect). Migrating to `TcpDeviceBase` would give them thread safety, `SendAndReceive`, persistent connection, and TX/RX events for free.

| Client | Protocol | Transport | Events | Lock | Migration Effort |
|--------|----------|-----------|--------|------|-------------------|
| `DeltaDvpClient` | Delta DVP | TCP | 5 | 2 | Medium — DVP uses Modbus-like framing |
| `FanucClient` | FANUC FOCAS | TCP | 10 | 6 | High — complex multi-step handshake |
| `FujiSphClient` | Fuji SPH | TCP | 8 | 2 | Medium — custom frame protocol |
| `GeSrtpClient` | GE SRTP | TCP | 9 | 6 | Medium — SRTP frame format |
| `KukaEkiClient` | KUKA EKI | TCP | 8 | 6 | Medium — XML-based protocol |
| `LsXgtClient` | LS XGT | TCP | 8 | 2 | Medium — XGT frame format |
| `XinjeClient` | Xinje | TCP | 8 | 6 | Medium — similar to Modbus RTU over TCP |

**Recommendation**: Migrate in priority order:
1. **DeltaDvpClient** — Modbus-like framing, closest to existing patterns
2. **XinjeClient** — Also Modbus-variant
3. **GeSrtpClient**, **FujiSphClient**, **LsXgtClient** — Custom frame protocols
4. **KukaEkiClient**, **FanucClient** — Complex handshakes, highest risk

### Category B: Acceptable Direct Implementation (2 clients)

These use `Stream` (not raw TCP) and have legitimate reasons not to use the base classes.

| Client | Transport | Why Acceptable |
|--------|-----------|----------------|
| `FxLinkClient` | `Stream` | Supports FX-over-TCP/DTU scenarios where `ISerialPort` abstraction doesn't fit |
| `FinsSerialClient` | `Stream` | FINS serial over raw stream, similar pattern to FxLinkClient |

**Recommendation**: Keep as-is. Both are documented in their protocol docs.

### Category C: Robot/Special Protocol (3 clients)

These use completely different communication patterns (XML, proprietary binary, OPC UA stack).

| Client | Transport | Why Acceptable |
|--------|-----------|----------------|
| `UrClient` | TCP (UR protocol) | Robot protocol, not PLC register access pattern |
| `StaubliClient` | TCP (Staubli protocol) | Robot protocol |
| `OpcUaClient` | OPC UA stack | Completely different protocol layer; subscription model |

**Recommendation**: Keep as-is. Robot and OPC UA clients are fundamentally different from PLC register access patterns.

### Category D: Already Using Base Classes (not listed above)

All other protocol clients correctly inherit from `TcpDeviceBase`, `SerialDeviceBase`, or `UdpDeviceBase`. These are the production-proven pattern.

## Action Items

| Priority | Action | Target |
|----------|--------|--------|
| P1 | Migrate DeltaDvpClient to inherit TcpDeviceBase | Delta |
| P1 | Migrate XinjeClient to inherit TcpDeviceBase | Xinje |
| P2 | Migrate GeSrtpClient to inherit TcpDeviceBase | GE |
| P2 | Migrate FujiSphClient to inherit TcpDeviceBase | Fuji |
| P2 | Migrate LsXgtClient to inherit TcpDeviceBase | LS Electric |
| P3 | Evaluate KukaEkiClient migration feasibility | KUKA |
| P3 | Evaluate FanucClient migration feasibility | FANUC |
| — | Keep FxLinkClient, FinsSerialClient as direct IReadWriteDevice | — |
| — | Keep UrClient, StaubliClient, OpcUaClient as direct IReadWriteDevice | — |

## Migration Pattern

For Category A clients, the migration follows this pattern:

```csharp
// Before (direct IReadWriteDevice)
public class XxxClient : IReadWriteDevice
{
    private TcpClient _tcp;
    private NetworkStream _stream;
    private object _lock = new object();
    // ... custom connect, send, receive, events ...
}

// After (inherit TcpDeviceBase)
public class XxxClient : TcpDeviceBase
{
    // ResponseHeaderLength and GetResponsePayloadLength handle frame parsing
    protected override int ResponseHeaderLength => 10;
    protected override int GetResponsePayloadLength(byte[] header) => ...;

    // Connect() / SendAndReceive() from base class
    // OnMessageSent / OnMessageReceived / OnError from base class
    // Thread safety from base class _lock
}
```

Benefits of migration:
- Thread safety via base class `_lock`
- Auto-connect before send (short-connection mode)
- Persistent connection support via `SetPersistentConnection()`
- Standard TX/RX events for WPF PacketRecorder integration
- Consistent error handling and timeout behavior
- `AutoReconnectGuard` and `HeartbeatGuard` compatibility

## Risk Assessment

- **Low risk**: DeltaDvpClient, XinjeClient (Modbus-variant protocols)
- **Medium risk**: GeSrtpClient, FujiSphClient, LsXgtClient (custom framing)
- **High risk**: KukaEkiClient, FanucClient (complex handshake, may need custom connection lifecycle)
- **No risk**: Category B and C clients (no change recommended)
