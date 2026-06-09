# Nexus.Dnp3

DNP3 (Distributed Network Protocol v3) client for power utility SCADA systems.

## Quick Start

```csharp
using Nexus.Dnp3;

using var client = new Dnp3Client("192.168.1.100", port: 20000);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("1.0.0");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- DNP3 application layer protocol over TCP.
- Binary input, binary output, analog input, analog output.
- Test coverage (12 tests).
