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

## Siemens Migration Details

This section maps HSL Siemens usage patterns to Nexus equivalents. Nexus supports S7, FetchWrite, and PPI protocols.

### Client Selection

| Existing Siemens Need | Nexus Client | Transport | Default Port | Notes |
|-----------------------|--------------|-----------|--------------|-------|
| S7-1200 / S7-1500 | `SiemensS7Client(S7_1200/S7_1500)` | TCP | 102 | Most common. Full S7 protocol. |
| S7-300 / S7-400 | `SiemensS7Client(S7_300/S7_400)` | TCP | 102 | Rack/Slot required. |
| S7-200 / S7-200 Smart | `SiemensS7Client(S7_200/S7_200Smart)` | TCP | 102 | Limited feature set. |
| FetchWrite (legacy) | `SiemensFetchWriteClient` | TCP | 102 | Older S7-300/400 without S7 routing. |
| PPI (S7-200 serial) | `SiemensPpiClient` | Serial | — | S7-200 point-to-point. |

```csharp
using Nexus.Siemens;

// S7-1200 / S7-1500
using var s71200 = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10");
using var s71500 = new SiemensS7Client(SiemensPLCS.S7_1500, "192.168.0.11");

// S7-300 (Rack=0, Slot=2 is default for S7-300)
using var s7300 = new SiemensS7Client(SiemensPLCS.S7_300, "192.168.0.20")
{
    Rack = 0,
    Slot = 2
};

// FetchWrite (legacy)
using var fw = new SiemensFetchWriteClient("192.168.0.30");
```

### Rack and Slot Configuration

S7 protocol uses Rack and Slot to address the PLC CPU within the backplane:

| PLC Series | Default Rack | Default Slot | Notes |
|------------|-------------|--------------|-------|
| S7-1200 | 0 | 1 | Fixed. |
| S7-1500 | 0 | 1 | Fixed. |
| S7-300 | 0 | 2 | Slot 2 is the CPU in standard rack. |
| S7-400 | 0 | 2 | May vary with H-system. |
| S7-200 Smart | — | — | Uses fixed TSAP, not Rack/Slot. |

```csharp
// Override Rack/Slot if needed
var client = new SiemensS7Client(SiemensPLCS.S7_300, "192.168.0.20")
{
    Rack = 0,
    Slot = 3  // Non-standard CPU position
};
```

### Address Format

Nexus S7 addresses follow standard S7 notation:

| Address | Area | Type | Description |
|---------|------|------|-------------|
| `DB1.DBW0` | DB | Word | Data Block 1, Word 0 |
| `DB1.DBD4` | DB | DWord | Data Block 1, DWord at byte 4 |
| `DB1.DBB8` | DB | Byte | Data Block 1, Byte 8 |
| `DB1.DBX12.0` | DB | Bit | Data Block 1, Byte 12, Bit 0 |
| `I0.0` | I (Input) | Bit | Input byte 0, bit 0 |
| `Q0.0` | Q (Output) | Bit | Output byte 0, bit 0 |
| `MW0` | M (Memory) | Word | Memory word 0 |
| `MD4` | M (Memory) | DWord | Memory double word 4 |
| `M8.0` | M (Memory) | Bit | Memory byte 8, bit 0 |

```csharp
using Nexus.Siemens;

using var client = new SiemensS7Client(SiemensPLCS.S7_1200, "192.168.0.10");
client.Connect();

// DB reads
short dbw0 = client.ReadInt16("DB1.DBW0").Content;
int dbd4 = client.ReadInt32("DB1.DBD4").Content;
float dbd8 = client.ReadFloat("DB1.DBD8").Content;
bool bit = client.ReadBool("DB1.DBX12.0").Content;

// Memory area reads
short mw0 = client.ReadInt16("MW0").Content;
bool m8 = client.ReadBool("M8.0").Content;

// I/O
bool input = client.ReadBool("I0.0").Content;
bool output = client.ReadBool("Q0.0").Content;

// Writes
client.Write("DB1.DBW0", (short)100);
client.Write("DB1.DBD4", 123456);
client.Write("DB1.DBD8", 3.14f);
client.Write("DB1.DBX12.0", true);
```

### S7 String Support

Nexus provides dedicated S7 string read/write methods that handle the S7 string header (max-length byte + actual-length byte):

```csharp
// Read S7 String (default max length 254)
var text = client.ReadS7String("DB1.DBB100");

// Write S7 String
client.WriteS7String("DB1.DBB100", "Hello World");

// Read S7 WString (wide/Unicode string)
var wideText = client.ReadS7WString("DB1.DBB200");

// Write S7 WString
client.WriteS7WString("DB1.DBB200", "你好世界");
```

**Important**: S7 String and WString have different header formats:
- **S7 String**: 1 byte max-length + 1 byte actual-length + ASCII data
- **S7 WString**: 2 bytes max-length + 2 bytes actual-length + UTF-16 data

### HSL-to-Nexus Migration Pattern

| HSL Pattern | Nexus Equivalent | Notes |
|-------------|------------------|-------|
| `SiemensS7Net(S71200, ip)` | `SiemensS7Client(S7_1200, ip)` | Different class name and enum naming. |
| `SiemensS7Net(S71500, ip)` | `SiemensS7Client(S7_1500, ip)` | Same. |
| `.ConnectServer()` | `.Connect()` | Different method name. |
| `.ConnectClose()` | `.Disconnect()` then `.Dispose()` | Nexus uses standard lifecycle. |
| `ReadInt16("DB1.DBW0")` | `ReadInt16("DB1.DBW0")` | Same address syntax. |
| `Write("DB1.DBW0", 100)` | `Write("DB1.DBW0", (short)100)` | Nexus requires explicit cast. |
| `ReadString("DB1.DBB100")` | `ReadS7String("DB1.DBB100")` | Different method name for S7 strings. |
| `Write("DB1.DBB100", "Hello")` | `WriteS7String("DB1.DBB100", "Hello")` | Use dedicated S7 string methods. |

### TIA Portal Configuration Checklist

Before connecting Nexus to a Siemens PLC, verify these TIA Portal settings:

1. **Enable PUT/GET**: Properties → Protection → "Permit access with PUT/GET communication" must be checked.
2. **Disable optimized block access** (for standard DB reads): In DB properties, uncheck "Optimized block access". Only non-optimized DBs are accessible via S7 protocol.
3. **Connection resources**: Ensure enough connection resources are available (S7-1200: typically 3-8).
4. **IP address**: Verify the PLC's IP is in the same subnet.
5. **Port**: Default is 102; almost never changed.

### Virtual Server Testing

`SiemensS7VirtualPlc` provides offline integration testing:

```csharp
using var server = new SiemensS7VirtualPlc(11102);
server.Start();

using var client = new SiemensS7Client(SiemensPLCS.S7_1200, "127.0.0.1", 11112);
client.Connect();

client.Write("DB1.DBW0", (short)42);
var result = client.ReadInt16("DB1.DBW0");
```

### Field Validation Checklist for Siemens

Before declaring an HSL-to-Nexus Siemens migration complete:

- [ ] PLC model enum matches (S7_1200, S7_1500, S7_300, etc.)
- [ ] Rack and Slot are correct for the PLC type
- [ ] DB address uses non-optimized block access
- [ ] PUT/GET is enabled in TIA Portal protection settings
- [ ] S7 String/WString methods are used for text data (not generic ReadString)
- [ ] Persistent connection mode is used (`SetPersistentConnection()`)
- [ ] Reconnect guard is configured with longer delays than Modbus (S7 handshake is heavier)
- [ ] Timeout accounts for PLC scan cycle impact on response time

```csharp
using Nexus.Mitsubishi;

// MC3E Binary — 最常用的三菱以太网协议，端口 6000 (FX5U/Q 系列)
using var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "192.168.1.10", 6000);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

var value = client.ReadInt16("D100");
var coil  = client.ReadBool("M100");
```

## Mitsubishi Migration Details

This section maps HSL Mitsubishi usage patterns to Nexus equivalents. Nexus provides five Mitsubishi clients covering MC Binary, MC ASCII, MC UDP, A1E, and FX serial variants.

### Client Selection

| Existing Mitsubishi Need | Nexus Client | Transport | Default Port | Notes |
|--------------------------|--------------|-----------|--------------|-------|
| MC 协议 Binary (Q/L/iQ-R/FX5U) | `Mc3EBinaryClient` | TCP | 6000 | Most common. Binary framing. |
| MC 协议 ASCII (Q/L/iQ-R/FX5U) | `Mc3EAsciiClient` | TCP | 6000 | ASCII hex framing for serial-to-Ethernet. |
| MC 协议 UDP | `Mc3EUdpClient` | UDP | 5551 | Binary or ASCII mode via `UseAscii` property. |
| A1E 兼容帧 (A/QnA) | `MelsecA1EClient` | TCP | 5551 | Legacy A-series frame. |
| FX 编程口协议 (FX1N/2N/3U) | `FxSerialClient` | Serial (`ISerialPort`) | — | ENQ/ACK/STX/ETX handshake. |
| FX 计算机链接协议 (RS-485) | `FxLinkClient` | `Stream` | — | Station-numbered RS-485 multidrop. |

```csharp
using Nexus.Mitsubishi;

// MC3E Binary
using var binary = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "192.168.1.10", 6000);

// MC3E ASCII
using var ascii = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "192.168.1.10", 6000);

// MC3E UDP (Binary mode default, switch to ASCII with UseAscii)
using var udp = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "192.168.1.10", 5551);
udp.UseAscii = true; // optional

// A1E legacy
using var a1e = new MelsecA1EClient("192.168.1.10", 5551);

// FX Serial (编程口)
ISerialPort port = CreateSerialPort(); // Application adapter
using var fxProg = new FxSerialClient(port);

// FX Link (计算机链接, RS-485 multi-drop)
Stream stream = GetStream(); // Serial-over-TCP/DTU or raw serial
using var fxLink = new FxLinkClient(stream, station: 1);
```

### Model Selection

The `MitsubishiModel` enum selects the correct MC frame format:

| Nexus Model | HSL Equivalent | PLC Series |
|-------------|----------------|------------|
| `Qna_3E` | QnA 3E frame | Q/L/FX5U/iQ-R/iQ-F (most common) |
| `Qna_2E` | QnA 2E frame | Q series legacy |
| `A_3E` | A 3E frame | A series |
| `A_1E` | A 1E frame | A series (use `MelsecA1EClient` instead) |
| `FX_3U` | FX 3E frame | FX3U |
| `FX_5U` | FX 3E frame | FX5U (same framing as Qna_3E) |
| `IQ_R` | iQ-R 3E frame | iQ-R series |
| `IQ_F` | iQ-F 3E frame | iQ-F series |
| `L_Series` | L 3E frame | L series |

### Address Format

Nexus Mitsubishi addresses use the standard MC protocol notation. The `Mc3EAddressParser` handles the sub-label conversion internally.

| Data Area | Address Examples | Sub-Label | Type | Address Base |
|-----------|-----------------|-----------|------|--------------|
| Data Register | `D0`, `D100`, `D8000` | 0xA8 | Word | Decimal |
| Internal Relay | `M0`, `M100` | 0x90 | Bit | Decimal |
| Input | `X0`, `X10`, `X1F` | 0x9C | Bit | **Hex** |
| Output | `Y0`, `Y20`, `Y3F` | 0x9D | Bit | **Hex** |
| Link Relay | `B0`, `B10` | 0xA0 | Bit | **Hex** |
| Link Register | `W0`, `W100` | 0xB4 | Word | Decimal |
| Latch Relay | `L0`, `L100` | 0x92 | Bit | Decimal |
| Step Relay | `S0`, `S100` | 0x98 | Bit | Decimal |
| File Register | `R0`, `R100` | 0xAF | Word | Decimal |
| Extended File Register | `ZR0` | 0xB0 | Word | Decimal |
| Special Relay | `SM0` | 0x91 | Bit | Decimal |
| Special Register | `SD0` | 0xA9 | Word | Decimal |
| Timer Contact | `TS0` | 0xC1 | Bit | Decimal |
| Timer Coil | `TC0` | 0xC0 | Bit | Decimal |
| Counter Contact | `CS0` | 0xC4 | Bit | Decimal |
| Counter Coil | `CC0` | 0xC3 | Bit | Decimal |
| Index Register | `Z0` | 0xCC | Word | Decimal |
| Edge Relay | `V0` | 0x94 | Bit | Decimal |
| Direct Input | `DX0` | 0xA2 | Bit | **Hex** |
| Direct Link Register | `SW0` | 0xB5 | Word | Decimal |

**Important**: X/Y/B/DX addresses are **hexadecimal**, not decimal. `X10` means input 0x10 = 16 in decimal, not input 10.

For FX serial protocols (`FxSerialClient` and `FxLinkClient`), addresses are simpler: `D100`, `M100`, `Y0`, `X0`, `T0`, `C0`, `S0` (decimal only).

### Read/Write API Mapping

The same `IReadWriteDevice` pattern applies to all Mitsubishi clients:

```csharp
using Nexus.Mitsubishi;

using var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "192.168.1.10", 6000);
client.Connect();

// Typed reads
OperateResult<short> d100 = client.ReadInt16("D100");
OperateResult<int>   d200 = client.ReadInt32("D200");
OperateResult<float> d300 = client.ReadFloat("D300");
OperateResult<bool>  m50  = client.ReadBool("M50");
OperateResult<string> str = client.ReadString("D500", 10); // 10 words

// Typed writes
client.Write("D100", (short)100);
client.Write("D200", 12345);
client.Write("D300", 3.14f);
client.Write("M50", true);
client.Write("D500", "Hello");
```

### HSL-to-Nexus Migration Pattern

| HSL Pattern | Nexus Equivalent | Notes |
|-------------|------------------|-------|
| `MelsecMcNet(ip, port)` | `Mc3EBinaryClient(Qna_3E, ip, port)` | MC3E Binary, the most common. |
| `MelsecMcAsciiNet(ip, port)` | `Mc3EAsciiClient(Qna_3E, ip, port)` | MC3E ASCII. |
| `MelsecA1ENet(ip, port)` | `MelsecA1EClient(ip, port)` | A1E legacy frame. |
| `MitsubishiFxSerial()` | `FxSerialClient(ISerialPort)` | FX 编程口协议. |
| `ReadInt16("D100")` | `ReadInt16("D100")` | Same address syntax. |
| `ReadBool("M100")` | `ReadBool("M100")` | Same. |
| `Write("D100", 100)` | `Write("D100", (short)100)` | Nexus requires explicit type cast. |
| `.ConnectServer()` | `.Connect()` | Different method name. |
| `.ConnectClose()` | `.Disconnect()` then `.Dispose()` | Nexus uses standard lifecycle. |

### Error Codes

Nexus maps SLMP completion codes to Chinese descriptions via `SlmpErrorCodes.GetDescription()`:

```csharp
ushort endCode = 0xC003;
Console.WriteLine(SlmpErrorCodes.GetDescription(endCode));
// 输出: 地址超出范围
```

Common error codes:

| Code | Meaning |
|------|---------|
| 0x0000 | 正常完成 |
| 0xC001 | 不支持的功能码 |
| 0xC003 | 地址超出范围 |
| 0xC004 | 数据长度超出范围 |
| 0xC006 | PLC 当前模式不支持此操作 |
| 0xC020 | 帧长度错误 |
| 0xC024 | 路由参数错误 |
| 0xCF70 | 从站无响应 |

### Virtual Server Testing

`Mc3EVirtuServer` and `MelsecA1EVirtualServer` provide offline integration testing without real PLC hardware:

```csharp
using var server = new Mc3EVirtuServer(6000);
server.Start();

using var client = new Mc3EBinaryClient(MitsubishiModel.Qna_3E, "127.0.0.1", 6000);
client.Connect();

// Read/write against virtual PLC memory
client.Write("D0", (short)42);
var result = client.ReadInt16("D0");
Assert.Equal((short)42, result.Content);
```

### Field Validation Checklist for Mitsubishi

Before declaring an HSL-to-Nexus Mitsubishi migration complete:

- [ ] Model enum matches the actual PLC series (Qna_3E for Q/L/FX5U/iQ-R).
- [ ] TCP port matches the PLC configuration (default 6000 for MC3E, 5551 for A1E/UDP).
- [ ] Address syntax matches the data area (X/Y/B are hex, D/M/S are decimal).
- [ ] Byte order is correct for multi-register types (Int32/Float/Int64/Double).
- [ ] Batch and random read/write limits are respected (960 words max per batch).
- [ ] FX serial baud rate, parity, and stop bits match the PLC programming port settings.
- [ ] FX Link station number matches the RS-485 multidrop address.

## Omron Migration Details

### Client Selection

| Existing Omron Need | Nexus Client | Transport | Default Port |
|---------------------|--------------|-----------|--------------|
| FINS TCP (CJ/NJ/NX) | `FinsTcpClient` | TCP | 9600 |
| FINS UDP | `FinsUdpClient` | UDP | 9600 |
| FINS Serial (RS-232) | `FinsSerialClient` | Serial (`Stream`) | — |
| HostLink TCP | `OmronHostLinkClient` | TCP | 9600 |
| HostLink Serial | `OmronHostLinkSerialClient` | Serial (`ISerialPort`) | — |

```csharp
using Nexus.Omron;

// FINS TCP
using var fins = new FinsTcpClient("192.168.1.10", 9600);
fins.Connect();

// FINS UDP
using var udp = new FinsUdpClient("192.168.1.10", 9600);

// HostLink TCP
using var hl = new OmronHostLinkClient("192.168.1.10", 9600);
```

### Address Format

| Address | Area | Description |
|---------|------|-------------|
| `D100` / `DM100` | DM | Data Memory (word), most commonly used |
| `CIO100` | CIO | Core I/O (bit or word) |
| `W100` / `WR100` | WR | Work Relay |
| `H100` / `HR100` | HR | Holding Relay (retained) |
| `A100` / `AR100` | AR | Auxiliary Relay (system flags) |
| `E0_100` | EM | Extended DM bank 0 |
| `D100.03` | DM bit | Word 100, bit 3 |
| `T100` | Timer PV | Timer current value |
| `C100` | Counter PV | Counter current value |

### HSL-to-Nexus Mapping

| HSL Pattern | Nexus Equivalent | Notes |
|-------------|------------------|-------|
| `OmronFinsNet(ip, port)` | `FinsTcpClient(ip, port)` | Different class name. |
| `OmronFinsUdp(ip, port)` | `FinsUdpClient(ip, port)` | Same concept. |
| `OmronHostLink(ip, port)` | `OmronHostLinkClient(ip, port)` | Same. |
| `.ConnectServer()` | `.Connect()` | Different method name. |
| `ReadInt16("D100")` | `ReadInt16("D100")` | Same address syntax. |
| `ReadBool("CIO100.03")` | `ReadBool("CIO100.03")` | Same. |

### PLC Configuration

Before connecting, ensure:
1. FINS/TCP is enabled in PLC Ethernet settings.
2. IP Address Table allows the PC's IP (or use auto-allocation).
3. Port 9600 is not blocked by firewall.

## AllenBradley Migration Details

### Client Selection

| Existing AB Need | Nexus Client | Transport | Default Port |
|------------------|--------------|-----------|--------------|
| ControlLogix / CompactLogix | `AllenBradleyCipClient` | TCP | 44818 |
| MicroLogix 1100/1400 | `PcccClient` | TCP | 44818 |
| PLC-5 / SLC 500 | `PcccClient` | TCP | 44818 |
| Micro850 / Micro870 | `AllenBradleyCipClient` | TCP | 44818 |

```csharp
using Nexus.AllenBradley;

// CIP (ControlLogix/CompactLogix)
using var cip = new AllenBradleyCipClient("192.168.1.10", 44818, slot: 0);
cip.Connect();

// PCCC (MicroLogix)
using var pccc = new PcccClient("192.168.1.20", 44818);
pccc.Connect();
```

### Address Format

**CIP (Logix family)** — tag-based:

| Example | Description |
|---------|-------------|
| `MyTag` | Controller-scoped DINT/REAL/BOOL tag |
| `Motor.Speed` | UDT member |
| `MyArray[0]` | Array element |
| `Program:Main.Tag` | Program-scoped tag |

**PCCC (MicroLogix/PLC-5)** — file-based:

| Example | File Type | Description |
|---------|-----------|-------------|
| `N7:0` | Integer | Integer file 7, element 0 |
| `F8:0` | Float | Float file 8, element 0 |
| `B3:0` | Binary | Bit file 3 |
| `T4:0` | Timer | Timer file 4 |

### HSL-to-Nexus Mapping

| HSL Pattern | Nexus Equivalent | Notes |
|-------------|------------------|-------|
| `AllenBradleyNet(ip)` | `AllenBradleyCipClient(ip, 44818, slot)` | Must specify slot. |
| `MelsecMcNet(ip)` → AB | `AllenBradleyCipClient` | Completely different protocol family. |
| `ReadDInt("Tag")` | `ReadInt32("Tag")` | Nexus uses `ReadInt32`; DINT = Int32. |
| `ReadReal("Tag")` | `ReadFloat("Tag")` | REAL = float. |
| `ReadInt16("N7:0")` | PCCC: `ReadInt16("N7:0")` | Same for PCCC addresses. |

### Important Notes

1. **DINT is native** — Use `ReadInt32`/`Write(tag, int)` as default for Logix controllers.
2. **Slot must be correct** — ControlLogix Rack/Slot varies by backplane layout.
3. **PUT/GET** must be enabled for PCCC clients on MicroLogix.
4. **UDT auto-deserialization is not available** — read individual members with typed methods.

| HSL Family | Nexus Module | Migration Status | Notes |
|------------|--------------|------------------|-------|
| Modbus TCP/RTU/ASCII/UDP | `Nexus.Modbus` | Reference candidate | First migration docs should be completed here. |
| Siemens S7 | `Nexus.Siemens` | Usable | S7 is strongest; PPI needs audit. |
| Mitsubishi MC/A1E/FX | `Nexus.Mitsubishi` | Usable | MC Binary/ASCII/UDP + A1E + FX Serial all in one package. |
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
