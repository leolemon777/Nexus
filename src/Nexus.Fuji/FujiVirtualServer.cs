using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Fuji
{
    /// <summary>
    /// 富士 SPH 虚拟 PLC 服务器 — 模拟 S-BUS ASCII 协议，用于无硬件测试。
    /// <para>帧格式: STX(0x02) + Station(2) + Command(2) + Data + ETX(0x03) + BCC(2)</para>
    /// <para>支持区域: D(数据寄存器), M(内部继电器), X(输入), Y(输出), T(定时器), C(计数器)</para>
    /// </summary>
    public class FujiVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // 字区域存储 (每个字 2 字节)
        private readonly byte[] _dArea = new byte[32768]; // D: 16384 字
        private readonly byte[] _mArea = new byte[4096];  // M: 2048 字
        private readonly byte[] _xArea = new byte[2048];  // X: 1024 字
        private readonly byte[] _yArea = new byte[2048];  // Y: 1024 字
        private readonly byte[] _tArea = new byte[2048];  // T: 1024 字
        private readonly byte[] _cArea = new byte[2048];  // C: 1024 字

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();

        public byte Station { get; set; } = 1;
        public int Port { get; }
        public bool IsRunning => _running;

        public FujiVirtualServer(int port = 9000)
        {
            Port = port;
        }

        // ── 数据设置 API ────────────────────────────

        public void SetDWord(int wordIndex, ushort value)
        {
            lock (_lock)
            {
                int off = wordIndex * 2;
                if (off >= 0 && off + 1 < _dArea.Length)
                {
                    _dArea[off] = (byte)(value >> 8);
                    _dArea[off + 1] = (byte)(value & 0xFF);
                }
            }
        }

        public void SetDBytes(int byteOffset, byte[] data)
        {
            lock (_lock)
            {
                int off = Math.Min(byteOffset, _dArea.Length - data.Length);
                Buffer.BlockCopy(data, 0, _dArea, off, data.Length);
            }
        }

        // ── 服务器控制 ────────────────────────────

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
            lock (_lock)
            {
                foreach (var c in _clients) { try { c.Close(); } catch { } }
                _clients.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    lock (_lock) _clients.Add(client);
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        // S-BUS 帧: STX(1) + Station(2) + Command(2) + Data + ETX(1) + BCC(2)
                        int b = stream.ReadByte();
                        if (b < 0 || b != 0x02) continue;

                        var buf = new List<byte> { 0x02 };
                        int deadline = Environment.TickCount + 5000;

                        while (Environment.TickCount <= deadline)
                        {
                            b = stream.ReadByte();
                            if (b < 0) break;
                            buf.Add((byte)b);
                            if (b == 0x03 && buf.Count > 5)
                            {
                                // 读 BCC (2字节)
                                for (int i = 0; i < 2; i++)
                                {
                                    b = stream.ReadByte();
                                    if (b < 0) break;
                                    buf.Add((byte)b);
                                }
                                break;
                            }
                        }

                        if (buf.Count < 7) continue;

                        byte[]? response = ProcessFrame(buf.ToArray());
                        if (response != null)
                            stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
            finally { lock (_lock) { _clients.Remove(client); } }
        }

        private byte[]? ProcessFrame(byte[] frame)
        {
            if (frame.Length < 7 || frame[0] != 0x02) return null;

            string station = Encoding.ASCII.GetString(frame, 1, 2);
            string command = Encoding.ASCII.GetString(frame, 3, 2);

            // 验证站号
            if (int.Parse(station) != Station) return null;

            // 提取数据（跳过 STX+Station+Command，在 ETX 前）
            int dataEnd = frame.Length - 3; // 去掉 ETX + BCC(2)
            if (dataEnd <= 5) return BuildErrorResponse(station, command);

            string data = Encoding.ASCII.GetString(frame, 5, dataEnd - 5);

            return command switch
            {
                "RR" => ProcessReadRegs(station, data),    // 读寄存器
                "WR" => ProcessWriteRegs(station, data),   // 写寄存器
                _ => BuildErrorResponse(station, command)
            };
        }

        private byte[] ProcessReadRegs(string station, string data)
        {
            // data = areaCode(2) + startAddr(4) + count(4)
            if (data.Length < 10) return BuildErrorResponse(station, "RR");

            string areaCode = data.Substring(0, 2);
            int startAddr = int.Parse(data.Substring(2, 4));
            int count = int.Parse(data.Substring(6, 4));

            var storage = GetStorage(areaCode);
            if (storage == null) return BuildErrorResponse(station, "RR");

            var hexData = new StringBuilder(count * 4);
            lock (_storage)
            {
                for (int i = 0; i < count; i++)
                {
                    int off = (startAddr + i) * 2;
                    if (off + 1 < storage.Length)
                    {
                        hexData.Append(storage[off].ToString("X2"));
                        hexData.Append(storage[off + 1].ToString("X2"));
                    }
                    else
                    {
                        hexData.Append("0000");
                    }
                }
            }

            // Response: STX + Station(2) + Command(2) + Data + ETX + BCC(2)
            string body = station + "RR" + hexData.ToString();
            return BuildFrame(body);
        }

        private byte[] ProcessWriteRegs(string station, string data)
        {
            if (data.Length < 10) return BuildErrorResponse(station, "WR");

            string areaCode = data.Substring(0, 2);
            int startAddr = int.Parse(data.Substring(2, 4));
            string hexData = data.Substring(6);

            var storage = GetStorage(areaCode);
            if (storage == null) return BuildErrorResponse(station, "WR");

            lock (_storage)
            {
                for (int i = 0; i < hexData.Length / 4; i++)
                {
                    int off = (startAddr + i) * 2;
                    if (off + 1 < storage.Length)
                    {
                        storage[off] = (byte)(HexV(hexData[i * 4]) << 4 | HexV(hexData[i * 4 + 1]));
                        storage[off + 1] = (byte)(HexV(hexData[i * 4 + 2]) << 4 | HexV(hexData[i * 4 + 3]));
                    }
                }
            }

            return BuildFrame(station + "WR");
        }

        private byte[] BuildFrame(string body)
        {
            string frame = "\x02" + body + "\x03";
            // BCC = XOR of all bytes from Station to ETX (inclusive)
            byte bcc = 0;
            var bodyBytes = Encoding.ASCII.GetBytes(body);
            foreach (byte b in bodyBytes) bcc ^= b;
            bcc ^= 0x03; // ETX
            frame += bcc.ToString("X2");
            return Encoding.ASCII.GetBytes(frame);
        }

        private byte[] BuildErrorResponse(string station, string command)
        {
            string body = station + "FF00";
            return BuildFrame(body);
        }

        private byte[]? GetStorage(string areaCode) => areaCode switch
        {
            "01" => _dArea, // D
            "02" => _mArea, // M
            "03" => _xArea, // X
            "04" => _yArea, // Y
            "05" => _tArea, // T
            "06" => _cArea, // C
            _ => null
        };

        private static int HexV(char c) => c >= '0' && c <= '9' ? c - '0' : c >= 'A' && c <= 'F' ? c - 'A' + 10 : c >= 'a' && c <= 'f' ? c - 'a' + 10 : 0;

        // For lock consistency
        private readonly object _storage = new object();

        public void Dispose() => Stop();
    }
}
