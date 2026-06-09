# Mitsubishi Support Matrix

This matrix is a source-and-test inventory for Mitsubishi support in Nexus. It intentionally separates implemented code, test evidence, and real-device readiness.

## Protocol Matrix

| Area | Client | Transport | Read/write | Batch/random | Virtual server | Test evidence | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| MC3E Binary | `Mc3EBinaryClient` | TCP, default port `5007` | Typed `IReadWriteDevice`; bytes, string, bools | `IBatchReadWrite`, random read/write, multi-length random read, large read/write splitting | `Mc3EVirtuServer` | Address parser and many virtual-server tests | Usable candidate |
| MC3E ASCII | `Mc3EAsciiClient` | TCP, default port `5007` | Typed `IReadWriteDevice` in source | No `IBatchReadWrite` implementation | None found | No dedicated tests found | Needs audit |
| MC3E UDP | `Mc3EUdpClient` | UDP, default port `5007`; `UseAscii` switch | Typed `IReadWriteDevice` in source | No `IBatchReadWrite` implementation | None found | No dedicated tests found | Needs audit |
| A1E | `MelsecA1EClient` | TCP, default port `5551` | Typed `IReadWriteDevice`; bytes, string, bools | `IBatchReadWrite`; sequential batch/random methods | `MelsecA1EVirtualServer` | Address, frame, response, virtual-server tests | Usable candidate |
| FX serial | `FxSerialClient` | `ISerialPort`; ENQ/ACK/STX/ETX/SUM flow | Typed `IReadWriteDevice` overrides | No batch interface | None found | No dedicated tests found | Experimental / needs audit |
| FX module package | `MitsubishiFxSerialClient` | `Stream`; ENQ frame path | Typed `IReadWriteDevice` in source | No batch interface | None found | Constructor/logger/dispose only | Experimental / needs audit |

## MC3E Binary Details

Source-confirmed capabilities:

- Model enum accepted: `Qna_3E`, `Qna_2E`, `A_3E`, `A_1E`, `FX_3U`, `FX_5U`.
- Frame properties: `NetworkNo`, `PcNo`, `DestinationStationNo`, `WaitTimeUnit`.
- Aliases: `NetworkNumber` maps to `NetworkNo`, `StationNumber` maps to `PcNo`.
- Data settings: `ByteOrder` default `BigEndian`; `StringEncoding` default ASCII.
- Size settings: `MaxReadWordCount` and `MaxWriteWordCount` default to `960`.
- Commands implemented in source: batch word read/write, random word read/write, batch bit read/write, multi-length random read, large read/write splitting, remote run, remote stop, remote reset, read PLC type, error-state reset.
- Interfaces: inherits `TcpDeviceBase`, implements `IBatchReadWrite`.

Test-confirmed areas:

- Address parsing for D/M/X/Y/Z/R/B/W/L/F/V/S/TS/TC/CS/CC/SM/SD/DX/SW/ZR.
- Virtual TCP server start/stop.
- Int16, Int32, Float, Double, string encoding, byte order variants, batch read/write, random read/write.
- Bool arrays and bit areas including M, X, Y, B, L, F, V, S.
- ZR, SD, SW word register access against the virtual server.
- PLC-control commands against the virtual server.

Open audit items:

- Real Q/L/iQ-R/FX5U Ethernet module tests.
- Exact MC frame length semantics and timeout behavior under fragmented TCP packets.
- Device-code support for timer/counter current values not represented by `Mc3EAddressParser`.
- Whether every parser prefix is accepted by target PLC families and Ethernet modules.
- Error-code mapping beyond the small set currently translated.

## MC3E ASCII Details

Source-confirmed capabilities:

- Typed `ReadBool`, numeric reads, string/bytes read, and typed writes.
- Converts binary frame bytes to ASCII hex for transmit and parses ASCII hex response back to binary.
- Has `ByteOrder`, `StringEncoding`, max read/write word count properties.

Current limitations:

- No dedicated test file or virtual ASCII server was found.
- Sync methods call async internals through `GetAwaiter().GetResult()`.
- No `IBatchReadWrite`.
- Needs protocol-level audit for ASCII response framing, completion-code offset, and partial-read behavior.

## MC3E UDP Details

Source-confirmed capabilities:

- Typed `IReadWriteDevice` implementation over `UdpDeviceBase`.
- `UseAscii` controls whether frames are sent as binary or ASCII hex.
- Has `ByteOrder` and `StringEncoding`.

Current limitations:

- No UDP virtual server or UDP-specific tests were found.
- No `IBatchReadWrite`.
- UDP retry, packet loss, response correlation, broadcast behavior, and ASCII mode need field validation.

## A1E Details

Source-confirmed capabilities:

- TCP client with default port `5551`.
- `PLCNumber` default `0xFF`.
- `MaxWordReadCount = 64`, `MaxBitReadCount = 256`.
- Typed reads/writes for bool, numeric types, string, bytes.
- `ReadBools` and `WriteBools`.
- Static command builders and response helpers for testing.
- Implements `IBatchReadWrite`.

Test-confirmed areas:

- Address parsing for D, M, X, Y, S, B, R, W, F, TS, TC, TN, CS, CC, CN.
- X/Y octal-or-hex parsing rule.
- Read/write command construction.
- Response success/error handling.
- Data extraction for word and bit modes.
- Virtual TCP server start/stop and end-to-end D/R/M/string/float scenarios.
- Custom `PLCNumber`.

Open audit items:

- Real FX/QnA/A-series Ethernet module behavior.
- Float, double, long write byte order. Some write paths use `BitConverter.GetBytes`, which is platform-endian.
- A1E error-code documentation and richer diagnostics.
- Batch implementation is sequential over typed reads/writes, not a true protocol-level random aggregation.

## FX Serial Details

There are two FX serial paths:

| Client | Namespace | Input abstraction | Address support observed | Status |
| --- | --- | --- | --- | --- |
| `FxSerialClient` | `Nexus.Mitsubishi` | `ISerialPort` | Regex allows D/M/X/Y/T/S | Experimental |
| `MitsubishiFxSerialClient` | `Nexus.MitsubishiFx` | `Stream` | Parser handles D/M/Y/X/T/C/S/R/Z/V, with default fallback to D | Experimental |

Important notes:

- `FxSerialClient` uses `FxFrameBuilder` and an ENQ/ACK handshake before STX-framed commands.
- `MitsubishiFxSerialClient` builds ENQ frames with station and sum check, but the current tests only cover construction and disposal.
- Neither path has a fake serial integration test that validates full FX request/response behavior.
- The two implementations should be audited and either consolidated or documented as separate protocol variants.

## Virtual Server Coverage

| Virtual server | Protocol | Backed areas in source | Notes |
| --- | --- | --- | --- |
| `Mc3EVirtuServer` | MC3E Binary TCP | D/W/R/Z/ZR/SD/SW words; M/X/Y/B/L/F/V/S bits | Supports batch, random, multi-length random, PLC control; does not model every timer/counter parser prefix |
| `MelsecA1EVirtualServer` | A1E TCP | D/R/W/TN/CN words; M/X/Y/S/B/F/TS/TC/CS/CC bits | Supports word/bit read/write and bit-area word access |

## Real Device Validation Needed

Minimum field matrix before production claims:

| Target | Required checks |
| --- | --- |
| MC3E Binary TCP | Q/L/iQ-R/FX5U module connection, D/M/X/Y/W/R/ZR, byte order, large read/write, random read/write, error responses, reconnect |
| MC3E ASCII TCP | Same address and typed data matrix as Binary, plus ASCII frame captures |
| MC3E UDP | Binary and ASCII UDP modes, timeout/retry behavior, response matching, packet loss handling |
| A1E TCP | D/M/X/Y/R/W/TN/CN, bool packing, max counts, PLC number, real error responses |
| FX serial | Serial parameters, station handling, ENQ/ACK/NAK flow, SUM validation, D/M/X/Y/T/C/S behavior |

