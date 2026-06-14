using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 MC-3E UDP 协议客户端 — 支持 Q/A/FX 系列全型号。
    /// 基于 SLMP MC 3E Binary/ASCII 帧格式，通过 UDP 传输。
    /// </summary>
    public class Mc3EUdpClient : UdpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public MitsubishiModel Model { get; }
        public byte NetworkNo { get; set; } = 0x00;
        public byte PcNo { get; set; } = 0xFF;
        public ushort DestinationStationNo { get; set; } = 0x00;
        public byte WaitTimeUnit { get; set; } = 0x00;
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;

        /// <summary>网络号别名 — 用于多网络访问。</summary>
        public byte NetworkNumber { get => NetworkNo; set => NetworkNo = value; }

        /// <summary>站号别名 — 用于多网络访问。</summary>
        public byte StationNumber { get => PcNo; set => PcNo = value; }

        /// <summary>
        /// 单次批量读取的最大字数 (默认 960，Q 系列标准值)。
        /// 超出此长度时 ReadLarge/WriteLarge 会自动分片。
        /// </summary>
        public ushort MaxReadWordCount { get; set; } = 960;

        /// <summary>
        /// 单次批量写入的最大字数 (默认 960，Q 系列标准值)。
        /// </summary>
        public ushort MaxWriteWordCount { get; set; } = 960;

        /// <summary>是否使用 ASCII 编码传输 (默认 false, 使用 Binary)。</summary>
        public bool UseAscii { get; set; } = false;

        public Mc3EUdpClient(MitsubishiModel model, string ip, int port = 5007, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Model = model;
        }

        protected override int ResponseHeaderLength => UseAscii ? 18 : 9;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── ASCII 编解码 ─────────────────────────

        private byte[] BuildAsciiFrame(byte[] binaryFrame)
        {
            string hex = BitConverter.ToString(binaryFrame).Replace("-", "");
            return Encoding.ASCII.GetBytes(hex);
        }

        private byte[] ParseAsciiResponse(byte[] asciiResponse)
        {
            string hex = Encoding.ASCII.GetString(asciiResponse);
            byte[] binary = new byte[hex.Length / 2];
            for (int i = 0; i < binary.Length; i++)
                binary[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return binary;
        }

        // ── 通讯（处理 ASCII 编解码）──────────────

        protected new async Task<OperateResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken ct)
        {
            byte[] sendFrame = UseAscii ? BuildAsciiFrame(request) : request;
            var result = await base.SendAndReceiveAsync(sendFrame, ct).ConfigureAwait(false);
            if (!result.IsSuccess) return result;

            return OperateResult<byte[]>.Success(UseAscii ? ParseAsciiResponse(result.Content) : result.Content);
        }

        // ── 帧构建 ──────────────────────────────

        private byte[] BuildMc3EFrame(ushort command, ushort subCommand, byte[] data)
        {
            int frameLen = 2 + 1 + 1 + 2 + 2 + 2 + 2 + data.Length;
            byte[] frame = new byte[frameLen];
            int offset = 0;
            frame[offset++] = 0x50; frame[offset++] = 0x00;
            frame[offset++] = NetworkNo; frame[offset++] = PcNo;
            frame[offset++] = (byte)(DestinationStationNo & 0xFF); frame[offset++] = (byte)((DestinationStationNo >> 8) & 0xFF);
            frame[offset++] = WaitTimeUnit; frame[offset++] = 0x00;
            frame[offset++] = (byte)(command >> 8); frame[offset++] = (byte)(command & 0xFF);
            frame[offset++] = (byte)(subCommand >> 8); frame[offset++] = (byte)(subCommand & 0xFF);
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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E UDP 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        private async Task<OperateResult<byte[]>> ReadWordsBatchAsync(byte subLabel, uint startAddress, ushort count, CancellationToken ct)
        {
            byte[] data = new byte[6];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);

            var req = BuildMc3EFrame(0x0401, 0x0000, data);
            var resp = await SendAndReceiveAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E UDP 响应长度不足");

            ushort completionCode = (ushort)((resp.Content[7] << 8) | resp.Content[8]);
            if (completionCode != 0x0000)
                return OperateResult<byte[]>.Failed($"PLC 错误码: 0x{completionCode:X4}");

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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult.Failed("MC-3E UDP 写响应长度不足");

            ushort completionCode = (ushort)((resp.Content[7] << 8) | resp.Content[8]);
            if (completionCode != 0x0000)
                return OperateResult.Failed($"PLC 错误码: 0x{completionCode:X4}");

            return OperateResult.Success();
        }

        private async Task<OperateResult> WriteWordsBatchAsync(byte subLabel, uint startAddress, ushort count, byte[] writeData, CancellationToken ct)
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
            var resp = await SendAndReceiveAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult.Failed("MC-3E UDP 写响应长度不足");

            ushort completionCode = (ushort)((resp.Content[7] << 8) | resp.Content[8]);
            if (completionCode != 0x0000)
                return OperateResult.Failed($"PLC 错误码: 0x{completionCode:X4}");

            return OperateResult.Success();
        }

        // ── 批量读位 (Command=0x0401, SubCommand=0x0001) ──

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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E UDP 位读取响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 批量写位 (Command=0x1401, SubCommand=0x0001) ──

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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── Bool 数组读写 ───────────────────────────

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

        public OperateResult WriteBools(string address, bool[] values)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] data = new byte[values.Length];
            for (int i = 0; i < values.Length; i++)
                data[i] = (byte)(values[i] ? 0x01 : 0x00);
            return WriteBitsBatch(subLabel, addr, (ushort)values.Length, data);
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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E UDP 响应长度不足");

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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── 多长度随机读取 (Command=0x0403, SubCommand=0x0002) ──

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
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC-3E UDP 多长度随机读取响应长度不足");

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

        public OperateResult<byte[]> ReadLarge(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);

            if (length <= MaxReadWordCount)
                return ReadWordsBatch(subLabel, addr, length);

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

        public OperateResult WriteLarge(string address, byte[] data)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort totalWords = (ushort)((data.Length + 1) / 2);

            if (totalWords <= MaxWriteWordCount)
                return WriteWordsBatch(subLabel, addr, totalWords, data);

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

        public OperateResult RemoteRun()
        {
            var req = BuildMc3EFrame(0x1001, 0x0000, new byte[] { 0x01, 0x00, 0x00, 0x00 });
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public OperateResult RemoteStop()
        {
            var req = BuildMc3EFrame(0x1002, 0x0000, new byte[] { 0x01, 0x00 });
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public OperateResult RemoteReset()
        {
            var req = BuildMc3EFrame(0x1006, 0x0000, new byte[] { 0x01, 0x00 });
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public OperateResult<string> ReadPlcType()
        {
            var req = BuildMc3EFrame(0x0101, 0x0000, Array.Empty<byte>());
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            if (resp.Content.Length < 9)
                return OperateResult<string>.Failed("MC-3E UDP ReadPlcType 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            if (dataLen < 16)
                return OperateResult<string>.Failed("PLC 型号响应数据不足");

            string typeName = Encoding.ASCII.GetString(resp.Content, 9, 16).TrimEnd('\0', ' ');
            return OperateResult<string>.Success(typeName);
        }

        public OperateResult ErrorStateReset()
        {
            var req = BuildMc3EFrame(0x1617, 0x0000, Array.Empty<byte>());
            var resp = SendAndReceiveAsync(req, CancellationToken.None).GetAwaiter().GetResult();
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public Task<OperateResult> RemoteRunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteRun());

        public Task<OperateResult> RemoteStopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteStop());

        public Task<OperateResult> RemoteResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(RemoteReset());

        public Task<OperateResult<string>> ReadPlcTypeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ReadPlcType());

        public Task<OperateResult> ErrorStateResetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ErrorStateReset());

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
                        for (int i = 0; i < 4; i++) { byte tmp = result[i]; result[i] = result[7 - i]; result[7 - i] = tmp; }
                        break;
                    case Endianness.MidBigEndian:
                        for (int i = 0; i < 8; i += 2) { byte tmp = result[i]; result[i] = result[i + 1]; result[i + 1] = tmp; }
                        break;
                    case Endianness.MidLittleEndian:
                        { byte tmp = result[0]; result[0] = result[2]; result[2] = tmp; tmp = result[1]; result[1] = result[3]; result[3] = tmp; }
                        { byte tmp = result[4]; result[4] = result[6]; result[6] = tmp; tmp = result[5]; result[5] = result[7]; result[7] = tmp; }
                        break;
                }
            }
            return result;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            // A2 修复：位区域应使用位读取，原用字读取被 PLC 拒绝。
            var r = ReadBitsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content[0] != 0);
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
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            ushort wordCount = (ushort)(data.Length / 2);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

        // ── 异步覆写 ──────────────────────────────

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
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
            => Task.FromResult(BatchRead(addresses));

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
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                var (subLabel, addr) = Mc3EAddressParser.Parse(kv.Key);
                byte[] data = ObjectToBytes(kv.Value);
                ushort wordCount = (ushort)((data.Length + 1) / 2);
                if (data.Length % 2 != 0)
                    Array.Resize(ref data, data.Length + 1);
                var r = WriteWordsBatch(subLabel, addr, wordCount, data);
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

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

        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

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

        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

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
