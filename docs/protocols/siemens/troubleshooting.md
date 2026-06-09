# Siemens Troubleshooting

Status: draft. This page lists diagnostics to collect and common checks. It is not a substitute for real-device validation.

## First Checks

- Confirm the selected client matches the PLC/project: S7 Communication, PPI, or Fetch/Write.
- Confirm IP/port for TCP clients or serial settings for PPI.
- Confirm PLC model, firmware, rack/slot, TSAP, and project security settings.
- Confirm the address area and data size match the PLC variable.
- Check every `OperateResult.IsSuccess` before using `Content`.

## S7 Connection Fails

Check:

- PLC family passed to `SiemensS7Client`.
- Default slot: S7-1200/1500 use slot 1; S7-300/400 use slot 2 in current source.
- Custom `Rack`, `Slot`, `LocalTSAP`, `DestTSAP`, and `ConnectionType`.
- CPU protection/security settings and whether the DB is accessible.
- Firewall and port 102 reachability.

Collect:

- PLC model and firmware.
- Project software and access/security settings.
- Constructor arguments and changed properties.
- TX/RX logs if available.
- Error message and error code from `OperateResult`.

## S7 Reads Wrong Values

Check:

- Address spelling: `DB1.DBW0`, `DB1.DBD10`, `MW10`, `MD20`, `IW0`, `QW0`, `VW100`.
- Byte order via `ByteOrder`.
- Whether the address is byte, word, dword, or bit.
- Whether the PLC DB uses optimized layout or a layout that changes offsets.
- String type: raw string, S7 String, and WString are different formats.

Collect:

- Expected PLC variable declaration.
- Address used in Nexus.
- Raw bytes read by `ReadBytes` or `RandomRead`.
- Expected value from engineering software.

## Batch Operations Fail

Check:

- Number of addresses and whether any individual address is invalid.
- Mixed DB areas and address sizes.
- PDU size negotiation if the target is not a virtual PLC.
- Whether the operation should use `BatchRead`, `RandomRead`, or individual typed reads.

TODO:

- Add returned object type mapping for `BatchRead`.
- Add examples for partial failure handling once the public error contract is finalized.

## String Problems

Check:

- `ReadString(address, length)` is raw fixed-length data.
- `ReadStringEncoded` uses `StringEncoding`.
- `ReadS7String` expects the Siemens String length header.
- `ReadWString` expects WString metadata and Big Endian Unicode payload.

Collect:

- PLC declaration such as `String[20]` or `WString[20]`.
- Start address and expected max/current length.
- Raw bytes around the header and payload.

## Fetch/Write Fails

Check:

- PLC-side Fetch/Write access is enabled.
- Address format is one of `I100`, `Q100`, `M100`, `DB1.100`, `T100`, or `C100`.
- DB number is within the current source parser limit.
- Error code from response byte 8 if available.

Collect:

- PLC-side Fetch/Write configuration.
- Full command/response bytes.
- Whether the same address works through S7 Communication.

## PPI Fails

Check:

- `ISerialPort` adapter implementation.
- COM settings, station addresses, and cable/adapter.
- `MasterAddress` and `SlaveAddress`.
- PPI response BCC and frame length.

Collect:

- COM port, baud rate, parity, data bits, stop bits, and adapter model.
- Station address configuration.
- Request/response bytes.
- Timeout behavior and retry count if implemented by the adapter.

## Real Device Bug Report Template

Include:

- Nexus version or commit.
- Client type and constructor arguments.
- PLC model, firmware, project software, and project settings.
- Address, expected value, actual value, and data type.
- Full `OperateResult.Message` and `ErrorCode`.
- Packet log or serial trace.
- Whether the same operation passes against the virtual server.

## Open Troubleshooting TODO

- Add S7 error-code table.
- Add Fetch/Write response-code table.
- Add PPI frame diagrams after audit.
- Add WPF debugger capture/export workflow when the diagnostic tooling is finalized.
