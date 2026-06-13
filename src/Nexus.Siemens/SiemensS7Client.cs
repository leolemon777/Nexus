using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 S7 协议客户端 — 支持 S7-200/200Smart/300/400/1200/1500。
    /// 基于 TPKT + COTP + S7 Communication 三层协议栈。
    /// </summary>
    /// <remarks>
    /// <para>支持的功能：</para>
    /// <list type="bullet">
    ///   <item>全系列型号连接（自动处理 TSAP/Rack/Slot）</item>
    ///   <item>基础数据读写（Bool/Int16/Int32/Int64/Float/Double/String/Bytes）</item>
    ///   <item>批量多地址读写（自动按 PDU 分包，最多19地址/包）</item>
    ///   <item>S7 String/WString 读写（自动处理长度前缀）</item>
    ///   <item>PLC 控制命令（读订货号/热启动/冷启动/停止）</item>
    ///   <item>大块数据自动按 PDU 分割读写</item>
    /// </list>
    /// </remarks>
    public class SiemensS7Client : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public SiemensPLCS PLCType { get; }
        public byte Rack { get; set; } = 0;
        public byte Slot { get; set; } = 0;
        public ushort MaxPduSize { get; private set; } = 240;

        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;

        /// <summary>
        /// 连接类型。PG=0x01, OP=0x02, S7Basic=0x03~0x10。
        /// 仅对 S7-300/400/1200/1500 有效，S7-200/200Smart 使用固定 TSAP。
        /// </summary>
        public byte ConnectionType { get; set; } = 0x03;

        /// <summary>
        /// 自定义本地 TSAP。设置为非零值时覆盖自动计算的 TSAP。
        /// </summary>
        public int? LocalTSAP { get; set; }

        /// <summary>
        /// 自定义目标 TSAP。设置为非零值时覆盖自动计算的 TSAP。
        /// </summary>
        public int? DestTSAP { get; set; }

        public SiemensS7Client(SiemensPLCS plcType, string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout)
        {
            PLCType = plcType;
            if (plcType == SiemensPLCS.S7_1200 || plcType == SiemensPLCS.S7_1500)
                Slot = 1;
            else if (plcType == SiemensPLCS.S7_300 || plcType == SiemensPLCS.S7_400)
                Slot = 2;

            SetHeartbeatCallback(SendS7HeartbeatAsync);
        }

        // ── S7 协议层 ──────────────────────────────

        protected override int ResponseHeaderLength => _isFirstPacket ? 4 : 7;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (_isFirstPacket)
            {
                _isFirstPacket = false;
                return ((header[2] << 8) | header[3]) - 4;
            }
            return 0;
        }

        private bool _isFirstPacket = true;

        protected new OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                lock (_lock)
                {
                    System.Net.Sockets.NetworkStream? ns = _stream;
                    if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                    string requestHex = DataConverter.ToHexString(request);
                    Log.Debug($"TX → {requestHex}");
                    RaiseMessageSent(requestHex);

                    ns.Write(request, 0, request.Length);

                    byte[]? tpktHeader = ReadExactNs(ns, 4);
                    if (tpktHeader == null) return OperateResult<byte[]>.Failed("读取TPKT头失败");

                    int totalLen = (tpktHeader[2] << 8) | tpktHeader[3];
                    int payloadLen = totalLen - 4;
                    if (payloadLen < 0 || payloadLen > 65535) return OperateResult<byte[]>.Failed("TPKT长度异常");

                    byte[] payload = payloadLen > 0 ? ReadExactNs(ns, payloadLen) ?? new byte[0] : new byte[0];

                    byte[] full = new byte[totalLen];
                    Buffer.BlockCopy(tpktHeader, 0, full, 0, 4);
                    if (payload.Length > 0) Buffer.BlockCopy(payload, 0, full, 4, payload.Length);

                    string responseHex = DataConverter.ToHexString(full);
                    Log.Debug($"RX ← {responseHex}");
                    RaiseMessageReceived(responseHex);

                    if (!_persistentMode) DisconnectCore();

                    return OperateResult<byte[]>.Success(full);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        private byte[]? ReadExactNs(System.Net.Sockets.NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        private Task<OperateResult> SendS7HeartbeatAsync()
        {
            return Task.Run(() =>
            {
                var result = ReadS7Raw("M0", 1, S7DataType.Byte);
                return result.IsSuccess
                    ? OperateResult.Success()
                    : OperateResult.Failed(result.Message, result.ErrorCode);
            });
        }

        // ── TPKT + COTP + S7 报文构建 ─────────────

        private byte[] BuildTPKT(byte[] payload)
        {
            int total = 4 + payload.Length;
            byte[] frame = new byte[total];
            frame[0] = 0x03;
            frame[1] = 0x00;
            frame[2] = (byte)(total >> 8);
            frame[3] = (byte)total;
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        private byte[] BuildCOTPConnectionRequest()
        {
            // S7-200Smart 使用固定 TSAP: 0x1000/0x0300
            // S7-200 使用固定 TSAP: 0x4D57/0x4D57
            // 其他型号使用 Rack*32+Slot 计算
            bool is200 = PLCType == SiemensPLCS.S7_200;
            bool is200Smart = PLCType == SiemensPLCS.S7_200Smart;

            byte[] cr = new byte[] {
                0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
                0xC0, 0x01, 0x0A,                         // param1
                0x01, 0x00,                                // local TSAP hi/lo (placeholder)
                0xC1, 0x01, 0x0A,
                0x01, 0x00,                                // local TSAP2 hi/lo (placeholder)
                0xC0, 0x01, 0x09,
                0x01, 0x00                                 // dest TSAP hi/lo (placeholder)
            };

            if (is200)
            {
                // S7-200: Local=0x4D57, Dest=0x4D57
                cr[10] = 0x01; cr[11] = 0x00;
                cr[15] = 0x4D; cr[16] = 0x57;
                cr[20] = 0x01; cr[21] = 0x00;
            }
            else if (is200Smart)
            {
                // S7-200Smart: Local=0x1000, Dest=0x0300
                cr[10] = 0x10; cr[11] = 0x00;
                cr[15] = 0x02; cr[16] = 0x03;
                cr[20] = 0x03; cr[21] = 0x00;
            }
            else
            {
                // S7-300/400/1200/1500
                int localTsap = LocalTSAP ?? (ConnectionType << 8 | 0x01);
                int destTsap = DestTSAP ?? (0x01 << 8 | (Rack * 0x20 + Slot));

                cr[10] = (byte)((localTsap >> 8) & 0xFF);
                cr[11] = (byte)(localTsap & 0xFF);
                cr[15] = (byte)(localTsap >> 8 & 0xFF);
                cr[16] = (byte)(localTsap & 0xFF);
                cr[20] = (byte)((destTsap >> 8) & 0xFF);
                cr[21] = (byte)(destTsap & 0xFF);
            }

            return BuildTPKT(cr);
        }

        private byte[] BuildCOTPDataRequest(byte[] s7Pdu)
        {
            byte[] cotpData = new byte[3 + s7Pdu.Length];
            cotpData[0] = 0x02;
            cotpData[1] = 0xF0;
            cotpData[2] = 0x80;
            Buffer.BlockCopy(s7Pdu, 0, cotpData, 3, s7Pdu.Length);
            return BuildTPKT(cotpData);
        }

        private byte[] BuildS7SetupCommunication()
        {
            return BuildCOTPDataRequest(new byte[] {
                0x32, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00,
                0xF0, 0x00, 0x00, 0x01, 0x00, 0x01,
                0x03, (byte)(MaxPduSize >> 8), (byte)MaxPduSize
            });
        }

        private static byte[] BuildS7AddressItem(
            byte function, byte wordLen, int lengthOrBitCount,
            S7Area area, int dbNumber, ushort byteAddress)
        {
            return new byte[]
            {
                function, 0x01,
                0x12, 0x0A, 0x10,
                wordLen,
                (byte)((lengthOrBitCount >> 8) & 0xFF),
                (byte)(lengthOrBitCount & 0xFF),
                (byte)((dbNumber >> 8) & 0xFF),
                (byte)(dbNumber & 0xFF),
                (byte)area,
                (byte)((byteAddress >> 16) & 0xFF),
                (byte)((byteAddress >> 8) & 0xFF),
                (byte)(byteAddress & 0xFF)
            };
        }

        private byte[] BuildS7Header(byte function, byte[]? param, byte[]? data)
        {
            int paramLen = param?.Length ?? 0;
            int dataLen = data?.Length ?? 0;
            byte[] header = new byte[10 + paramLen + dataLen];

            header[0] = 0x32;
            header[1] = 0x01;
            header[2] = 0x00; header[3] = 0x00;
            header[4] = 0x00; header[5] = 0x01;
            header[6] = (byte)(paramLen >> 8); header[7] = (byte)paramLen;
            header[8] = (byte)(dataLen >> 8); header[9] = (byte)dataLen;
            if (param != null) Buffer.BlockCopy(param, 0, header, 10, paramLen);
            if (data != null) Buffer.BlockCopy(data, 0, header, 10 + paramLen, dataLen);
            return header;
        }

        // ── 连接建立 ──────────────────────────────

        public override OperateResult Connect()
        {
            var conn = base.Connect();
            if (!conn.IsSuccess) return conn;

            bool wasPersistent = _persistentMode;
            _persistentMode = true;
            try
            {
                // 阶段1: COTP 连接请求
                var crReq = BuildCOTPConnectionRequest();
                var crResp = SendAndReceive(crReq);
                if (!crResp.IsSuccess) return crResp;

                // 阶段2: S7 通信设置（协商 PDU）
                var setupReq = BuildS7SetupCommunication();
                var setupResp = SendAndReceive(setupReq);
                if (!setupResp.IsSuccess) return setupResp;

                if (setupResp.Content.Length > 26)
                {
                    MaxPduSize = (ushort)((setupResp.Content[25] << 8) | setupResp.Content[26]);
                    if (MaxPduSize < 16) MaxPduSize = 240;
                }

                return OperateResult.Success();
            }
            finally
            {
                _persistentMode = wasPersistent;
            }
        }

        // ── 地址解析（委托 SiemensS7Address）──────

        private static SiemensS7Address ParseS7Address(string address)
            => SiemensS7Address.Parse(address);

        // ── S7 数据类型 ────────────────────────────

        private enum S7DataType { Bit, Byte, Word, Int, DInt, Real, String, Timer, Counter }

        // ── 字节序处理 ─────────────────────────────

        private byte[] ApplyByteOrderWrite(byte[] data, int typeSize)
        {
            if (ByteOrder == Endianness.BigEndian || typeSize <= 1) return data;
            byte[] swapped = (byte[])data.Clone();
            if (typeSize == 2)
            {
                if (ByteOrder == Endianness.LittleEndian) { byte t = swapped[0]; swapped[0] = swapped[1]; swapped[1] = t; }
            }
            else if (typeSize == 4)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        byte t0 = swapped[0]; swapped[0] = swapped[3]; swapped[3] = t0;
                        byte t1 = swapped[1]; swapped[1] = swapped[2]; swapped[2] = t1;
                        break;
                    case Endianness.MidBigEndian:
                        byte b0 = swapped[0]; swapped[0] = swapped[1]; swapped[1] = b0;
                        byte b2 = swapped[2]; swapped[2] = swapped[3]; swapped[3] = b2;
                        break;
                    case Endianness.MidLittleEndian:
                        byte c0 = swapped[0]; byte c1 = swapped[1];
                        swapped[0] = swapped[2]; swapped[1] = swapped[3];
                        swapped[2] = c0; swapped[3] = c1;
                        break;
                }
            }
            else if (typeSize == 8)
            {
                switch (ByteOrder)
                {
                    case Endianness.LittleEndian:
                        Array.Reverse(swapped);
                        break;
                    case Endianness.MidBigEndian:
                        for (int i = 0; i < 8; i += 2) { byte t = swapped[i]; swapped[i] = swapped[i + 1]; swapped[i + 1] = t; }
                        break;
                    case Endianness.MidLittleEndian:
                        for (int i = 0; i < 4; i++) { byte t = swapped[i]; swapped[i] = swapped[i + 4]; swapped[i + 4] = t; }
                        break;
                }
            }
            return swapped;
        }

        private byte[] ApplyByteOrderRead(byte[] data, int typeSize)
        {
            if (ByteOrder == Endianness.BigEndian || typeSize <= 1) return data;
            return ApplyByteOrderWrite(data, typeSize);
        }

        // ── 读取实现 ──────────────────────────────

        private OperateResult<byte[]> ReadS7Raw(string address, int byteCount, S7DataType type)
        {
            var s7Addr = ParseS7Address(address);
            int addrBits = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;
            byte wordLenByte = (byte)(type == S7DataType.Bit ? 0x01 : 0x02);

            byte[] param = BuildS7AddressItem(0x04, wordLenByte, byteCount,
                s7Addr.Area, s7Addr.DBNumber, (ushort)addrBits);

            var req = BuildCOTPDataRequest(BuildS7Header(0x04, param, null));
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            byte[] raw = resp.Content;
            const int DataOffset = 25;
            if (raw.Length < DataOffset) return OperateResult<byte[]>.Failed("S7响应长度不足");

            byte returnCode = raw[21];
            if (returnCode != 0xFF)
                return OperateResult<byte[]>.Failed($"S7读取错误: 返回码=0x{returnCode:X2}", returnCode);

            int dataLen = (raw[23] << 8) | raw[24];
            if (dataLen <= 0 && raw.Length > DataOffset) dataLen = raw.Length - DataOffset;

            byte[] data = new byte[Math.Min(byteCount, raw.Length - DataOffset)];
            if (data.Length > 0)
                Buffer.BlockCopy(raw, DataOffset, data, 0, data.Length);

            return OperateResult<byte[]>.Success(data);
        }

        /// <summary>
        /// 读取大块字节数据 — 自动按 PDU 大小分割请求。
        /// </summary>
        public OperateResult<byte[]> ReadLarge(string address, int totalBytes)
        {
            if (totalBytes <= 0) return OperateResult<byte[]>.Success(Array.Empty<byte>());

            var s7Addr = ParseS7Address(address);
            int maxPdu = MaxPduSize > 0 ? MaxPduSize : 240;

            if (totalBytes <= maxPdu)
                return ReadS7Raw(address, totalBytes, S7DataType.Byte);

            // 按 PDU 分割
            var result = new List<byte>(totalBytes);
            int offset = 0;

            while (offset < totalBytes)
            {
                int chunkSize = Math.Min(totalBytes - offset, maxPdu);
                string chunkAddr = s7Addr.Area == S7Area.DB
                    ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + offset}"
                    : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + offset}";

                var chunk = ReadS7Raw(chunkAddr, chunkSize, S7DataType.Byte);
                if (!chunk.IsSuccess) return OperateResult<byte[]>.Failed(chunk.Message, chunk.ErrorCode);

                result.AddRange(chunk.Content);
                offset += chunkSize;
            }

            return OperateResult<byte[]>.Success(result.ToArray());
        }

        /// <summary>
        /// 异步读取大块字节数据 — 自动按 PDU 大小分割请求。
        /// </summary>
        public Task<OperateResult<byte[]>> ReadLargeAsync(string address, int totalBytes,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ReadLarge(address, totalBytes), cancellationToken);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadS7Raw(address, 1, S7DataType.Bit);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

        /// <summary>
        /// 批量读取 Bool 值 — 从起始地址读取多个连续位。
        /// 地址格式: "DB1.DBX0.0" 或 "M0.0"，count 为读取的位数。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());

            var s7Addr = ParseS7Address(address);
            int startBit = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;

            // 读取所需的字节数
            int totalBits = startBit + count;
            int bytesNeeded = (totalBits + 7) / 8;
            int readStart = startBit / 8;

            string readAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{readStart}"
                : $"{AreaPrefix(s7Addr.Area)}{readStart}";

            var r = ReadS7Raw(readAddr, bytesNeeded, S7DataType.Byte);
            if (!r.IsSuccess) return OperateResult<bool[]>.Failed(r.Message, r.ErrorCode);

            var result = new bool[count];
            for (int i = 0; i < count; i++)
            {
                int bitIndex = startBit + i;
                int byteIndex = bitIndex / 8 - readStart;
                int bitOffset = bitIndex % 8;
                if (byteIndex < r.Content.Length)
                    result[i] = (r.Content[byteIndex] & (1 << bitOffset)) != 0;
            }

            return OperateResult<bool[]>.Success(result);
        }

        /// <summary>
        /// 异步批量读取 Bool 值。
        /// </summary>
        public Task<OperateResult<bool[]>> ReadBoolsAsync(string address, ushort count,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ReadBools(address, count), cancellationToken);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadS7Raw(address, 2, S7DataType.Int);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadS7Raw(address, 2, S7DataType.Word);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadS7Raw(address, 4, S7DataType.DInt);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadS7Raw(address, 8, S7DataType.DInt);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadS7Raw(address, 4, S7DataType.Real);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(DataConverter.ToFloat(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadS7Raw(address, 8, S7DataType.Real);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadS7Raw(address, length, S7DataType.String);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
        }

        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var r = ReadS7Raw(address, length, S7DataType.String);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(StringEncoding.GetString(r.Content, 0, r.Content.Length).TrimEnd('\0', ' '));
        }

        /// <summary>
        /// 读取西门子 S7 String 类型 — 自动处理长度前缀。
        /// PLC 中格式: [最大长度(1B)][当前长度(1B)][ASCII数据...]。
        /// </summary>
        public OperateResult<string> ReadS7String(string address)
        {
            // 先读2字节获取长度信息
            var header = ReadS7Raw(address, 2, S7DataType.Byte);
            if (!header.IsSuccess) return OperateResult<string>.Failed(header.Message, header.ErrorCode);

            int actualLen = header.Content[1];
            if (actualLen == 0) return OperateResult<string>.Success(string.Empty);

            // 读实际字符串数据（地址偏移2字节）
            var s7Addr = ParseS7Address(address);
            string dataAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + 2}"
                : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + 2}";

            var data = ReadS7Raw(dataAddr, actualLen, S7DataType.Byte);
            if (!data.IsSuccess) return OperateResult<string>.Failed(data.Message, data.ErrorCode);

            return OperateResult<string>.Success(Encoding.ASCII.GetString(data.Content, 0, data.Content.Length));
        }

        /// <summary>
        /// 读取西门子 WString 类型 — Unicode 双字节字符串。
        /// PLC 中格式: [最大长度(2B)][当前长度(2B)][UTF-16LE数据...]。
        /// </summary>
        public OperateResult<string> ReadWString(string address)
        {
            // 先读4字节获取长度信息
            var header = ReadS7Raw(address, 4, S7DataType.Byte);
            if (!header.IsSuccess) return OperateResult<string>.Failed(header.Message, header.ErrorCode);

            int actualLen = (header.Content[2] << 8) | header.Content[3];
            if (actualLen == 0) return OperateResult<string>.Success(string.Empty);

            // 读实际 UTF-16LE 字符串数据（地址偏移4字节）
            var s7Addr = ParseS7Address(address);
            string dataAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + 4}"
                : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + 4}";

            var data = ReadS7Raw(dataAddr, actualLen * 2, S7DataType.Byte);
            if (!data.IsSuccess) return OperateResult<string>.Failed(data.Message, data.ErrorCode);

            return OperateResult<string>.Success(Encoding.BigEndianUnicode.GetString(data.Content, 0, data.Content.Length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadS7Raw(address, length, S7DataType.Byte);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return r;
        }

        // ── 写入实现 ──────────────────────────────

        private OperateResult WriteS7Raw(string address, byte[] data, S7DataType type)
        {
            var s7Addr = ParseS7Address(address);
            int addrBits = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;
            byte wordLen = (byte)(type == S7DataType.Bit ? 0x01 : 0x02);

            byte[] param = BuildS7AddressItem(0x05, wordLen, data.Length,
                s7Addr.Area, s7Addr.DBNumber, (ushort)addrBits);

            byte[] s7Data = new byte[4 + data.Length];
            s7Data[0] = 0x00;
            s7Data[1] = wordLen;
            s7Data[2] = (byte)(data.Length >> 8);
            s7Data[3] = (byte)(data.Length & 0xFF);
            Buffer.BlockCopy(data, 0, s7Data, 4, data.Length);

            var req = BuildCOTPDataRequest(BuildS7Header(0x05, param, s7Data));
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length > 21)
            {
                byte retCode = resp.Content[21];
                if (retCode != 0xFF)
                    return OperateResult.Failed($"S7写入错误: 返回码=0x{retCode:X2}", retCode);
            }

            return OperateResult.Success();
        }

        /// <summary>
        /// 写入大块字节数据 — 自动按 PDU 大小分割。
        /// </summary>
        public OperateResult WriteLarge(string address, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Success();

            int maxPdu = MaxPduSize > 0 ? MaxPduSize : 240;

            if (data.Length <= maxPdu)
                return WriteS7Raw(address, data, S7DataType.Byte);

            var s7Addr = ParseS7Address(address);
            int offset = 0;

            while (offset < data.Length)
            {
                int chunkSize = Math.Min(data.Length - offset, maxPdu);
                byte[] chunk = new byte[chunkSize];
                Buffer.BlockCopy(data, offset, chunk, 0, chunkSize);

                string chunkAddr = s7Addr.Area == S7Area.DB
                    ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + offset}"
                    : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + offset}";

                var r = WriteS7Raw(chunkAddr, chunk, S7DataType.Byte);
                if (!r.IsSuccess) return r;

                offset += chunkSize;
            }

            return OperateResult.Success();
        }

        /// <summary>
        /// 异步写入大块字节数据。
        /// </summary>
        public Task<OperateResult> WriteLargeAsync(string address, byte[] data,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => WriteLarge(address, data), cancellationToken);
        }

        public override OperateResult Write(string address, bool value)
        {
            return WriteS7Raw(address, new byte[] { (byte)(value ? 1 : 0) }, S7DataType.Bit);
        }

        /// <summary>
        /// 批量写入 Bool 值 — 向连续位地址写入多个布尔值。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();

            var s7Addr = ParseS7Address(address);
            int startBit = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;

            // 计算需要写入的字节范围
            int endBit = startBit + values.Length - 1;
            int startByte = startBit / 8;
            int endByte = endBit / 8;
            int byteCount = endByte - startByte + 1;

            // 先读取当前字节（保留未修改的位）
            string readAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{startByte}"
                : $"{AreaPrefix(s7Addr.Area)}{startByte}";

            var current = ReadS7Raw(readAddr, byteCount, S7DataType.Byte);
            byte[] buffer = current.IsSuccess ? (byte[])current.Content.Clone() : new byte[byteCount];
            if (buffer.Length < byteCount) Array.Resize(ref buffer, byteCount);

            // 设置目标位
            for (int i = 0; i < values.Length; i++)
            {
                int bitIndex = startBit + i;
                int byteIndex = bitIndex / 8 - startByte;
                int bitOffset = bitIndex % 8;

                if (values[i])
                    buffer[byteIndex] |= (byte)(1 << bitOffset);
                else
                    buffer[byteIndex] &= (byte)~(1 << bitOffset);
            }

            return WriteS7Raw(readAddr, buffer, S7DataType.Byte);
        }

        /// <summary>
        /// 异步批量写入 Bool 值。
        /// </summary>
        public Task<OperateResult> WriteBoolsAsync(string address, bool[] values,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => WriteBools(address, values), cancellationToken);
        }

        public override OperateResult Write(string address, short value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 2), S7DataType.Int);
        }

        public override OperateResult Write(string address, ushort value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 2), S7DataType.Word);
        }

        public override OperateResult Write(string address, int value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 4), S7DataType.DInt);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 8), S7DataType.DInt);
        }

        public override OperateResult Write(string address, ulong value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(unchecked((long)value)), 8), S7DataType.DInt);
        }

        public override OperateResult Write(string address, float value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 4), S7DataType.Real);
        }

        public override OperateResult Write(string address, double value)
        {
            return WriteS7Raw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 8), S7DataType.Real);
        }

        public override OperateResult Write(string address, string value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.String);
        }

        public OperateResult WriteStringEncoded(string address, string value)
        {
            return WriteS7Raw(address, StringEncoding.GetBytes(value), S7DataType.String);
        }

        /// <summary>
        /// 写入西门子 S7 String 类型 — 自动添加长度前缀。
        /// PLC 中格式: [最大长度(1B)][当前长度(1B)][ASCII数据...]。
        /// </summary>
        public OperateResult WriteS7String(string address, string value, byte maxLength = 254)
        {
            if (value == null) value = string.Empty;
            byte actualLen = (byte)Math.Min(value.Length, maxLength - 2);

            byte[] data = new byte[2 + actualLen];
            data[0] = maxLength;
            data[1] = actualLen;
            byte[] strBytes = Encoding.ASCII.GetBytes(value);
            Buffer.BlockCopy(strBytes, 0, data, 2, actualLen);

            return WriteS7Raw(address, data, S7DataType.Byte);
        }

        /// <summary>
        /// 写入西门子 WString 类型 — 自动添加 Unicode 长度前缀。
        /// PLC 中格式: [最大长度(2B)][当前长度(2B)][UTF-16BE数据...]。
        /// </summary>
        public OperateResult WriteWString(string address, string value, ushort maxLength = 254)
        {
            if (value == null) value = string.Empty;
            ushort actualLen = (ushort)Math.Min(value.Length, maxLength - 4);

            byte[] strBytes = Encoding.BigEndianUnicode.GetBytes(value);
            if (strBytes.Length > (maxLength - 4) * 2)
                Array.Resize(ref strBytes, (maxLength - 4) * 2);

            byte[] data = new byte[4 + strBytes.Length];
            data[0] = (byte)(maxLength >> 8);
            data[1] = (byte)(maxLength & 0xFF);
            data[2] = (byte)(actualLen >> 8);
            data[3] = (byte)(actualLen & 0xFF);
            Buffer.BlockCopy(strBytes, 0, data, 4, strBytes.Length);

            return WriteS7Raw(address, data, S7DataType.Byte);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            return WriteS7Raw(address, data, S7DataType.Byte);
        }

        // ── PLC 控制命令 ────────────────────────────

        private static readonly byte[] _s7OrderNumber = new byte[33]
        {
            0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32, 0x07,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x08,
            0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00,
            0xFF, 0x09, 0x00, 0x04, 0x00, 0x11, 0x00, 0x00
        };

        private static readonly byte[] _s7Stop = new byte[33]
        {
            0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32, 0x01,
            0x00, 0x00, 0x0E, 0x00, 0x00, 0x10, 0x00, 0x00,
            0x29, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09,
            0x50, 0x5F, 0x50, 0x52, 0x4F, 0x47, 0x52, 0x41, 0x4D
        };

        private static readonly byte[] _s7HotStart = new byte[37]
        {
            0x03, 0x00, 0x00, 0x25, 0x02, 0xF0, 0x80, 0x32, 0x01,
            0x00, 0x00, 0x0C, 0x00, 0x00, 0x14, 0x00, 0x00,
            0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFD,
            0x00, 0x00, 0x09, 0x50, 0x5F, 0x50, 0x52, 0x4F,
            0x47, 0x52, 0x41, 0x4D
        };

        private static readonly byte[] _s7ColdStart = new byte[39]
        {
            0x03, 0x00, 0x00, 0x27, 0x02, 0xF0, 0x80, 0x32, 0x01,
            0x00, 0x00, 0x0F, 0x00, 0x00, 0x16, 0x00, 0x00,
            0x28, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFD,
            0x00, 0x02, 0x43, 0x20, 0x09, 0x50, 0x5F, 0x50,
            0x52, 0x4F, 0x47, 0x52, 0x41, 0x4D
        };

        /// <summary>
        /// 读取 PLC 订货号/型号序列号。
        /// </summary>
        public OperateResult<string> ReadOrderNumber()
        {
            var resp = SendAndReceive((byte[])_s7OrderNumber.Clone());
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 91)
                return OperateResult<string>.Failed($"读取订货号响应长度不足: {raw?.Length ?? 0} < 91");

            string orderNum = Encoding.ASCII.GetString(raw, 71, 20).TrimEnd('\0', ' ');
            return OperateResult<string>.Success(orderNum);
        }

        /// <summary>
        /// 异步读取 PLC 订货号。
        /// </summary>
        public Task<OperateResult<string>> ReadOrderNumberAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => ReadOrderNumber(), cancellationToken);
        }

        /// <summary>
        /// PLC 热启动（从停止状态恢复运行）。
        /// </summary>
        public OperateResult HotStart()
        {
            var resp = SendAndReceive((byte[])_s7HotStart.Clone());
            if (!resp.IsSuccess) return resp;

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 21)
                return OperateResult.Failed("热启动响应长度不足");

            // byte[19] 应为 0x28(40), byte[20] 应为 0x02(已启动)
            if (raw[19] != 0x28)
                return OperateResult.Failed($"热启动失败: 功能码=0x{raw[19]:X2}");
            if (raw[20] != 0x02)
                return OperateResult.Failed($"热启动失败: 状态=0x{raw[20]:X2}");

            return OperateResult.Success();
        }

        /// <summary>
        /// PLC 冷启动（完全重新初始化）。
        /// </summary>
        public OperateResult ColdStart()
        {
            var resp = SendAndReceive((byte[])_s7ColdStart.Clone());
            if (!resp.IsSuccess) return resp;

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 21)
                return OperateResult.Failed("冷启动响应长度不足");

            if (raw[19] != 0x28)
                return OperateResult.Failed($"冷启动失败: 功能码=0x{raw[19]:X2}");
            if (raw[20] != 0x02)
                return OperateResult.Failed($"冷启动失败: 状态=0x{raw[20]:X2}");

            return OperateResult.Success();
        }

        /// <summary>
        /// 停止 PLC 运行。
        /// </summary>
        public OperateResult Stop()
        {
            var resp = SendAndReceive((byte[])_s7Stop.Clone());
            if (!resp.IsSuccess) return resp;

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 21)
                return OperateResult.Failed("停止响应长度不足");

            // byte[19] 应为 0x29(41), byte[20] 应为 0x07(已停止)
            if (raw[19] != 0x29)
                return OperateResult.Failed($"停止失败: 功能码=0x{raw[19]:X2}");
            if (raw[20] != 0x07)
                return OperateResult.Failed($"停止失败: 状态=0x{raw[20]:X2}");

            return OperateResult.Success();
        }

        /// <summary>异步 PLC 热启动。</summary>
        public Task<OperateResult> HotStartAsync(CancellationToken ct = default)
            => Task.Run(() => HotStart(), ct);

        /// <summary>异步 PLC 冷启动。</summary>
        public Task<OperateResult> ColdStartAsync(CancellationToken ct = default)
            => Task.Run(() => ColdStart(), ct);

        /// <summary>异步 PLC 停止。</summary>
        public Task<OperateResult> StopAsync(CancellationToken ct = default)
            => Task.Run(() => Stop(), ct);

        // ── 批量读取 ──────────────────────────────

        private struct BatchItem
        {
            public SiemensS7Address Address;
            public int ByteCount;
            public S7DataType Type;
            public int OriginalIndex;
        }

        private BatchItem ResolveBatchItem(string address)
        {
            var addr = ParseS7Address(address);
            int byteCount;
            S7DataType type;
            switch (addr.DataSize)
            {
                case 1:
                    byteCount = 1;
                    type = S7DataType.Byte;
                    break;
                case 4:
                    byteCount = 4;
                    type = S7DataType.DInt;
                    break;
                case 8:
                    byteCount = 8;
                    type = S7DataType.DInt;
                    break;
                default:
                    byteCount = 2;
                    type = S7DataType.Word;
                    break;
            }
            return new BatchItem { Address = addr, ByteCount = byteCount, Type = type };
        }

        private OperateResult<byte[][]> BatchReadRaw(IEnumerable<string> addresses)
        {
            var itemList = addresses.Select((addr, i) =>
            {
                var item = ResolveBatchItem(addr);
                item.OriginalIndex = i;
                return item;
            }).ToList();

            if (itemList.Count == 0) return OperateResult<byte[][]>.Success(Array.Empty<byte[]>());

            int maxItemsPerRequest = (MaxPduSize - 12 - 2) / 12;
            if (maxItemsPerRequest < 1) maxItemsPerRequest = 1;
            // S7 协议最多支持19个地址/包
            if (maxItemsPerRequest > 19) maxItemsPerRequest = 19;

            byte[][] results = new byte[itemList.Count][];
            int offset = 0;

            while (offset < itemList.Count)
            {
                int chunkSize = Math.Min(maxItemsPerRequest, itemList.Count - offset);
                var chunk = itemList.GetRange(offset, chunkSize);

                byte[] param = new byte[2 + chunkSize * 12];
                param[0] = 0x04;
                param[1] = (byte)chunkSize;
                for (int i = 0; i < chunkSize; i++)
                {
                    var item = chunk[i];
                    int addrBits = item.Address.ByteAddress * 8 + item.Address.BitOffset;
                    byte wordLenByte = (byte)(item.Type == S7DataType.Bit ? 0x01 : 0x02);
                    byte[] itemBytes = BuildS7AddressItem(0x04, wordLenByte, item.ByteCount,
                        item.Address.Area, item.Address.DBNumber, (ushort)addrBits);
                    Buffer.BlockCopy(itemBytes, 2, param, 2 + i * 12, 12);
                }

                var req = BuildCOTPDataRequest(BuildS7Header(0x04, param, null));
                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return OperateResult<byte[][]>.Failed(resp.Message, resp.ErrorCode);

                byte[] raw = resp.Content;
                int dataOffset = 19 + 2; // TPKT(4)+COTP(3)+S7Header(12)+Param(2)
                if (raw.Length < dataOffset) return OperateResult<byte[][]>.Failed("批量读取响应长度不足");

                for (int i = 0; i < chunkSize; i++)
                {
                    int itemDataOffset = dataOffset;
                    for (int j = 0; j < i; j++)
                    {
                        if (itemDataOffset + 4 > raw.Length) break;
                        int prevLen = (raw[itemDataOffset + 2] << 8) | raw[itemDataOffset + 3];
                        itemDataOffset += 4 + prevLen;
                    }

                    if (itemDataOffset + 4 > raw.Length)
                    {
                        results[chunk[i].OriginalIndex] = new byte[0];
                        continue;
                    }

                    byte retCode = raw[itemDataOffset];
                    if (retCode != 0xFF)
                    {
                        results[chunk[i].OriginalIndex] = new byte[0];
                        continue;
                    }

                    int dataLen = (raw[itemDataOffset + 2] << 8) | raw[itemDataOffset + 3];
                    byte[] data = new byte[dataLen];
                    if (itemDataOffset + 4 + dataLen <= raw.Length)
                        Buffer.BlockCopy(raw, itemDataOffset + 4, data, 0, dataLen);
                    results[chunk[i].OriginalIndex] = data;
                }

                offset += chunkSize;
            }

            return OperateResult<byte[][]>.Success(results);
        }

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            var r = BatchReadRaw(addrList);
            if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);

            var dict = new Dictionary<string, object?>();
            for (int i = 0; i < addrList.Count; i++)
            {
                var addr = ParseS7Address(addrList[i]);
                byte[] data = r.Content[i];
                object? val = null;
                if (data.Length > 0)
                {
                    switch (addr.DataSize)
                    {
                        case 1: val = data[0]; break;
                        case 2: val = DataConverter.ToInt16(ApplyByteOrderRead(data, 2), 0); break;
                        case 4: val = DataConverter.ToInt32(ApplyByteOrderRead(data, 4), 0); break;
                        case 8: val = DataConverter.ToInt64(ApplyByteOrderRead(data, 8), 0); break;
                        default: val = data; break;
                    }
                }
                dict[addrList[i]] = val;
            }
            return OperateResult<Dictionary<string, object?>>.Success(dict);
        }

        public async Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => BatchRead(addresses), cancellationToken).ConfigureAwait(false);
        }

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            var r = BatchReadRaw(addrList);
            if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);

            var dict = new Dictionary<string, byte[]>();
            for (int i = 0; i < addrList.Count; i++)
                dict[addrList[i]] = r.Content[i];
            return OperateResult<Dictionary<string, byte[]>>.Success(dict);
        }

        public async Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => RandomRead(addresses), cancellationToken).ConfigureAwait(false);
        }

        // ── 批量写入 ──────────────────────────────

        private OperateResult BatchWriteRaw(IEnumerable<KeyValuePair<string, byte[]>> items)
        {
            var itemList = items.Select(kvp =>
            {
                var addr = ParseS7Address(kvp.Key);
                return new { Address = addr, Data = kvp.Value };
            }).ToList();

            if (itemList.Count == 0) return OperateResult.Success();

            int maxItemsPerRequest = (MaxPduSize - 12 - 2) / (12 + 4);
            if (maxItemsPerRequest < 1) maxItemsPerRequest = 1;
            if (maxItemsPerRequest > 19) maxItemsPerRequest = 19;

            int offset = 0;
            while (offset < itemList.Count)
            {
                int chunkSize = Math.Min(maxItemsPerRequest, itemList.Count - offset);
                var chunk = itemList.GetRange(offset, chunkSize);

                byte[] param = new byte[2 + chunkSize * 12];
                param[0] = 0x05;
                param[1] = (byte)chunkSize;

                int dataLen = 0;
                foreach (var item in chunk)
                    dataLen += 4 + item.Data.Length;

                byte[] data = new byte[dataLen];
                int dataPos = 0;
                for (int i = 0; i < chunkSize; i++)
                {
                    var item = chunk[i];
                    int addrBits = item.Address.ByteAddress * 8 + item.Address.BitOffset;
                    byte[] itemBytes = BuildS7AddressItem(0x05, 0x02, item.Data.Length,
                        item.Address.Area, item.Address.DBNumber, (ushort)addrBits);
                    Buffer.BlockCopy(itemBytes, 2, param, 2 + i * 12, 12);

                    data[dataPos] = 0x00;
                    data[dataPos + 1] = 0x02;
                    data[dataPos + 2] = (byte)(item.Data.Length >> 8);
                    data[dataPos + 3] = (byte)(item.Data.Length & 0xFF);
                    Buffer.BlockCopy(item.Data, 0, data, dataPos + 4, item.Data.Length);
                    dataPos += 4 + item.Data.Length;
                }

                var req = BuildCOTPDataRequest(BuildS7Header(0x05, param, data));
                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return resp;

                if (resp.Content.Length > 19 + 2 + chunkSize)
                {
                    // 响应: TPKT(4)+COTP(3)+S7Header(12)+Param(2)+Data(returnCode*chunkSize)
                    // 返回码在 data 区，紧接 param 之后
                    for (int i = 0; i < chunkSize; i++)
                    {
                        int retOffset = 21 + i; // 19+2+i
                        if (retOffset < resp.Content.Length)
                        {
                            byte retCode = resp.Content[retOffset];
                            if (retCode != 0xFF)
                                return OperateResult.Failed($"S7批量写入错误: 项目{i} 返回码=0x{retCode:X2}", retCode);
                        }
                    }
                }

                offset += chunkSize;
            }

            return OperateResult.Success();
        }

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var rawItems = new List<KeyValuePair<string, byte[]>>();
            foreach (var kvp in items)
            {
                byte[] data;
                switch (kvp.Value)
                {
                    case bool bv:
                        data = new byte[] { (byte)(bv ? 1 : 0) };
                        break;
                    case short sv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes(sv), 2);
                        break;
                    case ushort usv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes(usv), 2);
                        break;
                    case int iv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes(iv), 4);
                        break;
                    case uint uiv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes((int)uiv), 4);
                        break;
                    case float fv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes(fv), 4);
                        break;
                    case double dv:
                        data = ApplyByteOrderWrite(DataConverter.GetBytes((float)dv), 4);
                        break;
                    case string sv:
                        data = DataConverter.GetBytes(sv);
                        break;
                    case byte[] bv:
                        data = bv;
                        break;
                    default:
                        data = DataConverter.GetBytes(Convert.ToInt32(kvp.Value));
                        break;
                }
                rawItems.Add(new KeyValuePair<string, byte[]>(kvp.Key, data));
            }
            return BatchWriteRaw(rawItems);
        }

        public async Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
        {
            return await Task.Run(() => BatchWrite(items), cancellationToken).ConfigureAwait(false);
        }

        // ── DB 块发现 ──────────────────────────────

        /// <summary>
        /// 读取 PLC 中所有 DB 块编号列表。
        /// 使用 S7 ListBlocks 功能码 (0x1A/0x1B) 枚举 PLC 中的数据块。
        /// 仅适用于 S7-300/400/1200/1500。
        /// </summary>
        public OperateResult<int[]> ListDBBlocks()
        {
            // S7 ListBlocks 请求: 读取 DB 类型 (0x0A) 的块列表
            byte[] listBlocksReq =
            {
                0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32,
                0x07, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x0C, 0x00, 0x00, 0x01, 0x12, 0x08, 0x12,
                0x06, 0x0A, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0A
            };

            var resp = SendAndReceive(listBlocksReq);
            if (!resp.IsSuccess) return OperateResult<int[]>.Failed(resp.Message, resp.ErrorCode);

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 30)
                return OperateResult<int[]>.Failed($"ListBlocks 响应长度不足: {raw?.Length ?? 0}");

            // 解析响应中的块编号列表
            try
            {
                // 检查 S7 错误码 (byte 21-23)
                if (raw.Length > 21 && raw[21] != 0x0A && raw[21] != 0x00)
                {
                    // 某些型号可能不支持此功能
                    byte errClass = raw.Length > 25 ? raw[25] : (byte)0;
                    byte errCode = raw.Length > 26 ? raw[26] : (byte)0;
                    if (errClass != 0 || errCode != 0)
                        return OperateResult<int[]>.Failed($"PLC 不支持 ListBlocks: class=0x{errClass:X2} code=0x{errCode:X2}");
                }

                // 尝试从响应中提取块信息
                var blocks = new List<int>();
                int payloadStart = 27;
                while (payloadStart + 4 <= raw.Length)
                {
                    byte blockType = raw[payloadStart];
                    if (blockType == 0x00) break; // 结束标记

                    int blockNum = (raw[payloadStart + 1] << 8) | raw[payloadStart + 2];
                    if (blockType == 0x0A || blockType == 0x0B) // DB 类型
                        blocks.Add(blockNum);

                    payloadStart += 4;
                }

                return OperateResult<int[]>.Success(blocks.ToArray());
            }
            catch (Exception ex)
            {
                return OperateResult<int[]>.Failed($"解析块列表失败: {ex.Message}");
            }
        }

        /// <summary>异步读取 DB 块编号列表。</summary>
        public Task<OperateResult<int[]>> ListDBBlocksAsync(CancellationToken ct = default)
            => Task.Run(() => ListDBBlocks(), ct);

        /// <summary>
        /// 读取指定 DB 块的大小（字节数）。
        /// 使用 GetBlockInfo 功能码 (0x1A/0x1D)。
        /// </summary>
        public OperateResult<int> GetDBBlockSize(int dbNumber)
        {
            byte[] getBlockInfoReq =
            {
                0x03, 0x00, 0x00, 0x25, 0x02, 0xF0, 0x80, 0x32,
                0x07, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x10, 0x00, 0x00, 0x01, 0x12, 0x08, 0x12,
                0x08, 0x0A, 0x01, 0x12, 0x05, 0x00,
                (byte)(dbNumber >> 8), (byte)(dbNumber & 0xFF),
                0x0A, 0x00, 0x00, 0x00
            };

            var resp = SendAndReceive(getBlockInfoReq);
            if (!resp.IsSuccess) return OperateResult<int>.Failed(resp.Message, resp.ErrorCode);

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 50)
                return OperateResult<int>.Failed($"GetBlockInfo 响应长度不足: {raw?.Length ?? 0}");

            try
            {
                // 块大小在响应尾部的特定偏移
                int sizeOffset = raw.Length - 8;
                if (sizeOffset < 30) sizeOffset = 42;
                int blockSize = (raw[sizeOffset] << 24) | (raw[sizeOffset + 1] << 16) |
                                (raw[sizeOffset + 2] << 8) | raw[sizeOffset + 3];
                return OperateResult<int>.Success(blockSize);
            }
            catch (Exception ex)
            {
                return OperateResult<int>.Failed($"解析块大小失败: {ex.Message}");
            }
        }

        // ── PLC 时钟 ────────────────────────────────

        /// <summary>
        /// 读取 PLC 系统时钟。
        /// 使用 S7 ReadClock 功能码 (SZL 0x0220 / 0x0001)，返回 PLC 当前时间。
        /// </summary>
        public OperateResult<DateTime> ReadPlcClock()
        {
            // SZL 请求: 读取时钟 (SZL ID=0x0220, Index=0x0001)
            byte[] readClockReq =
            {
                0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32,
                0x07, 0x00, 0x00, 0x10, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x0C, 0x00, 0x00, 0x01, 0x12, 0x04, 0x11,
                0x44, 0x01, 0x00, 0xFF, 0x09, 0x00, 0x22, 0x00,
                0x01
            };

            var resp = SendAndReceive(readClockReq);
            if (!resp.IsSuccess) return OperateResult<DateTime>.Failed(resp.Message, resp.ErrorCode);

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 50)
                return OperateResult<DateTime>.Failed($"ReadClock 响应长度不足: {raw?.Length ?? 0}");

            try
            {
                // S7 时钟数据在 SZL 响应尾部，格式为：
                // Year(2) Month(1) Day(1) Hour(1) Minute(1) Second(1) DayOfWeek(1) Milliseconds(2) ?
                // 跳过 SZL 头部 (通常到偏移 42 附近开始时钟数据)
                int clockStart = raw.Length - 14;
                if (clockStart < 30) return OperateResult<DateTime>.Failed("时钟数据偏移异常");

                int year = (raw[clockStart] << 8) | raw[clockStart + 1];
                int month = raw[clockStart + 2];
                int day = raw[clockStart + 3];
                int hour = raw[clockStart + 5];
                int minute = raw[clockStart + 6];
                int second = raw[clockStart + 7];

                if (year < 2000 || month < 1 || month > 12 || day < 1 || day > 31)
                    return OperateResult<DateTime>.Failed($"时钟数据不合法: {year}-{month:D2}-{day:D2} {hour:D2}:{minute:D2}:{second:D2}");

                return OperateResult<DateTime>.Success(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc));
            }
            catch (Exception ex)
            {
                return OperateResult<DateTime>.Failed($"解析 PLC 时钟失败: {ex.Message}");
            }
        }

        /// <summary>异步读取 PLC 系统时钟。</summary>
        public Task<OperateResult<DateTime>> ReadPlcClockAsync(CancellationToken ct = default)
            => Task.Run(() => ReadPlcClock(), ct);

        // ── 定时器/计数器读写 ────────────────────

        /// <summary>
        /// 读取定时器当前值（BCD 格式，2 字节）。
        /// 地址示例: timerNumber=0 表示 T0。
        /// </summary>
        public OperateResult<short> ReadTimer(int timerNumber)
        {
            string address = $"T{timerNumber}";
            var r = ReadS7Raw(address, 2, S7DataType.Timer);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        /// <summary>异步读取定时器当前值。</summary>
        public Task<OperateResult<short>> ReadTimerAsync(int timerNumber, CancellationToken ct = default)
            => Task.Run(() => ReadTimer(timerNumber), ct);

        /// <summary>
        /// 读取计数器当前值（BCD 格式，2 字节）。
        /// 地址示例: counterNumber=0 表示 C0。
        /// </summary>
        public OperateResult<short> ReadCounter(int counterNumber)
        {
            string address = $"C{counterNumber}";
            var r = ReadS7Raw(address, 2, S7DataType.Counter);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        /// <summary>异步读取计数器当前值。</summary>
        public Task<OperateResult<short>> ReadCounterAsync(int counterNumber, CancellationToken ct = default)
            => Task.Run(() => ReadCounter(counterNumber), ct);

        /// <summary>
        /// 写入定时器预设值。
        /// </summary>
        public OperateResult WriteTimer(int timerNumber, short value)
        {
            string address = $"T{timerNumber}";
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.Timer);
        }

        /// <summary>异步写入定时器预设值。</summary>
        public Task<OperateResult> WriteTimerAsync(int timerNumber, short value, CancellationToken ct = default)
            => Task.Run(() => WriteTimer(timerNumber, value), ct);

        /// <summary>
        /// 写入计数器预设值。
        /// </summary>
        public OperateResult WriteCounter(int counterNumber, short value)
        {
            string address = $"C{counterNumber}";
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.Counter);
        }

        /// <summary>异步写入计数器预设值。</summary>
        public Task<OperateResult> WriteCounterAsync(int counterNumber, short value, CancellationToken ct = default)
            => Task.Run(() => WriteCounter(counterNumber, value), ct);

        /// <summary>
        /// 批量读取多个定时器当前值。
        /// </summary>
        public OperateResult<short[]> ReadTimers(int startTimer, int count)
        {
            if (count <= 0) return OperateResult<short[]>.Success(new short[0]);
            var addresses = Enumerable.Range(startTimer, count).Select(i => $"T{i}").ToArray();
            var r = BatchReadRaw(addresses);
            if (!r.IsSuccess) return OperateResult<short[]>.Failed(r.Message, r.ErrorCode);

            var result = new short[count];
            for (int i = 0; i < count; i++)
            {
                if (r.Content[i].Length >= 2)
                    result[i] = DataConverter.ToInt16(r.Content[i], 0);
            }
            return OperateResult<short[]>.Success(result);
        }

        /// <summary>异步批量读取定时器。</summary>
        public Task<OperateResult<short[]>> ReadTimersAsync(int startTimer, int count, CancellationToken ct = default)
            => Task.Run(() => ReadTimers(startTimer, count), ct);

        /// <summary>
        /// 批量读取多个计数器当前值。
        /// </summary>
        public OperateResult<short[]> ReadCounters(int startCounter, int count)
        {
            if (count <= 0) return OperateResult<short[]>.Success(new short[0]);
            var addresses = Enumerable.Range(startCounter, count).Select(i => $"C{i}").ToArray();
            var r = BatchReadRaw(addresses);
            if (!r.IsSuccess) return OperateResult<short[]>.Failed(r.Message, r.ErrorCode);

            var result = new short[count];
            for (int i = 0; i < count; i++)
            {
                if (r.Content[i].Length >= 2)
                    result[i] = DataConverter.ToInt16(r.Content[i], 0);
            }
            return OperateResult<short[]>.Success(result);
        }

        /// <summary>异步批量读取计数器。</summary>
        public Task<OperateResult<short[]>> ReadCountersAsync(int startCounter, int count, CancellationToken ct = default)
            => Task.Run(() => ReadCounters(startCounter, count), ct);

        // ── PLC 状态检测 ──────────────────────────

        /// <summary>
        /// 读取 PLC 运行状态（通过 SZL 0x0424 模块状态信息）。
        /// 返回 "RUN"、"STOP" 或 "STARTUP"。
        /// </summary>
        public OperateResult<string> ReadPlcStatus()
        {
            byte[] readStatusReq =
            {
                0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32,
                0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00,
                0x00, 0x08, 0x00, 0x00, 0x01, 0x12, 0x04, 0x11,
                0x44, 0x01, 0x00, 0xFF, 0x09, 0x00, 0x04, 0x04,
                0x24, 0x00, 0x00
            };

            var resp = SendAndReceive(readStatusReq);
            if (!resp.IsSuccess) return OperateResult<string>.Failed(resp.Message, resp.ErrorCode);

            byte[] raw = resp.Content;
            if (raw == null || raw.Length < 32)
                return OperateResult<string>.Failed($"ReadPlcStatus 响应长度不足: {raw?.Length ?? 0}");

            // SZL 0x0424 数据在响应尾部
            // 状态字节通常在偏移 32 附近 (SZL ID + 数据)
            // RUN=0x08, STOP=0x04, STARTUP=0x02
            int statusOffset = raw.Length >= 34 ? 32 : raw.Length - 2;
            if (statusOffset < 0) statusOffset = 0;

            byte statusByte = raw[statusOffset];
            string status;
            switch (statusByte)
            {
                case 0x08:
                    status = "RUN";
                    break;
                case 0x04:
                    status = "STOP";
                    break;
                case 0x02:
                    status = "STARTUP";
                    break;
                default:
                    status = $"UNKNOWN(0x{statusByte:X2})";
                    break;
            }

            return OperateResult<string>.Success(status);
        }

        /// <summary>异步读取 PLC 运行状态。</summary>
        public Task<OperateResult<string>> ReadPlcStatusAsync(CancellationToken ct = default)
            => Task.Run(() => ReadPlcStatus(), ct);

        // ── 辅助方法 ──────────────────────────────

        /// <summary>
        /// 获取区域地址前缀（用于构建地址字符串）。
        /// </summary>
        private static string AreaPrefix(S7Area area)
        {
            switch (area)
            {
                case S7Area.PE: return "I";
                case S7Area.PA: return "Q";
                case S7Area.MK: return "M";
                case S7Area.DB: return "DB";
                case S7Area.V: return "V";
                case S7Area.TM: return "T";
                case S7Area.CT: return "C";
                default: return "";
            }
        }

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
        protected override byte[] BuildHeartbeat()
        {
            try { return BuildS7SetupCommunication(); }
            catch { return null; }
        }
    }
}
