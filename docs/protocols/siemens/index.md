# Siemens Protocols

Status: draft. This page is based on `src/Nexus.Siemens` source and `tests/Nexus.Siemens.Tests`. It does not claim real-device validation.

Nexus currently keeps Siemens support in one package with three protocol lines:

| Protocol | Client | Current documentation status | Source/test signal |
| --- | --- | --- | --- |
| S7 Communication | `SiemensS7Client` | Primary Siemens path. Document first and validate first. | Deep source coverage, `IBatchReadWrite`, `SiemensS7VirtualPlc`, many virtual PLC tests. |
| PPI | `SiemensPpiClient` | Needs audit before production guidance. | Serial client and fake serial unit tests exist; current implementation/user changes must be reviewed before final docs. |
| Fetch/Write | `SiemensFetchWriteClient` | Usable draft for specific PLC-side Fetch/Write configuration. | Virtual server and integration-style tests exist. |

## Client Selection

Use `SiemensS7Client` when:

- The PLC supports S7 Communication over TCP.
- You need S7-200, S7-200 Smart, S7-300, S7-400, S7-1200, or S7-1500 addressing.
- You need batch read/write, large block splitting, S7 String/WString handling, or PLC control commands.

Use `SiemensFetchWriteClient` when:

- The PLC/project exposes Fetch/Write access and the target workflow is plain byte-area read/write.
- You want to test against `SiemensFetchWriteVirtualServer`.
- You can confirm PLC-side Fetch/Write settings separately. This has not been documented as real-device validated yet.

Use `SiemensPpiClient` only after audit when:

- The target is S7-200 class serial PPI.
- You provide an `ISerialPort` implementation.
- The current PPI framing/address behavior is reviewed against hardware or protocol traces.

## Current Siemens Package Signals

Confirmed from source:

- `SiemensS7Client` inherits `TcpDeviceBase` and implements `IBatchReadWrite`.
- `SiemensS7Client` supports model selection through `SiemensPLCS`.
- S7 address parsing supports DB, I/E, Q/A, M, and V areas.
- S7 exposes `ByteOrder`, `StringEncoding`, `Rack`, `Slot`, `ConnectionType`, `LocalTSAP`, `DestTSAP`, and `MaxPduSize`.
- S7 exposes special helpers for `ReadLarge`, `WriteLarge`, `ReadBools`, `WriteBools`, `ReadS7String`, `WriteS7String`, `ReadWString`, `WriteWString`, `ReadOrderNumber`, `HotStart`, `ColdStart`, and `Stop`.
- `SiemensS7VirtualPlc` exists for offline and integration-style tests.
- `SiemensFetchWriteClient` supports I, Q, M, DB, T, and C address parsing and has `SiemensFetchWriteVirtualServer`.
- `SiemensPpiClient` is serial based and accepts `ISerialPort`.

Not confirmed yet:

- Real-device behavior on S7-200/200 Smart/300/400/1200/1500.
- Siemens security/project settings needed for each PLC family.
- PPI timing, BCC/framing edge cases, and serial adapter behavior on physical hardware.
- Fetch/Write PLC-side configuration recipes and model-specific restrictions.

## Pages

- [S7 Communication](s7.md)
- [PPI](ppi.md)
- [PPI Audit](ppi-audit.md)
- [Fetch/Write](fetch-write.md)
- [Reconnect and Heartbeat](reconnect-heartbeat.md)
- [Troubleshooting](troubleshooting.md)

## Real Device Validation Gate

Before marking Siemens as production candidate, collect:

- PLC model, firmware, project settings, CPU protection/security settings, rack/slot, TSAP if overridden, and network/serial topology.
- Read/write evidence for Bool, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, String, Bytes.
- Batch read/write evidence for S7.
- String evidence for raw string, S7 String, and WString.
- Long-run stability evidence for reconnect and repeated polling.
- Packet logs or diagnostic traces for failed cases.

## Draft TODO

- Add exact NuGet package installation once package metadata is finalized.
- Add real-device screenshots or packet captures after hardware validation.
- Add PLC setup recipes for TIA Portal, STEP 7, MicroWIN, and SMART software after verification.
- Decide whether Siemens docs should include WPF debugger flows once the debugger feature set is stable.
