# Virtual PLC Framework

> Last updated: 2026-06-13

The Virtual PLC framework (`Nexus.VirtualPlc`) provides a configurable in-memory PLC simulator for testing, demo, and development without physical hardware.

## Architecture

```
VirtualPlcHost
  ├── VirtualPlcMemory       — shared address space (cross-protocol)
  ├── VirtualPlcRuleEngine   — timer-based rule evaluation
  └── ScenarioScript         — initial state + rules
```

## VirtualPlcMemory

A thread-safe, cross-protocol shared address space supporting multiple memory regions:

| Region | Key Type | Value Type | Description |
|--------|----------|------------|-------------|
| Coils | `int` | `bool` | Generic coil storage |
| Registers | `int` | `short` | Generic register storage |
| Holding Registers | `ushort` | `short` | Modbus-style |
| Input Registers | `ushort` | `short` | Modbus-style |
| Modbus Coils | `ushort` | `bool` | Modbus-style |
| Discrete Inputs | `ushort` | `bool` | Modbus-style |
| DB Bytes | `long` | `byte` | S7-style (dbNumber << 32 \| offset) |

Multi-word operations (Int32, Float, Int64, Double) use big-endian word order with atomic locking.

### Events

`OnWrite` fires on every Bool/Int16 write with address, value, data type, and timestamp.

## ScenarioScript

Defines initial memory state and automation rules:

```csharp
var scenario = new ScenarioScript
{
    Name = "Temperature Sensor",
    RegisterPresets = new List<RegisterPreset>
    {
        new RegisterPreset { Address = 0, Value = 250 },   // D0 = 25.0°C
        new RegisterPreset { Address = 1, Value = 800 },   // D1 = alarm threshold
    },
    CoilPresets = new List<CoilPreset>
    {
        new CoilPreset { Address = 0, Value = false },     // M0 = alarm output
    },
    Rules = new List<ScenarioRule>
    {
        new ScenarioRule
        {
            Name = "HighTempAlarm",
            WatchAddress = 0,
            TriggerValue = 800,
            TargetAddress = 0,
            TargetValue = true,
            IsBoolTarget = true
        }
    }
};

var host = new VirtualPlcHost();
host.LoadScenario(scenario);
host.Start();
```

### Built-in Scenarios

| Scenario | Description |
|----------|-------------|
| `BuiltInScenarios.TemperatureSensor()` | D0=25.0°C, D1=alarm threshold, M0=alarm output |
| `BuiltInScenarios.MotorControl()` | M0=start, M1=stop, D10=target RPM, D11=current RPM |
| `BuiltInScenarios.ConveyorBelt()` | D20=speed, D21=counter, M3=running, M4=sensor |
| `BuiltInScenarios.PidTemperature()` | PID controller with Kp/Ki/Kd registers |
| `BuiltInScenarios.RandomWalkSensor()` | Random walk between 100-500 |
| `BuiltInScenarios.SineWaveSensor()` | Amplitude 100, period 5000ms, offset 300 |
| `BuiltInScenarios.Blank()` | Empty memory |

### JSON Loading

```csharp
host.LoadScenarioFromJson("scenario.json");
host.LoadScenarioFromJsonString(jsonString);
```

JSON schema:
```json
{
  "name": "My Scenario",
  "description": "Test scenario",
  "registers": [{ "address": 0, "value": 100 }],
  "coils": [{ "address": 0, "value": true }],
  "rules": [{ "name": "Rule1", "watchAddress": 0, "triggerValue": 200, "targetAddress": 1, "targetValue": 300 }]
}
```

## Rule Engine

`VirtualPlcRuleEngine` evaluates rules on a configurable timer (default 100ms):

- **TriggerValue = -1**: fires on any change (tracks last value)
- **Exact match**: fires when value equals trigger
- **Delayed actions**: `DelayMs > 0` uses `ThreadPool.QueueUserWorkItem`
- **Action expressions**: `pid(...)`, `random_walk(...)`, `sine(...)`

### Events

`OnRuleFired` fires with rule name, target address, and timestamp.

## Snapshot

```csharp
var snapshot = host.GetSnapshot();
// snapshot.HostName, snapshot.ScenarioName, snapshot.IsRunning,
// snapshot.RuleCount, snapshot.FireCount
```
