using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 S7 Plus 协议客户端 — 支持 S7-1200/1500 (TIA Portal 优化)。
    /// 基于 TPKT + COTP + S7 Plus 三层协议栈。
    /// </summary>
    /// <remarks>
    /// <para>S7 Plus 是新版 S7 协议，用于 S7-1200/1500 等新一代 PLC：</para>
    /// <list type="bullet">
    ///   <item>魔术字节 0x72（经典 S7 使用 0x32）</item>
    ///   <item>不同的 PDU 结构和功能码</item>
    ///   <item>支持更大的数据块和符号寻址</item>
    ///   <item>请求格式: TPKT(4) + COTP(3-4) + S7Plus PDU</item>
    /// </list>
    /// </remarks>
    public class SiemensS7PlusClient : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public SiemensPLCS PLCType { get; }
        public byte Rack { get; set; } = 0;
        public byte Slot { get; set; } = 1;
        public ushort MaxPduSize { get; private set; } = 960;

        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        public Encoding StringEncoding { get; set; } = Encoding.ASCII;

        /// <summary>
        /// 连接类型。PG=0x01, OP=0x02, S7Basic=0x03~0x10。
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

        private const byte S7PlusMagic = 0x72;
        private const byte PduTypeRequest = 0x00;
        private const byte PduTypeResponse = 0x03;

        // S7 Plus 功能码
        private const byte FuncReadVar = 0x04;
        private const byte FuncWriteVar = 0x05;
        private const byte FuncSetupCommunication = 0xF0;

        public SiemensS7PlusClient(SiemensPLCS plcType, string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout)
        {
            PLCType = plcType;
            SetHeartbeatCallback(SendHeartbeatAsync);
        }

        // ── S7 Plus 协议层 ─────────────────────────

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

        private Task<OperateResult> SendHeartbeatAsync()
        {
            return Task.Run(() =>
            {
                var result = ReadS7PlusRaw("M0", 1, S7PlusDataType.Byte);
                return result.IsSuccess
                    ? OperateResult.Success()
                    : OperateResult.Failed(result.Message, result.ErrorCode);
            });
        }

        // ── TPKT + COTP + S7 Plus 报文构建 ────────

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
            // S7 Plus 使用不同的 COTP 连接参数
            // 目标 TSAP: 0x0100 + Rack*0x20 + Slot
            // 源 TSAP: ConnectionType << 8 | 0x01
            int localTsap = LocalTSAP ?? (ConnectionType << 8 | 0x01);
            int destTsap = DestTSAP ?? (0x01 << 8 | (Rack * 0x20 + Slot));

            byte[] cr = new byte[]
            {
                0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
                0xC0, 0x01, 0x0A,
                (byte)((localTsap >> 8) & 0xFF), (byte)(localTsap & 0xFF),
                0xC1, 0x01, 0x0A,
                (byte)((localTsap >> 8) & 0xFF), (byte)(localTsap & 0xFF),
                0xC0, 0x01, 0x09,
                (byte)((destTsap >> 8) & 0xFF), (byte)(destTsap & 0xFF)
            };

            return BuildTPKT(cr);
        }

        private byte[] BuildCOTPDataRequest(byte[] s7PlusPdu)
        {
            byte[] cotpData = new byte[3 + s7PlusPdu.Length];
            cotpData[0] = 0x02;
            cotpData[1] = 0xF0;
            cotpData[2] = 0x80;
            Buffer.BlockCopy(s7PlusPdu, 0, cotpData, 3, s7PlusPdu.Length);
            return BuildTPKT(cotpData);
        }

        /// <summary>
        /// 构建 S7 Plus PDU 头。
        /// 格式: Magic(0x72) + PDU类型 + 数据长度(2) + ...
        /// </summary>
        private byte[] BuildS7PlusHeader(byte pduType, byte[] data)
        {
            int dataLen = data.Length;
            byte[] header = new byte[4 + dataLen];
            header[0] = S7PlusMagic;
            header[1] = pduType;
            header[2] = (byte)((dataLen >> 8) & 0xFF);
            header[3] = (byte)(dataLen & 0xFF);
            Buffer.BlockCopy(data, 0, header, 4, dataLen);
            return header;
        }

        /// <summary>
        /// 构建 S7 Plus Setup Communication 请求。
        /// </summary>
        private byte[] BuildS7PlusSetupCommunication()
        {
            // S7 Plus 协商 PDU 大小
            byte[] setupData = new byte[]
            {
                0x00, 0x00, 0x00, 0x00,
                0xF0, 0x00, 0x00, 0x01, 0x00, 0x01,
                0x03, (byte)(MaxPduSize >> 8), (byte)(MaxPduSize & 0xFF)
            };
            return BuildCOTPDataRequest(BuildS7PlusHeader(PduTypeRequest, setupData));
        }

        /// <summary>
        /// 构建 S7 Plus 读取请求的地址项。
        /// S7 Plus 使用与经典 S7 相同的地址编码方式。
        /// </summary>
        private static byte[] BuildS7PlusAddressItem(
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

        /// <summary>
        /// 构建完整的 S7 Plus 请求 PDU（参数 + 数据）。
        /// </summary>
        private byte[] BuildS7PlusRequest(byte function, byte[]? param, byte[]? data)
        {
            int paramLen = param?.Length ?? 0;
            int dataLen = data?.Length ?? 0;

            // S7 Plus PDU: Magic + Type + Length + ParamLength(2) + DataLength(2) + Param + Data
            byte[] pdu = new byte[8 + paramLen + dataLen];
            pdu[0] = S7PlusMagic;
            pdu[1] = PduTypeRequest;
            int totalDataLen = 4 + paramLen + dataLen;
            pdu[2] = (byte)((totalDataLen >> 8) & 0xFF);
            pdu[3] = (byte)(totalDataLen & 0xFF);
            pdu[4] = (byte)((paramLen >> 8) & 0xFF);
            pdu[5] = (byte)(paramLen & 0xFF);
            pdu[6] = (byte)((dataLen >> 8) & 0xFF);
            pdu[7] = (byte)(dataLen & 0xFF);

            if (param != null) Buffer.BlockCopy(param, 0, pdu, 8, paramLen);
            if (data != null) Buffer.BlockCopy(data, 0, pdu, 8 + paramLen, dataLen);

            return BuildCOTPDataRequest(pdu);
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

                // 阶段2: S7 Plus 通信设置（协商 PDU）
                var setupReq = BuildS7PlusSetupCommunication();
                var setupResp = SendAndReceive(setupReq);
                if (!setupResp.IsSuccess) return setupResp;

                // 解析协商的 PDU 大小
                if (setupResp.Content.Length > 26)
                {
                    MaxPduSize = (ushort)((setupResp.Content[25] << 8) | setupResp.Content[26]);
                    if (MaxPduSize < 16) MaxPduSize = 960;
                }

                return OperateResult.Success();
            }
            finally
            {
                _persistentMode = wasPersistent;
            }
        }

        // ── 地址解析 ──────────────────────────────

        private static SiemensS7Address ParseS7Address(string address)
            => SiemensS7Address.Parse(address);

        // ── S7 Plus 数据类型 ──────────────────────

        private enum S7PlusDataType { Bit, Byte, Word, Int, DInt, Real, String }

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

        private OperateResult<byte[]> ReadS7PlusRaw(string address, int byteCount, S7PlusDataType type)
        {
            var s7Addr = ParseS7Address(address);
            int addrBits = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;
            byte wordLenByte = (byte)(type == S7PlusDataType.Bit ? 0x01 : 0x02);

            byte[] param = BuildS7PlusAddressItem(FuncReadVar, wordLenByte, byteCount,
                s7Addr.Area, s7Addr.DBNumber, (ushort)addrBits);

            var req = BuildS7PlusRequest(FuncReadVar, param, null);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            byte[] raw = resp.Content;
            // S7 Plus 响应: TPKT(4) + COTP(3) + S7PlusHeader(4+) + ...
            // 数据区起始偏移根据实际协议调整
            const int DataOffset = 25;
            if (raw.Length < DataOffset) return OperateResult<byte[]>.Failed("S7 Plus 响应长度不足");

            byte returnCode = raw[21];
            if (returnCode != 0xFF)
                return OperateResult<byte[]>.Failed($"S7 Plus 读取错误: 返回码=0x{returnCode:X2}", returnCode);

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
            int maxPdu = MaxPduSize > 0 ? MaxPduSize : 960;

            if (totalBytes <= maxPdu)
                return ReadS7PlusRaw(address, totalBytes, S7PlusDataType.Byte);

            var result = new List<byte>(totalBytes);
            int offset = 0;

            while (offset < totalBytes)
            {
                int chunkSize = Math.Min(totalBytes - offset, maxPdu);
                string chunkAddr = s7Addr.Area == S7Area.DB
                    ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + offset}"
                    : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + offset}";

                var chunk = ReadS7PlusRaw(chunkAddr, chunkSize, S7PlusDataType.Byte);
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
            var r = ReadS7PlusRaw(address, 1, S7PlusDataType.Bit);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

        /// <summary>
        /// 批量读取 Bool 值 — 从起始地址读取多个连续位。
        /// </summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            if (count == 0) return OperateResult<bool[]>.Success(Array.Empty<bool>());

            var s7Addr = ParseS7Address(address);
            int startBit = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;

            int totalBits = startBit + count;
            int bytesNeeded = (totalBits + 7) / 8;
            int readStart = startBit / 8;

            string readAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{readStart}"
                : $"{AreaPrefix(s7Addr.Area)}{readStart}";

            var r = ReadS7PlusRaw(readAddr, bytesNeeded, S7PlusDataType.Byte);
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
            var r = ReadS7PlusRaw(address, 2, S7PlusDataType.Int);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadS7PlusRaw(address, 2, S7PlusDataType.Word);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(ApplyByteOrderRead(r.Content, 2), 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadS7PlusRaw(address, 4, S7PlusDataType.DInt);
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
            var r = ReadS7PlusRaw(address, 8, S7PlusDataType.DInt);
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
            var r = ReadS7PlusRaw(address, 4, S7PlusDataType.Real);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(DataConverter.ToFloat(ApplyByteOrderRead(r.Content, 4), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadS7PlusRaw(address, 8, S7PlusDataType.Real);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(ApplyByteOrderRead(r.Content, 8), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadS7PlusRaw(address, length, S7PlusDataType.String);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
        }

        /// <summary>
        /// 读取西门子 S7 String 类型 — 自动处理长度前缀。
        /// PLC 中格式: [最大长度(1B)][当前长度(1B)][ASCII数据...]。
        /// </summary>
        public OperateResult<string> ReadS7String(string address)
        {
            var header = ReadS7PlusRaw(address, 2, S7PlusDataType.Byte);
            if (!header.IsSuccess) return OperateResult<string>.Failed(header.Message, header.ErrorCode);

            int actualLen = header.Content[1];
            if (actualLen == 0) return OperateResult<string>.Success(string.Empty);

            var s7Addr = ParseS7Address(address);
            string dataAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + 2}"
                : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + 2}";

            var data = ReadS7PlusRaw(dataAddr, actualLen, S7PlusDataType.Byte);
            if (!data.IsSuccess) return OperateResult<string>.Failed(data.Message, data.ErrorCode);

            return OperateResult<string>.Success(Encoding.ASCII.GetString(data.Content, 0, data.Content.Length));
        }

        /// <summary>
        /// 读取西门子 WString 类型 — Unicode 双字节字符串。
        /// PLC 中格式: [最大长度(2B)][当前长度(2B)][UTF-16LE数据...]。
        /// </summary>
        public OperateResult<string> ReadWString(string address)
        {
            var header = ReadS7PlusRaw(address, 4, S7PlusDataType.Byte);
            if (!header.IsSuccess) return OperateResult<string>.Failed(header.Message, header.ErrorCode);

            int actualLen = (header.Content[2] << 8) | header.Content[3];
            if (actualLen == 0) return OperateResult<string>.Success(string.Empty);

            var s7Addr = ParseS7Address(address);
            string dataAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{s7Addr.ByteAddress + 4}"
                : $"{AreaPrefix(s7Addr.Area)}{s7Addr.ByteAddress + 4}";

            var data = ReadS7PlusRaw(dataAddr, actualLen * 2, S7PlusDataType.Byte);
            if (!data.IsSuccess) return OperateResult<string>.Failed(data.Message, data.ErrorCode);

            return OperateResult<string>.Success(Encoding.BigEndianUnicode.GetString(data.Content, 0, data.Content.Length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadS7PlusRaw(address, length, S7PlusDataType.Byte);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return r;
        }

        // ── 写入实现 ──────────────────────────────

        private OperateResult WriteS7PlusRaw(string address, byte[] data, S7PlusDataType type)
        {
            var s7Addr = ParseS7Address(address);
            int addrBits = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;
            byte wordLen = (byte)(type == S7PlusDataType.Bit ? 0x01 : 0x02);

            byte[] param = BuildS7PlusAddressItem(FuncWriteVar, wordLen, data.Length,
                s7Addr.Area, s7Addr.DBNumber, (ushort)addrBits);

            byte[] s7Data = new byte[4 + data.Length];
            s7Data[0] = 0x00;
            s7Data[1] = wordLen;
            s7Data[2] = (byte)(data.Length >> 8);
            s7Data[3] = (byte)(data.Length & 0xFF);
            Buffer.BlockCopy(data, 0, s7Data, 4, data.Length);

            var req = BuildS7PlusRequest(FuncWriteVar, param, s7Data);
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            if (resp.Content.Length > 21)
            {
                byte retCode = resp.Content[21];
                if (retCode != 0xFF)
                    return OperateResult.Failed($"S7 Plus 写入错误: 返回码=0x{retCode:X2}", retCode);
            }

            return OperateResult.Success();
        }

        /// <summary>
        /// 写入大块字节数据 — 自动按 PDU 大小分割。
        /// </summary>
        public OperateResult WriteLarge(string address, byte[] data)
        {
            if (data == null || data.Length == 0) return OperateResult.Success();

            int maxPdu = MaxPduSize > 0 ? MaxPduSize : 960;

            if (data.Length <= maxPdu)
                return WriteS7PlusRaw(address, data, S7PlusDataType.Byte);

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

                var r = WriteS7PlusRaw(chunkAddr, chunk, S7PlusDataType.Byte);
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
            return WriteS7PlusRaw(address, new byte[] { (byte)(value ? 1 : 0) }, S7PlusDataType.Bit);
        }

        /// <summary>
        /// 批量写入 Bool 值 — 向连续位地址写入多个布尔值。
        /// </summary>
        public OperateResult WriteBools(string address, bool[] values)
        {
            if (values == null || values.Length == 0) return OperateResult.Success();

            var s7Addr = ParseS7Address(address);
            int startBit = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;

            int endBit = startBit + values.Length - 1;
            int startByte = startBit / 8;
            int endByte = endBit / 8;
            int byteCount = endByte - startByte + 1;

            string readAddr = s7Addr.Area == S7Area.DB
                ? $"DB{s7Addr.DBNumber}.DB{startByte}"
                : $"{AreaPrefix(s7Addr.Area)}{startByte}";

            var current = ReadS7PlusRaw(readAddr, byteCount, S7PlusDataType.Byte);
            byte[] buffer = current.IsSuccess ? (byte[])current.Content.Clone() : new byte[byteCount];
            if (buffer.Length < byteCount) Array.Resize(ref buffer, byteCount);

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

            return WriteS7PlusRaw(readAddr, buffer, S7PlusDataType.Byte);
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
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 2), S7PlusDataType.Int);
        }

        public override OperateResult Write(string address, ushort value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 2), S7PlusDataType.Word);
        }

        public override OperateResult Write(string address, int value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 4), S7PlusDataType.DInt);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 8), S7PlusDataType.DInt);
        }

        public override OperateResult Write(string address, ulong value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(unchecked((long)value)), 8), S7PlusDataType.DInt);
        }

        public override OperateResult Write(string address, float value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 4), S7PlusDataType.Real);
        }

        public override OperateResult Write(string address, double value)
        {
            return WriteS7PlusRaw(address, ApplyByteOrderWrite(DataConverter.GetBytes(value), 8), S7PlusDataType.Real);
        }

        public override OperateResult Write(string address, string value)
        {
            return WriteS7PlusRaw(address, DataConverter.GetBytes(value), S7PlusDataType.String);
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

            return WriteS7PlusRaw(address, data, S7PlusDataType.Byte);
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

            return WriteS7PlusRaw(address, data, S7PlusDataType.Byte);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            return WriteS7PlusRaw(address, data, S7PlusDataType.Byte);
        }

        // ── 批量读取 ──────────────────────────────

        private struct BatchItem
        {
            public SiemensS7Address Address;
            public int ByteCount;
            public S7PlusDataType Type;
            public int OriginalIndex;
        }

        private BatchItem ResolveBatchItem(string address)
        {
            var addr = ParseS7Address(address);
            int byteCount;
            S7PlusDataType type;
            switch (addr.DataSize)
            {
                case 1:
                    byteCount = 1;
                    type = S7PlusDataType.Byte;
                    break;
                case 4:
                    byteCount = 4;
                    type = S7PlusDataType.DInt;
                    break;
                case 8:
                    byteCount = 8;
                    type = S7PlusDataType.DInt;
                    break;
                default:
                    byteCount = 2;
                    type = S7PlusDataType.Word;
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
            if (maxItemsPerRequest > 19) maxItemsPerRequest = 19;

            byte[][] results = new byte[itemList.Count][];
            int offset = 0;

            while (offset < itemList.Count)
            {
                int chunkSize = Math.Min(maxItemsPerRequest, itemList.Count - offset);
                var chunk = itemList.GetRange(offset, chunkSize);

                byte[] param = new byte[2 + chunkSize * 12];
                param[0] = FuncReadVar;
                param[1] = (byte)chunkSize;
                for (int i = 0; i < chunkSize; i++)
                {
                    var item = chunk[i];
                    int addrBits = item.Address.ByteAddress * 8 + item.Address.BitOffset;
                    byte wordLenByte = (byte)(item.Type == S7PlusDataType.Bit ? 0x01 : 0x02);
                    byte[] itemBytes = BuildS7PlusAddressItem(FuncReadVar, wordLenByte, item.ByteCount,
                        item.Address.Area, item.Address.DBNumber, (ushort)addrBits);
                    Buffer.BlockCopy(itemBytes, 2, param, 2 + i * 12, 12);
                }

                var req = BuildS7PlusRequest(FuncReadVar, param, null);
                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return OperateResult<byte[][]>.Failed(resp.Message, resp.ErrorCode);

                byte[] raw = resp.Content;
                int dataOffset = 19 + 2;
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
                param[0] = FuncWriteVar;
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
                    byte[] itemBytes = BuildS7PlusAddressItem(FuncWriteVar, 0x02, item.Data.Length,
                        item.Address.Area, item.Address.DBNumber, (ushort)addrBits);
                    Buffer.BlockCopy(itemBytes, 2, param, 2 + i * 12, 12);

                    data[dataPos] = 0x00;
                    data[dataPos + 1] = 0x02;
                    data[dataPos + 2] = (byte)(item.Data.Length >> 8);
                    data[dataPos + 3] = (byte)(item.Data.Length & 0xFF);
                    Buffer.BlockCopy(item.Data, 0, data, dataPos + 4, item.Data.Length);
                    dataPos += 4 + item.Data.Length;
                }

                var req = BuildS7PlusRequest(FuncWriteVar, param, data);
                var resp = SendAndReceive(req);
                if (!resp.IsSuccess) return resp;

                if (resp.Content.Length > 19 + 2 + chunkSize)
                {
                    for (int i = 0; i < chunkSize; i++)
                    {
                        int retOffset = 21 + i;
                        if (retOffset < resp.Content.Length)
                        {
                            byte retCode = resp.Content[retOffset];
                            if (retCode != 0xFF)
                                return OperateResult.Failed($"S7 Plus 批量写入错误: 项目{i} 返回码=0x{retCode:X2}", retCode);
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

        // ── 辅助方法 ──────────────────────────────

        private static string AreaPrefix(S7Area area)
        {
            switch (area)
            {
                case S7Area.PE: return "I";
                case S7Area.PA: return "Q";
                case S7Area.MK: return "M";
                case S7Area.DB: return "DB";
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
            try { return BuildS7PlusSetupCommunication(); }
            catch { return null; }
        }
    }
}
