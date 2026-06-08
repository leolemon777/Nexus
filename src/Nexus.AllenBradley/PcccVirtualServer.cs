using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// PCCC 虚拟 PLC 服务器 — 用于测试 Allen-Bradley SLC/PLC-5 PCCC 协议。
    /// <para>实现 ENIP 封装层 + CIP Execute PCCC + PCCC 命令处理。</para>
    /// <para>支持: Protected Typed Logical Read (0xA2), Write (0xAA), Mask Write (0xAB)</para>
    /// <para>数据文件: N(Integer), F(Float), B(Bit), T(Timer), C(Counter), R(Control), S(Status)</para>
    /// <para>字节序: LittleEndian (PCCC/SLC 标准)</para>
    /// </summary>
    public class PcccVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        private uint _nextSessionId = 1;
        private readonly object _sessionLock = new object();

        // ── 数据文件存储（小端序，每个文件 256 个字 = 512 字节）──
        private readonly byte[] _n7 = new byte[512]; // N7: Integer file 7
        private readonly byte[] _f8 = new byte[512]; // F8: Float file 8
        private readonly byte[] _b3 = new byte[512]; // B3: Bit file 3
        private readonly byte[] _t4 = new byte[512]; // T4: Timer file 4
        private readonly byte[] _c5 = new byte[512]; // C5: Counter file 5
        private readonly byte[] _r6 = new byte[512]; // R6: Control file 6
        private readonly byte[] _s2 = new byte[512]; // S2: Status file 2

        private readonly object _dataLock = new object();

        public int Port { get; }
        public bool IsRunning => _running;

        // ENIP 命令常量
        private const ushort EnipRegisterSession = 0x0065;
        private const ushort EnipUnregisterSession = 0x0066;
        private const ushort EnipSendRRData = 0x006F;

        public PcccVirtualServer(int port)
        {
            Port = port;
        }

        // ═══════════════════════════════════════════
        //  数据设置 API（供测试使用）
        // ═══════════════════════════════════════════

        /// <summary>设置 N7 区域字值（小端序）。</summary>
        public void SetN7Word(int element, short value)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                if (offset + 1 < _n7.Length)
                {
                    _n7[offset] = (byte)(value & 0xFF);
                    _n7[offset + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置 N7 区域字节。</summary>
        public void SetN7Bytes(int element, byte[] data)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                Array.Copy(data, 0, _n7, offset, Math.Min(data.Length, _n7.Length - offset));
            }
        }

        /// <summary>设置 F8 区域 float 值（小端序）。</summary>
        public void SetF8Float(int element, float value)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                if (offset + 3 < _f8.Length)
                {
                    byte[] bytes = BitConverter.GetBytes(value);
                    // 小端序 — BitConverter 在 LE 平台上已经是 LE
                    Array.Copy(bytes, 0, _f8, offset, 4);
                }
            }
        }

        /// <summary>设置 B3 区域字值（小端序）。</summary>
        public void SetB3Word(int element, short value)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                if (offset + 1 < _b3.Length)
                {
                    _b3[offset] = (byte)(value & 0xFF);
                    _b3[offset + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置 T4 区域字值（小端序）。</summary>
        public void SetT4Word(int element, short value)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                if (offset + 1 < _t4.Length)
                {
                    _t4[offset] = (byte)(value & 0xFF);
                    _t4[offset + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        /// <summary>设置 C5 区域字值（小端序）。</summary>
        public void SetC5Word(int element, short value)
        {
            lock (_dataLock)
            {
                int offset = element * 2;
                if (offset + 1 < _c5.Length)
                {
                    _c5[offset] = (byte)(value & 0xFF);
                    _c5[offset + 1] = (byte)((value >> 8) & 0xFF);
                }
            }
        }

        // ═══════════════════════════════════════════
        //  服务器生命周期
        // ═══════════════════════════════════════════

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _acceptThread?.Join(2000);
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
            try
            {
                using (client)
                using (var ns = client.GetStream())
                {
                    ns.ReadTimeout = 5000;
                    ns.WriteTimeout = 5000;

                    while (_running && client.Connected)
                    {
                        // ENIP Header: 24 bytes
                        byte[]? header = ReadExact(ns, 24);
                        if (header == null) break;

                        ushort command = (ushort)(header[0] | (header[1] << 8));
                        ushort dataLen = (ushort)(header[2] | (header[3] << 8));

                        byte[]? data = dataLen > 0 ? ReadExact(ns, dataLen) : new byte[0];
                        if (data == null) break;

                        byte[]? response = ProcessEnipCommand(command, header, data);
                        if (response != null)
                            ns.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        // ═══════════════════════════════════════════
        //  ENIP 命令处理
        // ═══════════════════════════════════════════

        private byte[]? ProcessEnipCommand(ushort command, byte[] header, byte[] data)
        {
            switch (command)
            {
                case EnipRegisterSession:
                    return BuildRegisterSessionResponse(header, data);

                case EnipUnregisterSession:
                    return null; // 关闭连接

                case EnipSendRRData:
                    return ProcessSendRRData(header, data);

                default:
                    return BuildEnipErrorResponse(command, header, 0x0001);
            }
        }

        private byte[] BuildRegisterSessionResponse(byte[] header, byte[] data)
        {
            uint sessionId;
            lock (_sessionLock)
            {
                sessionId = _nextSessionId++;
            }

            byte[] resp = new byte[28];
            // ENIP Header (24 bytes)
            resp[0] = 0x65; resp[1] = 0x00; // RegisterSession response
            resp[2] = 0x04; resp[3] = 0x00; // Length = 4
            // Session handle
            resp[4] = (byte)(sessionId & 0xFF);
            resp[5] = (byte)((sessionId >> 8) & 0xFF);
            resp[6] = (byte)((sessionId >> 16) & 0xFF);
            resp[7] = (byte)((sessionId >> 24) & 0xFF);
            // Status = 0, SenderContext = 0, Options = 0
            // RegisterSession data: ProtocolVersion(2)=1 + Options(2)=0
            resp[24] = 0x01; resp[25] = 0x00;
            resp[26] = 0x00; resp[27] = 0x00;
            return resp;
        }

        private byte[] ProcessSendRRData(byte[] header, byte[] data)
        {
            // 解析 SendRRData: InterfaceHandle(4) + Timeout(2) + ItemCount(2) + Items...
            if (data.Length < 10) return BuildEnipErrorResponse(EnipSendRRData, header, 0x0001);

            // Skip InterfaceHandle(4) + Timeout(2) + ItemCount(2)
            int offset = 8;

            // Item 1: Null Address (skip)
            if (offset + 4 > data.Length) return BuildEnipErrorResponse(EnipSendRRData, header, 0x0001);
            offset += 4;

            // Item 2: Unconnected Data
            if (offset + 4 > data.Length) return BuildEnipErrorResponse(EnipSendRRData, header, 0x0001);
            ushort item2Type = (ushort)(data[offset] | (data[offset + 1] << 8));
            ushort item2Len = (ushort)(data[offset + 2] | (data[offset + 3] << 8));
            offset += 4;

            if (offset + item2Len > data.Length) return BuildEnipErrorResponse(EnipSendRRData, header, 0x0001);
            byte[] cipRequest = new byte[item2Len];
            Buffer.BlockCopy(data, offset, cipRequest, 0, item2Len);

            // 解析 CIP Execute PCCC 请求
            // Service(1) + PathSize(1) + Path(4) + Params(6) + PCCC data
            // Service = 0x4B (Execute PCCC)
            if (cipRequest.Length < 13 || cipRequest[0] != 0x4B)
                return BuildCipErrorResponse(header, 0x05); // 不支持的服务

            // 提取 PCCC 命令数据 (跳过 12 字节 CIP 头)
            byte[] pcccCmd = new byte[cipRequest.Length - 12];
            Buffer.BlockCopy(cipRequest, 12, pcccCmd, 0, pcccCmd.Length);

            // 处理 PCCC 命令
            byte[]? pcccResponse = ProcessPcccCommand(pcccCmd);

            // 构建 CIP 响应
            byte[] cipResponse = BuildCipPcccResponse(pcccResponse);

            // 封装到 ENIP SendRRData 响应
            return BuildSendRRDataResponse(header, cipResponse);
        }

        // ═══════════════════════════════════════════
        //  PCCC 命令处理
        // ═══════════════════════════════════════════

        private byte[]? ProcessPcccCommand(byte[] cmd)
        {
            if (cmd.Length < 5) return null;
            // cmd[0] = 0x0F (PCCC Command)
            // cmd[1] = Status
            // cmd[2-3] = TNS
            // cmd[4] = Function code

            byte function = cmd[4];

            switch (function)
            {
                case 0xA2: // Protected Typed Logical Read
                    return ProcessPcccRead(cmd);

                case 0xAA: // Protected Typed Logical Write
                    return ProcessPcccWrite(cmd);

                case 0xAB: // Protected Typed Logical Mask Write
                    return ProcessPcccMaskWrite(cmd);

                default:
                    return BuildPcccErrorResponse(cmd, 0x10); // Illegal command
            }
        }

        private byte[]? ProcessPcccRead(byte[] cmd)
        {
            // PCCC Read: 0x0F, 0x00, tns_lo, tns_hi, 0xA2, byteCount, fileNo, dataCode, element, subElement
            if (cmd.Length < 7) return BuildPcccErrorResponse(cmd, 0x01);

            int offset = 5;
            int byteCount = cmd[offset++];
            int fileNo = PcccClient.ReadPcccLength(cmd, offset, out int bytesRead);
            offset += bytesRead;
            if (offset >= cmd.Length) return BuildPcccErrorResponse(cmd, 0x01);

            byte dataCode = cmd[offset++];
            int element = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);
            offset += bytesRead;
            int subElement = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);

            // 获取存储区
            var storage = GetStorage(dataCode, fileNo);
            if (storage == null) return BuildPcccErrorResponse(cmd, 0x06);

            lock (_dataLock)
            {
                int srcOffset = element * 2 + subElement * 2;
                byte[] result = new byte[byteCount];
                int copyLen = Math.Min(byteCount, Math.Max(0, storage.Length - srcOffset));
                if (copyLen > 0)
                    Array.Copy(storage, srcOffset, result, 0, copyLen);
                return BuildPcccSuccessResponse(cmd, result);
            }
        }

        private byte[]? ProcessPcccWrite(byte[] cmd)
        {
            // PCCC Write: 0x0F, 0x00, tns_lo, tns_hi, 0xAA, dataLen, fileNo, dataCode, element, subElement, data...
            if (cmd.Length < 7) return BuildPcccErrorResponse(cmd, 0x01);

            int offset = 5;
            int dataLen = cmd[offset++];
            int fileNo = PcccClient.ReadPcccLength(cmd, offset, out int bytesRead);
            offset += bytesRead;
            if (offset >= cmd.Length) return BuildPcccErrorResponse(cmd, 0x01);

            byte dataCode = cmd[offset++];
            int element = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);
            offset += bytesRead;
            int subElement = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);
            offset += bytesRead;

            // 提取写入数据
            int remaining = cmd.Length - offset;
            int writeLen = Math.Min(dataLen, remaining);

            var storage = GetStorage(dataCode, fileNo);
            if (storage == null) return BuildPcccErrorResponse(cmd, 0x06);

            lock (_dataLock)
            {
                int dstOffset = element * 2 + subElement * 2;
                if (writeLen > 0 && dstOffset < storage.Length)
                    Array.Copy(cmd, offset, storage, dstOffset, Math.Min(writeLen, storage.Length - dstOffset));
            }

            return BuildPcccSuccessResponse(cmd, Array.Empty<byte>());
        }

        private byte[]? ProcessPcccMaskWrite(byte[] cmd)
        {
            // PCCC Mask Write: 0x0F, 0x00, tns_lo, tns_hi, 0xAB, 0x02, fileNo, dataCode, element, subElement, andMask(2), orMask(2)
            if (cmd.Length < 7) return BuildPcccErrorResponse(cmd, 0x01);

            int offset = 5;
            // byteCount = cmd[offset++] (should be 0x02)
            offset++;
            int fileNo = PcccClient.ReadPcccLength(cmd, offset, out int bytesRead);
            offset += bytesRead;
            if (offset >= cmd.Length) return BuildPcccErrorResponse(cmd, 0x01);

            byte dataCode = cmd[offset++];
            int element = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);
            offset += bytesRead;
            int subElement = PcccClient.ReadPcccLength(cmd, offset, out bytesRead);
            offset += bytesRead;

            if (offset + 4 > cmd.Length) return BuildPcccErrorResponse(cmd, 0x01);

            // AND mask (LE) and OR mask (LE)
            ushort andMask = (ushort)(cmd[offset] | (cmd[offset + 1] << 8));
            ushort orMask = (ushort)(cmd[offset + 2] | (cmd[offset + 3] << 8));

            var storage = GetStorage(dataCode, fileNo);
            if (storage == null) return BuildPcccErrorResponse(cmd, 0x06);

            lock (_dataLock)
            {
                int dstOffset = element * 2 + subElement * 2;
                if (dstOffset + 1 < storage.Length)
                {
                    ushort current = (ushort)(storage[dstOffset] | (storage[dstOffset + 1] << 8));
                    ushort newVal = (ushort)((current & andMask) | orMask);
                    storage[dstOffset] = (byte)(newVal & 0xFF);
                    storage[dstOffset + 1] = (byte)((newVal >> 8) & 0xFF);
                }
            }

            return BuildPcccSuccessResponse(cmd, Array.Empty<byte>());
        }

        private byte[]? GetStorage(byte dataCode, int fileNo)
        {
            switch (dataCode)
            {
                case 0x89: // Integer (N)
                    return fileNo == 7 ? _n7 : null;
                case 0x8A: // Float (F)
                    return fileNo == 8 ? _f8 : null;
                case 0x85: // Bit (B)
                    return fileNo == 3 ? _b3 : null;
                case 0x86: // Timer (T)
                    return fileNo == 4 ? _t4 : null;
                case 0x87: // Counter (C)
                    return fileNo == 5 ? _c5 : null;
                case 0x88: // Control (R)
                    return fileNo == 6 ? _r6 : null;
                case 0x84: // Status (S)
                    return fileNo == 2 ? _s2 : null;
                default:
                    return null;
            }
        }

        // ═══════════════════════════════════════════
        //  PCCC 响应构建
        // ═══════════════════════════════════════════

        private static byte[] BuildPcccSuccessResponse(byte[] cmd, byte[] data)
        {
            // PCCC 响应: Command(1) + Status(1) + TNS(2) + ExtStatus(1) + Data
            var ms = new MemoryStream();
            ms.WriteByte(cmd[0]); // 回显命令
            ms.WriteByte(0x00);   // Status: 成功
            ms.WriteByte(cmd[2]); // TNS low (回显)
            ms.WriteByte(cmd[3]); // TNS high (回显)
            ms.WriteByte(0x00);   // ExtStatus: 成功
            if (data.Length > 0)
                ms.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        private static byte[] BuildPcccErrorResponse(byte[] cmd, byte errorCode)
        {
            // PCCC 错误响应: Command(1) + Status(1) + TNS(2) + ExtStatus(1)
            return new byte[]
            {
                cmd.Length > 0 ? cmd[0] : (byte)0x0F,
                errorCode,
                cmd.Length > 2 ? cmd[2] : (byte)0,
                cmd.Length > 3 ? cmd[3] : (byte)0,
                0x00 // ExtStatus
            };
        }

        // ═══════════════════════════════════════════
        //  CIP / ENIP 响应构建
        // ═══════════════════════════════════════════

        private static byte[] BuildCipPcccResponse(byte[]? pcccData)
        {
            // CIP 响应: ReplyService(1) + Reserved(1) + Status(1) + ExtStatusSize(1) + Data
            // ReplyService = 0x4B | 0x80 = 0xCB (响应)
            byte[] data = pcccData ?? new byte[0];
            byte[] resp = new byte[4 + data.Length];
            resp[0] = 0xCB; // Execute PCCC Reply (0x4B | 0x80)
            resp[1] = 0x00; // Reserved
            resp[2] = 0x00; // Status: Success
            resp[3] = 0x00; // ExtStatus size: 0
            if (data.Length > 0)
                Buffer.BlockCopy(data, 0, resp, 4, data.Length);
            return resp;
        }

        private byte[] BuildCipErrorResponse(byte[] header, byte cipStatus)
        {
            // CIP 错误响应
            byte[] cipResp = new byte[] { 0xCB, 0x00, cipStatus, 0x00 };
            return BuildSendRRDataResponse(header, cipResp);
        }

        private byte[] BuildSendRRDataResponse(byte[] header, byte[] cipResponse)
        {
            // SendRRData 响应: InterfaceHandle(4) + Timeout(2) + ItemCount(2) +
            //   Item1: Null(4) + Item2: Type(2) + Length(2) + Data
            int rrLen = 4 + 2 + 2 + 4 + 2 + 2 + cipResponse.Length;
            byte[] rrData = new byte[rrLen];
            int i = 0;
            // Interface Handle = 0
            rrData[i++] = 0; rrData[i++] = 0; rrData[i++] = 0; rrData[i++] = 0;
            // Timeout = 0
            rrData[i++] = 0; rrData[i++] = 0;
            // Item Count = 2
            rrData[i++] = 2; rrData[i++] = 0;
            // Item 1: Null Address
            rrData[i++] = 0x00; rrData[i++] = 0x00;
            rrData[i++] = 0x00; rrData[i++] = 0x00;
            // Item 2: Connected Data (0x00B2)
            rrData[i++] = 0xB2; rrData[i++] = 0x00;
            rrData[i++] = (byte)(cipResponse.Length & 0xFF);
            rrData[i++] = (byte)((cipResponse.Length >> 8) & 0xFF);
            Buffer.BlockCopy(cipResponse, 0, rrData, i, cipResponse.Length);

            // ENIP Header
            byte[] resp = new byte[24 + rrData.Length];
            resp[0] = 0x6F; resp[1] = 0x00; // SendRRData response
            resp[2] = (byte)(rrData.Length & 0xFF);
            resp[3] = (byte)((rrData.Length >> 8) & 0xFF);
            // 复制 session handle
            resp[4] = header[4]; resp[5] = header[5];
            resp[6] = header[6]; resp[7] = header[7];
            Buffer.BlockCopy(rrData, 0, resp, 24, rrData.Length);
            return resp;
        }

        private byte[] BuildEnipErrorResponse(ushort command, byte[] header, uint status)
        {
            byte[] resp = new byte[24];
            resp[0] = (byte)(command & 0xFF);
            resp[1] = (byte)((command >> 8) & 0xFF);
            // Length = 0
            // Session handle from request
            resp[4] = header[4]; resp[5] = header[5];
            resp[6] = header[6]; resp[7] = header[7];
            // Status
            resp[8] = (byte)(status & 0xFF);
            resp[9] = (byte)((status >> 8) & 0xFF);
            resp[10] = (byte)((status >> 16) & 0xFF);
            resp[11] = (byte)((status >> 24) & 0xFF);
            return resp;
        }

        private static byte[]? ReadExact(NetworkStream ns, int count)
        {
            var buf = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = ns.Read(buf, read, count - read);
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
