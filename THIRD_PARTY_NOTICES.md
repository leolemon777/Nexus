# Third-Party Notices

This document tracks all third-party software referenced, adapted, or redistributed
by the Nexus Industrial Communication Library. See `NOTICE` for the canonical
attribution block and `LICENSE` for Nexus's own license (MIT).

## 1. HslCommunication

- **Project**: HslCommunication — Industrial IoT communication framework for .NET
- **Upstream**: https://github.com/GitHslAmateur/HslCommunication
- **Version referenced**: v12.2.0
- **Copyright**: © Richard.Hu 2017-2025 (杭州胡工物联科技有限公司)
- **License**: MIT
- **Usage**: Protocol message-flow designs, address-parsing logic, and selected
  infrastructure abstractions (`CommunicationPipe`, `INetMessage`,
  `DeviceCommunication`, `DeviceServer`) are adapted and rewritten into Nexus.
  No HSL binary (`HslCommunication.dll`, `HslCommunicationDemo.exe`,
  `HslControls.dll`) is redistributed by Nexus.
- **Attribution marker**: Source files with substantive HSL-derived logic carry the
  header comment:
  `// Derived from HslCommunication (MIT, Copyright © Richard.Hu 2017-2025). See NOTICE.`

## 2. Build-time / runtime dependencies

The Nexus solution targets `netstandard2.0` for protocol libraries and
`net8.0-windows` for the WPF debugger app. The following packages are consumed
via NuGet:

| Package | License | Purpose |
|---|---|---|
| `MinVer` 5.0.0 | Apache-2.0 | Git-tag-based versioning (build-time only, `PrivateAssets=All`) |
| `Microsoft.NET.Test.Sdk` | MIT | xUnit test host |
| `xunit` / `xunit.runner.visualstudio` | Apache-2.0 | Test framework |
| `CommunityToolkit.Mvvm` | MIT | WPF ViewModel source generators |
| `Microsoft.Extensions.Hosting` / `.Options` / `.Logging` | MIT | DI & configuration in `Nexus.App` |
| `BenchmarkDotNet` | MIT | `Nexus.Benchmarks` micro-benchmarks |

(Exact versions are pinned in the individual `.csproj` files. Run
`dotnet list package --include-transitive` for a live dependency tree.)

## 3. Protocol specifications

Nexus implements open, published industrial protocol specifications. The
specifications themselves are not software and are owned by their respective
standards bodies. Implementations in Nexus are original unless a file header
says otherwise (per Section 1).

| Protocol | Standards body / spec owner |
|---|---|
| Modbus (TCP/RTU/ASCII) | Modbus Organization / Schneider Electric (spec is public) |
| PROFIBUS / PROFINET | PI (PROFIBUS & PROFINET International) |
| EtherCAT | ETG (EtherCAT Technology Group) |
| CIP / EtherNet/IP | ODVA |
| CC-Link / CC-Link IE | CLPA (CC-Link Partner Association) |
| BACnet | ASHRAE (ANSI/ASHRAE 135) |
| IEC 60870-5-101/103/104 | IEC TC 57 |
| IEC 61850 | IEC TC 57 |
| DNP3 | IEEE 1815 |
| OPC UA | OPC Foundation (IEC 62541) |
| SECS/GEM (HSMS, SECS-I/II) | SEMI (SEMI E5 / E30 / E37) |
| MQTT 5.0 / 3.1.1 | OASIS |
| AMQP 1.0 | OASIS |
| CoAP | IETF RFC 7252 |
| HART / WirelessHART | FieldComm Group |
| CANopen | CiA (CAN in Automation) |
| DeviceNet | ODVA |
| AS-Interface | AS-International |
| Foundation Fieldbus H1 | FieldComm Group |
| LonWorks / ANSI/CEA-709.1 | LonMark International |
| HDLC, BSPC, DF1, MPI, PPI, S7, S7-Plus | Siemens AG (public specs) |

Vendor-specific protocols (Mitsubishi MELSEC MC/A1E/FX, Omron FINS/HostLink,
Allen-Bradley PCCC, Yaskawa Memobus, etc.) are documented in the respective
vendor manuals; Nexus implementations are original works based on those public
manuals, except where a file header references HSL per Section 1.

## Updating this file

Whenever you:

- bring in new code from HSL, or
- add a new third-party NuGet package, or
- reference a new protocol specification under a non-permissive license,

you **must** update this file in the same PR.
