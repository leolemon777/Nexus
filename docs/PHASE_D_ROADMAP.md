# Phase D Roadmap — HSL-only Protocols

This document tracks protocols present in HslCommunication v12.2.0 but missing
or incomplete in Nexus. Phase D adds these to close the gap with HSL.

## Status (after Phase D PR #1)

| Protocol | HSL has | Nexus status | Action |
|---|---|---|---|
| DAM3601 analog module | ModbusRtu subclass | ✅ Added (Phase D-1) | Done — `src/Nexus.Dam3601/` |
| DcsNanJingAuto | ModbusTcp subclass | ✅ Added (Phase D-2) | Done — `src/Nexus.DcsNanJingAuto/` — Nanjing Automation DCS over Modbus TCP with connect handshake |
| Turck RFID (BLident) | ReaderNet + ReaderServer | ✅ Added (Phase D-3) | Done — `src/Nexus.Turck/` — BLident RFID reader client with 0xAA frame format, CRC-16 (poly 0x8408), ReadUid/ReadBlocks/WriteBlocks. 13 tests |
| Sick ICR RFID | SickIcrTcpServer (server only) | ✅ Added (Phase D-5) | Done — `src/Nexus.Sick/` — SickIcrBarcodeServer inherits DeviceServer, listens for barcode pushes from Sick/Hikvision/Keyence/Datalogic scanners. CleanBarcode strips STX/ETX/CR/LF. 10 tests |
| Toyota-Puc welder | ToyoPuc + ToyoPucServer | ✅ Added (Phase D-4) | Done — `src/Nexus.ToyoPuc/` — Toyota-Puc PLC computer-link protocol with 4-byte frame header, ReadWord/WriteWord commands (with/without PRG), address parsing (D/M/X/Y/S/R + prg= prefix), error-code mapping. 26 tests |
| ShineIn light source | ShineInLightSourceController (serial) | ✅ Added (Phase D-6) | Done — `src/Nexus.ShineIn/` — ShineIn light source controller over RS-232. /* */ frame format with XOR checksum, Read/Write channel params (color/brightness/mode), SetBrightness/TurnOn/TurnOff. 14 tests |
| Geniitek vibration | VibrationSensorClient | Missing | Add — Geniitek vibration sensor protocol |
| SAM ID card | SAMSerial + SAMTcpNet | Missing | Add — China 2nd-gen ID card SAM reader |

## What's NOT in scope (HSL has, but Nexus deliberately defers)

- **MQTT broker / WebSocket server / Redis / embedded file servers**: HSL ships
  these as part of its "all-in-one" framework, but Nexus is focused on protocol
  clients. MQTT/WebSocket/Redis already have first-class .NET libraries
  (MQTTnet, System.Net.WebSockets, StackExchange.Redis). Reimplementing them is
  low value.
- **HSL "Enthernet" framework** (NetSimplifyClient, NetPushServer, etc.): HSL's
  own RPC primitives. Not industrial protocols; out of scope for Nexus.

## DAM3601 implementation notes (this PR)

The DAM3601 implementation demonstrates the right pattern for adding Modbus-
derivative devices:

1. Create `src/Nexus.Dam3601/` referencing `Nexus.Modbus` (not Nexus.Core).
2. Class wraps `ModbusRtuClient` rather than inheriting it — composition over
   inheritance, keeps the Modbus API clean.
3. Exposes device-specific accessors: `ReadRawValue(channel)`,
   `ReadAllRawValues()`, `ReadRange()`, `ReadEngineeringValue()`.
4. The engineering-unit conversion (`ConvertToEngineering`) is a pure static
   function — trivially testable without any Modbus plumbing.
5. Configurable register base addresses (`ChannelValueRegister`,
   `ChannelRangeRegister`) accommodate vendor variants.

Future Modbus-derivative additions (DcsNanJingAuto, etc.) should follow this
template.

## Recommended next steps for Phase D

1. **DcsNanJingAuto** — copy DAM3601 pattern with the Nanjing-Auto register map.
   1-2 hours.
2. **Turck BLident** — TCP protocol with custom framing, needs HSL reference
   for frame format. 2-3 days.
3. **Toyota-Puc** — welder-specific, niche demand. 3-5 days.

Each addition should include:
- Device-specific register/command accessors
- Pure-function conversions (testable in isolation)
- Unit tests for parameter validation + conversion logic
- A VirtualServer if the protocol has unique framing
