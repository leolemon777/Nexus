# Address Context

> Last updated: 2026-06-09

## Overview

`AddressContext` provides runtime parameter overrides embedded in address strings. It allows station numbers, byte order, and other per-operation parameters to be specified inline with the address.

## Format

```
key1=value1;key2=value2;CoreAddress
```

Everything before the last semicolon-separated segment is treated as a parameter. The last segment is the core address.

## Parsing

```csharp
using Nexus;

AddressContext ctx = AddressContext.Parse("s=2;bo=DCBA;D100");

ctx.OriginalAddress  // "s=2;bo=DCBA;D100"
ctx.CoreAddress      // "D100"
ctx.Parameters       // { "s": "2", "bo": "DCBA" }
ctx.GetParameter("s")           // "2"
ctx.GetIntParameter("s")        // 2
ctx.HasParameter("bo")          // true
```

## Standard Parameter Keys

These are recommended keys, but `AddressContext` is generic — any key/value pair can be used:

| Key | Meaning | Example Values |
|-----|---------|----------------|
| `s` | Station/Slave ID override | `s=2`, `s=5` |
| `bo` | Byte order override | `bo=ABCD`, `bo=DCBA`, `bo=BADC`, `bo=CDAB` |
| `x` | Slot number (Allen-Bradley) | `x=3` |
| `w` | Word count override | `w=10` |

## Use Cases

### Per-Operation Station Override

In a multi-slave Modbus network, different operations may target different slaves:

```csharp
using Nexus.Modbus;

var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);

// Read from station 1 (default)
client.ReadInt16("D100");

// Read from station 5 (override)
var ctx = AddressContext.Parse("s=5;D100");
// Application can use ctx.GetParameter("s") to extract and apply station override
```

### Per-Operation Byte Order

```csharp
// Read with different byte order for a specific register
var ctx = AddressContext.Parse("bo=DCBA;D200");
// Use ctx.CoreAddress for the actual read, apply byte order from ctx
```

## Integration Pattern

Protocol clients can optionally support `AddressContext` in their address parsing:

```csharp
public OperateResult<short> ReadInt16(string address)
{
    var ctx = AddressContext.Parse(address);

    // Apply station override if present
    if (ctx.GetIntParameter("s") is int station)
        SetStationOverride(station);

    // Use core address for protocol operation
    return ReadInt16Core(ctx.CoreAddress);
}
```

## Plain Address Compatibility

If no parameters are present, `AddressContext` returns the original string as `CoreAddress`:

```csharp
AddressContext ctx = AddressContext.Parse("D100");
ctx.CoreAddress      // "D100"
ctx.Parameters.Count  // 0
```

This means existing code that passes plain addresses works without any changes.

## Error Handling

```csharp
try
{
    AddressContext ctx = AddressContext.Parse("s=abc;D100");
    // ctx.GetIntParameter("s") returns null (not a valid int)
}
catch (AddressParseException ex)
{
    Console.WriteLine($"Invalid address: {ex.Message}");
}
```
