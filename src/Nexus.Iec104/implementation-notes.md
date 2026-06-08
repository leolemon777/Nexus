# IEC 60870-5-104 Implementation Notes

## Files Created

| File | Purpose |
|---|---|
| `src/Nexus.Iec104/Nexus.Iec104.csproj` | Library project (netstandard2.0) |
| `src/Nexus.Iec104/Iec104Types.cs` | TypeId, COT, QualityFlags enums + info structs |
| `src/Nexus.Iec104/Iec104Asdu.cs` | ASDU encode/decode + build helpers |
| `src/Nexus.Iec104/Iec104Client.cs` | Main client with APCI framing, background receive |
| `tests/Nexus.Iec104.Tests/Nexus.Iec104.Tests.csproj` | Test project |
| `tests/Nexus.Iec104.Tests/Iec104AsduTests.cs` | 18 unit tests |

## Design Decisions

1. **Inherits `TcpDeviceBase`** — reuses TCP connection, events, logging; overrides `Connect`/`ConnectAsync` to add STARTDT handshake
2. **Background receive loop** — runs on `Task.Run` after STARTDT; handles I/S/U frames independently; uses synchronous `NetworkStream.Read` (blocking on background thread is acceptable)
3. **STARTDT before receive loop** — `PerformStartDT` reads response directly from stream since the receive loop hasn't started yet
4. **TaskCompletionSource for request-response** — GI, commands, and read requests use TCS; receive loop completes matching TCS on confirmation/response
5. **Data cache** (`ConcurrentDictionary<int, Iec104DataPoint>`) — updated by GI/spontaneous/periodic data; `ReadBool`/`ReadFloat` read from cache or send `C_RD_NA_1`
6. **Address format** — `{prefix}:{ioa}` (e.g. `SP:100`, `MF:200`, `SC:1`); bare number defaults to `MeasuredFloat`

## Address Prefixes

| Prefix | PointType | Read Type | Write Type |
|---|---|---|---|
| `SP` | SinglePoint | bool | — |
| `DP` | DoublePoint | bool | — |
| `MN` | MeasuredNormalized | float | — |
| `MF` | MeasuredFloat | float | — |
| `SC` | SingleCommand | — | bool |
| `DC` | DoubleCommand | — | bool |
| `SN` | SetpointNormalized | — | float |

## Spec Deviations

- **Normalized values** use `raw / 32767.0f` simplification (true IEC 104 NVA range is -1..+1 with 2^-15 resolution)
- **Quality descriptor** bit positions simplified: bits 5-7 for point quality, bits 0-4 for measured value quality
- **t2 timeout** (send S-frame ack) not yet enforced with a timer — acknowledgment is implicit in I-frame responses

## Remaining Risks

- `_ackSeq` and `_lastActivity` are tracked but not used for flow control timeout (t1/t2)
- `new Disconnect()` hides base class — calling through `IReadWriteDevice` interface invokes base `Disconnect` (accepted pattern per existing protocols)
- No automatic reconnection on connection loss
