# Modbus Function Codes

The Modbus clients currently support the common read/write function codes across TCP, UDP, RTU, ASCII, and RTU-over-TCP.

## Function Code Matrix

| Code | Name | Nexus API |
|------|------|-----------|
| FC01 | Read Coils | `ReadBool`, `ReadBools` |
| FC02 | Read Discrete Inputs | `ReadBool`, `ReadBools` with `1xxxx` addresses |
| FC03 | Read Holding Registers | `ReadInt16`, `ReadUInt16`, `ReadInt32`, `ReadFloat`, `ReadBytes`, etc. |
| FC04 | Read Input Registers | Numeric reads with `3xxxx` addresses |
| FC05 | Write Single Coil | `Write(address, bool)` |
| FC06 | Write Single Register | `Write(address, short)` / `Write(address, ushort)` |
| FC15 | Write Multiple Coils | `WriteMultipleCoils` or `Write(address, bool[])` where available |
| FC16 | Write Multiple Registers | multi-register overloads for 32-bit, float, string, and byte arrays |
| FC22 | Mask Write Register | `MaskWriteRegister(address, andMask, orMask)` |
| FC23 | Read/Write Multiple Registers | `ReadWriteMultipleRegisters` |

## Custom Function Codes

Use `SendCustomModbus` when a device exposes vendor-specific Modbus functions.

```csharp
byte[] pdu = { 0x41, 0x00, 0x01 };
var response = client.SendCustomModbus(pdu);
```

TCP and UDP clients add an MBAP header automatically. RTU-style clients add station and checksum framing according to the transport.

## Exception Responses

Clients translate common exception codes into failure `OperateResult` messages:

| Exception Code | Meaning |
|----------------|---------|
| 1 | Illegal function |
| 2 | Illegal data address |
| 3 | Illegal data value |
| 4 | Slave device failure |

Always check `IsSuccess` before reading `Content`.
