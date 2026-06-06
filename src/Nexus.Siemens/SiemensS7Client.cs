using System;

namespace Nexus.Siemens
{
    /// <summary>
    /// 西门子 S7 协议客户端 — 支持 S7-200/200Smart/300/400/1200/1500。
    /// 基于 TPKT + COTP + S7 Communication 三层协议栈。
    /// </summary>
    public class SiemensS7Client : TcpDeviceBase
    {
        public SiemensPLCS PLCType { get; }
        public byte Rack { get; set; } = 0;
        public byte Slot { get; set; } = 0;
        public ushort MaxPduSize { get; private set; } = 240;

        public SiemensS7Client(SiemensPLCS plcType, string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout)
        {
            PLCType = plcType;
            // S7-1200/1500 默认 Slot=1; S7-300/400 默认 Rack=0, Slot=2
            if (plcType == SiemensPLCS.S7_1200 || plcType == SiemensPLCS.S7_1500)
                Slot = 1;
            else if (plcType == SiemensPLCS.S7_300 || plcType == SiemensPLCS.S7_400)
                Slot = 2;
        }

        // ── S7 协议层 ──────────────────────────────
        // Layer 1: TPKT Header (4 bytes): Version(1) + Reserved(1) + Length(2)
        // Layer 2: COTP (DT-Data or CR-Connection Request)
        // Layer 3: S7 Communication (Header + Parameters + Data)

        protected override int ResponseHeaderLength => _isFirstPacket ? 4 : 7;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (_isFirstPacket)
            {
                // TPKT: total length at offset 2-3, minus 4 for TPKT header
                _isFirstPacket = false;
                return ((header[2] << 8) | header[3]) - 4;
            }
            // S7 response: function code at [7], data length from PDU header
            return 0; // 简化：直接通过 TPKT 层读取完整帧
        }

        // 改用基于 TPKT 的完整帧读取，不依赖分包
        private bool _isFirstPacket = true;

        /// <summary>
        /// 重写收发方法：TPKT 完整帧读取。
        /// TPKT Header (4 bytes): 0x03, 0x00, LengthHi, LengthLo (total frame length including header)
        /// </summary>
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

                System.Net.Sockets.NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                // 读取 TPKT Header (4 bytes)
                byte[]? tpktHeader = ReadExactNs(ns, 4);
                if (tpktHeader == null) return OperateResult<byte[]>.Failed("读取TPKT头失败");

                int totalLen = (tpktHeader[2] << 8) | tpktHeader[3];
                int payloadLen = totalLen - 4;
                if (payloadLen < 0 || payloadLen > 65535) return OperateResult<byte[]>.Failed("TPKT长度异常");

                byte[] payload = payloadLen > 0 ? ReadExactNs(ns, payloadLen) ?? new byte[0] : new byte[0];

                byte[] full = new byte[totalLen];
                Buffer.BlockCopy(tpktHeader, 0, full, 0, 4);
                if (payload.Length > 0) Buffer.BlockCopy(payload, 0, full, 4, payload.Length);

                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                RaiseMessageReceived(DataConverter.ToHexString(full));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(full);
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

        // ── TPKT + COTP + S7 报文构建 ─────────────

        private byte[] BuildTPKT(byte[] payload)
        {
            int total = 4 + payload.Length;
            return new byte[] { 0x03, 0x00, (byte)(total >> 8), (byte)total };
        }

        /// <summary>构建 COTP 连接请求。</summary>
        private byte[] BuildCOTPConnectionRequest()
        {
            byte[] tpkt = BuildTPKT(new byte[] {
                // COTP CR: Length(1) + PDU Type CR(1) + DestRef(2) + SrcRef(2) + Flags(1) + TPDU Size(4)
                0x11,       // Length (17 bytes following)
                0xE0,       // PDU Type: Connection Request
                0x00, 0x00, // Dest Reference
                0x00, 0x01, // Src Reference
                0x00,       // Flags
                // Source TSAP (parameters)
                0xC0, 0x01, 0x0A, // Param: Source TSAP, length 2
                0x01, 0x00,       // Source TSAP value (will be overridden below)
                // Destination TSAP
                0xC1, 0x01, 0x0A, // Param: Dest TSAP, length 2
                0x01, 0x02,       // Dest TSAP value (will be overridden below)
                // TPDU Size
                0xC0, 0x01, 0x09, // Param: TPDU Size, length 1
                0x01               // TPDU Size: 1024
            });

            // 计算 TSAP
            byte[] tsapBytes = BuildTSAP();
            // Override TSAP bytes in the CR
            byte[] cr = tpkt;
            cr[11] = tsapBytes[0]; cr[12] = tsapBytes[1]; // Source TSAP
            cr[16] = tsapBytes[2]; cr[17] = tsapBytes[3]; // Dest TSAP
            return cr;
        }

        private byte[] BuildTSAP()
        {
            // Source TSAP: 0x0100 + Rack*0x20 + Slot
            // Dest TSAP: 0x0102
            byte srcHi = 0x01;
            byte srcLo = (byte)(Rack * 0x20 + Slot);
            byte dstHi = 0x01;
            byte dstLo = (byte)(PLCType == SiemensPLCS.S7_200 || PLCType == SiemensPLCS.S7_200Smart ? (byte)0x00 : (byte)0x02);
            return new byte[] { srcHi, srcLo, dstHi, dstLo };
        }

        /// <summary>构建 COTP Data Transfer + S7 报文。</summary>
        private byte[] BuildCOTPDataRequest(byte[] s7Pdu)
        {
            byte[] cotpData = new byte[3 + s7Pdu.Length];
            cotpData[0] = 0x02;   // COTP DT Header Length
            cotpData[1] = 0xF0;   // PDU Type: Data Transfer
            cotpData[2] = 0x80;   // Last Data Unit + TPU number
            Buffer.BlockCopy(s7Pdu, 0, cotpData, 3, s7Pdu.Length);

            byte[] tpkt = BuildTPKT(cotpData);
            byte[] result = new byte[tpkt.Length + cotpData.Length];
            Buffer.BlockCopy(tpkt, 0, result, 0, 4);
            Buffer.BlockCopy(cotpData, 0, result, 4, cotpData.Length);
            return result;
        }

        /// <summary>构建 S7 Communication Setup 请求。</summary>
        private byte[] BuildS7SetupCommunication()
        {
            return BuildCOTPDataRequest(new byte[] {
                // S7 Header (10 bytes)
                0x32,       // Protocol ID
                0x01,       // Job Type
                0x00, 0x00, // Reserved
                0x00, 0x00, // PDU Reference
                0x00, 0x08, // Parameter Length
                0x00, 0x00, // Data Length
                // S7 Parameter (8 bytes)
                0xF0,       // Function: Setup Communication
                0x00,       // Reserved
                0x00, 0x01, // Max AMQ Calling
                0x00, 0x01, // Max AMQ Receiving
                0x03, (byte)(MaxPduSize >> 8), (byte)MaxPduSize // PDU Size (big endian)
            });
        }

        /// <summary>
        /// 构建 S7 Read/Write Var 请求的地址项（13 字节）。
        /// Read 与 Write 路径的 Param 布局完全相同，只有 function(0x04/0x05)、wordLen、Length 字段不同。
        /// </summary>
        private static byte[] BuildS7AddressItem(
            byte function, byte wordLen, int lengthOrBitCount,
            S7Area area, int dbNumber, ushort byteAddress)
        {
            return new byte[]
            {
                function, 0x01,                              // Function, item count = 1
                0x12, 0x0A, 0x10,                            // Variable spec (S7 Any)
                wordLen,                                       // Transport size
                (byte)((lengthOrBitCount >> 8) & 0xFF),       // Length high
                (byte)(lengthOrBitCount & 0xFF),              // Length low
                (byte)area,                                   // Area code
                (byte)((dbNumber >> 8) & 0xFF),               // DB number high
                (byte)(dbNumber & 0xFF),                      // DB number low
                (byte)((byteAddress >> 8) & 0xFF),            // Byte address high
                (byte)(byteAddress & 0xFF)                    // Byte address low
            };
        }

        private byte[] BuildS7Header(byte function, byte[]? param, byte[]? data)
        {
            int paramLen = param?.Length ?? 0;
            int dataLen = data?.Length ?? 0;
            byte[] header = new byte[10 + paramLen + dataLen];

            header[0] = 0x32;   // Protocol ID
            header[1] = 0x01;   // Job Type
            header[2] = 0x00; header[3] = 0x00; // Reserved
            header[4] = 0x00; header[5] = 0x01; // PDU Reference
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

            // Step 1: COTP Connection Request
            var crReq = BuildCOTPConnectionRequest();
            var crResp = SendAndReceive(crReq);
            if (!crResp.IsSuccess) return crResp;

            // Step 2: S7 Setup Communication
            var setupReq = BuildS7SetupCommunication();
            var setupResp = SendAndReceive(setupReq);
            if (!setupResp.IsSuccess) return setupResp;

            // Parse MaxPduSize from response
            if (setupResp.Content.Length > 27)
            {
                MaxPduSize = (ushort)((setupResp.Content[26] << 8) | setupResp.Content[27]);
                if (MaxPduSize < 16) MaxPduSize = 240;
            }

            return OperateResult.Success();
        }

        // ── 地址解析 ──────────────────────────────

        private struct S7Address
        {
            public S7Area Area;
            public int DBNumber;
            public int ByteAddress;
            public int BitOffset;
        }

        private static S7Address ParseS7Address(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("地址不能为空");
            address = address.ToUpper().Trim();

            // DB block: DB1.DBW100, DB1.DBD0, DB1.DBX0.0
            if (address.StartsWith("DB"))
            {
                int dotIdx = address.IndexOf('.');
                if (dotIdx < 0) throw new ArgumentException($"无效DB地址: {address}");
                int dbNum = int.Parse(address.Substring(2, dotIdx - 2));
                string subAddr = address.Substring(dotIdx + 1);
                return ParseSubAddress(subAddr, S7Area.DB, dbNum);
            }

            // I area
            if (address.StartsWith("I") || address.StartsWith("EB"))
                return ParseSubAddress(address.TrimStart('I', 'E', 'B'), S7Area.PE, 0);

            // Q area
            if (address.StartsWith("Q") || address.StartsWith("AB"))
                return ParseSubAddrQ(address);

            // M area
            if (address.StartsWith("M") || address.StartsWith("MB"))
                return ParseSubAddress(address.TrimStart('M', 'B'), S7Area.MK, 0);

            // V area (S7-200)
            if (address.StartsWith("V"))
                return ParseSubAddress(address.Substring(1), S7Area.DB, 1);

            throw new ArgumentException($"不支持地址格式: {address}");
        }

        private static S7Address ParseSubAddrQ(string address)
        {
            string trimmed = address.TrimStart('Q', 'A', 'B');
            return ParseSubAddress(trimmed, S7Area.PA, 0);
        }

        private static S7Address ParseSubAddress(string sub, S7Area area, int db)
        {
            int bitOffset = 0;

            // X type with bit: M100.3
            if (sub.Contains("."))
            {
                var parts = sub.Split('.');
                int byteAddr = int.Parse(parts[0].TrimStart('X'));
                bitOffset = int.Parse(parts[1]);
                return new S7Address { Area = area, DBNumber = db, ByteAddress = byteAddr, BitOffset = bitOffset };
            }

            // Type prefix: W=word(2), D=dword(4), B=byte(1)
            if (sub.StartsWith("W"))
                return new S7Address { Area = area, DBNumber = db, ByteAddress = int.Parse(sub.Substring(1)), BitOffset = 0 };
            if (sub.StartsWith("D"))
                return new S7Address { Area = area, DBNumber = db, ByteAddress = int.Parse(sub.Substring(1)), BitOffset = 0 };
            if (sub.StartsWith("B"))
                return new S7Address { Area = area, DBNumber = db, ByteAddress = int.Parse(sub.Substring(1)), BitOffset = 0 };

            // Plain number = word address
            return new S7Address { Area = area, DBNumber = db, ByteAddress = int.Parse(sub), BitOffset = 0 };
        }

        // ── S7 数据类型 ────────────────────────────

        private enum S7DataType { Bit, Byte, Word, Int, DInt, Real, String }

        // ── 读取实现 ──────────────────────────────

        private OperateResult<byte[]> ReadS7Raw(string address, int byteCount, S7DataType type)
        {
            var s7Addr = ParseS7Address(address);
            int bitCount = type == S7DataType.Bit ? 1 : byteCount * 8;
            int wordLen = type == S7DataType.Bit ? 1 : byteCount / 2;
            if (wordLen < 1) wordLen = 1;

            int addrBits = s7Addr.ByteAddress * 8 + s7Addr.BitOffset;
            byte wordLenByte = (byte)(type == S7DataType.Bit ? 0x01 : 0x02);

            byte[] param = BuildS7AddressItem(0x04, wordLenByte, byteCount,
                s7Addr.Area, s7Addr.DBNumber, (ushort)addrBits);

            var req = BuildCOTPDataRequest(BuildS7Header(0x04, param, null));
            var resp = SendAndReceive(req);
            if (!resp.IsSuccess) return resp;

            // Parse S7 response: skip TPKT(4) + COTP(3) + S7 header(12) + return code(1) + transport size(1) + len(2) = 22 bytes offset
            byte[] raw = resp.Content;
            if (raw.Length < 22) return OperateResult<byte[]>.Failed("S7响应长度不足");

            // Check S7 error
            byte errorCode = raw[21]; // Return code in data item
            if (errorCode != 0xFF)
                return OperateResult<byte[]>.Failed($"S7读取错误: 返回码=0x{errorCode:X2}", errorCode);

            int dataLen = (raw[20] << 8) | raw[19]; // Sometimes reversed
            if (dataLen <= 0 && raw.Length > 22) dataLen = raw.Length - 22;

            byte[] data = new byte[Math.Min(byteCount, raw.Length - 22)];
            if (data.Length > 0)
                Buffer.BlockCopy(raw, 22, data, 0, data.Length);

            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadS7Raw(address, 1, S7DataType.Bit);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[0] & (1 << ParseS7Address(address).BitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadS7Raw(address, 2, S7DataType.Int);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadS7Raw(address, 2, S7DataType.Word);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadS7Raw(address, 4, S7DataType.DInt);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
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
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
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
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadS7Raw(address, 8, S7DataType.Real);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadS7Raw(address, length, S7DataType.String);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
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

            // S7 Data Item: 4 字节 return code / transport size / length + 实际数据
            byte[] s7Data = new byte[4 + data.Length];
            s7Data[0] = 0x00;    // Return code
            s7Data[1] = wordLen;  // Transport size
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

        public override OperateResult Write(string address, bool value)
        {
            return WriteS7Raw(address, new byte[] { (byte)(value ? 1 : 0) }, S7DataType.Bit);
        }

        public override OperateResult Write(string address, short value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.Int);
        }

        public override OperateResult Write(string address, ushort value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.Word);
        }

        public override OperateResult Write(string address, int value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.DInt);
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);

        public override OperateResult Write(string address, float value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.Real);
        }

        public override OperateResult Write(string address, double value) => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
        {
            return WriteS7Raw(address, DataConverter.GetBytes(value), S7DataType.String);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            return WriteS7Raw(address, data, S7DataType.Byte);
        }
    }
}
