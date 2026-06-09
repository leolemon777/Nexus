# Omron Troubleshooting

> Last updated: 2026-06-09

## Common Errors

### FINS Error Codes

| End Code | Meaning | Action |
|----------|---------|--------|
| 0x0000 | Normal completion | — |
| 0x0001 | Command not supported | Check PLC model and firmware version. |
| 0x0002 | Not ready | PLC may be in PROGRAM mode or starting up. |
| 0x0003 | Routing error | Check network/node/unit addressing. |
| 0x0201 | Parameter error | Check address format and command parameters. |
| 0x0202 | Data length error | Verify the read/write count is within limits (max 500 words). |
| 0x0301 | Memory area error | Check that the area prefix is valid for the PLC model. |
| 0x0302 | Address range error | Address exceeds the PLC's memory range. |
| 0x0303 | Address overflow | Start address + count exceeds the area boundary. |
| 0x0304 | Write protected | DM area or program area is write-protected. |
| 0x0401 | Aborted | PLC aborted the operation; may indicate mode conflict. |

### Connection Issues

#### "Connection refused" on TCP

1. Verify PLC IP address is correct and reachable (`ping` test).
2. Check that FINS/TCP is enabled in PLC settings.
3. Confirm port 9600 is not blocked by firewall.

#### FINS handshake fails

1. The PLC's IP Address Table must allow the PC's IP.
2. In CX-Programmer: go to PLC → Setup → FINS/TCP → enable "Automatic Node Allocation" or add the PC IP manually.
3. Try restarting the Ethernet unit after configuration changes.

#### Timeout on reads

1. Check PLC mode — some operations require RUN mode.
2. Verify address range matches the PLC model (CP1 has smaller DM range than CJ2).
3. Reduce read count if approaching the 500-word limit.

### Data Issues

#### Wrong values on Int32/Float reads

1. FINS uses **big-endian** byte order by default.
2. If the PLC is configured for little-endian, set `ByteOrder` on the client.
3. Verify you're reading consecutive words (Int32 = 2 words, Float = 2 words).

#### String encoding mismatch

1. Set `StringEncoding` property on the client:
   - `FinsStringEncoding.Ascii` (default)
   - `FinsStringEncoding.Unicode`
2. Match the PLC's string configuration.

## Diagnostic Steps

1. **Test connectivity**: `ping 192.168.1.10`
2. **Test FINS port**: `telnet 192.168.1.10 9600` (should connect)
3. **Read a known safe address**: `client.ReadInt16("D0")`
4. **Check PLC mode**: Some PLCs require RUN mode for data access.
5. **Use virtual server**: Test against `FinsVirtualServer` to isolate network issues.

```csharp
using var server = new FinsVirtualServer();
server.Start();

using var client = new FinsTcpClient("127.0.0.1", server.Port);
client.Connect();

var test = client.ReadInt16("D0");
Console.WriteLine($"Virtual server test: {test.IsSuccess}");
```
