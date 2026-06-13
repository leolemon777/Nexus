using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Yaskawa
{
    /// <summary>
    /// Memobus 虚拟 PLC 服务器 — 模拟 YASKAWA Memobus TCP 协议，用于无硬件测试。
    /// 内存模型: 保持寄存器 4096 字 + 输入寄存器 1024 字 + 线圈 4096 位 + 输入位 1024 位。
    /// 支持命名区域: M(内部继电器) + G(数据寄存器) + I(输入) + O(输出) + S(系统)。
    /// </summary>
    public class MemobusVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly object _lock = new object();

        // 外层帧头长度
        private const int OuterHeader = 12;

        // 内存区域
        private readonly byte[] _holdingRegs = new byte[8192];   // 4096 字
        private readonly byte[] _inputRegs = new byte[2048];      // 1024 字
        private readonly byte[] _coils = new byte[512];           // 4096 位
        private readonly byte[] _inputBits = new byte[128];       // 1024 位

        // 命名区域
        private readonly byte[] _mArea = new byte[2048];  // M 内部继电器
        private readonly byte[] _gArea = new byte[8192];  // G 数据寄存器
        private readonly byte[] _iArea = new byte[2048];  // I 输入
        private readonly byte[] _oArea = new byte[2048];  // O 输出
        private readonly byte[] _sArea = new byte[2048];  // S 系统

        // 默认 CpuTo/CpuFrom
        public byte CpuTo { get; set; } = 2;
        public byte CpuFrom { get; set; } = 1;

        public int Port { get; }
        public bool IsRunning => _running;

        public MemobusVirtualServer(int port = 502)
        {
            Port = port;
        }

        #region 数据设置 API

        /// <summary>设置保持寄存器值（小端序）。</summary>
        public void SetHolding(ushort address, ushort value)
        {
            lock (_lock)
            {
                if (address * 2 + 1 < _holdingRegs.Length)
                {
                    _holdingRegs[address * 2] = (byte)(value & 0xFF);
                    _holdingRegs[address * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置输入寄存器值（小端序）。</summary>
        public void SetInputReg(ushort address, ushort value)
        {
            lock (_lock)
            {
                if (address * 2 + 1 < _inputRegs.Length)
                {
                    _inputRegs[address * 2] = (byte)(value & 0xFF);
                    _inputRegs[address * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置线圈位。</summary>
        public void SetCoil(int bitIndex, bool value)
        {
            lock (_lock)
            {
                if (bitIndex >= 0 && bitIndex < 4096)
                {
                    if (value)
                        _coils[bitIndex / 8] |= (byte)(1 << (bitIndex % 8));
                    else
                        _coils[bitIndex / 8] &= (byte)~(1 << (bitIndex % 8));
                }
            }
        }

        /// <summary>设置 M 区域字值。</summary>
        public void SetMWord(int wordIndex, ushort value)
        {
            lock (_lock)
            {
                if (wordIndex * 2 + 1 < _mArea.Length)
                {
                    _mArea[wordIndex * 2] = (byte)(value & 0xFF);
                    _mArea[wordIndex * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置 G 区域字值。</summary>
        public void SetGWord(int wordIndex, ushort value)
        {
            lock (_lock)
            {
                if (wordIndex * 2 + 1 < _gArea.Length)
                {
                    _gArea[wordIndex * 2] = (byte)(value & 0xFF);
                    _gArea[wordIndex * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        #endregion

        #region 服务器控制

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
                foreach (var c in _clients) try { c.Close(); } catch { }
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
                        // 读取外层帧头 (12 字节)
                        byte[]? outerHeader = ReadExact(stream, OuterHeader);
                        if (outerHeader == null) break;

                        // 验证帧标记
                        if (outerHeader[0] != 0x11) break;

                        // 总长度
                        int totalLen = outerHeader[6] | (outerHeader[7] << 8);
                        int remaining = totalLen - OuterHeader;
                        if (remaining <= 0) break;

                        // 读取内层命令
                        byte[]? innerCmd = ReadExact(stream, remaining);
                        if (innerCmd == null) break;

                        // 处理并返回响应
                        byte[]? innerResp = ProcessCommand(innerCmd);
                        if (innerResp != null)
                        {
                            // 构建外层响应帧
                            byte[] fullResp = new byte[OuterHeader + innerResp.Length];
                            fullResp[0] = 0x11;
                            fullResp[1] = outerHeader[1]; // 回传 connection id
                            int respTotalLen = fullResp.Length;
                            fullResp[6] = (byte)(respTotalLen & 0xFF);
                            fullResp[7] = (byte)((respTotalLen >> 8) & 0xFF);
                            Buffer.BlockCopy(innerResp, 0, fullResp, OuterHeader, innerResp.Length);

                            stream.Write(fullResp, 0, fullResp.Length);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                lock (_lock) { _clients.Remove(client); }
            }
        }

        #endregion

        #region 命令处理

        private byte[]? ProcessCommand(byte[] inner)
        {
            if (inner == null || inner.Length < 5) return null;

            byte mfc = inner[2];
            byte sfc = inner[3];

            // 标准功能码 (MFC=0x20)
            if (mfc == 0x20)
            {
                switch (sfc)
                {
                    case 1: return ProcessReadCoils(inner, _coils, bigEndian: true);
                    case 2: return ProcessReadCoils(inner, _inputBits, bigEndian: true);
                    case 3: return ProcessReadRegisters(inner, _holdingRegs, bigEndian: true);
                    case 4: return ProcessReadRegisters(inner, _inputRegs, bigEndian: true);
                    case 5: return ProcessWriteSingleCoil(inner);
                    case 6: return ProcessWriteSingleRegister(inner);
                    case 0x0F: return ProcessWriteMultiCoils(inner);
                    case 0x10: return ProcessWriteMultiRegisters(inner);
                    case 0x0D: return ProcessReadRandom(inner);
                    case 0x0E: return ProcessWriteRandom(inner);
                    default: return BuildErrorSfcResponse(sfc, 0x01);
                }
            }

            // 命名区域功能码 (MFC=0x43)
            if (mfc == 0x43)
            {
                switch (sfc)
                {
                    case 0x41: return ProcessNamedBitRead(inner);
                    case 0x49: return ProcessNamedWordRead(inner);
                    case 0x4B: return ProcessNamedWordWrite(inner);
                    case 0x4F: return ProcessNamedBitWrite(inner);
                    case 0x4D: return ProcessNamedRandomRead(inner);
                    default: return BuildErrorSfcResponse(sfc, 0x01);
                }
            }

            return BuildErrorSfcResponse(sfc, 0x01);
        }

        /// <summary>
        /// 标准寄存器读取 (SFC 03/04)。地址和数量为大端序。
        /// 响应: [payloadLen(2), MFC, SFC, cpuToFrom, byteCount(1), data...]
        /// </summary>
        private byte[] ProcessReadRegisters(byte[] inner, byte[] storage, bool bigEndian)
        {
            // 请求: [payloadLen(2), MFC, SFC, cpuToFrom, addrHi, addrLo, countHi, countLo]
            if (inner.Length < 9) return BuildErrorSfcResponse(inner[3], 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            ushort count = (ushort)((inner[7] << 8) | inner[8]);

            int byteCount = count * 2;
            byte[] data = new byte[byteCount];

            lock (_lock)
            {
                int srcOffset = address * 2;
                int copyLen = Math.Min(byteCount, storage.Length - srcOffset);
                if (copyLen > 0 && srcOffset < storage.Length)
                    Buffer.BlockCopy(storage, srcOffset, data, 0, copyLen);
            }

            // 响应: payloadLen(2) + MFC + SFC + cpuToFrom + byteCount(1) + data
            byte[] resp = new byte[5 + byteCount];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = inner[2]; // MFC
            resp[3] = inner[3]; // SFC
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            // byteCount 是数据字节数，对于标准读放在 byte[5]...
            // 实际 HSL 的 ExtraContent 对 SFC 03/04 用 RemoveBegin(5)
            // 所以响应是 [len(2) + MFC + SFC + cpu + byteCount(1)] + data
            // 但看 HSL 源码 content[2]=0x20, content[3]=03/04 时 RemoveBegin(5)
            // 所以前5字节是 [lenLo, lenHi, MFC, SFC, cpuToFrom]，然后 byteCount，然后 data
            // 不对，标准 Modbus 读响应: [len, MFC, SFC, cpuToFrom, byteCount(1), data...]
            // ExtraContent 做 RemoveBegin(5) 是去掉 [len(2) + MFC(1) + SFC(1) + cpuToFrom(1)]...
            // 但那样 data 前面还有 byteCount。再看一遍...
            // HSL: content.RemoveBegin(5) 对于 SFC 03/04
            // content 是内层响应 = [payloadLen(2), MFC, SFC, cpuToFrom, byteCount(1), data...]
            // RemoveBegin(5) = 去掉 [payloadLen(2) + MFC(1) + SFC(1) + cpuToFrom(1)] = [byteCount(1), data...]
            // 不对，RemoveBegin(5) 跳过前5字节...
            // [0]=lenLo, [1]=lenHi, [2]=MFC, [3]=SFC, [4]=cpuToFrom → 这是5字节
            // 然后 [5]=byteCount, [6+]=data
            // RemoveBegin(5) = [byteCount, data...]
            // 但 ParseResponse 返回的 data 包含 byteCount...
            //
            // 等一下，标准 Modbus 响应格式：
            // 实际上 Memobus 标准读响应:
            // [payloadLen(2), MFC, SFC, cpuToFrom, byteCount(1), data...]
            // ExtraContent RemoveBegin(5) 得到 [byteCount, data...]
            // 然后调用方应该跳过 byteCount... 但 HSL 的 Read 方法是：
            // var content = ExtraContent(address, read.Content); 直接返回这个
            // 如果返回 [byteCount, data...] 那数据偏移不对
            //
            // 让我重新看 HSL UnpackResponseContent:
            // response.RemoveBegin(12) — 这是去掉外层12字节头
            // 然后结果传给 ExtraContent
            // ExtraContent 收到的 content 是内层响应
            // 对于 SFC 03/04: content.RemoveBegin(5)
            //
            // 内层响应可能是: [payloadLen(2), MFC, SFC, cpuToFrom, data...]
            // 也就是没有 byteCount 字段？而是直接是数据？
            // RemoveBegin(5) 跳过 [payloadLen(2) + MFC + SFC + cpuToFrom] = 前5字节
            // 然后直接得到 data
            //
            // OK 所以标准读取响应没有 byteCount，直接是数据

            byte[] resp2 = new byte[5 + byteCount];
            resp2[0] = (byte)((resp2.Length - 2) & 0xFF);
            resp2[1] = (byte)(((resp2.Length - 2) >> 8) & 0xFF);
            resp2[2] = inner[2];
            resp2[3] = inner[3];
            resp2[4] = (byte)((CpuTo << 4) | CpuFrom);
            Buffer.BlockCopy(data, 0, resp2, 5, byteCount);
            return resp2;
        }

        /// <summary>
        /// 线圈读取 (SFC 01/02)。响应格式与寄存器读取相同。
        /// </summary>
        private byte[] ProcessReadCoils(byte[] inner, byte[] storage, bool bigEndian)
        {
            if (inner.Length < 9) return BuildErrorSfcResponse(inner[3], 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            ushort count = (ushort)((inner[7] << 8) | inner[8]);

            int byteCount = (count + 7) / 8;
            byte[] data = new byte[byteCount];

            lock (_lock)
            {
                int srcByte = address / 8;
                int srcBit = address % 8;

                for (int i = 0; i < count; i++)
                {
                    int srcIdx = srcByte + (srcBit + i) / 8;
                    int srcBitIdx = (srcBit + i) % 8;
                    if (srcIdx < storage.Length && (storage[srcIdx] & (1 << srcBitIdx)) != 0)
                        data[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            byte[] resp = new byte[5 + byteCount];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = inner[2];
            resp[3] = inner[3];
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            Buffer.BlockCopy(data, 0, resp, 5, byteCount);
            return resp;
        }

        /// <summary>
        /// 单线圈写入 (SFC=05)。
        /// </summary>
        private byte[] ProcessWriteSingleCoil(byte[] inner)
        {
            if (inner.Length < 9) return BuildErrorSfcResponse(0x05, 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            bool value = inner[7] == 0xFF;

            lock (_lock)
            {
                if (address / 8 < _coils.Length)
                {
                    if (value)
                        _coils[address / 8] |= (byte)(1 << (address % 8));
                    else
                        _coils[address / 8] &= (byte)~(1 << (address % 8));
                }
            }

            // 回显请求
            return BuildEchoResponse(inner);
        }

        /// <summary>
        /// 单寄存器写入 (SFC=06)。
        /// </summary>
        private byte[] ProcessWriteSingleRegister(byte[] inner)
        {
            if (inner.Length < 9) return BuildErrorSfcResponse(0x06, 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            ushort value = (ushort)((inner[7] << 8) | inner[8]);

            lock (_lock)
            {
                if (address * 2 + 1 < _holdingRegs.Length)
                {
                    _holdingRegs[address * 2] = (byte)(value & 0xFF);
                    _holdingRegs[address * 2 + 1] = (byte)((value >> 8) & 0xFF);
                }
            }

            return BuildEchoResponse(inner);
        }

        /// <summary>
        /// 多线圈写入 (SFC=0x0F)。
        /// </summary>
        private byte[] ProcessWriteMultiCoils(byte[] inner)
        {
            if (inner.Length < 9) return BuildErrorSfcResponse(0x0F, 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            ushort count = (ushort)((inner[7] << 8) | inner[8]);

            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int dataByteIdx = 9 + i / 8;
                    int dataBitIdx = i % 8;
                    if (dataByteIdx < inner.Length)
                    {
                        bool bitValue = (inner[dataByteIdx] & (1 << dataBitIdx)) != 0;
                        int targetIdx = address + i;
                        if (targetIdx / 8 < _coils.Length)
                        {
                            if (bitValue)
                                _coils[targetIdx / 8] |= (byte)(1 << (targetIdx % 8));
                            else
                                _coils[targetIdx / 8] &= (byte)~(1 << (targetIdx % 8));
                        }
                    }
                }
            }

            return BuildEchoResponse(inner);
        }

        /// <summary>
        /// 多寄存器写入 (SFC=0x10)。
        /// </summary>
        private byte[] ProcessWriteMultiRegisters(byte[] inner)
        {
            if (inner.Length < 9) return BuildErrorSfcResponse(0x10, 0x02);

            ushort address = (ushort)((inner[5] << 8) | inner[6]);
            ushort wordCount = (ushort)((inner[7] << 8) | inner[8]);
            int dataLen = wordCount * 2;

            if (inner.Length < 9 + dataLen) return BuildErrorSfcResponse(0x10, 0x03);

            lock (_lock)
            {
                int dstOffset = address * 2;
                int copyLen = Math.Min(dataLen, _holdingRegs.Length - dstOffset);
                if (copyLen > 0 && dstOffset < _holdingRegs.Length)
                    Buffer.BlockCopy(inner, 9, _holdingRegs, dstOffset, copyLen);
            }

            return BuildEchoResponse(inner);
        }

        /// <summary>
        /// 随机读取 (SFC=0x0D)。
        /// </summary>
        private byte[] ProcessReadRandom(byte[] inner)
        {
            if (inner.Length < 8) return BuildErrorSfcResponse(0x0D, 0x02);

            int addrCount = inner[6] | (inner[7] << 8);
            byte[] data = new byte[addrCount * 2];

            lock (_lock)
            {
                for (int i = 0; i < addrCount; i++)
                {
                    int offset = 8 + i * 2;
                    if (offset + 1 >= inner.Length) break;
                    ushort addr = (ushort)(inner[offset] | (inner[offset + 1] << 8));
                    int srcOffset = addr * 2;
                    if (srcOffset + 1 < _holdingRegs.Length)
                    {
                        data[i * 2] = _holdingRegs[srcOffset];
                        data[i * 2 + 1] = _holdingRegs[srcOffset + 1];
                    }
                }
            }

            // 随机读取响应需要 word-reverse
            byte[] reversed = MemobusClient.ReverseWords(data);

            // 响应: [payloadLen(2), MFC, SFC, cpuToFrom, ?, ?, count(2), data...]
            // ExtraContent 对 SFC 0x0D: RemoveBegin(8) + word reverse
            // 但我们已经在上面 reverse 了... HSL 的 ExtraContent 又做了一次 reverse
            // 让我再看：
            // 标准随机读响应: [len(2), MFC, SFC, cpuToFrom, ?, ?, countLo, countHi, data...]
            // 客户端 ExtractPayload 对 SFC 0x0D: inner.Length-8 跳过8字节头, ReverseWords
            // 所以服务器返回的数据应该是原始顺序，客户端做 reverse
            // 不对，服务器返回 word-reversed 数据，客户端又 reverse 一次... 这取决于实际协议
            //
            // 看 HSL ReadRandom:
            // BuildReadRandomCommand → 发送
            // ReadFromCoreServer → 接收响应
            // UnpackResponseContent → 去掉外层12字节头
            // SoftBasic.BytesReverseByWord(content.RemoveBegin(8)) → 去掉8字节头后 word reverse
            //
            // 所以服务器返回的是未 reverse 的数据，客户端接收后 word reverse
            //
            // 但标准保持寄存器存储是小端序（LE），而 Memobus 协议标准读响应也是 LE
            // 对于扩展读(SFC 09/10)，响应数据是 word-reversed 的
            // 随机读(SFC 0x0D)的响应数据也是 word-reversed 的（HSL 做了 BytesReverseByWord）
            //
            // 等等，这意味着服务器返回的就是 word-swapped 的？
            // 让我假设服务器直接返回存储数据（小端），客户端做 word reverse
            // 这样 ReverseWords(data) 应该是原始小端数据
            // 然后客户端 ExtractPayload 会 ReverseWords，把 [lo,hi] 变成 [hi,lo]
            // 但最终 ReadInt16 期望小端序数据 [lo, hi]...
            //
            // 仔细想：
            // - 服务器存储: holding[0]=0x34, holding[1]=0x12 → 值 0x1234 (LE)
            // - 标准读(SFC 03): 服务器发送 [0x34, 0x12]，客户端 ExtractPayload 不 reverse，得到 [0x34, 0x12]
            //   ReadInt16: 0x34 | (0x12 << 8) = 0x1234 ✓
            // - 随机读(SFC 0D): 服务器发送 [0x34, 0x12]，客户端 ReverseWords 得到 [0x12, 0x34]
            //   ReadInt16: 0x12 | (0x34 << 8) = 0x3412 ✗
            //
            // 这说明随机读的服务器响应数据应该是 word-swapped 的！
            // 服务器发送 [0x12, 0x34]（即 [hi, lo]），客户端 ReverseWords 得到 [0x34, 0x12]（[lo, hi]）
            // ReadInt16: 0x34 | (0x12 << 8) = 0x1234 ✓
            //
            // 所以随机读服务器响应需要 word swap

            // 实际上重新看: 客户端 ExtractPayload 对随机读做了 ReverseWords
            // 服务器应该发送 CDAB 格式数据
            // 存储是 LE (AB) → 服务器需要转成 CDAB 再发送
            // CDAB 就是每个 word 内字节交换: [lo,hi] → [hi,lo]
            // 然后 客户端 ReverseWords([hi,lo]) → [lo,hi] → 正确

            byte[] resp = new byte[8 + data.Length];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = inner[2]; // MFC
            resp[3] = inner[3]; // SFC
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            // bytes[5-7] - 在 RemoveBegin(8) 的范围内，客户端会跳过
            resp[6] = (byte)(addrCount & 0xFF);
            resp[7] = (byte)((addrCount >> 8) & 0xFF);
            // data 已是 word-reversed
            Buffer.BlockCopy(reversed, 0, resp, 8, reversed.Length);
            return resp;
        }

        /// <summary>
        /// 随机写入 (SFC=0x0E)。
        /// </summary>
        private byte[] ProcessWriteRandom(byte[] inner)
        {
            if (inner.Length < 8) return BuildErrorSfcResponse(0x0E, 0x02);

            int addrCount = inner[6] | (inner[7] << 8);

            lock (_lock)
            {
                for (int i = 0; i < addrCount; i++)
                {
                    int offset = 8 + i * 4;
                    if (offset + 3 >= inner.Length) break;

                    ushort addr = (ushort)(inner[offset] | (inner[offset + 1] << 8));
                    // value is word-swapped in command: [addrLo, addrHi, valHi, valLo]
                    byte valLo = inner[offset + 3];
                    byte valHi = inner[offset + 2];

                    int dstOffset = addr * 2;
                    if (dstOffset + 1 < _holdingRegs.Length)
                    {
                        _holdingRegs[dstOffset] = valLo;
                        _holdingRegs[dstOffset + 1] = valHi;
                    }
                }
            }

            return BuildEchoResponse(inner);
        }

        /// <summary>
        /// 命名区域字读取 (SFC=0x49)。
        /// </summary>
        private byte[] ProcessNamedWordRead(byte[] inner)
        {
            if (inner.Length < 14) return BuildErrorSfcResponse(0x49, 0x02);

            byte dataType = inner[6];
            uint address = (uint)(inner[8] | (inner[9] << 8) | (inner[10] << 16) | (inner[11] << 24));
            ushort count = (ushort)(inner[12] | (inner[13] << 8));

            byte[]? storage = ResolveNamedStorage(dataType);
            if (storage == null) return BuildErrorSfcResponse(0x49, 0x02);

            int byteCount = count * 2;
            byte[] data = new byte[byteCount];

            lock (_lock)
            {
                int srcOffset = (int)address * 2;
                int copyLen = Math.Min(byteCount, storage.Length - srcOffset);
                if (copyLen > 0 && srcOffset < storage.Length)
                    Buffer.BlockCopy(storage, srcOffset, data, 0, copyLen);
            }

            // 命名字读取响应: 10字节头 + word-reversed data
            byte[] reversed = MemobusClient.ReverseWords(data);

            byte[] resp = new byte[10 + byteCount];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = inner[2]; // MFC=0x43
            resp[3] = inner[3]; // SFC=0x49
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            Buffer.BlockCopy(reversed, 0, resp, 10, reversed.Length);
            return resp;
        }

        /// <summary>
        /// 命名区域位读取 (SFC=0x41)。
        /// </summary>
        private byte[] ProcessNamedBitRead(byte[] inner)
        {
            if (inner.Length < 16) return BuildErrorSfcResponse(0x41, 0x02);

            byte dataType = inner[6];
            int boolIndex = inner[8] | (inner[9] << 8) | (inner[10] << 16) | (inner[11] << 24);
            ushort count = (ushort)(inner[12] | (inner[13] << 8));

            byte[]? storage = ResolveNamedStorage(dataType);
            if (storage == null) return BuildErrorSfcResponse(0x41, 0x02);

            int byteCount = (count + 7) / 8;
            byte[] data = new byte[byteCount];

            lock (_lock)
            {
                int startByte = boolIndex / 8;
                int startBit = boolIndex % 8;
                for (int i = 0; i < count; i++)
                {
                    int srcIdx = startByte + (startBit + i) / 8;
                    int srcBitIdx = (startBit + i) % 8;
                    if (srcIdx < storage.Length && (storage[srcIdx] & (1 << srcBitIdx)) != 0)
                        data[i / 8] |= (byte)(1 << (i % 8));
                }
            }

            // 命名位读取响应: 8字节头 + data (no word reverse)
            byte[] resp = new byte[8 + byteCount];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = inner[2];
            resp[3] = inner[3];
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            Buffer.BlockCopy(data, 0, resp, 8, byteCount);
            return resp;
        }

        /// <summary>
        /// 命名区域字写入 (SFC=0x4B)。
        /// </summary>
        private byte[] ProcessNamedWordWrite(byte[] inner)
        {
            if (inner.Length < 14) return BuildErrorSfcResponse(0x4B, 0x02);

            byte dataType = inner[6];
            uint address = (uint)(inner[8] | (inner[9] << 8) | (inner[10] << 16) | (inner[11] << 24));
            ushort wordCount = (ushort)(inner[12] | (inner[13] << 8));

            byte[]? storage = ResolveNamedStorage(dataType);
            if (storage == null) return BuildErrorSfcResponse(0x4B, 0x02);

            int dataLen = wordCount * 2;
            if (inner.Length < 14 + dataLen) return BuildErrorSfcResponse(0x4B, 0x03);

            // 命名写入数据是 word-reversed 的，需要 un-reverse
            byte[] reversed = new byte[dataLen];
            Buffer.BlockCopy(inner, 14, reversed, 0, dataLen);
            byte[] data = MemobusClient.ReverseWords(reversed);

            lock (_lock)
            {
                int dstOffset = (int)address * 2;
                int copyLen = Math.Min(dataLen, storage.Length - dstOffset);
                if (copyLen > 0 && dstOffset < storage.Length)
                    Buffer.BlockCopy(data, 0, storage, dstOffset, copyLen);
            }

            return BuildSuccessResponse(inner[3]);
        }

        /// <summary>
        /// 命名区域位写入 (SFC=0x4F)。
        /// </summary>
        private byte[] ProcessNamedBitWrite(byte[] inner)
        {
            if (inner.Length < 16) return BuildErrorSfcResponse(0x4F, 0x02);

            byte dataType = inner[6];
            int boolIndex = inner[8] | (inner[9] << 8) | (inner[10] << 16) | (inner[11] << 24);
            ushort count = (ushort)(inner[12] | (inner[13] << 8));

            byte[]? storage = ResolveNamedStorage(dataType);
            if (storage == null) return BuildErrorSfcResponse(0x4F, 0x02);

            int dataStart = 16;
            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int dataByteIdx = dataStart + i / 8;
                    int dataBitIdx = i % 8;
                    if (dataByteIdx < inner.Length)
                    {
                        bool bitValue = (inner[dataByteIdx] & (1 << dataBitIdx)) != 0;
                        int targetIdx = boolIndex + i;
                        if (targetIdx / 8 < storage.Length)
                        {
                            if (bitValue)
                                storage[targetIdx / 8] |= (byte)(1 << (targetIdx % 8));
                            else
                                storage[targetIdx / 8] &= (byte)~(1 << (targetIdx % 8));
                        }
                    }
                }
            }

            return BuildSuccessResponse(inner[3]);
        }

        /// <summary>
        /// 命名区域随机读取 (SFC=0x4D)。
        /// </summary>
        private byte[] ProcessNamedRandomRead(byte[] inner)
        {
            if (inner.Length < 8) return BuildErrorSfcResponse(0x4D, 0x02);

            int count = inner[6] | (inner[7] << 8);
            // 每个地址: dataType(1) + wordCount(1) + address(4) = 6 bytes
            byte[] data = new byte[count * 2]; // 每个 1 word = 2 bytes

            lock (_lock)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = 8 + i * 6;
                    if (offset + 5 >= inner.Length) break;

                    byte dataType = inner[offset];
                    // wordCount = inner[offset + 1], 固定为 2 (1 word)
                    uint addr = (uint)(inner[offset + 2] | (inner[offset + 3] << 8) |
                                       (inner[offset + 4] << 16) | (inner[offset + 5] << 24));

                    byte[]? storage = ResolveNamedStorage(dataType);
                    if (storage == null) continue;

                    int srcOffset = (int)addr * 2;
                    if (srcOffset + 1 < storage.Length)
                    {
                        data[i * 2] = storage[srcOffset];
                        data[i * 2 + 1] = storage[srcOffset + 1];
                    }
                }
            }

            // word-reversed
            byte[] reversed = MemobusClient.ReverseWords(data);

            byte[] resp = new byte[10 + data.Length];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = 0x43; // MFC
            resp[3] = 0x4D; // SFC
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            Buffer.BlockCopy(reversed, 0, resp, 10, reversed.Length);
            return resp;
        }

        #endregion

        #region 响应构建

        private byte[] BuildEchoResponse(byte[] inner)
        {
            // 写入成功: 回传请求（截取与响应相同长度）
            byte[] resp = new byte[Math.Min(inner.Length, 8)];
            Buffer.BlockCopy(inner, 0, resp, 0, resp.Length);
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            return resp;
        }

        private byte[] BuildSuccessResponse(byte sfc)
        {
            byte[] resp = new byte[5];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = 0x20; // MFC
            resp[3] = sfc;
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            return resp;
        }

        /// <summary>构建 SFC 错误响应（SFC + 0x80）。</summary>
        private byte[] BuildErrorSfcResponse(byte sfc, byte errorCode)
        {
            byte[] resp = new byte[6];
            resp[0] = (byte)((resp.Length - 2) & 0xFF);
            resp[1] = (byte)(((resp.Length - 2) >> 8) & 0xFF);
            resp[2] = 0x20; // MFC
            resp[3] = (byte)(sfc + 0x80); // error response SFC
            resp[4] = (byte)((CpuTo << 4) | CpuFrom);
            resp[5] = errorCode;
            return resp;
        }

        #endregion

        #region 存储解析

        private byte[]? ResolveNamedStorage(byte dataType)
        {
            switch (dataType)
            {
                case 77:  // 'M'
                case 109: return _mArea;
                case 71:  // 'G'
                case 103: return _gArea;
                case 73:  // 'I'
                case 105: return _iArea;
                case 79:  // 'O'
                case 111: return _oArea;
                case 83:  // 'S'
                case 115: return _sArea;
                default: return null;
            }
        }

        #endregion

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
