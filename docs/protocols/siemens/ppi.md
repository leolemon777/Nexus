# Siemens PPI

Status: audit draft. Do not treat this page as production guidance yet.

Detailed audit: [ppi-audit.md](ppi-audit.md).

`SiemensPpiClient` exists in `src/Nexus.Siemens` and targets serial PPI through the shared `ISerialPort` abstraction. The current source file is under active user change, so this page records only high-level observations and audit needs.

## Current Source Signals

Confirmed from current source:

- `SiemensPpiClient` inherits `SerialDeviceBase`.
- Constructor accepts `ISerialPort` and timeout.
- `MasterAddress` defaults to `1`.
- `SlaveAddress` defaults to `2`.
- Frame verification checks start/end bytes, length, and BCC.
- Address parser accepts `V`, `M`, `I`, `Q`, `S`, `SM`, and `C` prefixes with optional bit suffix.
- Read/write methods exist for Bool, Int16/UInt16, Int32/UInt32, Int64/UInt64, Float, Double, String, and Bytes.
- Tests use a fake serial port and cover frame construction/parsing for selected reads/writes plus invalid BCC.

## Basic Shape

Example skeleton only:

```csharp
ISerialPort serialPort = /* application adapter */;
var client = new SiemensPpiClient(serialPort, timeout: 5000)
{
    MasterAddress = 1,
    SlaveAddress = 2
};

var read = client.ReadInt16("V100");
```

TODO:

- Add a concrete serial-port adapter example after the project standardizes public serial setup docs.
- Add baud rate, parity, data bits, stop bits, and station-address guidance after hardware validation.

## Address Format

Current parser pattern:

| Area | Examples | Notes |
| --- | --- | --- |
| V | `V100`, `V100.0` | V memory. |
| M | `M10`, `M10.2` | Marker memory. |
| I | `I0`, `I0.1` | Input area. |
| Q | `Q0`, `Q0.1` | Output area. |
| S | `S0`, `S0.1` | Needs audit. |
| SM | `SM0`, `SM0.1` | Special marker area; needs audit. |
| C | `C0` | Counter area; needs audit. |

Bit operations require a bit suffix such as `V100.0` or `M10.2`.

Audit TODO:

- Confirm area codes against PPI hardware traces.
- Confirm bit range validation and error behavior for invalid bit offsets.
- Confirm byte/word/dword addressing conventions for S7-200.

## Current Risk Notes

- PPI is serial timing sensitive; fake serial tests do not prove hardware timing.
- The implementation uses serial response framing assumptions that need trace comparison.
- Current PPI async methods are wrapper-style methods; latency and cancellation behavior need review.
- Floating-point and 64-bit conversions need byte-order validation on a real PLC.

## Real Device Validation Checklist

- Validate with a known S7-200 CPU and a known-good serial/PPI adapter.
- Record COM settings and station addresses.
- Read/write V memory Bool, Int16, Int32, Float, String, and Bytes.
- Validate M/I/Q areas where the PLC/project allows it.
- Validate invalid BCC, timeout, wrong station, and reconnect behavior.
- Capture request/response bytes for every passing and failing scenario.

## Draft TODO

- Complete code audit without reverting current user changes.
- Add protocol frame examples only after verified against traces.
- Decide whether to expose PPI packet parser/diagnostics in the WPF debugger.
