using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Secs
{
    /// <summary>
    /// SECS II / HSMS（高速 SECS 消息服务）协议客户端。
    /// <para>用于半导体设备通讯（SEMI E4/E5/E37 标准）。</para>
    /// <para>HSMS 帧格式: Select(1) + PType(1) + SystemBytes(4) + SType(1) + PHeader(2) + SystemBytes(4) + Data...</para>
    /// <para>简化帧: Length(4 BE) + Header(10) + Data</para>
    /// </summary>
    public class SecsHsmsClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── HSMS 常量 ───────────────────────────
        private const byte PType_SECS2 = 0x00;
        private const byte PType_Select = 0x01;
        private const byte PType_Deselect = 0x02;
        private const byte PType_Linktest = 0x05;

        private const byte SType_SelectReq = 0x01;
        private const byte SType_SelectRsp = 0x02;
        private const byte DeselectReq = 0x03;
        private const byte DeselectRsp = 0x04;
        private const byte LinktestReq = 0x05;
        private const byte LinktestRsp = 0x06;
        private const byte RejectReq = 0x07;
        private const byte SeparateReq = 0x09;

        private const int HEADER_LENGTH = 10;
        private const int LENGTH_FIELD_SIZE = 4;

        // ── 属性 ─────────────────────────────────

        /// <summary>设备 ID（2字节，默认 0）。</summary>
        public ushort DeviceId { get; set; } = 0;

        private uint _systemBytesCounter;
        private static readonly object _counterLock = new object();

        // ── TcpDeviceBase 抽象实现 ───────────────

        protected override int ResponseHeaderLength => LENGTH_FIELD_SIZE + HEADER_LENGTH;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 4) return 0;
            int msgLen = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            return msgLen - HEADER_LENGTH;
        }

        // ── 构造 ────────────────────────────────

        public SecsHsmsClient(string ip, int port = 5000, int timeout = 10000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  HSMS 连接流程
        // ═══════════════════════════════════════════

        /// <summary>发送 Linktest 检测连接。</summary>
        public OperateResult Linktest()
        {
            var header = BuildHsmsHeader(LinktestReq, PType_Linktest);
            byte[] frame = BuildFrame(header, null);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            if (recv.Content.Length < 14) return OperateResult.Failed("Linktest 响应不完整");
            byte sType = recv.Content[6];
            return sType == LinktestRsp ? OperateResult.Success() : OperateResult.Failed($"Linktest 失败: SType=0x{sType:X2}");
        }

        /// <summary>发送 Select.req 建立 HSMS 会话。</summary>
        public OperateResult Select()
        {
            var header = BuildHsmsHeader(SType_SelectReq, PType_Select);
            byte[] frame = BuildFrame(header, null);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            if (recv.Content.Length < 14) return OperateResult.Failed("Select 响应不完整");
            byte sType = recv.Content[6];
            return sType == SType_SelectRsp ? OperateResult.Success() : OperateResult.Failed($"Select 失败: SType=0x{sType:X2}");
        }

        /// <summary>发送 Separate.req 断开 HSMS 会话。</summary>
        public OperateResult Separate()
        {
            var header = BuildHsmsHeader(SeparateReq, PType_SECS2);
            byte[] frame = BuildFrame(header, null);
            try { SendAndReceive(frame); } catch { /* 无需等响应 */ }
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  SECS 消息收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 SECS 消息（Primary Message）。
        /// </summary>
        /// <param name="stream">SxFn 中的 S（流号，0-127）。</param>
        /// <param name="function">SxFn 中的 F（功能号，1-255）。</param>
        /// <param name="data">SECS II 数据项（已编码）。</param>
        /// <returns>回复消息数据（Reply Message）。</returns>
        public OperateResult<SecsMessage> SendPrimaryMessage(byte stream, byte function, byte[]? data)
        {
            if (function == 0) return OperateResult<SecsMessage>.Failed("功能号不能为 0");

            uint sysBytes = NextSystemBytes();
            bool waitForReply = (function % 2) == 1; // 奇数功能号等待回复

            byte pType = PType_SECS2;
            var header = new byte[HEADER_LENGTH];
            header[0] = (byte)((DeviceId >> 8) & 0xFF);
            header[1] = (byte)(DeviceId & 0xFF);
            header[2] = 0x00; // PType / SType placeholder
            header[3] = (byte)((stream << 1) | (waitForReply ? 1 : 0));
            header[4] = function;
            header[5] = (byte)((sysBytes >> 24) & 0xFF);
            header[6] = (byte)((sysBytes >> 16) & 0xFF);
            header[7] = (byte)((sysBytes >> 8) & 0xFF);
            header[8] = (byte)(sysBytes & 0xFF);
            header[9] = pType;

            byte[] frame = BuildFrame(header, data);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult<SecsMessage>.Failed(recv.Message);

            return ParseSecsMessage(recv.Content);
        }

        // ═══════════════════════════════════════════
        //  S1F1/S1F2 — 设备状态查询
        // ═══════════════════════════════════════════

        /// <summary>S1F1 — Are You There（无数据）。</summary>
        public OperateResult<SecsMessage> AreYouThere()
            => SendPrimaryMessage(1, 1, null);

        /// <summary>S1F13 — Establish Communication Request。</summary>
        public OperateResult<SecsMessage> EstablishCommunication()
            => SendPrimaryMessage(1, 13, null);

        /// <summary>S1F17 — Online Request。</summary>
        public OperateResult<SecsMessage> OnlineRequest()
            => SendPrimaryMessage(1, 17, null);

        // ═══════════════════════════════════════════
        //  S2F41 — Remote Command
        // ═══════════════════════════════════════════

        /// <summary>S2F41 — Host Command Send。</summary>
        public OperateResult<SecsMessage> HostCommandSend(byte[] commandData)
            => SendPrimaryMessage(2, 41, commandData);

        // ═══════════════════════════════════════════
        //  S5F1 — Alarm Report
        // ═══════════════════════════════════════════

        /// <summary>S5F1 — Alarm Report Send。</summary>
        public OperateResult<SecsMessage> AlarmReportSend(byte[] alarmData)
            => SendPrimaryMessage(5, 1, alarmData);

        // ═══════════════════════════════════════════
        //  S6F1 — Trace Data Send
        // ═══════════════════════════════════════════

        /// <summary>S6F1 — Trace Data Send。</summary>
        public OperateResult<SecsMessage> TraceDataSend(byte[] traceData)
            => SendPrimaryMessage(6, 1, traceData);

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            // SECS 不直接支持地址读写，通过消息交互
            var dataId = ParseDataId(address);
            var msg = SendPrimaryMessage(dataId.Stream, dataId.Function, dataId.Data);
            if (!msg.IsSuccess) return OperateResult<byte[]>.Failed(msg.Message);
            return OperateResult<byte[]>.Success(msg.Content.Data ?? new byte[0]);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var dataId = ParseDataId(address);
            dataId.Data = data;
            var msg = SendPrimaryMessage(dataId.Stream, dataId.Function, data);
            if (!msg.IsSuccess) return OperateResult.Failed(msg.Message);
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

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
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success(DataConverter.ToUInt32(r.Content, 0));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success(DataConverter.ToUInt64(r.Content, 0));
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
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

        public override OperateResult Write(string address, string value)
            => Write(address, DataConverter.GetBytes(value));

        // ═══════════════════════════════════════════
        //  HSMS 帧构建与解析
        // ═══════════════════════════════════════════

        private byte[] BuildHsmsHeader(byte sType, byte pType)
        {
            uint sysBytes = NextSystemBytes();
            var header = new byte[HEADER_LENGTH];
            header[0] = (byte)((DeviceId >> 8) & 0xFF);
            header[1] = (byte)(DeviceId & 0xFF);
            header[2] = sType;
            header[3] = 0x00;
            header[4] = 0x00;
            header[5] = (byte)((sysBytes >> 24) & 0xFF);
            header[6] = (byte)((sysBytes >> 16) & 0xFF);
            header[7] = (byte)((sysBytes >> 8) & 0xFF);
            header[8] = (byte)(sysBytes & 0xFF);
            header[9] = pType;
            return header;
        }

        /// <summary>构建 HSMS 帧: Length(4 BE) + Header(10) + Data。</summary>
        public static byte[] BuildFrame(byte[] header, byte[]? data)
        {
            int dataLen = data?.Length ?? 0;
            int totalLen = LENGTH_FIELD_SIZE + HEADER_LENGTH + dataLen;
            int msgLen = HEADER_LENGTH + dataLen;

            byte[] frame = new byte[totalLen];
            frame[0] = (byte)((msgLen >> 24) & 0xFF);
            frame[1] = (byte)((msgLen >> 16) & 0xFF);
            frame[2] = (byte)((msgLen >> 8) & 0xFF);
            frame[3] = (byte)(msgLen & 0xFF);

            Array.Copy(header, 0, frame, LENGTH_FIELD_SIZE, HEADER_LENGTH);
            if (data != null && data.Length > 0)
                Array.Copy(data, 0, frame, LENGTH_FIELD_SIZE + HEADER_LENGTH, data.Length);

            return frame;
        }

        /// <summary>解析 SECS 消息回复。</summary>
        public static OperateResult<SecsMessage> ParseSecsMessage(byte[] raw)
        {
            if (raw == null || raw.Length < LENGTH_FIELD_SIZE + HEADER_LENGTH)
                return OperateResult<SecsMessage>.Failed($"响应数据过短 ({raw?.Length ?? 0})");

            int msgLen = (raw[0] << 24) | (raw[1] << 16) | (raw[2] << 8) | raw[3];
            if (raw.Length < LENGTH_FIELD_SIZE + msgLen)
                return OperateResult<SecsMessage>.Failed("响应数据不完整");

            byte[] header = new byte[HEADER_LENGTH];
            Array.Copy(raw, LENGTH_FIELD_SIZE, header, 0, HEADER_LENGTH);

            var msg = new SecsMessage
            {
                DeviceId = (ushort)((header[0] << 8) | header[1]),
                SType = header[2],
                PType = header[9],
                SystemBytes = (uint)((header[5] << 24) | (header[6] << 16) | (header[7] << 8) | header[8])
            };

            // SECS 消息: header[3] bit7 = reply expected, header[3] bit0-6 = stream * 2 + flag
            byte sfByte = header[3];
            msg.Stream = (byte)(sfByte >> 1);
            msg.ReplyExpected = (sfByte & 0x01) != 0;
            msg.Function = header[4];

            // 数据
            int dataLen = msgLen - HEADER_LENGTH;
            if (dataLen > 0)
            {
                msg.Data = new byte[dataLen];
                Array.Copy(raw, LENGTH_FIELD_SIZE + HEADER_LENGTH, msg.Data, 0, dataLen);
            }

            return OperateResult<SecsMessage>.Success(msg);
        }

        // ═══════════════════════════════════════════
        //  地址解析（格式: S1F1 或 S1F1:hexdata）
        // ═══════════════════════════════════════════

        private static SecsDataId ParseDataId(string address)
        {
            var result = new SecsDataId();
            if (string.IsNullOrEmpty(address)) return result;

            int colonIdx = address.IndexOf(':');
            string sfPart = colonIdx >= 0 ? address.Substring(0, colonIdx) : address;
            string dataHex = colonIdx >= 0 ? address.Substring(colonIdx + 1) : "";

            // Parse SxFn
            if (sfPart.StartsWith("S", StringComparison.OrdinalIgnoreCase) && sfPart.Contains("F"))
            {
                string[] parts = sfPart.Substring(1).Split('F');
                if (parts.Length == 2)
                {
                    byte.TryParse(parts[0], out byte s);
                    byte.TryParse(parts[1], out byte f);
                    result.Stream = s;
                    result.Function = f;
                }
            }

            if (!string.IsNullOrEmpty(dataHex) && dataHex.Length % 2 == 0)
            {
                result.Data = new byte[dataHex.Length / 2];
                for (int i = 0; i < result.Data.Length; i++)
                    result.Data[i] = Convert.ToByte(dataHex.Substring(i * 2, 2), 16);
            }

            return result;
        }

        // ═══════════════════════════════════════════
        //  工具方法
        // ═══════════════════════════════════════════

        private uint NextSystemBytes()
        {
            lock (_counterLock) { return ++_systemBytesCounter; }
        }

        public override string ToString() => $"SecsHsmsClient[{Ip}:{Port}, DevId={DeviceId}]";

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

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildFrame(BuildHsmsHeader(LinktestReq, PType_Linktest), null); }
            catch { return null; }
        }
    }

    // ── 辅助类型 ──────────────────────────────

    /// <summary>SECS 消息。</summary>
    public class SecsMessage
    {
        public ushort DeviceId { get; set; }
        public byte SType { get; set; }
        public byte PType { get; set; }
        public byte Stream { get; set; }
        public byte Function { get; set; }
        public bool ReplyExpected { get; set; }
        public uint SystemBytes { get; set; }
        public byte[]? Data { get; set; }

        public override string ToString() => $"S{Stream}F{Function} [Sys=0x{SystemBytes:X8}, Data={Data?.Length ?? 0}B]";
    }

    internal class SecsDataId
    {
        public byte Stream { get; set; }
        public byte Function { get; set; }
        public byte[]? Data { get; set; }
    }
}
