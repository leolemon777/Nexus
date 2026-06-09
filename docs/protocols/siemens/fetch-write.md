# Siemens Fetch/Write

Status: draft. This page is based on current source and virtual-server tests. It does not claim real-device validation.

`SiemensFetchWriteClient` implements Siemens Fetch/Write over TCP. Source comments state it is intended for S7-300/400/1200/1500 when PLC-side Fetch/Write access is enabled.

## When To Use

Use Fetch/Write when:

- The PLC project is configured to allow Fetch/Write access.
- You need simple byte-area read/write access.
- You can validate PLC-side security and connection settings in the target environment.

Prefer `SiemensS7Client` when:

- You need native S7 Communication, batch S7 reads/writes, large block helpers, S7 String/WString helpers, or PLC control commands.

## Connection

Constructor:

```csharp
var client = new SiemensFetchWriteClient("192.168.0.10", port: 102, timeout: 5000);
var connect = client.Connect();
```

TODO:

- Add PLC-side setup screenshots or exact project settings after real-device validation.
- Confirm whether port 102 is always correct for target families and configurations.

## Address Format

Confirmed from source/tests:

| Area | Examples | Notes |
| --- | --- | --- |
| DB | `DB1.100`, `DB3.0` | DB number must parse to 0-255 in current source. |
| M | `M50`, `M100`, `M50.1` | Bit reads/writes are implemented with read-modify-write. |
| I | `I100` | Input area. |
| Q | `Q200` | Output area. |
| T | `T10` | Timer area; read count is word-based in command build. Needs hardware validation. |
| C | `C5` | Counter area; read count is word-based in command build. Needs hardware validation. |

Invalid examples covered in tests include empty address, unsupported area such as `X100`, and incomplete DB address such as `DB1`.

## Read And Write

Confirmed public operations include:

- `ReadBytes(address, length)`
- `Write(address, byte[])`
- Bool read/write through read-modify-write
- Int16 / UInt16
- Int32 / UInt32
- Int64 / UInt64
- Float
- Double
- ASCII string

Example:

```csharp
var word = client.ReadInt16("M100");
var write = client.Write("DB1.0", (short)0x1234);
var text = client.ReadString("M400", 4);
```

Current source uses big-endian construction for Int16/Int32 reads and writes. Some 64-bit and floating-point write paths should be audited against real device behavior because source mixes direct `BitConverter` writes with big-endian read logic.

## Virtual Server And Tests

`SiemensFetchWriteVirtualServer` exists and tests cover:

- Server start/stop.
- Read/write M area.
- Read/write DB area.
- Bool read/write via read-modify-write.
- I and Q area reads/writes.
- String read.
- Float read.
- Static command builders and response checks.

This is useful for regression tests, but it is not real-device validation.

## Real Device Validation Checklist

- Confirm PLC model, firmware, and project Fetch/Write setting.
- Confirm connection establishment and timeout behavior.
- Read/write DB and M areas.
- Read I and write/read Q where safe and permitted.
- Validate Bool read-modify-write behavior under concurrent PLC writes.
- Validate T/C reads if they remain in public docs.
- Validate Int64/UInt64/Float/Double byte order.
- Capture TX/RX bytes and PLC diagnostics for failures.

## Draft TODO

- Add exact PLC configuration guide.
- Add endianness table after hardware traces.
- Decide whether Fetch/Write should implement batch helpers or stay simple.
