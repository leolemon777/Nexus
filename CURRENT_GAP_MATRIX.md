# Nexus Current Gap Matrix

> ⚠️ **SUPERSEDED** — 当前进度与权威数据见 [`OVERTAKE_HSL_PLAN.md`](OVERTAKE_HSL_PLAN.md)（顶部"当前进度快照"）与 [`CLAUDE.md`](CLAUDE.md)。本文件保留作历史记录。

> Last calibrated: 2026-06-08
>
> Purpose: replace stale "Phase 0" assumptions with the current repository reality, then drive the next sprint toward a production-grade open-source release.

## Current Baseline

| Area | Current Signal | Notes |
|------|----------------|-------|
| Source projects | 40 under `src/` | Includes `Nexus.Core` and `Nexus.App`. |
| Test projects | 39 under `tests/` | Most protocols have a matching test project. |
| Protocol `NotImplementedException` debt | 0 found outside `Nexus.Core` protocol templates | WPF `ProtocolLogViewer.ConvertBack` placeholder has been removed. |
| `IBatchReadWrite` clients | 14 | Stronger than the old 7-client baseline. |
| `ISubscribeDevice` clients | 3 | Still narrow: Modbus TCP/UDP and OPC UA. |
| Virtual/server classes | 16 | Good testing foundation, but not yet a full virtual PLC ecosystem. |
| Packaging | Project metadata exists | No complete NuGet release workflow or package README pipeline yet. |
| WPF debugger | Broad protocol pages exist; Modbus TCP page now decodes packet summaries and exports JSONL | Needs recorder/replay, diagnostic bundle export, broader protocol integration, and stronger monitor workflows. |

See `PROTOCOL_READINESS.md` for the module-by-module readiness table that backs this summary.

See `EXECUTION_PLAN.md` for the active multi-workstream execution plan.

## Strategic Position

The old "clear 65 stubs" milestone is no longer the main blocker. The current blocker is production trust:

- Users need to know which protocols are production-ready, experimental, or test-only.
- Top protocols need consistent behavior around reconnect, timeout, logging, cancellation, batch reads, strings, byte order, and diagnostics.
- Core infrastructure exists, but major clients still need consistent adoption instead of one-off behavior.
- The WPF debugger can become a differentiator, but it needs field troubleshooting features rather than only basic read/write pages.
- Open-source success depends on NuGet, docs, examples, migration guides, and real-device validation.

## Top 5 Protocol Gap Matrix

| Protocol Family | Current Strength | Production Gap | Sprint Direction |
|-----------------|------------------|----------------|------------------|
| Modbus | Broad family coverage, TCP/UDP batch support, virtual server, many tests, offline packet parser | Need current feature audit against FC coverage, server callbacks, long-run and gateway scenarios | Make it the reference implementation for docs, diagnostics, packaging, and benchmark style. |
| Siemens | S7 is deep, FetchWrite/PPI present, virtual PLC exists | Need PPI status audit, S7 real-device matrix, S7 string/WString docs, PLC setting guide, consistent reconnect/heartbeat story | Stabilize S7/PPI and publish the best migration story from HSL. |
| Mitsubishi | MC3E Binary/ASCII/UDP, A1E, FX serial are present; support matrix now exists | Need dedicated MC3E ASCII/UDP tests, FX serial production scope, address coverage proof by model | Make MC family consistent and document address/device-code support. |
| Omron | FINS TCP/UDP and HostLink have batch support and virtual servers | Need HostLink serial audit, FINS routing docs, node/network configuration guide, real-device tests | Make FINS/HostLink field setup predictable. |
| AllenBradley | CIP and PCCC exist, CIP has batch support and virtual server | Need fragmented tag/array/UDT audit, PCCC/MicroLogix coverage proof, error diagnostics | Focus on common ControlLogix/CompactLogix tag workflows first. |

## Core Infrastructure Gap Matrix

| Capability | Current Signal | Gap |
|------------|----------------|-----|
| Auto reconnect | `AutoReconnectGuard` exists | Needs adoption pattern, tests with real protocol clients, and WPF configuration. |
| Heartbeat | `HeartbeatGuard` exists | Needs per-protocol heartbeat strategy and documented defaults. |
| Connection pool | `ConnectionPool<T>` exists | Needs selected protocol integration and ownership/lifetime guidance. |
| Address parameters | `AddressContext` exists | Needs adoption in main protocol address parsers and examples like `s=2;e=cdab;D100`. |
| Struct mapping | `StructConverter` exists | Needs protocol-level examples and tests with byte order. |
| Logging | `ILogger` and message logger abstractions exist | Needs packet recorder/parser pipeline and consistent TX/RX events across clients. |
| Async | Base classes support async paths | Needs audit for `Task.Run` wrappers and protocol-specific true async where it matters. |

## WPF Debugger Gap Matrix

| Workflow | Current Signal | Needed |
|----------|----------------|--------|
| Basic read/write | Many protocol pages | More consistent reusable page components. |
| Logs | `ProtocolLogViewer` exists; Modbus offline packet parser exists; Modbus TCP page displays parsed packets and exports JSONL | Generalize packet export/display beyond the Modbus TCP page. |
| Monitor | `MonitorPage`, chart, services exist | Multi-device workflows, saved tag lists, stable long-run polling, trend export. |
| Simulators | Modbus/S7 app services exist | Virtual PLC manager with presets and scenario save/load. |
| Field diagnostics | Chinese diagnostics exists | Add diagnostic bundle export: app log, TX/RX log, connection settings, protocol version, failure timeline. |

## Sprint 0: Calibration And Production Gate

Goal: establish the standards that every later protocol hardening task must satisfy.

1. Create a protocol readiness table.
   - Status values: `Production Candidate`, `Usable`, `Experimental`, `Test Utility`, `Deprecated/Needs Audit`.
   - Record supported transports, address parser status, batch support, subscription support, virtual server, and test count.
   - Current output: `PROTOCOL_READINESS.md`.

2. Define the Top 5 production gate.
   - Must have documented address formats.
   - Must have TX/RX events and error diagnostics.
   - Must have at least one integration test path or virtual server test path.
   - Must have clear reconnect/heartbeat guidance.
   - Must have at least one quickstart example.

3. Make Modbus the reference package.
   - Use it to define README shape, package metadata, sample style, diagnostics style, and benchmark style.

4. Make WPF diagnostics the reference user experience.
   - Fix the `ProtocolLogViewer` placeholder.
   - Generalize packet log export format requirements.
   - Extend Modbus packet parser display from Modbus TCP to other Modbus transports.

5. Prepare open-source release assets.
   - NuGet package plan.
   - HSL migration guide outline.
   - Real-device validation matrix template.
   - Contribution rules for legal protocol references.
   - Current outputs: `HSL_MIGRATION_GUIDE.md`, `REAL_DEVICE_VALIDATION.md`.

## Sprint 0 Verification

No protocol implementation is required for this sprint. Verification should be:

```bash
dotnet build Nexus.slnx
dotnet test tests/Nexus.Core.Tests
dotnet test tests/Nexus.Modbus.Tests
```

Run narrower tests when only docs change; run the commands above once Sprint 0 begins touching code.

## Sprint 0 Added Release Infrastructure

- `CONTRIBUTING_PROTOCOLS.md` defines clean-room implementation, testing, docs, and production promotion rules.
- `.github/workflows/build-test.yml` provides the first Windows CI build/test/pack workflow.
- `docs/ci.md` documents the CI command strategy. Modbus UDP tests now use dynamic server ports, while full-solution CI remains conservative until every protocol integration test is audited.

## Immediate Risks

- Roadmap documents are inconsistent with current code. Treat this file as the active planning baseline until older documents are updated.
- `Nexus.App` has no test project. WPF changes need build verification and manual/runtime checks.
- Some clients implement `IReadWriteDevice` directly instead of using base classes. That is acceptable short term, but production gates must verify consistent behavior.
- Real-device validation is currently not represented in source control. Without that matrix, "production-ready" is only an internal claim.
