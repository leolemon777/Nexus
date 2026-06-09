# Nexus Execution Plan To Surpass HSL

> Workspace: `E:\Desktop\Nexus2.0`
>
> Last updated: 2026-06-08
>
> Goal: make Nexus a production-grade, MIT-licensed, open-source industrial communication library that can realistically replace and then exceed HSL in protocol depth, tooling, documentation, testing, and ecosystem value.

## Operating Principles

1. Do not claim production readiness without evidence.
2. Make the top 5 protocol families excellent before chasing protocol count.
3. Every release-facing feature needs docs, tests, diagnostics, and migration guidance.
4. Prefer small, surgical changes that preserve existing user edits.
5. Keep all generated project artifacts in `E:\Desktop\Nexus2.0`.
6. Avoid copying HSL code. Nexus must remain clean-room and open-source safe.

## Current Planning Baseline

Use these files as the active planning set:

| File | Purpose |
|------|---------|
| `CURRENT_GAP_MATRIX.md` | Current strategic gap summary. |
| `PROTOCOL_READINESS.md` | Module-by-module readiness status. |
| `REAL_DEVICE_VALIDATION.md` | Real-device evidence matrix and safety rules. |
| `HSL_MIGRATION_GUIDE.md` | Migration story for HSL users. |
| `docs/protocols/modbus/*` | First reference protocol documentation set. |
| `src/Nexus.Modbus/README.md` | First package README draft. |

Older plans such as `OVERTAKE_HSL_PLAN.md` and `PLAN.md` are useful historical direction, but current execution should be driven by this file plus the files above.

## Workstream Overview

| Workstream | Objective | Can Run In Parallel | Primary Outputs |
|------------|-----------|---------------------|-----------------|
| WS0 Planning and Gates | Keep readiness, evidence, and milestone criteria accurate | Yes | readiness tables, validation templates, release gates |
| WS1 Modbus Reference Package | Make Modbus the release template | Yes | package README, docs, parser, tests, benchmarks |
| WS2 Top 5 Protocol Hardening | Production-gate Siemens/Mitsubishi/Omron/AllenBradley | Yes by protocol | docs, tests, virtual-server paths, diagnostics |
| WS3 Core Infrastructure Adoption | Make reconnect/heartbeat/pool/address/struct features actually used | Partially | adoption patterns, tests, examples |
| WS4 WPF Diagnostic Tooling | Turn the debugger into a field troubleshooting product | Yes | packet viewer, parser, recorder, diagnostic export |
| WS5 Packaging and CI | Prepare public open-source release | Yes | NuGet pipeline, package metadata, CI checks |
| WS6 Real Device Validation | Build trust through real hardware evidence | Yes, hardware-dependent | validation rows, logs, test reports |
| WS7 Ecosystem Differentiators | Add things HSL does not offer well | Later parallel | virtual PLC, data acquisition, gateway, Web API |

## Milestone 0: Sprint 0 Baseline

Goal: stop planning from drifting and define what "ready" means.

Status: in progress.

Deliverables:

| Task | Status | Output |
|------|--------|--------|
| Current gap matrix | Done | `CURRENT_GAP_MATRIX.md` |
| Protocol readiness table | Done | `PROTOCOL_READINESS.md` |
| Real-device validation template | Done | `REAL_DEVICE_VALIDATION.md` |
| HSL migration guide outline | Done | `HSL_MIGRATION_GUIDE.md` |
| Modbus reference docs | Done | `docs/protocols/modbus/*` |
| Modbus package README draft | Done | `src/Nexus.Modbus/README.md` |
| NuGet release checklist | Done | `RELEASE_CHECKLIST.md` |
| Contribution/legal protocol guide | Done | `CONTRIBUTING_PROTOCOLS.md` |

Verification:

```powershell
dotnet test tests\Nexus.Modbus.Tests
```

Current Modbus result recorded: 186 passed, 0 failed, 0 skipped with the normal `dotnet test tests\Nexus.Modbus.Tests` command.

## Milestone 1: Modbus Reference Package

Goal: make `Nexus.Modbus` the model every other protocol package follows.

Tasks:

| Priority | Task | Files/Area | Verification |
|----------|------|------------|--------------|
| P0 | Wire package README into `.csproj` | `src/Nexus.Modbus/Nexus.Modbus.csproj` | Done: `dotnet pack src\Nexus.Modbus` |
| P0 | Create Modbus packet parser interface and model | `src/Nexus.Modbus/ModbusPacketParser.cs` | Done: parser tests |
| P0 | Implement Modbus TCP/UDP/RTU/ASCII/RTU-over-TCP packet parser | `src/Nexus.Modbus` | Done: parser tests with known TX/RX frames |
| P0 | Add packet parser docs | `docs/protocols/modbus/packet-logging.md` | Done: doc review |
| P0 | Define complete Modbus scope | `docs/protocols/modbus/complete-scope.md` | Done: scope review |
| P1 | Add benchmark plan | `docs/protocols/modbus/performance.md` | no build required |
| P1 | Add long-run test plan | `docs/protocols/modbus/long-run.md` | no build required |
| P1 | Audit address parser/client parser consistency | `src/Nexus.Modbus/ModbusAddress.cs`, clients | focused tests |
| P2 | Add gateway/DTU notes | docs | doc review |

Exit criteria:

- `Nexus.Modbus` can be packed locally.
- Packet parser decodes at least one request and one response for FC01/03/05/06/15/16/23.
- Docs explain address format, function codes, byte order, logging, and troubleshooting.
- At least one real-device validation row is planned or recorded.

## Milestone 2: Top 5 Protocol Production Gates

Goal: promote top protocol families from "usable" to "production candidate" with evidence.

### Siemens

Tasks:

| Priority | Task | Files/Area |
|----------|------|------------|
| P0 | Audit Siemens PPI current user changes | Done: `docs/protocols/siemens/ppi-audit.md` |
| P0 | Run Siemens focused tests | Done: `dotnet test tests\Nexus.Siemens.Tests --filter "FullyQualifiedName~SiemensPpi"` |
| P0 | Add Siemens setup docs skeleton; deeper setup details pending | Done: `docs/protocols/siemens` |
| P1 | Document S7 String/WString and PLC settings | Done: in s7.md and HSL migration chapter |
| P1 | Define S7 reconnect/heartbeat guidance | Done: `docs/protocols/siemens/reconnect-heartbeat.md` |
| P2 | Add real-device validation target rows | Done: `REAL_DEVICE_VALIDATION.md` Siemens S7 target detail |

### Mitsubishi

Tasks:

| Priority | Task | Files/Area |
|----------|------|------------|
| P0 | Build MC Binary/ASCII/UDP/A1E/FX support matrix | Done: `docs/protocols/mitsubishi` |
| P0 | Audit address/device-code coverage | Done: documented in Mitsubishi support matrix |
| P0 | Define complete Mitsubishi scope | Done: `docs/protocols/mitsubishi/complete-scope.md` |
| P1 | Add MC3E ASCII/UDP frame tests | Done: 257 tests in `tests/Nexus.Mitsubishi.Tests` |
| P1 | Consolidate FX serial into single package | Done: `Nexus.MitsubishiFx` merged, FxLinkClient added |
| P1 | Document virtual-server scenarios | Done: in HSL migration chapter |

### Omron

Tasks:

| Priority | Task | Files/Area |
|----------|------|------------|
| P0 | Document FINS TCP/UDP network/node settings | Done: `docs/protocols/omron/fins-setup.md` |
| P0 | Audit HostLink TCP/Serial coverage | Done: `docs/protocols/omron/hostlink-coverage.md` |
| P1 | Add setup and routing examples | Done: in fins-setup.md |
| P1 | Add diagnostic examples for end codes | Done: `docs/protocols/omron/troubleshooting.md` |
| P2 | Add real-device validation target rows | Done: `REAL_DEVICE_VALIDATION.md` Omron FINS TCP target detail |

### AllenBradley

Tasks:

| Priority | Task | Files/Area |
|----------|------|------------|
| P0 | Document CIP path, slot, and tag syntax | Done: `docs/protocols/allenbradley/cip-tag-syntax.md` |
| P0 | Audit fragmented tag and string tag workflows | Done: documented in cip-tag-syntax.md |
| P1 | Define UDT/array scope honestly | Done: `docs/protocols/allenbradley/udt-arrays.md` |
| P1 | Document PCCC/MicroLogix coverage | Done: `docs/protocols/allenbradley/pccc-coverage.md` |
| P2 | Add real-device validation target rows | Done: `REAL_DEVICE_VALIDATION.md` AB CIP target detail |

Exit criteria for each protocol:

- Protocol docs exist.
- Read/write examples exist.
- Supported address/tag syntax is documented.
- Existing tests pass.
- Gaps are clearly listed.
- At least one real-device target row exists.

## Milestone 3: Core Infrastructure Adoption

Goal: make core features consistent across protocol clients.

Tasks:

| Priority | Task | Files/Area | Notes |
|----------|------|------------|-------|
| P0 | Define reconnect/heartbeat adoption pattern | Done: `docs/core/reconnect-heartbeat.md` | AutoReconnectGuard + HeartbeatGuard docs with S7-specific guidance. |
| P0 | Add example using `AutoReconnectGuard` | Done: in reconnect-heartbeat.md | Modbus + S7 + combined examples. |
| P1 | Add connection-pool usage guidance | Done: `docs/core/connection-pool.md` | Lifecycle, thread safety, multi-PLC example. |
| P1 | Add `AddressContext` examples | Done: `docs/core/address-context.md` | Station override, byte order override. |
| P1 | Add struct mapping examples | Done: `docs/core/struct-mapping.md` | StructConverter with byte order, nested structs, MotorBlock example. |
| P2 | Audit direct `IReadWriteDevice` implementations | Done: `docs/core/ireadwrite-device-audit.md` | 13 clients audited; 7 should migrate, 6 acceptable as-is. |

Exit criteria:

- Main protocols have documented reconnect and timeout behavior.
- Core helper classes are not just present; they are shown in real examples.

## Milestone 4: WPF Field Diagnostics

Goal: make `Nexus.App` useful for field troubleshooting, not just manual reads.

Tasks:

| Priority | Task | Files/Area |
|----------|------|------------|
| P0 | Fix `ProtocolLogViewer.ConvertBack` placeholder (done) | `src/Nexus.App/Controls/ProtocolLogViewer.xaml.cs` |
| P0 | Define packet log JSONL export | Done for Modbus TCP page; shared export service still pending |
| P0 | Add Modbus packet parsing display | Done for Modbus TCP page; RTU/ASCII/UDP pages still pending |
| P1 | Add diagnostic bundle export | app log, TX/RX log, settings |
| P1 | Improve multi-address monitor workflows | `MonitorPage`, `MonitorViewModel` |
| P2 | Add virtual PLC manager page | simulator services |

Verification:

```powershell
dotnet build src\Nexus.App
```

Manual/runtime checks are required for WPF flows.

## Milestone 5: Packaging, CI, And Release

Goal: make public installation boring and reliable.

Tasks:

| Priority | Task | Output |
|----------|------|--------|
| P0 | Create `RELEASE_CHECKLIST.md` | release checklist |
| P0 | Add package README metadata for Modbus | `.csproj` |
| P0 | Add GitHub Actions build/test | Done: `.github/workflows/build-test.yml` |
| P1 | Add NuGet pack validation | Done for Modbus artifact pack in CI |
| P1 | Add package metadata audit | script/docs |
| P1 | Add docs index | `docs/index.md` |
| P2 | Add DocFX or static docs plan | docs |

Verification:

```powershell
dotnet build Nexus.slnx
dotnet test Nexus.slnx
dotnet pack src\Nexus.Modbus
```

## Milestone 6: Real Device Validation

Goal: turn claims into evidence.

Tasks:

| Priority | Task | Output |
|----------|------|--------|
| P0 | Identify first five available devices | update `REAL_DEVICE_VALIDATION.md` |
| P0 | Create per-device safe scratch-address plan | validation rows |
| P0 | Capture TX/RX logs during validation | evidence files under workspace |
| P1 | Run 1-hour polling validation | validation row |
| P1 | Run reconnect validation | validation row |
| P2 | Publish known limitations | docs |

Target devices:

- Modbus TCP device or gateway.
- Siemens S7-1200 or S7-1500.
- Mitsubishi Q/L/FX5U.
- Omron CJ/CP/NJ/NX.
- AllenBradley ControlLogix or CompactLogix.

## Milestone 7: B-Tier Protocol Promotion

Goal: move strong secondary modules toward usability and production candidates.

Priority order:

1. `Nexus.Yaskawa`
2. `Nexus.Yokogawa`
3. `Nexus.Inovance`
4. `Nexus.Fatek`
5. `Nexus.Bacnet`
6. `Nexus.Iec104`
7. `Nexus.Redis`
8. `Nexus.Mqtt`

Tasks per module:

- Feature support matrix.
- Address or topic syntax docs.
- Focused tests for missing areas.
- Real-device or interoperability validation.
- Package README.

## Milestone 8: Differentiators Beyond HSL

Goal: build capabilities that make Nexus more than a clone or replacement.

Tasks:

| Area | Deliverable |
|------|-------------|
| Virtual PLC ecosystem | Scenario-based virtual PLC memory models and presets. |
| Data acquisition engine | Multi-device polling, data sinks, change events. |
| Protocol gateway | Modbus/S7 to MQTT, OPC UA, Redis mappings. |
| Packet recorder/replay | JSONL capture, replay against clients/servers, anomaly detection. |
| Web API | ASP.NET integration for remote device read/write/monitor. |
| WPF diagnostics | End-to-end packet decode and diagnostic bundle export. |

## Parallel Execution Batches

### Batch A: Release Foundation

Can run together:

1. Modbus package metadata and docs.
2. Release checklist.
3. Real-device validation template updates.
4. HSL migration guide expansion.

Avoid conflicts:

- Do not edit `src/Nexus.Modbus/Nexus.Modbus.csproj` from two tasks at once.

### Batch B: Top 5 Audits

Can run together by protocol:

1. Siemens audit.
2. Mitsubishi audit.
3. Omron audit.
4. AllenBradley audit.

Avoid conflicts:

- Keep each protocol owner inside its own `src/Nexus.{Protocol}` and `tests/Nexus.{Protocol}.Tests`.

### Batch C: WPF Diagnostics

Can run with protocol docs, but not with another WPF UI task touching the same files.

1. `ProtocolLogViewer` cleanup.
2. Packet export service design.
3. Modbus parser display.

### Batch D: CI And Packaging

Can run after Modbus package metadata is stable.

1. Build/test workflow.
2. Pack workflow.
3. Metadata audit.

## Immediate Next 10 Tasks

| Order | Task | Type | Verification |
|-------|------|------|--------------|
| 1 | Add Modbus FC43/14 Read Device Identification | ~~code/docs/tests~~ ✅ | focused Modbus tests |
| 2 | Add Modbus FC08 diagnostics model and loopback tests | ~~code/docs/tests~~ ✅ | focused Modbus tests |
| 3 | Extract shared WPF packet recorder/export service | ~~WPF~~ ✅ | `dotnet build src\Nexus.App` |
| 4 | Extend WPF packet decode to Modbus RTU/ASCII/UDP/RTU-over-TCP pages | ~~WPF~~ ✅ | build + manual runtime check |
| 5 | Add Mitsubishi MC3E ASCII frame tests | ~~tests/docs~~ ✅ | `dotnet test tests\Nexus.Mitsubishi.Tests` |
| 6 | Add Mitsubishi MC3E UDP tests | ~~tests/docs~~ ✅ | `dotnet test tests\Nexus.Mitsubishi.Tests` |
| 7 | Decide Mitsubishi FX serial consolidation path | ~~docs/code review~~ ✅ | consolidated into Nexus.Mitsubishi |
| 8 | Add Mitsubishi HSL migration chapter | ~~docs~~ ✅ | review |
| 9 | Add Modbus/Mitsubishi real-device target rows | ~~validation docs~~ ✅ | review |
| 10 | Add Modbus benchmark and long-run notes | ~~docs/tests~~ ✅ | `performance.md` + `long-run.md` |

## Verification Command Bank

```powershell
dotnet build Nexus.slnx
dotnet test Nexus.slnx
dotnet test tests\Nexus.Modbus.Tests
dotnet test tests\Nexus.Siemens.Tests
dotnet test tests\Nexus.Mitsubishi.Tests
dotnet test tests\Nexus.Omron.Tests
dotnet test tests\Nexus.AllenBradley.Tests
dotnet build src\Nexus.App
dotnet pack src\Nexus.Modbus
```

## Definition Of "Surpass HSL"

Nexus surpasses HSL when all of these are true:

1. Top 5 protocol families are production candidates with real-device evidence.
2. Public NuGet packages install cleanly.
3. Docs and examples let a user run a first PLC read in under 5 minutes.
4. WPF debugger can capture, decode, export, and package field diagnostics.
5. Virtual servers and integration tests are available for key protocols.
6. Long-run stability and reconnect behavior are measured.
7. Open-source legal cleanliness is documented and maintained.
8. Nexus has at least one differentiator HSL does not provide: virtual PLC scenarios, packet replay, data acquisition, or protocol gateway.
