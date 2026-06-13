using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Cjt
{
    /// <summary>
    /// CJ/T 188 户用计量仪表数据传输协议客户端。
    /// <para>支持水表、热量表、燃气表、电表。</para>
    /// <para>帧格式: 68H + T(类型) + A0..A6(地址) + C(控制) + L(长度) + DI0..DI3 + DATA + CS + 16H</para>
    /// <para>数据域加 33H 加密传输。</para>
    /// </summary>
    public class Cjt188Client : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── SerialDeviceBase 抽象实现（串口协议自定义收发，不使用基类 SendAndReceive）──
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;
        private const byte FRAME_HEADER = 0x68;
        private const byte FRAME_END = 0x16;
        private const byte DATA_OFFSET = 0x33;

        // ── 仪表类型码 ──────────────────────────
        /// <summary>冷水水表</summary>
        public const byte TYPE_WATER_COLD = 0x10;
        /// <summary>热水水表</summary>
        public const byte TYPE_WATER_HOT = 0x11;
        /// <summary>热量表</summary>
        public const byte TYPE_HEAT = 0x20;
        /// <summary>燃气表</summary>
        public const byte TYPE_GAS = 0x30;
        /// <summary>电表</summary>
        public const byte TYPE_ELECTRIC = 0x40;

        // ── 控制码 ──────────────────────────────
        private const byte CTRL_READ_DATA = 0x01;
        private const byte CTRL_READ_FOLLOW = 0x02;
        private const byte CTRL_WRITE_DATA = 0x04;
        private const byte CTRL_WRITE_FOLLOW = 0x05;

        // ── 属性 ─────────────────────────────────

        /// <summary>仪表类型。</summary>
        public byte MeterType { get; set; } = TYPE_WATER_COLD;

        /// <summary>仪表地址（7字节）。</summary>
        public byte[] MeterAddress { get; set; } = new byte[7];

        private readonly object _serialLock = new object();

        // ── 构造 ────────────────────────────────

        public Cjt188Client(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        // ═══════════════════════════════════════════
        //  读取数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取指定数据标识的数据。
        /// </summary>
        public OperateResult<byte[]> ReadData(byte[] dataId)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult<byte[]>.Failed("数据标识必须为 4 字节");

            var frame = BuildFrame(CTRL_READ_DATA, dataId, null);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseResponse(recv.Content, CTRL_READ_DATA);
        }

        /// <summary>读取当前累积用量。</summary>
        public OperateResult<decimal> ReadCurrentUsage()
        {
            var r = ReadData(new byte[] { 0x90, 0x1F, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimalUsage(r.Content);
        }

        /// <summary>读取结算日累积用量。</summary>
        public OperateResult<decimal> ReadSettlementUsage()
        {
            var r = ReadData(new byte[] { 0x90, 0x20, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimalUsage(r.Content);
        }

        // ═══════════════════════════════════════════
        //  写入数据
        // ═══════════════════════════════════════════

        public OperateResult WriteData(byte[] dataId, byte[] data)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult.Failed("数据标识必须为 4 字节");

            var frame = BuildFrame(CTRL_WRITE_DATA, dataId, data);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content, CTRL_WRITE_DATA);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var id = ParseDataId(address);
            if (id == null) return OperateResult<byte[]>.Failed($"数据标识格式错误: {address}");
            return ReadData(id);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var id = ParseDataId(address);
            if (id == null) return OperateResult.Failed($"数据标识格式错误: {address}");
            return WriteData(id, data);
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

        public override string ToString() => $"Cjt188Client[Type=0x{MeterType:X2},Addr={BitConverter.ToString(MeterAddress)}]";

        // ═══════════════════════════════════════════
        //  帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 CJ/T 188 帧。</summary>
        public byte[] BuildFrame(byte control, byte[] dataId, byte[]? userData)
        {
            int dataLen = 4 + (userData?.Length ?? 0);
            byte[] dataField = new byte[dataLen];
            dataField[0] = dataId[0];
            dataField[1] = dataId[1];
            dataField[2] = dataId[2];
            dataField[3] = dataId[3];
            if (userData != null) Array.Copy(userData, 0, dataField, 4, userData.Length);

            // 加 33H
            byte[] encrypted = new byte[dataLen];
            for (int i = 0; i < dataLen; i++)
                encrypted[i] = (byte)(dataField[i] + DATA_OFFSET);

            // 校验: T + A(7) + C + L + DATA
            byte cs = MeterType;
            for (int i = 0; i < 7; i++) cs ^= MeterAddress[i];
            cs ^= control;
            cs ^= (byte)dataLen;
            for (int i = 0; i < dataLen; i++) cs ^= encrypted[i];

            // 68H(1) + T(1) + A(7) + 68H(1) + C(1) + L(1) + DATA(dataLen) + CS(1) + 16H(1) = 14 + dataLen
            byte[] frame = new byte[14 + dataLen];
            frame[0] = FRAME_HEADER;
            frame[1] = MeterType;
            Array.Copy(MeterAddress, 0, frame, 2, 7);
            frame[9] = FRAME_HEADER;
            frame[10] = control;
            frame[11] = (byte)dataLen;
            Array.Copy(encrypted, 0, frame, 12, dataLen);
            frame[12 + dataLen] = cs;
            frame[13 + dataLen] = FRAME_END;
            return frame;
        }

        // ═══════════════════════════════════════════
        //  帧解析
        // ═══════════════════════════════════════════

        /// <summary>解析 CJ/T 188 响应帧。</summary>
        public static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedCtrl)
        {
            if (response == null || response.Length < 13)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != FRAME_HEADER || response[9] != FRAME_HEADER)
                return OperateResult<byte[]>.Failed("帧头不匹配");

            byte ctrl = response[10];
            byte dataLen = response[11];

            // 错误检查
            if ((ctrl & 0x80) != 0)
            {
                byte errCode = (byte)(response[16] - DATA_OFFSET);
                return OperateResult<byte[]>.Failed($"仪表错误码: 0x{errCode:X2}", errCode);
            }

            if (response.Length < 12 + dataLen + 1)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            // 校验
            byte cs = response[1]; // T
            for (int i = 0; i < 7; i++) cs ^= response[2 + i]; // A
            cs ^= ctrl;
            cs ^= dataLen;
            for (int i = 0; i < dataLen; i++) cs ^= response[12 + i];

            if (cs != response[12 + dataLen])
                return OperateResult<byte[]>.Failed($"校验和不匹配");

            // 解密
            byte[] data = new byte[dataLen];
            for (int i = 0; i < dataLen; i++)
                data[i] = (byte)(response[12 + i] - DATA_OFFSET);

            // 返回纯数据（跳过 DI）
            if (dataLen > 4)
            {
                byte[] pureData = new byte[dataLen - 4];
                Array.Copy(data, 4, pureData, 0, pureData.Length);
                return OperateResult<byte[]>.Success(pureData);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        // ═══════════════════════════════════════════
        //  串口通讯
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendAndReceiveSerial(byte[] frame)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(frame));
                    Port.Write(frame, 0, frame.Length);

                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                response.Add(buf[i]);
                                if (response.Count >= 13 && response[0] == FRAME_HEADER && response[9] == FRAME_HEADER)
                                {
                                    int expectedLen = 12 + response[11] + 1;
                                    if (response.Count >= expectedLen)
                                    {
                                        byte[] result = response.ToArray();
                                        RaiseMessageReceived(DataConverter.ToHexString(result));
                                        return OperateResult<byte[]>.Success(result);
                                    }
                                }
                            }
                        }
                    }

                    return OperateResult<byte[]>.Failed($"CJT188 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"CJT188 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"CJT188 通讯异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  BCD 工具
        // ═══════════════════════════════════════════

        private static OperateResult<decimal> BcdToDecimalUsage(byte[] data)
        {
            if (data == null || data.Length < 4)
                return OperateResult<decimal>.Failed("用量数据不足 4 字节");

            string bcdStr = BcdToString(data);
            // 格式: XXXXXX.XX（整数 6 位，小数 2 位）
            if (decimal.TryParse(bcdStr.Insert(bcdStr.Length - 2, "."), out decimal result))
                return OperateResult<decimal>.Success(result);

            return OperateResult<decimal>.Failed($"用量解析失败: {bcdStr}");
        }

        /// <summary>解析数据标识字符串（8位十六进制，低字节在前）。</summary>
        public static byte[]? ParseDataId(string dataIdStr)
        {
            if (string.IsNullOrEmpty(dataIdStr) || dataIdStr.Length != 8)
                return null;

            try
            {
                return new byte[]
                {
                    Convert.ToByte(dataIdStr.Substring(6, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(4, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(2, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(0, 2), 16)
                };
            }
            catch { return null; }
        }

        /// <summary>BCD 字节数组转字符串（低字节在前，逆序显示）。</summary>
        public static string BcdToString(byte[] data)
        {
            var sb = new System.Text.StringBuilder(data.Length * 2);
            for (int i = data.Length - 1; i >= 0; i--)
            {
                sb.Append((data[i] >> 4).ToString());
                sb.Append((data[i] & 0x0F).ToString());
            }
            return sb.ToString();
        }

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
    }
}
