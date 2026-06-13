using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 FX 编程口协议 over TCP 客户端 — 将 FX 串口帧封装在 TCP 连接上。
    /// <para>适用于 FX3U/FX5U 等通过以太网适配器或串口服务器暴露编程口协议的场景。</para>
    /// <para>帧格式: ENQ(0x05) → ACK(0x06) → STX(0x02) + Cmd(1) + Device(1) + Addr(4) + Data(N) + ETX(0x03) + SUM(2hex)</para>
    /// <para>地址格式: D100, M100, X0, Y10, S100, T100, C100</para>
    /// </summary>
    public class MelsecFxSerialOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        private readonly object _fxLock = new object();

        /// <summary>默认 TCP 端口（FX 以太网适配器常用 5551）。</summary>
        public const int DefaultPort = 5551;

        public MelsecFxSerialOverTcpClient(string ip, int port = DefaultPort, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ── TcpDeviceBase 抽象实现 ───────────────

        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  FX 帧收发（TCP 通道）
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendFxCommand(byte[] commandFrame)
        {
            lock (_fxLock)
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message);
                }

                try
                {
                    // 1. 发送 ENQ
                    _stream!.Write(new byte[] { 0x05 }, 0, 1);

                    // 2. 等待 ACK
                    byte[] ackBuf = new byte[1];
                    int read = _stream.Read(ackBuf, 0, 1);
                    if (read < 1) return OperateResult<byte[]>.Failed("等待 FX ACK 超时");
                    if (ackBuf[0] == 0x15) return OperateResult<byte[]>.Failed("FX PLC 返回 NAK");
                    if (ackBuf[0] != 0x06) return OperateResult<byte[]>.Failed($"FX 握手失败: 0x{ackBuf[0]:X2}");

                    // 3. 发送命令帧
                    _stream.Write(commandFrame, 0, commandFrame.Length);

                    // 4. 读取响应头
                    byte[] respHeader = new byte[1];
                    read = _stream.Read(respHeader, 0, 1);
                    if (read < 1) return OperateResult<byte[]>.Failed("读取 FX 响应头超时");
                    if (respHeader[0] == 0x06) return OperateResult<byte[]>.Success(Array.Empty<byte>());
                    if (respHeader[0] != 0x02) return OperateResult<byte[]>.Failed($"FX 响应格式错误: 0x{respHeader[0]:X2}");

                    // 5. 读取 STX ... ETX + SUM
                    using var ms = new System.IO.MemoryStream();
                    ms.WriteByte(0x02);
                    bool etxFound = false;
                    while (!etxFound)
                    {
                        byte[] buf = new byte[1];
                        read = _stream.Read(buf, 0, 1);
                        if (read < 1) return OperateResult<byte[]>.Failed("读取 FX 响应数据超时");
                        ms.WriteByte(buf[0]);
                        if (buf[0] == 0x03) etxFound = true;
                    }
                    byte[] sumBuf = new byte[2];
                    read = _stream.Read(sumBuf, 0, 2);
                    if (read < 2) return OperateResult<byte[]>.Failed("读取 FX SUM 校验和超时");
                    ms.Write(sumBuf, 0, 2);

                    // 6. 校验
                    if (!FxFrameBuilder.VerifyResponse(ms.ToArray(), out byte[] data))
                        return OperateResult<byte[]>.Failed("FX 响应 SUM 校验失败");

                    return OperateResult<byte[]>.Success(data);
                }
                catch (Exception ex)
                {
                    return OperateResult<byte[]>.Failed($"FX TCP 通讯异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  地址解析
        // ═══════════════════════════════════════════

        private static readonly System.Text.RegularExpressions.Regex _fxAddrRegex =
            new System.Text.RegularExpressions.Regex(@"^([DMXYTSRC])(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private class FxAddress { public char DeviceCode; public int Address; }

        private static FxAddress ParseAddress(string address)
        {
            var match = _fxAddrRegex.Match(address.ToUpper());
            if (!match.Success) throw new ArgumentException($"无效的 FX 地址格式: {address}");
            return new FxAddress { DeviceCode = match.Groups[1].Value[0], Address = int.Parse(match.Groups[2].Value) };
        }

        // ═══════════════════════════════════════════
        //  标准类型读取
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 1));
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 2
                ? OperateResult<short>.Success((short)((result.Content[1] << 8) | result.Content[0]))
                : OperateResult<short>.Failed("FX 读取响应数据不足");
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 2));
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 4
                ? OperateResult<int>.Success((result.Content[3] << 24) | (result.Content[2] << 16) | (result.Content[1] << 8) | result.Content[0])
                : OperateResult<int>.Failed("FX 读取响应数据不足");
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 4));
            if (!result.IsSuccess) return OperateResult<long>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < 8) return OperateResult<long>.Failed("FX 读取长整型响应数据不足");
            return OperateResult<long>.Success(BitConverter.ToInt64(result.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(r.Content), 0)) : OperateResult<float>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0)) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, 1));
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return result.Content.Length >= 1
                ? OperateResult<bool>.Success((result.Content[0] & 0x01) != 0)
                : OperateResult<bool>.Failed("FX 读取 Bool 响应数据不足");
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = ParseAddress(address);
            int words = (length + 1) / 2;
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, words));
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < length) return OperateResult<string>.Failed("FX 读取字符串响应数据不足");
            return OperateResult<string>.Success(Encoding.ASCII.GetString(result.Content, 0, length).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddress(address);
            int words = (length + 1) / 2;
            var result = SendFxCommand(FxFrameBuilder.BuildReadCommand(addr.DeviceCode, addr.Address, words));
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length < length) return OperateResult<byte[]>.Failed("FX 读取字节响应数据不足");
            byte[] data = new byte[length];
            Buffer.BlockCopy(result.Content, 0, data, 0, length);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  标准类型写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, new byte[] { (byte)(value ? 1 : 0), 0x00 }));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, new byte[] { (byte)(value & 0xFF), (byte)(value >> 8) }));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = ParseAddress(address);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, BitConverter.GetBytes(value)));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = ParseAddress(address);
            byte[] data = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value & 0xFFFFFFFF)), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((int)(value >> 32)), 0, data, 4, 4);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, ulong value) => Write(address, unchecked((long)value));

        public override OperateResult Write(string address, float value) => Write(address, BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, double value) => Write(address, BitConverter.ToInt64(BitConverter.GetBytes(value), 0));

        public override OperateResult Write(string address, string value)
        {
            if (value == null) return OperateResult.Failed("写入字符串不能为空");
            var addr = ParseAddress(address);
            byte[] data = Encoding.ASCII.GetBytes(value);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var addr = ParseAddress(address);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            var result = SendFxCommand(FxFrameBuilder.BuildWriteCommand(addr.DeviceCode, addr.Address, data));
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
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

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = new List<string>(addresses);
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
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

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        protected override byte[]? BuildHeartbeat()
        {
            try { return FxFrameBuilder.BuildReadCommand('D', 0, 1); }
            catch { return null; }
        }

        public override string ToString() => $"MelsecFxSerialOverTcpClient[{Ip}:{Port}]";
    }
}
