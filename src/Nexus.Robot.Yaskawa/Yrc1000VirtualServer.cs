using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Robot.Yaskawa
{
    /// <summary>
    /// YRC1000 虚拟机器人服务器 — 模拟安川机器人 TCP 协议通讯。
    /// <para>用于集成测试，无需真实 YRC1000 控制器硬件。</para>
    /// <para>帧格式: ReqId(2) + BlockId(1) + Reserved(1) + CmdCode(2) + SubCmd(2) + DataLen(4) + Reserved(4) + Data</para>
    /// </summary>
    public class Yrc1000VirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _dataLock = new object();
        private int _connectionCount;

        // 内存模型
        private readonly bool[] _inputs = new bool[256];
        private readonly bool[] _outputs = new bool[256];
        private readonly int[] _registers = new int[256];
        private readonly byte _servoState = 0;
        private readonly byte _runState = 0;
        private readonly ushort _alarmCode = 0;
        private readonly ushort _errorCode = 0;

        // 命令码
        private const ushort CMD_READ_IO_INPUT = 0x0101;
        private const ushort CMD_READ_IO_OUTPUT = 0x0102;
        private const ushort CMD_WRITE_IO = 0x0103;
        private const ushort CMD_READ_REGISTER = 0x0201;
        private const ushort CMD_WRITE_REGISTER = 0x0202;
        private const ushort CMD_READ_VARIABLE = 0x0301;
        private const ushort CMD_READ_POSITION = 0x0401;
        private const ushort CMD_READ_STATUS = 0x0501;
        private const ushort CMD_SERVO_ON = 0x0601;
        private const ushort CMD_SERVO_OFF = 0x0602;

        private const int HEADER_SIZE = 16;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>累计接收的 TCP 连接数量。</summary>
        public int ConnectionCount => _connectionCount;

        public Yrc1000VirtualServer(int port = 18080)
        {
            Port = port;
        }

        // ── 数据设置方法（测试用） ──

        /// <summary>设置输入信号。</summary>
        public void SetInput(int address, bool value)
        {
            if (address >= 0 && address < 256) lock (_dataLock) _inputs[address] = value;
        }

        /// <summary>设置输出信号。</summary>
        public void SetOutput(int address, bool value)
        {
            if (address >= 0 && address < 256) lock (_dataLock) _outputs[address] = value;
        }

        /// <summary>设置寄存器值。</summary>
        public void SetRegister(int address, int value)
        {
            if (address >= 0 && address < 256) lock (_dataLock) _registers[address] = value;
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
                        // 读取 16 字节头
                        byte[]? header = ReadExact(stream, HEADER_SIZE);
                        if (header == null) break;

                        // 解析数据长度 (big endian, offset 8-11)
                        int dataLen = (header[8] << 24) | (header[9] << 16) | (header[10] << 8) | header[11];

                        // 读取数据部分
                        byte[]? requestData = null;
                        if (dataLen > 0)
                        {
                            requestData = ReadExact(stream, dataLen);
                            if (requestData == null) break;
                        }

                        // 解析命令码
                        ushort cmdCode = (ushort)((header[4] << 8) | header[5]);

                        // 复用请求 ID
                        byte reqIdHi = header[0];
                        byte reqIdLo = header[1];
                        byte blockId = header[2];

                        // 处理命令并生成响应
                        byte[] responseData = HandleCommand(cmdCode, requestData);
                        byte[] response = BuildResponse(reqIdHi, reqIdLo, blockId, responseData);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] HandleCommand(ushort cmdCode, byte[]? requestData)
        {
            switch (cmdCode)
            {
                case CMD_READ_IO_INPUT:
                case CMD_READ_IO_OUTPUT:
                    return HandleReadIO(cmdCode, requestData);

                case CMD_WRITE_IO:
                    HandleWriteIO(requestData);
                    return new byte[0];

                case CMD_READ_REGISTER:
                    return HandleReadRegister(requestData);

                case CMD_WRITE_REGISTER:
                    HandleWriteRegister(requestData);
                    return new byte[0];

                case CMD_READ_STATUS:
                    return HandleReadStatus();

                case CMD_SERVO_ON:
                case CMD_SERVO_OFF:
                    return new byte[0];

                case CMD_READ_POSITION:
                    return HandleReadPosition();

                case CMD_READ_VARIABLE:
                    return HandleReadVariable(requestData);

                default:
                    return new byte[0];
            }
        }

        private byte[] HandleReadIO(ushort cmdCode, byte[]? requestData)
        {
            if (requestData == null || requestData.Length < 8) return new byte[0];

            int address = (requestData[0] << 24) | (requestData[1] << 16) | (requestData[2] << 8) | requestData[3];
            int count = (requestData[4] << 24) | (requestData[5] << 16) | (requestData[6] << 8) | requestData[7];

            var source = cmdCode == CMD_READ_IO_INPUT ? _inputs : _outputs;
            byte[] result = new byte[count];
            lock (_dataLock)
            {
                for (int i = 0; i < count && (address + i) < 256; i++)
                    result[i] = source[address + i] ? (byte)1 : (byte)0;
            }
            return result;
        }

        private void HandleWriteIO(byte[]? requestData)
        {
            if (requestData == null || requestData.Length < 5) return;

            int address = (requestData[0] << 24) | (requestData[1] << 16) | (requestData[2] << 8) | requestData[3];
            byte value = requestData[4];

            lock (_dataLock)
            {
                if (address >= 0 && address < 256)
                    _outputs[address] = value != 0;
            }
        }

        private byte[] HandleReadRegister(byte[]? requestData)
        {
            if (requestData == null || requestData.Length < 8) return new byte[0];

            int address = (requestData[0] << 24) | (requestData[1] << 16) | (requestData[2] << 8) | requestData[3];
            int count = (requestData[4] << 24) | (requestData[5] << 16) | (requestData[6] << 8) | requestData[7];

            byte[] result = new byte[count * 4];
            lock (_dataLock)
            {
                for (int i = 0; i < count && (address + i) < 256; i++)
                {
                    // little endian — client uses BitConverter.ToInt32
                    byte[] val = BitConverter.GetBytes(_registers[address + i]);
                    Buffer.BlockCopy(val, 0, result, i * 4, 4);
                }
            }
            return result;
        }

        private void HandleWriteRegister(byte[]? requestData)
        {
            if (requestData == null || requestData.Length < 8) return;

            // address is big endian
            int address = (requestData[0] << 24) | (requestData[1] << 16) | (requestData[2] << 8) | requestData[3];
            // value is little endian (written via BitConverter.GetBytes)
            int value = BitConverter.ToInt32(requestData, 4);

            lock (_dataLock)
            {
                if (address >= 0 && address < 256)
                    _registers[address] = value;
            }
        }

        private byte[] HandleReadStatus()
        {
            byte[] data = new byte[8];
            lock (_dataLock)
            {
                data[0] = _servoState;
                data[1] = _runState;
                byte[] alarm = BitConverter.GetBytes(_alarmCode);
                data[2] = alarm[0]; data[3] = alarm[1];
                byte[] error = BitConverter.GetBytes(_errorCode);
                data[4] = error[0]; data[5] = error[1];
            }
            return data;
        }

        private byte[] HandleReadPosition()
        {
            // 返回 7 个轴位置（每个 4 字节 float）
            byte[] data = new byte[7 * 4];
            return data;
        }

        private byte[] HandleReadVariable(byte[]? requestData)
        {
            // 简单返回空数据
            return new byte[0];
        }

        private static byte[] BuildResponse(byte reqIdHi, byte reqIdLo, byte blockId, byte[] data)
        {
            int dataLen = data.Length;
            byte[] frame = new byte[HEADER_SIZE + dataLen];

            // ReqId
            frame[0] = reqIdHi;
            frame[1] = reqIdLo;
            // BlockId
            frame[2] = blockId;
            frame[3] = 0x00;
            // Status = 0 (success)
            frame[4] = 0x00;
            frame[5] = 0x00;
            // SubCmd
            frame[6] = 0x00;
            frame[7] = 0x00;
            // DataLen (little endian — client uses BitConverter.ToInt32)
            frame[8] = (byte)(dataLen & 0xFF);
            frame[9] = (byte)(dataLen >> 8);
            frame[10] = (byte)(dataLen >> 16);
            frame[11] = (byte)(dataLen >> 24);
            // Reserved
            frame[12] = 0; frame[13] = 0; frame[14] = 0; frame[15] = 0;

            if (dataLen > 0)
                Array.Copy(data, 0, frame, HEADER_SIZE, dataLen);

            return frame;
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
