using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Fanuc
{
    /// <summary>
    /// FANUC FOCAS 虚拟 CNC 服务器 — 模拟 FOCAS2 二进制协议 over TCP。
    /// <para>用于集成测试，无需真实 FANUC CNC。</para>
    /// </summary>
    public class FanucFocasVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();
        private readonly ushort[] _registers = new ushort[65536];
        private readonly bool[] _coils = new bool[65536];

        /// <summary>CNC 状态模拟。</summary>
        public byte RunStatus { get; set; } = 4; // Running
        /// <summary>当前程序名。</summary>
        public string ProgramName { get; set; } = "O0001";
        /// <summary>主轴转速。</summary>
        public int SpindleSpeed { get; set; } = 1000;
        /// <summary>进给倍率。</summary>
        public int FeedOverride { get; set; } = 100;
        /// <summary>X 轴位置。</summary>
        public double AxisX { get; set; } = 10.5;
        /// <summary>Y 轴位置。</summary>
        public double AxisY { get; set; } = 20.3;
        /// <summary>Z 轴位置。</summary>
        public double AxisZ { get; set; } = 5.1;

        public int Port { get; }
        public bool IsRunning => _running;

        public FanucFocasVirtualServer(int port = 81930) { Port = port; }

        public void SetRegister(ushort addr, ushort val) { lock (_memLock) _registers[addr] = val; }
        public ushort GetRegister(ushort addr) { lock (_memLock) return _registers[addr]; }

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
                var buf = new byte[4096];
                while (_running && client.Connected)
                {
                    try
                    {
                        // 简化的 FOCAS 帧: Length(4) + FunctionCode(2) + Data
                        if (!ReadExact(stream, buf, 0, 6)) break;
                        int length = BitConverter.ToInt32(buf, 0);
                        ushort funcCode = BitConverter.ToUInt16(buf, 4);

                        int dataLen = length - 6;
                        if (dataLen > 0)
                        {
                            if (!ReadExact(stream, buf, 6, dataLen)) break;
                        }

                        byte[] resp = ProcessFunction(funcCode, buf, 6, dataLen);
                        stream.Write(resp, 0, resp.Length);
                    }
                    catch { break; }
                }
            }
        }

        private byte[] ProcessFunction(ushort funcCode, byte[] buf, int off, int len)
        {
            // 返回简化的成功响应
            switch (funcCode)
            {
                case 0x0101: // cnc_allclibhndl
                    return BuildResponse(0);

                case 0x0112: // cnc_statinfo
                    {
                        var data = new byte[10];
                        data[0] = RunStatus; // run status
                        data[1] = 0; // motion
                        data[2] = 0; // mstb
                        data[3] = 0; // emergency
                        return BuildResponse(0, data);
                    }

                case 0x0116: // cnc_absolute (axis position)
                    {
                        var data = new byte[24];
                        WriteDouble(data, 0, AxisX);
                        WriteDouble(data, 8, AxisY);
                        WriteDouble(data, 16, AxisZ);
                        return BuildResponse(0, data);
                    }

                case 0x0118: // cnc_rdprognum (program number)
                    {
                        var nameBytes = System.Text.Encoding.ASCII.GetBytes(ProgramName.PadRight(36, '\0'));
                        return BuildResponse(0, nameBytes);
                    }

                case 0x0120: // cnc_rdparam (read parameter)
                    {
                        if (len >= 4)
                        {
                            ushort paramNum = BitConverter.ToUInt16(buf, off);
                            ushort val = 0;
                            lock (_memLock) val = _registers[paramNum];
                            return BuildResponse(0, BitConverter.GetBytes(val));
                        }
                        return BuildResponse(0);
                    }

                default:
                    return BuildResponse(0);
            }
        }

        private static byte[] BuildResponse(int rc)
        {
            var r = new byte[6];
            r[0] = 0; r[1] = 0; r[2] = 0; r[3] = 6; // length
            r[4] = (byte)(rc & 0xFF); r[5] = (byte)((rc >> 8) & 0xFF);
            return r;
        }

        private static byte[] BuildResponse(int rc, byte[] data)
        {
            var r = new byte[6 + data.Length];
            int len = 6 + data.Length;
            r[0] = (byte)(len & 0xFF); r[1] = (byte)((len >> 8) & 0xFF); r[2] = (byte)((len >> 16) & 0xFF); r[3] = (byte)((len >> 24) & 0xFF);
            r[4] = (byte)(rc & 0xFF); r[5] = (byte)((rc >> 8) & 0xFF);
            Buffer.BlockCopy(data, 0, r, 6, data.Length);
            return r;
        }

        private static void WriteDouble(byte[] buf, int offset, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, buf, offset, 8);
        }

        private static bool ReadExact(NetworkStream s, byte[] b, int o, int c) { int r = 0; while (r < c) { int n = s.Read(b, o + r, c - r); if (n <= 0) return false; r += n; } return true; }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
