# Nexus — Open-Source Industrial Communication Library

> 🆓 MIT Licensed · Free Forever · No Restrictions

Nexus is an open-source, modern industrial communication library and debugger for .NET. It aims to be a free, high-quality alternative to commercial solutions, supporting mainstream PLC protocols including Modbus, Siemens S7, Mitsubishi MC, Omron FINS, Allen-Bradley CIP, and more.

## Features

- 📡 **Multi-Protocol**: Modbus TCP/RTU/ASCII, Siemens S7, Mitsubishi MC, Omron FINS, AB CIP
- 🎨 **Modern UI**: 25 color themes × 15 form styles = 375 combinations
- 🧩 **Modular**: Install only the protocols you need via NuGet
- 🧪 **Virtual PLC**: Built-in virtual PLC servers for testing without hardware
- ⚡ **Modern C#**: Async-first, nullable, .NET Standard 2.0 core
- 🆓 **MIT License**: Free for everyone, forever

## Quick Start

```csharp
// Modbus TCP
var client = new ModbusTcpClient("192.168.1.100", 502, station: 1);
var result = await client.ReadInt16Async("100");
if (result.IsSuccess) Console.WriteLine(result.Content);

// Siemens S7
var plc = new SiemensS7Client(SiemensModel.S1200, "192.168.1.110");
await plc.ConnectAsync();
var value = (await plc.ReadInt16Async("DB1.DBW100")).Content;
```

## Roadmap

- [x] Phase 0: Project skeleton + WPF UI framework
- [ ] Phase 1: Modbus TCP protocol + virtual server
- [ ] Phase 2: Siemens S7 protocol
- [ ] Phase 3: Mitsubishi MC protocol
- [ ] Phase 4: Omron FINS + Modbus RTU/ASCII
- [ ] Phase 5: AB CIP + Panasonic + Keyence
- [ ] Phase 6: Real-time monitoring + IoT middleware
- [ ] Phase 7: Documentation + NuGet release

## License

MIT License — free for personal and commercial use.
