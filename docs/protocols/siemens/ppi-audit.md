# Siemens PPI Audit

Date: 2026-06-08

Scope:

- `src/Nexus.Siemens/SiemensPpiClient.cs`
- `tests/Nexus.Siemens.Tests/SiemensPpiClientTests.cs`
- Siemens source and docs were used only as read-only context.

This audit did not modify PPI source code or tests.

## Executive Conclusion

The current user change in `SiemensPpiClient.cs` looks reasonable for the implemented long-frame model: changing the total frame size and verification size from `4 + len + 1` to `4 + len + 2` correctly accounts for both BCC and the trailing `0x16`.

Within the current fake-serial tests, request building, response parsing, BCC verification, and the serial base class read length are internally consistent. This is not yet enough to mark PPI production-ready, because the implementation still needs real S7-200/PPI traces for frame variants, station addressing, command payload semantics, and serial timing behavior.

## Frame Construction And Parsing

### Long Frame Shape

Current request builder:

- Starts with `0x68`.
- Writes duplicated length bytes at indexes `1` and `2`.
- Writes the second `0x68` at index `3`.
- Writes `control`, `SlaveAddress`, `MasterAddress`, and `functionCode` at indexes `4..7`.
- Copies command data from index `8`.
- Calculates BCC over indexes `4` through the last data byte.
- Appends BCC and trailing `0x16`.

The current length expression is:

```text
lenField = 4 + dataLen
frame.Length = 4 + lenField + 2
```

That expands to `10 + dataLen`, which matches:

```text
68 LL LL 68 C DA SA FC DATA... BCC 16
```

The previous `4 + lenField + 1` form would have been one byte short for this model.

### Base Class Receive Length

`SiemensPpiClient.ResponseHeaderLength` is `8`. `GetResponsePayloadLength()` returns `len - 2`.

Because `SerialDeviceBase` reads:

```text
header length + payload length
```

the total read length becomes:

```text
8 + (len - 2) = len + 6 = 4 + len + 2
```

This matches the updated `VerifyPpiFrame()` total length check. The user change is therefore consistent with the existing base-class receive path.

### BCC Range

The BCC implementation XORs from index `4` through the final data byte and excludes:

- leading `0x68`
- duplicated length fields
- second `0x68`
- BCC byte itself
- trailing `0x16`

The tests assert concrete BCC values for read/write frames, and the negative test confirms an invalid BCC is rejected.

### Gaps In Frame Validation

Current validation does not yet check:

- `response[2] == response[1]`
- response destination/source addresses match `MasterAddress` and `SlaveAddress`
- response control byte
- response function code matches the request expectation
- short/fixed PPI responses such as ACK-style frames, if required by target devices
- variable timing/inter-frame behavior on real serial adapters

The parser treats any non-long-frame response as invalid. That may be correct for the current command model, but it needs hardware trace confirmation.

## Function Codes And Data Offset

Current code uses:

- request function code `0x01` for reads
- request function code `0x02` for writes
- response function code `0x04` in read tests
- response function code `0x02` in write tests
- `0x01` and `0x03` as failure function codes

Response data begins at index `8`, with length `lenField - 4`. The first response payload byte is expected to be `0xFF` for read success.

Risk: these function-code and payload conventions are only validated against the repository fake serial port. They need comparison with captured PPI frames from a known-good S7-200 setup before the API is advertised as production-compatible.

## Address Parsing

Current regex accepts:

```text
V, M, I, Q, S, SM, C
```

with optional bit suffix:

```text
V100
V100.0
M10.2
```

Current area-code mapping:

| Area | Code | Audit status |
| --- | --- | --- |
| `I` | `0x81` | Plausible, needs trace validation. |
| `Q` | `0x82` | Plausible, needs trace validation. |
| `M` | `0x83` | Plausible, needs trace validation. |
| `S` | `0x84` | Needs hardware validation. |
| `V` | `0x85` | Main S7-200 memory target, needs trace validation. |
| `SM` | `0x86` | Needs hardware validation. |
| `C` | `0x1C` | Highest risk; counter semantics may not match byte-style read/write. |

DB addresses are not currently supported by this parser. For S7-200 PPI this may be acceptable, but it must be documented clearly because users migrating from S7 or HSL-style Siemens APIs may expect `DBx.DBWn` support.

### Address Risks

- Bit offset is not limited to `0..7`; `M10.8` parses and can produce invalid masks or meaningless reads.
- Byte address is parsed as `int` but serialized into two bytes, so values above `65535` silently truncate.
- Read length is `ushort` at the public API but serialized into one byte in PPI command data, so values above `255` silently truncate.
- Write `byte[]` and `string` lengths are also stored in one byte after padding; large writes can overflow the command length field.
- `C` is accepted for all read/write shapes, but counters may require different semantics than byte memory.
- `T`, `AI`, `AQ`, and DB-style addresses are unsupported.

## Test Coverage

Current PPI tests cover:

- `ReadInt16_BuildsPpiFrameAndParsesResponse`
- `ReadBool_UsesBitAddressAndExtractsTargetBit`
- `WriteInt16_BuildsWriteFrame`
- `ReadBytes_InvalidBcc_ReturnsFailure`

These tests verify:

- long-frame request construction
- duplicated length byte values in constructed frames
- BCC calculation for selected requests
- response parsing for selected read/write paths
- bit extraction for one `M` address
- invalid BCC failure path

### Coverage Gaps

Missing high-priority tests:

- `response[1] != response[2]` must fail
- response address mismatch must fail or be explicitly tolerated
- unexpected function code must fail
- malformed `0x68`/`0x16` placement
- all supported address areas: `V`, `I`, `Q`, `M`, `S`, `SM`, `C`
- invalid bit offsets, especially `8` and higher
- byte address overflow above `65535`
- read/write length overflow above `255`
- `ReadBytes`, `ReadString`, `ReadInt32`, `ReadInt64`, `ReadFloat`, `ReadDouble`
- `Write(bool)`, `Write(int)`, `Write(long)`, `Write(float)`, `Write(double)`, `Write(string)`, `Write(byte[])`
- timeout, short read, reconnect, and serial port closed behavior

Missing validation:

- real S7-200 device trace tests
- multiple serial adapter behavior
- station address configuration
- PPI baud/parity/data/stop-bit matrix
- PLC-side restrictions for I/Q/S/SM/C writes

## Assessment Of Current User Change

The current user change appears correct for the implemented frame model:

- Request length now includes both BCC and `0x16`.
- Response verification now expects the same total frame size that the base serial receive path returns.
- Existing Siemens PPI tests pass with the new length model.

Remaining risk is not the specific `+2` change. The larger risk is that the whole PPI command envelope and payload model are still validated only against fake serial responses. Before production labeling, the project needs real frame captures or a protocol reference-backed conformance test set.

## Recommendations

### P0

- Add validation for duplicated length byte: reject `response[1] != response[2]`.
- Add validation for response source/destination addresses against `MasterAddress` and `SlaveAddress`, or explicitly document why broadcast/adapter behavior requires tolerance.
- Add bounds checks for bit offsets `0..7`, byte address `0..65535`, and command length `0..255`.
- Capture real S7-200 PPI traces for at least `V` memory read/write Bool, Int16, Int32, Float, String, and Bytes.

### P1

- Add unit tests for malformed frame shape, length mismatch, address mismatch, and unexpected function code.
- Add tests for every currently accepted address area and unsupported DB/T/AI/AQ behavior.
- Confirm whether `C` should remain in the generic byte read/write parser or move behind explicit counter APIs.
- Confirm bit write semantics. Current `Write(bool)` sends a single mask/value byte and may not preserve neighboring bits unless the PLC command semantics are bit-specific.

### P2

- Document recommended serial settings for known CPU/adapter combinations.
- Add packet logging examples to Siemens PPI docs after real trace validation.
- Consider a small offline PPI packet parser for WPF diagnostics, similar to the Modbus parser workstream.
- Review async wrappers for latency/cancellation expectations after protocol correctness is settled.

## Verification

Command run:

```powershell
dotnet test tests\Nexus.Siemens.Tests --filter "FullyQualifiedName~SiemensPpi"
```

Result:

```text
Passed: 4
Failed: 0
Skipped: 0
Total: 4
Duration: 618 ms
```

