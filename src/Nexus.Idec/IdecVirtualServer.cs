using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Idec
{
    /// <summary>
    /// IDEC MicroSmart Computer Link 虚拟 PLC 服务器（ASCII 帧）。
    /// <para>模拟 IDEC PLC 的 Computer Link 协议响应，用于集成测试。</para>
    /// <para>支持 R2 连续读 + W2 连续写，覆盖 D/X/Y/M/T/C 区域。</para>
    /// </summary>
    public class IdecVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();

        // 对象字典式内存：按 (区域, operand号) 存储值
        private readonly ushort[] _wordData = new ushort[20000];   // D/T/C 共用
        private readonly bool[] _bitData = new bool[20000];          // X/Y/M 共用

        public int Port { get; }
        public bool IsRunning => _running;

        public IdecVirtualServer(int port = 5025) { Port = port; }

        // 测试预置/校验
        public void SetWordValue(IdecArea area, int operand, ushort value)
        {
            int idx = WordIndex(area, operand);
            if (idx >= 0) lock (_memLock) _wordData[idx] = value;
        }

        public ushort GetWordValue(IdecArea area, int operand)
        {
            int idx = WordIndex(area, operand);
            if (idx < 0) return 0;
            lock (_memLock) return _wordData[idx];
        }

        public void SetBitValue(IdecArea area, int operand, bool value)
        {
            int idx = BitIndex(area, operand);
            if (idx >= 0) lock (_memLock) _bitData[idx] = value;
        }

        public bool GetBitValue(IdecArea area, int operand)
        {
            int idx = BitIndex(area, operand);
            if (idx < 0) return false;
            lock (_memLock) return _bitData[idx];
        }

        private static int WordIndex(IdecArea area, int operand)
        {
            switch (area)
            {
                case IdecArea.DataRegister: return operand;
                case IdecArea.Timer: return 5000 + operand;
                case IdecArea.Counter: return 10000 + operand;
                default: return -1;
            }
        }

        private static int BitIndex(IdecArea area, int operand)
        {
            switch (area)
            {
                case IdecArea.InputBit: return operand;
                case IdecArea.OutputBit: return 5000 + operand;
                case IdecArea.InternalRelay: return 10000 + operand;
                default: return -1;
            }
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buf = new byte[256];

                while (_running && client.Connected)
                {
                    try
                    {
                        // 读到 ENQ 起始的请求，直到 CR
                        int first = stream.ReadByte();
                        if (first < 0) break;
                        if (first != IdecFrameControl.ENQ) continue;

                        var reqBytes = new List<byte> { (byte)first };
                        while (true)
                        {
                            int b = stream.ReadByte();
                            if (b < 0) return;
                            reqBytes.Add((byte)b);
                            if (b == IdecFrameControl.CR) break;
                            if (reqBytes.Count > 200) break; // 防失控
                        }

                        byte[] resp = ProcessRequest(reqBytes.ToArray());
                        stream.Write(resp, 0, resp.Length);
                    }
                    catch { break; }
                }
            }
        }

        private byte[] ProcessRequest(byte[] req)
        {
            // 请求: [ENQ][站号1][命令2][类型码1][operand6][count2][BCC2][CR]
            // 用 ASCII 视图解析（ENQ 和 CR 是控制字符，中间是可打印 ASCII）
            string ascii = Encoding.ASCII.GetString(req);
            // 去掉首部 ENQ 和尾部 CR
            string body = ascii.Substring(1).TrimEnd((char)IdecFrameControl.CR);
            if (body.Length < 12)
                return BuildNakResponse('1');

            // body: [站号1][命令2][类型码1][operand6][count2][BCC2]
            char stationChar = body[0];
            string command = body.Substring(1, 2);
            char typeCode = body[2];
            int operand;
            if (!int.TryParse(body.Substring(3, 6), out operand))
                return BuildNakResponse('2');
            ushort count;
            if (!ushort.TryParse(body.Substring(9, 2), out count))
                return BuildNakResponse('3');

            IdecArea area;
            try { area = IdecDataTypeCode.From(typeCode); }
            catch { return BuildNakResponse('4'); }

            if (command == IdecCommandType.ReadContinuous)
            {
                return BuildReadResponse(stationChar, area, operand, count);
            }
            else if (command == IdecCommandType.WriteContinuous)
            {
                // body: [站号1][命令2][类型码1][operand6][count2][dataHex N][BCC2]
                int dataLen = body.Length - 11 - 2; // 减去头部11 + BCC2
                if (dataLen < 0) return BuildNakResponse('5');
                string dataPart = body.Substring(11, dataLen);
                return BuildWriteResponse(stationChar, area, operand, count, dataPart);
            }

            return BuildNakResponse('9');
        }

        private byte[] BuildReadResponse(char stationChar, IdecArea area, int operand, ushort count)
        {
            string data;
            if (area == IdecArea.DataRegister || area == IdecArea.Timer || area == IdecArea.Counter)
            {
                var sb = new StringBuilder(count * 4);
                for (int i = 0; i < count; i++)
                {
                    ushort v = GetWordValue(area, operand + i);
                    sb.Append(v.ToString("X4"));
                }
                data = sb.ToString();
            }
            else
            {
                // 位设备：每 1 char = 1 bit
                var sb = new StringBuilder(count);
                for (int i = 0; i < count; i++)
                    sb.Append(GetBitValue(area, operand + i) ? '1' : '0');
                data = sb.ToString();
            }

            // [STX][站号][数据][ETX][BCC2][CR]
            string bccSource = stationChar.ToString() + data + (char)IdecFrameControl.ETX;
            byte bcc = IdecFrame.ComputeBcc(bccSource);
            string bccHex = bcc.ToString("X2");

            var frame = new StringBuilder();
            frame.Append((char)IdecFrameControl.STX);
            frame.Append(bccSource);
            frame.Append(bccHex);
            frame.Append((char)IdecFrameControl.CR);
            return Encoding.ASCII.GetBytes(frame.ToString());
        }

        private byte[] BuildWriteResponse(char stationChar, IdecArea area, int operand, ushort count, string dataPart)
        {
            if (area == IdecArea.DataRegister || area == IdecArea.Timer || area == IdecArea.Counter)
            {
                // 每 4 hex chars = 1 word
                for (int i = 0; i < count; i++)
                {
                    int start = i * 4;
                    if (start + 4 > dataPart.Length) break;
                    string wordHex = dataPart.Substring(start, 4);
                    if (ushort.TryParse(wordHex, System.Globalization.NumberStyles.HexNumber, null, out ushort v))
                        SetWordValue(area, operand + i, v);
                }
            }
            else
            {
                for (int i = 0; i < count && i < dataPart.Length; i++)
                    SetBitValue(area, operand + i, dataPart[i] == '1');
            }

            // 成功写响应: [ACK][站号][BCC2][CR]
            string bccSource = stationChar.ToString();
            byte bcc = IdecFrame.ComputeBcc(bccSource);
            string bccHex = bcc.ToString("X2");
            var frame = new StringBuilder();
            frame.Append((char)IdecFrameControl.ACK);
            frame.Append(bccSource);
            frame.Append(bccHex);
            frame.Append((char)IdecFrameControl.CR);
            return Encoding.ASCII.GetBytes(frame.ToString());
        }

        private byte[] BuildNakResponse(char errorCode)
        {
            // [NAK][站号][错误码1][BCC2][CR]
            char stationChar = '0';
            string bccSource = stationChar.ToString() + errorCode.ToString();
            byte bcc = IdecFrame.ComputeBcc(bccSource);
            string bccHex = bcc.ToString("X2");
            var frame = new StringBuilder();
            frame.Append((char)IdecFrameControl.NAK);
            frame.Append(bccSource);
            frame.Append(bccHex);
            frame.Append((char)IdecFrameControl.CR);
            return Encoding.ASCII.GetBytes(frame.ToString());
        }

        public void Dispose() { Stop(); GC.SuppressFinalize(this); }
    }
}
