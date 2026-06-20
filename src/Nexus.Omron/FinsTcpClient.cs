using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 FINS-TCP 协议客户端 — 支持 CIO/DM/WR/HR/AR/EM 等内存区域的读写。
    /// <para>FINS over TCP 帧结构:</para>
    /// <para>  帧头: FrameLength(4, big-endian) + CommandCode(2) + ...</para>
    /// <para>  连接握手: 交换客户端/服务端节点号</para>
    /// <para>支持数据类型: Bit, Int16, UInt16, Int32, UInt32, Int64, Float, Double, String, Bytes</para>
    /// </summary>
    public class FinsTcpClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        /// <summary>服务端 FINS 节点号。</summary>
        public byte ServerNode { get; private set; }

        /// <summary>客户端 FINS 节点号（连接握手时分配）。</summary>
        public byte ClientNode { get; private set; }

        /// <summary>源网络地址。</summary>
        public byte SNA { get; set; } = 0x00;

        /// <summary>源节点号。</summary>
        public byte SA1 { get; set; } = 0x00;

        /// <summary>源单元地址。</summary>
        public byte SA2 { get; set; } = 0x00;

        /// <summary>目标网络地址。</summary>
        public byte DNA { get; set; } = 0x00;

        /// <summary>目标单元地址（CPU = 0x00）。</summary>
        public byte DA2 { get; set; } = 0x00;

        /// <summary>多寄存器值的字节序（默认大端）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        /// <summary>字符串编码选项（默认 ASCII）。</summary>
        public FinsStringEncoding StringEncoding { get; set; } = FinsStringEncoding.Ascii;

        /// <summary>服务 ID（递增计数器）。</summary>
        private int _sid;

        /// <summary>地址解析器。</summary>
        private readonly FinsAddressParser _addressParser = new FinsAddressParser();

        /// <summary>
        /// 创建 FINS-TCP 客户端。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">FINS-TCP 端口（默认 9600）。</param>
        /// <param name="timeout">超时毫秒。</param>
        public FinsTcpClient(string ip, int port = 9600, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        // ── FINS 响应头解析 ──────────────────────────
        // FINS TCP 帧: FrameLength(4) + Payload(N)。基类收包先读 4 字节长度头。

        protected override int ResponseHeaderLength => 4;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            // header[0..3] = FrameLength (big-endian), 包含自身 4 字节
            int totalFrameLen = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            return totalFrameLen - 4; // 减去 FrameLength 本身
        }

        // ── 连接握手 ──────────────────────────────

        public override OperateResult Connect()
        {
            var conn = base.Connect();
            if (!conn.IsSuccess) return conn;

            var handshakeResult = FinsHandshake();
            if (!handshakeResult.IsSuccess)
            {
                Disconnect();
                return handshakeResult;
            }

            return OperateResult.Success();
        }

        public override async Task<OperateResult> ConnectAsync()
        {
            var conn = await base.ConnectAsync().ConfigureAwait(false);
            if (!conn.IsSuccess) return conn;

            var handshakeResult = FinsHandshake();
            if (!handshakeResult.IsSuccess)
            {
                Disconnect();
                return handshakeResult;
            }

            return OperateResult.Success();
        }

        private OperateResult FinsHandshake()
        {
            lock (_lock)
            {
                var ns = _stream;
                if (ns == null) return OperateResult.Failed("连接已断开");

                byte[] ipBytes = System.Net.IPAddress.Parse(Ip).GetAddressBytes();
                byte[] request = new byte[12];
                request[0] = 0x00; request[1] = 0x00; request[2] = 0x00; request[3] = 0x0C;
                request[4] = 0x00; request[5] = 0x00; request[6] = 0x00; request[7] = 0x00;
                Buffer.BlockCopy(ipBytes, 0, request, 8, 4);

                Log.Debug($"FINS Handshake TX → {DataConverter.ToHexString(request)}");
                ns.Write(request, 0, request.Length);

                byte[] lenBuf = new byte[4];
                int lenRead = 0;
                while (lenRead < 4)
                {
                    int read = ns.Read(lenBuf, lenRead, 4 - lenRead);
                    if (read == 0) return OperateResult.Failed("FINS 握手: 读取响应长度失败");
                    lenRead += read;
                }

                int respFrameLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                int respPayloadLen = respFrameLen - 4;
                if (respPayloadLen < 4 || respPayloadLen > 65536)
                    return OperateResult.Failed("FINS 握手: 响应长度异常");

                byte[] respPayload = new byte[respPayloadLen];
                int payloadRead = 0;
                while (payloadRead < respPayloadLen)
                {
                    int read = ns.Read(respPayload, payloadRead, respPayloadLen - payloadRead);
                    if (read == 0) return OperateResult.Failed("FINS 握手: 读取响应数据失败");
                    payloadRead += read;
                }

                byte[] fullResponse = new byte[respFrameLen];
                Buffer.BlockCopy(lenBuf, 0, fullResponse, 0, 4);
                Buffer.BlockCopy(respPayload, 0, fullResponse, 4, respPayloadLen);

                Log.Debug($"FINS Handshake RX ← {DataConverter.ToHexString(fullResponse)}");

                if (fullResponse.Length < 12)
                    return OperateResult.Failed("FINS 握手: 响应数据不足");

                ServerNode = fullResponse[8];
                ClientNode = fullResponse[10];

                Log.Info($"FINS 握手成功 — ServerNode={ServerNode}, ClientNode={ClientNode}");
                return OperateResult.Success();
            }
        }

        // ── FINS 帧收发 ────────────────────────────

        private byte[] BuildFinsFrame(ushort commandCode, byte[] commandData)
        {
            byte sid = (byte)(Interlocked.Increment(ref _sid) & 0xFF);

            byte[] finsHeader = new byte[10];
            finsHeader[0] = 0x80;
            finsHeader[1] = 0x00;
            finsHeader[2] = 0x02;
            finsHeader[3] = DNA;
            finsHeader[4] = ServerNode;
            finsHeader[5] = DA2;
            finsHeader[6] = SNA;
            finsHeader[7] = ClientNode;
            finsHeader[8] = SA2;
            finsHeader[9] = sid;

            byte[] cmdBytes = new byte[] { (byte)(commandCode >> 8), (byte)(commandCode & 0xFF) };
            int payloadLen = 10 + 2 + commandData.Length;

            byte[] frame = new byte[4 + payloadLen];
            frame[0] = (byte)((payloadLen + 4) >> 24);
            frame[1] = (byte)((payloadLen + 4) >> 16);
            frame[2] = (byte)((payloadLen + 4) >> 8);
            frame[3] = (byte)(payloadLen + 4);

            Buffer.BlockCopy(finsHeader, 0, frame, 4, 10);
            Buffer.BlockCopy(cmdBytes, 0, frame, 14, 2);
            if (commandData.Length > 0)
                Buffer.BlockCopy(commandData, 0, frame, 16, commandData.Length);

            return frame;
        }

        /// <summary>默认心跳：读取 DM0 的 1 个 word。</summary>
        protected override byte[]? BuildHeartbeat()
        {
            byte[] commandData = { (byte)FinsMemoryArea.DM, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01 };
            return BuildFinsFrame(FinsCommandCode.MemoryAreaRead, commandData);
        }

        private OperateResult<byte[]> SendFinsCommand(ushort commandCode, byte[] commandData)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream ns;
                lock (_lock) { ns = _stream!; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                byte[] frame = BuildFinsFrame(commandCode, commandData);

                Log.Debug($"TX → {DataConverter.ToHexString(frame)}");
                RaiseMessageSent(DataConverter.ToHexString(frame));

                ns.Write(frame, 0, frame.Length);

                byte[]? lenBuf = ReadExactNs(ns, 4);
                if (lenBuf == null) return OperateResult<byte[]>.Failed("读取 FINS 响应长度失败");

                int respTotalLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
                int respPayloadLen = respTotalLen - 4;
                if (respPayloadLen < 0 || respPayloadLen > 65536)
                    return OperateResult<byte[]>.Failed("FINS 响应长度异常");

                byte[] respPayload = respPayloadLen > 0 ? ReadExactNs(ns, respPayloadLen) ?? Array.Empty<byte>() : Array.Empty<byte>();

                byte[] full = new byte[respTotalLen];
                Buffer.BlockCopy(lenBuf, 0, full, 0, 4);
                if (respPayload.Length > 0)
                    Buffer.BlockCopy(respPayload, 0, full, 4, respPayload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                RaiseMessageReceived(DataConverter.ToHexString(full));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                if (full.Length < 18)
                    return OperateResult<byte[]>.Failed("FINS 响应帧不完整");

                ushort endCode = (ushort)((full[16] << 8) | full[17]);
                if (endCode != 0x0000)
                {
                    return OperateResult<byte[]>.Failed(
                        $"FINS 错误: {FinsEndCode.ToMessage(endCode)} (0x{endCode:X4})",
                        (int)endCode);
                }

                byte[] result = new byte[full.Length - 4];
                Buffer.BlockCopy(full, 4, result, 0, result.Length);
                return OperateResult<byte[]>.Success(result);
            }
            catch (Exception ex)
            {
                Log.Error($"FINS 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"FINS 通讯异常: {ex.Message}");
            }
        }

        private static byte[]? ReadExactNs(NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        // ── 内存区域读取 ──────────────────────────

        private OperateResult<byte[]> ReadMemoryArea(FinsAddress addr, ushort readLength)
        {
            byte[] cmdDataFull = new byte[7];
            cmdDataFull[0] = (byte)addr.Area;

            if (addr.Area == FinsMemoryArea.EM)
            {
                // B4 修复：FINS EM 区域地址字段为 3 字节 bank(1)+address(2,big-endian)，
                // 原代码 bank 位置错（[2]应为[1]）且地址高字节丢弃（[3]只放低字节）。
                cmdDataFull[1] = addr.EmBank;
                cmdDataFull[2] = (byte)(addr.WordAddress >> 8);
                cmdDataFull[3] = (byte)(addr.WordAddress & 0xFF);
            }
            else
            {
                cmdDataFull[1] = (byte)(addr.BitOffset >= 0 ? 0x01 : 0x00);
                cmdDataFull[2] = (byte)(addr.WordAddress >> 8);
                cmdDataFull[3] = (byte)(addr.WordAddress & 0xFF);
            }

            cmdDataFull[4] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmdDataFull[5] = (byte)(readLength >> 8);
            cmdDataFull[6] = (byte)(readLength & 0xFF);

            return SendFinsCommand(FinsCommandCode.MemoryAreaRead, cmdDataFull);
        }

        // ── 字节序转换 ──────────────────────────────

        private short ToInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset)
        };

        private ushort ToUInt16Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.LittleEndian => (ushort)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToUInt16(data, offset)
        };

        private int ToInt32Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt32(data, offset),
            Endianness.LittleEndian => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24),
            Endianness.MidBigEndian => (data[offset + 1] << 24) | (data[offset] << 16) | (data[offset + 3] << 8) | data[offset + 2],
            Endianness.MidLittleEndian => (data[offset + 2] << 24) | (data[offset + 3] << 16) | (data[offset] << 8) | data[offset + 1],
            _ => DataConverter.ToInt32(data, offset)
        };

        private long ToInt64Ordered(byte[] data, int offset) => ByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt64(data, offset),
            Endianness.LittleEndian => (long)(
                (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24)) |
                ((long)(uint)(data[offset + 4] | (data[offset + 5] << 8) | (data[offset + 6] << 16) | (data[offset + 7] << 24)) << 32)),
            _ => DataConverter.ToInt64(data, offset)
        };

        private float ToFloatOrdered(byte[] data, int offset)
        {
            int v = ToInt32Ordered(data, offset);
            unsafe { return *(float*)&v; }
        }

        private double ToDoubleOrdered(byte[] data, int offset)
        {
            long v = ToInt64Ordered(data, offset);
            unsafe { return *(double*)&v; }
        }

        private byte[] GetBytesOrdered(short value) => ByteOrder == Endianness.LittleEndian
            ? new byte[] { (byte)value, (byte)(value >> 8) }
            : DataConverter.GetBytes(value);

        private byte[] GetBytesOrdered(ushort value) => GetBytesOrdered((short)value);

        private byte[] GetBytesOrdered(int value)
        {
            if (ByteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (ByteOrder == Endianness.LittleEndian) return new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
            if (ByteOrder == Endianness.MidBigEndian) return new byte[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };
            return new byte[] { (byte)(value >> 8), (byte)value, (byte)(value >> 24), (byte)(value >> 16) };
        }

        private byte[] GetBytesOrdered(float value)
        {
            unsafe { int v = *(int*)&value; return GetBytesOrdered(v); }
        }

        private byte[] GetBytesOrdered(long value)
        {
            if (ByteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (ByteOrder == Endianness.LittleEndian)
            {
                return new byte[] {
                    (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24),
                    (byte)(value >> 32), (byte)(value >> 40), (byte)(value >> 48), (byte)(value >> 56)
                };
            }
            return DataConverter.GetBytes(value);
        }

        private byte[] GetBytesOrdered(double value)
        {
            unsafe { long v = *(long*)&value; return GetBytesOrdered(v); }
        }

        // ── IReadWriteDevice 实现 ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult<bool>.Failed("读取 Bool 需要指定位偏移，例如 D100.03");

            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 15)
                return OperateResult<bool>.Failed("FINS Bool 响应数据不足");

            byte data = result.Content[14];
            return OperateResult<bool>.Success((data & (1 << addr.BitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 16)
                return OperateResult<short>.Failed("FINS Int16 响应数据不足");

            return OperateResult<short>.Success(ToInt16Ordered(result.Content, 14));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 1);
            if (!result.IsSuccess) return OperateResult<ushort>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 16)
                return OperateResult<ushort>.Failed("FINS UInt16 响应数据不足");

            return OperateResult<ushort>.Success(ToUInt16Ordered(result.Content, 14));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 2);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 18)
                return OperateResult<int>.Failed("FINS Int32 响应数据不足");

            return OperateResult<int>.Success(ToInt32Ordered(result.Content, 14));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 4);
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 22)
                return OperateResult<long>.Failed("FINS Int64 响应数据不足");

            return OperateResult<long>.Success(ToInt64Ordered(result.Content, 14));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 2);
            if (!result.IsSuccess) return OperateResult<float>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 18)
                return OperateResult<float>.Failed("FINS Float 响应数据不足");

            return OperateResult<float>.Success(ToFloatOrdered(result.Content, 14));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var addr = _addressParser.Parse(address);
            var result = ReadMemoryArea(addr, 4);
            if (!result.IsSuccess) return OperateResult<double>.Failed(result.Message, result.ErrorCode);

            if (result.Content.Length < 22)
                return OperateResult<double>.Failed("FINS Double 响应数据不足");

            return OperateResult<double>.Success(ToDoubleOrdered(result.Content, 14));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<string>.Failed("FINS String 响应数据不足");

            return OperateResult<string>.Success(DataConverter.ToString(result.Content, 14, Math.Min(length, available)));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<byte[]>.Failed("FINS Bytes 响应数据不足");

            byte[] data = new byte[Math.Min(length, available)];
            Buffer.BlockCopy(result.Content, 14, data, 0, data.Length);
            return OperateResult<byte[]>.Success(data);
        }

        // ── 字符串编码读写 ────────────────────────

        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadMemoryArea(addr, wordCount);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available <= 0)
                return OperateResult<string>.Failed("FINS String 响应数据不足");

            int byteLen = Math.Min(length, available);
            string text = StringEncoding switch
            {
                FinsStringEncoding.Utf8 => Encoding.UTF8.GetString(result.Content, 14, byteLen).TrimEnd('\0', ' '),
                FinsStringEncoding.Unicode => Encoding.Unicode.GetString(result.Content, 14, byteLen).TrimEnd('\0', ' '),
                _ => DataConverter.ToString(result.Content, 14, byteLen)
            };
            return OperateResult<string>.Success(text);
        }

        public OperateResult WriteStringEncoded(string address, string value)
        {
            var addr = _addressParser.Parse(address);
            byte[] data = StringEncoding switch
            {
                FinsStringEncoding.Utf8 => Encoding.UTF8.GetBytes(value),
                FinsStringEncoding.Unicode => Encoding.Unicode.GetBytes(value),
                _ => DataConverter.GetBytes(value)
            };
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        // ── 写入实现 ──────────────────────────────

        private OperateResult WriteMemoryArea(FinsAddress addr, ushort wordCount, byte[] data)
        {
            byte[] cmdData = new byte[7 + data.Length];
            cmdData[0] = (byte)addr.Area;

            if (addr.Area == FinsMemoryArea.EM)
            {
                // B4 修复：EM 地址字段 bank(1)+address(2,big-endian)。
                cmdData[1] = addr.EmBank;
                cmdData[2] = (byte)(addr.WordAddress >> 8);
                cmdData[3] = (byte)(addr.WordAddress & 0xFF);
            }
            else
            {
                cmdData[1] = (byte)(addr.BitOffset >= 0 ? 0x01 : 0x00);
                cmdData[2] = (byte)(addr.WordAddress >> 8);
                cmdData[3] = (byte)(addr.WordAddress & 0xFF);
            }

            cmdData[4] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmdData[5] = (byte)(wordCount >> 8);
            cmdData[6] = (byte)(wordCount & 0xFF);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, cmdData, 7, data.Length);

            var result = SendFinsCommand(FinsCommandCode.MemoryAreaWrite, cmdData);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult.Failed("写入 Bool 需要指定位偏移，例如 D100.03");

            var readResult = ReadMemoryArea(new FinsAddress(addr.Original, addr.Area, addr.WordAddress, -1), 1);
            if (!readResult.IsSuccess) return OperateResult.Failed("写入 Bool 前读取失败: " + readResult.Message);

            byte currentWord = readResult.Content.Length > 15 ? readResult.Content[14] : (byte)0;
            if (value)
                currentWord |= (byte)(1 << addr.BitOffset);
            else
                currentWord &= (byte)~(1 << addr.BitOffset);

            var writeAddr = new FinsAddress(addr.Original, addr.Area, addr.WordAddress, -1);
            return WriteMemoryArea(writeAddr, 1, new byte[] { 0x00, currentWord });
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 1, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 1, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, int value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 4, GetBytesOrdered(value));
        }
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value)
        {
            var addr = _addressParser.Parse(address);
            return WriteMemoryArea(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var addr = _addressParser.Parse(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0)
                Array.Resize(ref data, data.Length + 1);
            return WriteMemoryArea(addr, wordCount, data);
        }

        // ═══════════════════════════════════════════
        //  Bool 数组读写
        // ═══════════════════════════════════════════

        /// <summary>
        /// 批量读取连续位 — 读取指定地址开始的多个位。
        /// <para>内部读取包含这些位的字（word），然后逐位提取。</para>
        /// </summary>
        /// <param name="address">起始地址，需包含位偏移，如 "CIO0.00" 或 "D100.03"。</param>
        /// <param name="count">读取位数。</param>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult<bool[]>.Failed("读取 Bool 数组需要指定位偏移，例如 D100.03");

            // 计算需要读取的字范围
            int startBit = addr.BitOffset;
            int endBit = startBit + count - 1;
            int startWord = addr.WordAddress + (startBit / 16);
            int endWord = addr.WordAddress + (endBit / 16);
            ushort wordCount = (ushort)(endWord - startWord + 1);

            // 读取这些字
            var readAddr = new FinsAddress(addr.Original, addr.Area, (ushort)startWord, -1, addr.EmBank);
            var result = ReadMemoryArea(readAddr, wordCount);
            if (!result.IsSuccess) return OperateResult<bool[]>.Failed(result.Message, result.ErrorCode);

            int available = result.Content.Length - 14;
            if (available < wordCount * 2)
                return OperateResult<bool[]>.Failed("FINS Bool 数组响应数据不足");

            // 从字中逐位提取
            bool[] bools = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int absBit = startBit + i;
                int wordIdx = absBit / 16;
                int bitIdx = absBit % 16;
                int byteOff = 14 + wordIdx * 2;
                ushort wordVal = (ushort)((result.Content[byteOff] << 8) | result.Content[byteOff + 1]);
                bools[i] = (wordVal & (1 << bitIdx)) != 0;
            }

            return OperateResult<bool[]>.Success(bools);
        }

        /// <summary>
        /// 批量写入连续位 — 写入指定地址开始的多个位。
        /// <para>采用读-改-写方式: 先读取包含这些位的字，修改后再写回。</para>
        /// </summary>
        /// <param name="address">起始地址，需包含位偏移。</param>
        /// <param name="values">写入的 bool 数组。</param>
        public OperateResult WriteBools(string address, bool[] values)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult.Failed("写入 Bool 数组需要指定位偏移，例如 D100.03");

            // 计算需要操作的字范围
            int startBit = addr.BitOffset;
            int endBit = startBit + values.Length - 1;
            int startWord = addr.WordAddress + (startBit / 16);
            int endWord = addr.WordAddress + (endBit / 16);
            ushort wordCount = (ushort)(endWord - startWord + 1);

            // 读取当前字值
            var readAddr = new FinsAddress(addr.Original, addr.Area, (ushort)startWord, -1, addr.EmBank);
            var current = ReadMemoryArea(readAddr, wordCount);
            if (!current.IsSuccess) return OperateResult.Failed("写入 Bool 数组: 读取当前值失败 — " + current.Message);

            int available = current.Content.Length - 14;
            if (available < wordCount * 2)
                return OperateResult.Failed("写入 Bool 数组: 响应数据不足");

            // 复制到可修改数组
            byte[] wordData = new byte[wordCount * 2];
            Buffer.BlockCopy(current.Content, 14, wordData, 0, wordCount * 2);

            // 修改位
            for (int i = 0; i < values.Length; i++)
            {
                int absBit = startBit + i;
                int wordIdx = absBit / 16;
                int bitIdx = absBit % 16;
                int byteOff = wordIdx * 2;
                ushort wordVal = (ushort)((wordData[byteOff] << 8) | wordData[byteOff + 1]);
                if (values[i])
                    wordVal |= (ushort)(1 << bitIdx);
                else
                    wordVal &= (ushort)~(1 << bitIdx);
                wordData[byteOff] = (byte)(wordVal >> 8);
                wordData[byteOff + 1] = (byte)(wordVal & 0xFF);
            }

            return WriteMemoryArea(readAddr, wordCount, wordData);
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 远程启动 PLC (FINS Command=0x0401)。
        /// </summary>
        public OperateResult Run()
        {
            var r = SendFinsCommand(FinsCommandCode.Run, new byte[] { 0x00, 0x01, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>
        /// 远程停止 PLC (FINS Command=0x0402)。
        /// </summary>
        public OperateResult Stop()
        {
            var r = SendFinsCommand(FinsCommandCode.Stop, new byte[] { 0x00, 0x01, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>异步远程启动 PLC。</summary>
        public Task<OperateResult> RunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Run());

        /// <summary>异步远程停止 PLC。</summary>
        public Task<OperateResult> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Stop());

        // ═══════════════════════════════════════════
        //  CPU 单元数据/状态/时钟
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取 CPU 单元数据 (FINS Command=0x0501) — 返回原始数据。
        /// 包含: 型号代码、版本、系统保留等信息（约 162 字节）。
        /// </summary>
        public OperateResult<byte[]> ReadCpuUnitData()
        {
            var r = SendFinsCommand(FinsCommandCode.ControllerRead, new byte[] { 0x00 });
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 14)
                return OperateResult<byte[]>.Failed("FINS CPU 数据响应不足");

            int dataLen = r.Content.Length - 14;
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(r.Content, 14, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        /// <summary>
        /// 读取 PLC 型号代码 — 从 CPU 单元数据中提取型号名称。
        /// </summary>
        public OperateResult<string> ReadPlcModel()
        {
            var r = ReadCpuUnitData();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 20)
                return OperateResult<string>.Failed("CPU 数据不足以解析型号");

            // 型号代码在偏移 0-19 (20 字节 ASCII)
            int modelEnd = 20;
            for (int i = 0; i < 20; i++)
            {
                if (r.Content[i] == 0x00) { modelEnd = i; break; }
            }
            return OperateResult<string>.Success(
                Encoding.ASCII.GetString(r.Content, 0, modelEnd).TrimEnd());
        }

        /// <summary>
        /// 读取 CPU 运行状态 (FINS Command=0x0601)。
        /// </summary>
        public OperateResult<byte> ReadCpuStatus()
        {
            var r = SendFinsCommand(FinsCommandCode.ControllerStatusRead, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<byte>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 15)
                return OperateResult<byte>.Failed("FINS CPU 状态响应不足");

            // 状态字节在 offset 14
            return OperateResult<byte>.Success(r.Content[14]);
        }

        /// <summary>
        /// 读取 PLC 时钟 (FINS Command=0x0701) — 返回 PLC 时间。
        /// </summary>
        public OperateResult<DateTime> ReadCpuTime()
        {
            var r = SendFinsCommand(FinsCommandCode.TimeRead, Array.Empty<byte>());
            if (!r.IsSuccess) return OperateResult<DateTime>.Failed(r.Message, r.ErrorCode);

            if (r.Content.Length < 20)
                return OperateResult<DateTime>.Failed("FINS 时钟响应数据不足");

            // FINS 时间格式 (BCD): Year(2) + Month(1) + Day(1) + Hour(1) + Minute(1) + Second(1)
            int off = 14;
            int year = BcdToDecimal(r.Content[off]) * 100 + BcdToDecimal(r.Content[off + 1]);
            int month = BcdToDecimal(r.Content[off + 2]);
            int day = BcdToDecimal(r.Content[off + 3]);
            int hour = BcdToDecimal(r.Content[off + 4]);
            int minute = BcdToDecimal(r.Content[off + 5]);
            int second = BcdToDecimal(r.Content[off + 6]);

            if (year < 2000) year += 2000;

            return OperateResult<DateTime>.Success(new DateTime(year, month, day, hour, minute, second));
        }

        /// <summary>
        /// 写入 PLC 时钟 (FINS Command=0x0702) — 将 PC 时间同步到 PLC。
        /// </summary>
        public OperateResult WriteCpuTime(DateTime time)
        {
            byte[] data = new byte[7];
            int year = time.Year;
            data[0] = DecimalToBcd(year / 100);
            data[1] = DecimalToBcd(year % 100);
            data[2] = DecimalToBcd(time.Month);
            data[3] = DecimalToBcd(time.Day);
            data[4] = DecimalToBcd(time.Hour);
            data[5] = DecimalToBcd(time.Minute);
            data[6] = DecimalToBcd(time.Second);

            var r = SendFinsCommand(FinsCommandCode.TimeWrite, data);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message, r.ErrorCode);
            return OperateResult.Success();
        }

        /// <summary>异步读取 PLC 时钟。</summary>
        public Task<OperateResult<DateTime>> ReadCpuTimeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadCpuTime());

        /// <summary>异步写入 PLC 时钟。</summary>
        public Task<OperateResult> WriteCpuTimeAsync(DateTime time, CancellationToken cancellationToken = default)
            => Task.FromResult(WriteCpuTime(time));

        // ── BCD 转换 ──────────────────────────────

        private static int BcdToDecimal(byte bcd)
        {
            return ((bcd >> 4) & 0x0F) * 10 + (bcd & 0x0F);
        }

        private static byte DecimalToBcd(int value)
        {
            return (byte)(((value / 10) << 4) | (value % 10));
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();

            var groups = addrList.GroupBy(a =>
            {
                var parsed = _addressParser.Parse(a);
                return (parsed.Area, parsed.EmBank);
            });

            foreach (var group in groups)
            {
                var sorted = group.Select(a => _addressParser.Parse(a))
                                  .OrderBy(a => a.WordAddress).ToList();

                ushort minAddr = sorted.Min(a => a.WordAddress);
                ushort maxAddr = sorted.Max(a => a.WordAddress);
                ushort range = (ushort)(maxAddr - minAddr + 1);

                var sampleAddr = sorted[0];
                var readAddr = new FinsAddress("", sampleAddr.Area, minAddr, -1, sampleAddr.EmBank);
                var raw = ReadMemoryArea(readAddr, range);
                if (!raw.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(raw.Message, raw.ErrorCode);

                foreach (var addrStr in group)
                {
                    var parsed = _addressParser.Parse(addrStr);
                    int byteOffset = (parsed.WordAddress - minAddr) * 2 + 14;

                    if (parsed.BitOffset >= 0)
                    {
                        if (byteOffset < raw.Content.Length)
                        {
                            byte word = raw.Content[byteOffset];
                            result[addrStr] = (word & (1 << parsed.BitOffset)) != 0;
                        }
                    }
                    else
                    {
                        if (byteOffset + 2 <= raw.Content.Length)
                            result[addrStr] = ToInt16Ordered(raw.Content, byteOffset);
                    }
                }
            }

            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();

            foreach (var addrStr in addresses)
            {
                var parsed = _addressParser.Parse(addrStr);
                var raw = ReadMemoryArea(parsed, 1);
                if (!raw.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(raw.Message, raw.ErrorCode);

                int available = raw.Content.Length - 14;
                if (available <= 0)
                    return OperateResult<Dictionary<string, byte[]>>.Failed("FINS 响应数据不足");

                byte[] data = new byte[available];
                Buffer.BlockCopy(raw.Content, 14, data, 0, available);
                result[addrStr] = data;
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    long l => Write(kv.Key, l),
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock) { _monitors[address] = new MonitorEntry { Address = address, DataType = dataType, IntervalMs = intervalMs, LastValue = null }; }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }
                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };
                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            OnDataChanged?.Invoke(this, new DataChangeEventArgs { Address = entry.Address, OldValue = entry.LastValue, NewValue = current, Timestamp = DateTime.Now, Quality = "Good" });
                            entry.LastValue = current;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
