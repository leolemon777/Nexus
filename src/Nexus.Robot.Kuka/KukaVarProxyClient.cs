using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Kuka
{
    /// <summary>
    /// KUKA 机器人 KUKAVARPROXY 通讯客户端。
    /// <para>基于 KRC4 控制器运行的 KUKAVARPROXY 第三方软件。</para>
    /// <para>默认端口 7000。</para>
    /// <para>帧格式: Id(2 BE) + Len(2 BE) + Core，其中 Core: Func(1) + Len(2) + Name + [Len(2) + Value]</para>
    /// </summary>
    public class KukaVarProxyClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        protected override int ResponseHeaderLength => 4; // Id(2) + Len(2)

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 4) return 0;
            // 响应头: Id(2) + DataLen(2)
            return BitConverter.ToUInt16(header, 2);
        }

        private ushort _requestId;
        private readonly object _idLock = new object();

        // ── 构造 ────────────────────────────────

        /// <summary>
        /// 创建 KUKA VarProxy 客户端。
        /// </summary>
        /// <param name="ip">KRC4 控制器 IP 地址。</param>
        /// <param name="port">端口号，默认 7000。</param>
        /// <param name="timeout">超时时间（毫秒），默认 5000。</param>
        public KukaVarProxyClient(string ip, int port = 7000, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  读取
        // ═══════════════════════════════════════════

        /// <summary>
        /// 根据变量名读取原始字节数据。
        /// </summary>
        public OperateResult<byte[]> Read(string address)
        {
            var cmd = BuildReadCore(address);
            var pack = PackCommand(cmd);
            var recv = SendAndReceive(pack);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);
            return ExtractActualData(recv.Content);
        }

        /// <summary>
        /// 根据变量名读取字符串数据。
        /// </summary>
        public OperateResult<string> ReadString(string address)
        {
            var r = Read(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.Default.GetString(r.Content));
        }

        // ═══════════════════════════════════════════
        //  写入
        // ═══════════════════════════════════════════

        /// <summary>
        /// 写入字符串到变量。
        /// </summary>
        public override OperateResult Write(string address, string value)
        {
            var cmd = BuildWriteCore(address, value);
            var pack = PackCommand(cmd);
            var recv = SendAndReceive(pack);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);
            var extracted = ExtractActualData(recv.Content);
            if (!extracted.IsSuccess) return OperateResult.Failed(extracted.Message);
            return OperateResult.Success();
        }

        /// <summary>
        /// 写入原始字节到变量。
        /// </summary>
        public override OperateResult Write(string address, byte[] value)
        {
            return Write(address, Encoding.Default.GetString(value));
        }

        // ── IReadWriteDevice 类型化读写 ────────────

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = Read(address);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            byte[] data = r.Content;
            if (data.Length > length)
            {
                byte[] trimmed = new byte[length];
                Buffer.BlockCopy(data, 0, trimmed, 0, length);
                data = trimmed;
            }
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            return r.IsSuccess ? OperateResult<bool>.Success(r.Content[0] != 0) : OperateResult<bool>.Failed(r.Message);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            return r.IsSuccess ? OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0)) : OperateResult<short>.Failed(r.Message);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 2);
            return r.IsSuccess ? OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0)) : OperateResult<ushort>.Failed(r.Message);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0)) : OperateResult<int>.Failed(r.Message);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<uint>.Success(DataConverter.ToUInt32(r.Content, 0)) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0)) : OperateResult<long>.Failed(r.Message);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<ulong>.Success(DataConverter.ToUInt64(r.Content, 0)) : OperateResult<ulong>.Failed(r.Message);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            return r.IsSuccess ? OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0)) : OperateResult<float>.Failed(r.Message);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            return r.IsSuccess ? OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0)) : OperateResult<double>.Failed(r.Message);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = Read(address);
            return r.IsSuccess ? OperateResult<string>.Success(Encoding.Default.GetString(r.Content)) : OperateResult<string>.Failed(r.Message);
        }

        public override OperateResult Write(string address, bool value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, short value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, ushort value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, int value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, uint value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, long value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, ulong value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, float value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, double value)
            => Write(address, DataConverter.GetBytes(value));

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        /// <summary>构建读取核心命令。</summary>
        public static byte[] BuildReadCore(string address)
        {
            return BuildCore(0x00, new string[] { address });
        }

        /// <summary>构建写入核心命令。</summary>
        public static byte[] BuildWriteCore(string address, string value)
        {
            return BuildCore(0x01, new string[] { address, value });
        }

        /// <summary>打包命令: Id(2) + Len(2) + Core。</summary>
        public byte[] PackCommand(byte[] core)
        {
            ushort id;
            lock (_idLock) { id = _requestId++; }

            byte[] result = new byte[4 + core.Length];
            result[0] = (byte)(id >> 8);
            result[1] = (byte)(id & 0xFF);
            ushort coreLen = (ushort)core.Length;
            result[2] = (byte)(coreLen >> 8);
            result[3] = (byte)(coreLen & 0xFF);
            Array.Copy(core, 0, result, 4, core.Length);
            return result;
        }

        /// <summary>从响应中提取实际数据。</summary>
        public static OperateResult<byte[]> ExtractActualData(byte[] response)
        {
            try
            {
                if (response == null || response.Length < 4)
                    return OperateResult<byte[]>.Failed("响应数据过短");

                // 跳过 4 字节头 (Id + Len)，检查结果标志
                if (response.Length > 4 && response[response.Length - 1] != 1)
                    return OperateResult<byte[]>.Failed($"KUKA VarProxy 错误码: {response[response.Length - 1]}");

                // 响应格式: Id(2) + DataLen(2) + Func(1) + NameLen(2) + Name + ValueLen(2) + Value + Status(1)
                // 实际数据从 offset 7 开始
                if (response.Length < 7)
                    return OperateResult<byte[]>.Success(new byte[0]);

                // 读取 name 后面的 value
                int offset = 4; // skip header
                byte func = response[offset++]; // func
                int nameLen = (response[offset] << 8) | response[offset + 1];
                offset += 2 + nameLen; // skip name

                if (offset + 2 > response.Length)
                    return OperateResult<byte[]>.Success(new byte[0]);

                int valueLen = (response[offset] << 8) | response[offset + 1];
                offset += 2;

                if (offset + valueLen > response.Length)
                    return OperateResult<byte[]>.Success(new byte[0]);

                byte[] data = new byte[valueLen];
                Array.Copy(response, offset, data, 0, valueLen);
                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"解析响应失败: {ex.Message}");
            }
        }

        private static byte[] BuildCore(byte func, string[] args)
        {
            var list = new System.Collections.Generic.List<byte>();
            list.Add(func);
            for (int i = 0; i < args.Length; i++)
            {
                byte[] bytes = Encoding.Default.GetBytes(args[i] ?? "");
                list.Add((byte)(bytes.Length >> 8));
                list.Add((byte)(bytes.Length & 0xFF));
                list.AddRange(bytes);
            }
            return list.ToArray();
        }

        public override string ToString() => $"KukaVarProxyClient[{Ip}:{Port}]";

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

        /// <inheritdoc/>
        protected override byte[] BuildHeartbeat()
        {
            try { return BuildReadCore("$POS_ACT"); }
            catch { return null; }
        }
    }
}
