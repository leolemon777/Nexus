using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Idec
{
    /// <summary>
    /// IDEC MicroSmart Computer Link（上位链接）TCP 客户端。
    /// </summary>
    /// <remarks>
    /// <para>基于公开手册 fc4a_protocol_im.pdf，实现 IDEC 自有的 ASCII 文本帧协议（非 Modbus）。</para>
    /// <para>帧格式（ASCII 模式，主推）：</para>
    /// <para>  请求 <c>[ENQ][站号 hex][命令 2][数据类型码 1][operand 6][count 2][BCC 2][CR]</c></para>
    /// <para>  成功响应 <c>[STX][站号][数据][ETX][BCC 2][CR]</c></para>
    /// <para>  失败响应 <c>[NAK][站号][错误码 1][BCC 2][CR]</c></para>
    /// <para>BCC = 站号到 BCC 前一字节的全部字节 XOR，表示为 2 字符 ASCII-HEX。</para>
    /// <para>继承 <see cref="TcpDeviceBase"/>，override <see cref="SendAndReceive(byte[])"/> 以读取 CR 结尾的响应行（参考 KeyenceNano 范式）。</para>
    /// <para>默认串口参数为 9600/Even/7/1；本客户端通过 TCP 透传访问（FC6A 以太网口或串口服务器）。</para>
    /// </remarks>
    public class IdecHostLinkClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>站号（0–15，映射到 1 位 hex char）。默认 0。</summary>
        public byte Station { get; set; }

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 0;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        /// <summary>
        /// 创建 IDEC Computer Link TCP 客户端。
        /// </summary>
        /// <param name="ip">PLC 或串口服务器 IP 地址。</param>
        /// <param name="port">TCP 端口（FC6A 以太网默认透传口，或串口服务器端口）。</param>
        /// <param name="station">站号（0–15，默认 0）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public IdecHostLinkClient(string ip, int port = 502, byte station = 0, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  SendAndReceive（ASCII 文本行）
        // ═══════════════════════════════════════════

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
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

                RaiseMessageSent(BytesToHex(request));

                ns.Write(request, 0, request.Length);

                byte[]? response = ReadUntilCr(ns);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取响应超时");

                RaiseMessageReceived(BytesToHex(response));

                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }

                return OperateResult<byte[]>.Success(response);
            }
            catch (Exception ex)
            {
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

        /// <summary>读取直到 CR (0x0D) 的响应帧（包含 STX/ETX/NAK/BCC）。返回完整原始 bytes。</summary>
        private byte[]? ReadUntilCr(NetworkStream ns)
        {
            var list = new List<byte>(64);
            int start = Environment.TickCount;

            while (unchecked(Environment.TickCount - start) <= Timeout)
            {
                int remaining = Timeout - unchecked(Environment.TickCount - start);
                if (remaining < 0) return null;
                int b = ReadByteWithTimeout(ns, remaining);
                if (b < 0) return null;
                list.Add((byte)b);
                if (b == IdecFrameControl.CR)
                    return list.ToArray();
            }
            return null;
        }

        private int ReadByteWithTimeout(NetworkStream ns, int timeoutMs)
        {
            if (!ns.CanRead || timeoutMs <= 0) return -1;
            if (ns.DataAvailable)
            {
                int n = ns.ReadByte();
                return n;
            }
            // 轮询等待数据到达
            int deadline = Environment.TickCount + timeoutMs;
            while (Environment.TickCount < deadline)
            {
                if (ns.DataAvailable)
                    return ns.ReadByte();
                Thread.Sleep(2);
            }
            return -1;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            IdecAddress? addr;
            if (!IdecAddress.TryParse(address, out addr) || addr == null)
                return OperateResult<byte[]>.Failed($"无法解析 IDEC 地址: {address}");

            char typeCode = IdecDataTypeCode.For(addr.Area);
            byte[] req = IdecFrame.BuildReadRequest(Station, IdecCommandType.ReadContinuous, typeCode, addr.Number, length);

            var result = SendAndReceive(req);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            IdecResponse resp = IdecFrame.ParseResponse(result.Content);
            if (!resp.IsSuccess)
                return OperateResult<byte[]>.Failed($"IDEC 错误码: {resp.ErrorCode}");
            if (!resp.BccValid)
                return OperateResult<byte[]>.Failed("IDEC 响应 BCC 校验失败");
            if (!resp.HasData)
                return OperateResult<byte[]>.Success(new byte[0]);

            // 字设备：数据是 ASCII-HEX（每 2 char = 1 byte）；位设备：每 1 char = 1 bit（'0'/'1'）
            try
            {
                if (addr.IsBitArea)
                {
                    byte[] bits = new byte[resp.Data.Length];
                    for (int i = 0; i < resp.Data.Length; i++)
                        bits[i] = (byte)(resp.Data[i] == '1' ? 1 : 0);
                    return OperateResult<byte[]>.Success(bits);
                }
                return OperateResult<byte[]>.Success(IdecFrame.HexToBytes(resp.Data));
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"解析数据失败: {ex.Message}");
            }
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");

            IdecAddress? addr;
            if (!IdecAddress.TryParse(address, out addr) || addr == null)
                return OperateResult.Failed($"无法解析 IDEC 地址: {address}");

            char typeCode = IdecDataTypeCode.For(addr.Area);
            string dataHex;
            ushort count;
            if (addr.IsBitArea)
            {
                // 位设备：每个 byte 视为 1 个 bit
                var sb = new StringBuilder(data.Length);
                for (int i = 0; i < data.Length; i++) sb.Append(data[i] != 0 ? '1' : '0');
                dataHex = sb.ToString();
                count = (ushort)data.Length;
            }
            else
            {
                dataHex = IdecFrame.BytesToHex(data);
                count = (ushort)(data.Length / 2);
            }

            byte[] req = IdecFrame.BuildWriteRequest(Station, IdecCommandType.WriteContinuous, typeCode, addr.Number, count, dataHex);

            var result = SendAndReceive(req);
            if (!result.IsSuccess) return result;

            IdecResponse resp = IdecFrame.ParseResponse(result.Content);
            if (!resp.IsSuccess)
                return OperateResult.Failed($"IDEC 错误码: {resp.ErrorCode}");
            if (!resp.BccValid)
                return OperateResult.Failed("IDEC 响应 BCC 校验失败");

            return OperateResult.Success();
        }

        // ── 高层 Read 方法 ──

        public override OperateResult<bool> ReadBool(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success(result.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(result.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }

        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => DataConverter.ToInt32(d, 0));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => DataConverter.ToUInt32(d, 0));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => DataConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => DataConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => DataConverter.ToFloat(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => DataConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message, result.ErrorCode);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        // ── 高层 Write 方法 ──

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 1 : 0) });
        public override OperateResult Write(string address, short value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

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

        // ═══════════════════════════════════════════
        //  心跳 / 辅助
        // ═══════════════════════════════════════════

        protected override byte[]? BuildHeartbeat()
        {
            try
            {
                return IdecFrame.BuildReadRequest(Station, IdecCommandType.ReadContinuous, 'D', 0, 1);
            }
            catch { return null; }
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }
    }
}
