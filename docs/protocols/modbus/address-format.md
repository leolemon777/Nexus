# Modbus Address Format

Nexus Modbus clients accept plain numeric addresses and standard Modbus-style prefixed addresses.

## Address Areas

| Input Form | Area | Read Function | Write Function | Notes |
|------------|------|---------------|----------------|-------|
| `0xxxx` | Coil | FC01 | FC05 / FC15 | Example: `00001`. |
| `1xxxx` | Discrete input | FC02 | none | Read-only. |
| `3xxxx` | Input register | FC04 | none | Read-only. |
| `4xxxx` | Holding register | FC03 | FC06 / FC16 | Example: `40001`. |
| short numeric, such as `0` or `100` | Holding register | FC03 | FC06 / FC16 | Treated as a 0-based holding-register offset. |

## 1-Based And 0-Based Behavior

For high-level client reads and writes, standard prefixed addresses with at least five digits are treated as the common 1-based Modbus notation:

| User Address | Internal Offset |
|--------------|-----------------|
| `00001` | coil offset `0` |
| `10001` | discrete input offset `0` |
| `30001` | input register offset `0` |
| `40001` | holding register offset `0` |

Short numeric addresses are treated as direct 0-based offsets:

| User Address | Internal Offset |
|--------------|-----------------|
| `0` | holding register offset `0` |
| `1` | holding register offset `1` |
| `100` | holding register offset `100` |

## Examples

```csharp
client.ReadBool("00001");       // coil 0, FC01
client.ReadBool("10001");       // discrete input 0, FC02
client.ReadInt16("30001");      // input register 0, FC04
client.ReadInt16("40001");      // holding register 0, FC03
client.Write("40001", (short)1); // holding register 0, FC06
client.Write("00001", true);     // coil 0, FC05
```

## Standalone Parser Note

`ModbusAddressParser` is useful for grouping and batch workflows. Client read/write methods have their own internal parsing path. When auditing address behavior, test the actual client method for the transport being used.
