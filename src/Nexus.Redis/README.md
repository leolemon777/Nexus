# Nexus.Redis

Redis client with connection pooling and full command support for Nexus.

## Quick Start

```csharp
using Nexus.Redis;

using var client = new RedisClient("192.168.1.100", port: 6379);
var connect = client.Connect();
if (!connect.IsSuccess) { Console.WriteLine(connect.Message); return; }

var result = client.ReadString("mykey");
if (result.IsSuccess) Console.WriteLine(result.Content);
```

## Features

- Full Redis command support (GET, SET, DEL, HGET, HSET, LPUSH, RPUSH, etc.).
- Connection pooling for high-throughput scenarios.
- Pub/Sub support.
- Test coverage (33 tests).
