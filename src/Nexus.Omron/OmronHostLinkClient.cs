using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Omron;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 HostLink 协议客户端（TCP 模式）。
    /// <para>HostLink 是 FINS 命令的 ASCII 文本封装，帧以 CR (0x0D) 结尾。</para>
    /// <para>地址格式与 FINS-TCP 相同：D100, CIO100, W100, H100, A100, D100.03, E0_100。</para>
    /// <para>默认端口 9600。</para>
    /// </summary>
    public class OmronHostLinkClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── HostLink 帧常量 ──────────────────────
        private const byte STX = (byte)'@';
        private const byte ETX = (byte)'*';
        private const byte CR  = 0x0D;

        // ── FINS 命令码 ──────────────────────────
        private const ushort CmdMemoryAreaRead  = 0x0101;
        private const ushort CmdMemoryAreaWrite = 0x0102;

        // ── 属性 ─────────────────────────────────

        /// <summary>站号（0-31，默认 0）。</summary>
        public byte UnitNumber { get; set; } = 0;

        /// <summary>ICF：网络中继标志（0x00=直连, 0x80=网络）。</summary>
        public byte ICF { get; set; } = 0x00;

        /// <summary>DA2：目标节点号。</summary>
        public byte DA2 { get; set; } = 0x00;

        /// <summary>SA2：源节点号。</summary>
        public byte SA2 { get; set; } = 0x00;

        /// <summary>SID：服务 ID。</summary>
        public byte SID { get; set; } = 0x00;

        /// <summary>响应等待时间（十六进制字符，0-F，单位 10ms，默认 '0'）。</summary>
        public byte ResponseWaitTime { get; set; } = (byte)'0';

        /// <summary>字读取分包大小（默认 260）。</summary>
        public int ReadSplits { get; set; } = 260;

        private int _sidCounter;
        private static readonly FinsAddressParser _addressParser = new FinsAddressParser();

        // ── TcpDeviceBase 抽象实现 ───────────────

        /// <summary>HostLink 不使用固定长度的响应头。</summary>
        protected override int ResponseHeaderLength => 1; // 不使用基类 framing

        protected override int GetResponsePayloadLength(byte[] header) => 0;

        /// <summary>
        /// 重写 SendAndReceive：HostLink 使用 CR 终止符而非长度前缀。
        /// </summary>
        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                // 读取直到 CR (0x0D)
                var ms = new MemoryStream();
                int b;
                while ((b = ns.ReadByte()) != -1)
                {
                    ms.WriteByte((byte)b);
                    if (b == CR) break;
                }

                if (ms.Length == 0)
                    return OperateResult<byte[]>.Failed("未收到 HostLink 响应");

                byte[] response = ms.ToArray();

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(response);
            }
            catch (Exception ex)
            {
                Log.Error($"HostLink 通讯异常 — {ex.Message}");
                RaiseError($"HostLink 通讯异常 — {ex.Message}");
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"HostLink 通讯异常: {ex.Message}");
            }
        }

        // ── 构造 ────────────────────────────────

        public OmronHostLinkClient(string ip, int port = 9600, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  原始字节读写
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);

            // 分包
            var result = new List<byte>();
            int remaining = wordCount;
            ushort currentWord = addr.WordAddress;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, ReadSplits);
                var cmdData = BuildFinsReadCommand(addr, currentWord, chunk, isBit: false);
                var frame = PackCommand(cmdData);
                var recv = SendAndReceive(frame);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

                var parsed = ParseResponse(recv.Content);
                if (!parsed.IsSuccess) return OperateResult<byte[]>.Failed(parsed.Message);

                result.AddRange(parsed.Content);
                currentWord += (ushort)chunk;
                remaining -= chunk;
            }

            byte[] final = result.ToArray();
            if (final.Length > length)
            {
                var trimmed = new byte[length];
                Array.Copy(final, 0, trimmed, 0, length);
                final = trimmed;
            }
            return OperateResult<byte[]>.Success(final);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)(data.Length / 2);
            var cmdData = BuildFinsWriteCommand(addr, wordCount, data, isBit: false);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  位操作
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult<bool>.Failed("位读取地址必须包含位偏移，例如 D100.03");

            var cmdData = BuildFinsReadCommand(addr, addr.WordAddress, 1, isBit: true);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult<bool>.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult<bool>.Failed(parsed.Message);
            return OperateResult<bool>.Success(parsed.Content.Length > 0 && parsed.Content[0] != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset < 0)
                return OperateResult.Failed("位写入地址必须包含位偏移，例如 D100.03");

            var data = new byte[] { (byte)(value ? 1 : 0) };
            var cmdData = BuildFinsWriteCommand(addr, 1, data, isBit: true);
            var frame = PackCommand(cmdData);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  标准类型读取（大端序）
        // ═══════════════════════════════════════════

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(
                BitConverter.ToSingle(BitConverter.GetBytes(DataConverter.ToInt32(r.Content, 0)), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(
                BitConverter.ToDouble(BitConverter.GetBytes(DataConverter.ToInt64(r.Content, 0)), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0'));
        }

        // ═══════════════════════════════════════════
        //  标准类型写入
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ushort value)
            => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
            => Write(address, new byte[] {
                (byte)(value >> 24), (byte)(value >> 16),
                (byte)(value >> 8),  (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
            => Write(address, new byte[] {
                (byte)(value >> 56), (byte)(value >> 48),
                (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 24), (byte)(value >> 16),
                (byte)(value >> 8),  (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        // ═══════════════════════════════════════════
        public override OperateResult Write(string address, float value)
        {
            int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
            return Write(address, new byte[] {
                (byte)(bits >> 24), (byte)(bits >> 16),
                (byte)(bits >> 8),  (byte)(bits & 0xFF) });
        }
        public override OperateResult Write(string address, double value)
        {
            long bits = BitConverter.DoubleToInt64Bits(value);
            return Write(address, new byte[] {
                (byte)(bits >> 56), (byte)(bits >> 48),
                (byte)(bits >> 40), (byte)(bits >> 32),
                (byte)(bits >> 24), (byte)(bits >> 16),
                (byte)(bits >> 8),  (byte)(bits & 0xFF) });
        }
        public override OperateResult Write(string address, string value)
            => Write(address, System.Text.Encoding.ASCII.GetBytes(value ?? string.Empty));
        //  HostLink 帧构建（公开，便于测试）
        // ═══════════════════════════════════════════

        /// <summary>将 FINS 二进制命令打包为 HostLink ASCII 帧。</summary>
        public byte[] PackCommand(byte[] finsCmd)
        {
            byte[] cmdAscii = BytesToAsciiHex(finsCmd);
            byte sid = (byte)(Interlocked.Increment(ref _sidCounter) & 0xFF);

            // @ + unit(2) + FA + wait(1) + ICF(2) + DA2(2) + SA2(2) + SID(2) + cmd + FCS(2) + * + CR
            int totalLen = 14 + cmdAscii.Length + 4; // 14 header + cmd + FCS(2) + *(1) + CR(1)
            var frame = new byte[totalLen];

            frame[0] = STX;
            frame[1] = ToAsciiHexHigh(UnitNumber);
            frame[2] = ToAsciiHexLow(UnitNumber);
            frame[3] = (byte)'F';
            frame[4] = (byte)'A';
            frame[5] = ResponseWaitTime;
            frame[6] = ToAsciiHexHigh(ICF);
            frame[7] = ToAsciiHexLow(ICF);
            frame[8] = ToAsciiHexHigh(DA2);
            frame[9] = ToAsciiHexLow(DA2);
            frame[10] = ToAsciiHexHigh(SA2);
            frame[11] = ToAsciiHexLow(SA2);
            frame[12] = ToAsciiHexHigh(sid);
            frame[13] = ToAsciiHexLow(sid);

            Array.Copy(cmdAscii, 0, frame, 14, cmdAscii.Length);

            frame[totalLen - 2] = ETX;
            frame[totalLen - 1] = CR;

            // FCS: XOR from [0] to [totalLen - 5]
            byte fcs = 0;
            for (int i = 0; i < totalLen - 4; i++)
                fcs ^= frame[i];
            frame[totalLen - 4] = ToAsciiHexHigh(fcs);
            frame[totalLen - 3] = ToAsciiHexLow(fcs);

            return frame;
        }

        /// <summary>解析 HostLink 响应，提取 FINS 数据。</summary>
        public static OperateResult<byte[]> ParseResponse(byte[] response)
        {
            if (response == null || response.Length < 10)
                return OperateResult<byte[]>.Failed($"HostLink 响应过短 ({response?.Length ?? 0} 字节)");

            // 查找响应中的 FINS 数据起点（跳过 HostLink 头）
            // HostLink 头: @ + unit(2) + FA(2) + wait(1) + ICF(2) + DA2(2) + SA2(2) + SID(2) = 14
            // 响应额外 1 字节后为 FINS 命令码
            if (response.Length < 27)
                return OperateResult<byte[]>.Failed("HostLink 响应不完整");

            try
            {
                // 命令码在 response[15..18]
                string cmdCode = Encoding.ASCII.GetString(response, 15, 4);
                // 结束码在 response[19..22]
                string endCodeStr = Encoding.ASCII.GetString(response, 19, 4);
                int endCode = Convert.ToInt32(endCodeStr, 16);

                // 数据区域 [23..length-4]（ASCII hex），转为字节
                byte[] data = new byte[0];
                if (response.Length > 27)
                {
                    string dataHex = Encoding.ASCII.GetString(response, 23, response.Length - 27);
                    data = AsciiHexToBytes(dataHex);
                }

                if (endCode > 0)
                    return OperateResult<byte[]>.Failed($"FINS 错误码: 0x{endCode:X4}");

                return OperateResult<byte[]>.Success(data);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"HostLink 响应解析失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  FINS 命令构建
        // ═══════════════════════════════════════════

        private byte[] BuildFinsReadCommand(FinsAddress addr, ushort wordAddress, int length, bool isBit)
        {
            var cmd = new byte[9];
            cmd[0] = (byte)(CmdMemoryAreaRead >> 8);
            cmd[1] = (byte)(CmdMemoryAreaRead & 0xFF);
            cmd[2] = (byte)addr.Area;
            cmd[3] = (byte)(isBit || addr.BitOffset >= 0 ? 0x01 : 0x00);
            cmd[4] = (byte)(wordAddress >> 8);
            cmd[5] = (byte)(wordAddress & 0xFF);
            cmd[6] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmd[7] = (byte)(length >> 8);
            cmd[8] = (byte)(length & 0xFF);
            return cmd;
        }

        private byte[] BuildFinsWriteCommand(FinsAddress addr, ushort length, byte[] data, bool isBit)
        {
            var cmd = new byte[9 + data.Length];
            cmd[0] = (byte)(CmdMemoryAreaWrite >> 8);
            cmd[1] = (byte)(CmdMemoryAreaWrite & 0xFF);
            cmd[2] = (byte)addr.Area;
            cmd[3] = (byte)(isBit || addr.BitOffset >= 0 ? 0x01 : 0x00);
            cmd[4] = (byte)(addr.WordAddress >> 8);
            cmd[5] = (byte)(addr.WordAddress & 0xFF);
            cmd[6] = (byte)(addr.BitOffset >= 0 ? addr.BitOffset : 0x00);
            cmd[7] = (byte)(length >> 8);
            cmd[8] = (byte)(length & 0xFF);
            if (data.Length > 0)
                Array.Copy(data, 0, cmd, 9, data.Length);
            return cmd;
        }

        // ═══════════════════════════════════════════
        //  ASCII 十六进制辅助方法
        // ═══════════════════════════════════════════

        public static byte[] BytesToAsciiHex(byte[] data)
        {
            var result = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                result[i * 2]     = ToAsciiHexHigh(data[i]);
                result[i * 2 + 1] = ToAsciiHexLow(data[i]);
            }
            return result;
        }

        public static byte[] AsciiHexToBytes(string hex)
        {
            if (hex.Length % 2 != 0) hex = "0" + hex;
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = (byte)((HexCharToInt(hex[i * 2]) << 4) | HexCharToInt(hex[i * 2 + 1]));
            return result;
        }

        public static byte ToAsciiHexHigh(byte b) => (byte)(b >> 4) switch
        {
            <= 9 => (byte)((b >> 4) + '0'),
            _    => (byte)((b >> 4) - 10 + 'A')
        };

        public static byte ToAsciiHexLow(byte b) => (byte)(b & 0x0F) switch
        {
            <= 9 => (byte)((b & 0x0F) + '0'),
            _    => (byte)((b & 0x0F) - 10 + 'A')
        };

        private static int HexCharToInt(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            return 0;
        }

        public override string ToString() => $"OmronHostLinkClient[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IBatchReadWrite 实现
        // ═══════════════════════════════════════════

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            foreach (string addr in addresses)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = (object?)r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, byte[]>();
            foreach (string addr in addresses)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <inheritdoc/>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

        /// <inheritdoc/>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool v => Write(kv.Key, v),
                    short v => Write(kv.Key, v),
                    ushort v => Write(kv.Key, v),
                    int v => Write(kv.Key, v),
                    uint v => Write(kv.Key, v),
                    long v => Write(kv.Key, v),
                    ulong v => Write(kv.Key, v),
                    float v => Write(kv.Key, v),
                    double v => Write(kv.Key, v),
                    string v => Write(kv.Key, v),
                    byte[] v => Write(kv.Key, v),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <inheritdoc/>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.Run(() => BatchWrite(items), cancellationToken);

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

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
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

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
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
