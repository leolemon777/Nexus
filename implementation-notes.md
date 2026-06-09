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

---

# Roadmap Calibration Notes

## Decisions
- **Created `CURRENT_GAP_MATRIX.md` as the active planning baseline**: `OVERTAKE_HSL_PLAN.md` and `PLAN.md` contain useful direction but stale status claims. The new document separates current repository facts from future ambition.
- **Created `PROTOCOL_READINESS.md` for module-level readiness**: This is the first Sprint 0 deliverable and gives every protocol project a conservative status, evidence summary, and promotion path.
- **Created `REAL_DEVICE_VALIDATION.md` and `HSL_MIGRATION_GUIDE.md`**: These establish the evidence bar for production claims and the first migration story for HSL users.
- **Started Modbus reference package docs**: Added `docs/protocols/modbus/*` and `src/Nexus.Modbus/README.md` as the package README draft.
- **Created `EXECUTION_PLAN.md`**: This is the active multi-workstream plan for parallel execution toward surpassing HSL.
- **Shifted the next milestone from stub removal to production gates**: Protocol implementations no longer show the old 65 protocol `NotImplementedException` debt. The next meaningful milestone is consistent production readiness for the top protocol families.
- **Top 5 first**: Prioritize Modbus, Siemens, Mitsubishi, Omron, and AllenBradley before broad new protocol expansion.
- **Core adoption over core existence**: `AutoReconnectGuard`, `HeartbeatGuard`, `ConnectionPool`, `AddressContext`, and `StructConverter` exist, but the next step is consistent usage and tests in main protocol clients.
- **WPF debugger as differentiator**: Treat packet logging, parsing, export, replay, monitoring, and diagnostic bundles as open-source value, not just UI polish.

## Current Signals
- Source projects: 40 under `src/`.
- Test projects: 39 under `tests/`.
- Protocol `NotImplementedException` debt outside `Nexus.Core`: none found. The WPF `ProtocolLogViewer.ConvertBack` placeholder has been removed.
- `IBatchReadWrite` clients: 14.
- `ISubscribeDevice` clients: 3.
- Virtual/server classes: 16.
- Top test-count signals: Modbus 227, Omron 112, Mitsubishi 92, Siemens 91, AllenBradley 63.
- Sprint 0 release assets started: readiness table, real-device validation matrix, HSL migration guide.
- Modbus docs now cover quickstart, address format, function codes, byte order, packet logging, parser API, and troubleshooting.
- Execution plan now defines workstreams, milestones, parallel batches, immediate next 10 tasks, and verification command bank.

## Verification
- Documentation-only calibration; no build or test run was required.
- Used `rg` scans for `NotImplementedException`, `IBatchReadWrite`, `ISubscribeDevice`, and virtual/server classes.
- Initial Modbus reference-doc verification ran `dotnet test tests\Nexus.Modbus.Tests` — 170 passed, 0 failed, 0 skipped. This was later superseded by the FC22/UDP-stability pass recorded below: 186 passed with the normal Modbus test command.

## Risks
- Existing planning documents may conflict with `CURRENT_GAP_MATRIX.md` until they are updated or retired.
- Counts are source-scan signals, not full feature audits. A client implementing an interface may still have incomplete feature depth.
- Real-device validation remains untracked; production readiness should not be claimed until device-model evidence is recorded.

---

# Agent Team Sprint 0 Execution Notes

## Decisions
- **Used parallel agents for disjoint work scopes**: one worker implemented Modbus packet parsing, one expanded the HSL migration guide, and one created Siemens docs. The main agent kept release/package/WPF edits local.
- **Kept Siemens code untouched**: `src/Nexus.Siemens/SiemensPpiClient.cs` has pre-existing user changes and still needs a dedicated audit.
- **Extended Modbus parser beyond the initial TCP target**: Added diagnostic entry points for TCP, UDP, RTU, ASCII, and RTU-over-TCP because all are public Modbus transports in this package.
- **Kept parser offline and non-throwing**: malformed frames return `ModbusPacketInfo.IsValid=false` plus `Error`; checksum failures still decode common PDU fields for diagnostics.
- **Preserved production-gate honesty**: Modbus now has docs, package README metadata, and parser tests, but real-device validation and benchmarks remain required before strong production claims.

## Architecture
- **`ModbusPacketParser`**: static offline parser with `ParseTcp`, `ParseUdp`, `ParseRtu`, `ParseAscii`, `ParseRtuOverTcp`, and transport-dispatch `Parse`.
- **`ModbusPacketInfo`**: diagnostic model with transport, direction, transaction/protocol/length, unit/station, function/base function, exception code, address, quantity, byte count, data, checksum status, raw frame, validity, and error text.
- **Transport mapping**: TCP/UDP share MBAP parsing; RTU/RTU-over-TCP share CRC16 parsing; ASCII decodes text frames and validates LRC.
- **WPF cleanup**: `StringToVisibilityConverter.ConvertBack` now returns `Binding.DoNothing`, removing the last non-core `NotImplementedException` hit.

## Verification
- `dotnet build src\Nexus.App` — passed; 2 warnings from existing WPF `CanExecuteChanged` warning emitted for the main and temporary WPF project.
- `dotnet pack src\Nexus.Modbus` — passed; package created at `src\Nexus.Modbus\bin\Release\Nexus.Modbus.1.0.0.nupkg`.
- Verified `.nupkg` contains `README.md`.
- `dotnet test tests\Nexus.Modbus.Tests --filter "FullyQualifiedName~ModbusPacketParserTests"` — 13 passed, 0 failed, 0 skipped.
- `dotnet test tests\Nexus.Modbus.Tests -- xunit.parallelizeTestCollections=false` — 183 passed, 0 failed, 0 skipped.
- `rg -n "NotImplementedException" src --glob "!src/Nexus.Core/**"` — no matches.

## Risks
- Superseded later in this file: plain `dotnet test tests\Nexus.Modbus.Tests` now passes after UDP integration tests were moved to dynamic server ports.
- Parser direction inference is diagnostic-grade. FC05/FC06 request and response frames are identical, so callers should pass direction when known.
- Parser does not yet correlate request/response latency or explain Modbus exception code meanings.
- Real-device validation is still the main blocker for production-ready claims.

---

# Agent Team Top 5 And CI Notes

## Decisions
- **Moved the next parallel batch to audit and release infrastructure**: Siemens PPI audit, Mitsubishi support matrix, and CI workflow were delegated with disjoint write scopes.
- **Kept PPI as audit-only**: The existing user change in `SiemensPpiClient.cs` appears reasonable, but no Siemens code was changed in this batch.
- **Documented Mitsubishi honestly**: MC3E Binary and A1E are the usable paths; MC3E ASCII, MC3E UDP, and FX serial remain audit targets.
- **Added contributor protocol rules before broad open-source work**: `CONTRIBUTING_PROTOCOLS.md` now captures clean-room, netstandard2.0, test, docs, and release-promotion requirements.
- **Added CI as validation scaffold, not release automation**: The workflow builds/tests/packs and uploads a Modbus artifact, but does not publish NuGet packages.

## Outputs
- `docs/protocols/siemens/ppi-audit.md` records frame-length, BCC, address, and test coverage findings.
- `docs/protocols/siemens/ppi.md` links to the detailed audit.
- `docs/protocols/mitsubishi/*` now includes index, support matrix, address format, and troubleshooting docs.
- `.github/workflows/build-test.yml` runs Windows restore/build/test and packs `Nexus.Modbus`.
- `docs/ci.md` explains CI command choices, including non-parallel xUnit collection execution.
- `CONTRIBUTING_PROTOCOLS.md` defines protocol contribution and promotion gates.

## Verification
- `dotnet test tests\Nexus.Siemens.Tests --filter "FullyQualifiedName~SiemensPpi"` — 4 passed, 0 failed, 0 skipped.
- `dotnet test tests\Nexus.Mitsubishi.Tests` — 166 passed, 0 failed, 0 skipped.
- `dotnet test tests\Nexus.MitsubishiFx.Tests` — 3 passed, 0 failed, 0 skipped.
- `git diff --check -- .github\workflows\build-test.yml docs\ci.md` — no whitespace issues.
- `dotnet pack src\Nexus.Modbus --configuration Release --no-build --output artifacts\packages` — passed.

## Risks
- CI installs both .NET 8 and .NET 10 SDKs because the repository uses `Nexus.slnx`; first GitHub run may still reveal SDK/tooling assumptions.
- `dotnet test Nexus.slnx` in CI is broader than the focused tests run locally in this batch and may expose unrelated existing failures.
- PPI remains validated only by fake serial tests, not real S7-200 hardware.
- Mitsubishi MC3E ASCII/UDP and FX serial still need dedicated tests before promotion.

---

# WPF Modbus Packet Diagnostics Notes

## Decisions
- **Implemented the first WPF packet parser integration in `ModbusTcpPage`**: This page already owns its own Modbus TCP ViewModel and raw TX/RX event wiring, so the first integration stayed local instead of changing every protocol log viewer.
- **Kept raw logs visible**: `[TX]` and `[RX]` lines remain in the communication log. A parsed `[PKT]` summary is appended immediately after each packet.
- **Added structured packet history inside `ModbusTcpViewModel`**: JSONL export uses structured records rather than trying to reconstruct packets from displayed strings.
- **Scoped export to Modbus TCP**: The parser supports UDP, RTU, ASCII, and RTU-over-TCP, but the WPF integration currently targets the Modbus TCP page because that is the active WPF Modbus implementation.

## Architecture
- `Client_OnMessageSent` and `Client_OnMessageReceived` now call `AppendPacketLog(...)`.
- `AppendPacketLog(...)` parses the raw hex with `ModbusPacketParser.ParseTcp(...)`, appends a human-readable `[PKT]` summary, and stores a `PacketLogRecord`.
- `ExportLogCommand` writes the visible text log.
- `ExportPacketJsonlCommand` writes structured JSON Lines with protocol, direction, hex, transaction id, unit id, function code, address, quantity, byte count, data, validity, and parser error text.

## Verification
- `dotnet build src\Nexus.App` — passed; 2 warnings from existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged`.
- `dotnet test tests\Nexus.Modbus.Tests --filter "FullyQualifiedName~ModbusPacketParserTests"` — 13 passed, 0 failed, 0 skipped.

## Risks
- No WPF runtime/manual click test was performed in this turn; build verification confirms binding and command generation compile.
- JSONL export uses a per-page implementation. A shared packet recorder/export service should replace it once RTU/ASCII/UDP pages are brought into the same workflow.
- The log line cap is 500 visible lines, while every packet now produces two visible lines (`TX/RX` plus `PKT`), so long captures should rely on JSONL export rather than the UI list alone.

---

# Complete Scope Matrix Notes

## Decisions
- **Created complete scope matrices before broad feature work**: Modbus and Mitsubishi now have explicit "complete target vs current state" documents so protocol expansion is driven by gaps instead of guesswork.
- **Modbus next-code priority starts with FC22 and FC43/14**: FC22 is a practical factory feature for atomic bit-mask writes; FC43/14 is a high-value diagnostics feature for device identity.
- **Mitsubishi next priority is parity evidence, not more claims**: MC3E Binary and A1E are usable candidates; MC3E ASCII, MC3E UDP, and FX serial need tests and clear positioning.
- **UI remains part of the product goal**: WPF packet decode/export should become a shared recorder service after the first Modbus TCP integration.

## Outputs
- `docs/protocols/modbus/complete-scope.md`
- `docs/protocols/mitsubishi/complete-scope.md`
- Updated Modbus and Mitsubishi docs index pages to link the complete-scope documents.
- Updated `EXECUTION_PLAN.md` immediate queue. FC22 and Modbus UDP test stability are now complete; next Modbus priority moves to FC43/14 and diagnostics.

## Verification
- Documentation-only change so far in this note section; no build was required for the scope files.

## Risks
- Some extended Modbus function codes and Mitsubishi device areas require protocol manual or hardware confirmation before implementation.
- "Complete" must remain evidence-based: client code, server/simulator support, parser support, docs, tests, and real-device validation are separate gates.

---

# Modbus FC22 Mask Write Register Notes

## Decisions
- Added FC22 as an explicit public API named `MaskWriteRegister(address, andMask, orMask)` across TCP, UDP, RTU, ASCII, and RTU-over-TCP clients.
- Implemented TCP simulator support because it gives a hardware-free integration path for user testing and CI.
- Parser stores `AndMask` and `OrMask` as structured fields. Direction remains caller-provided for FC22 because request and normal response have the same PDU shape.
- Modbus TCP WPF packet logs and JSONL export now include FC22 masks so captures are understandable without manually decoding bytes.

## Outputs
- `src/Nexus.Modbus/ModbusTcpClient.cs`
- `src/Nexus.Modbus/ModbusUdpClient.cs`
- `src/Nexus.Modbus/ModbusRtuClient.cs`
- `src/Nexus.Modbus/ModbusAsciiClient.cs`
- `src/Nexus.Modbus/ModbusRtuOverTcpClient.cs`
- `src/Nexus.Modbus/ModbusTcpServer.cs`
- `src/Nexus.Modbus/ModbusPacketParser.cs`
- `src/Nexus.App/ViewModels/ModbusTcpViewModel.cs`
- `tests/Nexus.Modbus.Tests/ModbusPacketParserTests.cs`
- `tests/Nexus.Modbus.Tests/ModbusTcpTests.cs`

## Verification
- `dotnet test tests\Nexus.Modbus.Tests --filter "FullyQualifiedName~ModbusPacketParserTests|FullyQualifiedName~ModbusTcpTests"` passed: 15/15.
- `dotnet build src\Nexus.App` passed with existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged` unused-event warnings.
- `dotnet test tests\Nexus.Modbus.Tests -- xunit.parallelizeTestCollections=false` passed: 185/185.
- `dotnet test tests\Nexus.Modbus.Tests` passed after converting UDP server tests to dynamic ports: 186/186.

## Risks
- FC22 is now covered by parser plus TCP and UDP integration tests; RTU/ASCII/RTU-over-TCP have API coverage through shared PDU shape but still need per-transport edge-case tests.
- `ModbusVirtualServer` does not yet mirror FC22; `ModbusTcpServer` is the current integration simulator path.

---

# Modbus UDP Test Stability Notes

## Decisions
- Replaced fixed UDP test-server ports with dynamic OS-assigned ports from `ModbusUdpTestServer.Port`.
- Left no-server connect/disconnect event tests on inert remote port values because they do not bind a local server socket.
- Added FC22 handling to the UDP test server so the newly exposed UDP API has a real transport test.

## Outputs
- `tests/Nexus.Modbus.Tests/ModbusUdpTests.cs`
- Updated Modbus complete-scope notes to remove the fixed-port parallel-test gap.

## Verification
- `dotnet test tests\Nexus.Modbus.Tests` passed: 186/186.

## Risks
- Dynamic UDP ports remove fixed-port collisions, but UDP tests are still timing-sensitive by nature; timeout failures should be diagnosed with packet logs, not hidden by retry inflation.

---

# Modbus ASCII Over TCP Notes

## Decisions
- Added `ModbusAsciiOverTcpClient` as a first-class protocol entry instead of asking users to manually adapt TCP streams to `ModbusAsciiClient`.
- Reused the existing Modbus ASCII implementation through a TCP-backed `ISerialPort` adapter so ASCII frame building, LRC validation, address parsing, data conversion, FC22, and FC23 behavior stay consistent.
- Added a WPF navigation/page entry so the debugger starts matching the protocol-tree completeness users expect from HSL-like tools.

## Outputs
- `src/Nexus.Modbus/ModbusAsciiOverTcpClient.cs`
- `src/Nexus.Modbus/TcpStreamSerialPortAdapter.cs`
- `tests/Nexus.Modbus.Tests/ModbusAsciiOverTcpTests.cs`
- `src/Nexus.App/Views/ModbusAsciiOverTcpPage.xaml`
- `src/Nexus.App/Views/ModbusAsciiOverTcpPage.xaml.cs`
- Updated Modbus docs/readiness tables and WPF navigation.

## Verification
- `dotnet test tests\Nexus.Modbus.Tests` passed: 188/188.
- `dotnet build src\Nexus.App` passed with existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged` unused-event warnings.

## Risks
- The new WPF page is currently a visible protocol entry and layout baseline; it still needs binding to a shared Modbus read/write ViewModel before it is a full interactive debugger.
- ASCII-over-TCP has local integration coverage, but still needs gateway/device field validation before production claims.

---

# Modbus Network Page Binding Notes

## Decisions
- Promoted Modbus UDP, RTU-over-TCP, and ASCII-over-TCP WPF pages to functional connection/read/write/log panels.
- Used `ProtocolViewModelBase` for the network pages so common connect, read, write, validation, confirmation, and log behavior stays consistent with the rest of the app.
- Kept advanced packet decode/export on `ModbusTcpPage` for now; the next UI architecture step is a shared packet recorder instead of copying JSONL code into every page.

## Outputs
- `src/Nexus.App/ViewModels/ModbusUdpViewModel.cs`
- `src/Nexus.App/ViewModels/ModbusRtuOverTcpViewModel.cs`
- `src/Nexus.App/ViewModels/ModbusAsciiOverTcpViewModel.cs`
- `src/Nexus.App/Views/ModbusUdpPage.xaml`
- `src/Nexus.App/Views/ModbusUdpPage.xaml.cs`
- `src/Nexus.App/Views/ModbusRtuOverTcpPage.xaml`
- `src/Nexus.App/Views/ModbusRtuOverTcpPage.xaml.cs`
- Updated `ModbusAsciiOverTcpPage`, DI registration, navigation, and address validation.

## Verification
- `dotnet build src\Nexus.App` passed with existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged` unused-event warnings.

## Risks
- These pages are build-verified but not manually clicked in a live WPF runtime in this turn.
- Serial Modbus RTU and serial Modbus ASCII pages are still placeholders; they need a serial-port selection ViewModel before they can match HSL's serial demo experience.

---

# Modbus Server Tool Notes

## Decisions
- Upgraded the existing WPF Modbus simulator into a more usable TCP Server panel instead of adding a duplicate page.
- Added editable memory snapshots for holding registers, input registers, coils, and discrete inputs.
- Added request logging so incoming unit id, function code, and PDU are visible while clients test against the server.
- Added FC22 and FC23 support to the WPF simulator so it stays aligned with the Modbus client library and `ModbusTcpServer`.
- Removed the remaining WPF `.Wait()` usage in `MonitorViewModel.Dispose()` by adding a synchronous disposal path to `MonitorService`.

## Outputs
- `src/Nexus.App/Services/ModbusTcpSimulator.cs`
- `src/Nexus.App/ViewModels/SimulatorViewModel.cs`
- `src/Nexus.App/Views/SimulatorPage.xaml`
- `src/Nexus.App/Services/MonitorService.cs`
- `src/Nexus.App/ViewModels/MonitorViewModel.cs`
- Updated Modbus complete-scope server notes.

## Verification
- `dotnet build src\Nexus.App` passed with existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged` unused-event warnings.
- `rg -n "\.Wait\(|\.Result\b" src\Nexus.App` returned no matches.

## Risks
- The Server page is build-verified, but this turn did not perform manual UI click testing.
- The WPF simulator is TCP-only; UDP/RTU/ASCII server tooling remains future work.

---

# Modbus Serial Page Binding Notes

## Decisions
- Promoted Modbus RTU and Modbus ASCII WPF pages from placeholders to functional serial client panels.
- Added a shared serial Modbus ViewModel base for COM-port discovery, serial settings, connect/disconnect, TX/RX logs, and unified read/write behavior.
- Kept RTU defaults at 8 data bits, no parity; ASCII defaults at 7 data bits, even parity.
- Adjusted `ProtocolViewModelBase.ReadFmt` to avoid nullable access on generic numeric `Content`.

## Outputs
- `src/Nexus.App/ViewModels/ModbusSerialViewModelBase.cs`
- `src/Nexus.App/ViewModels/ModbusRtuViewModel.cs`
- `src/Nexus.App/ViewModels/ModbusAsciiViewModel.cs`
- `src/Nexus.App/Views/ModbusRtuPage.xaml`
- `src/Nexus.App/Views/ModbusRtuPage.xaml.cs`
- `src/Nexus.App/Views/ModbusAsciiPage.xaml`
- `src/Nexus.App/Views/ModbusAsciiPage.xaml.cs`
- Updated DI registration.

## Verification
- `dotnet build src\Nexus.App` passed with existing `ProtocolLogViewer.RelayCommand.CanExecuteChanged` unused-event warnings.

## Risks
- Serial pages are build-verified but not hardware-verified.
- Some USB/RS485 adapters require DTR/RTS behavior that must be validated on real hardware.

---

# Mitsubishi WPF Transport Matrix Notes

## Decisions
- Expanded the WPF Mitsubishi page from a single MC3E Binary TCP client into a transport selector covering MC3E Binary TCP, MC3E ASCII TCP, MC3E Binary UDP, MC3E ASCII UDP, and A1E Binary TCP.
- Reused the existing Mitsubishi clients instead of changing protocol frame logic in this pass; MC3E ASCII/UDP still need dedicated protocol tests before their status can be promoted.
- Added visible MC connection parameters for network number, PC number, destination station, wait-time unit, byte order, and A1E PLC number so the debugger is useful for real factory setups.
- Kept FX serial on the separate Mitsubishi FX page because it uses a different serial workflow and still needs package-level consolidation decisions.

## Outputs
- `src/Nexus.App/ViewModels/MitsubishiViewModel.cs`
- `src/Nexus.App/Views/MitsubishiPage.xaml`
- `src/Nexus.App/Services/AddressValidator.cs`
- `tests/Nexus.Mitsubishi.Tests/Mc3ETransportParityTests.cs`
- Updated Mitsubishi docs to record the new WPF coverage without claiming real-device validation.

## Verification
- `dotnet build src\Nexus.App` passed.
- `dotnet test tests\Nexus.Mitsubishi.Tests --filter "FullyQualifiedName~Mc3ETransportParityTests"` passed: 3/3.
- `dotnet test tests\Nexus.Mitsubishi.Tests` passed: 169/169.
- `dotnet test tests\Nexus.MitsubishiFx.Tests` passed: 3/3.

## Risks
- MC3E ASCII and MC3E UDP now have basic fake-server D-register tests, but still need broader area, bit, error, and real-device validation.
- The WPF page is build-verified, not manually clicked in this turn.
- Mitsubishi packet parser/export/replay is still future work.

---

# FC08/FC43 Modbus Diagnostics + Packet Recorder + MC3E ASCII Tests

## Decisions
- **FC08/FC43 in ModbusTcpServer**: Server now handles all FC08 diagnostic sub-functions (loopback, counters, clear) and FC43/14 Read Device Identification with configurable vendor/product/version properties.
- **PacketRecorderService as singleton**: Shared WPF service registered via DI. All Modbus ViewModels (TCP, UDP, RTU, ASCII, RTU-over-TCP, ASCII-over-TCP) now use it for packet recording + JSONL export instead of per-ViewModel implementations.
- **ProtocolViewModelBase optional injection**: Added a second constructor accepting PacketRecorderService + protocol/transport params. ViewModels without packet recording (non-Modbus protocols) continue using the default constructor.
- **MC3E ASCII fake server dynamic body**: Fixed the parity test server to read variable-length write bodies based on register count, not hardcoded 8 bytes.
- **ModbusPacketParser FC08/FC43**: Added MeiType, SubFunction, ReadDeviceIdLevel, ConformityLevel, MoreFollows, NextObjectId, ObjectCount fields to ModbusPacketInfo.

## Verification
- `dotnet build Nexus.slnx` — 0 errors
- `dotnet test tests/Nexus.Modbus.Tests` — 210 passed (was 188)
- `dotnet test tests/Nexus.Mitsubishi.Tests` — 232 passed (was 169)
- `dotnet build src/Nexus.App` — 0 errors

## Risks
- PacketRecorderService is singleton: all protocol tabs share the same recorder instance. Each tab clears the global recorder when clearing logs. This may surprise users with multiple tabs open.
- MC3E ASCII client uses polling-based DataAvailable read loop — fragile on slow networks. Not changed in this session.
