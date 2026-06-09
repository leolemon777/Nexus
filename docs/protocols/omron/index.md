# Nexus Omron

Nexus Omron covers FINS TCP/UDP and HostLink TCP/Serial protocols for Omron PLC families: CJ, CP, NJ, NX, and CS series.

## Clients

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `FinsTcpClient` | TCP | `TcpDeviceBase` | 9600 | Primary FINS client. Supports `IBatchReadWrite`. |
| `FinsUdpClient` | UDP | `UdpDeviceBase` | 9600 | FINS over UDP. Supports `IBatchReadWrite` and device discovery. |
| `FinsSerialClient` | Serial (`Stream`) | `IReadWriteDevice` | — | FINS over serial (RS-232/RS-485). |
| `OmronHostLinkClient` | TCP | `TcpDeviceBase` | 9600 | HostLink protocol over Ethernet. Supports `IBatchReadWrite`. |
| `OmronHostLinkSerialClient` | Serial | `SerialDeviceBase` | — | HostLink protocol over serial. |
| `FinsVirtualServer` | TCP server | standalone | 9600 | Virtual PLC for integration testing. |
| `OmronHostLinkVirtualServer` | TCP server | standalone | configurable | HostLink virtual server for testing. |

## Feature Summary

| Feature | FINS TCP | FINS UDP | FINS Serial | HostLink TCP | HostLink Serial |
|---------|----------|----------|-------------|--------------|-----------------|
| Memory area read/write | Yes | Yes | Yes | Yes | Yes |
| `IBatchReadWrite` | Yes | Yes | No | Yes | No |
| Controller status read | Yes | Yes | No | No | No |
| Time read/write | Yes | Yes | No | No | No |
| Run/Stop control | Yes | Yes | No | No | No |
| Device discovery | No | Yes | No | No | No |
| String encoding options | Yes | Yes | No | Yes | Yes |
| Virtual server | Yes | No | No | Yes | No |

## Quick Start

### FINS TCP

```csharp
using Nexus.Omron;

using var client = new FinsTcpClient("192.168.1.10", 9600);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Read DM100 as Int16
var value = client.ReadInt16("D100");
if (value.IsSuccess)
    Console.WriteLine($"DM100 = {value.Content}");

// Write DM200
client.Write("D200", (short)1234);
```

### HostLink TCP

```csharp
using Nexus.Omron;

using var client = new OmronHostLinkClient("192.168.1.10", 9600);
client.Connect();

var value = client.ReadInt16("D100");
```

## Supported PLC Models

| Model | Series | Typical Transport |
|-------|--------|-------------------|
| CJ2M | CJ | FINS TCP/UDP, HostLink |
| CJ2H | CJ | FINS TCP/UDP, HostLink |
| CP1H | CP | FINS Serial, HostLink Serial |
| CP1L | CP | FINS Serial, HostLink Serial |
| CP1E | CP | FINS Serial, HostLink Serial |
| NJ501 | NJ | FINS TCP/UDP |
| NJ101 | NJ | FINS TCP/UDP |
| NX701 | NX | FINS TCP/UDP |
| NX102 | NX | FINS TCP/UDP |
| CS1G | CS | FINS TCP, HostLink |
| CS1H | CS | FINS TCP, HostLink |

## Related Pages

- [Address Format](address-format.md)
- [FINS Setup](fins-setup.md)
- [HostLink Coverage](hostlink-coverage.md)
- [Troubleshooting](troubleshooting.md)
