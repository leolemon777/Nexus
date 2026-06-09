using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Xinje
{
    /// <summary>
    /// 信捷虚拟 PLC 服务器 — 模拟 Modbus TCP 兼容协议。
    /// <para>用于集成测试，无需真实信捷 PLC。</para>
    /// <para>支持 FC01/02/03/04/05/06/0F/10 功能码。</para>
    /// </summary>
    public class XinjeVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();
        private readonly bool[] _coils = new bool[65536];
        private readonly bool[] _discreteInputs = new bool[65536];
        private readonly ushort[] _inputRegisters = new ushort[32768];
        private readonly ushort[] _holdingRegisters = new ushort[65536];

        public int Port { get; }
        public bool IsRunning => _running;

        public XinjeVirtualServer(int port = 5021)
        {
            Port = port;
        }

        public void SetHoldingRegister(ushort address, ushort value) { lock (_memLock) _holdingRegisters[address] = value; }
        public void SetCoil(ushort address, bool value) { lock (_memLock) _coils[address] = value; }
        public void SetDiscreteInput(ushort address, bool value) { lock (_memLock) _discreteInputs[address] = value; }
        public ushort GetHoldingRegister(ushort address) { lock (_memLock) return _holdingRegisters[address]; }
        public bool GetCoil(ushort address) { lock (_memLock) return _coils[address]; }

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
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[1024];

                while (_running && client.Connected)
                {
                    try
                    {
                        if (!ReadExact(stream, buffer, 0, 7)) break;
                        int length = (buffer[4] << 8) | buffer[5];
                        int pduLen = length - 1;
                        if (pduLen <= 0 || pduLen > 260) break;
                        if (!ReadExact(stream, buffer, 7, pduLen)) break;

                        byte unitId = buffer[6];
                        byte fc = buffer[7];
                        byte[]? response = ProcessRequest(fc, buffer, 8, pduLen - 1);
                        if (response == null) break;

                        int respLen = 1 + response.Length;
                        var resp = new byte[7 + response.Length];
                        resp[0] = buffer[0]; resp[1] = buffer[1];
                        resp[2] = 0; resp[3] = 0;
                        resp[4] = (byte)(respLen >> 8); resp[5] = (byte)(respLen & 0xFF);
                        resp[6] = unitId;
                        Buffer.BlockCopy(response, 0, resp, 7, response.Length);
                        stream.Write(resp, 0, resp.Length);
                    }
                    catch { break; }
                }
            }
        }

        private byte[]? ProcessRequest(byte fc, byte[] buf, int off, int len)
        {
            switch (fc)
            {
                case 0x01: return ReadBits(0x01, _coils, buf, off);
                case 0x02: return ReadBits(0x02, _discreteInputs, buf, off);
                case 0x03: return ReadRegisters(0x03, _holdingRegisters, buf, off);
                case 0x04: return ReadRegisters(0x04, _inputRegisters, buf, off);
                case 0x05: return WriteSingleCoil(buf, off);
                case 0x06: return WriteSingleRegister(buf, off);
                case 0x0F: return WriteMultipleCoils(buf, off, len);
                case 0x10: return WriteMultipleRegisters(buf, off, len);
                default: return new byte[] { (byte)(fc | 0x80), 0x01 };
            }
        }

        private byte[] ReadBits(byte fc, bool[] mem, byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            int bc = (count + 7) / 8;
            var data = new byte[bc];
            lock (_memLock) { for (int i = 0; i < count; i++) if (mem[addr + i]) data[i / 8] |= (byte)(1 << (i % 8)); }
            var r = new byte[2 + bc]; r[0] = fc; r[1] = (byte)bc;
            Buffer.BlockCopy(data, 0, r, 2, bc); return r;
        }

        private byte[] ReadRegisters(byte fc, ushort[] mem, byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            var r = new byte[2 + count * 2]; r[0] = fc; r[1] = (byte)(count * 2);
            lock (_memLock) { for (int i = 0; i < count; i++) { ushort v = mem[addr + i]; r[2 + i * 2] = (byte)(v >> 8); r[3 + i * 2] = (byte)(v & 0xFF); } }
            return r;
        }

        private byte[] WriteSingleCoil(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]); bool val = buf[off + 2] == 0xFF;
            lock (_memLock) _coils[addr] = val;
            return new byte[] { 0x05, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] };
        }

        private byte[] WriteSingleRegister(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]); ushort val = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            lock (_memLock) _holdingRegisters[addr] = val;
            return new byte[] { 0x06, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] };
        }

        private byte[] WriteMultipleCoils(byte[] buf, int off, int len)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]); ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            lock (_memLock) { for (int i = 0; i < count; i++) _coils[addr + i] = (buf[off + 5 + i / 8] & (1 << (i % 8))) != 0; }
            return new byte[] { 0x0F, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] };
        }

        private byte[] WriteMultipleRegisters(byte[] buf, int off, int len)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]); ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            lock (_memLock) { for (int i = 0; i < count; i++) _holdingRegisters[addr + i] = (ushort)((buf[off + 5 + i * 2] << 8) | buf[off + 6 + i * 2]); }
            return new byte[] { 0x10, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] };
        }

        private static bool ReadExact(NetworkStream s, byte[] b, int o, int c) { int r = 0; while (r < c) { int n = s.Read(b, o + r, c - r); if (n <= 0) return false; r += n; } return true; }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
