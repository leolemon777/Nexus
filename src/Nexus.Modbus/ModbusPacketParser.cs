using System;
using System.Text;
using Nexus;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus offline frame parser for diagnostics and packet capture tooling.
    /// Supports FC01-06, 08, 15, 16, 22, 23, 43/14.
    /// </summary>
    public static class ModbusPacketParser
    {
        public static ModbusPacketInfo ParseTcp(byte[] frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            return ParseMbap(frame, ModbusPacketTransport.Tcp, "Modbus TCP", direction);
        }

        public static ModbusPacketInfo ParseUdp(byte[] frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            return ParseMbap(frame, ModbusPacketTransport.Udp, "Modbus UDP", direction);
        }

        private static ModbusPacketInfo ParseMbap(byte[] frame, ModbusPacketTransport transport, string transportName, ModbusPacketDirection direction)
        {
            var result = ModbusPacketInfo.Create(transport, direction, frame);
            result.ChecksumStatus = ModbusChecksumStatus.NotApplicable;

            if (frame == null || frame.Length < 8)
            {
                result.MarkInvalid(transportName + " frame is too short. Expected at least 8 bytes.");
                return result;
            }

            result.TransactionId = ReadUInt16(frame, 0);
            result.ProtocolId = ReadUInt16(frame, 2);
            ushort length = ReadUInt16(frame, 4);
            result.Length = length;
            result.UnitId = frame[6];
            result.Station = frame[6];

            if (result.ProtocolId != 0)
                result.MarkInvalid(transportName + " protocol id must be 0.");

            if (length < 2)
                result.MarkInvalid(transportName + " length field must include UnitId and at least one PDU byte.");

            int expectedTotalLength = 6 + length;
            if (expectedTotalLength != frame.Length)
                result.MarkInvalid(transportName + " length field does not match frame length.");

            int pduLength = frame.Length - 7;
            if (pduLength <= 0)
            {
                result.MarkInvalid(transportName + " frame does not contain a PDU.");
                return result;
            }

            ParsePdu(result, frame, 7, pduLength, direction);
            return result;
        }

        public static ModbusPacketInfo ParseRtu(byte[] frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            return ParseRtuCore(frame, ModbusPacketTransport.Rtu, "Modbus RTU", direction);
        }

        public static ModbusPacketInfo ParseRtuOverTcp(byte[] frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            return ParseRtuCore(frame, ModbusPacketTransport.RtuOverTcp, "Modbus RTU-over-TCP", direction);
        }

        private static ModbusPacketInfo ParseRtuCore(byte[] frame, ModbusPacketTransport transport, string transportName, ModbusPacketDirection direction)
        {
            var result = ModbusPacketInfo.Create(transport, direction, frame);

            if (frame == null || frame.Length < 5)
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid(transportName + " frame is too short. Expected station, function, payload and CRC.");
                return result;
            }

            result.Station = frame[0];
            result.UnitId = frame[0];

            int crcOffset = frame.Length - 2;
            ushort actual = (ushort)(frame[crcOffset] | (frame[crcOffset + 1] << 8));
            ushort expected = CrcCalculator.ComputeCrc16(frame, 0, crcOffset);
            result.Checksum = actual;
            result.ExpectedChecksum = expected;
            result.ChecksumStatus = actual == expected ? ModbusChecksumStatus.Valid : ModbusChecksumStatus.Invalid;

            if (result.ChecksumStatus == ModbusChecksumStatus.Invalid)
                result.MarkInvalid(transportName + " CRC check failed.");

            ParsePdu(result, frame, 1, frame.Length - 3, direction);
            return result;
        }

        public static ModbusPacketInfo ParseAscii(byte[] frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            string text = frame == null ? string.Empty : Encoding.ASCII.GetString(frame);
            return ParseAsciiCore(text, direction, frame ?? Array.Empty<byte>());
        }

        public static ModbusPacketInfo ParseAscii(string frame, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            byte[] rawFrame = frame == null ? Array.Empty<byte>() : Encoding.ASCII.GetBytes(frame);
            return ParseAsciiCore(frame ?? string.Empty, direction, rawFrame);
        }

        public static ModbusPacketInfo Parse(byte[] frame, ModbusPacketTransport transport, ModbusPacketDirection direction = ModbusPacketDirection.Unknown)
        {
            switch (transport)
            {
                case ModbusPacketTransport.Tcp:
                    return ParseTcp(frame, direction);
                case ModbusPacketTransport.Udp:
                    return ParseUdp(frame, direction);
                case ModbusPacketTransport.Rtu:
                    return ParseRtu(frame, direction);
                case ModbusPacketTransport.RtuOverTcp:
                    return ParseRtuOverTcp(frame, direction);
                case ModbusPacketTransport.Ascii:
                    return ParseAscii(frame, direction);
                default:
                    var result = ModbusPacketInfo.Create(transport, direction, frame);
                    result.MarkInvalid("Unsupported Modbus transport.");
                    return result;
            }
        }

        private static ModbusPacketInfo ParseAsciiCore(string frame, ModbusPacketDirection direction, byte[] rawFrame)
        {
            var result = ModbusPacketInfo.Create(ModbusPacketTransport.Ascii, direction, rawFrame);

            if (string.IsNullOrWhiteSpace(frame))
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid("Modbus ASCII frame is empty.");
                return result;
            }

            string trimmed = frame.Trim();
            if (trimmed.Length < 7)
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid("Modbus ASCII frame is too short.");
                return result;
            }

            if (trimmed[0] != ':')
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid("Modbus ASCII frame must start with ':'.");
                return result;
            }

            string hex = trimmed.Substring(1);
            if ((hex.Length % 2) != 0)
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid("Modbus ASCII hex payload must have an even number of characters.");
                return result;
            }

            byte[] decoded;
            string? hexError = TryDecodeHex(hex, out decoded);
            if (hexError != null)
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid(hexError);
                return result;
            }

            if (decoded.Length < 3)
            {
                result.ChecksumStatus = ModbusChecksumStatus.Missing;
                result.MarkInvalid("Modbus ASCII decoded frame is too short.");
                return result;
            }

            result.Station = decoded[0];
            result.UnitId = decoded[0];
            byte actual = decoded[decoded.Length - 1];
            byte expected = CrcCalculator.ComputeLrc(decoded, 0, decoded.Length - 1);
            result.Checksum = actual;
            result.ExpectedChecksum = expected;
            result.ChecksumStatus = actual == expected ? ModbusChecksumStatus.Valid : ModbusChecksumStatus.Invalid;

            if (result.ChecksumStatus == ModbusChecksumStatus.Invalid)
                result.MarkInvalid("Modbus ASCII LRC check failed.");

            ParsePdu(result, decoded, 1, decoded.Length - 2, direction);
            return result;
        }

        private static void ParsePdu(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection hint)
        {
            if (length <= 0)
            {
                result.MarkInvalid("Modbus PDU is empty.");
                return;
            }

            byte fc = buffer[offset];
            result.FunctionCode = fc;
            result.BaseFunctionCode = (byte)(fc & 0x7F);
            result.IsException = (fc & 0x80) != 0;

            if (result.IsException)
            {
                result.Direction = ModbusPacketDirection.Response;
                if (length < 2)
                {
                    result.MarkInvalid("Modbus exception response is missing exception code.");
                    return;
                }

                result.ExceptionCode = buffer[offset + 1];
                result.Data = CopyRange(buffer, offset + 1, length - 1);
                return;
            }

            ModbusPacketDirection actualDirection = hint == ModbusPacketDirection.Unknown
                ? InferDirection(result.BaseFunctionCode.GetValueOrDefault(), buffer, offset, length)
                : hint;
            result.Direction = actualDirection;

            switch (result.BaseFunctionCode.GetValueOrDefault())
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                    ParseReadFunction(result, buffer, offset, length, actualDirection);
                    break;
                case 0x05:
                case 0x06:
                    ParseSingleWriteFunction(result, buffer, offset, length);
                    break;
                case 0x08:
                    ParseDiagnostics(result, buffer, offset, length, actualDirection);
                    break;
                case 0x0F:
                case 0x10:
                    ParseMultipleWriteFunction(result, buffer, offset, length, actualDirection);
                    break;
                case 0x16:
                    ParseMaskWriteRegister(result, buffer, offset, length);
                    break;
                case 0x17:
                    ParseReadWriteFunction(result, buffer, offset, length, actualDirection);
                    break;
                case 0x2B:
                    ParseEncapsulatedInterface(result, buffer, offset, length, actualDirection);
                    break;
                default:
                    result.Data = CopyRange(buffer, offset + 1, length - 1);
                    break;
            }
        }

        private static void ParseReadFunction(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            if (direction == ModbusPacketDirection.Response)
            {
                if (length < 2)
                {
                    result.MarkInvalid("Modbus read response is missing byte count.");
                    return;
                }

                int byteCount = buffer[offset + 1];
                result.ByteCount = (byte)byteCount;
                if (length != byteCount + 2)
                    result.MarkInvalid("Modbus read response byte count does not match PDU length.");
                result.Data = CopyRange(buffer, offset + 2, Math.Min(byteCount, Math.Max(0, length - 2)));
                return;
            }

            if (length < 5)
            {
                result.MarkInvalid("Modbus read request is too short.");
                return;
            }

            result.Address = ReadUInt16(buffer, offset + 1);
            result.Quantity = ReadUInt16(buffer, offset + 3);
        }

        private static void ParseSingleWriteFunction(ModbusPacketInfo result, byte[] buffer, int offset, int length)
        {
            if (length < 5)
            {
                result.MarkInvalid("Modbus single write PDU is too short.");
                return;
            }

            result.Address = ReadUInt16(buffer, offset + 1);
            result.Data = CopyRange(buffer, offset + 3, 2);
        }

        // ── FC08 — Diagnostics ────────────────────

        private static void ParseDiagnostics(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            // PDU: FC(1) + SubFunction(2) + Data(2+)
            if (length < 4)
            {
                result.MarkInvalid("Modbus FC08 diagnostics PDU is too short.");
                return;
            }

            result.MeiType = 0x08; // Reuse MeiType field for FC08 sub-function identification
            result.SubFunction = ReadUInt16(buffer, offset + 1);

            if (length > 4)
                result.Data = CopyRange(buffer, offset + 3, length - 3);

            // FC08 request and response have same shape — direction is caller-provided or Unknown
            if (direction == ModbusPacketDirection.Unknown)
                result.Direction = ModbusPacketDirection.Unknown;
        }

        // ── FC43 — Encapsulated Interface Transport ──

        private static void ParseEncapsulatedInterface(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            // PDU: FC(1) + MEI Type(1) + ...
            if (length < 2)
            {
                result.MarkInvalid("Modbus FC43 encapsulated interface PDU is too short.");
                return;
            }

            byte meiType = buffer[offset + 1];
            result.MeiType = meiType;

            switch (meiType)
            {
                case 0x0E: // Read Device Identification
                    ParseReadDeviceId(result, buffer, offset, length, direction);
                    break;
                default:
                    result.Data = CopyRange(buffer, offset + 2, length - 2);
                    break;
            }
        }

        private static void ParseReadDeviceId(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            if (direction == ModbusPacketDirection.Request)
            {
                // Request: FC(1) + MEI(1) + ReadLevel(1) + ObjectId(1)
                if (length < 4)
                {
                    result.MarkInvalid("Modbus FC43/14 request PDU is too short.");
                    return;
                }
                result.ReadDeviceIdLevel = buffer[offset + 2];
                result.Data = CopyRange(buffer, offset + 3, 1); // ObjectId
                return;
            }

            // Response: FC(1) + MEI(1) + ReadLevel(1) + Conformity(1) + MoreFollows(1) + NextObjId(1) + ObjCount(1) + Objects...
            if (length < 7)
            {
                result.MarkInvalid("Modbus FC43/14 response PDU is too short.");
                return;
            }

            result.ReadDeviceIdLevel = buffer[offset + 2];
            result.ConformityLevel = buffer[offset + 3];
            result.MoreFollows = buffer[offset + 4] != 0;
            result.NextObjectId = buffer[offset + 5];
            result.ObjectCount = buffer[offset + 6];

            // Parse individual objects into Data as raw bytes after the header
            int objOffset = offset + 7;
            int remaining = length - 7;
            result.Data = CopyRange(buffer, objOffset, remaining);
        }

        private static void ParseMultipleWriteFunction(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            if (direction == ModbusPacketDirection.Response)
            {
                if (length < 5)
                {
                    result.MarkInvalid("Modbus multiple write response is too short.");
                    return;
                }

                result.Address = ReadUInt16(buffer, offset + 1);
                result.Quantity = ReadUInt16(buffer, offset + 3);
                return;
            }

            if (length < 6)
            {
                result.MarkInvalid("Modbus multiple write request is too short.");
                return;
            }

            result.Address = ReadUInt16(buffer, offset + 1);
            result.Quantity = ReadUInt16(buffer, offset + 3);
            int byteCount = buffer[offset + 5];
            result.ByteCount = (byte)byteCount;
            if (length != byteCount + 6)
                result.MarkInvalid("Modbus multiple write request byte count does not match PDU length.");
            result.Data = CopyRange(buffer, offset + 6, Math.Min(byteCount, Math.Max(0, length - 6)));
        }

        private static void ParseMaskWriteRegister(ModbusPacketInfo result, byte[] buffer, int offset, int length)
        {
            if (length < 7)
            {
                result.MarkInvalid("Modbus mask write register PDU is too short.");
                return;
            }

            result.Address = ReadUInt16(buffer, offset + 1);
            result.AndMask = ReadUInt16(buffer, offset + 3);
            result.OrMask = ReadUInt16(buffer, offset + 5);
        }

        private static void ParseReadWriteFunction(ModbusPacketInfo result, byte[] buffer, int offset, int length, ModbusPacketDirection direction)
        {
            if (direction == ModbusPacketDirection.Response)
            {
                if (length < 2)
                {
                    result.MarkInvalid("Modbus read/write response is missing byte count.");
                    return;
                }

                int byteCount = buffer[offset + 1];
                result.ByteCount = (byte)byteCount;
                if (length != byteCount + 2)
                    result.MarkInvalid("Modbus read/write response byte count does not match PDU length.");
                result.Data = CopyRange(buffer, offset + 2, Math.Min(byteCount, Math.Max(0, length - 2)));
                return;
            }

            if (length < 10)
            {
                result.MarkInvalid("Modbus read/write request is too short.");
                return;
            }

            result.Address = ReadUInt16(buffer, offset + 1);
            result.Quantity = ReadUInt16(buffer, offset + 3);
            result.WriteAddress = ReadUInt16(buffer, offset + 5);
            result.WriteQuantity = ReadUInt16(buffer, offset + 7);
            int writeByteCount = buffer[offset + 9];
            result.ByteCount = (byte)writeByteCount;
            if (length != writeByteCount + 10)
                result.MarkInvalid("Modbus read/write request byte count does not match PDU length.");
            result.Data = CopyRange(buffer, offset + 10, Math.Min(writeByteCount, Math.Max(0, length - 10)));
        }

        private static ModbusPacketDirection InferDirection(byte functionCode, byte[] buffer, int offset, int length)
        {
            switch (functionCode)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                    if (length >= 2 && buffer[offset + 1] == length - 2)
                        return ModbusPacketDirection.Response;
                    if (length == 5)
                        return ModbusPacketDirection.Request;
                    return ModbusPacketDirection.Unknown;
                case 0x05:
                case 0x06:
                case 0x16:
                    return ModbusPacketDirection.Unknown;
                case 0x08:
                    // FC08 request and response have same shape
                    return ModbusPacketDirection.Unknown;
                case 0x0F:
                case 0x10:
                    if (length >= 6 && buffer[offset + 5] == length - 6)
                        return ModbusPacketDirection.Request;
                    if (length == 5)
                        return ModbusPacketDirection.Response;
                    return ModbusPacketDirection.Unknown;
                case 0x17:
                    if (length >= 10 && buffer[offset + 9] == length - 10)
                        return ModbusPacketDirection.Request;
                    if (length >= 2 && buffer[offset + 1] == length - 2)
                        return ModbusPacketDirection.Response;
                    return ModbusPacketDirection.Unknown;
                case 0x2B:
                    // FC43: if MEI=0x0E, response has more fields than request
                    if (length >= 2 && buffer[offset + 1] == 0x0E)
                    {
                        if (length <= 4) return ModbusPacketDirection.Request;
                        if (length >= 7) return ModbusPacketDirection.Response;
                    }
                    return ModbusPacketDirection.Unknown;
                default:
                    return ModbusPacketDirection.Unknown;
            }
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
        }

        private static byte[] CopyRange(byte[] buffer, int offset, int length)
        {
            if (buffer == null || length <= 0 || offset >= buffer.Length)
                return Array.Empty<byte>();

            int safeLength = Math.Min(length, buffer.Length - offset);
            byte[] copy = new byte[safeLength];
            Buffer.BlockCopy(buffer, offset, copy, 0, safeLength);
            return copy;
        }

        private static string? TryDecodeHex(string hex, out byte[] decoded)
        {
            decoded = Array.Empty<byte>();
            byte[] bytes = new byte[hex.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                int high = HexValue(hex[i * 2]);
                int low = HexValue(hex[i * 2 + 1]);
                if (high < 0 || low < 0)
                    return "Modbus ASCII frame contains non-hex characters.";

                bytes[i] = (byte)((high << 4) | low);
            }

            decoded = bytes;
            return null;
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return -1;
        }
    }

    public enum ModbusPacketTransport
    {
        Tcp,
        Udp,
        Rtu,
        Ascii,
        RtuOverTcp
    }

    public enum ModbusPacketDirection
    {
        Unknown,
        Request,
        Response
    }

    public enum ModbusChecksumStatus
    {
        NotApplicable,
        Missing,
        Valid,
        Invalid
    }

    public sealed class ModbusPacketInfo
    {
        private ModbusPacketInfo(ModbusPacketTransport transport, ModbusPacketDirection direction, byte[] rawFrame)
        {
            Transport = transport;
            Direction = direction;
            RawFrame = rawFrame;
            Data = Array.Empty<byte>();
            IsValid = true;
            ChecksumStatus = ModbusChecksumStatus.NotApplicable;
        }

        public ModbusPacketTransport Transport { get; private set; }
        public ModbusPacketDirection Direction { get; internal set; }
        public ushort? TransactionId { get; internal set; }
        public ushort? ProtocolId { get; internal set; }
        public ushort? Length { get; internal set; }
        public byte? UnitId { get; internal set; }
        public byte? Station { get; internal set; }
        public byte? FunctionCode { get; internal set; }
        public byte? BaseFunctionCode { get; internal set; }
        public bool IsException { get; internal set; }
        public byte? ExceptionCode { get; internal set; }
        public ushort? Address { get; internal set; }
        public ushort? Quantity { get; internal set; }
        public ushort? WriteAddress { get; internal set; }
        public ushort? WriteQuantity { get; internal set; }
        public ushort? AndMask { get; internal set; }
        public ushort? OrMask { get; internal set; }
        public byte? ByteCount { get; internal set; }
        public byte? MeiType { get; internal set; }
        public ushort? SubFunction { get; internal set; }
        public byte? ReadDeviceIdLevel { get; internal set; }
        public byte? ConformityLevel { get; internal set; }
        public bool MoreFollows { get; internal set; }
        public byte? NextObjectId { get; internal set; }
        public byte? ObjectCount { get; internal set; }
        public byte[] Data { get; internal set; }
        public ushort? Checksum { get; internal set; }
        public ushort? ExpectedChecksum { get; internal set; }
        public ModbusChecksumStatus ChecksumStatus { get; internal set; }
        public byte[] RawFrame { get; private set; }
        public string? Error { get; private set; }
        public bool IsValid { get; private set; }

        internal static ModbusPacketInfo Create(ModbusPacketTransport transport, ModbusPacketDirection direction, byte[] frame)
        {
            return new ModbusPacketInfo(transport, direction, Clone(frame));
        }

        internal void MarkInvalid(string error)
        {
            IsValid = false;
            Error = Error == null ? error : Error + " " + error;
        }

        private static byte[] Clone(byte[] frame)
        {
            if (frame == null || frame.Length == 0)
                return Array.Empty<byte>();

            byte[] copy = new byte[frame.Length];
            Buffer.BlockCopy(frame, 0, copy, 0, frame.Length);
            return copy;
        }
    }
}
