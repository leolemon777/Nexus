# Mitsubishi Protocols

This page summarizes the Mitsubishi-related implementations currently present in Nexus. It is based on local source and test evidence only. No real Mitsubishi PLC validation is claimed here.

## Packages And Clients

| Package | Client | Transport | Current status | Evidence |
| --- | --- | --- | --- | --- |
| `Nexus.Mitsubishi` | `Mc3EBinaryClient` | TCP, MC 3E Binary / SLMP-style frame | Usable candidate | End-to-end tests with `Mc3EVirtuServer`; address parser, typed read/write, batch, random, bit, large-read, PLC-control tests |
| `Nexus.Mitsubishi` | `Mc3EAsciiClient` | TCP, MC 3E ASCII | Basic tested, exposed in WPF | Source implements typed `IReadWriteDevice`; WPF debugger can connect/read/write; fake-server D-register read/write tests |
| `Nexus.Mitsubishi` | `Mc3EUdpClient` | UDP, MC 3E Binary or ASCII | Basic tested, exposed in WPF | Source implements typed `IReadWriteDevice`; WPF debugger can select binary/ASCII UDP; fake-server D-register read/write tests |
| `Nexus.Mitsubishi` | `MelsecA1EClient` | TCP, A1E compatible binary frame | Usable candidate | End-to-end tests with `MelsecA1EVirtualServer`; address, frame, response, typed read/write tests |
| `Nexus.Mitsubishi` | `FxSerialClient` | Serial `ISerialPort`, FX programming-port style frame | Experimental / needs audit | Source implements typed read/write through `FxFrameBuilder`; no dedicated tests found |
| `Nexus.MitsubishiFx` | `MitsubishiFxSerialClient` | `Stream`, FX serial frame | Experimental / needs audit | Only constructor, logger, dispose tests found |

Status meaning:

- Usable candidate: source and offline tests cover meaningful end-to-end behavior with virtual servers, but real-device evidence is still required.
- Needs audit: source exists, but tests are shallow or absent for the protocol path.
- Experimental: implementation exists but protocol details, device coverage, or verification depth are not yet sufficient for production claims.

## Recommended User Choice

Use `Mc3EBinaryClient` first for modern Mitsubishi Ethernet communication when the PLC or module supports MC 3E Binary over TCP.

Use `MelsecA1EClient` for A1E-compatible TCP scenarios, especially where the device is configured for the older A1E frame path.

Treat MC3E ASCII, MC3E UDP, and FX serial clients as audit targets before field use. ASCII TCP and UDP now have basic fake-server tests, but still need packet capture, hardware tests, and stricter documentation before being marked as production candidates.

## Current Strengths

- MC3E Binary supports typed reads/writes for `bool`, integer types, `float`, `double`, `string`, and raw bytes.
- MC3E Binary implements `IBatchReadWrite`, random read/write, multi-length random read, bit batch operations, large read/write splitting, and PLC control commands.
- A1E implements `IBatchReadWrite`, typed reads/writes, bool array reads/writes, command builders, response checking, and a virtual server.
- MC3E and A1E both have TCP virtual servers for offline integration testing.
- MC3E exposes connection parameters such as network number, PC number, destination station number, wait time unit, byte order, and string encoding.
- The WPF Mitsubishi page now exposes MC3E Binary TCP, MC3E ASCII TCP, MC3E Binary UDP, MC3E ASCII UDP, and A1E Binary TCP from one debugger panel.

## Main Gaps

- No real-device validation is recorded in this document set.
- MC3E ASCII and MC3E UDP only have basic D-register fake-server tests; they still need broader area, bit, error, and hardware validation.
- FX serial has two implementations with different abstractions and address handling; both need consolidation or a clear package-level recommendation.
- MC3E address support includes many device prefixes in the parser, but some prefixes are not backed by the virtual server storage or tested equally.
- A1E long, float, and double write byte ordering should be audited against real devices and protocol manuals.
- There is no Mitsubishi packet parser/replay diagnostic layer yet.

## Related Pages

- [Complete Scope](complete-scope.md)
- [Support Matrix](support-matrix.md)
- [Address Format](address-format.md)
- [Troubleshooting](troubleshooting.md)
