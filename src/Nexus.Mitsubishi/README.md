# Nexus.Mitsubishi

Mitsubishi MC3E Binary/ASCII/UDP, A1E, and FX Serial protocol clients for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Mitsubishi\Nexus.Mitsubishi.csproj" />
```

## Quick Start

```csharp
using Nexus.Mitsubishi;

using var client = new Mc3EBinaryClient("192.168.1.100", port: 5007);
client.Station = 0;
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var result = client.ReadInt16("D100");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
else
    Console.WriteLine(result.Message);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `Mc3EBinaryClient` | TCP | MC3E Binary (SLMP), Q/L/FX5U series, IBatchReadWrite |
| `Mc3EAsciiClient` | TCP | MC3E ASCII frame encoding |
| `Mc3EUdpClient` | UDP | MC3E over UDP |
| `MelsecA1EClient` | TCP | A1E protocol, older Q/A series, IBatchReadWrite |
| `FxSerialClient` | Serial | FX series programming port protocol |
| `FxLinkClient` | Serial | FX computer link protocol |

## Supported Features

- MC3E Binary: full read/write for D, M, X, Y, R, ZR, B, W, TN, CN, STN areas.
- Batch and random read/write through `IBatchReadWrite`.
- Word and bit-level address parsing with sub-address support.
- Multiple transport modes: TCP Binary, TCP ASCII, UDP, A1E TCP.
- FX Serial: programming port and computer link (RS-232/RS-485).
- Virtual server for integration testing.

## Address Format

- MC3E: `D100`, `M100`, `X0`, `Y10`, `ZR100`, `W100`, `B100`
- A1E: `D100`, `M100`, `X0`, `Y10`
- FX: `D100`, `M100`, `X0`, `Y0`, `C100`, `T100`

## More Documentation

See:

- `docs/protocols/mitsubishi/complete-scope.md`
- `docs/protocols/mitsubishi/support-matrix.md`
- `docs/protocols/mitsubishi/hsl-migration.md`

## Production Readiness

MC3E Binary client has comprehensive test coverage (257 tests) and virtual server support. ASCII and UDP modes are actively being improved.
