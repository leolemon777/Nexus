# Nexus.Yaskawa

YASKAWA Memobus TCP protocol client for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Yaskawa\Nexus.Yaskawa.csproj" />
```

## Quick Start

```csharp
using Nexus.Yaskawa;

using var client = new MemobusClient("192.168.1.100", port: 502);
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
| `MemobusClient` | TCP | Memobus/YASKAWA Viper protocol, IBatchReadWrite |

## Supported Features

- Read/write for D, M, X, Y registers.
- Batch read/write through `IBatchReadWrite`.
- Configurable station number.
- Virtual server for integration testing (94 tests).
- Servo and inverter communication support.

## Address Format

- `D100`, `M100`, `X0`, `Y10`

## Production Readiness

Comprehensive test coverage with virtual server. Awaiting real-device validation for production claims.
