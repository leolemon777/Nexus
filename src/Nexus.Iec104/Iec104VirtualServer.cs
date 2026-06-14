using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Iec104
{
    /// <summary>
    /// IEC 60870-5-104 虚拟服务器 — 模拟 IEC104 TCP 规约通讯。
    /// <para>用于集成测试，无需真实 IED/RTU 硬件。</para>
    /// <para>APDU 帧: StartByte(0x68) + APDULen(1) + APCI(4) + [ASDU]</para>
    /// <para>APCI 的字节 0-3 区分 U/S/I 三种格式（低2位）。</para>
    /// </summary>
    public class Iec104VirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        /// <summary>监听端口。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        public Iec104VirtualServer(int port = 0)
        {
            Port = port;
        }

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
                    var buffer = new byte[4096];
                    // IEC104 序列号：VS = 发送序号（本端发出），VR = 接收序号（对端已确认到）。
                    // 真实 IEC104 中 VS/VR 是独立的递增计数器，不能简单 echo 交换。
                    int vs = 0; // 本端下一帧的发送序号
                    int vr = 0; // 期望从对端收到的下一序号

                    while (_running && client.Connected)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        if (read < 2 || buffer[0] != 0x68) continue;

                        int apduLen = buffer[1];
                        if (read < 2 + apduLen || apduLen < 2) continue;

                        byte frameType = (byte)(buffer[2] & 0x03);

                        if (frameType == 0x03) // U-Format
                        {
                            HandleUFormat(stream, buffer[2]);
                        }
                        else if (frameType == 0x01) // I-Format
                        {
                            // I 帧头: [2][3] = NS(15bit)|0(1)  [4][5] = NR(15bit)|0(1)
                            int clientNS = (buffer[2] >> 1) | (buffer[3] << 7);
                            // 更新 VR：客户端发了 NS，本端确认到 NS+1
                            vr = clientNS + 1;

                            // 返回 I 响应：带独立 VS 和确认的 NR(=VR)。
                            // ASDU 用客户端请求的类型标识、COT=ActivationCon，让客户端 _pendingRequests 能匹配。
                            byte[] response = BuildIResponse(vs, vr, buffer, apduLen);
                            stream.Write(response, 0, response.Length);
                            vs++; // 本端发送了一帧，VS 递增
                        }
                        else if (frameType == 0x02) // S-Format
                        {
                            // S 帧：客户端确认收到。可回 S 帧确认。
                        }
                    }
                }
            }
            catch { }
        }

        private void HandleUFormat(NetworkStream stream, byte uByte)
        {
            // U 帧 APCI 只有 4 字节，bit 模式：STARTDT act=0x07, con=0x0B, STOPDT act=0x13, con=0x17,
            // TESTFR act=0x43, con=0x83。
            byte[] confirm = new byte[] { 0x68, 0x04, 0x00, 0x00, 0x00, 0x00 };
            if ((uByte & 0x04) != 0) // STARTDT act
                confirm[2] = 0x0B;
            else if ((uByte & 0x10) != 0) // STOPDT act
                confirm[2] = 0x17;
            else if ((uByte & 0x40) != 0) // TESTFR act
                confirm[2] = 0x83;
            else
                return;

            try { stream.Write(confirm, 0, confirm.Length); } catch { }
        }

        /// <summary>构建 I-Format 响应帧。</summary>
        /// <param name="vs">本端发送序号</param>
        /// <param name="vr">本端接收序号（确认到对端的 NS+1）</param>
        /// <param name="request">客户端请求帧（含 APCI+ASDU）</param>
        /// <param name="apduLen">请求 APDU 长度</param>
        private byte[] BuildIResponse(int vs, int vr, byte[] request, int apduLen)
        {
            // APCI(4) + ASDU。ASDU 至少 6 字节：类型(1)+可变结构限定(1)+COT(2)+OA(1)+CA(1)
            // 请求 ASDU 从 [6] 开始（frame[2..5] 是 APCI）。
            // 响应 ASDU：保留类型标识，COT 改为 ActivationCon(0x07)。
            int asduLen = apduLen - 4;
            byte[] response = new byte[2 + 4 + asduLen];

            // 起始字节 + 长度
            response[0] = 0x68;
            response[1] = (byte)(4 + asduLen);

            // APCI: NS | 0, NR | 0
            response[2] = (byte)((vs << 1) & 0xFE);   // NS 低7位，bit0=0(I帧)
            response[3] = (byte)((vs >> 7) & 0xFF);    // NS 高8位
            response[4] = (byte)((vr << 1) & 0xFE);   // NR 低7位
            response[5] = (byte)((vr >> 7) & 0xFF);    // NR 高8位

            // ASDU：复制请求 ASDU 但修改 COT
            if (asduLen >= 6 && request.Length >= 2 + 4 + asduLen)
            {
                Buffer.BlockCopy(request, 6, response, 6, asduLen);
                // ASDU[2..3] = COT（小端2字节）。改为 ActivationCon=0x07。
                // 注：对于总召唤响应实际应用 Spontaneous，但 ActivationCon 让客户端 _pendingRequests 匹配。
                response[8] = 0x07; // COT low = ActivationCon
                response[9] = 0x00; // COT high
            }
            else
            {
                // 不完整的 ASDU，填充零（保底）
                for (int i = 6; i < response.Length; i++) response[i] = 0;
            }

            return response;
        }
    }
}
