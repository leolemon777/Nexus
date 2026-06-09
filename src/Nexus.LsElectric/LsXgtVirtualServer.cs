using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS 产电 XGT 虚拟 PLC 服务器。
    /// <para>模拟 XGT 二进制协议帧格式，支持读/写/请求/控制命令。</para>
    /// </summary>
    public class LsXgtVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();
        private readonly ushort[] _registers = new ushort[65536];
        private readonly bool[] _bits = new bool[65536];
        private byte _plcStatus = 0x01; // Run

        public int Port { get; }
        public bool IsRunning => _running;

        public LsXgtVirtualServer(int port = 20040)
        {
            Port = port;
        }

        public void SetRegister(ushort address, ushort value) { lock (_memLock) _registers[address] = value; }
        public ushort GetRegister(ushort address) { lock (_memLock) return _registers[address]; }
        public void SetBit(ushort address, bool value) { lock (_memLock) _bits[address] = value; }
        public bool GetBit(ushort address) { lock (_memLock) return _bits[address]; }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    new Thread(() => HandleClient(client)) { IsBackground = true }.Start();
                }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                while (_running && client.Connected)
                {
                    try
                    {
                        // XGT Frame: ENQ(1) + Company(10) + CPUInfo(10) + PLCInfo(6) + Cmd(1) + DataType(1) + Reserve(2) + BlockInfo(2) + Data + EOT(1)
                        // Read header (33 bytes fixed)
                        var header = new byte[LsXgtConstants.FrameHeaderLength];
                        if (!ReadExact(stream, header, 0, header.Length)) break;
                        if (header[0] != LsXgtConstants.ENQ) break;

                        byte cmd = header[28];
                        byte dataType = header[29];
                        ushort dataLen = (ushort)((header[32] << 8) | header[31]); // Little-endian in XGT

                        var data = new byte[dataLen];
                        if (dataLen > 0 && !ReadExact(stream, data, 0, dataLen)) break;

                        // Read EOT
                        var eot = new byte[1];
                        if (!ReadExact(stream, eot, 0, 1) || eot[0] != LsXgtConstants.EOT) break;

                        // Process
                        byte[] respData = ProcessCommand(cmd, dataType, data);

                        // Build response frame
                        int respFrameLen = LsXgtConstants.FrameHeaderLength + respData.Length + 1;
                        var resp = new byte[respFrameLen];
                        int i = 0;
                        resp[i++] = LsXgtConstants.ACK; // ACK
                        byte[] company = Encoding.ASCII.GetBytes("LSIS-XGT\0\0");
                        Buffer.BlockCopy(company, 0, resp, i, 10); i += 10;
                        i += 10; // CPU info (zeros)
                        resp[i++] = 0xA0; // CPU type
                        i += 5; // PLC info padding
                        resp[i++] = cmd;
                        resp[i++] = dataType;
                        resp[i++] = 0; resp[i++] = 0; // Reserve
                        resp[i++] = (byte)(respData.Length & 0xFF);
                        resp[i++] = (byte)((respData.Length >> 8) & 0xFF);
                        if (respData.Length > 0) Buffer.BlockCopy(respData, 0, resp, i, respData.Length);
                        i += respData.Length;
                        resp[i] = LsXgtConstants.EOT;

                        stream.Write(resp, 0, resp.Length);
                    }
                    catch { break; }
                }
            }
        }

        private byte[] ProcessCommand(byte cmd, byte dataType, byte[] data)
        {
            switch (cmd)
            {
                case LsXgtConstants.CmdRead:
                    return ProcessRead(dataType, data);
                case LsXgtConstants.CmdWrite:
                    return ProcessWrite(dataType, data);
                case LsXgtConstants.CmdRequest:
                    return ProcessRequest(data);
                case LsXgtConstants.CmdControl:
                    return ProcessControl(data);
                default:
                    return new byte[] { 0x01 }; // Error: unknown command
            }
        }

        private byte[] ProcessRead(byte dataType, byte[] data)
        {
            if (data.Length < 8) return new byte[] { 0x01 };
            ushort varCount = (ushort)(data[0] | (data[1] << 8));
            byte varType = data[2];
            byte area = data[3];
            int offset = data[4] | (data[5] << 8);
            ushort count = (ushort)(data[6] | (data[7] << 8));

            if (dataType == LsXgtConstants.TypeBit || varType == LsXgtConstants.TypeBit)
            {
                var result = new byte[4 + count];
                result[0] = 0x00; // No error
                result[1] = (byte)(count & 0xFF); result[2] = (byte)((count >> 8) & 0xFF);
                lock (_memLock) { for (int i = 0; i < count; i++) result[3 + i] = (byte)(_bits[offset + i] ? 1 : 0); }
                return result;
            }
            else
            {
                int byteCount = count * 2;
                var result = new byte[3 + byteCount];
                result[0] = 0x00; // No error
                result[1] = (byte)(byteCount & 0xFF); result[2] = (byte)((byteCount >> 8) & 0xFF);
                lock (_memLock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ushort v = _registers[offset + i];
                        result[3 + i * 2] = (byte)(v & 0xFF);
                        result[4 + i * 2] = (byte)((v >> 8) & 0xFF);
                    }
                }
                return result;
            }
        }

        private byte[] ProcessWrite(byte dataType, byte[] data)
        {
            if (data.Length < 8) return new byte[] { 0x01 };
            byte varType = data[2];
            byte area = data[3];
            int offset = data[4] | (data[5] << 8);
            ushort count = (ushort)(data[6] | (data[7] << 8));

            if (dataType == LsXgtConstants.TypeBit || varType == LsXgtConstants.TypeBit)
            {
                if (data.Length < 9) return new byte[] { 0x01 };
                lock (_memLock) _bits[offset] = data[8] != 0;
            }
            else
            {
                lock (_memLock)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int srcOff = 8 + i * 2;
                        if (srcOff + 1 < data.Length)
                            _registers[offset + i] = (ushort)(data[srcOff] | (data[srcOff + 1] << 8));
                    }
                }
            }
            return new byte[] { 0x00 }; // Success
        }

        private byte[] ProcessRequest(byte[] data)
        {
            return new byte[] { 0x00, _plcStatus };
        }

        private byte[] ProcessControl(byte[] data)
        {
            if (data.Length < 1) return new byte[] { 0x01 };
            _plcStatus = data[0];
            return new byte[] { 0x00 };
        }

        private static bool ReadExact(NetworkStream s, byte[] b, int o, int c) { int r = 0; while (r < c) { int n = s.Read(b, o + r, c - r); if (n <= 0) return false; r += n; } return true; }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
