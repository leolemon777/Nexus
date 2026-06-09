# Omron Address Format

> Last updated: 2026-06-09

## Overview

Omron FINS addresses follow a `AreaPrefix + WordAddress` pattern with optional bit offset. Nexus uses `FinsAddressParser` to convert string addresses into protocol-level memory area codes.

## Address Syntax

```
[AreaPrefix]WordAddress[.BitOffset]
```

## Memory Areas

| Prefix | Area | FINS Code | Type | Address Range | Notes |
|--------|------|-----------|------|---------------|-------|
| `CIO` | Core I/O | 0xB0 | Bit/Word | 0–6143 (CJ2), 0–511 (CP1) | CIO includes I/O, work, and link relays. |
| `D` or `DM` | Data Memory | 0x82 | Word | 0–32767 (CJ2/NJ), 0–9999 (CP1) | Most commonly used for data storage. |
| `W` or `WR` | Work Relay | 0xB1 | Bit/Word | 0–511 | Internal work bits. |
| `H` or `HR` | Holding Relay | 0xB2 | Bit/Word | 0–511 | Retained on power cycle. |
| `A` or `AR` | Auxiliary Relay | 0xB3 | Bit/Word | 0–959 | System flags and status. |
| `E{bank}_` | Extended DM | 0x98 | Word | bank 0–C, word 0–32767 | EM area with bank selection. |
| `T` | Timer PV | 0x91 | Word | 0–4095 | Timer current values. |
| `TF` | Timer Flags | 0x92 | Bit | 0–4095 | Timer completion flags. |
| `C` | Counter PV | 0xA1 | Word | 0–4095 | Counter current values. |
| `CF` | Counter Flags | 0xA2 | Bit | 0–4095 | Counter completion flags. |

## Bit Addressing

Use a dot (`.`) to specify a bit within a word:

| Address | Meaning |
|---------|---------|
| `D100.00` | DM word 100, bit 0 |
| `D100.15` | DM word 100, bit 15 |
| `CIO100.03` | CIO word 100, bit 3 |
| `W50.07` | Work relay word 50, bit 7 |

Bit offsets range from 0 to 15.

## Default Area

Plain numbers without a prefix default to **DM area**:

```csharp
client.ReadInt16("100");    // Equivalent to D100 / DM100
client.ReadInt16("D100");   // Same
client.ReadInt16("DM100");  // Same
```

## EM (Extended Data Memory) Area

EM requires a bank number:

```csharp
client.ReadInt16("E0_100");   // EM bank 0, word 100
client.ReadInt16("E1_200");   // EM bank 1, word 200
client.ReadInt16("E2_100.03"); // EM bank 2, word 100, bit 3
```

## Read/Write Examples

```csharp
using Nexus.Omron;

using var client = new FinsTcpClient("192.168.1.10");
client.Connect();

// Word reads
short dm100 = client.ReadInt16("D100").Content;
int dm200 = client.ReadInt32("D200").Content;      // DM200+DM201
float dm300 = client.ReadFloat("D300").Content;     // DM300+DM301

// Bit reads
bool cioBit = client.ReadBool("CIO100.03").Content;
bool dmBit = client.ReadBool("D50.15").Content;

// Writes
client.Write("D100", (short)42);
client.Write("D200", 123456);
client.Write("D300", 3.14f);
client.Write("CIO100.03", true);

// String
client.Write("D400", "Hello");
string text = client.ReadString("D400", 5).Content;
```

## Area-Specific Notes

### CIO Area

CIO addresses depend on the PLC model and configuration. Lower addresses map to I/O points (fixed by the PLC rack/slot layout), while higher addresses are available as internal work bits. Consult the PLC's I/O allocation table before reading CIO.

### AR Area

AR (Auxiliary Relay) contains system status flags. Common examples:
- AR0: Clock/calendar data
- AR2: Error flags
- AR4–AR5: Status information

AR bits are **read-only** for system status. Writing to AR may cause unexpected behavior.

### Timer/Counter

Timer PV (T) and Counter PV (C) hold current values as 16-bit signed integers. Timer Flags (TF) and Counter Flags (CF) are single-bit completion indicators.
