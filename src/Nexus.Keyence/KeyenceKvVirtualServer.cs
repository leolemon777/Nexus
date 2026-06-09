using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Keyence
{
    /// <summary>
    /// 基恩士 KV 虚拟 PLC 服务器 — 模拟 KV 上位链接二进制协议 over TCP。
    /// </summary>
    public class KeyenceKvVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();
        private readonly ushort[] _registers = new ushort[65536];
        private readonly bool[] _coils = new bool[65536];

        public int Port { get; }
        public bool IsRunning => _running;

        public KeyenceKvVirtualServer(int port = 5022) { Port = port; }

        public void SetRegister(ushort addr, ushort val) { lock (_memLock) _registers[addr] = val; }
        public ushort GetRegister(ushort addr) { lock (_memLock) return _registers[addr]; }
        public void SetCoil(ushort addr, bool val) { lock (_memLock) _coils[addr] = val; }
        public bool GetCoil(ushort addr) { lock (_memLock) return _coils[addr]; }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop() { _running = false; try { _listener?.Stop(); } catch { } _listener = null; }

        private void AcceptLoop()
        {
            while (_running)
            {
                try { var c = _listener!.AcceptTcpClient(); new Thread(() => HandleClient(c)) { IsBackground = true }.Start(); }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buf = new byte[1024];
                while (_running && client.Connected)
                {
                    try
                    {
                        if (!ReadExact(stream, buf, 0, 7)) break;
                        int len = (buf[4] << 8) | buf[5];
                        int pduLen = len - 1;
                        if (pduLen <= 0 || pduLen > 260) break;
                        if (!ReadExact(stream, buf, 7, pduLen)) break;

                        byte unitId = buf[6]; byte fc = buf[7];
                        byte[]? resp = ProcessModbus(fc, buf, 8, pduLen - 1);
                        if (resp == null) break;

                        int rLen = 1 + resp.Length;
                        var r = new byte[7 + resp.Length];
                        r[0] = buf[0]; r[1] = buf[1]; r[2] = 0; r[3] = 0;
                        r[4] = (byte)(rLen >> 8); r[5] = (byte)(rLen & 0xFF); r[6] = unitId;
                        Buffer.BlockCopy(resp, 0, r, 7, resp.Length);
                        stream.Write(r, 0, r.Length);
                    }
                    catch { break; }
                }
            }
        }

        private byte[]? ProcessModbus(byte fc, byte[] buf, int off, int len)
        {
            switch (fc)
            {
                case 0x01: case 0x02:
                    { ushort a = (ushort)((buf[off] << 8) | buf[off + 1]); ushort c = (ushort)((buf[off + 2] << 8) | buf[off + 3]); int bc = (c + 7) / 8; var d = new byte[bc]; lock (_memLock) { for (int i = 0; i < c; i++) if (_coils[a + i]) d[i / 8] |= (byte)(1 << (i % 8)); } var r = new byte[2 + bc]; r[0] = fc; r[1] = (byte)bc; Buffer.BlockCopy(d, 0, r, 2, bc); return r; }
                case 0x03: case 0x04:
                    { ushort a = (ushort)((buf[off] << 8) | buf[off + 1]); ushort c = (ushort)((buf[off + 2] << 8) | buf[off + 3]); var r = new byte[2 + c * 2]; r[0] = fc; r[1] = (byte)(c * 2); lock (_memLock) { for (int i = 0; i < c; i++) { ushort v = _registers[a + i]; r[2 + i * 2] = (byte)(v >> 8); r[3 + i * 2] = (byte)(v & 0xFF); } } return r; }
                case 0x05:
                    { ushort a = (ushort)((buf[off] << 8) | buf[off + 1]); bool val = buf[off + 2] == 0xFF; lock (_memLock) _coils[a] = val; return new byte[] { 0x05, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] }; }
                case 0x06:
                    { ushort a = (ushort)((buf[off] << 8) | buf[off + 1]); ushort v = (ushort)((buf[off + 2] << 8) | buf[off + 3]); lock (_memLock) _registers[a] = v; return new byte[] { 0x06, buf[off], buf[off + 1], buf[off + 2], buf[off + 3] }; }
                default: return new byte[] { (byte)(fc | 0x80), 0x01 };
            }
        }

        private static bool ReadExact(NetworkStream s, byte[] b, int o, int c) { int r = 0; while (r < c) { int n = s.Read(b, o + r, c - r); if (n <= 0) return false; r += n; } return true; }
        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
