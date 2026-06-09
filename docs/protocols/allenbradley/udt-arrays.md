# Allen-Bradley UDT and Array Scope

> Last updated: 2026-06-09

## Current Scope

This document honestly describes what Nexus supports and what remains experimental for Allen-Bradley UDT (User-Defined Type) and array operations.

## Array Support

### Single-Dimensional Arrays

**Status**: Supported via `IReadWriteDevice` typed methods.

```csharp
// Read individual elements
var elem0 = client.ReadInt32("MyArray[0]");
var elem5 = client.ReadInt32("MyArray[5]");

// Write individual elements
client.Write("MyArray[0]", 42);
client.Write("MyArray[5]", 100);
```

### Multi-Dimensional Arrays

**Status**: Supported for reading individual elements using chained index notation.

```csharp
// 2D array: DINT[5][10]
var elem = client.ReadInt32("MyMatrix[2][3]");
```

### Bulk Array Read

**Status**: Supported via fragmented read for large arrays.

```csharp
// Read a block of array elements as raw bytes
var data = client.ReadTagFragmented("MyArray[0]");
```

**Limitation**: Bulk array read returns raw bytes. Typed conversion for bulk elements is not yet a first-class API. Users must manually parse the byte array.

## UDT (User-Defined Type) Support

### Member Access

**Status**: Supported for primitive-type members.

```csharp
// Given UDT: Motor { Speed: DINT, Position: DINT, Running: BOOL }
var speed = client.ReadInt32("Motor.Speed");
var running = client.ReadBool("Motor.Running");

client.Write("Motor.Speed", 1500);
```

### Nested UDT

**Status**: Supported via chained dot notation.

```csharp
// Motor.Config.MaxSpeed where Config is itself a UDT
var maxSpeed = client.ReadInt32("Motor.Config.MaxSpeed");
```

### UDT Array

**Status**: Partial — individual element/member access works, bulk UDT read requires manual parsing.

```csharp
// Motor[0].Speed — works
var speed = client.ReadInt32("Motor[0].Speed");

// Motor[1].Speed — works
var speed2 = client.ReadInt32("Motor[1].Speed");
```

### Whole UDT Read/Write

**Status**: Experimental — `ReadTagFragmented` returns raw bytes for the entire UDT structure. Manual byte-to-struct mapping is required.

```csharp
// Read entire UDT as raw bytes
var raw = client.ReadTagFragmented("Motor");
// Manual parsing required — Nexus does not yet auto-deserialize UDTs
```

**Known gap**: Nexus does not provide automatic UDT deserialization (struct mapping from CIP structure data). This is planned for a future release.

## STRING Tags

**Status**: Supported.

```csharp
// Read a Logix STRING tag
var name = client.ReadString("ProductName");

// Write a string
client.Write("ProductName", "Hello");
```

Logix STRING is a structure with a length prefix (DINT) followed by up to 82 characters. Nexus handles the encoding automatically.

## Honest Assessment

| Capability | Status | Notes |
|------------|--------|-------|
| Simple tag read/write | ✅ Complete | DINT, REAL, BOOL, INT, STRING, etc. |
| Array element access | ✅ Complete | Single and multi-dimensional |
| UDT member access (primitive) | ✅ Complete | Dot notation |
| Nested UDT | ✅ Complete | Chained dot notation |
| UDT array member access | ✅ Complete | `Tag[index].Member` |
| Batch tag read | ✅ Complete | `IBatchReadWrite` |
| Fragmented tag read/write | ✅ Complete | For large data |
| Auto UDT deserialization | ❌ Not available | Must parse raw bytes manually |
| Struct mapping (struct-to-tag) | ❌ Not available | Planned for future |
| Tag browsing / discovery | ❌ Not available | — |
| Symbol upload | ❌ Not available | — |

## Recommendations

1. Use `ReadInt32` (DINT) as the default integer type — it's native to Logix controllers.
2. For UDT access, read individual primitive members rather than the whole structure.
3. For large arrays, use `ReadTagFragmented` and parse the raw byte response.
4. Verify tag names match the Studio 5000 / RSLogix 5000 tag database exactly.
