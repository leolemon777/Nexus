# Nexus.Mqtt

MQTT 3.1.1 client and lightweight embedded broker for Nexus.

## Quick Start

```csharp
using Nexus.Mqtt;

using var client = new MqttClient("broker.hivemq.com", port: 1883);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

client.Publish("sensors/temperature", "22.5");
```

## Supported Clients

| Client | Transport | Notes |
|--------|-----------|-------|
| `MqttClient` | TCP | MQTT 3.1.1 client with QoS 0/1/2 |
| `MqttBroker` | TCP | Lightweight embedded MQTT broker |

## Features

- MQTT 3.1.1 CONNECT/PUBLISH/SUBSCRIBE/UNSUBSCRIBE/PINGREQ/DISCONNECT.
- QoS 0, 1, and 2 support.
- Topic wildcard matching.
- Lightweight embedded broker for testing.
- Test coverage (65 tests).
