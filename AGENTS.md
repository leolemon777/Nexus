# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Build & Test Commands

```bash
# Build entire solution (uses .slnx format, NOT .sln)
dotnet build Nexus.slnx

# Run all tests (~3886 tests across ~64 test projects)
dotnet test Nexus.slnx

# Run a single test project
dotnet test tests/Nexus.Siemens.Tests

# Run a specific test by name
dotnet test Nexus.slnx --filter "FullyQualifiedName~SiemensFetchWrite"

# Run the WPF debugging app
dotnet run --project src/Nexus.App

# Restore packages (needed after adding new .csproj files)
dotnet restore Nexus.slnx

# Run benchmarks (Release mode required for accurate results)
dotnet run --project tests/Nexus.Benchmarks -c Release
```

## Architecture Overview

Nexus is an open-source industrial communication library targeting HslCommunication replacement. ~54 protocol libraries target netstandard2.0 with zero external dependencies. A WPF debugger app (Nexus.App) targets net8.0-windows.

### Layer Structure

```
Nexus.Core (netstandard2.0)              — Unified interfaces & base classes
  └── Nexus.{Protocol} (netstandard2.0)  — Protocol client libraries (41 total)
        └── Nexus.App (net8.0-windows)   — WPF debugger application
```

### Core Types (Nexus.Core)

- **`OperateResult` / `OperateResult<T>`** — All device operations return this instead of throwing. Check `IsSuccess`, read `Message` on failure, access typed `Content` on success. **`Content` is a value type for numeric reads — never use `?.` on it.**
- **`IReadWriteDevice`** — Unified interface with sync/async Read/Write for Bool, Int16–UInt64, Float, Double, String, Bytes. All protocol clients implement this. Extended interfaces: `IBatchReadWrite` (batch/random operations), `ISubscribeDevice` (polling-based change notifications).
- **`TcpDeviceBase`** — Abstract base for TCP clients. Subclasses implement only `ResponseHeaderLength` and `GetResponsePayloadLength(byte[])`. Key internals: `SendAndReceive(byte[])` auto-connects, sends, reads header+payload, optionally disconnects (short-connection mode). Uses `_lock` for thread safety, `_stream` for NetworkStream access. Supports persistent mode via `SetPersistentConnection()`. Has `*CoreAsync` virtual methods for true async override (default falls back to sync).
- **`SerialDeviceBase`** — Abstract base for serial port clients. Takes `ISerialPort` (not `System.IO.Ports.SerialPort`) for netstandard2.0 compatibility. Has auto-reconnect-once in `SendAndReceive`.
- **`UdpDeviceBase`** — Abstract base for UDP clients. Supports broadcast via `SendBroadcast()`.
- **`DataConverter`** — Big-endian byte encoding/decoding. Uses `unsafe` pointer casts for float↔int conversion (netstandard2.0 lacks `BitConverter.Int32BitsToSingle`).
- **`CrcCalculator`** — CRC16-Modbus (lookup table) and LRC for Modbus RTU/ASCII.
- **`Endianness`** — Four byte orders: BigEndian(ABCD), LittleEndian(DCBA), MidBigEndian(BADC), MidLittleEndian(CDAB).
- **`ConnectionPool<T>` / `IConnectionPool<T>`** — Thread-safe per-key pooling with `ConcurrentDictionary<string, DeviceBucket>`, idle cleanup timer, and `SemaphoreSlim` concurrency limit. Currently exists in Core but not yet consumed by any protocol client.
- **`DtuClient`** — DTU transparent transmission (4G/Ethernet serial-over-TCP), common in Chinese factory deployments.
- **`ILogger`** / `IMessageLogger` — Logging abstractions with `NullLogger` and `ConsoleLogger` defaults.

### Protocol Library Pattern

Each protocol library follows this structure:
- `{Name}Client.cs` — Main client inheriting TcpDeviceBase/SerialDeviceBase/UdpDeviceBase, implementing IReadWriteDevice
- `{Name}Address.cs` — Address parsing (implements `IDataAddress` + `IAddressParser<T>`)
- `{Name}Model.cs` — Enums and data structures
- `{Name}VirtualServer.cs` — TCP server simulating the real device (for integration tests)

**Key architectural detail**: `TcpDeviceBase.SendAndReceive()` auto-connects before sending. Clients that bypass `SendAndReceive` and use `_stream` directly (e.g., Siemens S7 for multi-step handshake) must implement their own `EnsureConnected()` and handle connection lifecycle.

### Cross-Project Dependencies

Most protocol libraries reference only `Nexus.Core`. Exception:
- `Nexus.Robot.Estun` → `Nexus.Modbus` (Estun robot uses Modbus TCP internally)

### WPF App (Nexus.App)

- **MVVM**: CommunityToolkit.Mvvm `[ObservableProperty]` + `[RelayCommand]`
- **`ProtocolViewModelBase`** — All 29 protocol page VMs inherit this; provides Connect/Disconnect/Read/Write commands, address validation via `AddressValidator`, write confirmation via `WriteConfirmationService`, Chinese error diagnostics via `ChineseDiagnostics`, timestamped log (500-line FIFO ObservableCollection).
- **Page/VM wiring**: Frame creates Pages via reflection → code-behind resolves VM via `App.Services.GetRequiredService<TViewModel>()` → sets DataContext. Pages have no constructor injection.
- **DI registration**: VMs as `AddTransient<T>()` in `App.xaml.cs`; services as singletons. `MainViewModel` is singleton.
- **Navigation**: Defined by `NavGroup`/`NavItem` collections in `MainViewModel.cs`. Each NavItem maps to a Page Type.
- **Configuration**: `IOptions<T>` pattern via `appsettings.json`. Example: `ModbusOptions` binds from "Modbus" section.
- **Theme**: `ThemeManager.Init("mono", "soft")` — **⚠️ NEVER change this default.**

### Adding a New Protocol Library

1. Create `src/Nexus.{Name}/Nexus.{Name}.csproj` (netstandard2.0, reference Nexus.Core, `AllowUnsafeBlocks` if needed for pointer casts)
2. Create client class inheriting `TcpDeviceBase`/`SerialDeviceBase`/`UdpDeviceBase`, implementing `IReadWriteDevice`
3. Optionally implement `IBatchReadWrite` and/or `ISubscribeDevice` for advanced features
4. Create `tests/Nexus.{Name}.Tests/` (net8.0, xunit, reference src project + Nexus.Core)
5. Add both projects to `Nexus.slnx` (XML-based format, not `.sln`)
6. Run `dotnet restore Nexus.slnx` then `dotnet build Nexus.slnx`
7. Optionally: add WPF page (ViewModel inheriting ProtocolViewModelBase + View XAML + DI registration in App.xaml.cs + NavGroup entry in MainViewModel.cs + address regex in AddressValidator.cs)

### Virtual Servers

13 protocols have TCP virtual servers for integration testing without hardware: AllenBradley (CIP, PCCC), Fatek, Fuji, GeSrtp, Inovance, Mitsubishi (MC3E, A1E), Modbus, Omron (FINS, HostLink), Siemens (S7, FetchWrite), Yaskawa (Memobus), Yokogawa.

## Hard Constraints

- **⚠️ NEVER change the default theme** — must remain `ThemeManager.Init("mono", "soft")`.
- **netstandard2.0 limitations**: No `Span<T>`, no `MemoryExtensions`, no `BitConverter.Int32BitsToSingle`/`SingleToInt32Bits` (use unsafe pointer cast or `BitConverter.ToSingle(BitConverter.GetBytes(v), 0)`), no `string.Contains(string, StringComparison)` (use `.ToLowerInvariant().Contains()`), no `IAsyncDisposable`.
- **`OperateResult<T>.Content`** is a value type for numeric reads — never use `?.` on it.
- **No computer-use tools.**
- **Legal**: Never copy HSL Communication code. Only reference protocol message flows from decompiled HSL code.
- **No `.Result` / `.Wait()`** in WPF app. No `async void` except event handlers.
- **Solution format is `.slnx`** (XML-based), not `.sln`.

## Test Patterns

- Test projects target net8.0, use xUnit
- **Offline tests** (majority): command building (`BuildXxxCommand`), response parsing (`ParseFrom`/`ParseResponse`), address parsing, data model construction — all tested with byte arrays, no hardware needed. Use fake serial ports (`AsciiFakeSerialPort : ISerialPort`) for serial protocol tests.
- **Integration tests**: use virtual servers (TcpListener-based). Example: `ModbusTcpServer` starts on localhost, client connects and exercises full round-trip.
- `Directory.Build.props` sets `Nullable=enable` and `LangVersion=latest` globally

## Project Roadmap

Full plan in `OVERTAKE_HSL_PLAN.md` — 6-phase plan to surpass HslCommunication covering protocol depth, new protocols, infrastructure, ecosystem, and differentiation features.

## Current Implementation Status

**Deep implementations (A-tier)**: Modbus TCP, Siemens S7 (with S7String/WString, batch read/write, PLC control commands)

**Solid implementations (B-tier)**: Modbus RTU/ASCII/UDP/RtuOverTcp, Mitsubishi MC3E Binary/Ascii/UDP, Omron FINS TCP, AllenBradley CIP + PCCC

**Partial/stub implementations**: FX Serial (partial), Siemens PPI (partial) — see `OVERTAKE_HSL_PLAN.md` Phase 0 for the full list of 65 stubs to fill. *(Note: MC3E Ascii and MC3E UDP, previously listed as stubs, are now fully implemented.)*
