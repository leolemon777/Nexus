# HSL Migration Guide

> Draft outline for moving from HslCommunication-style usage to Nexus.
>
> Last calibrated: 2026-06-08.

## Goal

Make migration predictable for users who already have HSL-based PLC communication code. Nexus should not copy HSL code, but it can provide compatible concepts, clear mapping tables, and examples for rewriting applications.

## Core Concept Mapping

| HSL Concept | Nexus Equivalent | Notes |
|-------------|------------------|-------|
| Operation result object | `OperateResult` / `OperateResult<T>` | Same broad pattern: check success before reading content. |
| PLC client object | `Nexus.{Protocol}` client | Example: `ModbusTcpClient`, `SiemensS7Client`, `Mc3EBinaryClient`. |
| Address string | Protocol-specific address parser | Nexus keeps string addresses but needs protocol docs per family. |
| Byte transform / data format | `Endianness`, `DataConverter`, per-client byte-order options | Needs examples for ABCD/DCBA/BADC/CDAB. |
| Batch read | `IBatchReadWrite` | Implemented for the main protocol families, but feature depth varies. |
| Data subscription/monitor | `ISubscribeDevice` or WPF monitor services | Narrower than batch support today. |
| Logging hooks | `ILogger`, TX/RX events | Use for diagnostics and packet recorder work. |

## General Migration Pattern

1. Replace HSL package references with the matching Nexus package once NuGet packages are available.
2. Replace client construction.
3. Keep address strings where compatible; normalize where Nexus uses stricter syntax.
4. Replace result checks with `OperateResult<T>.IsSuccess` and `Message`.
5. Replace batch/subscribe APIs only after confirming the module implements the relevant Nexus interface.
6. Run against a virtual server where available.
7. Validate against real hardware and record the result in `REAL_DEVICE_VALIDATION.md`.

## Example: Modbus TCP

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
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
```

## Modbus Migration Details

This section is clean-room guidance for rewriting Modbus usage from an HSL-style application into Nexus. It maps concepts and call patterns only; do not copy HSL source code or long-form HSL documentation text into this project.

### Client Selection

| Existing Modbus Need | Nexus Client | Transport Notes |
|----------------------|--------------|-----------------|
| Modbus TCP over Ethernet | `ModbusTcpClient` | Use for ordinary TCP devices on port 502 or vendor-specific TCP ports. Supports `IBatchReadWrite` and polling subscription. |
| Modbus RTU over a serial line | `ModbusRtuClient` | Requires an `ISerialPort` implementation because Nexus protocol libraries target `netstandard2.0`. |
| Modbus ASCII over a serial line | `ModbusAsciiClient` | Also uses `ISerialPort`; configure serial framing in the adapter, not in the protocol client. |
| Modbus UDP | `ModbusUdpClient` | Use when the device manual explicitly says UDP. Supports `IBatchReadWrite` and polling subscription. |
| RTU frame carried through TCP | `ModbusRtuOverTcpClient` | Use for serial-to-Ethernet gateways that forward RTU frames without a Modbus TCP MBAP header. |

For TCP-style transports, migrate host, port, station/unit id, timeout, byte order, and connection lifetime first. For serial transports, migrate baud rate, parity, data bits, stop bits, and timeout into your `ISerialPort` adapter, then pass that adapter into the Nexus client.

```csharp
using Nexus.Modbus;

using var tcp = new ModbusTcpClient("192.168.1.100", port: 502, station: 1, timeout: 5000);
using var udp = new ModbusUdpClient("192.168.1.100", port: 502, station: 1, timeout: 5000);
using var gateway = new ModbusRtuOverTcpClient("192.168.1.200", port: 502, station: 1);
```

```csharp
using Nexus;
using Nexus.Modbus;

ISerialPort serialPort = CreateSerialPort(); // Your application adapter.
using var rtu = new ModbusRtuClient(serialPort, station: 1, timeout: 5000);

// For ASCII devices, create the matching serial adapter and choose this client instead:
// using var ascii = new ModbusAsciiClient(serialPort, station: 1, timeout: 5000);
```

### Address Format Migration

Nexus accepts string addresses and routes them to Modbus function codes from the address prefix. The public client methods treat standard 5-digit Modbus addresses as 1-based and convert them to the 0-based wire offset internally.

| Data Area | Standard Address Examples | Nexus Read API | Write Support |
|-----------|---------------------------|----------------|---------------|
| Coils | `00001`, `00010` | `ReadBool`, `ReadBools` with FC01 | `Write(address, bool)`, multiple coils where available |
| Discrete inputs | `10001`, `10010` | `ReadBool`, `ReadBools` with FC02 | Read-only |
| Input registers | `30001`, `30010` | Numeric/string/bytes reads with FC04 | Read-only |
| Holding registers | `40001`, `40010` | Numeric/string/bytes reads with FC03 | Numeric/string/bytes writes with FC06/FC16 |

Important address rules:

- `40001` means holding register offset `0`; `40002` means offset `1`.
- `00001` means coil offset `0`; `10001` means discrete input offset `0`; `30001` means input register offset `0`.
- Short addresses without a standard prefix, such as `0`, `1`, or `100`, are treated as direct holding-register offsets.
- If existing HSL-style code mixed `40001` and `0` for the same device, normalize the application to one style before field validation.
- `ModbusAddressParser` is a standalone parser utility; validate migration behavior through the concrete client methods because client address parsing is what sends frames.

### Read/Write API Mapping

Nexus exposes typed reads and writes through `IReadWriteDevice`-style methods. The common migration pattern is: call a typed method, check `IsSuccess`, then read `Content`. Never read `Content` on failure.

| Value Shape | Nexus Read | Nexus Write | Notes |
|-------------|------------|-------------|-------|
| Single bit | `ReadBool("00001")` / `ReadBool("10001")` | `Write("00001", true)` | Use coil addresses for writes; discrete inputs are read-only. |
| 16-bit integer | `ReadInt16`, `ReadUInt16` | `Write(address, short)` / `Write(address, ushort)` | One register. |
| 32-bit integer | `ReadInt32`, `ReadUInt32` | `Write(address, int)` / `Write(address, uint)` | Two registers; set `ByteOrder` for word/byte order. |
| 64-bit integer | `ReadInt64`, `ReadUInt64` | Available overloads may narrow in some transports; verify before depending on 64-bit writes. | Four registers. |
| Floating point | `ReadFloat`, `ReadDouble` | `Write(address, float)` / `Write(address, double)` | `double` support may be transport-specific; validate on target device. |
| Raw bytes | `ReadBytes(address, length)` | `Write(address, byte[])` | `length` is byte count in public API. |
| String | `ReadString(address, length)` / `ReadStringEncoded` | `Write(address, string)` / `WriteStringEncoded` | Use encoded variants when device text encoding matters. |

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1)
{
    ByteOrder = Endianness.MidBigEndian
};

OperateResult<short> speed = client.ReadInt16("40001");
if (!speed.IsSuccess)
{
    Console.WriteLine(speed.Message);
    return;
}

OperateResult write = client.Write("40002", (short)(speed.Content + 10));
if (!write.IsSuccess)
    Console.WriteLine(write.Message);
```

### Endianness

HSL-style applications often configure a byte transform or data format per client. In Nexus Modbus clients, use `ByteOrder`:

| Nexus `Endianness` | Register Byte Order | Typical Meaning |
|--------------------|---------------------|-----------------|
| `BigEndian` | `ABCD` | Modbus default for multi-register values. |
| `LittleEndian` | `DCBA` | Full byte reversal. |
| `MidBigEndian` | `BADC` | Byte swap inside each 16-bit register. |
| `MidLittleEndian` | `CDAB` | Word swap. |

Set this before typed multi-register reads/writes (`ReadInt32`, `ReadFloat`, `ReadDouble`, and matching writes). Single-register values are only affected by byte order where the client has explicit 16-bit byte swapping.

### Batch And Random Reads

For TCP and UDP, use `IBatchReadWrite` when existing code reads several addresses in one logical operation. Nexus groups compatible Modbus areas where it can, but callers should still expect an `OperateResult<Dictionary<string, object?>>` and handle failure as one operation.

```csharp
using System.Collections.Generic;
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);

IBatchReadWrite batch = client;
OperateResult<Dictionary<string, object?>> read = batch.BatchRead(new[]
{
    "00001",
    "40001",
    "40002"
});

if (read.IsSuccess)
{
    object value = read.Content["40001"];
    Console.WriteLine(value);

    OperateResult write = batch.BatchWrite(new[]
    {
        new KeyValuePair<string, object>("00001", true),
        new KeyValuePair<string, object>("40001", (short)120),
        new KeyValuePair<string, object>("40002", 123456)
    });

    if (!write.IsSuccess)
        Console.WriteLine(write.Message);
}
else
{
    Console.WriteLine(read.Message);
}
```

### Error Handling Migration

If existing code already follows an HSL-style result-object pattern, the control flow maps directly to Nexus:

- Use `OperateResult` for writes and connection operations.
- Use `OperateResult<T>` for typed reads.
- Check `IsSuccess` first.
- Use `Message` for diagnostics and `ErrorCode` where the protocol maps a Modbus exception code.
- Read `Content` only after success; numeric `Content` is a value type and should not use null-conditional access.

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);

OperateResult connected = client.Connect();
if (!connected.IsSuccess)
{
    Console.WriteLine("Connect failed: " + connected.Message);
    return;
}

OperateResult<float> pressure = client.ReadFloat("40010");
if (!pressure.IsSuccess)
{
    Console.WriteLine("Read failed: " + pressure.Message);
    return;
}

Console.WriteLine(pressure.Content);
```

### Field Validation Checklist For Modbus Ports

Before declaring an HSL-to-Nexus Modbus migration complete, validate these points with the user's real device:

- Station/unit id and timeout match the old deployment.
- `40001` versus `0` addressing returns the same register the user expects.
- Coil and discrete-input prefixes are not accidentally swapped.
- `ByteOrder` matches every 32-bit and 64-bit numeric value.
- String length and encoding match the PLC/device memory layout.
- Batch reads return the same values as individual reads for the same addresses.
- Device-specific Modbus exception codes are captured in `OperateResult.Message`/`ErrorCode`.

## Example: Siemens S7

```csharp
using Nexus.Siemens;

using var s7 = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.1.10");
var connect = s7.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var value = s7.ReadInt16("DB1.DBW0");
var text = s7.ReadS7String("DB1.DBB100");
```

## Protocol Mapping

| HSL Family | Nexus Module | Migration Status | Notes |
|------------|--------------|------------------|-------|
| Modbus TCP/RTU/ASCII/UDP | `Nexus.Modbus` | Reference candidate | First migration docs should be completed here. |
| Siemens S7 | `Nexus.Siemens` | Usable | S7 is strongest; PPI needs audit. |
| Mitsubishi MC/A1E/FX | `Nexus.Mitsubishi` / `Nexus.MitsubishiFx` | Usable/Needs Audit | MC family parity table needed. |
| Omron FINS/HostLink | `Nexus.Omron` | Usable | Routing and node setup docs needed. |
| AllenBradley CIP/PCCC | `Nexus.AllenBradley` | Usable | Tag workflow and PCCC scope docs needed. |
| Beckhoff ADS | `Nexus.Beckhoff` | Experimental | Test evidence is thin. |
| Panasonic Mewtocol | `Nexus.Panasonic` | Experimental | Batch exists, tests thin. |
| Keyence KV | `Nexus.Keyence` | Experimental | Batch exists, tests thin. |
| Yaskawa Memobus | `Nexus.Yaskawa` | Usable | Stronger B-tier candidate. |
| Yokogawa | `Nexus.Yokogawa` | Usable | Needs field validation. |
| OPC UA | `Nexus.OpcUa` | Experimental | No matching tests found in scan. |
| MQTT / Redis | `Nexus.Mqtt` / `Nexus.Redis` | Utility modules | Useful ecosystem modules, not PLC memory protocol replacements. |

## Compatibility Notes

- Nexus protocol libraries target `netstandard2.0`; avoid APIs unavailable on that target when adding examples.
- `OperateResult<T>.Content` is non-nullable in the type model, but callers must only read it when `IsSuccess` is true.
- Numeric `Content` is a value type; do not use null-conditional access on numeric results.
- WPF apps should use async APIs without `.Result` or `.Wait()`.
- Nexus is MIT-licensed and should remain clean-room. Do not copy HSL source code.

## Migration Checklist

| Step | Done | Notes |
|------|------|-------|
| Identify current HSL protocol families in the app | No | List every PLC/device and transport. |
| Pick Nexus module and client class | No | Use `PROTOCOL_READINESS.md` first. |
| Verify address syntax compatibility | No | Record required rewrites. |
| Port connection settings | No | Include station, slot, rack, timeout, byte order. |
| Port read/write calls | No | Preserve result checks. |
| Port batch calls | No | Confirm `IBatchReadWrite`. |
| Add packet logging | No | Required for field troubleshooting. |
| Run virtual-server tests | No | Where available. |
| Run real-device validation | No | Record in `REAL_DEVICE_VALIDATION.md`. |

## Open Questions

- Should Nexus provide adapter helpers for common HSL naming patterns, or only documentation?
- Which HSL APIs are most common in current user projects?
- Which migration examples should be written first: Modbus TCP, Siemens S7, Mitsubishi MC3E, Omron FINS, or AllenBradley CIP?
