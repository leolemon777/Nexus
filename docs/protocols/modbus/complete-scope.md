# Modbus Complete Scope

This page is the working target for making `Nexus.Modbus` complete, detailed, and easy for field users. It separates protocol ambition from current repository evidence.

Status meanings:

- **Implemented**: public client/server/parser code exists.
- **Tested**: automated tests cover the path.
- **Documented**: user-facing docs exist.
- **Needs Work**: missing code, tests, docs, or field validation.
- **Not Planned Yet**: useful, but lower priority than common factory workflows.

## Current Summary

| Area | Current state | Main gap |
| --- | --- | --- |
| Common read/write function codes | FC01/02/03/04/05/06/15/16/22/23 implemented across main transports | Need stricter feature-by-feature tests per transport and server/parser parity |
| Transports | TCP, UDP, RTU, ASCII, ASCII-over-TCP, RTU-over-TCP clients exist | WPF packet decode/export is deepest on TCP; serial adapter docs need examples |
| Server/simulator | TCP server, virtual server, and WPF editable Modbus TCP Server exist | No UDP/RTU/ASCII server path yet |
| Packet parser | TCP, UDP, RTU, ASCII, RTU-over-TCP parser exists | No request/response correlation or exception-code descriptions |
| Batch/subscription | TCP and UDP implement `IBatchReadWrite` and `ISubscribeDevice` | RTU/ASCII/RTU-over-TCP batch/subscription not implemented |
| Real-device validation | Template exists | No recorded device rows yet |

## Transport Scope

| Transport | Client | Server/simulator | Parser | Tests | Docs | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- |
| TCP MBAP | `ModbusTcpClient` | `ModbusTcpServer`, `ModbusVirtualServer` | `ParseTcp` | Strong | Strong | Reference path | Add long-run and benchmark evidence |
| UDP MBAP | `ModbusUdpClient` | Test-only UDP server | `ParseUdp` | Strong; tests use dynamic server ports | Basic | Usable | Add public reusable simulator if field users need it |
| RTU serial | `ModbusRtuClient` | None | `ParseRtu` | Good fake-serial style coverage | Basic | Usable | Add serial adapter examples and real RS485 validation |
| ASCII serial | `ModbusAsciiClient` | None | `ParseAscii` | Basic/good | Basic | Usable | Add serial adapter examples and ASCII edge-case tests |
| ASCII-over-TCP | `ModbusAsciiOverTcpClient` | Test server | `ParseAscii` | Basic integration | Basic | Usable | Add gateway field validation |
| RTU-over-TCP | `ModbusRtuOverTcpClient` | Test server | `ParseRtuOverTcp` | Strong | Basic | Usable | Add DTU/gateway setup docs and field validation |
| DTU transparent serial | Core `DtuClient` exists | None | Reuse RTU parser after extraction | Thin | Thin | Needs Work | Document how DTU maps to RTU-over-TCP or transparent serial |
| Modbus over TLS | None | None | None | None | None | Not Planned Yet | Decide whether this belongs in Nexus or app-level transport wrappers |

## Function Code Scope

| FC | Standard name | Common use | Client | Server | Parser | Tests | Docs | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 01 | Read Coils | Read bit outputs | Yes | Yes | Yes | Yes | Yes | Implemented | Add per-transport parity checklist |
| 02 | Read Discrete Inputs | Read bit inputs | Yes | Yes | Yes | Yes | Yes | Implemented | Add per-transport parity checklist |
| 03 | Read Holding Registers | Read word registers | Yes | Yes | Yes | Yes | Yes | Implemented | Add long-run and max-count tests |
| 04 | Read Input Registers | Read read-only word registers | Yes | Yes | Yes | Yes | Yes | Implemented | Add long-run and max-count tests |
| 05 | Write Single Coil | Write one bit output | Yes | Yes | Yes | Yes | Yes | Implemented | Confirm bool write semantics on real devices |
| 06 | Write Single Register | Write one word | Yes | Yes | Yes | Yes | Yes | Implemented | Confirm signed/unsigned examples |
| 07 | Read Exception Status | Device status | No | No | No | No | No | Needs Work | Add optional API and parser support |
| 08 | Diagnostics | Loopback, counters, comm diagnostics | No | No | No | No | No | Needs Work | Add diagnostic subfunction model and tests |
| 11 | Get Comm Event Counter | Serial/device event count | No | No | No | No | No | Needs Work | Add low-priority diagnostic API |
| 12 | Get Comm Event Log | Serial/device event log | No | No | No | No | No | Needs Work | Add low-priority diagnostic API |
| 15 | Write Multiple Coils | Write multiple bits | Yes | Yes | Yes | Yes | Yes | Implemented | Add parser/server parity checks |
| 16 | Write Multiple Registers | Write many words | Yes | Yes | Yes | Yes | Yes | Implemented | Add max-count and byte-count tests |
| 17 | Report Server ID | Device identity on serial devices | No | No | No | No | No | Needs Work | Add RTU/ASCII-first API |
| 20 | Read File Record | File/subrecord data | No | No | No | No | No | Needs Work | Add model and offline parser tests before client API |
| 21 | Write File Record | File/subrecord write | No | No | No | No | No | Needs Work | Add model and explicit safety docs |
| 22 | Mask Write Register | Atomic bit mask write | Yes | Yes | Yes | Yes | Yes | Implemented | Add per-transport edge-case tests and WPF action |
| 23 | Read/Write Multiple Registers | Atomic write then read | Yes | Yes | Yes | Yes | Yes | Implemented | Add examples and WPF action |
| 24 | Read FIFO Queue | FIFO register queue | No | No | No | No | No | Needs Work | Add parser/model first |
| 43/14 | Read Device Identification | Vendor/model/firmware | No | No | No | No | No | Needs Work | High-value next API for diagnostics |
| User-defined | Vendor custom function codes | `CustomModbus` style APIs exist in some clients | No | Parser preserves data | Some UDP/RTU-over-TCP tests | Basic | Partial | Normalize custom PDU API across all transports |

## Data Type Scope

| Data shape | TCP | UDP | RTU | ASCII | RTU-over-TCP | Status | Next action |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Bool single | Yes | Yes | Yes | Yes | Yes | Implemented | Real-device validation |
| Bool array | Yes | Yes | Yes | Yes | Yes | Implemented | Per-transport tests for bit packing |
| Int16/UInt16 | Yes | Yes | Yes | Yes | Yes | Implemented | Real-device validation |
| Int32/UInt32 | Yes | Yes | Yes | Yes | Yes | Implemented | Byte-order examples |
| Int64/UInt64 | Yes | Yes | Yes | Yes | Yes | Implemented | Confirm write parity per transport |
| Float | Yes | Yes | Yes | Yes | Yes | Implemented | Byte-order field tests |
| Double | Yes | Yes | Yes | Yes | Yes | Implemented | Byte-order field tests |
| Raw bytes | Yes | Yes | Yes | Yes | Yes | Implemented | Clarify byte-count vs register-count rules |
| ASCII string | Yes | Yes | Yes | Yes | Yes | Implemented | Add examples per encoding |
| Encoded string | Yes for TCP/UDP and some transports | Yes | Needs audit | Needs audit | Yes | Partial | Normalize `ReadStringEncoded`/`WriteStringEncoded` across transports |
| Structured object mapping | Core helpers exist | Core helpers exist | Core helpers exist | Core helpers exist | Core helpers exist | Needs Work | Add Modbus struct mapping examples |

## Address Scope

| Address form | Meaning | Current state | Gap |
| --- | --- | --- | --- |
| `00001` | Coil offset 0, FC01/05/15 | Supported by clients | Add more tests for 1-based conversion edge cases |
| `10001` | Discrete input offset 0, FC02 | Supported by clients | Add read-only write-failure tests |
| `30001` | Input register offset 0, FC04 | Supported by clients | Add read-only write-failure tests |
| `40001` | Holding register offset 0, FC03/06/16 | Supported by clients | Add consistency tests between parser and clients |
| `0`, `1`, `100` | Direct holding-register offset | Supported by clients | Document migration choice clearly |
| Extended context, e.g. `s=2;40001` | Per-address station override | Core `AddressContext` exists | Not integrated in Modbus clients |
| Typed suffix, e.g. `40001:int32` | Address-level type hint | Not implemented | Candidate for WPF/monitor convenience, not core API yet |

## Server And Simulator Scope

| Capability | Current state | Gap |
| --- | --- | --- |
| TCP listener | Implemented | Add long-run and concurrent client tests |
| Holding/input register memory | Implemented and editable in WPF Server page | Add configurable size and persistence examples |
| Coil/discrete memory | Implemented and editable in WPF Server page | Add configurable size and persistence examples |
| Write callbacks | `ModbusVirtualServer` has callback support | Standardize callback/event behavior between server classes |
| Request log | WPF Server page logs unit, function code, and PDU | Add filter/export |
| Exception responses | Basic | Add full exception-code docs and tests |
| FC22 | Implemented in `ModbusTcpServer` and WPF simulator | Add to `ModbusVirtualServer` if useful |
| FC23 | Implemented in `ModbusTcpServer` and WPF simulator | Add to `ModbusVirtualServer` if missing |
| UDP server | Test-only | Create reusable public simulator if useful |
| RTU/ASCII serial simulator | None | Add fake serial simulator for docs/tests |
| Replay captured packets | None | Future differentiator |

## WPF Tooling Scope

| Tooling item | Current state | Gap |
| --- | --- | --- |
| Raw TX/RX display | Present on Modbus TCP and serial/network Modbus client pages | Generalize parsed packet style across pages |
| Parsed Modbus TCP summary | Present in `ModbusTcpPage` | Extend structured packet decode to UDP/RTU/ASCII/RTU-over-TCP pages |
| Network Modbus client pages | TCP, UDP, RTU-over-TCP, ASCII-over-TCP pages have connection/read/write/log bindings | Add packet JSONL export outside TCP |
| Serial Modbus client pages | RTU and ASCII pages have serial-port configuration, connection, read/write, and log bindings | Add fake-serial runtime demos and packet export |
| JSONL export | Present for Modbus TCP packet records | Move into shared service |
| Diagnostic bundle | Not implemented | Export settings, logs, parsed packets, device metadata |
| Packet replay | Not implemented | Add replay runner against client/server |
| Exception-code explanation | Not implemented | Add lookup table and UI text |

## Test And Validation Scope

| Evidence | Current state | Required for "complete" |
| --- | --- | --- |
| Offline parser tests | 14 parser tests | Add FC07/08/20/21/24/43 when implemented |
| Transport integration tests | TCP/UDP/RTU/ASCII/RTU-over-TCP tests exist | Add per-transport FC22 edge cases and gateway notes |
| Server tests | TCP server tests exist | Add long-run, concurrent, exception, and callback tests |
| Benchmark | None | Add baseline for read/write/batch across TCP and UDP |
| Long-run | None | Add 8h or 24h stability plan and at least short automated soak |
| Real devices | None recorded | Add rows for at least one TCP device, one RTU device, one gateway |

## Priority Backlog

1. Add FC43/14 read device identification for diagnostics.
2. Add FC08 diagnostics model and loopback/counter tests.
3. Add exception-code lookup and WPF explanation text.
4. Add shared WPF packet recorder/export service.
5. Add real-device validation rows and packet captures.
6. Add benchmark and long-run documentation.
