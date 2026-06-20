using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Schneider
{
    /// <summary>
    /// 施耐德 Modicon M580/M340 协议客户端。
    /// <para>基于 Modbus TCP，支持标准 FC01-06 及 Modicon 扩展功能码 (OFs/UNA)。</para>
    /// <para>地址格式: %MW100 (内部字), %M50 (内部位), %I0.0 (输入位), %IW10 (输入字), %Q0.1 (输出位), %QW20 (输出字), %S0 (系统位), %SW100 (系统字)。</para>
    /// </summary>
    public class SchneiderModiconClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        /// <summary>Modbus 从站地址 (默认 1)。</summary>
        public byte SlaveId { get; set; } = 1;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 9;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 9) return 0;
            // MBAP 头: TxId(2) + ProtocolId(2) + Length(2) + UnitId(1) + FC(1) + ByteCount(1)
            int totalLen = (header[4] << 8) | header[5];
            return totalLen - 3; // 去掉 UnitId + FC + ByteCount (在 payload 部分)
        }

        public SchneiderModiconClient(string ip, int port = 502, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        /// <summary>Modbus 字节序（默认大端）。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;

        // ═══════════════════════════════════════════
        //  MBAP 帧构建
        // ═══════════════════════════════════════════

        private static int _transactionId;

        private byte[] BuildMbap(byte[] pdu)
        {
            ushort tid = (ushort)System.Threading.Interlocked.Increment(ref _transactionId);
            byte[] frame = new byte[7 + pdu.Length];
            // MBAP Header
            frame[0] = (byte)(tid >> 8);
            frame[1] = (byte)tid;
            frame[2] = 0x00; // Protocol ID (Modbus)
            frame[3] = 0x00;
            frame[4] = (byte)((pdu.Length + 1) >> 8);
            frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01; // Unit ID placeholder (使用 SlaveId)
            frame[6] = SlaveId;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        // ═══════════════════════════════════════════
        //  命令构建
        // ═══════════════════════════════════════════

        /// <summary>构建读取命令 PDU。</summary>
        public static byte[] BuildReadPdu(byte fc, ushort address, ushort count)
        {
            return new byte[]
            {
                fc,
                (byte)(address >> 8), (byte)address,
                (byte)(count >> 8), (byte)count
            };
        }

        /// <summary>构建写入单个寄存器命令 PDU。</summary>
        public static byte[] BuildWriteSingleRegisterPdu(ushort address, short value)
        {
            return new byte[]
            {
                0x06,
                (byte)(address >> 8), (byte)address,
                (byte)(value >> 8), (byte)value
            };
        }

        /// <summary>构建写入多个寄存器命令 PDU。</summary>
        public static byte[] BuildWriteMultipleRegistersPdu(ushort address, byte[] data)
        {
            ushort wordCount = (ushort)(data.Length / 2);
            byte byteCount = (byte)data.Length;
            byte[] pdu = new byte[6 + data.Length];
            pdu[0] = 0x10; // FC16
            pdu[1] = (byte)(address >> 8);
            pdu[2] = (byte)address;
            pdu[3] = (byte)(wordCount >> 8);
            pdu[4] = (byte)wordCount;
            pdu[5] = byteCount;
            Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
            return pdu;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult<byte[]>.Failed($"无法解析施耐德地址: {address}");

            byte[] pdu = BuildReadPdu(addrResult.FunctionCode, addrResult.AddressValue, length);
            byte[] frame = BuildMbap(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<byte[]>.Failed("响应长度不足");

            // 检查异常响应
            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult<byte[]>.Failed(SchneiderErrorCodes.GetDescription(errCode), errCode);
            }

            // 提取数据 (跳过 MBAP头7字节 + FC1字节 + ByteCount1字节)
            int byteCount = resp[8];
            if (resp.Length < 9 + byteCount)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(resp, 9, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult.Failed($"无法解析施耐德地址: {address}");

            byte[] pdu = BuildWriteMultipleRegistersPdu(addrResult.AddressValue, data);
            byte[] frame = BuildMbap(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12)
                return OperateResult.Failed("写入响应长度不足");

            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult.Failed(SchneiderErrorCodes.GetDescription(errCode), errCode);
            }

            return OperateResult.Success();
        }

        // ── 高层数据类型读写 ──

        public override OperateResult<short> ReadInt16(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(result.Content, 0, ByteOrder));
        }

        public override OperateResult Write(string address, short value)
        {
            return Write(address, DataConverter.GetBytes(value, ByteOrder));
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult<bool>.Failed($"无法解析地址: {address}");

            byte[] pdu = BuildReadPdu(addrResult.FunctionCode, addrResult.AddressValue, 1);
            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<bool>.Failed("响应长度不足");

            if ((resp[7] & 0x80) != 0)
                return OperateResult<bool>.Failed(SchneiderErrorCodes.GetDescription(resp[8]));

            // 位读取返回 1 字节
            return OperateResult<bool>.Success((resp[9] & 0x01) != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addrResult = SchneiderAddress.TryParse(address);
            if (addrResult == null)
                return OperateResult.Failed($"无法解析地址: {address}");

            // FC05: Write Single Coil
            ushort coilValue = value ? (ushort)0xFF00 : (ushort)0x0000;
            byte[] pdu =
            {
                0x05,
                (byte)(addrResult.AddressValue >> 8), (byte)addrResult.AddressValue,
                (byte)(coilValue >> 8), (byte)coilValue
            };

            var result = SendAndReceive(BuildMbap(pdu));
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12) return OperateResult.Failed("写入响应长度不足");
            if ((resp[7] & 0x80) != 0)
                return OperateResult.Failed(SchneiderErrorCodes.GetDescription(resp[8]));

            return OperateResult.Success();
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe(address, 2, d => DataConverter.ToInt32(d, 0, ByteOrder));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe(address, 2, d => DataConverter.ToUInt32(d, 0, ByteOrder));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe(address, 4, d => DataConverter.ToInt64(d, 0, ByteOrder));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe(address, 4, d => DataConverter.ToUInt64(d, 0, ByteOrder));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe(address, 2, d => DataConverter.ToFloat(d, 0, ByteOrder));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe(address, 4, d => DataConverter.ToDouble(d, 0, ByteOrder));

        public override OperateResult Write(string address, int value) => Write(address, DataConverter.GetBytes(value, ByteOrder));
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, DataConverter.GetBytes(value, ByteOrder));
        public override OperateResult Write(string address, ulong value) => Write(address, DataConverter.GetBytes(value, ByteOrder));
        public override OperateResult Write(string address, float value) => Write(address, DataConverter.GetBytes(value, ByteOrder));
        public override OperateResult Write(string address, double value) => Write(address, DataConverter.GetBytes(value, ByteOrder));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

        /// <summary>读取字符串（从 Modicon 寄存器）。</summary>
        public override OperateResult<string> ReadString(string address, ushort length)
        {
            ushort wordCount = (ushort)((length + 1) / 2);
            var result = ReadBytes(address, wordCount);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            int charCount = Math.Min(length, result.Content.Length);
            return OperateResult<string>.Success(Encoding.ASCII.GetString(result.Content, 0, charCount));
        }

        /// <summary>写入字符串（到 Modicon 寄存器，自动补齐偶数字节）。</summary>
        public OperateResult WriteString(string address, string value, ushort maxRegisters)
        {
            if (value == null) return OperateResult.Failed("字符串不能为空");
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            int maxBytes = maxRegisters * 2;
            if (bytes.Length > maxBytes)
                return OperateResult.Failed($"字符串长度 {bytes.Length} 超出最大字节数 {maxBytes}");
            if (bytes.Length % 2 != 0)
            {
                byte[] padded = new byte[bytes.Length + 1];
                Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);
                bytes = padded;
            }
            return Write(address, bytes);
        }

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message, result.ErrorCode);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值（按区域分组，连续地址合并读取）。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var result = new Dictionary<string, object?>();
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");

            // 按功能码分组
            var groups = addrList.GroupBy(a =>
            {
                var parsed = SchneiderAddress.TryParse(a);
                return parsed?.FunctionCode ?? 0x03;
            });

            foreach (var group in groups)
            {
                var sorted = group.Select(a => new { Address = a, Parsed = SchneiderAddress.TryParse(a) })
                                  .Where(a => a.Parsed != null)
                                  .OrderBy(a => a.Parsed!.AddressValue)
                                  .ToList();

                if (sorted.Count == 0) continue;

                ushort minAddr = (ushort)sorted[0].Parsed!.AddressValue;
                ushort maxAddr = (ushort)sorted.Last().Parsed!.AddressValue;
                ushort range = (ushort)(maxAddr - minAddr + 1);

                byte fc = (byte)group.Key;
                if (fc == 0x01 || fc == 0x02)
                {
                    // 位区域 — 批量读线圈
                    byte[] pdu = BuildReadPdu(fc, minAddr, range);
                    var r = SendAndReceive(BuildMbap(pdu));
                    if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                    if (r.Content == null || r.Content.Length < 10)
                        return OperateResult<Dictionary<string, object?>>.Failed("响应长度不足");

                    int byteCount = r.Content[8];
                    foreach (var item in sorted)
                    {
                        int idx = item.Parsed!.AddressValue - minAddr;
                        if (idx >= 0 && idx < byteCount * 8)
                            result[item.Address] = (r.Content[9 + idx / 8] & (1 << (idx % 8))) != 0;
                    }
                }
                else
                {
                    // 字区域 — 批量读寄存器
                    byte[] pdu = BuildReadPdu(fc, minAddr, range);
                    var r = SendAndReceive(BuildMbap(pdu));
                    if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                    if (r.Content == null || r.Content.Length < 10)
                        return OperateResult<Dictionary<string, object?>>.Failed("响应长度不足");

                    int byteCount = r.Content[8];
                    foreach (var item in sorted)
                    {
                        int byteOffset = (item.Parsed!.AddressValue - minAddr) * 2;
                        if (byteOffset >= 0 && byteOffset + 2 <= byteCount)
                            result[item.Address] = (short)((r.Content[9 + byteOffset] << 8) | r.Content[10 + byteOffset]);
                    }
                }
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


        // ═══════════════════════════════════════════
        //  PLC 诊断与识别
        // ═══════════════════════════════════════════

        /// <summary>读取 PLC 识别信息（系统字区域 %SW0-%SW9）。</summary>
        public OperateResult<SchneiderPlcInfo> ReadPlcInfo()
        {
            var info = new SchneiderPlcInfo();
            var sw0 = ReadUInt16("%SW0");
            if (!sw0.IsSuccess) return OperateResult<SchneiderPlcInfo>.Failed(sw0.Message, sw0.ErrorCode);
            info.DeviceType = sw0.Content;

            var sw1 = ReadUInt16("%SW1");
            if (sw1.IsSuccess) info.FirmwareVersion = sw1.Content;

            var sw2 = ReadUInt16("%SW2");
            if (sw2.IsSuccess) info.HardwareVersion = sw2.Content;

            var sw3 = ReadUInt16("%SW3");
            if (sw3.IsSuccess) info.StatusWord = sw3.Content;

            return OperateResult<SchneiderPlcInfo>.Success(info);
        }

        /// <summary>读取系统状态字（%SW 指定偏移）。</summary>
        public OperateResult<ushort> ReadSystemWord(ushort offset)
        {
            return ReadUInt16($"%SW{offset}");
        }

        /// <summary>读取系统状态位（%S 指定编号）。</summary>
        public OperateResult<bool> ReadSystemBit(ushort index)
        {
            return ReadBool($"%S{index}");
        }

        /// <summary>读取诊断寄存器（%SW100-%SW109: 错误计数器、通信统计）。</summary>
        public OperateResult<SchneiderDiagnostics> ReadDiagnostics()
        {
            var diag = new SchneiderDiagnostics();
            var sw100 = ReadUInt16("%SW100");
            if (!sw100.IsSuccess) return OperateResult<SchneiderDiagnostics>.Failed(sw100.Message, sw100.ErrorCode);
            diag.CommErrorCount = sw100.Content;

            var sw101 = ReadUInt16("%SW101");
            if (sw101.IsSuccess) diag.CrcErrorCount = sw101.Content;

            var sw102 = ReadUInt16("%SW102");
            if (sw102.IsSuccess) diag.TimeoutCount = sw102.Content;

            var sw103 = ReadUInt16("%SW103");
            if (sw103.IsSuccess) diag.ExceptionCount = sw103.Content;

            var sw104 = ReadUInt16("%SW104");
            if (sw104.IsSuccess) diag.LastErrorCode = sw104.Content;

            var sw105 = ReadUInt16("%SW105");
            if (sw105.IsSuccess) diag.RunMode = sw105.Content;

            return OperateResult<SchneiderDiagnostics>.Success(diag);
        }

        // ═══════════════════════════════════════════
        //  批量优化 — 按区域分组合并连续地址
        // ═══════════════════════════════════════════

        /// <summary>将地址列表按区域分组并合并连续范围，返回 (功能码, 起始地址, 数量) 的列表。</summary>
        public static List<(byte Fc, ushort Start, ushort Count)> GroupAddressesForBatch(IEnumerable<string> addresses)
        {
            var parsed = new List<(string Raw, SchneiderAddress Addr)>();
            foreach (var a in addresses)
            {
                var p = SchneiderAddress.TryParse(a);
                if (p != null) parsed.Add((a, p));
            }

            var groups = parsed.GroupBy(x => x.Addr.FunctionCode);
            var result = new List<(byte Fc, ushort Start, ushort Count)>();

            foreach (var group in groups)
            {
                var sorted = group.OrderBy(x => x.Addr.AddressValue).ToList();
                int i = 0;
                while (i < sorted.Count)
                {
                    ushort start = sorted[i].Addr.AddressValue;
                    ushort end = start;
                    while (i + 1 < sorted.Count && sorted[i + 1].Addr.AddressValue - end <= 1)
                    {
                        i++;
                        end = sorted[i].Addr.AddressValue;
                    }
                    ushort count = (ushort)(end - start + 1);
                    result.Add((group.Key, start, count));
                    i++;
                }
            }
            return result;
        }

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try
            {
                var addr = SchneiderAddress.TryParse("%MW0");
                if (addr == null) return null;
                return BuildReadPdu(addr.FunctionCode, addr.AddressValue, 1);
            }
            catch { return null; }
        }
    }
}
