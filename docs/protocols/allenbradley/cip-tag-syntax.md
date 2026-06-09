# Allen-Bradley CIP Tag Syntax

> Last updated: 2026-06-09

## Overview

Allen-Bradley Logix-family PLCs (ControlLogix, CompactLogix) use tag-based addressing. Tags are named data elements that map directly to controller memory. Nexus encodes tag names into CIP path segments automatically.

## Tag Name Format

```
TagName[.Member][[Index]]
```

### Simple Tags

| Tag Example | Type | Description |
|-------------|------|-------------|
| `MyTag` | DINT | Controller-scoped tag |
| `Temperature` | REAL | Any primitive type |
| `Enable` | BOOL | Boolean tag |
| `ProductName` | STRING | String tag |

### Program-Scoped Tags

Prefix with `Program:` to access tags in a specific program:

```
Program:MyProgram.MyTag
```

### Array Access

| Tag Example | Description |
|-------------|-------------|
| `MyArray[0]` | First element |
| `MyArray[10]` | Element at index 10 |
| `MyArray[0][1]` | 2D array element (row 0, column 1) |

### Struct/UDT Members

Dot notation accesses UDT members:

| Tag Example | Description |
|-------------|-------------|
| `Motor.Speed` | Member `Speed` of UDT tag `Motor` |
| `Motor.Status.Running` | Nested UDT member |
| `Motor.Temps[0]` | Array member of a UDT |

### Combined

```
Program:Line1.Motors[2].Speed
```

This reads: in program `Line1`, tag `Motors` array element 2, member `Speed`.

## Slot Configuration

ControlLogix systems use a backplane with multiple modules. The `Slot` parameter specifies which slot the CPU is in:

```csharp
// ControlLogix: CPU typically in slot 0
var client = new AllenBradleyCipClient("192.168.1.10", slot: 0);

// CompactLogix: always slot 0
var client = new AllenBradleyCipClient("192.168.1.10", slot: 0);
```

| Controller | Typical Slot |
|------------|--------------|
| ControlLogix | 0 (check rack configuration) |
| CompactLogix | 0 (always) |

## Data Types

| CIP Type | Code | .NET Type | Size | Notes |
|----------|------|-----------|------|-------|
| BOOL | 0x00C1 | `bool` | 1 bit | Read via `ReadBool` |
| SINT | 0x00C2 | `sbyte` | 1 byte | 8-bit signed |
| INT | 0x00C3 | `short` | 2 bytes | 16-bit signed |
| DINT | 0x00C4 | `int` | 4 bytes | 32-bit signed — **most common** |
| LINT | 0x00C5 | `long` | 8 bytes | 64-bit signed |
| USINT | 0x00C6 | `byte` | 1 byte | 8-bit unsigned |
| UINT | 0x00C7 | `ushort` | 2 bytes | 16-bit unsigned |
| UDINT | 0x00C8 | `uint` | 4 bytes | 32-bit unsigned |
| ULINT | 0x00C9 | `ulong` | 8 bytes | 64-bit unsigned |
| REAL | 0x00CA | `float` | 4 bytes | 32-bit IEEE float |
| LREAL | 0x00CB | `double` | 8 bytes | 64-bit IEEE float |
| STRING | 0x00D0 | `string` | variable | Logix STRING structure |

**Important**: DINT (32-bit signed) is the **native** data type for Logix controllers. Prefer `ReadInt32` / `Write(tag, int)` for best performance.

## Fragmented Read/Write

For large tags (arrays, strings, UDTs) that exceed the CIP PDU size, use fragmented operations:

```csharp
// Read large array in fragments
var data = client.ReadTagFragmented("LargeArray[0]", offset: 0, count: 1000);

// Write large data in fragments
client.WriteTagFragmented("LargeArray[0]", data, offset: 0, dataType: CipDataType.Dint);
```

## Batch Read

Read multiple tags in one request using `IBatchReadWrite`:

```csharp
IBatchReadWrite batch = client;
var result = batch.BatchRead(new[] { "Tag1", "Tag2", "Tag3" });
if (result.IsSuccess)
{
    int tag1 = (int)result.Content["Tag1"];
    float tag2 = (float)result.Content["Tag2"];
}
```

## Tag Name Limits

- Maximum tag name length: 82 characters (CIP symbolic segment limit).
- Only ASCII alphanumeric characters, underscores, and dots are supported.
- Tag names are case-insensitive in most Logix controllers.

## CIP Error Codes

| Status | Meaning |
|--------|---------|
| 0x00 | Success |
| 0x01 | Connection failure |
| 0x04 | Tag not found or path segment error |
| 0x05 | Path destination unknown |
| 0x06 | Partial transfer |
| 0x07 | Connection lost |
| 0x08 | Service not supported |
| 0x0C | Attribute not settable — tag is read-only |
