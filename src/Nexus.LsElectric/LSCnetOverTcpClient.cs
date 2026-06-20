using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.LsElectric
{
    /// <summary>
    /// LS Electric Cnet 协议 TCP 客户端 — 将 Cnet 串口帧封装在 TCP 传输中。
    /// <para>Cnet 帧格式 (ASCII): ENQ + Station(2) + PC(2) + Cmd(2) + Area(2) + Addr(4) + Count(4) + ETX + BCC(2)</para>
    /// <para>继承 TcpDeviceBase，复用连接管理。</para>
    /// </summary>
    public class LSCnetOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>站号 (0-255)。</summary>
        public byte Station { get; set; }

        /// <summary>PC 号 (默认 0xFF)。</summary>
        public byte PcNumber { get; set; } = 0xFF;

        public LSCnetOverTcpClient(string ip, int port = LSCnetConstants.DefaultPort, byte station = 0)
            : base(ip, port)
        {
            Station = station;
        }

        /// <summary>默认心跳：读 D0 的 1 个 word。</summary>
        protected override byte[]? BuildHeartbeat()
        {
            return BuildReadRequest(new LSCnetAddress(0x44, 0, LSCnetArea.DataRegister, false), 1);
        }

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 1; // STX

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            // Cnet 响应长度不确定，返回 0 让基类只读 header
            // 实际收发通过自定义方法完成
            return 0;
        }

        // ═══════════════════════════════════════════
        //  Cnet 帧构建（与 LSCnetSerialClient 相同）
        // ═══════════════════════════════════════════

        private static byte ComputeBcc(byte[] data, int offset, int length)
        {
            byte bcc = 0;
            for (int i = offset; i < offset + length; i++)
                bcc ^= data[i];
            return bcc;
        }

        private static void AppendHexByte(byte value, byte[] dest, int offset)
        {
            dest[offset] = (byte)ToHexChar((value >> 4) & 0x0F);
            dest[offset + 1] = (byte)ToHexChar(value & 0x0F);
        }

        private static byte ParseHexByte(byte[] src, int offset)
        {
            return (byte)((FromHexChar(src[offset]) << 4) | FromHexChar(src[offset + 1]));
        }

        private static char ToHexChar(int value)
        {
            return (char)(value < 10 ? '0' + value : 'A' + value - 10);
        }

        private static int FromHexChar(byte c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return 0;
        }

        /// <summary>构建 Cnet 读取请求帧。</summary>
        private byte[] BuildReadRequest(LSCnetAddress address, ushort count)
        {
            byte[] frame = new byte[20];
            int i = 0;

            frame[i++] = LSCnetConstants.ENQ;
            AppendHexByte(Station, frame, i); i += 2;
            AppendHexByte(PcNumber, frame, i); i += 2;
            frame[i++] = (byte)'R'; frame[i++] = (byte)'D';
            AppendHexByte(address.AreaCode, frame, i); i += 2;
            frame[i++] = (byte)ToHexChar((address.Offset >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(address.Offset & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(count & 0x0F);
            frame[i++] = LSCnetConstants.ETX;

            byte bcc = ComputeBcc(frame, 1, 18);
            AppendHexByte(bcc, frame, i);

            return frame;
        }

        /// <summary>构建 Cnet 写入请求帧。</summary>
        private byte[] BuildWriteRequest(LSCnetAddress address, ushort count, byte[] data)
        {
            int dataHexLen = data.Length * 4;
            int frameLen = 17 + dataHexLen + 3;
            byte[] frame = new byte[frameLen];
            int i = 0;

            frame[i++] = LSCnetConstants.ENQ;
            AppendHexByte(Station, frame, i); i += 2;
            AppendHexByte(PcNumber, frame, i); i += 2;
            frame[i++] = (byte)'W'; frame[i++] = (byte)'R';
            AppendHexByte(address.AreaCode, frame, i); i += 2;
            frame[i++] = (byte)ToHexChar((address.Offset >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((address.Offset >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(address.Offset & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 12) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 8) & 0x0F);
            frame[i++] = (byte)ToHexChar((count >> 4) & 0x0F);
            frame[i++] = (byte)ToHexChar(count & 0x0F);

            for (int d = 0; d < data.Length; d++)
            {
                AppendHexByte(data[d], frame, i);
                i += 2;
                frame[i++] = (byte)'0'; frame[i++] = (byte)'0';
            }

            frame[i++] = LSCnetConstants.ETX;

            byte bcc = ComputeBcc(frame, 1, frameLen - 3);
            AppendHexByte(bcc, frame, i);

            return frame;
        }

        // ═══════════════════════════════════════════
        //  Cnet 帧收发（重写基类方法）
        // ═══════════════════════════════════════════

        /// <summary>
        /// 重写 SendAndReceive 以处理 Cnet 协议的变长响应。
        /// </summary>
        private OperateResult<byte[]> SendAndReceiveCnet(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                _asyncLock.Wait();
                try { ns = _stream; }
                finally { _asyncLock.Release(); }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"CNET TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                // 读取 STX
                byte[]? stx = ReadExact(ns, 1);
                if (stx == null) return OperateResult<byte[]>.Failed("读取 STX 超时");

                if (stx[0] == LSCnetConstants.NAK)
                    return OperateResult<byte[]>.Failed("Cnet NAK 响应");

                if (stx[0] != LSCnetConstants.STX)
                    return OperateResult<byte[]>.Failed($"Cnet 响应异常: 0x{stx[0]:X2}");

                // 读取 Station(2) + PC(2) + Cmd(2) = 6 字节
                byte[]? header = ReadExact(ns, 6);
                if (header == null) return OperateResult<byte[]>.Failed("读取响应头失败");

                // 读取数据直到 ETX
                var dataBytes = new List<byte>();
                int start = Environment.TickCount;
                while (unchecked(Environment.TickCount - start) <= Timeout)
                {
                    byte[]? b = ReadExact(ns, 1);
                    if (b == null) return OperateResult<byte[]>.Failed("读取数据超时");

                    if (b[0] == LSCnetConstants.ETX)
                    {
                        // 读取 BCC(2)
                        byte[]? bccBytes = ReadExact(ns, 2);
                        if (bccBytes == null) return OperateResult<byte[]>.Failed("读取 BCC 失败");

                        // 验证 BCC
                        int checkLen = 6 + dataBytes.Count + 1;
                        byte[] checkData = new byte[checkLen];
                        Buffer.BlockCopy(header, 0, checkData, 0, 6);
                        for (int j = 0; j < dataBytes.Count; j++)
                            checkData[6 + j] = dataBytes[j];
                        checkData[checkLen - 1] = LSCnetConstants.ETX;

                        byte expectedBcc = ComputeBcc(checkData, 0, checkLen);
                        byte actualBcc = ParseHexByte(bccBytes, 0);

                        if (expectedBcc != actualBcc)
                            return OperateResult<byte[]>.Failed($"BCC 校验失败: 期望 0x{expectedBcc:X2} 实际 0x{actualBcc:X2}");

                        // 检查错误响应
                        byte respCmd = header[4];
                        if (respCmd == (byte)'E' || respCmd == (byte)'e')
                        {
                            string errCode = dataBytes.Count >= 2
                                ? Encoding.ASCII.GetString(new[] { dataBytes[0], dataBytes[1] })
                                : "??";
                            return OperateResult<byte[]>.Failed($"Cnet 错误响应: {errCode}");
                        }

                        byte[] result = dataBytes.ToArray();
                        Log.Debug($"CNET RX ← {DataConverter.ToHexString(result)}");
                        RaiseMessageReceived(DataConverter.ToHexString(result));

                        if (!_persistentMode)
                        {
                            _asyncLock.Wait();
                            try { DisconnectCore(); }
                            finally { _asyncLock.Release(); }
                        }

                        return OperateResult<byte[]>.Success(result);
                    }
                    dataBytes.Add(b[0]);
                }

                return OperateResult<byte[]>.Failed("读取响应超时");
            }
            catch (Exception ex)
            {
                Log.Error($"Cnet 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private byte[]? ReadExact(NetworkStream ns, int count)
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

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegisters(LSCnetAddress address, ushort count)
        {
            byte[] request = BuildReadRequest(address, count);
            var result = SendAndReceiveCnet(request);
            if (!result.IsSuccess) return result;
            return OperateResult<byte[]>.Success(result.Content);
        }

        private OperateResult WriteRegisters(LSCnetAddress address, byte[] data)
        {
            int count = data.Length / 2;
            byte[] request = BuildWriteRequest(address, (ushort)count, data);
            var result = SendAndReceiveCnet(request);
            if (!result.IsSuccess) return result;
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 1) return OperateResult<bool>.Failed("响应数据不足");
            return OperateResult<bool>.Success((r.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足");
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足");
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足");
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 2);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var parsed = LSCnetAddress.Parse(address);
            var r = ReadRegisters(parsed, 4);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<double>.Failed("响应数据不足");
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var parsed = LSCnetAddress.Parse(address);
            int regCount = (length + 1) / 2;
            var r = ReadRegisters(parsed, (ushort)regCount);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Array.Copy(r.Content, data, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, bool value)
        {
            var parsed = LSCnetAddress.Parse(address);
            byte[] data = new byte[] { (byte)(value ? 1 : 0), 0 };
            return WriteRegisters(parsed, data);
        }

        public override OperateResult Write(string address, short value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)(long)value);

        public override OperateResult Write(string address, float value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, double value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, DataConverter.GetBytes(value));
        }

        public override OperateResult Write(string address, string value)
        {
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, Encoding.ASCII.GetBytes(value ?? ""));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var parsed = LSCnetAddress.Parse(address);
            return WriteRegisters(parsed, data);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
        // ═══════════════════════════════════════════

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

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

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

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

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
            => Task.FromResult(BatchWrite(items));
    }
}
