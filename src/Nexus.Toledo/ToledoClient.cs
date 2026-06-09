using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Toledo
{
    /// <summary>
    /// 梅特勒-托利多（Mettler Toledo）电子秤 TCP 通讯客户端。
    /// <para>支持标准连续输出模式和扩展输出模式。</para>
    /// <para>数据帧由秤主动发送或通过命令触发。</para>
    /// </summary>
    public class ToledoClient : TcpDeviceBase, IBatchReadWrite
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── 构造 ────────────────────────────────

        public ToledoClient(string ip, int port = 8000, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  读取称重数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取一次称重数据。
        /// </summary>
        public OperateResult<ToledoStandardData> ReadWeight()
        {
            var recv = ReceiveFrame();
            if (!recv.IsSuccess) return OperateResult<ToledoStandardData>.Failed(recv.Message);

            try
            {
                return OperateResult<ToledoStandardData>.Success(new ToledoStandardData(recv.Content));
            }
            catch (Exception ex)
            {
                return OperateResult<ToledoStandardData>.Failed($"解析托利多数据失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 读取原始字节。
        /// </summary>
        public OperateResult<byte[]> ReadRaw()
        {
            return ReceiveFrame();
        }

        // ═══════════════════════════════════════════
        //  内部实现
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReceiveFrame()
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        if (_stream!.DataAvailable)
                        {
                            int read = _stream.Read(buf, 0, buf.Length);
                            if (read > 0)
                            {
                                for (int i = 0; i < read; i++)
                                {
                                    response.Add(buf[i]);
                                    // 帧结束: CR (0x0D) 或 CR+LF
                                    if (buf[i] == 0x0D && response.Count >= 16)
                                    {
                                        byte[] result = response.ToArray();
                                        RaiseMessageReceived(DataConverter.ToHexString(result));
                                        return OperateResult<byte[]>.Success(result);
                                    }
                                }
                            }
                        }
                        else if (response.Count > 0)
                        {
                            // 等一小段时间看是否还有数据
                            System.Threading.Thread.Sleep(50);
                            if (!_stream.DataAvailable) break;
                        }
                        else
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }

                    if (response.Count > 0)
                    {
                        byte[] result = response.ToArray();
                        RaiseMessageReceived(DataConverter.ToHexString(result));
                        return OperateResult<byte[]>.Success(result);
                    }

                    return OperateResult<byte[]>.Failed("Toledo 响应超时");
                }
                catch (Exception ex)
                {
                    RaiseError($"Toledo 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"Toledo 通讯异常: {ex.Message}");
                }
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                var conn = Connect();
                if (!conn.IsSuccess) throw new InvalidOperationException($"Toledo 连接失败: {conn.Message}");
            }
        }

        public override string ToString() => $"ToledoClient[{Ip}:{Port}]";

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
    }

    /// <summary>
    /// 托利多标准格式称重数据。
    /// </summary>
    public class ToledoStandardData
    {
        /// <summary>净重标志（true=净重，false=毛重）。</summary>
        public bool IsNet { get; set; }

        /// <summary>正负号（true=正，false=负）。</summary>
        public bool Positive { get; set; }

        /// <summary>是否超出范围。</summary>
        public bool BeyondScope { get; set; }

        /// <summary>动态/稳态（true=动态，false=稳态）。</summary>
        public bool IsDynamic { get; set; }

        /// <summary>单位。</summary>
        public string Unit { get; set; } = "kg";

        /// <summary>是否打印。</summary>
        public bool IsPrint { get; set; }

        /// <summary>是否 10 倍扩展。</summary>
        public bool IsTenExtend { get; set; }

        /// <summary>重量值。</summary>
        public float Weight { get; set; }

        /// <summary>皮重值。</summary>
        public float Tare { get; set; }

        /// <summary>皮重类型（0=无，1=按键去皮，2=预置去皮，3=皮重内存）。</summary>
        public int TareType { get; set; }

        /// <summary>数据是否有效。</summary>
        public bool DataValid { get; set; } = true;

        /// <summary>是否为扩展输出模式。</summary>
        public bool IsExpandOutput { get; set; }

        /// <summary>原始数据。</summary>
        public byte[]? SourceData { get; set; }

        /// <summary>
        /// 从原始字节解析托利多数据。
        /// </summary>
        public ToledoStandardData(byte[] buffer)
        {
            if (buffer == null || buffer.Length < 16)
                throw new ArgumentException("数据长度不足");

            SourceData = buffer;

            if (buffer[0] == 0x02)
            {
                // 标准连续输出模式
                ParseStandardOutput(buffer);
            }
            else if (buffer[0] == 0x01)
            {
                // 扩展输出模式
                ParseExpandOutput(buffer);
            }
            else
            {
                throw new ArgumentException($"未知的输出模式: 0x{buffer[0]:X2}");
            }
        }

        /// <summary>默认构造。</summary>
        public ToledoStandardData() { }

        /// <summary>从原始数据解析（公开供测试）。</summary>
        public static OperateResult<ToledoStandardData> ParseFrom(byte[] data)
        {
            try
            {
                return OperateResult<ToledoStandardData>.Success(new ToledoStandardData(data));
            }
            catch (Exception ex)
            {
                return OperateResult<ToledoStandardData>.Failed(ex.Message);
            }
        }

        private void ParseStandardOutput(byte[] buffer)
        {
            // 重量和皮重: 各 6 字节 ASCII at offset 4, 10
            if (buffer.Length >= 16)
            {
                Weight = float.Parse(Encoding.ASCII.GetString(buffer, 4, 6));
                Tare = float.Parse(Encoding.ASCII.GetString(buffer, 10, 6));

                // 小数点位置 (buffer[1] & 7)
                ApplyDecimalPlaces(buffer[1] & 7);
            }

            // 状态位 (buffer[2])
            if (buffer.Length > 2)
            {
                IsNet = (buffer[2] & 0x01) != 0;
                Positive = (buffer[2] & 0x02) != 0;
                BeyondScope = (buffer[2] & 0x04) != 0;
                IsDynamic = (buffer[2] & 0x08) != 0;
            }

            // 单位 (buffer[3] & 7)
            if (buffer.Length > 3)
            {
                Unit = DecodeUnit(buffer[3] & 7, (buffer[2] & 0x10) != 0);
                IsPrint = (buffer[3] & 0x08) != 0;
                IsTenExtend = (buffer[3] & 0x10) != 0;
            }
        }

        private void ParseExpandOutput(byte[] buffer)
        {
            IsExpandOutput = true;

            if (buffer.Length >= 14)
            {
                // 重量: 9 字节 at offset 6
                string weightStr = Encoding.ASCII.GetString(buffer, 6, 9).Replace(" ", "");
                if (!string.IsNullOrEmpty(weightStr) && float.TryParse(weightStr, out float w))
                    Weight = w;

                // 皮重: 8 字节 at offset 15
                if (buffer.Length >= 23)
                {
                    string tareStr = Encoding.ASCII.GetString(buffer, 15, 8).Replace(" ", "");
                    if (!string.IsNullOrEmpty(tareStr) && float.TryParse(tareStr, out float t))
                        Tare = t;
                }
            }

            // 状态位
            if (buffer.Length > 2)
            {
                byte unitCode = (byte)(buffer[2] & 0x0F);
                Unit = DecodeExpandUnit(unitCode);
                IsDynamic = (buffer[2] & 0x40) != 0;
            }

            if (buffer.Length > 3)
            {
                IsNet = (buffer[3] & 0x01) != 0;
                TareType = (buffer[3] & 0x06) >> 1;
            }

            if (buffer.Length > 4)
            {
                DataValid = (buffer[4] & 0x01) != 0;
                BeyondScope = (buffer[4] & 0x02) != 0 || (buffer[4] & 0x04) != 0;
                IsPrint = (buffer[4] & 0x10) != 0;
            }
        }

        private void ApplyDecimalPlaces(int dp)
        {
            switch (dp)
            {
                case 0: Weight *= 100f; Tare *= 100f; break;
                case 1: Weight *= 10f; Tare *= 10f; break;
                case 3: Weight /= 10f; Tare /= 10f; break;
                case 4: Weight /= 100f; Tare /= 100f; break;
                case 5: Weight /= 1000f; Tare /= 1000f; break;
                case 6: Weight /= 10000f; Tare /= 10000f; break;
                case 7: Weight /= 100000f; Tare /= 100000f; break;
            }
        }

        private static string DecodeUnit(int code, bool isKg)
        {
            switch (code)
            {
                case 0: return isKg ? "kg" : "lb";
                case 1: return "g";
                case 2: return "t";
                case 3: return "oz";
                case 4: return "ozt";
                case 5: return "dwt";
                case 6: return "ton";
                case 7: return "newton";
                default: return "unknown";
            }
        }

        private static string DecodeExpandUnit(int code)
        {
            switch (code)
            {
                case 0: return "None";
                case 1: return "lb";
                case 2: return "kg";
                case 3: return "g";
                case 4: return "t";
                case 5: return "ton";
                case 8: return "oz";
                case 9: return "newton";
                default: return "unknown";
            }
        }

        public override string ToString() => $"ToledoData[{Weight} {Unit}]";
    }
}
