# Nexus.Siemens

Siemens S7, Fetch/Write, and PPI protocol clients for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Siemens\Nexus.Siemens.csproj" />
```

## Quick Start

```csharp
using Nexus.Siemens;

using var client = new SiemensS7Client("192.168.1.100", rack: 0, slot: 1);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var result = client.ReadInt16("DB1.DBW0");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
else
    Console.WriteLine(result.Message);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `SiemensS7Client` | TCP | S7-1200/1500/300/400, rack/slot routing, IBatchReadWrite |
| `SiemensFetchWriteClient` | TCP | Fetch/Write protocol for S7-200/300/400 |
| `SiemensPpiClient` | Serial | PPI protocol for S7-200 (partial) |

## Supported Features

- S7 read/write: DB, I, Q, M, T, C areas.
- S7 String and WString data types.
- Batch read/write through `IBatchReadWrite`.
- PLC control commands: Start, Stop, Hot Restart.
- S7 multi-step handshake with automatic connection lifecycle.
- Configurable rack/slot, connection type, and PDU size.
- S7 Keep-alive and timeout management.
- Fetch/Write: word and byte access to DB/I/Q/M/T/C areas.

## Address Format

- S7: `DB1.DBW0`, `DB1.DBD4`, `DB1.DBX0.0`, `M100`, `I0.0`, `Q0.0`
- Fetch/Write: `DB=1.W0`, `M100`, `I0`, `Q0`

## More Documentation

See:

- `docs/protocols/siemens/s7.md`
- `docs/protocols/siemens/setup.md`
- `docs/protocols/siemens/reconnect-heartbeat.md`
- `docs/protocols/siemens/ppi-audit.md`

## Production Readiness

S7 client is a top-priority protocol with comprehensive test coverage and virtual server support. Record real-device validation in `REAL_DEVICE_VALIDATION.md`.
