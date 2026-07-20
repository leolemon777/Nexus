# Phase B Migration Notes — How to Migrate a Protocol to the New Bases

This document guides protocol authors through migrating an existing client from
the legacy bases (`TcpDeviceBase` / `SerialDeviceBase` / `UdpDeviceBase`) to the
new Phase B architecture (`DeviceCommunication` + `CommunicationPipe` +
`INetMessage` + `IByteTransform`).

## TL;DR

For a TCP protocol:

```csharp
// BEFORE (legacy)
public class MyClient : TcpDeviceBase
{
    public MyClient(string ip, int port) : base(ip, port) { }

    protected override int ResponseHeaderLength => 8;
    protected override int GetResponsePayloadLength(byte[] header)
        => /* parse header */;

    public override OperateResult<short> ReadInt16(string address)
    {
        var request = BuildRequest(address);
        var resp = SendAndReceive(request);
        if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
        return OperateResult<short>.Success(/* parse resp */);
    }
}
```

```csharp
// AFTER (Phase B)
public class MyClient : DeviceTcpNet
{
    public MyClient(string ip, int port) : base(ip, port)
    {
        MessageFrame = new MyProtocolMessage();   // INetMessage
        ByteTransform = RegularByteTransform.Instance;  // or other endianness
    }

    public override OperateResult<short> ReadInt16(string address)
    {
        var request = BuildRequest(address);
        var resp = ReadFromCoreServer(request);
        if (!resp.IsSuccess) return OperateResult<short>.Failed(resp.Message);
        return OperateResult<short>.Success(/* parse resp */);
    }
}
```

## Why Migrate?

1. **Transport-agnostic** — switching TCP → SSL/DTU/serial is one constructor
   change, not a full reimplementation.
2. **No NIE footgun** — the new base returns `OperateResult.Failed` for any
   operation the protocol doesn't override, instead of throwing
   `NotImplementedException`.
3. **No dual-lock bug** — the legacy `SerialDeviceBase` had separate
   `lock(_lock)` and `_asyncLock`; the new `CommunicationPipe` owns a single
   pluggable `ICommunicationLock`.
4. **CancellationToken everywhere** — async paths honor CT.
5. **Cleaner testability** — `PipeTcpNet`/`PipeUdpNet` can be replaced with a
   fake for unit tests; `INetMessage` is a pure function on byte arrays.

## Step-by-Step

### 1. Pick the convenience base

- TCP → `DeviceTcpNet`
- UDP → `DeviceUdpNet`
- Serial → `DeviceSerialPort`

For unusual combinations (TLS-over-TCP, MQTT tunneling, Moxa serial), inherit
`DeviceCommunication` directly and inject the appropriate `CommunicationPipe`.

### 2. Provide the `INetMessage` (frame parser)

If your protocol has a length-prefixed header (most do), implement
`NetMessageBase`:

```csharp
public sealed class MyProtocolMessage : NetMessageBase
{
    public override int ProtocolHeadBytesLength => 8;  // your header size

    public override int GetContentLength(byte[] head)
    {
        // parse head and return payload length
    }

    public override bool CheckHeadBytesLegal(byte[] head)
    {
        // optional: validate magic bytes / version
    }
}
```

For protocols without a fixed header (e.g., text protocols ending in `\n`),
wrap them in a `SpecifiedCharacterMessage` (planned for a future PR — for now,
you can skip `MessageFrame` and use the legacy fixed-length path via
`GetResponseLength()`).

### 3. Pick the `IByteTransform` (endianness)

- `RegularByteTransform.Instance` — big-endian ABCD (most PLCs)
- `ReverseBytesTransform.Instance` — little-endian DCBA
- `ReverseWordTransform.MidBigEndianInstance` — BADC
- `ReverseWordTransform.MidLittleEndianInstance` — CDAB

Set in constructor: `ByteTransform = ByteTransformFactory.ForEndianness(yourByteOrder)`.

### 4. Implement Read/Write operations

Override only the `IReadWriteDevice` methods your protocol supports. The base
returns `OperateResult.Failed("当前协议未支持 ReadX")` for the rest.

### 5. Migrate tests

For tests using a virtual server, inherit `DeviceServer` instead of writing
the `TcpListener` boilerplate.

## Reference

`src/Nexus.LengthPrefixTcp/` is a minimal reference implementation showing
all the pieces fitting together (~60 lines of client code + tests).
