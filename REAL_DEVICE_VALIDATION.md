# Real Device Validation Matrix

> ⚠️ **SUPERSEDED** — 当前进度与权威数据见 [`OVERTAKE_HSL_PLAN.md`](OVERTAKE_HSL_PLAN.md)（顶部"当前进度快照"）与 [`CLAUDE.md`](CLAUDE.md)。本文件保留作历史记录。

> Last calibrated: 2026-06-08.
>
> Rule: a protocol is not production-ready until at least one real-device validation row exists or the limitation is explicitly documented.

## Validation Levels

| Level | Meaning |
|-------|---------|
| L0 Source Only | Code and unit tests exist, but no integration or real-device evidence is recorded. |
| L1 Virtual Server | Integration tests pass against a Nexus virtual server or local protocol simulator. |
| L2 Lab Device | Tested against a real device in a controlled environment. |
| L3 Field Device | Tested in a real production or commissioning environment. |
| L4 Long Run | Field or lab device survived long-run validation, reconnect scenarios, and repeated reads/writes. |

## Required Evidence

Each validation row should include:

- Protocol module and client class.
- Device vendor, model, firmware, and communication module if applicable.
- Transport: TCP, UDP, serial, RTU-over-TCP, or other.
- Network/serial settings.
- Tested operations.
- Result summary.
- Known limitations.
- Tester and date.
- Test evidence path if available: logs, packet captures, screenshots, or exported diagnostic bundles.

## Top 5 Target Matrix

| Protocol | Client | Minimum Target | Current Level | Required Device Evidence |
|----------|--------|----------------|---------------|--------------------------|
| Modbus TCP | `ModbusTcpClient` | L4 | L1 | Any industrial Modbus TCP PLC/gateway; read/write coils and holding registers; reconnect test. |
| Modbus RTU | `ModbusRtuClient` | L3 | L0/L1 pending serial fixture | RS485 device or Modbus simulator hardware; CRC failure and timeout behavior. |
| Modbus ASCII | `ModbusAsciiClient` | L2 | L0 | Serial device with ASCII framing; verify LRC checksum handling. |
| Modbus UDP | `ModbusUdpClient` | L2 | L1 | UDP-capable Modbus device or gateway. |
| Siemens S7 | `SiemensS7Client` | L4 | L1 | S7-1200 or S7-1500; DB/I/Q/M read/write; S7String; reconnect. |
| Siemens PPI | `SiemensPpiClient` | L3 | Needs audit | S7-200 or S7-200 SMART over serial/PPI adapter. |
| Mitsubishi MC3E Binary | `Mc3EBinaryClient` | L4 | L1 | Q/L/FX5U or compatible simulator; D/M/X/Y reads/writes; remote run/stop only if safe. |
| Mitsubishi MC3E ASCII | `Mc3EAsciiClient` | L3 | L1 | Same PLC, ASCII frame encoding; verify parity with Binary results. |
| Mitsubishi MC3E UDP | `Mc3EUdpClient` | L2 | L1 | UDP-capable Mitsubishi PLC. |
| Mitsubishi FX Serial | `FxSerialClient` | L3 | L0 | FX3U/FX5U via SC-09 cable; ENQ/ACK/STX/ETX handshake validation. |
| Mitsubishi FX Link | `FxLinkClient` | L2 | L0 | FX via RS-485 multi-drop with station number. |
| Omron FINS TCP | `FinsTcpClient` | L4 | L1 | CJ/CP/NJ/NX PLC; DM/CIO reads/writes; routing/node setup. |
| AllenBradley CIP | `AllenBradleyCipClient` | L4 | L1 | ControlLogix or CompactLogix; DINT/REAL/BOOL/String tags; fragmented tag read. |

## Validation Records

Add real records below. Keep newest first.

| Date | Level | Protocol | Client | Device | Firmware | Transport | Settings | Operations | Result | Evidence | Notes |
|------|-------|----------|--------|--------|----------|-----------|----------|------------|--------|----------|-------|
| TBD | L0 | Modbus TCP | `ModbusTcpClient` | TBD | TBD | TCP | Port 502, Station 1 | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Siemens S7 | `SiemensS7Client` | TBD | TBD | TCP | Rack/slot TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Mitsubishi MC3E | `Mc3EBinaryClient` | TBD | TBD | TCP | Port 6000, Qna_3E | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | Omron FINS TCP | `FinsTcpClient` | TBD | TBD | TCP | Network/node TBD | TBD | Pending | TBD | First real-device validation needed. |
| TBD | L0 | AllenBradley CIP | `AllenBradleyCipClient` | TBD | TBD | TCP | Slot/path TBD | TBD | Pending | TBD | First real-device validation needed. |

## Detailed Target Rows

### Modbus TCP — Target Detail

**Target devices** (pick first available):

| Priority | Device | Why |
|----------|--------|-----|
| 1 | Modbus TCP gateway (e.g. USR-TCP232, Moxa NPort) | Common in Chinese factories, easy to set up |
| 2 | Schneider Modicon M221 / M340 | Standard Modbus TCP PLC |
| 3 | Delta DVP series with Ethernet module | Widely available |
| 4 | Wago 750-xxx series | Common in European installations |

**Safe scratch address plan:**

| Address | Type | Purpose |
|---------|------|---------|
| 40001–40010 | Holding registers | Numeric read/write (Int16/Int32/Float) |
| 00001–00008 | Coils | Bool read/write |
| 30001–30004 | Input registers | Read-only verification |
| 10001–10008 | Discrete inputs | Read-only verification |

**Required operations for L2:**

- [ ] Connect/disconnect 10 times, confirm no socket leak
- [ ] Read `ReadInt16("40001")` and verify expected value
- [ ] Write then read-back `Write("40001", (short)1234)` → `ReadInt16("40001")` matches
- [ ] Read/write `ReadInt32("40003")` / `Write("40003", 0x12345678)` (byte order sensitive)
- [ ] Read/write `ReadFloat("40005")` / `Write("40005", 3.14f)` (byte order sensitive)
- [ ] Read `ReadBool("00001")` and `ReadBool("10001")`
- [ ] Write `Write("00001", true)` and read-back
- [ ] Trigger bad address (e.g. `ReadInt16("49999")`) and confirm clear error message
- [ ] Disconnect Ethernet cable mid-session, confirm reconnect behavior
- [ ] Record TX/RX packet log via WPF PacketRecorder export

**Required operations for L4 (long run):**

- [ ] 1-hour polling loop: `ReadInt16("40001")` every 1 second, log success/failure count
- [ ] Reconnect test: kill Ethernet for 10 seconds, confirm auto-recovery
- [ ] Batch read: `BatchRead(new[] { "40001", "40003", "40005" })` vs individual reads match

### Modbus RTU — Target Detail

**Target devices:**

| Priority | Device | Why |
|----------|--------|-----|
| 1 | USB-to-RS485 adapter + Modbus RTU slave simulator | Lab fixture, no hardware needed |
| 2 | Delta DVP series via RS-485 | Common serial PLC |
| 3 | Schneider PM series power meter | Standard Modbus RTU meter |

**Settings:** 9600/8/N/1 (default), Station 1, Timeout 3000ms

**Required operations for L2:**

- [ ] Connect via `ISerialPort` adapter, verify connection
- [ ] Read holding register `ReadInt16("0")` (offset mode) and `ReadInt16("40001")` (standard mode)
- [ ] Write then read-back Int16/Int32/Float
- [ ] Verify CRC error detection (corrupt frame should fail)
- [ ] Test timeout behavior (disconnect serial mid-read)

### Mitsubishi MC3E Binary — Target Detail

**Target devices** (pick first available):

| Priority | Device | Why |
|----------|--------|-----|
| 1 | Mitsubishi FX5U (built-in Ethernet) | Most accessible modern Mitsubishi PLC |
| 2 | Mitsubishi Q series with Ethernet module | Common in factory floors |
| 3 | Mitsubishi iQ-R | Higher-end, same MC3E protocol |
| 4 | GX Works3 simulator + MC protocol | Software-only option |

**Settings:**
- Model: `MitsubishiModel.Qna_3E`
- Port: 6000 (FX5U default)
- Network No: 0, PC No: 0xFF (default)

**Safe scratch address plan:**

| Address | Type | Purpose |
|---------|------|---------|
| D0–D19 | Data registers | Word read/write (Int16/Int32/Float/String) |
| M0–M15 | Internal relays | Bit read/write |
| X0–X7 | Input | Bit read-only |
| Y0–Y7 | Output | Bit read/write (confirm safe first) |

**Required operations for L2:**

- [ ] Connect/disconnect 10 times
- [ ] Read `ReadInt16("D0")` and verify expected value
- [ ] Write then read-back `Write("D0", (short)5678)` → `ReadInt16("D0")` matches
- [ ] Read/write `ReadInt32("D10")` / `Write("D10", 0x12345678)`
- [ ] Read/write `ReadFloat("D20")` / `Write("D20", 3.14f)`
- [ ] Read `ReadBool("M0")` / Write `Write("M0", true)` and read-back
- [ ] Read input `ReadBool("X0")` (read-only, no write)
- [ ] Read/write string `Write("D100", "HELLO")` → `ReadString("D100", 5)` matches
- [ ] Trigger bad address and confirm SLMP error code (e.g. 0xC003)
- [ ] Disconnect Ethernet mid-session, confirm reconnect
- [ ] Record TX/RX packet log

**Required operations for L4 (long run):**

- [ ] 1-hour polling loop: `ReadInt16("D0")` every 1 second
- [ ] Reconnect test: disconnect Ethernet 10s, confirm recovery
- [ ] Sequential write 10 registers, read all back, verify data integrity

### Mitsubishi MC3E ASCII — Target Detail

**Target devices:** Same as MC3E Binary (same PLC, different frame encoding)

**Required operations for L2:**

- [ ] Same operations as MC3E Binary but using `Mc3EAsciiClient`
- [ ] Verify ASCII hex framing in packet log
- [ ] Compare results with Binary client to confirm parity

### Mitsubishi FX Serial — Target Detail

**Target devices:**

| Priority | Device | Why |
|----------|--------|-----|
| 1 | Mitsubishi FX3U with SC-09 cable | Most common FX serial setup |
| 2 | Mitsubishi FX3G | Lower-cost alternative |
| 3 | USB-to-RS422 adapter + FX programming port | Lab setup |

**Settings:** 9600 baud, Even parity, 7 data bits, 1 stop bit (standard FX programming port)

**Required operations for L2:**

- [ ] Connect via `FxSerialClient(ISerialPort)`, verify handshake
- [ ] Read `ReadInt16("D0")` and verify
- [ ] Write then read-back Int16
- [ ] Read `ReadBool("M0")` / Write `Write("M0", true)`
- [ ] Read `ReadBool("X0")` / `ReadBool("Y0")`
- [ ] Verify ENQ/ACK/NAK handshake in raw packet log
- [ ] Test timeout behavior (disconnect serial mid-read)

### Siemens S7 — Target Detail

**Target devices** (pick first available):

| Priority | Device | Why |
|----------|--------|-----|
| 1 | Siemens S7-1200 (CPU 1212C/1214C/1215C) | Most common entry-level S7 PLC |
| 2 | Siemens S7-1500 (CPU 1511/1515) | Higher-end, same protocol |
| 3 | Siemens S7-300 (CPU 313C/315) | Legacy but still widely deployed |

**Settings:**
- Port: 102 (always)
- Model: `SiemensPLCS.S7_1200` or `SiemensPLCS.S7_1500`
- Rack: 0, Slot: 1 (S7-1200/1500)

**TIA Portal prerequisites:**
- [ ] Enable "Permit access with PUT/GET communication" in PLC Properties → Protection
- [ ] Disable "Optimized block access" for target DBs
- [ ] Verify enough connection resources are available

**Safe scratch address plan:**

| Address | Type | Purpose |
|---------|------|---------|
| `DB1.DBW0` | Word (Int16) | Numeric read/write |
| `DB1.DBD2` | DWord (Int32) | 32-bit read/write |
| `DB1.DBD6` | Real (Float) | Float read/write |
| `DB1.DBX10.0` | Bit | Boolean read/write |
| `DB1.DBB20` | String | S7 String read/write |
| `MW0` | Word | Memory area |
| `I0.0` | Bit | Input (read-only) |
| `Q0.0` | Bit | Output (confirm safe) |

**Required operations for L2:**

- [ ] Connect/disconnect 10 times
- [ ] Read `ReadInt16("DB1.DBW0")` and verify expected value
- [ ] Write then read-back `Write("DB1.DBW0", (short)1234)`
- [ ] Read/write Int32: `ReadInt32("DB1.DBD2")` / `Write("DB1.DBD2", 0x12345678)`
- [ ] Read/write Float: `ReadFloat("DB1.DBD6")` / `Write("DB1.DBD6", 3.14f)`
- [ ] Read/write Bool: `ReadBool("DB1.DBX10.0")` / `Write("DB1.DBX10.0", true)`
- [ ] Read/write S7 String: `WriteS7String("DB1.DBB20", "HELLO")` → `ReadS7String("DB1.DBB20")` matches
- [ ] Batch read: `BatchRead(new[] { "DB1.DBW0", "DB1.DBD2" })` vs individual reads match
- [ ] Trigger bad address and confirm clear S7 error code
- [ ] Disconnect Ethernet mid-session, confirm reconnect with guard
- [ ] Record TX/RX packet log

### Omron FINS TCP — Target Detail

**Target devices:**

| Priority | Device | Why |
|----------|--------|-----|
| 1 | Omron CJ2M (built-in Ethernet) | Common mid-range PLC |
| 2 | Omron NX102 (built-in Ethernet) | Modern NJ/NX series |
| 3 | Omron CP1H + Ethernet option board | Compact PLC |

**Settings:**
- Port: 9600 (FINS default)
- FINS/TCP enabled in PLC settings
- Auto node allocation or manual IP Address Table

**Safe scratch address plan:**

| Address | Area | Purpose |
|---------|------|---------|
| `D0`–`D19` | DM | Word read/write (Int16/Int32/Float) |
| `CIO100.00`–`CIO100.15` | CIO | Bit read/write |
| `W0`–`W9` | Work Relay | Bit read/write |
| `H0`–`H9` | Holding Relay | Retained bit |

**Required operations for L2:**

- [ ] Connect/disconnect 10 times
- [ ] Read `ReadInt16("D0")` and verify
- [ ] Write then read-back Int16/Int32/Float
- [ ] Read/write bit: `ReadBool("CIO100.03")` / `Write("CIO100.03", true)`
- [ ] Read holding relay: `ReadBool("H0.00")`
- [ ] Trigger bad address, confirm FINS error code (e.g., 0x0302)
- [ ] Disconnect Ethernet, confirm reconnect
- [ ] Record TX/RX packet log

### AllenBradley CIP — Target Detail

**Target devices:**

| Priority | Device | Why |
|----------|--------|-----|
| 1 | CompactLogix 5380 (5069-L306ER) | Common mid-range controller |
| 2 | ControlLogix 5580 (1756-L83E) | Higher-end |
| 3 | Micro850 (2080-LC50-24QWB) | Entry-level with CIP |

**Settings:**
- Port: 44818 (EtherNet/IP default)
- Slot: 0 (CompactLogix/Micro800 always)

**Studio 5000 prerequisites:**
- [ ] Create test tags (DINT, REAL, BOOL, STRING) in controller scope
- [ ] Verify Ethernet module firmware supports CIP

**Safe scratch address plan:**

| Address | Type | Purpose |
|---------|------|---------|
| `Test_DINT` | DINT | 32-bit read/write |
| `Test_REAL` | REAL | Float read/write |
| `Test_BOOL` | BOOL | Boolean read/write |
| `Test_STRING` | STRING | String read/write |
| `Test_Array[0]`–`[9]` | DINT[] | Array access |
| `Test_UDT.Speed` | DINT (UDT member) | UDT member access |

**Required operations for L2:**

- [ ] Connect/disconnect 10 times
- [ ] Read `ReadInt32("Test_DINT")` and verify
- [ ] Write then read-back: `Write("Test_DINT", 42)` → matches
- [ ] Read/write REAL: `ReadFloat("Test_REAL")` / `Write("Test_REAL", 3.14f)`
- [ ] Read/write BOOL: `ReadBool("Test_BOOL")` / `Write("Test_BOOL", true)`
- [ ] Read/write STRING: `ReadString("Test_STRING")` / `Write("Test_STRING", "Hello")`
- [ ] Array element: `ReadInt32("Test_Array[5]")`
- [ ] UDT member: `ReadInt32("Test_UDT.Speed")`
- [ ] Batch read: `BatchRead(new[] { "Test_DINT", "Test_REAL" })`
- [ ] Trigger bad tag name and confirm CIP error code 0x04
- [ ] Disconnect Ethernet, confirm reconnect
- [ ] Record TX/RX packet log

For each device, run the narrowest safe subset first:

1. Connect and disconnect 10 times.
2. Read a known read-only value.
3. Read and write a safe scratch register/tag/address.
4. Read multiple adjacent values.
5. Trigger a bad address and confirm diagnostic quality.
6. Disconnect network/serial path and confirm recovery behavior.
7. Run a 1-hour polling loop before attempting long-run validation.

## Safety Rules

- Never write production control addresses during validation.
- PLC control commands such as run/stop/reset require explicit test-bench approval.
- Record exact device settings so failures can be reproduced.
- If a device requires unsafe plant access, mark the row as pending rather than improvising.
