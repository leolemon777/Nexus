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

        public SchneiderModiconClient(string ip, int port = 502)
            : base(ip, port)
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
            byte[] pdu = new byte[6 + 1 + data.Length];
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
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message);
            return OperateResult<short>.Success((short)((result.Content[0] << 8) | result.Content[1]));
        }

        public override OperateResult Write(string address, short value)
        {
            return Write(address, new byte[] { (byte)(value >> 8), (byte)value });
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
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);
        public override OperateResult Write(string address, float value) { unsafe { int bits = *(int*)&value; return Write(address, bits); } }
        public override OperateResult Write(string address, double value) => Write(address, (float)value);
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

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
