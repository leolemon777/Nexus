# Nexus.Ftp

简易 FTP 客户端 for PLC 程序上传/下载场景。

## Quick Start

```csharp
using Nexus.Ftp;

using var client = new FtpClient("192.168.1.100", port: 21);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var files = client.ListDirectory("/");
```

## Features

- FTP 连接、上传、下载、目录列表。
- PLC 程序文件传输场景优化。
- Test coverage (6 tests).
