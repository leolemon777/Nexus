using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.Tests;

/// <summary>
/// MC3E UDP Binary/ASCII 帧测试 — 多数据类型读写验证。
/// 覆盖 Binary 和 ASCII 两种编码模式下的 Int16/Int32/UInt16/Float/String 读写。
/// </summary>
public sealed class Mc3EUdpFrameTests
{
    // ═══════════════════════════════════════════
    //  Binary 模式
    // ═══════════════════════════════════════════

    [Fact]
    public void Binary_ReadInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(0, 0x0100);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt16("D0");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)0x0100, read.Content);
    }

    [Fact]
    public void Binary_WriteInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D5", (short)100);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)100, server.GetDRegister(5));
    }

    [Fact]
    public void Binary_ReadInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(10, 0xAAAA);
        server.SetDRegister(11, 0xBBBB);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadInt32("D10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((int)0xAAAABBBB), read.Content);
    }

    [Fact]
    public void Binary_WriteInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D20", unchecked((int)0x11223344));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x1122, server.GetDRegister(20));
        Assert.Equal(0x3344, server.GetDRegister(21));
    }

    [Fact]
    public void Binary_ReadFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        // -1.0f = 0xBF800000
        server.SetDRegister(50, 0xBF80);
        server.SetDRegister(51, 0x0000);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadFloat("D50");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(-1.0f, read.Content);
    }

    [Fact]
    public void Binary_WriteFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var write = client.Write("D60", 1.0f);
        Assert.True(write.IsSuccess, write.Message);
        // 1.0f = 0x3F800000
        Assert.Equal(0x3F80, server.GetDRegister(60));
        Assert.Equal(0x0000, server.GetDRegister(61));
    }

    [Fact]
    public void Binary_ReadString_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(100, 0x4865); // "He"
        server.SetDRegister(101, 0x6C6C); // "ll"
        server.SetDRegister(102, 0x6F00); // "o\0"
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);
        var read = client.ReadString("D100", 4);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal("Hell", read.Content);
    }

    [Fact]
    public void Binary_SequentialWriteRead_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000);

        for (int i = 0; i < 5; i++)
        {
            var write = client.Write("D" + i, (short)(i * 100));
            Assert.True(write.IsSuccess, $"Write D{i} failed: {write.Message}");
        }

        for (int i = 0; i < 5; i++)
        {
            var read = client.ReadInt16("D" + i);
            Assert.True(read.IsSuccess, $"Read D{i} failed: {read.Message}");
            Assert.Equal((short)(i * 100), read.Content);
        }
    }

    // ═══════════════════════════════════════════
    //  ASCII 模式
    // ═══════════════════════════════════════════

    [Fact]
    public void Ascii_ReadInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(0, 0x5678);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadInt16("D0");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((short)0x5678), read.Content);
    }

    [Fact]
    public void Ascii_WriteInt16_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var write = client.Write("D10", (short)999);
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal((ushort)999, server.GetDRegister(10));
    }

    [Fact]
    public void Ascii_ReadInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(20, 0xDEAD);
        server.SetDRegister(21, 0xBEEF);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadInt32("D20");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(unchecked((int)0xDEADBEEF), read.Content);
    }

    [Fact]
    public void Ascii_WriteInt32_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var write = client.Write("D30", unchecked((int)0xCAFEBABE));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0xCAFE, server.GetDRegister(30));
        Assert.Equal(0xBABE, server.GetDRegister(31));
    }

    [Fact]
    public void Ascii_ReadFloat_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        // 0.5f = 0x3F000000
        server.SetDRegister(40, 0x3F00);
        server.SetDRegister(41, 0x0000);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 2000)
        {
            UseAscii = true
        };

        var read = client.ReadFloat("D40");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0.5f, read.Content);
    }

    // ═══════════════════════════════════════════
    //  Fake Server — 支持 D 寄存器，Binary/ASCII 双模式
    // ═══════════════════════════════════════════

    private sealed class Mc3EUdpFakeServer : IDisposable
    {
        private readonly ushort[] _dRegisters = new ushort[1024];
        private readonly bool _useAscii;
        private readonly UdpClient _udp;
        private Thread? _thread;
        private volatile bool _running;

        public int Port { get; }

        public Mc3EUdpFakeServer(bool useAscii)
        {
            _useAscii = useAscii;
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(ReceiveLoop) { IsBackground = true };
            _thread.Start();
        }

        public void SetDRegister(int address, ushort value) => _dRegisters[address] = value;
        public ushort GetDRegister(int address) => _dRegisters[address];

        private void ReceiveLoop()
        {
            while (_running)
            {
                try
                {
                    var remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] requestFrame = _udp.Receive(ref remote);
                    byte[] request = _useAscii ? FromAsciiHex(requestFrame) : requestFrame;

                    byte[] response = HandleRequest(request);
                    byte[] responseFrame = _useAscii ? ToAsciiHex(response) : response;
                    _udp.Send(responseFrame, responseFrame.Length, remote);
                }
                catch
                {
                    if (!_running) return;
                }
            }
        }

        private byte[] HandleRequest(byte[] request)
        {
            if (request.Length < 12) return BuildErrorResponse(request, 0xC001);

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
                    if (dataOffset + 1 >= request.Length) break;
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
            try { _udp.Close(); } catch { }
            _udp.Dispose();
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
