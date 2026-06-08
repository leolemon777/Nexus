using System;
using System.Text;

namespace Nexus.Robot.Yamaha
{
    /// <summary>
    /// 雅马哈（YAMAHA）机器人 RCX 控制器通讯客户端。
    /// <para>基于 ASCII 文本协议，命令以 CRLF 结尾。</para>
    /// <para>响应格式: "OK\r\n" 成功 / "NG=错误码\r\n" 失败 / "END\r\n" 数据结束。</para>
    /// <para>默认端口由 RCX 控制器配置决定。</para>
    /// </summary>
    public class YamahaRcxClient : TcpDeviceBase
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
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        if (_stream.DataAvailable)
                        {
                            int read = _stream.Read(buf, 0, buf.Length);
                            if (read > 0) response.AddRange(buf);

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
            var r = ReadCommand($"＠ LOAD <{program}>, T{taskId}");
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
    }
}
