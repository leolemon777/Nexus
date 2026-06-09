using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    public class FxSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        private readonly object _fxLock = new object();

        public FxSerialClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }

        // FX 使用自定义 ENQ/ACK/STX+ETX 帧协议，不走 SendAndReceive 基类路径
        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        protected async Task<OperateResult<byte[]>> SendFxAsync(byte[] commandFrame, CancellationToken ct)
        {
            lock (_fxLock) { if (!Port.IsOpen) return OperateResult<byte[]>.Failed("串口未打开"); Port.Write(new byte[] { 0x05 }, 0, 1); }
            byte[] ackBuf = new byte[1];
            if (await ReadExactSerialAsync(ackBuf, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("等待 FX ACK 超时");
            if (ackBuf[0] == 0x15) return OperateResult<byte[]>.Failed("FX PLC 返回 NAK");
            if (ackBuf[0] != 0x06) return OperateResult<byte[]>.Failed($"FX 握手失败: 0x{ackBuf[0]:X2}");

            lock (_fxLock) { Port.Write(commandFrame, 0, commandFrame.Length); }
            byte[] respHeader = new byte[1];
            if (await ReadExactSerialAsync(respHeader, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("读取 FX 响应头超时");
            if (respHeader[0] == 0x06) return OperateResult<byte[]>.Success(Array.Empty<byte>());
            if (respHeader[0] != 0x02) return OperateResult<byte[]>.Failed($"FX 响应格式错误: 0x{respHeader[0]:X2}");

            using var ms = new System.IO.MemoryStream();
            ms.WriteByte(0x02);
            bool etxFound = false;
            while (!etxFound)
            {
                byte[] buf = new byte[1];
                if (await ReadExactSerialAsync(buf, 0, 1, ct).ConfigureAwait(false) < 1) return OperateResult<byte[]>.Failed("读取 FX 响应数据超时");
                ms.WriteByte(buf[0]);
                if (buf[0] == 0x03) etxFound = true;
            }
            byte[] sumBuf = new byte[2];
            if (await ReadExactSerialAsync(sumBuf, 0, 2, ct).ConfigureAwait(false) < 2) return OperateResult<byte[]>.Failed("读取 FX SUM 校验和超时");
            ms.Write(sumBuf, 0, 2);

            if (!FxFrameBuilder.VerifyResponse(ms.ToArray(), out byte[] data)) return OperateResult<byte[]>.Failed("FX 响应 SUM 校验失败");
            return OperateResult<byte[]>.Success(data);
        }

        private async Task<int> ReadExactSerialAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            using var timeoutToken = new CancellationTokenSource(Timeout);
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutToken.Token);
            while (totalRead < count)
            {
                try { int read = await Task.Run(() => Port.Read(buffer, offset + totalRead, count - totalRead), linkedToken.Token).ConfigureAwait(false); if (read == 0) return totalRead; totalRead += read; }
                catch (OperationCanceledException) { return totalRead; }
            }
            return totalRead;
        }

        private static readonly Regex _fxAddrRegex = new Regex(@"^([DMXYTS])(\d+)$", RegexOptions.IgnoreCase);
        private class FxAddress { public char DeviceCode; public int Address; }
        private static FxAddress ParseAddress(string address)
        {
            var match = _fxAddrRegex.Match(address.ToUpper());
            if (!match.Success) throw new ArgumentException($"无效的 FX 地址格式: {address}");
            return new FxAddress { DeviceCode = match.Groups[1].Value[0], Address = int.Parse(match.Groups[2].Value) };
        }

        public async Task<OperateResult<short>> ReadInt16Async(string address, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 1), ct).ConfigureAwait(false);
            return result.IsSuccess && result.Content.Length >= 2 ? OperateResult<short>.Success((short)((result.Content[1] << 8) | result.Content[0])) : OperateResult<short>.Failed("FX 读取响应数据不足");
        }
        public override OperateResult<short> ReadInt16(string address) => ReadInt16Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<int>> ReadInt32Async(string address, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 2), ct).ConfigureAwait(false);
            return result.IsSuccess && result.Content.Length >= 4 ? OperateResult<int>.Success((result.Content[3] << 24) | (result.Content[2] << 16) | (result.Content[1] << 8) | result.Content[0]) : OperateResult<int>.Failed("FX 读取响应数据不足");
        }
        public override OperateResult<int> ReadInt32(string address) => ReadInt32Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<float>> ReadFloatAsync(string address, CancellationToken ct = default)
        {
            var r = await ReadInt32Async(address, ct).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult<float> ReadFloat(string address) => ReadFloatAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<string>> ReadStringAsync(string address, ushort length, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            int words = (length + 1) / 2;
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, words), ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Content.Length < 2) return OperateResult<string>.Failed("FX 读取字符串响应数据不足");
            return OperateResult<string>.Success(Encoding.ASCII.GetString(result.Content, 0, Math.Min(length, result.Content.Length)).TrimEnd('\0'));
        }
        public override OperateResult<string> ReadString(string address, ushort length) => ReadStringAsync(address, length, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            int words = (length + 1) / 2;
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, words), ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Content.Length < 2) return OperateResult<byte[]>.Failed("FX 读取字节响应数据不足");
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 0, data, 0, Math.Min(length, result.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => ReadBytesAsync(address, length, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, short value, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, new byte[] { (byte)(value & 0xFF), (byte)(value >> 8) }), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, short value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, int value, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, BitConverter.GetBytes(value)), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, int value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public async Task<OperateResult> WriteAsync(string address, string value, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            byte[] data = Encoding.ASCII.GetBytes(value);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, string value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<OperateResult> WriteAsync(string address, byte[] data, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, byte[] data) => WriteAsync(address, data, CancellationToken.None).GetAwaiter().GetResult();

        // ── 补全类型读取 ──────────────────────────

        public async Task<OperateResult<bool>> ReadBoolAsync(string address, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 1), ct).ConfigureAwait(false);
            return result.IsSuccess && result.Content.Length >= 1 ? OperateResult<bool>.Success((result.Content[0] & 0x01) != 0) : OperateResult<bool>.Failed("FX 读取 Bool 响应数据不足");
        }
        public override OperateResult<bool> ReadBool(string address) => ReadBoolAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<long>> ReadInt64Async(string address, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 4), ct).ConfigureAwait(false);
            if (!result.IsSuccess || result.Content.Length < 8) return OperateResult<long>.Failed("FX 读取长整型响应数据不足");
            return OperateResult<long>.Success(
                (long)result.Content[4] << 56 | (long)result.Content[5] << 48 |
                (long)result.Content[6] << 40 | (long)result.Content[7] << 32 |
                (long)result.Content[0] << 24 | (long)result.Content[1] << 16 |
                (long)result.Content[2] << 8  | (long)result.Content[3]);
        }
        public override OperateResult<long> ReadInt64(string address) => ReadInt64Async(address, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public async Task<OperateResult<double>> ReadDoubleAsync(string address, CancellationToken ct = default)
        {
            var r = await ReadInt64Async(address, ct).ConfigureAwait(false);
            return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0)) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }
        public override OperateResult<double> ReadDouble(string address) => ReadDoubleAsync(address, CancellationToken.None).GetAwaiter().GetResult();

        // ── 补全类型写入 ──────────────────────────

        public async Task<OperateResult> WriteAsync(string address, bool value, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, new byte[] { (byte)(value ? 1 : 0), 0x00 }), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, bool value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public async Task<OperateResult> WriteAsync(string address, long value, CancellationToken ct = default)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value & 0xFFFFFFFF)), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value >> 32)), 0, data, 4, 4);
            var result = await SendFxAsync(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data), ct).ConfigureAwait(false);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }
        public override OperateResult Write(string address, long value) => WriteAsync(address, value, CancellationToken.None).GetAwaiter().GetResult();

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        // ── 异步覆写（补全类型）──────────────────

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));

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
