using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Panasonic
{
    /// <summary>
    /// Panasonic MC 协议客户端 — 基于 MC 3E Binary 帧格式，使用松下特定设备代码。
    /// <para>支持 FP7 系列 PLC，指令格式与三菱 MC 3E Binary 相同，设备代码不同。</para>
    /// <para>指令支持: 批量读字(0x0401)、批量写字(0x1401)、随机读取(0x0403)、随机写入(0x1402)。</para>
    /// </summary>
    public class PanasonicMcClient : TcpDeviceBase, IBatchReadWrite
    {
        public byte NetworkNo { get; set; } = 0x00;
        public byte PcNo { get; set; } = 0xFF;
        public ushort DestinationStationNo { get; set; } = 0x00;
        public byte WaitTimeUnit { get; set; } = 0x00;

        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;
        public ushort MaxReadWordCount { get; set; } = 960;
        public ushort MaxWriteWordCount { get; set; } = 960;

        public PanasonicMcClient(string ip, int port = 5007, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        protected override int ResponseHeaderLength => 9;

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        protected override byte[]? BuildHeartbeat()
        {
            return BuildMcFrame(0x0401, 0x0000, new byte[] { 0xA8, 0x00, 0x00, 0x00, 0x00, 0x01 });
        }

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                // C1-R2 修复：与基类走同一把 IO 锁，消除双锁串台。
                AcquireIoLock();
                try
                {
                    NetworkStream? ns = _stream;
                    if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                    Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                    RaiseMessageSent(DataConverter.ToHexString(request));

                    ns.Write(request, 0, request.Length);

                    byte[]? header = ReadExactNs(ns, 9);
                    if (header == null) return OperateResult<byte[]>.Failed("读取 MC 响应头失败");

                    ushort completionCode = (ushort)((header[7] << 8) | header[8]);
                    if (completionCode != 0)
                    {
                        string errMsg = $"MC 错误码: 0x{completionCode:X4}";
                        byte[] errResp = header;
                        Log.Debug($"RX ← {DataConverter.ToHexString(errResp)}");
                        RaiseMessageReceived(DataConverter.ToHexString(errResp));
                        if (!_persistentMode) DisconnectCore();
                        return OperateResult<byte[]>.Failed(errMsg, completionCode);
                    }

                    byte[]? payload = null;
                    Thread.Sleep(10);
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
                                    Thread.Sleep(10);
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

                    if (!_persistentMode) DisconnectCore();

                    return OperateResult<byte[]>.Success(full);
                }
                finally
                {
                    ReleaseIoLock();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"MC 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode)
                {
                    AcquireIoLock();
                    try { DisconnectCore(); }
                    finally { ReleaseIoLock(); }
                }
                return OperateResult<byte[]>.Failed($"MC 通讯异常: {ex.Message}");
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

        private byte[] BuildMcFrame(ushort command, ushort subCommand, byte[] data)
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

        public OperateResult<byte[]> ReadWordsBatch(byte subLabel, uint startAddress, ushort count)
        {
            byte[] data = new byte[6];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);

            var req = BuildMcFrame(0x0401, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

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

            var req = BuildMcFrame(0x1401, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public OperateResult<byte[]> ReadWordsRandom((byte subLabel, uint address)[] items)
        {
            byte[] data = new byte[2 + items.Length * 4];
            data[0] = (byte)(items.Length & 0xFF);
            data[1] = (byte)((items.Length >> 8) & 0xFF);
            for (int i = 0; i < items.Length; i++)
            {
                int o = 2 + i * 4;
                data[o] = items[i].subLabel;
                data[o + 1] = (byte)(items[i].address & 0xFF);
                data[o + 2] = (byte)((items[i].address >> 8) & 0xFF);
                data[o + 3] = (byte)((items[i].address >> 16) & 0xFF);
            }

            var req = BuildMcFrame(0x0403, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC 响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        public OperateResult WriteWordsRandom((byte subLabel, uint address, ushort value)[] items)
        {
            byte[] data = new byte[2 + items.Length * 6];
            data[0] = (byte)(items.Length & 0xFF);
            data[1] = (byte)((items.Length >> 8) & 0xFF);
            for (int i = 0; i < items.Length; i++)
            {
                int o = 2 + i * 6;
                data[o] = items[i].subLabel;
                data[o + 1] = (byte)(items[i].address & 0xFF);
                data[o + 2] = (byte)((items[i].address >> 8) & 0xFF);
                data[o + 3] = (byte)((items[i].address >> 16) & 0xFF);
                data[o + 4] = (byte)(items[i].value >> 8);
                data[o + 5] = (byte)(items[i].value & 0xFF);
            }

            var req = BuildMcFrame(0x1402, 0x0000, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        public OperateResult<byte[]> ReadBitsBatch(byte subLabel, uint startAddress, ushort count)
        {
            byte[] data = new byte[6];
            data[0] = subLabel;
            data[1] = (byte)(startAddress & 0xFF);
            data[2] = (byte)((startAddress >> 8) & 0xFF);
            data[3] = (byte)((startAddress >> 16) & 0xFF);
            data[4] = (byte)(count & 0xFF);
            data[5] = (byte)((count >> 8) & 0xFF);

            var req = BuildMcFrame(0x0401, 0x0001, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length < 9)
                return OperateResult<byte[]>.Failed("MC 位读取响应长度不足");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

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

            var req = BuildMcFrame(0x1401, 0x0001, data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;
            return OperateResult.Success();
        }

        // ── IReadWriteDevice 实现 ──────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            // A2 修复：位区域应使用位读取，原用字读取被 PLC 拒绝。
            var r = ReadBitsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 1);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
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
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
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
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 4);
            return OperateResult<float>.Success(DataConverter.ToFloat(ordered, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            var r = ReadWordsBatch(subLabel, addr, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 8);
            return OperateResult<double>.Success(DataConverter.ToDouble(ordered, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatch(subLabel, addr, wordCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatch(subLabel, addr, wordCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, bool value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes((short)(value ? 1 : 0)));
        }

        public override OperateResult Write(string address, short value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ushort value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            return WriteWordsBatch(subLabel, addr, 1, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, int value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatch(subLabel, addr, 2, ordered);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatch(subLabel, addr, 4, ordered);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));

        public override OperateResult Write(string address, float value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatch(subLabel, addr, 2, ordered);
        }

        public override OperateResult Write(string address, double value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatch(subLabel, addr, 4, ordered);
        }

        public override OperateResult Write(string address, string value)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var (subLabel, addr) = PanasonicMcAddress.Parse(address);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatch(subLabel, addr, wordCount, data);
        }

        // ── IBatchReadWrite 实现 ──────────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            var result = new Dictionary<string, object?>();

            var groups = new Dictionary<byte, List<(string original, uint address)>>();
            foreach (var addr in addrList)
            {
                var (subLabel, addrVal) = PanasonicMcAddress.Parse(addr);
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
                var (subLabel, addr) = PanasonicMcAddress.Parse(addrList[i]);
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
                var (subLabel, addr) = PanasonicMcAddress.Parse(kv.Key);
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
        {
            return Task.FromResult(BatchWrite(items));
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
    }
}
