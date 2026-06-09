# Nexus.Omron

Omron FINS TCP/UDP/Serial and HostLink protocol clients for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Omron\Nexus.Omron.csproj" />
```

## Quick Start

```csharp
using Nexus.Omron;

using var client = new FinsTcpClient("192.168.1.100", port: 9600);
client.SA1 = 0; // Source node
client.DA1 = 1; // Destination node
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
| `FinsTcpClient` | TCP | FINS over TCP, CJ/CP/NJ/NX series, IBatchReadWrite |
| `FinsUdpClient` | UDP | FINS over UDP, IBatchReadWrite |
| `FinsSerialClient` | Serial | FINS over serial |
| `OmronHostLinkClient` | TCP | HostLink over TCP, C-series, IBatchReadWrite |
| `OmronHostLinkSerialClient` | Serial | HostLink over serial |

## Supported Features

- FINS TCP/UDP: read/write for CIO, WR, HR, AR, DM, EM areas.
- Configurable FINS routing: SA1/DA1 network and node addressing.
- Batch read/write through `IBatchReadWrite`.
- HostLink: C-mode command support for legacy PLCs.
- FINS end-code diagnostics with Chinese error messages.
- Virtual server for integration testing.

## Address Format

- FINS: `D100`, `CIO100`, `W100`, `H100`, `A100`, `E100_0`
- HostLink: `D100`, `CIO100`, `WR100`

## More Documentation

See:

- `docs/protocols/omron/fins-setup.md`
- `docs/protocols/omron/troubleshooting.md`
- `docs/protocols/omron/hostlink-coverage.md`
- `docs/protocols/omron/fins-coverage.md`

## Production Readiness

FINS TCP client has comprehensive test coverage and virtual server support. Record real-device validation in `REAL_DEVICE_VALIDATION.md`.
