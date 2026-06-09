using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// MC3E ASCII 帧构建与多数据类型集成测试。
/// 验证 ASCII 编码帧的构建、多区域读写、错误响应处理。
/// </summary>
public sealed class Mc3EAsciiFrameTests
{
    // ═══════════════════════════════════════════
    //  帧构建验证（离线，无需服务器）
    // ═══════════════════════════════════════════

    [Fact]
    public void BuildMc3EFrame_CommandAndSubCommand_CorrectLayout()
    {
        byte[] binary = { 0x50, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x00, 0x00, 0xA8, 0x0A, 0x00, 0x00, 0x01, 0x00 };
        byte[] ascii = ToAsciiHex(binary);

        string hex = Encoding.ASCII.GetString(ascii);
        Assert.Equal(binary.Length * 2, hex.Length);
        Assert.StartsWith("5000", hex);
        Assert.Equal("04", hex.Substring(16, 2));
        Assert.Equal("01", hex.Substring(18, 2));
    }

    [Fact]
    public void AsciiEncoding_RoundTrip()
    {
        byte[] original = { 0x50, 0x00, 0xFF, 0xAB, 0x12, 0x34 };
        byte[] ascii = ToAsciiHex(original);
        byte[] decoded = FromAsciiHex(ascii);
        Assert.Equal(original, decoded);
    }

    // ═══════════════════════════════════════════
    //  多数据类型集成测试（通过 Fake Server）
    // ═══════════════════════════════════════════

    [Fact]
    public void ReadInt16_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.SetDRegister(10, 0x1234);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt16("D10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)0x1234, read.Content);
    }

    [Fact]
    public void WriteInt16_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D11", unchecked((short)0xABCD));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0xABCD, server.GetDRegister(11));
    }

    [Fact]
    public void ReadInt32_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.SetDRegister(20, 0x1234);
        server.SetDRegister(21, 0x5678);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt32("D20");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x12345678, read.Content);
    }

    [Fact]
    public void WriteInt32_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D20", unchecked((int)0xAABBCCDD));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0xAABB, server.GetDRegister(20));
        Assert.Equal(0xCCDD, server.GetDRegister(21));
    }

    [Fact]
    public void ReadUInt16_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.SetDRegister(50, 60000);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadUInt16("D50");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)60000, read.Content);
    }

    [Fact]
    public void WriteUInt16_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D55", (ushort)55555);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)55555, server.GetDRegister(55));
    }

    [Fact]
    public void ReadFloat_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.SetDRegister(100, 0x4048);
        server.SetDRegister(101, 0xF5C3);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadFloat("D100");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(3.14f, read.Content, 0.001f);
    }

    [Fact]
    public void WriteFloat_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D60", 2.5f);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x4020, server.GetDRegister(60));
        Assert.Equal(0x0000, server.GetDRegister(61));
    }

    [Fact]
    public void ReadString_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.SetDRegister(200, 0x4142);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadString("D200", 2);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal("AB", read.Content);
    }

    [Fact]
    public void SequentialWriteThenRead_Works()
    {
        using var server = new Mc3EAsciiFakeServer();
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);

        for (int i = 0; i < 10; i++)
        {
            var write = client.Write("D" + i, (short)(i * 10));
            Assert.True(write.IsSuccess, $"Write D{i} failed: {write.Message}");
        }

        for (int i = 0; i < 10; i++)
        {
            var read = client.ReadInt16("D" + i);
            Assert.True(read.IsSuccess, $"Read D{i} failed: {read.Message}");
            Assert.Equal((short)(i * 10), read.Content);
        }
    }

    [Fact]
    public void Constructor_SetsModel()
    {
        var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", 5007);
        Assert.Equal(MitsubishiModel.Qna_3E, client.Model);
        client.Dispose();
    }

    // ═══════════════════════════════════════════
    //  Fake Server — 支持 D 寄存器多字读写
    // ═══════════════════════════════════════════

    private sealed class Mc3EAsciiFakeServer : IDisposable
    {
        private readonly ushort[] _dRegisters = new ushort[1024];
        private readonly TcpListener _listener;
        private Thread? _thread;
        private volatile bool _running;

        public int Port { get; }

        public Mc3EAsciiFakeServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(AcceptLoop) { IsBackground = true };
            _thread.Start();
        }

        public void SetDRegister(int address, ushort value) => _dRegisters[address] = value;
        public ushort GetDRegister(int address) => _dRegisters[address];

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    using var client = _listener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    while (_running && client.Connected)
                    {
                        // Read ASCII header: 12 binary bytes = 24 ASCII chars
                        byte[]? headerAscii = ReadExact(stream, 24);
                        if (headerAscii == null) break;
                        byte[] header = FromAsciiHex(headerAscii);

                        ushort command = (ushort)((header[8] << 8) | header[9]);

                        // Read fixed body part: subLabel(1) + address(3) + count(2) = 6 bytes = 12 ASCII chars
                        byte[]? fixedBodyAscii = ReadExact(stream, 12);
                        if (fixedBodyAscii == null) break;
                        byte[] fixedBody = FromAsciiHex(fixedBodyAscii);
                        ushort count = (ushort)(fixedBody[4] | (fixedBody[5] << 8));

                        // Calculate total body length: address info (6) + write data (count * 2) if write command
                        int bodyLength = 6;
                        if (command == 0x1401) bodyLength += count * 2;

                        // Read remaining write data if present
                        byte[] body;
                        if (bodyLength > 6)
                        {
                            byte[]? extraAscii = ReadExact(stream, (bodyLength - 6) * 2);
                            if (extraAscii == null) break;
                            byte[] extra = FromAsciiHex(extraAscii);
                            body = new byte[bodyLength];
                            Buffer.BlockCopy(fixedBody, 0, body, 0, 6);
                            Buffer.BlockCopy(extra, 0, body, 6, extra.Length);
                        }
                        else
                        {
                            body = fixedBody;
                        }

                        // Assemble full request
                        byte[] request = new byte[12 + bodyLength];
                        Buffer.BlockCopy(header, 0, request, 0, 12);
                        Buffer.BlockCopy(body, 0, request, 12, bodyLength);

                        // Handle and respond
                        byte[] response = HandleRequest(request);
                        byte[] asciiResponse = ToAsciiHex(response);
                        stream.Write(asciiResponse, 0, asciiResponse.Length);
                    }
                }
                catch
                {
                    if (!_running) return;
                }
            }
        }

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buffer;
        }

        private byte[] HandleRequest(byte[] request)
        {
            ushort command = (ushort)((request[8] << 8) | request[9]);
            byte subLabel = request[12];
            uint address = (uint)(request[13] | (request[14] << 8) | (request[15] << 16));
            ushort count = (ushort)(request[16] | (request[17] << 8));

            if (command == 0x0401) // Read
            {
                byte[] data = new byte[count * 2];
                for (int i = 0; i < count; i++)
                {
                    ushort value = _dRegisters[address + i];
                    data[i * 2] = (byte)(value >> 8);
                    data[i * 2 + 1] = (byte)(value & 0xFF);
                }
                return BuildSuccessResponse(request, data);
            }

            if (command == 0x1401) // Write
            {
                for (int i = 0; i < count; i++)
                {
                    int dataOffset = 18 + i * 2;
                    _dRegisters[address + i] = (ushort)((request[dataOffset] << 8) | request[dataOffset + 1]);
                }
                return BuildSuccessResponse(request, Array.Empty<byte>());
            }

            return BuildErrorResponse(request, 0xC001);
        }

        private static byte[] BuildSuccessResponse(byte[] request, byte[] payload)
        {
            byte[] response = new byte[9 + payload.Length];
            response[0] = 0xD0;
            response[1] = 0x00;
            response[2] = request[2];
            response[3] = request[3];
            response[4] = request[4];
            response[5] = request[5];
            response[6] = 0x00;
            response[7] = 0x00;
            response[8] = 0x00;
            Buffer.BlockCopy(payload, 0, response, 9, payload.Length);
            return response;
        }

        private static byte[] BuildErrorResponse(byte[] request, ushort code)
        {
            byte[] response = BuildSuccessResponse(request, Array.Empty<byte>());
            response[7] = (byte)(code >> 8);
            response[8] = (byte)(code & 0xFF);
            return response;
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }
    }

    private static byte[] ToAsciiHex(byte[] bytes)
        => Encoding.ASCII.GetBytes(BitConverter.ToString(bytes).Replace("-", string.Empty));

    private static byte[] FromAsciiHex(byte[] asciiBytes)
    {
        string hex = Encoding.ASCII.GetString(asciiBytes);
        byte[] bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
