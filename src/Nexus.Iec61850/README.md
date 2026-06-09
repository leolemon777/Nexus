# Nexus.Iec61850

IEC 61850 MMS/GOOSE protocol client for smart substations.

## Quick Start

```csharp
using Nexus.Iec61850;

using var client = new Iec61850Client("192.168.1.100", port: 102);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadInt16("LD0/LLN0.GGIO1.Ind1.stVal");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- MMS (Manufacturing Message Specification) over TCP.
- GOOSE subscription support.
- IEC 61850 object model addressing.
- Test coverage (15 tests).
