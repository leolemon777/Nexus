using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Omron
{
    /// <summary>
    /// FINS 虚拟服务器 — 模拟欧姆龙 PLC FINS-TCP 通讯，用于无硬件测试。
    /// 实现连接握手 + Memory Area Read/Write/Fill + PLC 控制（Run/Stop/状态/时钟）。
    /// </summary>
    public class FinsVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // ── 内存模型 ─────────────────────────────
        private readonly byte[] _cio = new byte[65536];      // CIO 区: 32768 words
        private readonly byte[] _dm = new byte[65536];        // DM 区: 32768 words
        private readonly byte[] _wr = new byte[512];          // WR 区: 256 words
        private readonly byte[] _hr = new byte[512];          // HR 区: 256 words
        private readonly byte[] _ar = new byte[512];          // AR 区: 256 words
        private readonly byte[][] _em = new byte[16][];       // EM 区: 16 banks × 32768 words
        private readonly object _memLock = new object();

        // ── PLC 状态 ─────────────────────────────
        private volatile bool _plcRunning = true;
        private string _plcModel = "CJ2M-CPU33";

        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _clientsLock = new object();

        public int Port { get; }
        public bool IsRunning => _running;
        public byte ServerNode { get; set; } = 0x01;

        public FinsVirtualServer(int port = 9600)
        {
            Port = port;
            // 初始化 EM bank 0-1
            _em[0] = new byte[65536];
            _em[1] = new byte[65536];
        }

        // ── 预设数据 API ─────────────────────────

        public void SetDMWord(int wordOffset, short value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _dm[byteOff] = (byte)(value >> 8);
                _dm[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetDMWord(int offset, ushort value)
        {
            lock (_memLock)
            {
                _dm[offset * 2] = (byte)(value >> 8);
                _dm[offset * 2 + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetDMDWord(int wordOffset, int value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _dm[byteOff] = (byte)(value >> 24);
                _dm[byteOff + 1] = (byte)(value >> 16);
                _dm[byteOff + 2] = (byte)(value >> 8);
                _dm[byteOff + 3] = (byte)(value & 0xFF);
            }
        }

        public void SetDMReal(int wordOffset, float value)
        {
            var bytes = DataConverter.GetBytes(value);
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                Buffer.BlockCopy(bytes, 0, _dm, byteOff, 4);
            }
        }

        public void SetCIOWord(int wordOffset, short value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _cio[byteOff] = (byte)(value >> 8);
                _cio[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetCIOWord(int wordOffset, ushort value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _cio[byteOff] = (byte)(value >> 8);
                _cio[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetWRWord(int wordOffset, ushort value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _wr[byteOff] = (byte)(value >> 8);
                _wr[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetHRWord(int wordOffset, ushort value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _hr[byteOff] = (byte)(value >> 8);
                _hr[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetARWord(int wordOffset, ushort value)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _ar[byteOff] = (byte)(value >> 8);
                _ar[byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetEMWord(int bank, int wordOffset, ushort value)
        {
            EnsureEM(bank);
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                _em[bank][byteOff] = (byte)(value >> 8);
                _em[bank][byteOff + 1] = (byte)(value & 0xFF);
            }
        }

        public ushort GetDMWord(int wordOffset)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                return (ushort)((_dm[byteOff] << 8) | _dm[byteOff + 1]);
            }
        }

        public short GetDMWordSigned(int wordOffset)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                return (short)((_dm[byteOff] << 8) | _dm[byteOff + 1]);
            }
        }

        private void EnsureEM(int bank)
        {
            if (bank < 0 || bank >= 16) throw new ArgumentOutOfRangeException(nameof(bank));
            if (_em[bank] == null) _em[bank] = new byte[65536];
        }

        // ── PLC 状态控制 API（测试用）───────────────

        /// <summary>获取 PLC 运行状态。</summary>
        public bool IsPlcRunning => _plcRunning;

        /// <summary>设置 PLC 运行状态。</summary>
        public void SetPlcRunning(bool running) => _plcRunning = running;

        /// <summary>获取/设置 PLC 型号名称。</summary>
        public string PlcModel
        {
            get => _plcModel;
            set => _plcModel = value ?? "Unknown";
        }

        /// <summary>获取 DM 区原始字节数据（测试用）。</summary>
        public byte[] GetDMBytes(int wordOffset, int wordCount)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                int byteCount = wordCount * 2;
                if (byteOff + byteCount > _dm.Length)
                    throw new ArgumentOutOfRangeException(nameof(wordOffset));
                byte[] data = new byte[byteCount];
                Buffer.BlockCopy(_dm, byteOff, data, 0, byteCount);
                return data;
            }
        }

        /// <summary>设置 DM 区原始字节数据（测试用）。</summary>
        public void SetDMBytes(int wordOffset, byte[] data)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                if (byteOff + data.Length > _dm.Length)
                    throw new ArgumentOutOfRangeException(nameof(wordOffset));
                Buffer.BlockCopy(data, 0, _dm, byteOff, data.Length);
            }
        }

        /// <summary>获取 CIO 区原始字节数据（测试用）。</summary>
        public byte[] GetCIOBytes(int wordOffset, int wordCount)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                int byteCount = wordCount * 2;
                if (byteOff + byteCount > _cio.Length)
                    throw new ArgumentOutOfRangeException(nameof(wordOffset));
                byte[] data = new byte[byteCount];
                Buffer.BlockCopy(_cio, byteOff, data, 0, byteCount);
                return data;
            }
        }

        /// <summary>设置 CIO 区原始字节数据（测试用）。</summary>
        public void SetCIOBytes(int wordOffset, byte[] data)
        {
            lock (_memLock)
            {
                int byteOff = wordOffset * 2;
                if (byteOff + data.Length > _cio.Length)
                    throw new ArgumentOutOfRangeException(nameof(wordOffset));
                Buffer.BlockCopy(data, 0, _cio, byteOff, data.Length);
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
            _listener?.Stop();
            lock (_clientsLock)
            {
                foreach (var c in _clients)
                {
                    try { c.Close(); } catch { }
                }
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
                    lock (_clientsLock) { _clients.Add(client); }
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            byte clientNode = 0x00;
            bool handshakeDone = false;

            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        // 读取帧长度 (4 bytes)
                        byte[]? lenBuf = ReadExact(stream, 4);
                        if (lenBuf == null) break;

                        int frameLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                        if (frameLen < 4 || frameLen > 65536) break;

                        int payloadLen = frameLen - 4;
                        byte[]? payload = payloadLen > 0 ? ReadExact(stream, payloadLen) : new byte[0];
                        if (payload == null) break;

                        // 完整帧
                        byte[] frame = new byte[frameLen];
                        Buffer.BlockCopy(lenBuf, 0, frame, 0, 4);
                        if (payload.Length > 0)
                            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

                        byte[]? response;

                        if (!handshakeDone)
                        {
                            // 连接握手: 检查是否是全零命令
                            if (payload.Length >= 4 &&
                                payload[0] == 0x00 && payload[1] == 0x00 &&
                                payload[2] == 0x00 && payload[3] == 0x00)
                            {
                                clientNode = (byte)(0x02); // 分配客户端节点号
                                handshakeDone = true;
                                response = BuildHandshakeResponse(clientNode);
                                stream.Write(response, 0, response.Length);
                                continue;
                            }
                        }

                        if (handshakeDone && payload.Length >= 12)
                        {
                            // FINS 命令: FINS Header(10) + CommandCode(2) + Data
                            ushort cmdCode = (ushort)((payload[10] << 8) | payload[11]);
                            response = ProcessFinsCommand(payload, cmdCode);

                            if (response != null)
                            {
                                // 加上帧长度头
                                byte[] fullResp = new byte[4 + response.Length];
                                int totalLen = response.Length + 4;
                                fullResp[0] = (byte)(totalLen >> 24);
                                fullResp[1] = (byte)(totalLen >> 16);
                                fullResp[2] = (byte)(totalLen >> 8);
                                fullResp[3] = (byte)(totalLen & 0xFF);
                                Buffer.BlockCopy(response, 0, fullResp, 4, response.Length);
                                stream.Write(fullResp, 0, fullResp.Length);
                                continue;
                            }
                        }

                        // 未知帧，断开
                        break;
                    }
                }
            }
            catch { }
            finally
            {
                lock (_clientsLock) { _clients.Remove(client); }
            }
        }

        // ── 连接握手响应 ──────────────────────────

        private byte[] BuildHandshakeResponse(byte clientNode)
        {
            // 响应: FrameLen(4) + 0x00000000(4) + ServerNode(1) + 0x00(1) + ClientNode(1) + 0x00(1)
            byte[] resp = new byte[12];
            resp[0] = 0x00; resp[1] = 0x00; resp[2] = 0x00; resp[3] = 0x0C; // FrameLen = 12
            resp[4] = 0x00; resp[5] = 0x00; resp[6] = 0x00; resp[7] = 0x00; // Command
            resp[8] = ServerNode;    // Server node
            resp[9] = 0x00;          // Reserved
            resp[10] = clientNode;   // Client node
            resp[11] = 0x00;         // Reserved
            return resp;
        }

        // ── FINS 命令处理 ──────────────────────────

        private byte[]? ProcessFinsCommand(byte[] finsFrame, ushort cmdCode)
        {
            // finsFrame: ICF(1)+RSV(1)+GCT(1)+DNA(1)+DA1(1)+DA2(1)+SNA(1)+SA1(1)+SA2(1)+SID(1)+CommandCode(2)+Data(N)
            // 客户端请求的 ICF[0]=0x80 → 响应 ICF=0xC1
            byte[] header = new byte[10];
            header[0] = 0xC1; // ICF: response
            header[1] = 0x00; // RSV
            header[2] = 0x02; // GCT
            header[3] = finsFrame[6];  // DNA = 客户端的 SNA
            header[4] = finsFrame[7];  // DA1 = 客户端的 SA1
            header[5] = finsFrame[8];  // DA2 = 客户端的 SA2
            header[6] = finsFrame[3];  // SNA = 客户端的 DNA
            header[7] = finsFrame[4];  // SA1 = 客户端的 DA1
            header[8] = finsFrame[5];  // SA2 = 客户端的 DA2
            header[9] = finsFrame[9];  // SID = echo

            return cmdCode switch
            {
                FinsCommandCode.MemoryAreaRead => ProcessMemoryAreaRead(finsFrame, header),
                FinsCommandCode.MemoryAreaWrite => ProcessMemoryAreaWrite(finsFrame, header),
                FinsCommandCode.MemoryAreaFill => ProcessMemoryAreaFill(finsFrame, header),
                FinsCommandCode.ControllerStatusRead => ProcessControllerStatus(header),
                FinsCommandCode.ControllerRead => ProcessControllerRead(header),
                FinsCommandCode.Run => ProcessRun(finsFrame, header),
                FinsCommandCode.Stop => ProcessStop(finsFrame, header),
                FinsCommandCode.TimeRead => ProcessTimeRead(header),
                FinsCommandCode.TimeWrite => ProcessTimeWrite(finsFrame, header),
                _ => BuildFinsResponse(header, cmdCode, 0x0001) // 不支持的命令
            };
        }

        // ── Memory Area Read ──────────────────────

        private byte[] ProcessMemoryAreaRead(byte[] finsFrame, byte[] header)
        {
            // Command data 从 offset 12 开始
            // AreaCode(1) + BitAccess(1) + WordAddressHi(1) + WordAddressLo(1) + BitOffset(1) + ReadWordCount(2) = 7 bytes
            if (finsFrame.Length < 19)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaRead, 0x0202);

            byte areaCode = finsFrame[12];
            byte bitAccess = finsFrame[13];
            int wordAddr = (finsFrame[14] << 8) | finsFrame[15];
            byte bitOffset = finsFrame[16];
            ushort readCount = (ushort)((finsFrame[17] << 8) | finsFrame[18]);

            byte[] data = ReadFromMemory(areaCode, wordAddr, readCount, bitAccess);
            if (data == null)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaRead, 0x0301);

            // 响应: Header(10) + CommandCode(2) + EndCode(2) + Data(N)
            byte[] response = new byte[14 + data.Length];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(FinsCommandCode.MemoryAreaRead >> 8);
            response[11] = (byte)(FinsCommandCode.MemoryAreaRead & 0xFF);
            response[12] = 0x00; response[13] = 0x00; // EndCode = Success
            Buffer.BlockCopy(data, 0, response, 14, data.Length);
            return response;
        }

        // ── Memory Area Write ─────────────────────

        private byte[] ProcessMemoryAreaWrite(byte[] finsFrame, byte[] header)
        {
            if (finsFrame.Length < 19)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaWrite, 0x0202);

            byte areaCode = finsFrame[12];
            byte bitAccess = finsFrame[13];
            int wordAddr = (finsFrame[14] << 8) | finsFrame[15];
            byte bitOffset = finsFrame[16];
            ushort writeCount = (ushort)((finsFrame[17] << 8) | finsFrame[18]);

            int dataLen = writeCount * 2;
            if (finsFrame.Length < 19 + dataLen)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaWrite, 0x0202);

            byte[] writeData = new byte[dataLen];
            Buffer.BlockCopy(finsFrame, 19, writeData, 0, dataLen);

            bool success = WriteToMemory(areaCode, wordAddr, writeData);
            if (!success)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaWrite, 0x0301);

            // 成功响应: Header(10) + CommandCode(2) + EndCode(2)
            byte[] response = new byte[14];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(FinsCommandCode.MemoryAreaWrite >> 8);
            response[11] = (byte)(FinsCommandCode.MemoryAreaWrite & 0xFF);
            response[12] = 0x00; response[13] = 0x00;
            return response;
        }

        // ── Controller Status ─────────────────────

        private byte[] ProcessControllerStatus(byte[] header)
        {
            // 状态字节: bit0=Run/Stop, bit1=HasError, bit2..7=保留
            byte statusByte = (byte)(_plcRunning ? 0x00 : 0x01);

            byte[] status = new byte[4];
            status[0] = statusByte;
            status[1] = 0x00;
            status[2] = 0x00;
            status[3] = 0x00;

            byte[] response = new byte[14 + status.Length];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(FinsCommandCode.ControllerStatusRead >> 8);
            response[11] = (byte)(FinsCommandCode.ControllerStatusRead & 0xFF);
            response[12] = 0x00; response[13] = 0x00;
            Buffer.BlockCopy(status, 0, response, 14, status.Length);
            return response;
        }

        // ── Controller Read (CPU Unit Data) ───────

        private byte[] ProcessControllerRead(byte[] header)
        {
            // 返回 CPU 单元数据 — 型号代码(20 bytes) + 版本等(共约 162 bytes)
            // 简化: 返回型号(20 bytes padded) + 版本(4 bytes) + 其余补零(总共 80 bytes)
            byte[] data = new byte[80];

            // 型号代码 (20 字节 ASCII)
            byte[] modelBytes = System.Text.Encoding.ASCII.GetBytes(_plcModel);
            int copyLen = Math.Min(modelBytes.Length, 20);
            Buffer.BlockCopy(modelBytes, 0, data, 0, copyLen);

            // 版本信息 (偏移 20-23)
            data[20] = 0x01; // 版本主号
            data[21] = 0x00; // 版本次号

            byte[] response = new byte[14 + data.Length];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(FinsCommandCode.ControllerRead >> 8);
            response[11] = (byte)(FinsCommandCode.ControllerRead & 0xFF);
            response[12] = 0x00; response[13] = 0x00;
            Buffer.BlockCopy(data, 0, response, 14, data.Length);
            return response;
        }

        // ── Run / Stop ───────────────────────────

        private byte[] ProcessRun(byte[] finsFrame, byte[] header)
        {
            // FINS Run 命令数据: Mode(1) + 保留(3)
            if (finsFrame.Length < 16)
                return BuildFinsResponse(header, FinsCommandCode.Run, 0x0202);

            _plcRunning = true;
            return BuildFinsResponse(header, FinsCommandCode.Run, 0x0000);
        }

        private byte[] ProcessStop(byte[] finsFrame, byte[] header)
        {
            if (finsFrame.Length < 16)
                return BuildFinsResponse(header, FinsCommandCode.Stop, 0x0202);

            _plcRunning = false;
            return BuildFinsResponse(header, FinsCommandCode.Stop, 0x0000);
        }

        // ── Time Read / Time Write ───────────────

        private byte[] ProcessTimeRead(byte[] header)
        {
            // 返回 PLC 时钟 (BCD 格式): YearHi(1) + YearLo(1) + Month(1) + Day(1) + Hour(1) + Minute(1) + Second(1)
            var now = DateTime.Now;
            byte[] data = new byte[7];
            data[0] = DecimalToBcd(now.Year / 100);
            data[1] = DecimalToBcd(now.Year % 100);
            data[2] = DecimalToBcd(now.Month);
            data[3] = DecimalToBcd(now.Day);
            data[4] = DecimalToBcd(now.Hour);
            data[5] = DecimalToBcd(now.Minute);
            data[6] = DecimalToBcd(now.Second);

            byte[] response = new byte[14 + data.Length];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(FinsCommandCode.TimeRead >> 8);
            response[11] = (byte)(FinsCommandCode.TimeRead & 0xFF);
            response[12] = 0x00; response[13] = 0x00;
            Buffer.BlockCopy(data, 0, response, 14, data.Length);
            return response;
        }

        private byte[] ProcessTimeWrite(byte[] finsFrame, byte[] header)
        {
            // 写入时钟: 需要 7 字节 BCD 数据
            if (finsFrame.Length < 19)
                return BuildFinsResponse(header, FinsCommandCode.TimeWrite, 0x0202);

            // 虚拟服务器接受时钟写入（实际不持久化，仅响应成功）
            return BuildFinsResponse(header, FinsCommandCode.TimeWrite, 0x0000);
        }

        // ── Memory Area Fill ─────────────────────

        private byte[] ProcessMemoryAreaFill(byte[] finsFrame, byte[] header)
        {
            // Fill 命令数据: AreaCode(1) + BitAccess(1) + WordAddressHi(1) + WordAddressLo(1)
            //               + BitOffset(1) + FillWordCount(2) + FillData(2)
            if (finsFrame.Length < 21)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaFill, 0x0202);

            byte areaCode = finsFrame[12];
            int wordAddr = (finsFrame[14] << 8) | finsFrame[15];
            ushort fillCount = (ushort)((finsFrame[17] << 8) | finsFrame[18]);
            byte fillHi = finsFrame[19];
            byte fillLo = finsFrame[20];

            byte[]? target = GetMemoryBuffer(areaCode);
            if (target == null)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaFill, 0x0301);

            int byteAddr = wordAddr * 2;
            int fillBytes = fillCount * 2;
            if (byteAddr + fillBytes > target.Length)
                return BuildFinsResponse(header, FinsCommandCode.MemoryAreaFill, 0x0303);

            lock (_memLock)
            {
                for (int i = 0; i < fillCount; i++)
                {
                    target[byteAddr + i * 2] = fillHi;
                    target[byteAddr + i * 2 + 1] = fillLo;
                }
            }

            return BuildFinsResponse(header, FinsCommandCode.MemoryAreaFill, 0x0000);
        }

        // ── BCD 转换 ──────────────────────────────

        private static byte DecimalToBcd(int value)
        {
            return (byte)(((value / 10) << 4) | (value % 10));
        }

        // ── 内存读写 ──────────────────────────────

        private byte[]? ReadFromMemory(byte areaCode, int wordAddr, ushort wordCount, byte bitAccess)
        {
            int byteAddr = wordAddr * 2;
            int byteCount = wordCount * 2;

            byte[]? source = GetMemoryBuffer(areaCode);
            if (source == null) return null;

            if (byteAddr + byteCount > source.Length) return null;

            byte[] data = new byte[byteCount];
            lock (_memLock)
            {
                Buffer.BlockCopy(source, byteAddr, data, 0, byteCount);
            }
            return data;
        }

        private bool WriteToMemory(byte areaCode, int wordAddr, byte[] data)
        {
            int byteAddr = wordAddr * 2;

            byte[]? target = GetMemoryBuffer(areaCode);
            if (target == null) return false;

            if (byteAddr + data.Length > target.Length) return false;

            lock (_memLock)
            {
                Buffer.BlockCopy(data, 0, target, byteAddr, data.Length);
            }
            return true;
        }

        private byte[]? GetMemoryBuffer(byte areaCode)
        {
            return areaCode switch
            {
                (byte)FinsMemoryArea.CIO => _cio,
                (byte)FinsMemoryArea.DM => _dm,
                (byte)FinsMemoryArea.WR => _wr,
                (byte)FinsMemoryArea.HR => _hr,
                (byte)FinsMemoryArea.AR => _ar,
                (byte)FinsMemoryArea.EM => _em[0], // 默认 bank 0
                _ => null
            };
        }

        // ── 辅助 ──────────────────────────────────

        private static byte[] BuildFinsResponse(byte[] header, ushort cmdCode, ushort endCode)
        {
            byte[] response = new byte[14];
            Buffer.BlockCopy(header, 0, response, 0, 10);
            response[10] = (byte)(cmdCode >> 8);
            response[11] = (byte)(cmdCode & 0xFF);
            response[12] = (byte)(endCode >> 8);
            response[13] = (byte)(endCode & 0xFF);
            return response;
        }

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        public void Dispose() => Stop();
    }
}
