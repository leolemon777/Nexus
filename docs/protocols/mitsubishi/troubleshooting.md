# Mitsubishi Troubleshooting

This page lists practical checks for Mitsubishi communication paths in Nexus. It is based on the current implementation and offline tests.

## First Checks

1. Confirm the selected client matches the PLC/module setting.
   - MC3E Binary TCP: `Mc3EBinaryClient`
   - MC3E ASCII TCP: `Mc3EAsciiClient`
   - MC3E UDP: `Mc3EUdpClient`
   - A1E TCP: `MelsecA1EClient`
   - FX serial: `FxSerialClient` or `MitsubishiFxSerialClient`, both currently audit targets
2. Confirm the port.
   - MC3E clients default to `5007`.
   - A1E defaults to `5551`.
   - FX serial depends on serial adapter settings and the chosen serial abstraction.
3. Confirm address base rules.
   - MC3E `X/Y/B/DX` are parsed as hex.
   - A1E `B/W` are parsed as hex.
   - A1E `X/Y` are octal only when the text starts with `0`; otherwise hex.
4. Confirm byte order for 32-bit and 64-bit data.
   - MC3E exposes `ByteOrder`.
   - A1E and FX need device-specific validation for multi-word writes.

## MC3E Binary

Symptoms and checks:

| Symptom | Likely cause | Check |
| --- | --- | --- |
| Connection succeeds but reads fail | Wrong frame mode or module not configured for MC 3E Binary | Confirm PLC Ethernet module protocol mode and port |
| Values look byte-swapped | Wrong `ByteOrder` | Test `BigEndian`, `LittleEndian`, `MidBigEndian`, `MidLittleEndian` with a known register pattern |
| `X` or `Y` reads wrong offset | Hex address expectation | Compare `X10` as hex `0x10`, not decimal 10 |
| Batch read returns unexpected values | Mixed bit/word areas or sparse ranges | Try single-address reads first; then compare `BatchRead` and `RandomRead` |
| Large read fails | Device/module count limit lower than `MaxReadWordCount` | Lower `MaxReadWordCount` and retry |
| PLC-control command fails | PLC security or remote-operation setting | Validate remote run/stop/reset permission on the PLC |

Diagnostics to capture:

- Client type and version.
- IP, port, timeout, persistent/short connection mode.
- `NetworkNo`, `PcNo`, `DestinationStationNo`, `WaitTimeUnit`.
- Address, data type, length/count.
- `ByteOrder` and `StringEncoding`.
- TX/RX packet log from `OnMessageSent` and `OnMessageReceived`.
- PLC model, Ethernet module model, and configured protocol/frame mode.

## MC3E ASCII

Audit-focused checks:

- Verify the PLC/module is configured for MC 3E ASCII, not Binary.
- Capture raw TCP payloads; the implementation sends ASCII hex derived from binary frame bytes.
- Confirm response completion-code position with a known successful read and a forced error read.
- Test fragmented response behavior under real TCP conditions.
- Compare the same address and data type against `Mc3EBinaryClient` when the device supports both modes.

## MC3E UDP

Audit-focused checks:

- Confirm whether the PLC expects Binary UDP or ASCII UDP and set `UseAscii` accordingly.
- Keep timeouts conservative during first validation.
- Capture request and response packets because UDP has no connection state.
- Test duplicate/lost response behavior and simultaneous requests.
- Validate whether the target module supports broadcast; do not assume `UdpDeviceBase` broadcast behavior is accepted by the PLC.

## A1E

Symptoms and checks:

| Symptom | Likely cause | Check |
| --- | --- | --- |
| `X/Y` address mismatch | Octal-or-hex source rule | Use `X017` for octal 15, `X17` for hex 23 |
| Read works but long/float/double write is wrong | Write byte order needs audit | Validate with known 32/64-bit patterns before production use |
| Reads over 64 words fail | A1E max count | Source chunks word reads at `64`; verify target module limit |
| Bit reads are shifted | Packed bit response misunderstanding | Compare `ReadBool`, `ReadBools`, and direct server/PLC monitor |
| PLC number issue | Wrong `PLCNumber` | Try default `0xFF`, then device-specific PLC number |

A1E diagnostics to capture:

- IP, port, timeout, persistent mode.
- `PLCNumber`.
- Address, length/count, bit-versus-word operation.
- Raw response bytes and decoded error code.
- Real PLC/module model and configured A-compatible frame setting.

## FX Serial

FX serial is not yet a production-ready documented path in Nexus. Before relying on it:

- Confirm which API is being used: `Nexus.Mitsubishi.FxSerialClient` or `Nexus.MitsubishiFx.MitsubishiFxSerialClient`.
- Record serial parameters: port name, baud rate, data bits, parity, stop bits, station, timeout.
- Capture complete ENQ/ACK/NAK/STX/ETX/SUM frames.
- Validate `D`, `M`, `X`, `Y`, `T`, `C`, and `S` independently on a real FX device.
- Check whether `X/Y` addressing is octal, byte-based, or bit-based for the chosen PLC and adapter.
- Add fake serial tests before expanding the public docs.

## What Not To Claim Yet

Do not claim:

- Real Mitsubishi device validation completed.
- MC3E ASCII or UDP production readiness.
- FX serial production readiness.
- Full coverage of every Mitsubishi device code or PLC family.
- HSL feature parity for Mitsubishi until migration tests and real-device captures exist.

## Next Validation Steps

1. Run existing offline tests for `Nexus.Mitsubishi.Tests` and `Nexus.MitsubishiFx.Tests`.
2. Add focused tests for `Mc3EAsciiClient` frame send/receive behavior.
3. Add a UDP virtual server or deterministic fake for `Mc3EUdpClient`.
4. Add fake serial tests for both FX serial clients.
5. Build a real-device matrix covering Q/L/iQ-R/FX5U Ethernet and FX serial devices.
6. Store packet captures and device settings with each real-device validation result.

