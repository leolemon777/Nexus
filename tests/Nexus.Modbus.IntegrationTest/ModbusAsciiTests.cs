using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;
using Nexus.Modbus;

namespace Nexus.Modbus.IntegrationTest;

/// <summary>
/// 简易 Modbus ASCII 虚拟服务器 — TCP 监听，收发 ASCII 帧。
/// 帧格式: ':' + Hex(Station + PDU + LRC) + CR LF
/// </summary>
public sealed class ModbusAsciiTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private volatile bool _running;
    private Thread? _thread;
    private readonly object _lock = new object();

    private readonly bool[] _coils = new bool[65536];
    private readonly bool[] _discreteInputs = new bool[65536];
    private readonly ushort[] _holdingRegisters = new ushort[65536];
    private readonly ushort[] _inputRegisters = new ushort[65536];

    public int Port { get; }

    public ModbusAsciiTestServer(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void SetHoldingRegister(ushort address, ushort value) { lock (_lock) _holdingRegisters[address] = value; }
    public void SetInputRegister(ushort address, ushort value) { lock (_lock) _inputRegisters[address] = value; }
    public void SetCoil(ushort address, bool value) { lock (_lock) _coils[address] = value; }
    public void SetDiscreteInput(ushort address, bool value) { lock (_lock) _discreteInputs[address] = value; }

    public void Start()
    {
        _running = true;
        _thread = new Thread(AcceptLoop) { IsBackground = true };
        _thread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                _listener.Server.ReceiveTimeout = 500;
                var client = _listener.AcceptTcpClient();
                var t = new Thread(() => HandleClient(client)) { IsBackground = true };
                t.Start();
            }
            catch { if (!_running) break; }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using var ns = client.GetStream();
            ns.ReadTimeout = 5000;
            ns.WriteTimeout = 5000;

            while (_running && client.Connected)
            {
                // 等待起始符 ':'
                int b;
                do { b = ns.ReadByte(); } while (b >= 0 && b != ':');
                if (b < 0) break;

                // 读取直到 CR LF
                using var frameMs = new MemoryStream();
                frameMs.WriteByte((byte)':');
                bool sawCr = false;
                while (true)
                {
                    b = ns.ReadByte();
                    if (b < 0) goto done;
                    frameMs.WriteByte((byte)b);
                    if (b == '\r') sawCr = true;
                    else if (sawCr && b == '\n') break;
                    else sawCr = false;
                }

                // 解析帧
                string frameStr = Encoding.ASCII.GetString(frameMs.ToArray());
                string hex = frameStr.TrimStart(':').TrimEnd('\r', '\n');
                if (hex.Length < 4) continue; // 至少 Addr(2) + FC(2)

                byte[] raw = HexToBytes(hex);
                if (raw.Length < 3) continue;

                // 验证 LRC
                int dataLen = raw.Length - 1;
                byte expectedLrc = ComputeLrc(raw, 0, dataLen);
                if (raw[dataLen] != expectedLrc) continue;

                byte station = raw[0];
                byte[] pdu = new byte[dataLen - 1];
                Buffer.BlockCopy(raw, 1, pdu, 0, pdu.Length);

                byte[]? respPdu = ProcessPdu(pdu);
                if (respPdu == null) continue;

                // 构建响应帧: ':' + Hex(Station + RespPDU + LRC) + CR LF
                byte[] respRaw = new byte[1 + respPdu.Length];
                respRaw[0] = station;
                Buffer.BlockCopy(respPdu, 0, respRaw, 1, respPdu.Length);
                byte respLrc = ComputeLrc(respRaw);
                string respFrame = ":" + BytesToHex(respRaw) + BytesToHex(new[] { respLrc }) + "\r\n";
                byte[] respBytes = Encoding.ASCII.GetBytes(respFrame);
                ns.Write(respBytes, 0, respBytes.Length);
            }
        done:;
        }
        catch { }
        finally { try { client.Close(); } catch { } }
    }

    private byte[]? ProcessPdu(byte[] pdu)
    {
        if (pdu.Length < 1) return null;
        byte fc = pdu[0];
        try
        {
            return fc switch
            {
                0x01 => ReadBits(pdu, _coils),
                0x02 => ReadBits(pdu, _discreteInputs),
                0x03 => ReadRegisters(pdu, _holdingRegisters),
                0x04 => ReadRegisters(pdu, _inputRegisters),
                0x05 => WriteSingleCoil(pdu),
                0x06 => WriteSingleRegister(pdu),
                0x0F => WriteMultipleCoils(pdu),
                0x10 => WriteMultipleRegisters(pdu),
                _ => BuildException(fc, 1)
            };
        }
        catch { return BuildException(fc, 4); }
    }

    private byte[] ReadBits(byte[] pdu, bool[] store)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        int byteCount = (count + 7) / 8;
        byte[] data = new byte[byteCount];
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                if (store[addr + i]) data[i / 8] |= (byte)(1 << (i % 8));
        }
        byte[] result = new byte[2 + byteCount];
        result[0] = pdu[0]; result[1] = (byte)byteCount;
        Buffer.BlockCopy(data, 0, result, 2, byteCount);
        return result;
    }

    private byte[] ReadRegisters(byte[] pdu, ushort[] store)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        int byteCount = count * 2;
        byte[] result = new byte[2 + byteCount];
        result[0] = pdu[0]; result[1] = (byte)byteCount;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                ushort val = store[addr + i];
                result[2 + i * 2] = (byte)(val >> 8);
                result[3 + i * 2] = (byte)val;
            }
        }
        return result;
    }

    private byte[] WriteSingleCoil(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        bool value = pdu[3] == 0xFF;
        lock (_lock) _coils[addr] = value;
        return pdu;
    }

    private byte[] WriteSingleRegister(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock) _holdingRegisters[addr] = value;
        return pdu;
    }

    private byte[] WriteMultipleCoils(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _coils[addr + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
        }
        return new byte[] { 0x0F, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private byte[] WriteMultipleRegisters(byte[] pdu)
    {
        ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
        ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
                _holdingRegisters[addr + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
        }
        return new byte[] { 0x10, pdu[1], pdu[2], pdu[3], pdu[4] };
    }

    private static byte[] BuildException(byte fc, byte code) => new byte[] { (byte)(fc | 0x80), code };

    // ── ASCII 帧编解码辅助 ──

    private static readonly char[] HexChars = "0123456789ABCDEF".ToCharArray();

    private static string BytesToHex(byte[] data)
    {
        char[] chars = new char[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
        {
            chars[i * 2] = HexChars[(data[i] >> 4) & 0x0F];
            chars[i * 2 + 1] = HexChars[data[i] & 0x0F];
        }
        return new string(chars);
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = (byte)((HexVal(hex[i * 2]) << 4) | HexVal(hex[i * 2 + 1]));
        return result;
    }

    private static int HexVal(char c) => c >= '0' && c <= '9' ? c - '0' :
        c >= 'A' && c <= 'F' ? c - 'A' + 10 :
        c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

    private static byte ComputeLrc(byte[] data, int offset = 0, int length = -1)
    {
        if (length < 0) length = data.Length - offset;
        byte lrc = 0;
        for (int i = offset; i < offset + length; i++)
            lrc += data[i];
        return (byte)(-lrc);
    }

    public void Stop() { _running = false; }
    public void Dispose() { _running = false; try { _listener.Stop(); } catch { } }
}

// ═══════════════════════════════════════════════════
//  Modbus ASCII 客户端测试
// ═══════════════════════════════════════════════════

public class ModbusAsciiTests
{
    private const int PortBase = 18200;

    private static (ModbusAsciiTestServer server, ModbusAsciiClient client) CreatePair(int port)
    {
        var server = new ModbusAsciiTestServer(port);
        server.Start();

        var tcp = new TcpClient("127.0.0.1", server.Port);
        tcp.NoDelay = true;
        var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);
        client.Connect();
        return (server, client);
    }

    [Fact]
    public void Ascii_ReadInt16_PreSet()
    {
        int port = PortBase + 1;
        var server = new ModbusAsciiTestServer(port);
        server.SetHoldingRegister(100, 0x1234);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            var r = client.ReadInt16("100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x1234, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_Int16()
    {
        int port = PortBase + 2;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            var w = client.Write("200", (short)-12345);
            Assert.True(w.IsSuccess, w.Message);

            var r = client.ReadInt16("200");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)-12345, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_UInt16()
    {
        int port = PortBase + 3;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            client.Write("300", (ushort)60000);
            var r = client.ReadUInt16("300");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)60000, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_Int32()
    {
        int port = PortBase + 4;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            client.Write("400", unchecked((int)0xDEADBEEF));
            var r = client.ReadInt32("400");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((int)0xDEADBEEF), r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_Float()
    {
        int port = PortBase + 5;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            client.Write("500", 3.14f);
            var r = client.ReadFloat("500");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(Math.Abs(r.Content - 3.14f) < 0.01f, $"Got {r.Content}");

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_Bool()
    {
        int port = PortBase + 6;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            client.Write("00100", true);
            Assert.True(client.ReadBool("00100").Content);

            client.Write("00100", false);
            Assert.False(client.ReadBool("00100").Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_String()
    {
        int port = PortBase + 7;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            client.Write("600", "HELLO");
            var r = client.ReadString("600", 5);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("HELLO", r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_WriteRead_Bytes()
    {
        int port = PortBase + 8;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            byte[] data = { 0xAA, 0xBB, 0xCC, 0xDD };
            client.Write("700", data);
            var r = client.ReadBytes("700", 4);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(data, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_ReadInputRegister()
    {
        int port = PortBase + 9;
        var server = new ModbusAsciiTestServer(port);
        server.SetInputRegister(50, 9999);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            var r = client.ReadUInt16("30050");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)9999, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_ReadDiscreteInput()
    {
        int port = PortBase + 10;
        var server = new ModbusAsciiTestServer(port);
        server.SetDiscreteInput(10, true);
        server.SetDiscreteInput(11, false);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            Assert.True(client.ReadBool("10010").Content);
            Assert.False(client.ReadBool("10011").Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_MultipleSequentialOperations()
    {
        int port = PortBase + 11;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            for (int i = 0; i < 5; i++)
            {
                client.Write("800", (short)i);
                var r = client.ReadInt16("800");
                Assert.True(r.IsSuccess);
                Assert.Equal((short)i, r.Content);
            }

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Ascii_LrcChecksum_IsCorrect()
    {
        // 验证 LRC 计算与标准一致
        byte[] data = { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        byte lrc = CrcCalculator.ComputeLrc(data);
        // 标准值: sum = 0x01+0x03+0x00+0x00+0x00+0x0A = 0x0E, LRC = -0x0E = 0xF2
        Assert.Equal(0xF2, lrc);
    }

    [Fact]
    public void Ascii_WriteMultipleCoils()
    {
        int port = PortBase + 12;
        var server = new ModbusAsciiTestServer(port);
        server.Start();
        try
        {
            var tcp = new TcpClient("127.0.0.1", server.Port);
            var client = new ModbusAsciiClient(new StreamSerialPortAdapter(tcp.GetStream()), station: 1, timeout: 5000);

            var coils = new bool[] { true, false, true, true, false, false, true, false };
            var w = client.WriteMultipleCoils(0, coils);
            Assert.True(w.IsSuccess, w.Message);

            // 验证各个线圈
            Assert.True(client.ReadBool("00000").Content);
            Assert.False(client.ReadBool("00001").Content);
            Assert.True(client.ReadBool("00002").Content);
            Assert.True(client.ReadBool("00003").Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }
}
