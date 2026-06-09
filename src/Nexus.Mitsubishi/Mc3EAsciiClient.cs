using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 MC-3E ASCII 协议客户端 — 支持 Q/A/FX 系列全型号。
    /// 基于 SLMP MC 3E 帧格式，使用 ASCII 编码传输。
    /// </summary>
    public class Mc3EAsciiClient : TcpDeviceBase, IBatchReadWrite
    {
        public MitsubishiModel Model { get; }
        public byte NetworkNo { get; set; } = 0x00;
        public byte PcNo { get; set; } = 0xFF;
        public ushort DestinationStationNo { get; set; } = 0x00;
        public byte WaitTimeUnit { get; set; } = 0x00;
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;
        public ushort MaxReadWordCount { get; set; } = 960;
        public ushort MaxWriteWordCount { get; set; } = 960;

        public Mc3EAsciiClient(MitsubishiModel model, string ip, int port = 5007, int timeout = 5000) : base(ip, port, timeout) { Model = model; }

        protected override int ResponseHeaderLength => 18;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── ASCII 编解码 ─────────────────────────

        private byte[] BuildAsciiFrame(byte[] binaryFrame) => Encoding.ASCII.GetBytes(BitConverter.ToString(binaryFrame).Replace("-", ""));

        private byte[] ParseAsciiResponse(byte[] asciiResponse)
        {
            string hex = Encoding.ASCII.GetString(asciiResponse);
            byte[] binary = new byte[hex.Length / 2];
            for (int i = 0; i < binary.Length; i++) binary[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return binary;
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

        // ── 通讯（重写以正确读取完整 ASCII 响应）──

        protected new async Task<OperateResult<byte[]>> SendAndReceiveAsync(byte[] request, CancellationToken ct)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = await ConnectAsync().ConfigureAwait(false);
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                byte[] asciiFrame = BuildAsciiFrame(request);
                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                await ns.WriteAsync(asciiFrame, 0, asciiFrame.Length, ct).ConfigureAwait(false);

                // 读取完整 ASCII 响应（轮询 DataAvailable）
                using (var ms = new MemoryStream())
                {
                    byte[] buf = new byte[4096];
                    int retryCount = 0;
                    while (retryCount < 50)
                    {
                        if (ns.DataAvailable)
                        {
                            int read = await ns.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                            if (read == 0) break;
                            ms.Write(buf, 0, read);
                            retryCount = 0;
                        }
                        else
                        {
                            retryCount++;
                            if (ms.Length > 0 && retryCount > 3) break;
                            await Task.Delay(10, ct).ConfigureAwait(false);
                        }
                    }

                    if (ms.Length == 0)
                        return OperateResult<byte[]>.Failed("MC-3E ASCII 响应为空");

                    byte[] binaryResponse = ParseAsciiResponse(ms.ToArray());
                    Log.Debug($"RX ← {DataConverter.ToHexString(binaryResponse)}");
                    RaiseMessageReceived(DataConverter.ToHexString(binaryResponse));

                    if (!_persistentMode) lock (_lock) DisconnectCore();

                    return OperateResult<byte[]>.Success(binaryResponse);
                }
            }
            catch (OperationCanceledException)
            {
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"MC-3E ASCII 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"MC-3E ASCII 通讯异常: {ex.Message}");
            }
        }

        // ── 批量读字 (Command=0x0401, SubCommand=0x0000) ──

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
                return OperateResult<byte[]>.Failed("MC-3E ASCII 响应长度不足");

            ushort completionCode = (ushort)((resp.Content[7] << 8) | resp.Content[8]);
            if (completionCode != 0x0000)
                return OperateResult<byte[]>.Failed($"PLC 错误码: 0x{completionCode:X4}");

            int dataLen = resp.Content.Length - 9;
            byte[] result = new byte[dataLen];
            Buffer.BlockCopy(resp.Content, 9, result, 0, dataLen);
            return OperateResult<byte[]>.Success(result);
        }

        // ── 批量写字 (Command=0x1401, SubCommand=0x0000) ──

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
                return OperateResult.Failed("MC-3E ASCII 写响应长度不足");

            ushort completionCode = (ushort)((resp.Content[7] << 8) | resp.Content[8]);
            if (completionCode != 0x0000)
                return OperateResult.Failed($"PLC 错误码: 0x{completionCode:X4}");

            return OperateResult.Success();
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
            var r = ReadWordsBatchAsync(subLabel, addr, 1, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[1] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatchAsync(subLabel, addr, 1, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatchAsync(subLabel, addr, 1, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatchAsync(subLabel, addr, 2, CancellationToken.None).GetAwaiter().GetResult();
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
            var r = ReadWordsBatchAsync(subLabel, addr, 4, CancellationToken.None).GetAwaiter().GetResult();
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
            var r = ReadWordsBatchAsync(subLabel, addr, 2, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 4);
            return OperateResult<float>.Success(DataConverter.ToFloat(ordered, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            var r = ReadWordsBatchAsync(subLabel, addr, 4, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            byte[] ordered = ApplyByteOrder(r.Content, 8);
            return OperateResult<double>.Success(DataConverter.ToDouble(ordered, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatchAsync(subLabel, addr, wordCount, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);
            var r = ReadWordsBatchAsync(subLabel, addr, wordCount, CancellationToken.None).GetAwaiter().GetResult();
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ── 写入 ──────────────────────────────

        public override OperateResult Write(string address, bool value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatchAsync(subLabel, addr, 1, DataConverter.GetBytes((short)(value ? 1 : 0)), CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, short value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatchAsync(subLabel, addr, 1, DataConverter.GetBytes(value), CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, ushort value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            return WriteWordsBatchAsync(subLabel, addr, 1, DataConverter.GetBytes(value), CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, int value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatchAsync(subLabel, addr, 2, ordered, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatchAsync(subLabel, addr, 4, ordered, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));

        public override OperateResult Write(string address, float value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 4);
            return WriteWordsBatchAsync(subLabel, addr, 2, ordered, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, double value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] bytes = DataConverter.GetBytes(value);
            byte[] ordered = ApplyByteOrder(bytes, 8);
            return WriteWordsBatchAsync(subLabel, addr, 4, ordered, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, string value)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            byte[] data = DataConverter.GetBytes(value);
            ushort wordCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteWordsBatchAsync(subLabel, addr, wordCount, data, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var (subLabel, addr) = Mc3EAddressParser.Parse(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            ushort wordCount = (ushort)(data.Length / 2);
            return WriteWordsBatchAsync(subLabel, addr, wordCount, data, CancellationToken.None).GetAwaiter().GetResult();
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
        public override Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
