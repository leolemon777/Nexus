# Nexus.AllenBradley

Allen-Bradley CIP (ControlLogix/CompactLogix) and PCCC (MicroLogix/SLC) protocol clients for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.AllenBradley\Nexus.AllenBradley.csproj" />
```

## Quick Start

```csharp
using Nexus.AllenBradley;

using var client = new AllenBradleyCipClient("192.168.1.100", slot: 0);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var result = client.ReadInt32("MyTag");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
else
    Console.WriteLine(result.Message);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `AllenBradleyCipClient` | TCP | CIP over EtherNet/IP, ControlLogix/CompactLogix, IBatchReadWrite |
| `PcccClient` | TCP | PCCC/DF1 over TCP, MicroLogix/SLC 500 |

## Supported Features

- CIP: tag-based read/write for DINT, INT, REAL, BOOL, STRING, and arrays.
- CIP path routing for multi-slot backplane configurations.
- Batch read/write through `IBatchReadWrite`.
- Tag browsing and UDT structure support (basic).
- PCCC: N, F, B, T, C, S, L data file access.
- PCCC addressing with data file number and element.
- Virtual servers for both CIP and PCCC integration testing.

## Address Format

- CIP: `MyTag`, `MyArray[0]`, `Program:Main.MyTag`, `MyDint[0].SubField`
- PCCC: `N7:0`, `F8:0`, `B3:0/0`, `T4:0.PRE`, `C5:0.ACC`

## More Documentation

See:

- `docs/protocols/allenbradley/cip-tag-syntax.md`
- `docs/protocols/allenbradley/udt-arrays.md`
- `docs/protocols/allenbradley/pccc-coverage.md`

## Production Readiness

CIP client has solid test coverage and virtual server support. Record real-device validation in `REAL_DEVICE_VALIDATION.md`.
