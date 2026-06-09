# Mitsubishi Complete Scope

This page is the working target for making Mitsubishi support complete, detailed, and easy for field users. It separates protocol ambition from current repository evidence.

Status meanings:

- **Implemented**: public client/server/parser code exists.
- **Tested**: automated tests cover the path.
- **Documented**: user-facing docs exist.
- **Needs Audit**: source exists but tests, docs, or hardware evidence are not enough.
- **Needs Work**: missing code, tests, docs, or real-device validation.

## Current Summary

| Area | Current state | Main gap |
| --- | --- | --- |
| MC3E Binary TCP | Strongest Mitsubishi path; virtual server and broad tests exist | Needs real Q/L/iQ-R/FX5U validation and deeper error docs |
| MC3E ASCII TCP | Client source exists and has basic fake-server tests | Needs broader device-area, bit, error, and hardware validation |
| MC3E UDP | Client source exists with binary/ASCII switch and basic fake-server tests | Needs broader device-area, bit, error, and hardware validation |
| A1E TCP | Usable path with virtual server and tests | Needs real-device validation and byte-order audit for multi-word writes |
| FX serial | Two implementation paths exist | Needs consolidation, fake serial integration tests, and hardware traces |
| WPF tooling | Mitsubishi page exposes MC3E Binary/ASCII TCP, MC3E Binary/ASCII UDP, and A1E TCP | No Mitsubishi packet parser/export/replay workflow |

## Protocol Family Matrix

| Family | Frame/transport | Client | Server/simulator | Tests | Docs | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- |
| MC 3E Binary TCP | Binary 3E frame over TCP | `Mc3EBinaryClient` | `Mc3EVirtuServer` | Strong | Good | Usable candidate | Add device-model validation and error-code docs |
| MC 3E ASCII TCP | ASCII hex 3E frame over TCP | `Mc3EAsciiClient` | Test fake | Basic D-register read/write | Basic + WPF entry | Needs Audit | Add area, bit, error, and larger payload tests |
| MC 3E UDP Binary | Binary 3E frame over UDP | `Mc3EUdpClient` | Test fake | Basic D-register read/write | Basic + WPF entry | Needs Audit | Add response correlation, area, bit, and error tests |
| MC 3E UDP ASCII | ASCII 3E frame over UDP via `UseAscii` | `Mc3EUdpClient` | Test fake | Basic D-register read/write | Basic + WPF entry | Needs Audit | Add response correlation, area, bit, and error tests |
| MC 4E Binary/ASCII | 4E frame with serial/request id semantics | None | None | None | None | Needs Work | Decide API shape after 3E parity |
| MC 1E / A compatible | A1E-compatible TCP | `MelsecA1EClient` | `MelsecA1EVirtualServer` | Strong | Good | Usable candidate | Add hardware validation |
| FX programming-port serial | ENQ/ACK/STX/ETX style serial | `FxSerialClient` | None | None found | Basic | Needs Audit | Add fake serial integration tests |
| FX serial separate package | Stream-based FX serial | `MitsubishiFxSerialClient` | None | 3 shallow tests | Basic | Needs Audit | Decide whether to merge, deprecate, or position separately |
| SLMP naming | MC/SLMP-style 3E is represented by MC3E Binary | Same as MC3E | Same as MC3E | Same as MC3E | Basic | Partial | Document SLMP terminology clearly |
| MELSEC iQ-R specific extensions | Device/model-specific behavior | None explicit | None | None | None | Needs Work | Add only after base MC3E/4E coverage is stable |

## MC Command Scope

| Command family | Typical command | MC3E Binary | MC3E ASCII | MC3E UDP | A1E | FX serial | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Batch read words | `0401` word | Yes | Source signal | Source signal | Yes equivalent | Source signal | Partial | Add ASCII/UDP/FX tests |
| Batch read bits | `0401` bit | Yes | Source signal | Source signal | Yes equivalent | Source signal | Partial | Add ASCII/UDP/FX tests |
| Batch write words | `1401` word | Yes | Source signal | Source signal | Yes equivalent | Source signal | Partial | Add byte-order and max-count tests |
| Batch write bits | `1401` bit | Yes | Source signal | Source signal | Yes equivalent | Source signal | Partial | Add bit packing tests |
| Random read | `0403` | Yes | Unknown | Unknown | Sequential/batch style | No | Partial | Decide cross-client API shape |
| Random write | `1402` | Yes | Unknown | Unknown | Sequential/batch style | No | Partial | Add tests for mixed device writes |
| Multi-length random read | `0403` subcommand variant | Yes | Unknown | Unknown | No | No | Partial | Add docs and test examples |
| Remote run | `1001` | Yes | Unknown | Unknown | No | No | Partial | Add safety docs and hardware validation |
| Remote stop | `1002` | Yes | Unknown | Unknown | No | No | Partial | Add safety docs and hardware validation |
| Remote reset | `1006` | Yes | Unknown | Unknown | No | No | Partial | Add safety docs and hardware validation |
| Read PLC type | `0101` | Yes | Unknown | Unknown | No | No | Partial | Add WPF diagnostic display |
| Error state reset | `1617` | Yes | Unknown | Unknown | No | No | Partial | Add error code docs |
| Device monitor/status | model-specific | No | No | No | No | No | Needs Work | Add after command reference review |
| File/register extended commands | model-specific | No | No | No | No | No | Needs Work | Lower priority |

## Device Area Scope

MC3E and A1E use different device-code representations. The table below is the user-facing coverage target.

| Area | Meaning | MC3E parser | MC3E virtual server | A1E parser | A1E virtual server | FX paths | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| D | Data register | Yes | Yes | Yes | Yes | Yes | Good | Real-device validation |
| W | Link register | Yes | Yes | Yes | Yes | No/unknown | Good for MC/A1E | Validate on Q/L/iQ-R |
| R | File register | Yes | Yes | Yes | Yes | Yes in separate FX parser | Good for MC/A1E | Clarify FX support |
| ZR | Extended file register | Yes | Yes | No | No | No/unknown | Partial | Validate large address range |
| Z | Index register | Yes | Yes as word area | No | No | Yes in separate FX parser | Partial | Clarify use cases and tests |
| M | Internal relay | Yes | Yes | Yes | Yes | Yes | Good | Real-device validation |
| X | Input relay | Yes | Yes | Yes | Yes | Yes | Good | Document base differences |
| Y | Output relay | Yes | Yes | Yes | Yes | Yes | Good | Document base differences and write safety |
| L | Latch relay | Yes | Yes | No | No | No/unknown | Partial | Add docs/tests |
| F | Annunciator | Yes | Yes | Yes | Yes | No/unknown | Partial | Add docs/tests |
| V | Edge relay | Yes | Yes | No | No | Yes in separate FX parser | Partial | Confirm real support |
| S | Step relay | Yes | Yes | Yes | Yes | Yes | Good | Add tests per protocol path |
| B | Link relay | Yes | Yes | Yes | Yes | No/unknown | Good for MC/A1E | Real-device validation |
| SM | Special relay | Yes | Not fully modeled | No | No | No/unknown | Needs Work | Add virtual server backing or mark read-only |
| SD | Special register | Yes | Yes | No | No | No/unknown | Partial | Add real-device validation |
| SW | Link special register | Yes | Yes | No | No | No/unknown | Partial | Add docs/tests |
| TS | Timer contact | Yes | Not fully modeled | Yes | Yes | Yes/unknown | Partial | Validate semantics |
| TC | Timer coil | Yes | Not fully modeled | Yes | Yes | No/unknown | Partial | Validate semantics |
| TN | Timer current value | No in MC3E parser | No | Yes | Yes | T in FX paths | Needs Work | Add MC3E support if protocol/device supports it |
| CS | Counter contact | Yes | Not fully modeled | Yes | Yes | No/unknown | Partial | Validate semantics |
| CC | Counter coil | Yes | Not fully modeled | Yes | Yes | No/unknown | Partial | Validate semantics |
| CN | Counter current value | No in MC3E parser | No | Yes | Yes | C in FX paths | Needs Work | Add MC3E support if protocol/device supports it |
| DX | Direct input | Yes | Not fully modeled | No | No | No | Needs Work | Add docs/tests or mark unsupported |
| DY | Direct output | No | No | No | No | No | Needs Work | Decide support |
| AI/AO | Analog aliases | No | No | No | No | No | Not Planned Yet | Keep as application-level mapping unless protocol supports direct code |

## Model And Hardware Scope

| Target family | Expected protocol path | Current evidence | Required validation |
| --- | --- | --- | --- |
| Q series Ethernet | MC3E Binary or ASCII | Virtual tests only | D/M/X/Y/W/R/ZR, random read/write, remote run/stop safety |
| L series Ethernet | MC3E Binary or ASCII | Virtual tests only | Same as Q plus module-specific limits |
| iQ-R / R series | MC3E/SLMP, possibly 4E | No hardware evidence | 3E compatibility, 4E need, device-code differences |
| FX5U Ethernet | MC3E/SLMP | No hardware evidence | D/M/X/Y/R/ZR, byte order, max counts |
| FX3U Ethernet adapter | MC3E or A-compatible depending module | No hardware evidence | Correct frame mode and port setup |
| FX serial | FX serial path | Shallow tests | ENQ/ACK/NAK, station, serial settings, D/M/X/Y/T/C/S |
| Legacy A/QnA | A1E-compatible path | Virtual tests only | PLC number, max counts, X/Y base, error responses |

## API Scope

| API shape | MC3E Binary | MC3E ASCII | MC3E UDP | A1E | FX serial | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `IReadWriteDevice` typed reads | Yes | Yes | Yes | Yes | Yes | Implemented/source signal | Add parity tests |
| Typed writes | Yes | Yes | Yes | Yes | Yes | Implemented/source signal | Audit multi-word write byte order |
| `ReadBools`/`WriteBools` | Yes | Unknown | Unknown | Yes | Unknown | Partial | Normalize or document differences |
| `IBatchReadWrite` | Yes | No | No | Yes | No | Partial | Decide whether ASCII/UDP should implement it |
| Random read/write APIs | Yes | No/unknown | No/unknown | Sequential style | No | Partial | Add common docs |
| Large read/write splitting | Yes | Unknown | Unknown | Limited by A1E max count | No | Partial | Add tests and limits |
| String encoding | Yes | Source signal | Source signal | Yes | Unknown | Partial | Add examples |
| Byte order configuration | Yes | Yes | Yes | Partial | Unknown | Partial | Audit per protocol |
| Async APIs | Wrapper or direct depending client | Wrapper style | Wrapper style | Wrapper style | Wrapper style | Partial | Avoid UI blocking in WPF |
| Packet parser | No Mitsubishi parser | No | No | No | No | Needs Work | Add after Modbus recorder stabilizes |

## Server And Simulator Scope

| Simulator | Current state | Gap |
| --- | --- | --- |
| `Mc3EVirtuServer` | Covers MC3E Binary TCP for common word/bit areas and PLC control | Does not model every parser prefix or ASCII/UDP |
| `MelsecA1EVirtualServer` | Covers A1E TCP for common word/bit areas | Needs more error scenarios and byte-order tests |
| MC3E ASCII server | None | Needed for ASCII parity |
| MC3E UDP server | None | Needed for UDP parity |
| FX serial fake/server | None | Needed for serial tests |
| Packet replay | None | Future differentiator |

## Documentation Scope

| Topic | Current state | Gap |
| --- | --- | --- |
| Client selection | Basic docs exist | Add decision tree by PLC model/module |
| Address format | Good first pass | Add more examples and unsupported cases |
| Connection settings | Basic | Add network number, PC number, station, PLC number, port setup by model |
| Error codes | Thin | Add MC completion code and A1E error table |
| Byte order | Mentioned | Add Mitsubishi-specific examples and warnings |
| Remote control safety | Thin | Add prominent safety docs for run/stop/reset |
| WPF diagnostics | Connection/read/write panel covers the main implemented transports | Add packet parser/export after parser exists |
| HSL migration | Generic guide exists | Add Mitsubishi migration chapter |

## Test And Validation Scope

| Evidence | Current state | Required for "complete" |
| --- | --- | --- |
| `Nexus.Mitsubishi.Tests` | 169 passing tests recorded | Broaden MC3E ASCII/UDP tests beyond basic D-register read/write |
| `Nexus.MitsubishiFx.Tests` | 3 passing tests recorded | Add fake serial integration tests |
| Fixed-port robustness | Some virtual tests use fixed ports | Move to dynamic ports where practical |
| Real-device validation | None recorded | Validate Q/L/iQ-R/FX5U and at least one FX serial path |
| Long-run | None | Add stable reconnect/timeout test plans |
| Benchmark | None | Add MC3E Binary baseline against virtual server |

## Priority Backlog

1. Broaden MC3E ASCII TCP tests to bit areas, error responses, and larger payloads.
2. Broaden MC3E UDP Binary/ASCII tests to bit areas, response correlation, and error responses.
3. Decide the FX serial package story: consolidate, deprecate, or document both clearly.
4. Add MC3E `TN`/`CN` current-value support if confirmed by protocol/manual evidence.
5. Add MC completion-code and A1E error-code documentation.
6. Add Mitsubishi HSL migration chapter.
7. Add Mitsubishi packet parser for MC3E Binary, then ASCII and A1E.
8. Add WPF Mitsubishi packet decode/export after parser exists.
9. Add real-device validation rows for Q/L/iQ-R/FX5U and FX serial.
10. Add benchmark and long-run tests for MC3E Binary.
