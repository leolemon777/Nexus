using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 MC-3E Binary 协议客户端 — 支持 Q/A/FX 系列全型号。
    /// 基于 SLMP (Seamless Message Protocol) MC 3E Binary 帧格式。
    /// <para>指令支持: 批量读字(0x0401)、批量写字(0x1401)、随机读取(0x0403)、随机写入(0x1402)。</para>
    /// </summary>
    public class Mc3EBinaryClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        /// <summary>PLC 型号。</summary>
        public MitsubishiModel Model { get; }

        /// <summary>网络号 (0x00=本地网络)。</summary>
        public byte NetworkNo { get; set; } = 0x00;

        /// <summary>PC 号 (0xFF=自站)。</summary>
        public byte PcNo { get; set; } = 0xFF;

        /// <summary>请求目标模块站号。</summary>
        public ushort DestinationStationNo { get; set; } = 0x00;

        /// <summary>等待时间单位 (0=无限, 1=250ms单位)。</summary>
        public byte WaitTimeUnit { get; set; } = 0x00;

        /// <summary>网络号别名 — 用于多网络访问。</summary>
        public byte NetworkNumber { get => NetworkNo; set => NetworkNo = value; }

        /// <summary>站号别名 — 用于多网络访问。</summary>
        public byte StationNumber { get => PcNo; set => PcNo = value; }

        /// <summary>
        /// 多字节数据的字节序 (默认 BigEndian=ABCD)。
        /// 影响 Int32/UInt32/Float/Int64/UInt64/Double 的读写。
        /// </summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        /// <summary>
        /// 字符串编码 (默认 ASCII)。
        /// </summary>
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;

        /// <summary>
        /// 单次批量读取的最大字数 (默认 960，Q 系列标准值)。
        /// 超出此长度时 ReadLarge/WriteLarge 会自动分片。
        /// </summary>
        public ushort MaxReadWordCount { get; set; } = 960;

        /// <summary>
        /// 单次批量写入的最大字数 (默认 960，Q 系列标准值)。
        /// </summary>
        public ushort MaxWriteWordCount { get; set; } = 960;

        public Mc3EBinaryClient(MitsubishiModel model, string ip, int port = 5007, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Model = model;
        }

        // ── MC-3E Binary 帧结构 ──────────────────
        // 请求帧:
        //   SubHeader(2) + NetworkNo(1) + PcNo(1) + ReqDstStationNo(2) + WaitTime(2)
        //   + Command(2) + SubCommand(2) + Data(变长)
        //
        // 响应帧:
        //   SubHeader(2) + NetworkNo(1) + PcNo(1) + RespDstStationNo(2) + CompletionCode(2) + Data(变长)
        //
        // SubHeader = 0x50 0x00 (MC 3E Binary)

        protected override int ResponseHeaderLength => 9;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            return 0;
        }

        /// <summary>默认心跳：批量读字 D0 的 1 个 word（Command=0x0401, SubCommand=0x0000）。</summary>
        protected override byte[] BuildHeartbeat()
        {
            // D0: sub-label=0x0A(D), start=0, count=1
            return BuildMc3EFrame(0x0401, 0x0000, new byte[] { 0x0A, 0x00, 0x00, 0x00, 0x00, 0x01 });
        }

        // ── 帧读取（重写）────────────────────────

        protected new OperateResult<byte[]> SendAndReceive(byte[] request)
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

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                byte[]? header = ReadExactNs(ns, 9);
                if (header == null) return OperateResult<byte[]>.Failed("读取 MC-3E 响应头失败");

                ushort completionCode = (ushort)((header[7] << 8) | header[8]);
                if (completionCode != 0)
                {
                    string errMsg = completionCode switch
                    {
                        0xC001 => "无法识别的指令",
                        0xC002 => "无法识别的子指令",
                        0xC051 => "超出同时连接数",
                        0xD003 => "通信对象不存在",
                        0xD004 => "通信对象被其他站占用",
                        _ => $"MC-3E 错误码: 0x{completionCode:X4}"
                    };

                    byte[] errResp = header;
                    Log.Debug($"RX ← {DataConverter.ToHexString(errResp)}");
                    RaiseMessageReceived(DataConverter.ToHexString(errResp));
                    if (!_persistentMode) lock (_lock) DisconnectCore();
                    return OperateResult<byte[]>.Failed(errMsg, completionCode);
                }

                byte[]? payload = null;
                System.Threading.Thread.Sleep(10);
                if (ns.DataAvailable)
                {
                    using (var ms = new System.IO.MemoryStream())
                    {
                        byte[] buf = new byte[4096];
                        int retryCount = 0;
                        while (retryCount < 50)
                        {
                            if (ns.DataAvailable)
                            {
                                int read = ns.Read(buf, 0, buf.Length);
                                if (read == 0) break;
                                ms.Write(buf, 0, read);
                                retryCount = 0;
                            }
                            else
                            {
                                retryCount++;
                                if (ms.Length > 0 && retryCount > 3) break;
                                System.Threading.Thread.Sleep(10);
                            }
                        }
                        if (ms.Length > 0)
                            payload = ms.ToArray();
                    }
                }

                int payloadLen = payload?.Length ?? 0;
                byte[] full = new byte[9 + payloadLen];
                Buffer.BlockCopy(header, 0, full, 0, 9);
                if (payload != null && payload.Length > 0)
                    Buffer.BlockCopy(payload, 0, full, 9, payload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                RaiseMessageReceived(DataConverter.ToHexString(full));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(full);
            }
            catch (Exception ex)
            {
                Log.Error($"MC-3E 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"MC-3E 通讯异常: {ex.Message}");
            }
        }

        private byte[]? ReadExactNs(NetworkStream ns, int count)
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

        // ── 字节序处理 ──────────────────────────

        private byte[] ApplyByteOrder(byte[] data, int length)
        {
            if (ByteOrder == Endianness.BigEndian) return data;

            byte[] result = new byte[length];
            Buffer.BlockCopy(data, 0, result, 0, Math.Min(data.Length, length));

            if (length == 4)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        byte t0 = result[0]; result[0] = result[3]; result[3] = t0;
                        byte t1 = result[1]; result[1] = result[2]; result[2] = t1;
                        break;
                    case Endianness.MidBigEndian:
                        byte mb0 = result[0]; result[0] = result[1]; result[1] = mb0;
                        byte mb1 = result[2]; result[2] = result[3]; result[3] = mb1;
                        break;
                    case Endianness.MidLittleEndian:
                        byte ml0 = result[0]; result[0] = result[2]; result[2] = ml0;
                        byte ml1 = result[1]; result[1] = result[3]; result[3] = ml1;
                        break;
                }
            }
            else if (length == 8)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        Array.Reverse(result, 0, 8);
                        break;
                    case Endianness.MidBigEndian:
                        for (int i = 0; i < 8; i += 2)
                        { byte s = result[i]; result[i] = result[i + 1]; result[i + 1] = s; }
                        break;
                    case Endianness.MidLittleEndian:
                        for (int i = 0; i < 4; i++)
                        { byte s = result[i]; result[i] = result[i + 4]; result[i + 4] = s; }
                        break;
                }
            }

            return result;
        }

        // ── MC-3E 帧构建 ─────────────────────────

        private byte[] BuildMc3EFrame(ushort command, ushort subCommand, byte[] data)
        {
            int frameLen = 2 + 1 + 1 + 2 + 2 + 2 + 2 + data.Length;
            byte[] frame = new byte[frameLen];
            int offset = 0;

            frame[offset++] = 0x50; frame[offset++] = 0x00;
            frame[offset++] = NetworkNo;
            frame[offset++] = PcNo;
            frame[offset++] = (byte)(DestinationStationNo & 0xFF);
            frame[offset++] = (byte)((DestinationStationNo >> 8) & 0xFF);
            frame[offset++] = WaitTimeUnit;
            frame[offset++] = 0x00;
            frame[offset++] = (byte)(command >> 8);
            frame[offset++] = (byte)(command & 0xFF);
            frame[offset++] = (byte)(subCommand >> 8);
            frame[offset++] = (byte)(subCommand & 0xFF);
            Buffer.BlockCopy(data, 0, frame, offset, data.Length);
            return frame;
        }

        // ── 批量读字 (Command=0x0401, SubCommand=0x0000) ──

        public OperateResult<byte[]> ReadWordsBatch(byte subLabel, uint startAddress, ushort count)
        {
            byte[] data = new byte[6];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);

            var req = BuildMc3EFrame(0x0401, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 批量写字 (Command=0x1401, SubCommand=0x0000) ──

        public OperateResult WriteWordsBatch(byte subLabel, uint startAddress, ushort count, byte[] writeData)
        {
            byte[] data = new byte[6 + writeData.Length];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);
            Buffer.BlockCopy(writeData, 0, data, 6, writeData.Length);

            var req = BuildMc3EFrame(0x1401, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── 随机读取 (Command=0x0403, SubCommand=0x0000) ──

        public OperateResult<byte[]> ReadWordsRandom((byte subLabel, uint address)[] items)
        {
            byte[] data = new byte[2 + items.Length * 4];
            data[0] = (byte)(items.Length & 0xFF);
            data[1] = (byte)((items.Length >> 8) & 0xFF);
            for (int i = 0; i < items.Length; i++)
            {
                int offset = 2 + i * 4;
                data[offset] = items[i].subLabel;
                data[offset + 1] = (byte)(items[i].address & 0xFF);
                data[offset + 2] = (byte)((items[i].address >> 8) & 0xFF);
                data[offset + 3] = (byte)((items[i].address >> 16) & 0xFF);
            }

            var req = BuildMc3EFrame(0x0403, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 随机写入 (Command=0x1402, SubCommand=0x0000) ──

        public OperateResult WriteWordsRandom((byte subLabel, uint address, ushort value)[] items)
        {
            byte[] data = new byte[2 + items.Length * 6];
            data[0] = (byte)(items.Length & 0xFF);
            data[1] = (byte)((items.Length >> 8) & 0xFF);
            for (int i = 0; i < items.Length; i++)
            {
                int offset = 2 + i * 6;
                data[offset] = items[i].subLabel;
                data[offset + 1] = (byte)(items[i].address & 0xFF);
                data[offset + 2] = (byte)((items[i].address >> 8) & 0xFF);
                data[offset + 3] = (byte)((items[i].address >> 16) & 0xFF);
                data[offset + 4] = (byte)(items[i].value >> 8);
                data[offset + 5] = (byte)(items[i].value & 0xFF);
            }

            var req = BuildMc3EFrame(0x1402, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── 批量读位 (Command=0x0401, SubCommand=0x0001) ──

        /// <summary>
        /// 批量读取位数据 — 使用 MC-3E 位读取子命令 (SubCommand=0x0001)。
        /// 每个位返回一个字节 (0x00 或 0x01)。
        /// </summary>
        /// <param name="subLabel">子标签号 (如 M=0x90, X=0x9C, Y=0x9D)。</param>
        /// <param name="startAddress">起始地址。</param>
        /// <param name="count">读取位数。</param>
        public OperateResult<byte[]> ReadBitsBatch(byte subLabel, uint startAddress, ushort count)
        {
            byte[] data = new byte[6];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);

            var req = BuildMc3EFrame(0x0401, 0x0001, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E 位读取响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 批量写位 (Command=0x1401, SubCommand=0x0001) ──

        /// <summary>
        /// 批量写入位数据 — 使用 MC-3E 位写入子命令 (SubCommand=0x0001)。
        /// 每个位为一个字节 (0x00 或 0x01)。
        /// </summary>
        /// <param name="subLabel">子标签号。</param>
        /// <param name="startAddress">起始地址。</param>
        /// <param name="count">写入位数。</param>
        /// <param name="bitData">位数据 (每个字节 0x00 或 0x01)。</param>
        public OperateResult WriteBitsBatch(byte subLabel, uint startAddress, ushort count, byte[] bitData)
        {
            byte[] data = new byte[6 + bitData.Length];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);
            Buffer.BlockCopy(bitData, 0, data, 6, bitData.Length);

            var req = BuildMc3EFrame(0x1401, 0x0001, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── Bool 数组读写 ───────────────────────────

        /// <summary>
        /// 批量读取 bool 数组 — 支持 M/X/Y/B 等位地址区域。
        /// </summary>
        /// <param name="address">起始地址，如 "M100", "X0"。</param>
        /// <param name="count">读取位数。</param>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadBitsBatch(subLabel, addr, count);
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);

            bool[] result = new bool[r.Content.Length];
            for (int i = 0; i < r.Content.Length; i++)
                result[i] = r.Content[i] != 0;
            return OperateResult<bool[]>.Success(result);
        }

        /// <summary>
        /// 批量写入 bool 数组 — 支持 M/X/Y/B 等位地址区域。
        /// </summary>
        /// <param name="address">起始地址。</param>
        /// <param name="values">写入的 bool 数组。</param>
        public OperateResult WriteBools(string address, bool[] values)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] data = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                data[i] = (byte)(values[i] ? 0x01 : 0x00);
            return WriteBitsBatch(subLabel, addr, (ushort)values.Length, data);
        }

        // ── 多长度随机读取 (Command=0x0403, SubCommand=0x0002) ──

        /// <summary>
        /// 多长度随机读取 — 每个地址可以指定不同的读取长度。
        /// Data: count(2) + [SubLabel(1) + Address(3) + Length(2)] * N
        /// </summary>
        public OperateResult<byte[]> ReadWordsRandomMultiLength((byte subLabel, uint address, ushort length)[] items)
        {
            byte[] data = new byte[2 + items.Length * 6];
            data[0] = (byte)(items.Length & 0xFF);
            data[1] = (byte)((items.Length >> 8) & 0xFF);
            for (int i = 0; i < items.Length; i++)
            {
                int offset = 2 + i * 6;
                data[offset] = items[i].subLabel;
                data[offset + 1] = (byte)(items[i].address & 0xFF);
                data[offset + 2] = (byte)((items[i].address >> 8) & 0xFF);
                data[offset + 3] = (byte)((items[i].address >> 16) & 0xFF);
                data[offset + 4] = (byte)(items[i].length & 0xFF);
                data[offset + 5] = (byte)((items[i].length >> 8) & 0xFF);
            }

            var req = BuildMc3EFrame(0x0403, 0x0002, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E 多长度随机读取响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        /// <summary>
        /// 多长度随机读取 (高层接口) — 按地址+长度列表读取，返回各地址对应的字节数组。
        /// </summary>
        public OperateResult<Dictionary<string, byte[]>> ReadRandomMultiLength(
            IEnumerable<(string address, ushort length)> items)
        {
            var itemList = items.ToList();
            var mcItems = new (byte subLabel, uint address, ushort length)[itemList.Count];
            for (int i = 0; i < itemList.Count; i++)
            {
                var (subLabel, addr) = Mc3EAddressParser.Parse(itemList[i].address);
                mcItems[i] = (subLabel, addr, itemList[i].length);
            }

            var r = ReadWordsRandomMultiLength(mcItems);
            if (!r.IsSuccess)
                return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);

            var result = new Dictionary<string, byte[]>();
            int offset = 0;
            for (int i = 0; i < itemList.Count; i++)
            {
                int byteLen = itemList[i].length * 2;
                byte[] wordData = new byte[byteLen];
                if (offset + byteLen <= r.Content.Length)
                    Buffer.BlockCopy(r.Content, offset, wordData, 0, byteLen);
                result[itemList[i].address] = wordData;
                offset += byteLen;
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        // ── 大数据自动分片读写 ──────────────────────

        /// <summary>
        /// 读取大量数据 — 当请求长度超过 MaxReadWordCount 时自动分片。
        /// </summary>
        /// <param name="address">起始地址。</param>
        /// <param name="length">读取字数。</param>
        public OperateResult<byte[]> ReadLarge(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);

            if (length <= MaxReadWordCount)
                return ReadWordsBatch(subLabel, addr, length);

            // 自动分片
            using var ms = new System.IO.MemoryStream(length * 2);
            uint currentAddr = addr;
            ushort remaining = length;

            while (remaining > 0)
            {
                ushort chunkSize = (ushort)Math.Min(remaining, MaxReadWordCount);
                var r = ReadWordsBatch(subLabel, currentAddr, chunkSize);
                if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
                ms.Write(r.Content, 0, r.Content.Length);
                currentAddr += chunkSize;
                remaining -= chunkSize;
            }

            return OperateResult<byte[]>.Success(ms.ToArray());
        }

        /// <summary>
        /// 写入大量数据 — 当数据长度超过 MaxWriteWordCount 时自动分片。
        /// </summary>
        /// <param name="address">起始地址。</param>
        /// <param name="data">写入的字节数据。</param>
        public OperateResult WriteLarge(string address, byte[] data)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort totalWords = (ushort)((data.Length + 1) / 2);

            if (totalWords <= MaxWriteWordCount)
                return WriteWordsBatch(subLabel, addr, totalWords, data);

            // 自动分片
            uint currentAddr = addr;
            int offset = 0;
            ushort remaining = totalWords;

            while (remaining > 0)
            {
                ushort chunkWords = (ushort)Math.Min(remaining, MaxWriteWordCount);
                int chunkBytes = chunkWords * 2;
                byte[] chunkData = new byte[chunkBytes];
                int copyLen = Math.Min(chunkBytes, data.Length - offset);
                if (copyLen > 0)
                    Buffer.BlockCopy(data, offset, chunkData, 0, copyLen);

                var r = WriteWordsBatch(subLabel, currentAddr, chunkWords, chunkData);
                if (!r.IsSuccess) return r;

                currentAddr += chunkWords;
                offset += chunkBytes;
                remaining -= chunkWords;
            }

            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  PLC 控制命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 远程启动 PLC (Command=0x1001, SubCommand=0x0000)。
        /// </summary>
        public OperateResult RemoteRun()
        {
            var req = BuildMc3EFrame(0x1001, 0x0000, new byte[] { 0x01, 0x00, 0x00, 0x00 });
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        /// <summary>
        /// 远程停止 PLC (Command=0x1002, SubCommand=0x0000)。
        /// </summary>
        public OperateResult RemoteStop()
        {
            var req = BuildMc3EFrame(0x1002, 0x0000, new byte[] { 0x01, 0x00 });
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        /// <summary>
        /// 远程复位 PLC (Command=0x1006, SubCommand=0x0000)。
        /// </summary>
        public OperateResult RemoteReset()
        {
            var req = BuildMc3EFrame(0x1006, 0x0000, new byte[] { 0x01, 0x00 });
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        /// <summary>
        /// 读取 PLC 型号信息 (Command=0x0101, SubCommand=0x0000) — 返回 16 字节 ASCII 名称。
        /// </summary>
        public OperateResult<string> ReadPlcType()
        {
            var req = BuildMc3EFrame(0x0101, 0x0000, Array.Empty<byte>());
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            if (resp.Content.Length < 9)
                return OperateResult<string>.Failed("MC-3E ReadPlcType 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            if (dataLen < 16)
                return OperateResult<string>.Failed("PLC 型号响应数据不足");

            string typeName = Encoding.ASCII.GetString(resp.Content, 9, 16).TrimEnd('\0', ' ');
            return OperateResult<string>.Success(typeName);
        }

        /// <summary>
        /// 错误状态复位 (Command=0x1617, SubCommand=0x0000) — LED 熄灭、出错代码初始化。
        /// </summary>
        public OperateResult ErrorStateReset()
        {
            var req = BuildMc3EFrame(0x1617, 0x0000, Array.Empty<byte>());
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── PLC 控制异步版本 ──────────────────────

        /// <summary>异步远程启动 PLC。</summary>
        public Task<OperateResult> RemoteRunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteRun());

        /// <summary>异步远程停止 PLC。</summary>
        public Task<OperateResult> RemoteStopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteStop());

        /// <summary>异步远程复位 PLC。</summary>
        public Task<OperateResult> RemoteResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteReset());

        /// <summary>异步读取 PLC 型号。</summary>
        public Task<OperateResult<string>> ReadPlcTypeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadPlcType());

        /// <summary>异步错误状态复位。</summary>
        public Task<OperateResult> ErrorStateResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ErrorStateReset());

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            var result = new Dictionary<string, object?>();

            var groups = new Dictionary<byte, List<(string original, uint address)>>();
            foreach (var addr in addrList)
            {
                var (subLabel, addrVal) = Mc3EAddressParser.Parse(addr);
                if (!groups.ContainsKey(subLabel))
                    groups[subLabel] = new List<(string, uint)>();
                groups[subLabel].Add((addr, addrVal));
                result[addr] = null;
            }

            foreach (var kv in groups)
            {
                var items = kv.Value.OrderBy(x => x.address).ToList();
                uint minAddr = items[0].address;
                uint maxAddr = items[items.Count - 1].address;
                ushort count = (ushort)(maxAddr - minAddr + 1);

                var r = ReadWordsBatch(kv.Key, minAddr, count);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);

                foreach (var item in items)
                {
                    int offset = (int)(item.address - minAddr) * 2;
                    if (offset + 1 < r.Content.Length)
                    {
                        ushort val = (ushort)((r.Content[offset] << 8) | r.Content[offset + 1]);
                        result[item.original] = val;
                    }
                }
            }

            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BatchRead(addresses));
        }

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            var items = new (byte subLabel, uint address)[addrList.Count];
            for (int i = 0; i < addrList.Count; i++)
            {
                var (subLabel, addr) = Mc3EAddressParser.Parse(addrList[i]);
                items[i] = (subLabel, addr);
            }

            var r = ReadWordsRandom(items);
            if (!r.IsSuccess)
                return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);

            var result = new Dictionary<string, byte[]>();
            for (int i = 0; i < addrList.Count; i++)
            {
                int offset = i * 2;
                byte[] wordData = new byte[2];
                if (offset + 1 < r.Content.Length)
                {
                    wordData[0] = r.Content[offset];
                    wordData[1] = r.Content[offset + 1];
                }
                result[addrList[i]] = wordData;
            }

            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(RandomRead(addresses));
        }

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                var (subLabel, addr) = Mc3EAddressParser.Parse(kv.Key);
                byte[] data = ObjectToBytes(kv.Value);
                ushort wordCount = (ushort)((data.Length + 1) / 2);
                if (data.Length % 2 != 0)
                {
                    Array.Resize(ref data, data.Length + 1);
                }
                var r = WriteWordsBatch(subLabel, addr, wordCount, data);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BatchWrite(items));
        }

        /// <summary>
        /// 随机写入不连续地址 (MC 指令 0x1402)。
        /// </summary>
        public OperateResult RandomWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            var writeItems = new (byte subLabel, uint address, ushort value)[itemList.Count];
            for (int i = 0; i < itemList.Count; i++)
            {
                var (subLabel, addr) = Mc3EAddressParser.Parse(itemList[i].Key);
                ushort val = Convert.ToUInt16(itemList[i].Value);
                writeItems[i] = (subLabel, addr, val);
            }
            return WriteWordsRandom(writeItems);
        }

        private static byte[] ObjectToBytes(object value)
        {
            return value switch
            {
                bool b => DataConverter.GetBytes(b),
                short i16 => DataConverter.GetBytes(i16),
                ushort u16 => DataConverter.GetBytes(u16),
                int i32 => DataConverter.GetBytes(i32),
                uint u32 => DataConverter.GetBytes(u32),
                long i64 => DataConverter.GetBytes(i64),
                ulong u64 => DataConverter.GetBytes(unchecked((long)u64)),
                float f => DataConverter.GetBytes(f),
                double d => DataConverter.GetBytes(d),
                string s => DataConverter.GetBytes(s),
                byte[] barr => barr,
                _ => DataConverter.GetBytes(Convert.ToInt16(value))
            };
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[1] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 4);
            return OperateResult<int>.Success(DataConverter.ToInt32(ordered, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 8);
            return OperateResult<long>.Success(DataConverter.ToInt64(ordered, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 4);
            return OperateResult<float>.Success(DataConverter.ToFloat(ordered, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 8);
            return OperateResult<double>.Success(DataConverter.ToDouble(ordered, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatch(subLabel, addr, wordCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }

        /// <summary>
        /// 使用指定编码读取字符串。
        /// </summary>
        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatch(subLabel, addr, wordCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            string s = StringEncoding.GetString(r.Content, 0, length).TrimEnd('\0', ' ');
            return OperateResult<string>.Success(s);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatch(subLabel, addr, wordCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ──────────────────────────────

        public override OperateResult Write(string address, bool value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes((short)(value ? 1 : 0)));
        }

        public override OperateResult Write(string address, short value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, int value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatch(subLabel, addr, 2, ordered);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatch(subLabel, addr, 4, ordered);
        }
        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));

        public override OperateResult Write(string address, float value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatch(subLabel, addr, 2, ordered);
        }

        public override OperateResult Write(string address, double value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatch(subLabel, addr, 4, ordered);
        }

        public override OperateResult Write(string address, string value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

        /// <summary>
        /// 使用指定编码写入字符串。
        /// </summary>
        public OperateResult WriteStringEncoded(string address, string value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] data = StringEncoding.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

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
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
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
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
