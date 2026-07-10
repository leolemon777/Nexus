using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.BrPowerlink
{
    /// <summary>
    /// B&amp;R POWERLINK SDO 虚拟服务器 — 模拟 MN 对 CN 的 SDO 请求-应答。
    /// <para>用于集成测试，无需真实 B&amp;R POWERLINK 设备。</para>
    /// <para>支持 CmdReadOd (0x01) / CmdWriteOd (0x02)，基于对象字典 (nodeId, index, subIndex) 存储。</para>
    /// <para><b>诚实说明</b>：仅模拟 SDO 请求-应答，不模拟 Preq/Pres 实时周期调度。</para>
    /// </summary>
    public class BrPowerlinkVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _odLock = new object();

        // 对象字典：(nodeId, index, subIndex) → 字节数据
        private readonly Dictionary<(byte nodeId, ushort index, byte subIndex), byte[]> _od
            = new Dictionary<(byte, ushort, byte), byte[]>();

        public int Port { get; }
        public bool IsRunning => _running;

        public BrPowerlinkVirtualServer(int port = BrPowerlinkConstants.DefaultPort) { Port = port; }

        // ── 测试预置/校验 ──

        /// <summary>预置对象字典条目（测试用）。</summary>
        public void SetOdValue(byte nodeId, ushort index, byte subIndex, byte[] data)
        {
            lock (_odLock)
            {
                _od[(nodeId, index, subIndex)] = (byte[])data.Clone();
            }
        }

        /// <summary>读取对象字典条目（测试校验用）。未配置返回 null。</summary>
        public byte[]? GetOdValue(byte nodeId, ushort index, byte subIndex)
        {
            lock (_odLock)
            {
                if (_od.TryGetValue((nodeId, index, subIndex), out byte[] data))
                    return (byte[])data.Clone();
                return null;
            }
        }

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

                while (_running && client.Connected)
                {
                    try
                    {
                        // 读请求头：7 字节 [cmd][nodeId][index 2B][subIndex][size 2B]
                        byte[] header = new byte[BrPowerlinkConstants.RequestHeaderLength];
                        if (!ReadExact(stream, header, 0, header.Length)) break;

                        byte cmd = header[0];
                        byte nodeId = header[1];
                        ushort index = (ushort)((header[2] << 8) | header[3]);
                        byte subIndex = header[4];
                        ushort size = (ushort)((header[5] << 8) | header[6]);

                        if (cmd == BrPowerlinkConstants.CmdReadOd)
                        {
                            byte[] resp = ProcessReadOd(nodeId, index, subIndex, size);
                            stream.Write(resp, 0, resp.Length);
                        }
                        else if (cmd == BrPowerlinkConstants.CmdWriteOd)
                        {
                            // 读取写入数据
                            byte[] data = new byte[size];
                            if (size > 0 && !ReadExact(stream, data, 0, size)) break;
                            byte[] resp = ProcessWriteOd(nodeId, index, subIndex, data);
                            stream.Write(resp, 0, resp.Length);
                        }
                        else
                        {
                            // 未知命令
                            byte[] resp = BuildResponse(BrPowerlinkError.InternalError, Array.Empty<byte>());
                            stream.Write(resp, 0, resp.Length);
                        }
                    }
                    catch { break; }
                }
            }
        }

        private byte[] ProcessReadOd(byte nodeId, ushort index, byte subIndex, ushort size)
        {
            byte[]? data = GetOdValue(nodeId, index, subIndex);
            if (data == null)
                return BuildResponse(BrPowerlinkError.ObjectDoesNotExist, Array.Empty<byte>());

            int copyLen = Math.Min(size, data.Length);
            byte[] payload = new byte[copyLen];
            Buffer.BlockCopy(data, 0, payload, 0, copyLen);
            return BuildResponse(BrPowerlinkError.None, payload);
        }

        private byte[] ProcessWriteOd(byte nodeId, ushort index, byte subIndex, byte[] data)
        {
            SetOdValue(nodeId, index, subIndex, data);
            return BuildResponse(BrPowerlinkError.None, Array.Empty<byte>());
        }

        /// <summary>构建响应帧: [error 4B big-endian][payloadLen 2B big-endian][payload]。</summary>
        private static byte[] BuildResponse(uint error, byte[] payload)
        {
            byte[] resp = new byte[BrPowerlinkConstants.ResponseHeaderLength + payload.Length];
            resp[0] = (byte)((error >> 24) & 0xFF);
            resp[1] = (byte)((error >> 16) & 0xFF);
            resp[2] = (byte)((error >> 8) & 0xFF);
            resp[3] = (byte)(error & 0xFF);
            resp[4] = (byte)((payload.Length >> 8) & 0xFF);
            resp[5] = (byte)(payload.Length & 0xFF);
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, resp, BrPowerlinkConstants.ResponseHeaderLength, payload.Length);
            return resp;
        }

        private static bool ReadExact(NetworkStream s, byte[] b, int o, int c)
        {
            int r = 0;
            while (r < c)
            {
                int n = s.Read(b, o + r, c - r);
                if (n <= 0) return false;
                r += n;
            }
            return true;
        }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
