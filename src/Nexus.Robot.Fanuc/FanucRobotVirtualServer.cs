using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Robot.Fanuc
{
    /// <summary>
    /// FANUC 机器人 SocketMessage 虚拟服务器 — 模拟 FANUC 机器人 TCP 通讯。
    /// <para>用于集成测试，无需真实 FANUC 机器人硬件。</para>
    /// <para>请求帧: MsgId(4) + CmdCode(4) + Index(4) + DataLen(4) + Data</para>
    /// <para>响应帧: MsgId(4) + DataLen(4) + Data（DataLen&lt;0 表示错误）</para>
    /// </summary>
    public class FanucRobotVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 数据模型
        private readonly int[] _numericRegisters = new int[256];
        private readonly double[] _positionRegisters = new double[6 * 32]; // 32 PR, 6 axes each
        private readonly bool[] _digitalInputs = new bool[256];
        private readonly bool[] _digitalOutputs = new bool[256];
        private readonly int[] _groupInputs = new int[256];
        private readonly int[] _groupOutputs = new int[256];
        private readonly string[] _stringRegisters = new string[32];
        private readonly double[] _robotPosition = new double[6];
        private int _robotMode = 2;   // 自动
        private int _robotState;      // 停止

        // 命令码
        private const int CMD_READ_NUMERIC_REG = 1;
        private const int CMD_WRITE_NUMERIC_REG = 2;
        private const int CMD_READ_POS_REG = 3;
        private const int CMD_WRITE_POS_REG = 4;
        private const int CMD_READ_STRING_REG = 5;
        private const int CMD_WRITE_STRING_REG = 6;
        private const int CMD_READ_DI = 10;
        private const int CMD_READ_DO = 11;
        private const int CMD_WRITE_DO = 12;
        private const int CMD_READ_GI = 13;
        private const int CMD_READ_GO = 14;
        private const int CMD_WRITE_GO = 15;
        private const int CMD_READ_ROBOT_POS = 20;
        private const int CMD_READ_STATUS = 21;
        private const int CMD_SEND_STRING = 30;

        private const int REQUEST_HEADER_SIZE = 16;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>累计接收的 TCP 连接数量。</summary>
        public int ConnectionCount => _connectionCount;

        public FanucRobotVirtualServer(int port = 60008)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置数值寄存器。</summary>
        public void SetNumericRegister(int index, int value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _numericRegisters[index] = value;
        }

        /// <summary>设置数字输入。</summary>
        public void SetDigitalInput(int index, bool value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _digitalInputs[index] = value;
        }

        /// <summary>设置数字输出。</summary>
        public void SetDigitalOutput(int index, bool value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _digitalOutputs[index] = value;
        }

        /// <summary>设置组输入。</summary>
        public void SetGroupInput(int index, int value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _groupInputs[index] = value;
        }

        /// <summary>设置组输出。</summary>
        public void SetGroupOutput(int index, int value)
        {
            if (index >= 0 && index < 256) lock (_dataLock) _groupOutputs[index] = value;
        }

        /// <summary>设置机器人位置（6轴）。</summary>
        public void SetRobotPosition(int axis, double value)
        {
            if (axis >= 0 && axis < 6) lock (_dataLock) _robotPosition[axis] = value;
        }

        /// <summary>设置机器人状态。</summary>
        public void SetRobotStatus(int mode, int state)
        {
            lock (_dataLock) { _robotMode = mode; _robotState = state; }
        }

        /// <summary>设置字符串寄存器。</summary>
        public void SetStringRegister(int index, string value)
        {
            if (index >= 0 && index < 32) lock (_dataLock) _stringRegisters[index] = value ?? "";
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
                        // 读取 16 字节请求头
                        byte[]? header = ReadExact(stream, REQUEST_HEADER_SIZE);
                        if (header == null) break;

                        int msgId = BitConverter.ToInt32(header, 0);
                        int cmdCode = BitConverter.ToInt32(header, 4);
                        int index = BitConverter.ToInt32(header, 8);
                        int dataLen = BitConverter.ToInt32(header, 12);

                        // 读取数据部分
                        byte[]? requestData = null;
                        if (dataLen > 0)
                        {
                            requestData = ReadExact(stream, dataLen);
                            if (requestData == null) break;
                        }

                        // 处理命令
                        byte[] response = HandleCommand(msgId, cmdCode, index, requestData);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] HandleCommand(int msgId, int cmdCode, int index, byte[]? requestData)
        {
            switch (cmdCode)
            {
                case CMD_READ_NUMERIC_REG:
                    return BuildIntResponse(msgId, GetNumericReg(index));

                case CMD_WRITE_NUMERIC_REG:
                    if (requestData != null && requestData.Length >= 4 && index >= 0 && index < 256)
                    {
                        int value = BitConverter.ToInt32(requestData, 0);
                        lock (_dataLock) _numericRegisters[index] = value;
                    }
                    return BuildSuccessResponse(msgId);

                case CMD_READ_POS_REG:
                    return BuildPositionResponse(msgId, index);

                case CMD_WRITE_POS_REG:
                    if (requestData != null && index >= 0 && index < 32)
                    {
                        lock (_dataLock)
                        {
                            int axes = Math.Min(6, requestData.Length / 8);
                            for (int i = 0; i < axes; i++)
                                _positionRegisters[index * 6 + i] = BitConverter.ToDouble(requestData, i * 8);
                        }
                    }
                    return BuildSuccessResponse(msgId);

                case CMD_READ_STRING_REG:
                    return BuildStringResponse(msgId, index);

                case CMD_WRITE_STRING_REG:
                    if (requestData != null && index >= 0 && index < 32)
                    {
                        int strLen = BitConverter.ToInt32(requestData, 0);
                        string str = strLen > 0 ? Encoding.ASCII.GetString(requestData, 4, Math.Min(strLen, requestData.Length - 4)) : "";
                        lock (_dataLock) _stringRegisters[index] = str;
                    }
                    return BuildSuccessResponse(msgId);

                case CMD_READ_DI:
                    {
                        bool val = index >= 0 && index < 256 && _digitalInputs[index];
                        return BuildIntResponse(msgId, val ? 1 : 0);
                    }

                case CMD_READ_DO:
                    {
                        bool val = index >= 0 && index < 256 && _digitalOutputs[index];
                        return BuildIntResponse(msgId, val ? 1 : 0);
                    }

                case CMD_WRITE_DO:
                    if (requestData != null && requestData.Length >= 4 && index >= 0 && index < 256)
                    {
                        int val = BitConverter.ToInt32(requestData, 0);
                        lock (_dataLock) _digitalOutputs[index] = val != 0;
                    }
                    return BuildSuccessResponse(msgId);

                case CMD_READ_GI:
                    return BuildIntResponse(msgId, GetGroupInput(index));

                case CMD_READ_GO:
                    return BuildIntResponse(msgId, GetGroupOutput(index));

                case CMD_WRITE_GO:
                    if (requestData != null && requestData.Length >= 4 && index >= 0 && index < 256)
                    {
                        int val = BitConverter.ToInt32(requestData, 0);
                        lock (_dataLock) _groupOutputs[index] = val;
                    }
                    return BuildSuccessResponse(msgId);

                case CMD_READ_ROBOT_POS:
                    return BuildRobotPositionResponse(msgId);

                case CMD_READ_STATUS:
                    return BuildRobotStatusResponse(msgId);

                case CMD_SEND_STRING:
                    return BuildSuccessResponse(msgId);

                default:
                    return BuildErrorResponse(msgId, -1);
            }
        }

        private int GetNumericReg(int index)
        {
            if (index >= 0 && index < 256) lock (_dataLock) return _numericRegisters[index];
            return 0;
        }

        private int GetGroupInput(int index)
        {
            if (index >= 0 && index < 256) lock (_dataLock) return _groupInputs[index];
            return 0;
        }

        private int GetGroupOutput(int index)
        {
            if (index >= 0 && index < 256) lock (_dataLock) return _groupOutputs[index];
            return 0;
        }

        // ── 响应构建 ──

        private static byte[] BuildIntResponse(int msgId, int value)
        {
            byte[] resp = new byte[12];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(4).CopyTo(resp, 4);  // 数据长度
            BitConverter.GetBytes(value).CopyTo(resp, 8);
            return resp;
        }

        private static byte[] BuildSuccessResponse(int msgId)
        {
            byte[] resp = new byte[8];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(0).CopyTo(resp, 4);  // 成功，无数据
            return resp;
        }

        private static byte[] BuildErrorResponse(int msgId, int errorCode)
        {
            byte[] resp = new byte[8];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(errorCode).CopyTo(resp, 4);
            return resp;
        }

        private byte[] BuildPositionResponse(int msgId, int index)
        {
            byte[] resp = new byte[8 + 6 * 8];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(48).CopyTo(resp, 4); // 6 doubles
            lock (_dataLock)
            {
                int baseIdx = (index >= 0 && index < 32) ? index * 6 : 0;
                for (int i = 0; i < 6; i++)
                    BitConverter.GetBytes(_positionRegisters[baseIdx + i]).CopyTo(resp, 8 + i * 8);
            }
            return resp;
        }

        private byte[] BuildStringResponse(int msgId, int index)
        {
            string str = "";
            if (index >= 0 && index < 32) lock (_dataLock) str = _stringRegisters[index] ?? "";
            byte[] strBytes = Encoding.ASCII.GetBytes(str);
            byte[] resp = new byte[8 + 4 + strBytes.Length];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(4 + strBytes.Length).CopyTo(resp, 4);
            BitConverter.GetBytes(strBytes.Length).CopyTo(resp, 8);
            strBytes.CopyTo(resp, 12);
            return resp;
        }

        private byte[] BuildRobotPositionResponse(int msgId)
        {
            byte[] resp = new byte[8 + 6 * 8];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(48).CopyTo(resp, 4);
            lock (_dataLock)
            {
                for (int i = 0; i < 6; i++)
                    BitConverter.GetBytes(_robotPosition[i]).CopyTo(resp, 8 + i * 8);
            }
            return resp;
        }

        private byte[] BuildRobotStatusResponse(int msgId)
        {
            byte[] resp = new byte[16];
            BitConverter.GetBytes(msgId).CopyTo(resp, 0);
            BitConverter.GetBytes(8).CopyTo(resp, 4);
            lock (_dataLock)
            {
                BitConverter.GetBytes(_robotMode).CopyTo(resp, 8);
                BitConverter.GetBytes(_robotState).CopyTo(resp, 12);
            }
            return resp;
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
