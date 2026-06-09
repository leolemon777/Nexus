# Nexus.Yokogawa

Yokogawa binary link protocol client for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Yokogawa\Nexus.Yokogawa.csproj" />
```

## Quick Start

```csharp
using Nexus.Yokogawa;

using var client = new YokogawaClient("192.168.1.100", port: 8000);
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
| `YokogawaClient` | TCP | Binary link protocol for Centum/Vnet DCS |

## Supported Features

- Read/write for digital and analog I/O areas.
- Configurable timeout and connection parameters.
- Virtual server for integration testing (82 tests).

## Address Format

- `D100`, `X0`, `Y10`

## Production Readiness

Good test coverage with virtual server. DCS environments require careful real-device validation.
