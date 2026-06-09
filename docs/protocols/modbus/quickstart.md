# Modbus Quickstart

## TCP Client

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("127.0.0.1", port: 502, station: 1, timeout: 3000);

var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var read = client.ReadInt16("40001");
if (read.IsSuccess)
    Console.WriteLine(read.Content);
else
    Console.WriteLine(read.Message);

var write = client.Write("40001", (short)1234);
if (!write.IsSuccess)
    Console.WriteLine(write.Message);
```

## TCP Server For Local Testing

```csharp
using Nexus.Modbus;

using var server = new ModbusTcpServer(port: 15020);
server.SetHoldingRegister(0, 0x1234);
server.SetCoil(0, true);
server.Start();

using var client = new ModbusTcpClient("127.0.0.1", 15020, station: 1);
var connect = client.Connect();
var value = client.ReadInt16("40001");
```

## Byte Order

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100");
client.ByteOrder = Endianness.MidLittleEndian; // CDAB
```

## Batch Read

`ModbusTcpClient` and `ModbusUdpClient` implement `IBatchReadWrite`.

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100");
client.Connect();

IBatchReadWrite batch = client;
var result = batch.BatchRead(new[] { "40001", "40002", "00001" });
if (result.IsSuccess)
{
    foreach (var item in result.Content)
        Console.WriteLine($"{item.Key} = {item.Value}");
}
```

## Polling Subscription

`ModbusTcpClient` and `ModbusUdpClient` implement `ISubscribeDevice` through polling.

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100");
client.Connect();

ISubscribeDevice sub = client;
sub.OnDataChanged += (_, e) =>
{
    Console.WriteLine($"{e.Address}: {e.OldValue} -> {e.NewValue}");
};

sub.Subscribe("40001", intervalMs: 1000, dataType: "Int16");
sub.StartSubscriptions(globalIntervalMs: 500);
```

## RTU And ASCII

Serial clients use the `ISerialPort` abstraction from `Nexus.Core`, not `System.IO.Ports.SerialPort` directly. Application layers should provide an adapter.

```csharp
using Nexus;
using Nexus.Modbus;

ISerialPort port = CreateSerialPortAdapter(); // app-specific adapter
using var rtu = new ModbusRtuClient(port, station: 1, timeout: 3000);

var read = rtu.ReadInt16("40001");
```
