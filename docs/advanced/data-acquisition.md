# Data Acquisition Engine

> Last updated: 2026-06-13

`DataAcquisitionEngine` (`Nexus.Core`) provides multi-device, multi-address unified polling with pluggable data sinks.

## Quick Start

```csharp
using Nexus;

var engine = new DataAcquisitionEngine();

// Register devices
var modbus = new ModbusTcpClient("192.168.1.10", 502);
engine.RegisterDevice("plc1", modbus, new PollConfig { IntervalMs = 1000 });

// Add monitoring points
engine.AddPoint("plc1", "40001", "Int16", "Temperature");
engine.AddPoint("plc1", "40002", "Float", "Pressure");

// Add data sinks
engine.AddSink(new ConsoleDataSink());
engine.AddSink(new MemoryDataSink(capacity: 10000));
engine.AddSink(new CsvDataSink("data.csv"));

// Start polling
engine.Start();
```

## Configuration

### PollConfig

| Parameter | Default | Description |
|-----------|---------|-------------|
| `IntervalMs` | 1000 | Polling interval in milliseconds |
| `TimeoutMs` | 5000 | Read timeout per operation |
| `FailureThreshold` | 3 | Consecutive failures before offline mode |
| `RetryIntervalMs` | 10000 | Retry interval when offline |
| `OnlyOnChange` | true | Only emit samples when value changes |

## Data Sinks

| Sink | Description |
|------|-------------|
| `ConsoleDataSink` | Prints samples to stdout |
| `MemoryDataSink(capacity)` | Ring buffer; `GetAll()` returns samples in order |
| `CsvDataSink(filePath)` | Appends to CSV with auto-header, auto-directory creation |

### Custom Sinks

Implement `IDataSink`:

```csharp
public interface IDataSink
{
    void Write(DataSample sample);
    void Dispose();
}
```

## DataSample

Each sample contains:

| Field | Type | Description |
|-------|------|-------------|
| `DeviceName` | string | Registered device name |
| `Address` | string | Point address |
| `DataType` | string | "Int16", "Float", etc. |
| `Tag` | string | User-defined tag name |
| `Value` | string | Current value as string |
| `Quality` | string | "Good", "Uncertain", or "Bad" |
| `Timestamp` | DateTime | Sample timestamp |

## API Reference

| Method | Description |
|--------|-------------|
| `RegisterDevice(name, device, config)` | Register a device for polling |
| `UnregisterDevice(name)` | Remove a device |
| `AddPoint(device, address, dataType, tag)` | Add a monitoring point |
| `RemovePoint(device, address)` | Remove a monitoring point |
| `AddSink(sink)` / `RemoveSink(sink)` | Manage data sinks |
| `GetCurrentValues()` | Get all current values as Dictionary |
| `ExportToCsv(filePath)` | Export MemoryDataSink contents to CSV |
| `Start()` / `Stop()` | Lifecycle control |

## Events

| Event | Description |
|-------|-------------|
| `OnSample` | Fired on each sample with `DataSampleEventArgs` |
| `OnError` | Fired on polling errors with `DataErrorEventArgs` |

## Internal Behavior

Each registered device gets a `DevicePoller` — a timer-based polling loop that:

1. Auto-connects the device on first poll
2. Reads all registered points sequentially
3. Applies change-detection filtering (`OnlyOnChange`)
4. Tracks consecutive failures and enters offline/retry mode after threshold
5. Emits samples to all registered sinks
