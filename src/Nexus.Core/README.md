# Nexus.Core

Core library for Nexus — the open-source industrial communication framework for .NET.

Provides unified interfaces, base classes, data conversion, and infrastructure for all Nexus protocol clients.

## Key Types

| Type | Description |
|------|-------------|
| `OperateResult` / `OperateResult<T>` | Result type for all device operations (no exceptions) |
| `IReadWriteDevice` | Unified read/write interface for all protocols |
| `IBatchReadWrite` | Batch and random read/write operations |
| `ISubscribeDevice` | Polling-based change notifications |
| `TcpDeviceBase` | Base class for TCP protocol clients |
| `SerialDeviceBase` | Base class for serial protocol clients |
| `UdpDeviceBase` | Base class for UDP protocol clients |
| `DataConverter` | Big-endian byte encoding/decoding with unsafe pointer casts |
| `StructConverter` | Map bytes to/from C# structs with byte order support |
| `Endianness` | ABCD / DCBA / BADC / CDAB byte order support |
| `ConnectionPool<T>` | Thread-safe per-key connection pooling |
| `AutoReconnectGuard` | Automatic reconnection management |
| `HeartbeatGuard` | Connection health monitoring |
| `DataAcquisitionEngine` | Multi-device polling scheduler with IDataSink extensibility |
| `DtuClient` | DTU transparent transmission (4G/Ethernet serial-over-TCP) |

## Target Framework

netstandard2.0 — zero external dependencies, works with .NET Framework 4.6.1+, .NET Core 2.0+, .NET 5/6/7/8.

## Install

NuGet packaging is planned for the open-source release. Reference the project directly:

```xml
<ProjectReference Include="..\Nexus.Core\Nexus.Core.csproj" />
```
