# Modbus Byte Order

Many Modbus devices store multi-register values in different byte and word orders. Nexus exposes `ByteOrder` on Modbus clients.

## Supported Orders

| `Endianness` | Register/Byte Order | Common Name |
|--------------|---------------------|-------------|
| `BigEndian` | ABCD | Big-endian, default |
| `LittleEndian` | DCBA | Little-endian |
| `MidBigEndian` | BADC | byte-swap within words |
| `MidLittleEndian` | CDAB | word-swap |

## Example

```csharp
using Nexus;
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100");
client.ByteOrder = Endianness.MidLittleEndian;

var value = client.ReadFloat("40001");
```

## String Encoding

Several Modbus clients expose `StringEncodingOption` and helper methods:

```csharp
client.StringEncodingOption = StringEncoding.Utf8;
var text = client.ReadStringEncoded("40010", 20);
```

String layout is still device-specific. Record the expected byte order, padding, and encoding in real-device validation notes.

## Production Checklist

- Confirm the device manual's word order for 32-bit and 64-bit values.
- Test one known `Int32` or `Float` value before large-scale reads.
- Record the selected `ByteOrder` in connection templates and validation records.
- Do not assume all addresses on a device use the same string layout.
