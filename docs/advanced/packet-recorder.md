# Packet Recorder

> Last updated: 2026-06-13

`PacketRecorder` (`Nexus.Core`) captures raw TX/RX hex packets from `TcpDeviceBase` devices for debugging and protocol analysis.

## Quick Start

```csharp
using Nexus;

var recorder = new PacketRecorder();
var client = new ModbusTcpClient("192.168.1.10", 502);

// Attach to device
recorder.Attach(client);
recorder.StartRecording();

// Perform operations
client.Connect();
client.ReadInt16("40001");
client.Write("40001", (short)42);

// Stop and analyze
recorder.StopRecording();
var analysis = recorder.Analyze();
Console.WriteLine($"Total: {analysis.TotalPackets}, Avg RTT: {analysis.AverageResponseTimeMs}ms");

// Export
recorder.ExportToJsonl("capture.jsonl");
```

## API Reference

| Method | Description |
|--------|-------------|
| `Attach(device)` | Hook into device's OnMessageSent/OnMessageReceived events |
| `Detach(device)` | Unhook from device events |
| `StartRecording()` | Begin capturing packets |
| `StopRecording()` | Stop capturing |
| `Clear()` | Wipe all captured entries |
| `GetEntries()` | Return List<PacketEntry> snapshot |
| `ExportToJsonl(filePath)` | Write JSONL format (one JSON object per line) |
| `Analyze()` | Return PacketAnalysis summary |

## PacketEntry

| Field | Type | Description |
|-------|------|-------------|
| `Timestamp` | DateTime | Capture time |
| `Direction` | string | "TX" or "RX" |
| `HexData` | string | Hex-encoded packet data |
| `Description` | string | Optional description |

## PacketAnalysis

| Field | Type | Description |
|-------|------|-------------|
| `TotalPackets` | int | Total captured packets |
| `Duration` | TimeSpan | Time span from first to last packet |
| `TxCount` | int | Transmitted packets |
| `RxCount` | int | Received packets |
| `AverageResponseTimeMs` | double | Average round-trip time |
| `Errors` | int | Error count |

## WPF Integration

The WPF app includes `PacketRecorderService` with structured Modbus packet recording:

- `RecordMbap(...)` — TCP/MBAP packets
- `RecordRtu(...)` — RTU packets
- `RecordAscii(...)` — ASCII packets
- `GetStats()` — PacketStats with latency, FC frequency, exception counts
- `GetExceptions()` — Filtered list of exception/error packets
- `ExportJsonl(filePath)` — JSONL export

Capacity: 2000 records (FIFO).
