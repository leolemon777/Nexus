# Nexus.Fatek

Fatek (永宏) FBs series protocol client for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Fatek\Nexus.Fatek.csproj" />
```

## Quick Start

```csharp
using Nexus.Fatek;

using var client = new FatekClient("192.168.1.100", port: 5000, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var result = client.ReadInt16("R100");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
else
    Console.WriteLine(result.Message);
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `FatekClient` | TCP / Stream | FBs series, IBatchReadWrite |

## Supported Features

- Read/write for R, D, X, Y, M, T, C registers.
- Batch read/write through `IBatchReadWrite`.
- ASCII-based protocol with STX/ETX framing.
- Configurable station number.
- Virtual server for integration testing (60 tests).
- Supports both TCP and direct stream connections.

## Address Format

- `R100`, `D100`, `X0`, `Y0`, `M100`, `T100`, `C100`

## Production Readiness

Solid test coverage with virtual server. FBs series is popular in small-machine automation.
