using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests;

internal sealed class ModbusAsciiOverTcpTestServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ushort[] _holdingRegisters = new ushort[65536];
    private Thread? _thread;
    private volatile bool _running;

    public int Port { get; }
    public string LastRequestText { get; private set; } = string.Empty;

    public ModbusAsciiOverTcpTestServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public void SetHoldingRegister(ushort address, ushort value) => _holdingRegisters[address] = value;
    public ushort GetHoldingRegister(ushort address) => _holdingRegisters[address];

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
                var client = _listener.AcceptTcpClient();
                var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                thread.Start();
            }
            catch
            {
                if (!_running) break;
            }
        }
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                while (_running && client.Connected)
                {
                    string? request = ReadAsciiFrame(stream);
                    if (request == null) break;
                    LastRequestText = request;

                    byte[] raw = DecodeAsciiFrame(request);
                    byte station = raw[0];
                    byte[] pdu = new byte[raw.Length - 2];
                    Buffer.BlockCopy(raw, 1, pdu, 0, pdu.Length);

                    byte[] response = BuildAsciiFrame(station, ProcessPdu(pdu));
                    stream.Write(response, 0, response.Length);
                }
            }
        }
        catch { }
    }

    private byte[] ProcessPdu(byte[] pdu)
    {
        byte fc = pdu[0];
        switch (fc)
        {
            case 0x03:
                return ReadHoldingRegisters(pdu);
            case 0x16:
                return MaskWriteRegister(pdu);
            default:
                return new byte[] { (byte)(fc | 0x80), 0x01 };
        }
    }

    private byte[] ReadHoldingRegisters(byte[] pdu)
    {
        ushort address = ReadUInt16(pdu, 1);
        ushort count = ReadUInt16(pdu, 3);
        byte[] response = new byte[2 + count * 2];
        response[0] = 0x03;
        response[1] = (byte)(count * 2);
        for (int i = 0; i < count; i++)
        {
            ushort value = _holdingRegisters[address + i];
            response[2 + i * 2] = (byte)(value >> 8);
            response[3 + i * 2] = (byte)value;
        }
        return response;
    }

    private byte[] MaskWriteRegister(byte[] pdu)
    {
        ushort address = ReadUInt16(pdu, 1);
        ushort andMask = ReadUInt16(pdu, 3);
        ushort orMask = ReadUInt16(pdu, 5);
        ushort current = _holdingRegisters[address];
        _holdingRegisters[address] = (ushort)((current & andMask) | (orMask & ~andMask));
        return new byte[] { 0x16, pdu[1], pdu[2], pdu[3], pdu[4], pdu[5], pdu[6] };
    }

    private static ushort ReadUInt16(byte[] buffer, int offset)
    {
        return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static string? ReadAsciiFrame(NetworkStream stream)
    {
        int b;
        do
        {
            b = stream.ReadByte();
            if (b < 0) return null;
        }
        while (b != ':');

        using (var ms = new MemoryStream())
        {
            ms.WriteByte((byte)':');
            bool sawCr = false;
            while (true)
            {
                b = stream.ReadByte();
                if (b < 0) return null;
                ms.WriteByte((byte)b);
                if (b == '\r') sawCr = true;
                else if (sawCr && b == '\n') return Encoding.ASCII.GetString(ms.ToArray());
                else sawCr = false;
            }
        }
    }

    private static byte[] DecodeAsciiFrame(string frame)
    {
        string hex = frame.TrimStart(':').TrimEnd('\r', '\n');
        byte[] raw = HexToBytes(hex);
        byte expected = CrcCalculator.ComputeLrc(raw, 0, raw.Length - 1);
        if (raw[raw.Length - 1] != expected)
            throw new InvalidOperationException("Invalid LRC.");
        return raw;
    }

    private static byte[] BuildAsciiFrame(byte station, byte[] pdu)
    {
        byte[] raw = new byte[1 + pdu.Length];
        raw[0] = station;
        Buffer.BlockCopy(pdu, 0, raw, 1, pdu.Length);
        byte lrc = CrcCalculator.ComputeLrc(raw);

        byte[] withLrc = new byte[raw.Length + 1];
        Buffer.BlockCopy(raw, 0, withLrc, 0, raw.Length);
        withLrc[raw.Length] = lrc;

        return Encoding.ASCII.GetBytes(":" + BytesToHex(withLrc) + "\r\n");
    }

    private static byte[] HexToBytes(string hex)
    {
        byte[] result = new byte[hex.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return result;
    }

    private static string BytesToHex(byte[] data)
    {
        StringBuilder builder = new StringBuilder(data.Length * 2);
        for (int i = 0; i < data.Length; i++)
            builder.Append(data[i].ToString("X2"));
        return builder.ToString();
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }
}

public class ModbusAsciiOverTcpTests
{
    [Fact]
    public void ReadUInt16_SendsAsciiFrameOverTcp()
    {
        using var server = new ModbusAsciiOverTcpTestServer();
        server.SetHoldingRegister(0, 0x1234);
        server.Start();

        using var client = new ModbusAsciiOverTcpClient("127.0.0.1", server.Port, station: 1, timeout: 2000);
        client.Connect();

        var result = client.ReadUInt16("0");

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal((ushort)0x1234, result.Content);
        Assert.StartsWith(":", server.LastRequestText);
        Assert.EndsWith("\r\n", server.LastRequestText);
        Assert.Contains("010300000001", server.LastRequestText);
    }

    [Fact]
    public void MaskWriteRegister_UpdatesRegisterOverAsciiTcp()
    {
        using var server = new ModbusAsciiOverTcpTestServer();
        server.SetHoldingRegister(16, 0x1234);
        server.Start();

        using var client = new ModbusAsciiOverTcpClient("127.0.0.1", server.Port, station: 1, timeout: 2000);
        client.Connect();

        var write = client.MaskWriteRegister("16", 0xFF00, 0x00F0);
        var read = client.ReadUInt16("16");

        Assert.True(write.IsSuccess, write.Message);
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((ushort)0x12F0, read.Content);
        Assert.Equal((ushort)0x12F0, server.GetHoldingRegister(16));
    }
}
