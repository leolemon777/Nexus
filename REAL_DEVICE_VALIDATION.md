# Real Device Validation Matrix

> Last calibrated: 2026-06-08.
>
> Rule: a protocol is not production-ready until at least one real-device validation row exists or the limitation is explicitly documented.

## Validation Levels

| Level | Meaning |
|-------|---------|
| L0 Source Only | Code and unit tests exist, but no integration or real-device evidence is recorded. |
| L1 Virtual Server | Integration tests pass against a Nexus virtual server or local protocol simulator. |
| L2 Lab Device | Tested against a real device in a controlled environment. |
| L3 Field Device | Tested in a real production or commissioning environment. |
| L4 Long Run | Field or lab device survived long-run validation, reconnect scenarios, and repeated reads/writes. |

## Required Evidence

Each validation row should include:

- Protocol module and client class.
- Device vendor, model, firmware, and communication module if applicable.
- Transport: TCP, UDP, serial, RTU-over-TCP, or other.
- Network/serial settings.
- Tested operations.
- Result summary.
- Known limitations.
- Tester and date.
- Test evidence path if available: logs, packet captures, screenshots, or exported diagnostic bundles.

## Top 5 Target Matrix

| Protocol | Client | Minimum Target | Current Level | Required Device Evidence |
|----------|--------|----------------|---------------|--------------------------|
| Modbus TCP | `ModbusTcpClient` | L4 | L1 | Any industrial Modbus TCP PLC/gateway; read/write coils and holding registers; reconnect test. |
| Modbus RTU | `ModbusRtuClient` | L3 | L0/L1 pending serial fixture | RS485 device or Modbus simulator hardware; CRC failure and timeout behavior. |
| Siemens S7 | `SiemensS7Client` | L4 | L1 | S7-1200 or S7-1500; DB/I/Q/M read/write; S7String; reconnect. |
| Siemens PPI | `SiemensPpiClient` | L3 | Needs audit | S7-200 or S7-200 SMART over serial/PPI adapter. |
| Mitsubishi MC3E | `Mc3EBinaryClient` | L4 | L1 | Q/L/FX5U or compatible simulator; D/M/X/Y reads/writes; remote run/stop only if safe. |
| Omron FINS TCP | `FinsTcpClient` | L4 | L1 | CJ/CP/NJ/NX PLC; DM/CIO reads/writes; routing/node setup. |
| AllenBradley CIP | `AllenBradleyCipClient` | L4 | L1 | ControlLogix or CompactLogix; DINT/REAL/BOOL/String tags; fragmented tag read. |

## Validation Records

Add real records below. Keep newest first.

| Date | Level | Protocol | Client | Device | Firmware | Transport | Settings | Operations | Result | Evidence | Notes |
|------|-------|----------|--------|--------|----------|-----------|----------|------------|--------|----------|-------|
| TBD | L0 | Modbus TCP | `ModbusTcpClient` | TBD | TBD | TCP | TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Siemens S7 | `SiemensS7Client` | TBD | TBD | TCP | Rack/slot TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Mitsubishi MC3E | `Mc3EBinaryClient` | TBD | TBD | TCP | TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Omron FINS TCP | `FinsTcpClient` | TBD | TBD | TCP | Network/node TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | AllenBradley CIP | `AllenBradleyCipClient` | TBD | TBD | TCP | Slot/path TBD | TBD | Pending | TBD | First real-device validation needed. |

## Standard Test Script

For each device, run the narrowest safe subset first:

1. Connect and disconnect 10 times.
2. Read a known read-only value.
3. Read and write a safe scratch register/tag/address.
4. Read multiple adjacent values.
5. Trigger a bad address and confirm diagnostic quality.
6. Disconnect network/serial path and confirm recovery behavior.
7. Run a 1-hour polling loop before attempting long-run validation.

## Safety Rules

- Never write production control addresses during validation.
- PLC control commands such as run/stop/reset require explicit test-bench approval.
- Record exact device settings so failures can be reproduced.
- If a device requires unsafe plant access, mark the row as pending rather than improvising.
