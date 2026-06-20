using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Robot.Yamaha
{
    /// <summary>
    /// 雅马哈（YAMAHA）机器人 RCX 控制器通讯客户端。
    /// <para>基于 ASCII 文本协议，命令以 CRLF 结尾。</para>
    /// <para>响应格式: "OK\r\n" 成功 / "NG=错误码\r\n" 失败 / "END\r\n" 数据结束。</para>
    /// <para>默认端口由 RCX 控制器配置决定。</para>
    /// </summary>
    public class YamahaRcxClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        // ── TcpDeviceBase 抽象实现 ───────────────
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ── 构造 ────────────────────────────────

        public YamahaRcxClient(string ip, int port = 80, int timeout = 10000)
            : base(ip, port, timeout) { }

        // ═══════════════════════════════════════════
        //  发送命令
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送命令并读取响应（支持多行响应直到 OK/NG/END）。
        /// </summary>
        /// <param name="command">命令字符串（不含 CRLF）。</param>
        /// <returns>响应行数组。</returns>
        public OperateResult<string[]> ReadCommand(string command)
        {
            if (command == null)
                return OperateResult<string[]>.Failed("命令不能为 null");

            if (!command.EndsWith("\r\n"))
                command += "\r\n";

            lock (_lock)
            {
                try
                {
                    EnsureConnected();
                    byte[] sendBytes = Encoding.ASCII.GetBytes(command);
                    RaiseMessageSent(command.TrimEnd('\r', '\n'));

                    _stream!.Write(sendBytes, 0, sendBytes.Length);

                    // 读取响应直到 OK/NG/END + CRLF
                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[4096];
                    int start = Environment.TickCount;

                    while (unchecked(Environment.TickCount - start) < Timeout)
                    {
                        if (_stream.DataAvailable)
                        {
                            int read = _stream.Read(buf, 0, buf.Length);
                            if (read > 0)
                            {
                                byte[] chunk = new byte[read];
                                Array.Copy(buf, chunk, read);
                                response.AddRange(chunk);
                            }

                            // 检查是否以 OK\r\n, NG=...\r\n, END\r\n 结尾
                            string text = Encoding.ASCII.GetString(response.ToArray());
                            if (text.EndsWith("OK\r\n") || text.EndsWith("END\r\n") ||
                                (text.Contains("NG=") && text.EndsWith("\r\n")))
                                break;
                        }
                        else if (response.Count > 0)
                        {
                            System.Threading.Thread.Sleep(50);
                            if (!_stream.DataAvailable) break;
                        }
                        else
                        {
                            System.Threading.Thread.Sleep(10);
                        }
                    }

                    if (response.Count == 0)
                        return OperateResult<string[]>.Failed("YAMAHA RCX 响应超时");

                    string responseText = Encoding.ASCII.GetString(response.ToArray());
                    RaiseMessageReceived(responseText.TrimEnd('\r', '\n'));

                    // 检查错误
                    if (responseText.Contains("NG="))
                    {
                        string errLine = responseText.TrimEnd('\r', '\n');
                        return OperateResult<string[]>.Failed($"YAMAHA RCX 错误: {errLine}");
                    }

                    // 分割响应行
                    string[] lines = responseText.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    return OperateResult<string[]>.Success(lines);
                }
                catch (Exception ex)
                {
                    RaiseError($"YAMAHA RCX 通讯异常: {ex.Message}");
                    return OperateResult<string[]>.Failed($"YAMAHA RCX 通讯异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  机器人状态
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取马达电源状态。
        /// <para>返回: 0=关闭，1=开启，2=开启+所有伺服开启。</para>
        /// </summary>
        public OperateResult<int> ReadMotorStatus()
        {
            return ReadIntCommand("@?MOTOR ");
        }

        /// <summary>
        /// 读取模式状态。
        /// </summary>
        public OperateResult<int> ReadModeStatus()
        {
            return ReadIntCommand("@?MODE ");
        }

        /// <summary>
        /// 读取急停状态。
        /// <para>返回: 0=正常，1=急停。</para>
        /// </summary>
        public OperateResult<int> ReadEmergencyStatus()
        {
            return ReadIntCommand("@?EMG ");
        }

        /// <summary>
        /// 读取关节位置数据（各轴角度）。
        /// </summary>
        public OperateResult<float[]> ReadJoints()
        {
            var r = ReadCommand("@?WHERE ");
            if (!r.IsSuccess) return OperateResult<float[]>.Failed(r.Message);
            try
            {
                var values = new System.Collections.Generic.List<float>();
                foreach (string line in r.Content)
                {
                    foreach (string part in line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (float.TryParse(part, out float val))
                            values.Add(val);
                    }
                }
                return OperateResult<float[]>.Success(values.ToArray());
            }
            catch (Exception ex)
            {
                return OperateResult<float[]>.Failed($"解析关节数据失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  IO 读取
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取数字输入。
        /// </summary>
        /// <param name="index">DI 索引。</param>
        public OperateResult<bool[]> ReadDI(int index)
        {
            return ReadBoolArrayCommand($"@?DI{index}()");
        }

        /// <summary>
        /// 读取数字输出。
        /// </summary>
        /// <param name="index">DO 索引。</param>
        public OperateResult<bool[]> ReadDO(int index)
        {
            return ReadBoolArrayCommand($"@?DO{index}()");
        }

        // ═══════════════════════════════════════════
        //  程序控制
        // ═══════════════════════════════════════════

        /// <summary>复位所有程序。</summary>
        public OperateResult Reset()
        {
            var r = ReadCommand("@ RESET ");
            if (!r.IsSuccess) return r;
            return OperateResult.Success();
        }

        /// <summary>运行所有 RUN 状态程序。</summary>
        public OperateResult Run()
        {
            var r = ReadCommand("@ RUN ");
            if (!r.IsSuccess) return r;
            return OperateResult.Success();
        }

        /// <summary>停止所有 STOP 状态程序。</summary>
        public OperateResult Stop()
        {
            var r = ReadCommand("@ STOP ");
            if (!r.IsSuccess) return r;
            return OperateResult.Success();
        }

        /// <summary>加载程序到指定任务。</summary>
        /// <param name="program">程序名称。</param>
        /// <param name="taskId">任务编号。</param>
        public OperateResult Load(string program, int taskId)
        {
            var r = ReadCommand($"@ LOAD <{program}>, T{taskId}");
            if (!r.IsSuccess) return r;
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  命令构建（公开供测试）
        // ═══════════════════════════════════════════

        /// <summary>构建命令字符串（添加 CRLF）。</summary>
        public static string BuildCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return "\r\n";
            if (command.EndsWith("\r\n")) return command;
            return command + "\r\n";
        }

        // ═══════════════════════════════════════════
        //  内部辅助
        // ═══════════════════════════════════════════

        private OperateResult<int> ReadIntCommand(string command)
        {
            var r = ReadCommand(command);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            try
            {
                if (r.Content.Length == 0) return OperateResult<int>.Failed("无响应数据");
                return OperateResult<int>.Success(Convert.ToInt32(r.Content[0]));
            }
            catch (Exception ex)
            {
                return OperateResult<int>.Failed($"解析整数失败: {ex.Message}");
            }
        }

        private OperateResult<bool[]> ReadBoolArrayCommand(string command)
        {
            var r = ReadCommand(command);
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message);
            try
            {
                if (r.Content.Length == 0) return OperateResult<bool[]>.Failed("无响应数据");
                int value = Convert.ToInt32(r.Content[0]);
                var bits = new bool[8];
                for (int i = 0; i < 8; i++)
                    bits[i] = (value & (1 << i)) != 0;
                return OperateResult<bool[]>.Success(bits);
            }
            catch (Exception ex)
            {
                return OperateResult<bool[]>.Failed($"解析 bool 数组失败: {ex.Message}");
            }
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
            {
                var conn = Connect();
                if (!conn.IsSuccess) throw new InvalidOperationException($"YAMAHA RCX 连接失败: {conn.Message}");
            }
        }

        public override string ToString() => $"YamahaRcxClient[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 类型化读写
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadCommand($"@?{address}");
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            string text = string.Join(" ", r.Content);
            byte[] data = Encoding.ASCII.GetBytes(text);
            if (data.Length > length)
            {
                byte[] trimmed = new byte[length];
                Buffer.BlockCopy(data, 0, trimmed, 0, length);
                data = trimmed;
            }
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            return Write(address, Encoding.ASCII.GetString(data));
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
            var r = ReadBytes(address, length);
            return r.IsSuccess ? OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length)) : OperateResult<string>.Failed(r.Message);
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
            => ReadCommand($"@ {address}={value}").IsSuccess ? OperateResult.Success() : OperateResult.Failed($"写入 {address} 失败");

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
            try { return System.Text.Encoding.ASCII.GetBytes(BuildCommand("STATUS?")); }
            catch { return null; }
        }
    }
}
