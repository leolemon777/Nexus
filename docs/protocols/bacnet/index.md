# Nexus BACnet

Nexus BACnet covers BACnet/IP (ISO 16484-5 Annex J) communication over UDP for building automation and HVAC systems.

## Client

| Client | Transport | Base | Default Port | Notes |
|--------|-----------|------|--------------|-------|
| `BacnetIpClient` | UDP/IP | `UdpDeviceBase` | 47808 | Primary client. Supports `IBatchReadWrite`, `ISubscribeDevice`. |

## Feature Summary

| Feature | Status |
|---------|--------|
| ReadProperty | Yes |
| ReadPropertyMultiple | Yes |
| WriteProperty | Yes |
| WritePropertyMultiple | Yes |
| SubscribeCOV | Yes |
| AtomicReadFile | Yes |
| AtomicWriteFile | Yes |
| WhoIs / IAm | Yes |
| Device discovery | Yes |
| Object browsing | Yes |
| `IBatchReadWrite` | Yes |
| `ISubscribeDevice` | Yes |
| BACnet MSTP (serial) | Not yet |

## Address Format

Pattern: `ObjectType:Instance.PropertyId`

| Component | Default | Examples |
|-----------|---------|---------|
| ObjectType | `AnalogInput` | Numeric (`0`) or name (`AnalogInput`) |
| Instance | `0` | Any uint |
| PropertyId | `85` (PresentValue) | Numeric (`85`) or name (`PresentValue`) |

Examples:
- `"AnalogInput:0"` — AnalogInput instance 0, PresentValue
- `"AnalogInput:1.PresentValue"` — explicit property
- `"8:1234.ObjectName"` — Device object, ObjectName
- `"0:5.StatusFlags"` — AnalogInput, StatusFlags

## Quick Start

```csharp
using Nexus.Bacnet;

using var client = new BacnetIpClient("192.168.1.10", 47808);
var connect = client.Connect();

// Read analog input
var value = client.ReadFloat("AnalogInput:0");
if (value.IsSuccess)
    Console.WriteLine($"AI0 = {value.Content}");

// Write analog output
client.Write("AnalogOutput:0", 3.14f);

// Device discovery
client.OnIAm += (sender, e) =>
    Console.WriteLine($"Found device {e.DeviceId} at {e.RemoteAddress}");
client.WhoIs();

// Browse device objects
var objects = client.BrowseDeviceObjects(1234);
```

## Object Types

57 object types defined, including:

| Category | Types |
|----------|-------|
| Analog | AnalogInput, AnalogOutput, AnalogValue |
| Binary | BinaryInput, BinaryOutput, BinaryValue |
| Multi-State | MultiStateInput, MultiStateOutput, MultiStateValue |
| Scheduling | Calendar, Schedule, Command |
| Trending | TrendLog, TrendLogMultiple, EventLog |
| Infrastructure | Device, File, Group, Loop, Program |
| Lighting | LightingOutput, Channel |

## Wire Format

```
[UDP payload]
  └─ BVLC (4 bytes, type 0x81)
       └─ NPDU (2 bytes, version 0x01)
            └─ APDU (service-specific)
```

## Limitations

- No BACnet MSTP (serial/EIA-485) client
- No segmentation support in responses
- Batch operations are sequential (not ReadPropertyMultiple optimized)
- Long/ulong writes fail if value exceeds 32-bit range
- No virtual server for integration testing
