using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace Nexus.Mitsubishi.Tests;

public sealed class Mc3ETransportParityTests
{
    [Fact]
    public void Mc3EAsciiTcp_ReadWriteDRegister_Works()
    {
        using var server = new Mc3EAsciiTcpFakeServer();
        server.SetDRegister(10, 0x1234);
        server.Start();

        using var client = new Mc3EAsciiClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 1000);

        var read = client.ReadInt16("D10");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x1234, read.Content);

        var write = client.Write("D11", unchecked((short)0xABCD));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0xABCD, server.GetDRegister(11));
    }

    [Fact]
    public void Mc3EUdpBinary_ReadWriteDRegister_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: false);
        server.SetDRegister(20, 0x2468);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 1000);

        var read = client.ReadInt16("D20");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x2468, read.Content);

        var write = client.Write("D21", unchecked((short)0x1357));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x1357, server.GetDRegister(21));
    }

    [Fact]
    public void Mc3EUdpAscii_ReadWriteDRegister_Works()
    {
        using var server = new Mc3EUdpFakeServer(useAscii: true);
        server.SetDRegister(30, 0x2222);
        server.Start();

        using var client = new Mc3EUdpClient(MitsubishiModel.Qna_3E, "127.0.0.1", server.Port, 1000)
        {
            UseAscii = true
        };

        var read = client.ReadInt16("D30");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal(0x2222, read.Content);

        var write = client.Write("D31", unchecked((short)0x3333));
        Assert.True(write.IsSuccess, write.Message);
        Assert.Equal(0x3333, server.GetDRegister(31));
    }

    private sealed class Mc3EAsciiTcpFakeServer : IDisposable
    {
        private readonly ushort[] _dRegisters = new ushort[1024];
        private readonly TcpListener _listener;
        private Thread? _thread;
        private volatile bool _running;

        public int Port { get; }

        public Mc3EAsciiTcpFakeServer()
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
                        byte[]? headerAscii = ReadExact(stream, 24);
                        if (headerAscii == null) break;

                        byte[] header = FromAsciiHex(headerAscii);
                        ushort command = (ushort)((header[8] << 8) | header[9]);
                        int bodyLength = command == 0x1401 ? 8 : 6;
                        byte[]? bodyAscii = ReadExact(stream, bodyLength * 2);
                        if (bodyAscii == null) break;

                        byte[] request = new byte[12 + bodyLength];
                        Buffer.BlockCopy(header, 0, request, 0, header.Length);
                        Buffer.BlockCopy(FromAsciiHex(bodyAscii), 0, request, 12, bodyLength);

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
            => Mc3EFakeFrame.HandleDRegisterRequest(request, _dRegisters);

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch { }
        }
    }

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
                    byte[] response = Mc3EFakeFrame.HandleDRegisterRequest(request, _dRegisters);
                    byte[] responseFrame = _useAscii ? ToAsciiHex(response) : response;
                    _udp.Send(responseFrame, responseFrame.Length, remote);
                }
                catch
                {
                    if (!_running) return;
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _udp.Close(); } catch { }
            _udp.Dispose();
        }
    }

    private static class Mc3EFakeFrame
    {
        public static byte[] HandleDRegisterRequest(byte[] request, ushort[] dRegisters)
        {
            ushort command = (ushort)((request[8] << 8) | request[9]);
            byte subLabel = request[12];
            uint address = (uint)(request[13] | (request[14] << 8) | (request[15] << 16));
            ushort count = (ushort)(request[16] | (request[17] << 8));

            Assert.Equal(0xA8, subLabel);

            if (command == 0x0401)
            {
                byte[] data = new byte[count * 2];
                for (int i = 0; i < count; i++)
                {
                    ushort value = dRegisters[address + i];
                    data[i * 2] = (byte)(value >> 8);
                    data[i * 2 + 1] = (byte)(value & 0xFF);
                }
                return BuildSuccessResponse(request, data);
            }

            if (command == 0x1401)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = 18 + i * 2;
                    dRegisters[address + i] = (ushort)((request[offset] << 8) | request[offset + 1]);
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
