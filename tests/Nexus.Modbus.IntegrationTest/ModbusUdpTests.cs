using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;
using Nexus.Modbus;

namespace Nexus.Modbus.IntegrationTest;

/// <summary>
/// 简易 Modbus UDP 虚拟服务器 — 接收 MBAP 帧，处理 PDU，返回 MBAP 响应。
/// 内存模型与 ModbusTcpServer 相同（四区）。
/// </summary>
public sealed class ModbusUdpTestServer : IDisposable
{
    private readonly UdpClient _udp;
    private Thread? _thread;
    private volatile bool _running;
    private readonly object _lock = new object();

    private readonly bool[] _coils = new bool[65536];
    private readonly bool[] _discreteInputs = new bool[65536];
    private readonly ushort[] _holdingRegisters = new ushort[65536];
    private readonly ushort[] _inputRegisters = new ushort[65536];

    public int Port { get; }

    public ModbusUdpTestServer(int port = 0)
    {
        _udp = new UdpClient(port);
        _udp.Client.ReceiveTimeout = 5000;
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

    public void SetHoldingRegister(ushort address, ushort value) { lock (_lock) _holdingRegisters[address] = value; }
    public void SetInputRegister(ushort address, ushort value) { lock (_lock) _inputRegisters[address] = value; }
    public void SetCoil(ushort address, bool value) { lock (_lock) _coils[address] = value; }
    public void SetDiscreteInput(ushort address, bool value) { lock (_lock) _discreteInputs[address] = value; }

    public void Start()
    {
        _running = true;
        _thread = new Thread(ReceiveLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop() { _running = false; }

    private void ReceiveLoop()
    {
        while (_running)
        {
            try
            {
                IPEndPoint? remote = null;
                byte[] data = _udp.Receive(ref remote);
                if (data.Length < 8) continue;

                // 解析 MBAP
                // byte[] tid = data[0..2];
                // byte[] proto = data[2..4];
                int length = (data[4] << 8) | data[5];
                byte unitId = data[6];
                byte[] pdu = new byte[data.Length - 7];
                Buffer.BlockCopy(data, 7, pdu, 0, pdu.Length);

                byte[]? respPdu = ProcessPdu(pdu);
                if (respPdu == null) continue;

                byte[] response = new byte[7 + respPdu.Length];
                Buffer.BlockCopy(data, 0, response, 0, 6); // echo TID + proto
                response[6] = unitId;
                int respLen = respPdu.Length + 1;
                response[4] = (byte)(respLen >> 8);
                response[5] = (byte)respLen;
                Buffer.BlockCopy(respPdu, 0, response, 7, respPdu.Length);

                _udp.Send(response, response.Length, remote!);
            }
            catch { if (!_running) break; }
        }
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

    public void Dispose() { _running = false; try { _udp.Close(); } catch { } }
}

// ═══════════════════════════════════════════════════
//  Modbus UDP 客户端测试
// ═══════════════════════════════════════════════════

public class ModbusUdpTests
{
    private const int PortBase = 17200;

    [Fact]
    public void Udp_ConnectDisconnect_Works()
    {
        var client = new ModbusUdpClient("127.0.0.1", 19999, station: 1);
        var conn = client.Connect();
        Assert.True(conn.IsSuccess, conn.Message);
        Assert.True(client.IsConnected);

        client.Disconnect();
        Assert.False(client.IsConnected);
        client.Dispose();
    }

    [Fact]
    public void Udp_ReadInt16_PreSet()
    {
        int port = PortBase + 1;
        var server = new ModbusUdpTestServer(port);
        server.SetHoldingRegister(100, 0x1234);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            var r = client.ReadInt16("100");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((short)0x1234, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_Int16()
    {
        int port = PortBase + 2;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

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
    public void Udp_WriteRead_UInt16()
    {
        int port = PortBase + 3;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            client.Write("300", (ushort)60000);
            var r = client.ReadUInt16("300");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)60000, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_Int32()
    {
        int port = PortBase + 4;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            client.Write("400", unchecked((int)0xDEADBEEF));
            var r = client.ReadInt32("400");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal(unchecked((int)0xDEADBEEF), r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_Float()
    {
        int port = PortBase + 5;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            client.Write("500", 3.14f);
            var r = client.ReadFloat("500");
            Assert.True(r.IsSuccess, r.Message);
            Assert.True(Math.Abs(r.Content - 3.14f) < 0.01f, $"Got {r.Content}");

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_Bool()
    {
        int port = PortBase + 6;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            client.Write("00100", true);
            Assert.True(client.ReadBool("00100").Content);

            client.Write("00100", false);
            Assert.False(client.ReadBool("00100").Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_String()
    {
        int port = PortBase + 7;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            client.Write("600", "HELLO");
            var r = client.ReadString("600", 5);
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal("HELLO", r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_WriteRead_Bytes()
    {
        int port = PortBase + 8;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

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
    public void Udp_ReadInputRegister()
    {
        int port = PortBase + 9;
        var server = new ModbusUdpTestServer(port);
        server.SetInputRegister(50, 9999);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            var r = client.ReadUInt16("30050");
            Assert.True(r.IsSuccess, r.Message);
            Assert.Equal((ushort)9999, r.Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_ReadDiscreteInput()
    {
        int port = PortBase + 10;
        var server = new ModbusUdpTestServer(port);
        server.SetDiscreteInput(10, true);
        server.SetDiscreteInput(11, false);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

            Assert.True(client.ReadBool("10010").Content);
            Assert.False(client.ReadBool("10011").Content);

            client.Dispose();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public void Udp_MultipleSequentialOperations()
    {
        int port = PortBase + 11;
        var server = new ModbusUdpTestServer(port);
        server.Start();
        try
        {
            var client = new ModbusUdpClient("127.0.0.1", port, station: 1);
            client.Connect();

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
}
