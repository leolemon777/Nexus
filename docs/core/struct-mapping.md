# Struct Mapping

> Last updated: 2026-06-09

## Overview

`StructConverter` maps PLC memory byte arrays to C# structs and back, with byte-order control per field. Useful when reading multiple adjacent registers that form a structured data block.

## Quick Start

### Define a Struct

```csharp
using System.Runtime.InteropServices;
using Nexus;

// Use [StructLayout] to guarantee field order
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MotorStatus
{
    public short Speed;        // Word 0 (2 bytes)
    public short Position;     // Word 1 (2 bytes)
    public float Temperature;  // Words 2-3 (4 bytes)
    public short ErrorCode;    // Word 4 (2 bytes)
}
```

### Read Struct from PLC

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100", 502, station: 1)
{
    ByteOrder = Endianness.BigEndian
};
client.Connect();

// Read 5 words (10 bytes) starting at D100
var readResult = client.ReadBytes("D100", 10);
if (readResult.IsSuccess)
{
    // Native layout (big-endian PLC → big-endian struct)
    MotorStatus status = StructConverter.FromBytes<MotorStatus>(readResult.Content);

    Console.WriteLine($"Speed: {status.Speed}");
    Console.WriteLine($"Temperature: {status.Temperature}");
}
```

### Write Struct to PLC

```csharp
MotorStatus status = new MotorStatus
{
    Speed = 1500,
    Position = 200,
    Temperature = 45.5f,
    ErrorCode = 0
};

byte[] data = StructConverter.ToBytes(ref status);
client.Write("D100", data);
```

## Byte Order Aware Version

When the PLC byte order differs from the struct's native layout:

```csharp
// Read with explicit byte order (little-endian PLC data)
MotorStatus status = StructConverter.FromBytes<MotorStatus>(readResult.Content, 0, Endianness.LittleEndian);

// Write with explicit byte order
byte[] data = StructConverter.ToBytes(ref status, Endianness.LittleEndian);
```

### Byte Order Examples

| PLC Endianness | Nexus `Endianness` | Use Case |
|---------------|---------------------|----------|
| ABCD (big-endian) | `BigEndian` | Default for most protocols |
| DCBA (little-endian) | `LittleEndian` | Intel/ARM native |
| BADC | `MidBigEndian` | Byte-swapped registers |
| CDAB | `MidLittleEndian` | Word-swapped |

## Nested Structs

`StructConverter` handles nested structs recursively:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MotorConfig
{
    public short MaxSpeed;
    public short MinSpeed;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MotorData
{
    public MotorStatus Status;   // Nested struct
    public MotorConfig Config;   // Nested struct
}
```

## Supported Field Types

| C# Type | PLC Size | Notes |
|---------|----------|-------|
| `bool` | 1 byte | 0/1 |
| `byte` / `sbyte` | 1 byte | 8-bit |
| `short` / `ushort` | 2 bytes | 16-bit |
| `int` / `uint` | 4 bytes | 32-bit |
| `long` / `ulong` | 8 bytes | 64-bit |
| `float` | 4 bytes | 32-bit IEEE |
| `double` | 8 bytes | 64-bit IEEE |
| Nested struct | variable | Recursive decode |

## Example: Modbus Motor Control Block

A common pattern in industrial systems — a motor control block occupies consecutive holding registers:

```
Register  Map              Type
40001     Speed Setpoint   INT16
40002     Position Target  INT16
40003-04  Temperature      REAL32 (Float)
40005     Error Code       UINT16
40006     Status Flags     UINT16
```

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MotorBlock
{
    public short SpeedSetpoint;
    public short PositionTarget;
    public float Temperature;
    public ushort ErrorCode;
    public ushort StatusFlags;
}

// Read the entire block in one operation
var bytes = client.ReadBytes("40001", 12); // 6 words = 12 bytes
if (bytes.IsSuccess)
{
    var block = StructConverter.FromBytes<MotorBlock>(bytes.Content);
    Console.WriteLine($"Temp={block.Temperature}, Status=0x{block.StatusFlags:X4}");
}
```

## Limitations

1. **Blittable structs only** — no reference-type fields (strings, arrays).
2. **No automatic padding** — use `Pack = 1` to avoid alignment gaps.
3. **Field order matters** — always use `[StructLayout(LayoutKind.Sequential)]`.
4. **String fields not supported** — use fixed-size byte arrays and convert manually.
