using System;
using System.Text;
using Nexus.Modbus;

namespace Nexus.Robot.Estun
{
    /// <summary>
    /// 埃斯顿（Estun）机器人 Modbus TCP 通讯客户端。
    /// <para>底层使用 Modbus TCP 协议，端口 502。</para>
    /// <para>通过寄存器读写实现机器人数据读取和控制操作。</para>
    /// <para>地址映射: 0-99=机器人状态数据，99=命令控制字，51=命令参数，52=全局速度。</para>
    /// </summary>
    public class EstunClient
    {
        private readonly ModbusTcpClient _modbus;

        /// <summary>站号。</summary>
        public byte Station { get; set; } = 1;

        // ── 构造 ────────────────────────────────

        /// <summary>
        /// 创建埃斯顿机器人客户端。
        /// </summary>
        /// <param name="ip">机器人控制器 IP 地址。</param>
        /// <param name="port">端口号，默认 502。</param>
        /// <param name="timeout">超时时间（毫秒），默认 5000。</param>
        public EstunClient(string ip, int port = 502, int timeout = 5000)
        {
            _modbus = new ModbusTcpClient(ip, port, Station, timeout);
        }

        /// <summary>连接到机器人。</summary>
        public OperateResult Connect()
        {
            _modbus.Station = Station;
            return _modbus.Connect();
        }

        /// <summary>断开连接。</summary>
        public void Disconnect()
        {
            _modbus.Disconnect();
        }

        public bool IsConnected => _modbus.IsConnected;

        // ── 事件透传 ─────────────────────────────
        public event EventHandler? OnConnected { add { _modbus.OnConnected += value; } remove { _modbus.OnConnected -= value; } }
        public event EventHandler? OnDisconnected { add { _modbus.OnDisconnected += value; } remove { _modbus.OnDisconnected -= value; } }
        public event EventHandler<string>? OnError { add { _modbus.OnError += value; } remove { _modbus.OnError -= value; } }

        // ═══════════════════════════════════════════
        //  读取机器人数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取机器人完整状态数据（地址 0，100 个字）。
        /// </summary>
        public OperateResult<EstunData> ReadRobotData()
        {
            var r = _modbus.ReadBytes("0", 100);
            if (!r.IsSuccess) return OperateResult<EstunData>.Failed(r.Message);
            return OperateResult<EstunData>.Success(new EstunData(r.Content));
        }

        // ═══════════════════════════════════════════
        //  机器人控制
        // ═══════════════════════════════════════════

        /// <summary>启动机器人程序。</summary>
        public OperateResult RobotStart()
        {
            return ExecuteCommand(4);
        }

        /// <summary>停止机器人程序。</summary>
        public OperateResult RobotStop()
        {
            return ExecuteCommand(8);
        }

        /// <summary>复位机器人错误。</summary>
        public OperateResult RobotResetError()
        {
            return ExecuteCommand(16);
        }

        /// <summary>加载指定程序到机器人。</summary>
        /// <param name="projectName">程序名称（最长 20 字节）。</param>
        public OperateResult RobotLoadProject(string projectName)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(projectName ?? "");
            byte[] padded = new byte[20];
            int copyLen = Math.Min(nameBytes.Length, 20);
            Array.Copy(nameBytes, padded, copyLen);
            // Word-swap for Modbus
            byte[] swapped = new byte[20];
            for (int i = 0; i < 20; i += 2)
            {
                if (i + 1 < 20) { swapped[i] = padded[i + 1]; swapped[i + 1] = padded[i]; }
                else { swapped[i] = padded[i]; }
            }

            var w = _modbus.Write("53", swapped);
            if (!w.IsSuccess) return OperateResult.Failed("写入程序名失败: " + w.Message);
            return ExecuteCommand(128);
        }

        /// <summary>卸载程序。</summary>
        public OperateResult RobotUnloadProject()
        {
            return ExecuteCommand(256);
        }

        /// <summary>设置全局速度值。</summary>
        /// <param name="speed">速度值。</param>
        public OperateResult SetGlobalSpeed(short speed)
        {
            var w = _modbus.Write("52", speed);
            if (!w.IsSuccess) return w;
            return ExecuteCommand(512);
        }

        /// <summary>重启命令状态。</summary>
        public OperateResult CommandStatusRestart()
        {
            return ExecuteCommand(1024);
        }

        // ═══════════════════════════════════════════
        //  内部实现
        // ═══════════════════════════════════════════

        private OperateResult ExecuteCommand(short command)
        {
            // 步骤1: 检查 40100(地址99) 和 40052(地址51) 是否为 0
            var check99 = _modbus.ReadInt16("99");
            if (!check99.IsSuccess) return OperateResult.Failed("步骤1 检查 40100 失败: " + check99.Message);
            var check51 = _modbus.ReadInt16("51");
            if (!check51.IsSuccess) return OperateResult.Failed("步骤1 检查 40052 失败: " + check51.Message);
            if (check99.Content != 0) return OperateResult.Failed($"步骤1: 40100 值不为 0，实际值 {check99.Content}");
            if (check51.Content != 0) return OperateResult.Failed($"步骤1: 40052 值不为 0，实际值 {check51.Content}");

            // 步骤2: 写 40100 = 0x11 (17)
            var w2 = _modbus.Write("99", (short)17);
            if (!w2.IsSuccess) return OperateResult.Failed("步骤2 写 40100 失败: " + w2.Message);

            // 步骤3: 等待 40019 = 0x0801 (2049)
            int retry = 0;
            while (retry < 20)
            {
                var r3 = _modbus.ReadInt16("18");
                if (!r3.IsSuccess) return OperateResult.Failed("步骤3 读 40019 失败: " + r3.Message);
                if (r3.Content == 2049) break;
                retry++;
                System.Threading.Thread.Sleep(100);
            }
            if (retry >= 20) return OperateResult.Failed("步骤3 等待 40019=0x0801 超时");

            // 步骤4: 写命令到 40052
            var w4 = _modbus.Write("51", command);
            if (!w4.IsSuccess) return OperateResult.Failed("步骤4 写命令到 40052 失败: " + w4.Message);
            System.Threading.Thread.Sleep(100);

            // 步骤5: 读命令执行状态
            var r5 = _modbus.ReadInt16("18");
            if (!r5.IsSuccess) return OperateResult.Failed("步骤5 读状态失败: " + r5.Message);

            // 步骤6: 清除 40100 和 40052
            _modbus.Write("99", (short)0);
            _modbus.Write("51", (short)0);

            return OperateResult.Success();
        }

        public override string ToString() => $"EstunClient[{_modbus}]";
    }

    /// <summary>
    /// 埃斯顿机器人状态数据结构。
    /// </summary>
    public class EstunData
    {
        /// <summary>手动模式。</summary>
        public bool ManualMode { get; set; }

        /// <summary>自动模式。</summary>
        public bool AutoMode { get; set; }

        /// <summary>远程模式。</summary>
        public bool RemoteMode { get; set; }

        /// <summary>使能状态。</summary>
        public bool EnableStatus { get; set; }

        /// <summary>运行状态。</summary>
        public bool RunStatus { get; set; }

        /// <summary>错误状态。</summary>
        public bool ErrorStatus { get; set; }

        /// <summary>程序运行状态。</summary>
        public bool ProgramRunStatus { get; set; }

        /// <summary>机器人正在动作。</summary>
        public bool RobotMoving { get; set; }

        /// <summary>全局速度值。</summary>
        public short GlobalSpeedValue { get; set; }

        /// <summary>当前加载的工程名。</summary>
        public string ProjectName { get; set; } = "";

        /// <summary>数字输出（64位）。</summary>
        public bool[] DigitalOutputs { get; set; } = new bool[64];

        /// <summary>机器人执行命令状态。</summary>
        public ushort RobotCommandStatus { get; set; }

        /// <summary>模拟输出（32个float）。</summary>
        public float[] AnalogOutputs { get; set; } = new float[16];

        /// <summary>数字输入（64位）。</summary>
        public bool[] DigitalInputs { get; set; } = new bool[64];

        /// <summary>模拟输入（32个float）。</summary>
        public float[] AnalogInputs { get; set; } = new float[16];

        /// <summary>读写标志位。</summary>
        public short ReadWriteFlag { get; set; }

        /// <summary>
        /// 从 Modbus 字节数组解析埃斯顿机器人数据。
        /// </summary>
        public EstunData(byte[] data)
        {
            if (data == null || data.Length < 200) return;

            // 偏移 2: 全局速度值 (Int16 LE)
            GlobalSpeedValue = BitConverter.ToInt16(data, 2);

            // 偏移 7: 状态位字节
            if (data.Length > 7)
            {
                byte status = data[7];
                ManualMode = (status & 0x01) != 0;
                AutoMode = (status & 0x02) != 0;
                RemoteMode = (status & 0x04) != 0;
                EnableStatus = (status & 0x08) != 0;
                RunStatus = (status & 0x10) != 0;
                ErrorStatus = (status & 0x20) != 0;
                ProgramRunStatus = (status & 0x40) != 0;
                RobotMoving = (status & 0x80) != 0;
            }

            // 偏移 8: 工程名 (20 字节, word-swap)
            if (data.Length > 28)
            {
                byte[] nameBytes = new byte[20];
                Array.Copy(data, 8, nameBytes, 0, 20);
                // Word-swap: 每2字节交换
                for (int i = 0; i < 20; i += 2)
                {
                    if (i + 1 < 20)
                    {
                        byte tmp = nameBytes[i];
                        nameBytes[i] = nameBytes[i + 1];
                        nameBytes[i + 1] = tmp;
                    }
                }
                ProjectName = Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');
            }

            // 偏移 28: DO (8 字节 = 64 位)
            if (data.Length > 36)
            {
                for (int i = 0; i < 8 && (28 + i) < data.Length; i++)
                {
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if (i * 8 + bit < 64)
                            DigitalOutputs[i * 8 + bit] = (data[28 + i] & (1 << bit)) != 0;
                    }
                }
            }

            // 偏移 36: RobotCommandStatus (UInt16)
            if (data.Length > 38)
                RobotCommandStatus = BitConverter.ToUInt16(data, 36);

            // 偏移 38: AO (16 × float = 64 字节) — CDAB format
            if (data.Length >= 102)
            {
                for (int i = 0; i < 16; i++)
                    AnalogOutputs[i] = BitConverter.ToSingle(data, 38 + 4 * i);
            }

            // 偏移 126: DI (8 字节 = 64 位)
            if (data.Length > 134)
            {
                for (int i = 0; i < 8 && (126 + i) < data.Length; i++)
                {
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if (i * 8 + bit < 64)
                            DigitalInputs[i * 8 + bit] = (data[126 + i] & (1 << bit)) != 0;
                    }
                }
            }

            // 偏移 134: AI (16 × float = 64 字节)
            if (data.Length >= 198)
            {
                for (int i = 0; i < 16; i++)
                    AnalogInputs[i] = BitConverter.ToSingle(data, 134 + 4 * i);
            }

            // 偏移 198: ReadWriteFlag (Int16)
            if (data.Length > 200)
                ReadWriteFlag = BitConverter.ToInt16(data, 198);
        }
    }
}
