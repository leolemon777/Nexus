using System;
using System.Text;

namespace Nexus.Robot.Kuka
{
    /// <summary>
    /// KUKA 机器人 KUKAVARPROXY 通讯客户端。
    /// <para>基于 KRC4 控制器运行的 KUKAVARPROXY 第三方软件。</para>
    /// <para>默认端口 7000。</para>
    /// <para>帧格式: Id(2 BE) + Len(2 BE) + Core，其中 Core: Func(1) + Len(2) + Name + [Len(2) + Value]</para>
    /// </summary>
    public class KukaVarProxyClient : TcpDeviceBase
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
        public OperateResult Write(string address, string value)
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
        public OperateResult Write(string address, byte[] value)
        {
            return Write(address, Encoding.Default.GetString(value));
        }

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
    }
}
