using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.BrPowerlink
{
    /// <summary>
    /// B&amp;R POWERLINK SDO TCP 客户端 — 继承 <see cref="TcpDeviceBase"/>，复用连接管理。
    /// </summary>
    /// <remarks>
    /// <para><b>诚实定位</b>：本客户端实现 POWERLINK 的 <b>SDO（Service Data Object）请求-应答</b>
    /// 访问模式，用于读写对象字典（Object Dictionary）中的节点配置与参数。</para>
    /// <para>POWERLINK 完整的实时周期通信（MN↔CN 的 Preq/Pres 调度）<b>不在本库实现范围</b>。
    /// 本库采用 TCP 上的 SDO 封装（自定义简化封装，非 EPSG 标准实时帧），适合配置/参数读写，
    /// 不适合实时周期数据交换。</para>
    /// <para>地址格式: [node.]index.subindex（如 "1.6000.0"、"0x6000.0x01"）。</para>
    /// </remarks>
    public class BrPowerlinkClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>默认节点 ID（地址中未指定 node 时使用）。</summary>
        public byte DefaultNodeId { get; set; } = BrPowerlinkConstants.DefaultNodeId;

        private readonly BrPowerlinkAddressParser _parser = new BrPowerlinkAddressParser();

        /// <summary>
        /// 构造 POWERLINK SDO 客户端。
        /// </summary>
        /// <param name="ip">MN/网关 IP 地址。</param>
        /// <param name="port">TCP SDO 封装端口（默认 34962，自定义非标准端口）。</param>
        /// <param name="timeout">通讯超时（毫秒，默认 5000）。</param>
        public BrPowerlinkClient(string ip, int port = BrPowerlinkConstants.DefaultPort, int timeout = BrPowerlinkConstants.DefaultTimeout)
            : base(ip, port, timeout)
        {
        }

        /// <summary>默认心跳：读节点 1 的 0x1000（设备类型）对象 1 字节。</summary>
        protected override byte[]? BuildHeartbeat()
        {
            return BuildReadRequest(BrPowerlinkConstants.DefaultNodeId, BrPowerlinkConstants.OdDeviceType, 0, 1);
        }

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => BrPowerlinkConstants.ResponseHeaderLength; // error(4) + payloadLen(2)

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 6) return 0;
            // payloadLen 2 字节，大端
            return (header[4] << 8) | header[5];
        }

        // ═══════════════════════════════════════════
        //  帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建对象字典读取请求帧: [0x01][nodeId][index 2B][subIndex][size 2B]。</summary>
        public static byte[] BuildReadRequest(byte nodeId, ushort index, byte subIndex, uint size)
        {
            byte[] request = new byte[BrPowerlinkConstants.RequestHeaderLength];
            request[0] = BrPowerlinkConstants.CmdReadOd;
            request[1] = nodeId;
            request[2] = (byte)((index >> 8) & 0xFF);
            request[3] = (byte)(index & 0xFF);
            request[4] = subIndex;
            request[5] = (byte)((size >> 8) & 0xFF);
            request[6] = (byte)(size & 0xFF);
            return request;
        }

        /// <summary>构建对象字典写入请求帧: [0x02][nodeId][index 2B][subIndex][size 2B][data]。</summary>
        public static byte[] BuildWriteRequest(byte nodeId, ushort index, byte subIndex, byte[] data)
        {
            if (data == null) data = Array.Empty<byte>();
            ushort size = (ushort)data.Length;
            byte[] request = new byte[BrPowerlinkConstants.RequestHeaderLength + data.Length];
            request[0] = BrPowerlinkConstants.CmdWriteOd;
            request[1] = nodeId;
            request[2] = (byte)((index >> 8) & 0xFF);
            request[3] = (byte)(index & 0xFF);
            request[4] = subIndex;
            request[5] = (byte)((size >> 8) & 0xFF);
            request[6] = (byte)(size & 0xFF);
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, request, BrPowerlinkConstants.RequestHeaderLength, data.Length);
            return request;
        }

        // ═══════════════════════════════════════════
        //  核心 OD 读写
        // ═══════════════════════════════════════════

        /// <summary>读取对象字典条目，返回原始 payload bytes。</summary>
        private OperateResult<byte[]> ReadOd(byte nodeId, ushort index, byte subIndex, uint size)
        {
            try
            {
                byte[] request = BuildReadRequest(nodeId, index, subIndex, size);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

                byte[] response = result.Content;
                if (response == null || response.Length < 6)
                    return OperateResult<byte[]>.Failed("POWERLINK SDO 响应长度不足");

                uint error = ((uint)response[0] << 24) | ((uint)response[1] << 16) | ((uint)response[2] << 8) | response[3];
                if (error != BrPowerlinkConstants.ErrorNone)
                    return OperateResult<byte[]>.Failed($"POWERLINK SDO 错误: 0x{error:X8} ({BrPowerlinkError.GetMessage(error)})");

                int payloadLen = (response[4] << 8) | response[5];
                int available = response.Length - 6;
                if (payloadLen > available) payloadLen = available; // 防御性截断
                byte[] data = new byte[payloadLen];
                if (payloadLen > 0)
                    Buffer.BlockCopy(response, 6, data, 0, payloadLen);
                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        /// <summary>写入对象字典条目。</summary>
        private OperateResult WriteOd(byte nodeId, ushort index, byte subIndex, byte[] data)
        {
            try
            {
                byte[] request = BuildWriteRequest(nodeId, index, subIndex, data ?? Array.Empty<byte>());

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);

                byte[] response = result.Content;
                if (response == null || response.Length < 6)
                    return OperateResult.Failed("POWERLINK SDO 写入响应长度不足");

                uint error = ((uint)response[0] << 24) | ((uint)response[1] << 16) | ((uint)response[2] << 8) | response[3];
                if (error != BrPowerlinkConstants.ErrorNone)
                    return OperateResult.Failed($"POWERLINK SDO 错误: 0x{error:X8} ({BrPowerlinkError.GetMessage(error)})");

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        private BrPowerlinkAddress ParseAddr(string address)
        {
            var addr = _parser.Parse(address);
            return new BrPowerlinkAddress(addr.Original, addr.Index, addr.SubIndex, DefaultNodeId);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice — 基础读写
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddr(address);
            // OD 读返回的是原始 bytes，length 为请求读取的字节数
            return ReadOd(addr.NodeId, addr.Index, addr.SubIndex, length);
        }

        /// <inheritdoc/>
        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");
            var addr = ParseAddr(address);
            return WriteOd(addr.NodeId, addr.Index, addr.SubIndex, data);
        }

        // ── 高层读方法 ──

        /// <inheritdoc/>
        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && (r.Content[0] & 0x01) != 0);
        }

        /// <inheritdoc/>
        public override OperateResult<short> ReadInt16(string address)
            => ReadValueSafe<short>(address, 2, d => DataConverter.ToInt16(d, 0));

        /// <inheritdoc/>
        public override OperateResult<ushort> ReadUInt16(string address)
            => ReadValueSafe<ushort>(address, 2, d => DataConverter.ToUInt16(d, 0));

        /// <inheritdoc/>
        public override OperateResult<int> ReadInt32(string address)
            => ReadValueSafe<int>(address, 4, d => DataConverter.ToInt32(d, 0));

        /// <inheritdoc/>
        public override OperateResult<uint> ReadUInt32(string address)
            => ReadValueSafe<uint>(address, 4, d => DataConverter.ToUInt32(d, 0));

        /// <inheritdoc/>
        public override OperateResult<long> ReadInt64(string address)
            => ReadValueSafe<long>(address, 8, d => DataConverter.ToInt64(d, 0));

        /// <inheritdoc/>
        public override OperateResult<ulong> ReadUInt64(string address)
            => ReadValueSafe<ulong>(address, 8, d => DataConverter.ToUInt64(d, 0));

        /// <inheritdoc/>
        public override OperateResult<float> ReadFloat(string address)
            => ReadValueSafe<float>(address, 4, d => DataConverter.ToFloat(d, 0));

        /// <inheritdoc/>
        public override OperateResult<double> ReadDouble(string address)
            => ReadValueSafe<double>(address, 8, d => DataConverter.ToDouble(d, 0));

        /// <inheritdoc/>
        public override OperateResult<string> ReadString(string address, ushort length)
            => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        // ── 高层写方法 ──

        /// <inheritdoc/>
        public override OperateResult Write(string address, bool value)
            => Write(address, new byte[] { (byte)(value ? 0x01 : 0x00) });

        /// <inheritdoc/>
        public override OperateResult Write(string address, short value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, ushort value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, int value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, uint value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, long value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, ulong value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, float value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, double value)
            => Write(address, DataConverter.GetBytes(value));

        /// <inheritdoc/>
        public override OperateResult Write(string address, string value)
            => Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));

        /// <summary>
        /// 安全读取并通过转换函数得到目标类型。
        /// </summary>
        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message, result.ErrorCode);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值（返回地址→值的字典）。</summary>
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

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
