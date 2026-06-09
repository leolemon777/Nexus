# Mitsubishi Address Format

This page documents the address handling observed in the current Mitsubishi source. Treat it as an implementation inventory, not a Mitsubishi manual replacement.

## MC3E Address Parser

`Mc3EAddressParser.Parse(string)` accepts one-character and selected two-character prefixes.

| Prefix | Source label | Address base in parser | Bit/word classification in source | Notes |
| --- | --- | --- | --- | --- |
| `D` | `0xA8` | Decimal | Word | Data register |
| `M` | `0x90` | Decimal | Bit | Internal relay |
| `X` | `0x9C` | Hex | Bit | Input |
| `Y` | `0x9D` | Hex | Bit | Output |
| `Z` | `0xCC` | Decimal | Word | Index register |
| `R` | `0xAF` | Decimal | Word | File register |
| `B` | `0xA0` | Hex | Bit | Link relay |
| `W` | `0xB4` | Decimal | Word | Link register in current parser; TODO verify whether hex is expected by target PLC families |
| `L` | `0x92` | Decimal | Bit | Latch relay |
| `F` | `0x93` | Decimal | Bit | State/alarm style bit area in source comments |
| `V` | `0x94` | Decimal | Bit | Edge relay |
| `S` | `0x98` | Decimal | Bit | Step relay |
| `TS` | `0xC1` | Decimal | Bit | Timer contact |
| `TC` | `0xC0` | Decimal | Bit | Timer coil in current classification |
| `CS` | `0xC4` | Decimal | Bit | Counter contact |
| `CC` | `0xC3` | Decimal | Bit | Counter coil in current classification |
| `SM` | `0x91` | Decimal | Bit | Special relay |
| `SD` | `0xA9` | Decimal | Word | Special register |
| `DX` | `0xA2` | Hex | Bit | Direct input |
| `SW` | `0xB5` | Decimal | Word | Direct link register in current parser; TODO verify naming and address base |
| `ZR` | `0xB0` | Decimal | Word | Extended file register |

Examples:

```csharp
var d100 = Mc3EAddressParser.Parse("D100");  // label 0xA8, address 100
var xff = Mc3EAddressParser.Parse("XFF");    // label 0x9C, address 255
var zr1000 = Mc3EAddressParser.Parse("ZR1000");
```

Important MC3E notes:

- `X`, `Y`, `B`, and `DX` are parsed as hexadecimal.
- Most other prefixes are parsed as decimal in the current implementation.
- `IsBitAddress` classifies `M/X/Y/L/F/S/B/V/TS/TC/CS/CC/SM/DX` as bit addresses and `D/W/Z/R/SD/SW/ZR` as word addresses.
- `TN` and `CN` are not present in `Mc3EAddressParser`; timer/counter current-value support is therefore TODO for MC3E.
- `Mc3EVirtuServer` backs D/W/R/Z/ZR/SD/SW word areas and M/X/Y/B/L/F/V/S bit areas. It does not currently model TS/TC/CS/CC/SM/DX storage.

## A1E Address Parser

`MelsecA1EClient.AnalysisAddress(string)` returns a data code, data type, and numeric address.

| Prefix | Data code | Address base | Type | Notes |
| --- | --- | --- | --- | --- |
| `D` | `0x4440` | Decimal | Word | Data register |
| `M` | `0x4D20` | Decimal | Bit | Internal relay |
| `X` | `0x5820` | Octal if text starts with `0`, otherwise hex | Bit | Source-specific rule |
| `Y` | `0x5920` | Octal if text starts with `0`, otherwise hex | Bit | Source-specific rule |
| `S` | `0x5320` | Decimal | Bit | State |
| `F` | `0x4620` | Decimal | Bit | Alarm/state bit area |
| `B` | `0x4220` | Hex | Bit | Link relay |
| `W` | `0x5740` | Hex | Word | Link register |
| `R` | `0x5220` | Decimal | Word | File register |
| `TS` | `0x5453` | Decimal | Bit | Timer contact |
| `TC` | `0x5443` | Decimal | Bit | Timer coil |
| `TN` | `0x544E` | Decimal | Word | Timer current value |
| `CS` | `0x4353` | Decimal | Bit | Counter contact |
| `CC` | `0x4343` | Decimal | Bit | Counter coil |
| `CN` | `0x434E` | Decimal | Word | Counter current value |

Examples:

```csharp
var d100 = MelsecA1EClient.AnalysisAddress("D100");
var x17 = MelsecA1EClient.AnalysisAddress("X17");   // hex 0x17 = 23
var y017 = MelsecA1EClient.AnalysisAddress("Y017"); // octal 017 = 15
```

Important A1E notes:

- `X10` and `Y10` parse as hexadecimal because they do not have a leading zero.
- `X017` and `Y017` parse as octal because they start with `0`.
- `Z` is not supported by `MelsecA1EClient.AnalysisAddress`.
- `MelsecA1EVirtualServer` backs D/R/W/TN/CN word areas and M/X/Y/S/B/F/TS/TC/CS/CC bit areas.

## FX Serial Address Handling

`FxSerialClient` in `Nexus.Mitsubishi`:

- Regex: `^([DMXYTS])(\d+)$`
- Prefixes accepted by parser: `D`, `M`, `X`, `Y`, `T`, `S`
- Numeric part is parsed as decimal.
- Uses `FxFrameBuilder.BuildReadCommand` and `BuildWriteCommand`.

`MitsubishiFxSerialClient` in `Nexus.MitsubishiFx`:

- Prefixes handled in parser: `D`, `M`, `Y`, `X`, `T`, `C`, `S`, `R`, `Z`, `V`
- Numeric part is parsed as decimal.
- `Y` and `X` convert `num / 8` to a 2-digit hex address in the current source.
- Unknown prefixes fall back to `D` behavior in current source; this should be changed or documented before production use.

FX audit TODO:

- Confirm whether `X/Y` should be octal, bit-numbered, byte-numbered, or station/module dependent for each implementation.
- Confirm timer/counter bit versus current-value addressing.
- Add fake serial tests for full request/response frames.
- Decide whether `Nexus.Mitsubishi.FxSerialClient` or `Nexus.MitsubishiFx.MitsubishiFxSerialClient` is the recommended public API.

## Byte Order And Strings

MC3E Binary, ASCII, and UDP:

- `ByteOrder` default is `Endianness.BigEndian`.
- `ByteOrder` affects 32-bit and 64-bit numeric read/write paths in source.
- `StringEncoding` default is ASCII.
- `Mc3EBinaryClient` also exposes `ReadStringEncoded` and `WriteStringEncoded`.

A1E:

- Reads use big-endian conversion through `DataConverter` for standard typed reads.
- `short` and `int` writes are manually big-endian.
- `long`, `ulong`, `float`, and `double` writes currently use `BitConverter.GetBytes`; this requires audit because it is platform-endian.

FX serial:

- `FxSerialClient` reads/writes low byte first for several numeric paths through little-endian-style byte composition.
- `MitsubishiFxSerialClient` represents word data as ASCII hex strings and needs real-device byte-order validation.
