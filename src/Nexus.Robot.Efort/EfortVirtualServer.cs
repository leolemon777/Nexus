using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Efort
{
    /// <summary>
    /// 埃夫特机器人虚拟服务器 — 模拟 ER7BC10 协议通讯。
    /// <para>用于集成测试，无需真实埃夫特机器人硬件。</para>
    /// <para>请求：38 字节固定帧 (MessageHead + 长度 + 命令码 + 心跳 + MessageTail)</para>
    /// <para>响应：788 字节固定帧 (MessageHead + 状态数据 + 轴数据 + MessageTail)</para>
    /// </summary>
    public class EfortVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private byte _errorStatus;
        private byte _emergencyStopStatus = 1;
        private byte _servoStatus;
        private byte _axisMoveStatus = 0;
        private byte _programRunStatus = 0;
        private ushort _modeStatus = 1;
        private ushort _speedStatus = 100;
        private readonly float[] _axisPositions = new float[7];
        private readonly float[] _cartesianPositions = new float[6];
        private readonly float[] _axisSpeeds = new float[7];
        private readonly float[] _axisTorques = new float[7];
        private readonly byte[] _digitalOutputs = new byte[32];
        private readonly byte[] _digitalInputs = new byte[32];
        private readonly int[] _integerOutputs = new int[32];
        private readonly int[] _integerInputs = new int[32];

        private const string MSG_HEAD = "MessageHead";
        private const string MSG_TAIL = "MessageTail";
        private const ushort CMD_READ = 1001;
        private const int REQUEST_LENGTH = 38;
        private const int RESPONSE_LENGTH = 788;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>累计接收的 TCP 连接数量。</summary>
        public int ConnectionCount => _connectionCount;

        public EfortVirtualServer(int port = 18008)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置伺服状态。</summary>
        public void SetServoStatus(byte status) { lock (_dataLock) _servoStatus = status; }

        /// <summary>设置轴角度。</summary>
        public void SetAxisPosition(int axis, float value)
        {
            if (axis >= 0 && axis < 7) lock (_dataLock) _axisPositions[axis] = value;
        }

        /// <summary>设置笛卡尔坐标。</summary>
        public void SetCartesianPosition(int index, float value)
        {
            if (index >= 0 && index < 6) lock (_dataLock) _cartesianPositions[index] = value;
        }

        /// <summary>设置错误状态。</summary>
        public void SetErrorStatus(byte status) { lock (_dataLock) _errorStatus = status; }

        /// <summary>设置运行模式。</summary>
        public void SetModeStatus(ushort mode) { lock (_dataLock) _modeStatus = mode; }

        /// <summary>设置速度百分比。</summary>
        public void SetSpeedStatus(ushort speed) { lock (_dataLock) _speedStatus = speed; }

        /// <summary>设置数字输入。</summary>
        public void SetDigitalInput(int index, byte value)
        {
            if (index >= 0 && index < 32) lock (_dataLock) _digitalInputs[index] = value;
        }

        // ── 服务器控制 ──

        /// <summary>启动虚拟服务器。</summary>
        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        /// <summary>停止虚拟服务器。</summary>
        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _acceptThread?.Join(2000);
        }

        public void Dispose()
        {
            Stop();
            _listener = null;
        }

        // ── 内部实现 ──

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    Interlocked.Increment(ref _connectionCount);
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        // 读取 38 字节固定请求
                        byte[]? request = ReadExact(stream, REQUEST_LENGTH);
                        if (request == null) break;

                        // 验证帧头
                        string head = Encoding.ASCII.GetString(request, 0, Math.Min(MSG_HEAD.Length, request.Length));
                        if (head != MSG_HEAD) continue;

                        // 验证命令码
                        ushort cmd = BitConverter.ToUInt16(request, 18);
                        if (cmd != CMD_READ) continue;

                        // 构建并发送 788 字节响应
                        byte[] response = BuildResponse();
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] BuildResponse()
        {
            byte[] data = new byte[RESPONSE_LENGTH];

            lock (_dataLock)
            {
                // PacketStart: 16 字节 ASCII at offset 0
                byte[] headBytes = Encoding.ASCII.GetBytes(MSG_HEAD);
                Array.Copy(headBytes, 0, data, 0, headBytes.Length);

                // 总长度 (2B LE) at offset 16
                data[16] = (byte)(RESPONSE_LENGTH & 0xFF);
                data[17] = (byte)(RESPONSE_LENGTH >> 8);

                // 命令码 (2B LE) at offset 18
                data[18] = (byte)(CMD_READ & 0xFF);
                data[19] = (byte)(CMD_READ >> 8);

                // 心跳 (2B LE) at offset 20
                data[20] = 0;
                data[21] = 0;

                // 状态字节 at offset 22-29
                data[22] = _errorStatus;
                data[23] = _emergencyStopStatus;
                data[24] = 1; // AuthorityStatus
                data[25] = _servoStatus;
                data[26] = _axisMoveStatus;
                data[27] = _programRunStatus;
                data[28] = 0; // ProgramLoadStatus
                data[29] = 0; // ProgramHoldStatus

                // ModeStatus (2B LE) at offset 30
                data[30] = (byte)(_modeStatus & 0xFF);
                data[31] = (byte)(_modeStatus >> 8);

                // SpeedStatus (2B LE) at offset 32
                data[32] = (byte)(_speedStatus & 0xFF);
                data[33] = (byte)(_speedStatus >> 8);

                // DigitalOutputs: 32 bytes at offset 34
                Array.Copy(_digitalOutputs, 0, data, 34, 32);

                // DigitalInputs: 32 bytes at offset 66
                Array.Copy(_digitalInputs, 0, data, 66, 32);

                // IntegerOutputs: 32 × int at offset 100
                for (int i = 0; i < 32; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_integerOutputs[i]), 0, data, 100 + 4 * i, 4);

                // IntegerInputs: 32 × int at offset 228
                for (int i = 0; i < 32; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_integerInputs[i]), 0, data, 228 + 4 * i, 4);

                // ProjectName: 32 bytes at offset 356
                // ProgramName: 32 bytes at offset 388
                // ErrorText: 128 bytes at offset 420
                // (left as zeros)

                // AxisPositions: 7 × float at offset 548
                for (int i = 0; i < 7; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_axisPositions[i]), 0, data, 548 + 4 * i, 4);

                // CartesianPositions: 6 × float at offset 576
                for (int i = 0; i < 6; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_cartesianPositions[i]), 0, data, 576 + 4 * i, 4);

                // AxisSpeeds: 7 × float at offset 600
                for (int i = 0; i < 7; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_axisSpeeds[i]), 0, data, 600 + 4 * i, 4);

                // AxisAccelerations: 7 × float at offset 628 (zeros)
                // AxisJerk: 7 × float at offset 656 (zeros)

                // AxisTorques: 7 × float at offset 684
                for (int i = 0; i < 7; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(_axisTorques[i]), 0, data, 684 + 4 * i, 4);

                // AxisDirectionCounts: 7 × int at offset 712 (zeros)
                // AxisWorkTimes: 7 × int at offset 740 (zeros)
                // DeviceUptime: int at offset 768 (zero)

                // PacketEnd: 16 字节 ASCII at offset 772
                byte[] tailBytes = Encoding.ASCII.GetBytes(MSG_TAIL);
                Array.Copy(tailBytes, 0, data, 772, tailBytes.Length);
            }

            return data;
        }

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            try
            {
                byte[] buffer = new byte[count];
                int offset = 0;
                while (offset < count)
                {
                    int read = stream.Read(buffer, offset, count - offset);
                    if (read == 0) return null;
                    offset += read;
                }
                return buffer;
            }
            catch { return null; }
        }
    }
}
