# Nexus

Open-source industrial communication library for .NET.

Nexus provides a common `IReadWriteDevice` API, `OperateResult<T>` error handling, protocol clients, virtual servers, tests, and a WPF debugging app for PLC/industrial communication work.

## Current Support Status

Nexus is intentionally split into support tiers. Do not treat every project folder or WPF navigation entry as production-ready protocol support.

| Tier | Status | Examples |
|------|--------|----------|
| Production-oriented | Deep implementation and broad tests | `Nexus.Modbus`, Siemens S7 |
| Usable with validation | Protocol-specific implementation and focused tests, but still needs broader real-device evidence | Mitsubishi MC3E/A1E/FX, Omron FINS/HostLink, Allen-Bradley CIP/PCCC, Panasonic, Keyence, Beckhoff, LS Electric, Delta, Fuji, Fatek, Schneider, DNP3, IEC104, IEC61850, BACnet/IP, MQTT, Redis, SECS, selected robot/instrument clients |
| Experimental / mapping | Early scaffolds, vendor Modbus mappings, or compatibility facades | PROFINET, EtherCAT, POWERLINK, Sercos, CC-Link IE, cloud IoT, vehicle diagnostics, medical/finance/video protocols, and other newly scaffolded pages until they gain protocol-specific frames, docs, and tests |

For release decisions, prefer `PROTOCOL_READINESS.md`, protocol docs under `docs/`, and the relevant test project over package count.

## Features

- Unified read/write API via `IReadWriteDevice`
- `OperateResult<T>` return model instead of exception-driven device calls
- TCP, UDP, and serial base classes
- Modbus TCP/RTU/ASCII/UDP support with diagnostics and packet tooling
- Virtual servers for no-hardware integration testing
- WPF debugger for common protocol read/write workflows
- netstandard2.0 protocol libraries plus a net8.0-windows WPF app

## Install

```bash
dotnet add package Nexus.Modbus
dotnet add package Nexus.Siemens
dotnet add package Nexus.Mitsubishi
```

## Quick Start

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var read = client.ReadInt16("40001");
if (read.IsSuccess)
    Console.WriteLine(read.Content);

client.Write("40001", (short)1234);
client.Disconnect();
```

## Build And Test

```bash
dotnet build Nexus.slnx
dotnet test Nexus.slnx
dotnet test tests/Nexus.Modbus.Tests
dotnet run --project src/Nexus.App
```

The solution file is `Nexus.slnx`, not `.sln`.

## Repository Layout

```text
src/Nexus.Core/          common interfaces, base classes, converters
src/Nexus.Modbus/        Modbus TCP/RTU/ASCII/UDP clients and servers
src/Nexus.{Protocol}/    protocol-specific libraries
src/Nexus.App/           WPF debugger app
tests/                   xUnit tests and virtual-server integration tests
docs/                    protocol and operations documentation
examples/                runnable examples
```

## Contributing

Before promoting a protocol to supported status, add protocol-specific frame construction/parsing, offline tests, and either virtual-server or real-device validation notes. Avoid presenting a Modbus mapping wrapper as native support for a different fieldbus.

See `CONTRIBUTING.md` and `CONTRIBUTING_PROTOCOLS.md`.

## License

MIT. See `LICENSE`.
