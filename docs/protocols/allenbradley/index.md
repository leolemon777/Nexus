# Nexus Allen-Bradley

Nexus Allen-Bradley covers EtherNet/IP CIP (Common Industrial Protocol) and PCCC (Programmable Controller Communications) for Allen-Bradley PLC families: ControlLogix, CompactLogix, MicroLogix, Micro800, PLC-5, and SLC 500.

## Clients

| Client | Protocol | Transport | Default Port | Notes |
|--------|----------|-----------|--------------|-------|
| `AllenBradleyCipClient` | CIP / EtherNet/IP | TCP | 44818 | Primary client for Logix-family PLCs. Supports `IBatchReadWrite`, fragmented read/write. |
| `PcccClient` | PCCC / DF1 | TCP | 44818 | Legacy client for MicroLogix, PLC-5, SLC 500. |
| `CipVirtualServer` | CIP | TCP | configurable | Virtual PLC for integration testing. |
| `PcccVirtualServer` | PCCC | TCP | configurable | Virtual PCCC server for testing. |

## Feature Summary

| Feature | CIP Client | PCCC Client |
|---------|------------|-------------|
| Tag read/write | Yes | Yes (data table) |
| `IBatchReadWrite` | Yes | No |
| Fragmented tag read | Yes | No |
| Fragmented tag write | Yes | No |
| String tags | Yes | No |
| Array element access | Yes | No |
| UDT member access | Yes (partial) | No |
| PLC control (run/stop) | No | No |
| Virtual server | Yes | Yes |

## Quick Start

### CIP (ControlLogix / CompactLogix)

```csharp
using Nexus.AllenBradley;

using var client = new AllenBradleyCipClient("192.168.1.10", 44818, slot: 0);
var connect = client.Connect();
if (!connect.IsSuccess)
{
    Console.WriteLine(connect.Message);
    return;
}

// Read a DINT tag
var value = client.ReadInt32("MyTag");
if (value.IsSuccess)
    Console.WriteLine($"MyTag = {value.Content}");

// Write a tag
client.Write("MyTag", 42);

// Read a REAL tag
var temp = client.ReadFloat("Temperature");
```

### PCCC (MicroLogix / PLC-5)

```csharp
using Nexus.AllenBradley;

using var client = new PcccClient("192.168.1.20", 44818);
client.Connect();

// Read N7:0 (integer file 7, element 0)
var value = client.ReadInt16("N7:0");

// Write to N7:1
client.Write("N7:1", (short)100);

// Read a float from F8:0
var temp = client.ReadFloat("F8:0");
```

## Supported PLC Models

| Model | Protocol | Typical Port | Notes |
|-------|----------|--------------|-------|
| ControlLogix 5570 | CIP | 44818 | 1756-L7x controllers |
| ControlLogix 5580 | CIP | 44818 | 1756-L8x controllers |
| CompactLogix 5370 | CIP | 44818 | 1769-Lx7 controllers |
| CompactLogix 5380 | CIP | 44818 | 5069-L3xx controllers |
| CompactLogix 5480 | CIP | 44818 | 1768-L43/L45 |
| MicroLogix 1400 | PCCC | 44818 | 1766-L32 |
| MicroLogix 1100 | PCCC | 44818 | 1763-L16 |
| Micro850 | CIP (limited) | 44818 | 2080-LCxx |
| PLC-5 | PCCC | 44818 | 1785-Lxx |
| SLC 500 | PCCC | 44818 | 1747-Lxx |

## Related Pages

- [CIP Tag Syntax](cip-tag-syntax.md)
- [PCCC Coverage](pccc-coverage.md)
- [UDT and Arrays](udt-arrays.md)
- [Troubleshooting](troubleshooting.md)
