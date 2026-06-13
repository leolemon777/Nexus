# Protocol Bridge

> Last updated: 2026-06-13

`ProtocolBridge` (`Nexus.Bridge`) provides polling-based data bridging from industrial protocols to MQTT or console targets.

## Quick Start

```csharp
using Nexus.Bridge;

var config = new BridgeConfig
{
    SourceType = "ModbusTcp",
    SourceIp = "192.168.1.10",
    SourcePort = 502,
    TargetType = "Mqtt",
    TargetHost = "mqtt.example.com",
    TargetPort = 1883,
    MqttTopicPrefix = "factory/plc1/",
    MqttClientId = "nexus-bridge",
    PollIntervalMs = 1000,
    Points = new List<BridgePoint>
    {
        new BridgePoint { Address = "40001", DataType = "Int16", Tag = "temperature" },
        new BridgePoint { Address = "40002", DataType = "Float", Tag = "pressure", Scale = 0.1, Offset = -50 },
    }
};

var bridge = new ProtocolBridge(config);
var result = bridge.Start();
if (result.IsSuccess)
    Console.WriteLine("Bridge running");
```

## Configuration

### BridgeConfig

| Parameter | Description |
|-----------|-------------|
| `SourceType` | Source protocol ("ModbusTcp") |
| `SourceIp` | Source device IP |
| `SourcePort` | Source device port |
| `SourceStation` | Source station/unit ID |
| `TargetType` | Target type ("Mqtt" or "Console") |
| `TargetHost` | Target host |
| `TargetPort` | Target port |
| `MqttTopicPrefix` | MQTT topic prefix |
| `MqttClientId` | MQTT client ID |
| `PollIntervalMs` | Polling interval (default 1000ms) |
| `Points` | List of BridgePoint |

### BridgePoint

| Parameter | Default | Description |
|-----------|---------|-------------|
| `Address` | required | Device address |
| `DataType` | "Int16" | Data type to read |
| `Tag` | address | Tag name for output |
| `Scale` | 1.0 | Multiply raw value |
| `Offset` | 0.0 | Add to scaled value |

## Targets

| Target | Class | Description |
|--------|-------|-------------|
| MQTT | `MqttBridgeTarget` | Publishes JSON to `{topicPrefix}{tag}` |
| Console | `ConsoleBridgeTarget` | Prints `[timestamp] tag = value (type)` |

### Custom Targets

Implement `IBridgeTarget`:

```csharp
public interface IBridgeTarget
{
    OperateResult Connect();
    void Disconnect();
    void Publish(BridgeData data);
}
```

## BridgeData

| Field | Type | Description |
|-------|------|-------------|
| `Address` | string | Source address |
| `Tag` | string | Tag name |
| `DataType` | string | Data type |
| `RawValue` | object | Raw value from device |
| `ScaledValue` | object | After scale/offset transform |
| `Timestamp` | DateTime | Read timestamp |

## Events

| Event | Description |
|-------|-------------|
| `OnDataBridged` | Fired on each successful data bridge |
| `OnError` | Fired on errors |

## API

| Method | Description |
|--------|-------------|
| `Start()` | Connect source and target, start polling loop |
| `Stop()` | Cancel polling, disconnect |
| `BridgedCount` | Total bridged samples |
| `IsRunning` | Current state |
