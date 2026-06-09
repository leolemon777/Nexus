# Nexus.Modbus

Modbus TCP, UDP, RTU, ASCII, ASCII-over-TCP, and RTU-over-TCP clients for Nexus.

## Install

NuGet packaging is planned for the open-source release. Until package publishing is wired, reference the project from `Nexus.slnx`.

```xml
<ProjectReference Include="..\Nexus.Modbus\Nexus.Modbus.csproj" />
```

## Quick Start

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", port: 502, station: 1);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var result = client.ReadInt16("40001");
if (result.IsSuccess)
    Console.WriteLine(result.Content);
else
    Console.WriteLine(result.Message);
```

## Supported Transports

| Client | Transport |
|--------|-----------|
| `ModbusTcpClient` | Modbus TCP |
| `ModbusUdpClient` | Modbus UDP |
| `ModbusRtuClient` | Modbus RTU over serial |
| `ModbusAsciiClient` | Modbus ASCII over serial |
| `ModbusAsciiOverTcpClient` | Modbus ASCII frames over TCP |
| `ModbusRtuOverTcpClient` | RTU ADU frames over TCP |

## Supported Features

- FC01, FC02, FC03, FC04, FC05, FC06, FC15, FC16, FC22, FC23.
- Standard address prefixes: `0xxxx`, `1xxxx`, `3xxxx`, `4xxxx`.
- `Endianness` options for multi-register values.
- Encoded string helpers.
- TCP and UDP batch read/write through `IBatchReadWrite`.
- TCP and UDP polling subscriptions through `ISubscribeDevice`.
- Custom Modbus PDU sends.
- Raw TX/RX events for diagnostics.
- Local TCP server utilities for tests and debugging.

## More Documentation

See:

- `docs/protocols/modbus/quickstart.md`
- `docs/protocols/modbus/complete-scope.md`
- `docs/protocols/modbus/address-format.md`
- `docs/protocols/modbus/function-codes.md`
- `docs/protocols/modbus/byte-order.md`
- `docs/protocols/modbus/packet-logging.md`
- `docs/protocols/modbus/troubleshooting.md`

## Production Readiness

`Nexus.Modbus` is the first reference package candidate. Before public production claims, record real-device validation in `REAL_DEVICE_VALIDATION.md` and keep packet logs for any field issue.
