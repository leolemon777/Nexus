# Allen-Bradley Troubleshooting

> Last updated: 2026-06-09

## Common Errors

### CIP Error Codes

| Status | Meaning | Action |
|--------|---------|--------|
| 0x00 | Success | — |
| 0x01 | Connection failure | Check IP, port, and ENBT module status. |
| 0x04 | Tag not found / path error | Verify tag name matches PLC tag database exactly. |
| 0x05 | Path destination unknown | Check slot number; verify backplane configuration. |
| 0x06 | Partial transfer | Fragmented read incomplete; retry or reduce size. |
| 0x07 | Connection lost | Network interruption; check Ethernet cable. |
| 0x08 | Service not supported | PLC firmware may not support this CIP service. |
| 0x0C | Attribute not settable | Tag is read-only (e.g., input tag, constant). |

### Connection Issues

#### Cannot connect to ControlLogix

1. Verify IP address (`ping` test).
2. Check that EtherNet/IP is enabled (port 44818).
3. Verify the slot number matches the CPU position in the rack.
4. For ControlLogix, the ENBT module is in one slot, the CPU in another — the `slot` parameter refers to the **CPU slot**.

```csharp
// ControlLogix: CPU in slot 0, ENBT in slot 1
var client = new AllenBradleyCipClient("192.168.1.10", 44818, slot: 0);
```

#### Cannot connect to MicroLogix

1. MicroLogix 1100/1400 have built-in Ethernet — verify IP settings.
2. Check that "EtherNet/IP" or "PCCC" protocol is enabled in RSLogix.
3. Use `PcccClient` for MicroLogix, not `AllenBradleyCipClient`.

#### "Tag not found" (0x04) on valid tag

1. Tag names are case-sensitive in some firmware versions — match exactly.
2. Program-scoped tags need the `Program:` prefix: `Program:MainProgram.MyTag`.
3. Verify the tag exists in the controller scope, not just in a program.

#### Wrong values on array reads

1. Verify the CIP data type matches the tag type (DINT vs INT).
2. Logix uses **little-endian** byte order by default.
3. Use `ReadInt32` for DINT tags (most common), not `ReadInt16`.

### Slot Configuration

| Controller | Slot | How to Find |
|------------|------|-------------|
| ControlLogix | Varies | Check rack layout in RSLogix/Studio 5000 → I/O Configuration |
| CompactLogix | 0 (always) | Fixed |
| Micro800 | 0 (always) | Fixed |

To find the correct slot in ControlLogix:
1. Open Studio 5000 / RSLogix 5000.
2. Go to I/O Configuration → Backplane.
3. The CPU module's slot number is the position in the backplane (0-indexed).

## Diagnostic Steps

1. **Ping test**: `ping 192.168.1.10`
2. **Port check**: Verify port 44818 is reachable.
3. **Read a known tag**: `client.ReadInt32("MyTag")` with a simple DINT tag.
4. **Use virtual server**: Test against `CipVirtualServer` to isolate issues.

```csharp
using var server = new CipVirtualServer(44818);
server.Start();

using var client = new AllenBradleyCipClient("127.0.0.1", 44818, slot: 0);
client.Connect();

var test = client.ReadInt32("TestTag");
Console.WriteLine($"Virtual server test: {test.IsSuccess}");
```

## PCCC-Specific Issues

### Data file address format

- Integer files: `N7:0` (file 7, element 0)
- Float files: `F8:0`
- Binary/Bit files: `B3:0`
- Do NOT use tag names — PCCC uses file-based addressing only.

### "Not supported" on MicroLogix

Some PCCC commands are not supported on all MicroLogix models. Check the PLC's documentation for supported commands.
