using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Kuka
{
    /// <summary>
    /// KUKA 机器人 TCP 通讯客户端。
    /// <para>基于 KUKA TCP 通讯协议，通过变量名读写数据。</para>
    /// <para>默认端口 9999（KUKA TCP）。</para>
    /// <para>协议为纯 ASCII 文本：读取命令 "00" + 变量名，写入命令 "01" + 变量名=值。</para>
    /// </summary>
    public class KukaTcpClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        // KUKA TCP 是纯文本协议，没有固定帧头，需要特殊处理
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── 构造 ────────────────────────────────

        /// <summary>
        /// 创建 KUKA TCP 客户端。
        /// </summary>
        /// <param name="ip">KUKA 控制器 IP 地址。</param>
        /// <param name="port">端口号，默认 9999。</param>
        /// <param name="timeout">超时时间（毫秒），默认 5000。</param>
        public KukaTcpClient(string ip, int port = 9999, int timeout = 5000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  读取
        // ═══════════════════════════════════════════

        /// <summary>
        /// 根据变量名读取原始字节数据。
        /// </summary>
        /// <param name="address">变量名称。</param>
        /// <returns>读取到的原始数据。</returns>
        public OperateResult<byte[]> Read(string address)
        {
            string cmd = BuildReadCommand(address);
            return SendTextCommand(cmd, true);
        }

        /// <summary>
        /// 根据变量名读取字符串数据。
        /// </summary>
        /// <param name="address">变量名称。</param>
        /// <returns>读取到的字符串。</returns>
        public OperateResult<string> ReadString(string address)
        {
            var r = Read(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(Encoding.UTF8.GetString(r.Content));
        }

        /// <summary>
        /// 读取多个变量。
        /// </summary>
        /// <param name="addresses">变量名称数组。</param>
        /// <returns>读取到的原始数据。</returns>
        public OperateResult<byte[]> ReadMulti(string[] addresses)
        {
            string cmd = BuildReadCommands(addresses);
            return SendTextCommand(cmd, true);
        }

        // ═══════════════════════════════════════════
        //  写入
        // ═══════════════════════════════════════════

        /// <summary>
        /// 写入字符串到变量。
        /// </summary>
        /// <param name="address">变量名称。</param>
        /// <param name="value">写入值（字符串）。</param>
        public OperateResult Write(string address, string value)
        {
            return Write(new string[] { address }, new string[] { value });
        }

        /// <summary>
        /// 写入原始字节到变量。
        /// </summary>
        /// <param name="address">变量名称。</param>
        /// <param name="value">写入值（字节数组）。</param>
        public OperateResult Write(string address, byte[] value)
        {
            return Write(address, Encoding.UTF8.GetString(value));
        }

        /// <summary>
        /// 批量写入多个变量。
        /// </summary>
        /// <param name="addresses">变量名称数组。</param>
        /// <param name="values">值数组（与 addresses 一一对应）。</param>
        public OperateResult Write(string[] addresses, string[] values)
        {
            string cmd = BuildWriteCommands(addresses, values);
            return SendTextCommand(cmd, false);
        }

        // ═══════════════════════════════════════════
        //  程序控制
        // ═══════════════════════════════════════════

        /// <summary>启动指定程序。</summary>
        /// <param name="program">程序名称。</param>
        public OperateResult StartProgram(string program)
        {
            return SendTextCommand("03" + program, false);
        }

        /// <summary>复位当前程序。</summary>
        public OperateResult ResetProgram()
        {
            return SendTextCommand("0601", false);
        }

        /// <summary>停止当前程序。</summary>
        public OperateResult StopProgram()
        {
            return SendTextCommand("0621", false);
        }

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        /// <summary>构建读取单个变量的命令。</summary>
        public static string BuildReadCommand(string address)
        {
            return "00" + (address ?? "");
        }

        /// <summary>构建读取多个变量的命令。</summary>
        public static string BuildReadCommands(string[] addresses)
        {
            if (addresses == null || addresses.Length == 0)
                return "00";

            var sb = new StringBuilder("00");
            for (int i = 0; i < addresses.Length; i++)
            {
                sb.Append(addresses[i] ?? "");
                if (i < addresses.Length - 1)
                    sb.Append(",");
            }
            return sb.ToString();
        }

        /// <summary>构建写入单个变量的命令。</summary>
        public static string BuildWriteCommand(string address, string value)
        {
            return BuildWriteCommands(new string[] { address }, new string[] { value });
        }

        /// <summary>构建写入多个变量的命令。</summary>
        public static string BuildWriteCommands(string[] addresses, string[] values)
        {
            if (addresses == null || values == null)
                return "01";
            if (addresses.Length != values.Length)
                throw new ArgumentException("地址和值的数量不匹配");

            var sb = new StringBuilder("01");
            for (int i = 0; i < addresses.Length; i++)
            {
                sb.Append(addresses[i] ?? "");
                sb.Append("=");
                sb.Append(values[i] ?? "");
                if (i < addresses.Length - 1)
                    sb.Append(",");
            }
            return sb.ToString();
        }

        // ═══════════════════════════════════════════
        //  内部实现
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendTextCommand(string cmd, bool expectData)
        {
            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    byte[] sendBytes = Encoding.UTF8.GetBytes(cmd);
                    RaiseMessageSent(Encoding.UTF8.GetString(sendBytes));

                    _stream!.Write(sendBytes, 0, sendBytes.Length);

                    // 读取响应 — 纯文本，读到连接关闭或超时
                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[4096];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        if (_stream.DataAvailable)
                        {
                            int read = _stream.Read(buf, 0, buf.Length);
                            if (read > 0)
                            {
                                response.AddRange(buf);
                                // 短暂等待看是否有更多数据
                                System.Threading.Thread.Sleep(50);
                                continue;
                            }
                        }
                        else if (response.Count > 0)
                        {
                            break; // 有数据且无更多数据可读
                        }
                        System.Threading.Thread.Sleep(10);
                    }

                    if (response.Count == 0)
                        return OperateResult<byte[]>.Failed("KUKA TCP 响应超时");

                    byte[] result = response.ToArray();
                    RaiseMessageReceived(Encoding.UTF8.GetString(result));

                    string text = Encoding.UTF8.GetString(result);
                    if (text.ToLowerInvariant().Contains("err"))
                        return OperateResult<byte[]>.Failed("KUKA 返回错误: " + text);

                    return OperateResult<byte[]>.Success(result);
                }
                catch (Exception ex)
                {
                    RaiseError($"KUKA TCP 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"KUKA TCP 通讯异常: {ex.Message}");
                }
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                var conn = Connect();
                if (!conn.IsSuccess) throw new InvalidOperationException($"KUKA TCP 连接失败: {conn.Message}");
            }
        }

        public override string ToString() => $"KukaTcpClient[{Ip}:{Port}]";

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
