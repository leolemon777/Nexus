# Modbus Packet Logging

Modbus clients built on Nexus base classes expose raw TX/RX events for field diagnostics.

## Basic Logging

```csharp
using Nexus.Modbus;

using var client = new ModbusTcpClient("192.168.1.100");

client.OnMessageSent += (_, hex) => Console.WriteLine("TX " + hex);
client.OnMessageReceived += (_, hex) => Console.WriteLine("RX " + hex);
client.OnError += (_, message) => Console.WriteLine("ERR " + message);

client.Connect();
client.ReadInt16("40001");
```

## Export Format Target

The Modbus TCP WPF page can export parsed packet logs as JSON Lines:

```jsonl
{"ts":"2026-06-08T10:30:00.123-04:00","protocol":"modbus-tcp","direction":"TX","hex":"00 01 00 00 00 06 01 03 00 00 00 01"}
{"ts":"2026-06-08T10:30:00.130-04:00","protocol":"modbus-tcp","direction":"RX","hex":"00 01 00 00 00 05 01 03 02 12 34"}
```

Current WPF behavior:

- Raw TX/RX lines are still shown in the communication log.
- A parsed `[PKT]` summary is appended after each raw Modbus TCP packet.
- `导出TXT` exports the visible text log.
- `导出JSONL` exports structured Modbus TCP packet records, including transaction id, unit id, function code, address, quantity, byte count, data, validity, and parser error text.

## Offline Parser API

`Nexus.Modbus` includes a diagnostic parser for packet capture and WPF tooling:

```csharp
using Nexus.Modbus;

byte[] frame = { 0x00, 0x01, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x01 };

ModbusPacketInfo packet = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Request);
if (!packet.IsValid)
    Console.WriteLine(packet.Error);
```

Available entry points:

- `ParseTcp` for Modbus TCP MBAP frames.
- `ParseUdp` for Modbus UDP MBAP frames.
- `ParseRtu` for serial RTU frames with CRC16.
- `ParseAscii` for ASCII frames with LRC.
- `ParseRtuOverTcp` for RTU ADU frames carried through TCP, including CRC16.
- `Parse(byte[], ModbusPacketTransport, ModbusPacketDirection)` for transport-driven callers.

The parser returns `ModbusPacketInfo` with transport, direction, transaction id, unit/station, function code, exception code, address, quantity, byte count, data, checksum status, raw frame, validity, and error text. Direction inference is diagnostic-grade; pass `Request` or `Response` when the caller already knows it.

## Remaining Parser Requirements

A Modbus packet parser should decode:

- Exception responses and exception code meaning.
- Latency correlation between request and response when transaction ids are available.

## Field Diagnostic Bundle

When a field issue is reported, capture:

1. Connection settings.
2. Selected byte order and string encoding.
3. TX/RX packet log.
4. Error log.
5. Device model and firmware.
6. Whether the issue reproduces against `ModbusTcpServer`.
