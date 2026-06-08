using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Siemens
{
    /// <summary>
    /// S7 虚拟 PLC 服务器 — 模拟西门子 S7-1200/1500 PLC，用于无硬件测试。
    /// 实现完整的 TPKT + COTP + S7 Communication 三层协议栈服务端。
    /// 内存模型: DB 块(0-4095) + I 区 + Q 区 + M 区 + V 区 + C 区 + T 区。
    /// </summary>
    public class SiemensS7VirtualPlc : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        /// <summary>协商的 PDU 大小。</summary>
        public ushort PduSize { get; set; } = 240;

        /// <summary>PLC 型号。</summary>
        public SiemensPLCS Model { get; }

        // ── 内存模型 ─────────────────────────────
        private readonly byte[][] _dbBlocks = new byte[4096][];
        private readonly byte[] _inputs = new byte[65536];      // I 区 (PE 0x81)
        private readonly byte[] _outputs = new byte[65536];     // Q 区 (PA 0x82)
        private readonly byte[] _markers = new byte[65536];     // M 区 (MK 0x83)
        private readonly byte[] _vArea = new byte[65536];       // V 区 (V 存储区，S7-200/SMART)
        private readonly byte[] _counters = new byte[65536];    // C 区 (CT 0x1C)
        private readonly byte[] _timers = new byte[65536];      // T 区 (TM 0x1D)
        private readonly object _memLock = new object();

        private readonly ConcurrentDictionary<TcpClient, byte> _clients = new ConcurrentDictionary<TcpClient, byte>();

        /// <summary>写入接收到事件 — 当客户端写入任何内存区域时触发。</summary>
        public event EventHandler<S7WriteEventArgs>? OnWriteReceived;

        public int Port { get; }
        public bool IsRunning => _running;

        public SiemensS7VirtualPlc(SiemensPLCS model = SiemensPLCS.S7_1200, int port = 102)
        {
            Model = model;
            Port = port;
            // 初始化 DB 块 1-10
            for (int i = 1; i <= 10; i++)
                _dbBlocks[i] = new byte[65536];
        }

        // ── 预设数据 API ─────────────────────────

        public void SetDBWord(int dbNum, int offset, short value)
        {
            EnsureDB(dbNum);
            lock (_memLock)
            {
                _dbBlocks[dbNum][offset] = (byte)(value >> 8);
                _dbBlocks[dbNum][offset + 1] = (byte)(value & 0xFF);
            }
        }

        public void SetDBDWord(int dbNum, int offset, int value)
        {
            EnsureDB(dbNum);
            lock (_memLock)
            {
                _dbBlocks[dbNum][offset] = (byte)(value >> 24);
                _dbBlocks[dbNum][offset + 1] = (byte)(value >> 16);
                _dbBlocks[dbNum][offset + 2] = (byte)(value >> 8);
                _dbBlocks[dbNum][offset + 3] = (byte)(value & 0xFF);
            }
        }

        public void SetDBReal(int dbNum, int offset, float value)
        {
            EnsureDB(dbNum);
            var bytes = DataConverter.GetBytes(value);
            lock (_memLock)
            {
                Buffer.BlockCopy(bytes, 0, _dbBlocks[dbNum], offset, 4);
            }
        }

        /// <summary>写入原始字节数组到 DB 块（用于字符串等复杂数据设置）。</summary>
        public void SetDBBytes(int dbNum, int offset, byte[] data)
        {
            EnsureDB(dbNum);
            lock (_memLock)
            {
                Buffer.BlockCopy(data, 0, _dbBlocks[dbNum], offset, data.Length);
            }
        }

        /// <summary>读取 DB 块原始字节数组。</summary>
        public byte[] GetDBBytes(int dbNum, int offset, int length)
        {
            EnsureDB(dbNum);
            byte[] result = new byte[length];
            lock (_memLock)
            {
                Buffer.BlockCopy(_dbBlocks[dbNum], offset, result, 0, length);
            }
            return result;
        }

        public void SetMarkerByte(int offset, byte value)
        {
            lock (_memLock) { _markers[offset] = value; }
        }

        public void SetInputByte(int offset, byte value)
        {
            lock (_memLock) { _inputs[offset] = value; }
        }

        public void SetOutputByte(int offset, byte value)
        {
            lock (_memLock) { _outputs[offset] = value; }
        }

        public void SetVAreaByte(int offset, byte value)
        {
            lock (_memLock) { _vArea[offset] = value; }
        }

        public byte GetVAreaByte(int offset)
        {
            lock (_memLock) { return _vArea[offset]; }
        }

        public void SetCounterByte(int offset, byte value)
        {
            lock (_memLock) { _counters[offset] = value; }
        }

        public byte GetCounterByte(int offset)
        {
            lock (_memLock) { return _counters[offset]; }
        }

        public void SetTimerByte(int offset, byte value)
        {
            lock (_memLock) { _timers[offset] = value; }
        }

        public byte GetTimerByte(int offset)
        {
            lock (_memLock) { return _timers[offset]; }
        }

        public short GetDBWord(int dbNum, int offset)
        {
            EnsureDB(dbNum);
            lock (_memLock)
            {
                return (short)((_dbBlocks[dbNum][offset] << 8) | _dbBlocks[dbNum][offset + 1]);
            }
        }

        public int GetDBDWord(int dbNum, int offset)
        {
            EnsureDB(dbNum);
            lock (_memLock)
            {
                return (_dbBlocks[dbNum][offset] << 24) | (_dbBlocks[dbNum][offset + 1] << 16) |
                       (_dbBlocks[dbNum][offset + 2] << 8) | _dbBlocks[dbNum][offset + 3];
            }
        }

        private void EnsureDB(int dbNum)
        {
            if (dbNum < 0 || dbNum >= 4096) throw new ArgumentOutOfRangeException(nameof(dbNum));
            if (_dbBlocks[dbNum] == null) _dbBlocks[dbNum] = new byte[65536];
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
            foreach (var kv in _clients)
            {
                try { kv.Key.Close(); } catch { }
            }
            _clients.Clear();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    _clients.TryAdd(client, 0);
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
                        // 读取 TPKT Header (4 bytes)
                        byte[]? tpktHeader = ReadExact(stream, 4);
                        if (tpktHeader == null) break;

                        int totalLen = (tpktHeader[2] << 8) | tpktHeader[3];
                        int payloadLen = totalLen - 4;
                        if (payloadLen < 0 || payloadLen > 65535) break;

                        byte[]? payload = payloadLen > 0 ? ReadExact(stream, payloadLen) : new byte[0];
                        if (payload == null) break;

                        // 组装完整帧
                        byte[] frame = new byte[totalLen];
                        Buffer.BlockCopy(tpktHeader, 0, frame, 0, 4);
                        if (payload.Length > 0)
                            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);

                        // 处理帧并生成响应
                        byte[]? response = ProcessFrame(frame);
                        if (response != null)
                        {
                            stream.Write(response, 0, response.Length);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                _clients.TryRemove(client, out _);
            }
        }

        // ── 协议处理 ──────────────────────────────

        private byte[]? ProcessFrame(byte[] frame)
        {
            if (frame.Length < 8) return null;

            // TPKT Header: Version(1) + Reserved(1) + Length(2)
            // COTP: Length(1) + PDU Type(1) + ...
            byte cotpLen = frame[4];
            byte cotpType = frame[5];

            if (cotpType == 0xE0)
            {
                // COTP Connection Request → Connection Confirm
                return BuildCOTPConnectionConfirm(frame);
            }
            else if (cotpType == 0xF0)
            {
                // COTP Data Transfer → 处理 S7 层
                // COTP DT: Length(1) + Type(1) + TPU(1) = 3 bytes (after TPKT)
                int cotpHeaderEnd = 4 + 1 + cotpLen; // TPKT(4) + COTP(1+len)
                if (frame.Length < cotpHeaderEnd) return null;

                // S7 层起始
                byte[] s7Data = new byte[frame.Length - cotpHeaderEnd];
                Buffer.BlockCopy(frame, cotpHeaderEnd, s7Data, 0, s7Data.Length);

                byte[]? s7Response = ProcessS7(s7Data);
                if (s7Response == null) return null;

                // 构建 TPKT + COTP DT + S7 响应
                return BuildTPKTCOTPData(s7Response);
            }

            return null;
        }

        // ── COTP Connection Confirm ───────────────

        private byte[] BuildCOTPConnectionConfirm(byte[] request)
        {
            // 简化的 COTP CC: 回传请求中的参数
            // TPKT(4) + COTP CC
            byte[] cotpCC = new byte[] {
                0x0D,       // Length (13 bytes following)
                0xD0,       // PDU Type: Connection Confirm
                0x00, 0x01, // Dest Reference
                0x00, 0x01, // Src Reference
                0x00,       // Flags
                // Source TSAP
                0xC1, 0x02, 0x01, 0x00,
                // Dest TSAP
                0xC2, 0x02, 0x01, 0x02
            };

            int totalLen = 4 + cotpCC.Length;
            byte[] result = new byte[totalLen];
            result[0] = 0x03; result[1] = 0x00;
            result[2] = (byte)(totalLen >> 8); result[3] = (byte)totalLen;
            Buffer.BlockCopy(cotpCC, 0, result, 4, cotpCC.Length);
            return result;
        }

        // ── S7 Communication 处理 ────────────────

        private byte[]? ProcessS7(byte[] s7Data)
        {
            if (s7Data.Length < 10) return null;

            byte protocolId = s7Data[0];
            byte msgType = s7Data[1];

            if (protocolId != 0x32) return null;

            // S7 Header: ProtocolId(1) + MsgType(1) + Reserved(2) + PduRef(2) + ParamLen(2) + DataLen(2)
            int paramLen = (s7Data[6] << 8) | s7Data[7];
            int dataLen = (s7Data[8] << 8) | s7Data[9];

            byte function = s7Data.Length > 10 ? s7Data[10] : (byte)0;

            return function switch
            {
                0xF0 => BuildS7SetupCommunication(s7Data),
                0x04 => ProcessS7ReadVar(s7Data, paramLen, dataLen),
                0x05 => ProcessS7WriteVar(s7Data, paramLen, dataLen),
                _ => BuildS7Error(function, 0x02)
            };
        }

        // ── S7 Setup Communication 响应 ──────────

        private byte[] BuildS7SetupCommunication(byte[] request)
        {
            // S7 AckData Header(12) + Parameter(8)
            byte[] resp = new byte[20];
            resp[0] = 0x32;   // Protocol ID
            resp[1] = 0x03;   // Msg Type: Ack Data
            resp[2] = request[2]; resp[3] = request[3]; // Reserved
            resp[4] = request[4]; resp[5] = request[5]; // PDU Reference (echo back)
            resp[6] = 0x00; resp[7] = 0x08; // Param Length = 8
            resp[8] = 0x00; resp[9] = 0x00; // Data Length = 0
            resp[10] = 0x00;  // Error class (no error)
            resp[11] = 0x00;  // Error code (no error)
            // Parameter
            resp[12] = 0xFF;   // Reserved / Function echo
            resp[13] = 0x00;   // Reserved
            resp[14] = 0x00; resp[15] = 0x01; // Max AMQ Calling
            resp[16] = 0x00; resp[17] = 0x01; // Max AMQ Receiving
            resp[18] = (byte)(PduSize >> 8);
            resp[19] = (byte)(PduSize & 0xFF);
            return resp;
        }

        // ── S7 Read Var ──────────────────────────

        private byte[] ProcessS7ReadVar(byte[] s7Data, int paramLen, int dataLen)
        {
            // 参数区: Function(1) + ItemCount(1) + AddressItem(12) * N
            if (s7Data.Length < 12) return BuildS7Error(0x04, 0x05);

            byte itemCount = s7Data[11];

            // 构建响应参数: Function(1) + ItemCount(1)
            // 构建响应数据: 每个Item: ReturnCode(1) + TransportSize(1) + Length(2) + Data(N)
            using var paramMs = new System.IO.MemoryStream();
            using var dataMs = new System.IO.MemoryStream();

            paramMs.WriteByte(0x04); // Function
            paramMs.WriteByte(itemCount);

            for (int i = 0; i < itemCount; i++)
            {
                int itemOffset = 12 + i * 12;
                if (itemOffset + 12 > s7Data.Length) break;

                // 标准 S7 Any 地址项布局
                byte spec = s7Data[itemOffset];     // 0x12 = S7 Any
                byte transportSize = s7Data[itemOffset + 3]; // Transport size
                int length = (s7Data[itemOffset + 4] << 8) | s7Data[itemOffset + 5];
                int dbNum = (s7Data[itemOffset + 6] << 8) | s7Data[itemOffset + 7];
                S7Area area = (S7Area)s7Data[itemOffset + 8];
                int byteAddrBits = (s7Data[itemOffset + 9] << 16) | (s7Data[itemOffset + 10] << 8) | s7Data[itemOffset + 11];
                int byteAddr = byteAddrBits / 8;
                int bitOffset = byteAddrBits % 8;

                // 计算读取字节数
                int bytesToRead = transportSize == 0x01 ? (length + 7) / 8 : length;

                // 从内存读取
                byte[] data = ReadFromMemory(area, dbNum, byteAddr, bytesToRead);

                // 写入响应数据项: ReturnCode(1) + TransportSize(1) + Length(2) + Data(N)
                dataMs.WriteByte(0xFF); // Return Code: Success
                if (transportSize == 0x01)
                {
                    // Bit: 提取指定位，返回 0x00 或 0x01（S7 协议：bit 值始终在 position 0）
                    byte bitValue = (byte)((data[0] >> bitOffset) & 0x01);
                    dataMs.WriteByte(0x03); // Transport size for bit
                    dataMs.WriteByte(0x00);
                    dataMs.WriteByte(0x01); // 1 byte
                    dataMs.WriteByte(bitValue);
                }
                else
                {
                    dataMs.WriteByte(transportSize);
                    dataMs.WriteByte((byte)(data.Length >> 8));
                    dataMs.WriteByte((byte)(data.Length & 0xFF));
                    dataMs.Write(data, 0, data.Length);
                }
            }

            byte[] paramBytes = paramMs.ToArray();
            byte[] dataBytes = dataMs.ToArray();

            return BuildS7AckData(s7Data, 0x04, paramBytes, dataBytes);
        }

        // ── S7 Write Var ─────────────────────────

        private byte[] ProcessS7WriteVar(byte[] s7Data, int paramLen, int dataLen)
        {
            if (s7Data.Length < 12) return BuildS7Error(0x05, 0x05);

            byte itemCount = s7Data[11];

            // 参数区
            using var paramMs = new System.IO.MemoryStream();
            using var dataMs = new System.IO.MemoryStream();

            paramMs.WriteByte(0x05); // Function
            paramMs.WriteByte(itemCount);

            // 数据区从 header(10) + param 之后开始
            int dataStart = 10 + paramLen;

            for (int i = 0; i < itemCount; i++)
            {
                int itemOffset = 12 + i * 12;
                if (itemOffset + 12 > s7Data.Length) break;

                // 标准 S7 Any 地址项布局
                byte transportSize = s7Data[itemOffset + 3];
                int length = (s7Data[itemOffset + 4] << 8) | s7Data[itemOffset + 5];
                int dbNum = (s7Data[itemOffset + 6] << 8) | s7Data[itemOffset + 7];
                S7Area area = (S7Area)s7Data[itemOffset + 8];
                int byteAddrBits = (s7Data[itemOffset + 9] << 16) | (s7Data[itemOffset + 10] << 8) | s7Data[itemOffset + 11];
                int byteAddr = byteAddrBits / 8;
                int bitOffset = byteAddrBits % 8;

                // 从请求数据区读取写入值
                // 每个数据项: ReturnCode(1) + TransportSize(1) + Length(2) + Data(N)
                int dataItemOffset = dataStart + i * 4; // 简化
                if (dataItemOffset + 4 > s7Data.Length) break;

                // 寻找对应的数据项
                int currentDataOffset = dataStart;
                for (int j = 0; j < i; j++)
                {
                    if (currentDataOffset + 4 > s7Data.Length) break;
                    int itemLen = (s7Data[currentDataOffset + 2] << 8) | s7Data[currentDataOffset + 3];
                    currentDataOffset += 4 + itemLen;
                }

                if (currentDataOffset + 4 > s7Data.Length) break;
                int writeLen = (s7Data[currentDataOffset + 2] << 8) | s7Data[currentDataOffset + 3];

                byte[] writeData = new byte[writeLen];
                if (currentDataOffset + 4 + writeLen <= s7Data.Length)
                {
                    Buffer.BlockCopy(s7Data, currentDataOffset + 4, writeData, 0, writeLen);
                }

                // 写入内存
                if (transportSize == 0x01)
                {
                    // Bit: read-modify-write 指定位
                    byte[] current = ReadFromMemory(area, dbNum, byteAddr, 1);
                    byte mask = (byte)(1 << bitOffset);
                    if (writeData[0] != 0)
                        current[0] |= mask;
                    else
                        current[0] &= (byte)~mask;
                    WriteToMemory(area, dbNum, byteAddr, current);
                }
                else
                {
                    WriteToMemory(area, dbNum, byteAddr, writeData);
                }

                // 响应数据: ReturnCode (成功 = 0xFF)
                dataMs.WriteByte(0xFF);
            }

            byte[] paramBytes = paramMs.ToArray();
            byte[] dataBytes = dataMs.ToArray();

            return BuildS7AckData(s7Data, 0x05, paramBytes, dataBytes);
        }

        // ── 内存读写 ──────────────────────────────

        private byte[] ReadFromMemory(S7Area area, int dbNum, int byteAddr, int length)
        {
            byte[] data = new byte[length];
            lock (_memLock)
            {
                byte[]? source = area switch
                {
                    S7Area.DB => dbNum >= 0 && dbNum < 4096 ? _dbBlocks[dbNum] : null,
                    S7Area.PE => _inputs,
                    S7Area.PA => _outputs,
                    S7Area.MK => _markers,
                    S7Area.V => _vArea,
                    S7Area.CT => _counters,
                    S7Area.TM => _timers,
                    _ => null
                };

                if (source != null && byteAddr + length <= source.Length)
                {
                    Buffer.BlockCopy(source, byteAddr, data, 0, length);
                }
            }
            return data;
        }

        private void WriteToMemory(S7Area area, int dbNum, int byteAddr, byte[] data)
        {
            lock (_memLock)
            {
                byte[]? target = area switch
                {
                    S7Area.DB => dbNum >= 0 && dbNum < 4096 ? (_dbBlocks[dbNum] ?? (_dbBlocks[dbNum] = new byte[65536])) : null,
                    S7Area.PE => _inputs,
                    S7Area.PA => _outputs,
                    S7Area.MK => _markers,
                    S7Area.V => _vArea,
                    S7Area.CT => _counters,
                    S7Area.TM => _timers,
                    _ => null
                };

                if (target != null && byteAddr + data.Length <= target.Length)
                {
                    Buffer.BlockCopy(data, 0, target, byteAddr, data.Length);
                }
            }

            OnWriteReceived?.Invoke(this, new S7WriteEventArgs
            {
                Area = area,
                DbNumber = dbNum,
                ByteAddress = byteAddr,
                Data = (byte[])data.Clone(),
                Timestamp = DateTime.Now
            });
        }

        // ── S7 帧构建辅助 ─────────────────────────

        private byte[] BuildS7AckData(byte[] request, byte function, byte[] param, byte[] data)
        {
            int paramLen = param.Length;
            int dataLen = data.Length;
            // S7 Ack Data 头部 12 字节: Protocol(1)+MsgType(1)+Reserved(2)+PduRef(2)+ParamLen(2)+DataLen(2)+ErrorClass(1)+ErrorCode(1)
            byte[] s7 = new byte[12 + paramLen + dataLen];

            s7[0] = 0x32;   // Protocol ID
            s7[1] = 0x03;   // Msg Type: Ack Data
            s7[2] = request[2]; s7[3] = request[3];
            s7[4] = request[4]; s7[5] = request[5]; // PDU Reference echo
            s7[6] = (byte)(paramLen >> 8); s7[7] = (byte)paramLen;
            s7[8] = (byte)(dataLen >> 8); s7[9] = (byte)dataLen;
            s7[10] = 0x00;  // Error class (no error)
            s7[11] = 0x00;  // Error code (no error)
            if (paramLen > 0) Buffer.BlockCopy(param, 0, s7, 12, paramLen);
            if (dataLen > 0) Buffer.BlockCopy(data, 0, s7, 12 + paramLen, dataLen);

            return s7;
        }

        private byte[] BuildS7Error(byte function, byte errorCode)
        {
            return new byte[] {
                0x32, 0x03, // Ack Data
                0x00, 0x00, 0x00, 0x00, // Reserved + PDU Ref
                0x00, 0x02, // Param Len = 2
                0x00, 0x00, // Data Len = 0
                function, errorCode // Error class
            };
        }

        private byte[] BuildTPKTCOTPData(byte[] s7Pdu)
        {
            // COTP DT: Length(1) + Type(1) + TPU(1) = 3 bytes
            byte[] cotpDt = new byte[3 + s7Pdu.Length];
            cotpDt[0] = 0x02;   // Length
            cotpDt[1] = 0xF0;   // PDU Type: Data Transfer
            cotpDt[2] = 0x80;   // Last Data Unit
            Buffer.BlockCopy(s7Pdu, 0, cotpDt, 3, s7Pdu.Length);

            // TPKT
            int totalLen = 4 + cotpDt.Length;
            byte[] result = new byte[totalLen];
            result[0] = 0x03; result[1] = 0x00;
            result[2] = (byte)(totalLen >> 8); result[3] = (byte)totalLen;
            Buffer.BlockCopy(cotpDt, 0, result, 4, cotpDt.Length);
            return result;
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

    /// <summary>S7 写入事件参数。</summary>
    public class S7WriteEventArgs : EventArgs
    {
        public S7Area Area { get; set; }
        public int DbNumber { get; set; }
        public int ByteAddress { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }
}
