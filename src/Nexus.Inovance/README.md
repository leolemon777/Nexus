# Nexus.Inovance

Inovance (汇川) EasyNet protocol client for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Inovance\Nexus.Inovance.csproj" />
```

## Quick Start

```csharp
using Nexus.Inovance;

using var client = new InovanceEasyClient("192.168.1.100", port: 502);
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
| `InovanceEasyClient` | TCP | EasyNet protocol for H3U/AM/H5U series |

## Supported Features

- Read/write for D, M, X, Y, C, T registers.
- H3U, AM, and H5U PLC series support.
- Configurable station number and timeout.
- Virtual server for integration testing (83 tests).
- Chinese factory deployment common use case.

## Address Format

- `D100`, `M100`, `X0`, `Y0`, `C100`, `T100`

## Production Readiness

Strong test coverage with virtual server. Popular in Chinese factory deployments.
