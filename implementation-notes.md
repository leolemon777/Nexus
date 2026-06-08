# Phase 1 — Connection Layer & Address Context Implementation Notes

## Decisions
- **SemaphoreSlim alongside `_lock`**: Added `protected readonly SemaphoreSlim _asyncLock` to TcpDeviceBase while keeping `_lock` for backward compat. 30+ subclasses use `lock (_lock)` — removing it would be a breaking change across all protocol libraries.
- **`_asyncLock` for base, `_lock` for subclasses**: TcpDeviceBase internal methods use `_asyncLock.Wait()`/`_asyncLock.WaitAsync()`, subclasses keep their `lock (_lock)` pattern.
- **`IsConnected` lock-free**: Changed from `lock (_lock)` to volatile read of `_client` — safe since `_client` is only mutated under `_asyncLock`.
- **`_persistentMode` volatile**: Upgraded from `protected bool` to `protected volatile bool` for thread-safe reads from non-lock contexts.
- **External guards**: AutoReconnectGuard and HeartbeatGuard are standalone classes, not part of TcpDeviceBase hierarchy — no modifications to existing subclasses needed.
- **ILogger in guards**: Both guards accept optional ILogger in constructor instead of accessing protected `TcpDeviceBase.Log`.
- **`RaiseDisconnected()` / `RaiseConnected()`**: Added to TcpDeviceBase for subclasses to fire events from custom connection logic (consistent with existing RaiseMessageSent/RaiseError pattern).

## Architecture
- **AutoReconnectGuard**: Subscribes to `OnDisconnected` event → exponential backoff timer → calls `device.Connect()` → fires OnReconnecting/OnReconnected/OnReconnectFailed events
- **HeartbeatGuard**: Takes `Func<Task<OperateResult>>` callback + `IReadWriteDevice` → Timer-based periodic heartbeat → failure counting → auto-stop after MaxConsecutiveFailures
- **AddressContext**: Static `Parse("x=3;s=2;D100")` → `IReadOnlyDictionary<string,string>` params + `CoreAddress` string. Immutable after construction. Provides `GetIntParameter()` convenience method.
- **TaskExtensions**: Internal `IsCompletedSuccessfully()` extension for netstandard2.0 (no `Task.IsCompletedSuccessfully` property until netcoreapp2.0).

## Verification
- `dotnet build Nexus.slnx` — 0 errors, 159 warnings (all pre-existing)
- `dotnet test Nexus.slnx` — 0 failures across all 39 test projects
- Nexus.Core.Tests — 75 tests pass (was 32 before this session)
  - AddressContext: 11 new tests (parse, params, edge cases, roundtrip)
  - AutoReconnectGuard: 4 new tests (start/stop/dispose/isReconnecting)
  - HeartbeatGuard: 3 new tests (start/stop, failure counting, success reset)

## Risks
- **Dual lock**: `_lock` and `_asyncLock` protect overlapping state (`_stream`, `_client`). If sync and async methods are called concurrently on the same instance, they won't be mutually exclusive. This is acceptable — typical usage is either sync OR async, not both.
- **Subclass migration**: Subclasses still using `lock (_lock)` should eventually migrate to `_asyncLock`. This is a future cleanup task.
- **HeartbeatGuard async void**: `HeartbeatCallback` uses `async void` (Timer callback requirement). Exceptions are caught internally to prevent crashing the process.

---

# BACnet/IP Implementation Notes

## Decisions
- **Target framework**: netstandard2.0 (consistent with other protocol libraries)
- **Inheritance**: `BacnetIpClient : UdpDeviceBase` — BACnet/IP uses UDP port 47808
- **Address format**: `ObjectType:Instance.PropertyId` (e.g., `AnalogInput:1.85` for present-value, `Device:1234.77` for object-name)
- **No external dependencies**: Pure BACnet encoding/decoding, no BACnet stack library

## Architecture
- **BacnetObject.cs**: Object types (56 types), Property IDs (175+), Application tags, BacnetObjectId struct with 10-bit type + 22-bit instance packing
- **BacnetApdu.cs**: APDU encoding (Who-Is, I-Am, ReadProperty, ReadPropertyMultiple, WriteProperty, WritePropertyMultiple, SubscribeCOV, AtomicReadFile/WriteFile) + decoding (all PDU types)
- **BacnetIpClient.cs**: BVLC framing (0x81 type + function + length), NPDU wrapping, confirmed/unconfirmed request/response handling, broadcast listener for I-Am/COV

## Spec Deviations from Task
- Renamed `SendAndReceive`/`SendAndReceiveAsync` wrapper methods to `SendBvlc`/`SendBvlcAsync` to avoid shadowing base class methods
- `_invokeId` is `int` not `byte` (required for `Interlocked.Increment`)
- COV notifications handled via background listener thread (required for unconfirmed notifications)

## Verification
- `dotnet build src/Nexus.Bacnet/Nexus.Bacnet.csproj` — 0 errors
- `dotnet test tests/Nexus.Bacnet.Tests/` — all unit tests pass
- Tests cover: ObjectId encoding, APDU encoding/decoding, frame structure, object types, property IDs, application tags

## Risks
- Reflection used to access private `_client` field from UdpDeviceBase (in `GetUdpClient()`) — fragile if base class changes field name
- `private new` keyword pattern not used; instead renamed wrapper methods to avoid ambiguity
- COV notification parsing is basic — full ASN.1 decoding of complex event parameters not implemented
