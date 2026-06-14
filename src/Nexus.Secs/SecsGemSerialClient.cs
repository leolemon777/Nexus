using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Secs
{
    /// <summary>
    /// SECS-I（串口）协议客户端 — SEMI E4 标准。
    /// <para>通过串口进行 SECS 消息传输，使用 EOT/ENQ 握手和块传输机制。</para>
    /// <para>帧格式: EOT + Header(10) + Data + CheckSum(2) + ETX</para>
    /// <para>块传输: 多块消息使用块序号和结束标志。</para>
    /// </summary>
    public class SecsGemSerialClient : SerialDeviceBase
    {
        // ── SECS-I 控制字符 ───────────────────────
        private const byte ENQ = 0x05;
        private const byte EOT = 0x04;
        private const byte ACK = 0x06;
        private const byte NAK = 0x15;
        private const byte ETX = 0x03;

        private const int HEADER_LENGTH = 10;
        private const int MAX_BLOCK_SIZE = 244;

        /// <summary>设备 ID（2字节，默认 0）。</summary>
        public ushort DeviceId { get; set; } = 0;

        /// <summary>块大小（默认 244 字节，SECS-I 标准最大值）。</summary>
        public int BlockSize { get; set; } = MAX_BLOCK_SIZE;

        private uint _systemBytesCounter;
        private static readonly object _counterLock = new object();

        public SecsGemSerialClient(ISerialPort serialPort, int timeout = 10000)
            : base(serialPort, timeout) { }

        // ── SerialDeviceBase 抽象实现 ─────────────

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        // ═══════════════════════════════════════════
        //  SECS-I 消息收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 SECS 消息（Primary Message）并等待回复。
        /// </summary>
        /// <param name="stream">SxFn 中的 S（流号，0-127）。</param>
        /// <param name="function">SxFn 中的 F（功能号，1-255）。</param>
        /// <param name="data">SECS II 数据项（已编码）。</param>
        /// <returns>回复消息数据（Reply Message）。</returns>
        public OperateResult<SecsMessage> SendPrimaryMessage(byte stream, byte function, byte[]? data)
        {
            if (function == 0) return OperateResult<SecsMessage>.Failed("功能号不能为 0");

            uint sysBytes = NextSystemBytes();
            bool waitForReply = (function % 2) == 1;

            byte[] header = new byte[HEADER_LENGTH];
            header[0] = (byte)((DeviceId >> 8) & 0xFF);
            header[1] = (byte)(DeviceId & 0xFF);
            header[2] = 0x00;
            header[3] = (byte)((stream << 1) | (waitForReply ? 1 : 0));
            header[4] = function;
            header[5] = (byte)((sysBytes >> 24) & 0xFF);
            header[6] = (byte)((sysBytes >> 16) & 0xFF);
            header[7] = (byte)((sysBytes >> 8) & 0xFF);
            header[8] = (byte)(sysBytes & 0xFF);
            header[9] = 0x00;

            var sendResult = SendSecsBlocks(header, data);
            if (!sendResult.IsSuccess) return OperateResult<SecsMessage>.Failed(sendResult.Message);

            if (!waitForReply)
                return OperateResult<SecsMessage>.Success(new SecsMessage
                {
                    DeviceId = DeviceId,
                    Stream = stream,
                    Function = function,
                    SystemBytes = sysBytes
                });

            var recvResult = ReceiveSecsBlocks();
            if (!recvResult.IsSuccess) return OperateResult<SecsMessage>.Failed(recvResult.Message);

            return ParseSecsMessage(recvResult.Content);
        }

        // ═══════════════════════════════════════════
        //  SECS-I 常用消息
        // ═══════════════════════════════════════════

        /// <summary>S1F1 — Are You There。</summary>
        public OperateResult<SecsMessage> AreYouThere()
            => SendPrimaryMessage(1, 1, null);

        /// <summary>S1F13 — Establish Communication Request。</summary>
        public OperateResult<SecsMessage> EstablishCommunication()
            => SendPrimaryMessage(1, 13, null);

        /// <summary>S1F17 — Online Request。</summary>
        public OperateResult<SecsMessage> OnlineRequest()
            => SendPrimaryMessage(1, 17, null);

        /// <summary>S2F41 — Host Command Send。</summary>
        public OperateResult<SecsMessage> HostCommandSend(byte[] commandData)
            => SendPrimaryMessage(2, 41, commandData);

        // ═══════════════════════════════════════════
        //  SECS-I 块传输（EOT/ENQ 握手）
        // ═══════════════════════════════════════════

        private OperateResult SendSecsBlocks(byte[] header, byte[]? data)
        {
            lock (_lock)
            {
                try
                {
                    byte[] message = data != null && data.Length > 0
                        ? CombineArrays(header, data)
                        : header;

                    int totalBlocks = (message.Length + BlockSize - 1) / BlockSize;

                    for (int blockNum = 1; blockNum <= totalBlocks; blockNum++)
                    {
                        bool isLastBlock = blockNum == totalBlocks;
                        int offset = (blockNum - 1) * BlockSize;
                        int blockLen = Math.Min(BlockSize, message.Length - offset);

                        byte[] blockData = new byte[blockLen];
                        Array.Copy(message, offset, blockData, 0, blockLen);

                        // Block header: DeviceId(2) + BlockNum(2) + EndBit(1)
                        byte[] blockHeader = new byte[5];
                        blockHeader[0] = (byte)((DeviceId >> 8) & 0xFF);
                        blockHeader[1] = (byte)(DeviceId & 0xFF);
                        blockHeader[2] = (byte)((blockNum >> 8) & 0xFF);
                        blockHeader[3] = (byte)(blockNum & 0xFF);
                        blockHeader[4] = isLastBlock ? (byte)0x80 : (byte)0x00;

                        byte[] block = CombineArrays(blockHeader, blockData);

                        // Calculate checksum (XOR of all bytes in block)
                        byte checksum = 0;
                        foreach (byte b in block) checksum ^= b;

                        // Send EOT to indicate ready to send
                        Port.Write(new byte[] { EOT }, 0, 1);
                        RaiseMessageSent("EOT");

                        // Wait for ENQ from receiver (indicating ready to receive)
                        int response = ReadByte(Timeout);
                        if (response != ENQ)
                            return OperateResult.Failed($"SECS-I 发送等待 ENQ 超时或收到 0x{response:X2}");

                        RaiseMessageReceived("ENQ");

                        // Send block + checksum + ETX
                        byte[] frame = new byte[block.Length + 2];
                        Array.Copy(block, 0, frame, 0, block.Length);
                        frame[block.Length] = checksum;
                        frame[block.Length + 1] = ETX;

                        Port.Write(frame, 0, frame.Length);
                        RaiseMessageSent($"Block {blockNum}/{totalBlocks} [{blockLen} bytes]");

                        // Wait for ACK
                        response = ReadByte(Timeout);
                        if (response != ACK)
                            return OperateResult.Failed($"SECS-I 发送等待 ACK 失败: 0x{response:X2}");

                        RaiseMessageReceived("ACK");
                    }

                    return OperateResult.Success();
                }
                catch (Exception ex)
                {
                    RaiseError($"SECS-I 发送异常: {ex.Message}");
                    return OperateResult.Failed($"SECS-I 发送异常: {ex.Message}");
                }
            }
        }

        private OperateResult<byte[]> ReceiveSecsBlocks()
        {
            lock (_lock)
            {
                try
                {
                    using (var ms = new MemoryStream())
                    {
                        while (true)
                        {
                            // Send ENQ to indicate ready to receive
                            Port.Write(new byte[] { ENQ }, 0, 1);
                            RaiseMessageSent("ENQ");

                            // Wait for EOT or data
                            int firstByte = ReadByte(Timeout);
                            if (firstByte < 0)
                                return OperateResult<byte[]>.Failed("SECS-I 接收超时");

                            if (firstByte == EOT)
                            {
                                // EOT means multi-block, sender will send data
                                // Send ENQ to request the block
                                Port.Write(new byte[] { ENQ }, 0, 1);
                                RaiseMessageSent("ENQ (request block)");

                                firstByte = ReadByte(Timeout);
                                if (firstByte < 0)
                                    return OperateResult<byte[]>.Failed("SECS-I 接收块超时");
                            }

                            // Read block: 5-byte header + data + checksum + ETX
                            byte[] blockHeader = new byte[5];
                            blockHeader[0] = (byte)firstByte;
                            for (int i = 1; i < 5; i++)
                            {
                                int b = ReadByte(Timeout);
                                if (b < 0) return OperateResult<byte[]>.Failed("SECS-I 接收头超时");
                                blockHeader[i] = (byte)b;
                            }

                            int blockNum = (blockHeader[2] << 8) | blockHeader[3];
                            bool isLastBlock = (blockHeader[4] & 0x80) != 0;

                            // Read until ETX
                            using (var blockData = new MemoryStream())
                            {
                                while (true)
                                {
                                    int b = ReadByte(Timeout);
                                    if (b < 0) return OperateResult<byte[]>.Failed("SECS-I 接收数据超时");
                                    if (b == ETX) break;
                                    blockData.WriteByte((byte)b);
                                }

                                // Read checksum
                                int checksumByte = ReadByte(Timeout);
                                if (checksumByte < 0) return OperateResult<byte[]>.Failed("SECS-I 接收校验超时");

                                // Verify checksum
                                byte computedChecksum = 0;
                                foreach (byte bh in blockHeader) computedChecksum ^= bh;
                                foreach (byte bd in blockData.ToArray()) computedChecksum ^= bd;

                                if ((byte)checksumByte != computedChecksum)
                                {
                                    // Send NAK
                                    Port.Write(new byte[] { NAK }, 0, 1);
                                    RaiseMessageSent("NAK (checksum error)");
                                    return OperateResult<byte[]>.Failed($"SECS-I 校验和错误: 期望 0x{computedChecksum:X2}, 收到 0x{checksumByte:X2}");
                                }

                                // Send ACK
                                Port.Write(new byte[] { ACK }, 0, 1);
                                RaiseMessageReceived($"Block {blockNum} [{blockData.Length} bytes]");

                                // Skip block header (first 5 bytes were already parsed)
                                byte[] blockPayload = blockData.ToArray();
                                ms.Write(blockPayload, 0, blockPayload.Length);
                            }

                            if (isLastBlock)
                                break;
                        }

                        return OperateResult<byte[]>.Success(ms.ToArray());
                    }
                }
                catch (Exception ex)
                {
                    RaiseError($"SECS-I 接收异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"SECS-I 接收异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  SECS 消息解析
        // ═══════════════════════════════════════════

        private static OperateResult<SecsMessage> ParseSecsMessage(byte[] raw)
        {
            if (raw == null || raw.Length < HEADER_LENGTH)
                return OperateResult<SecsMessage>.Failed($"SECS-I 响应数据过短 ({raw?.Length ?? 0})");

            byte[] header = new byte[HEADER_LENGTH];
            Array.Copy(raw, 0, header, 0, HEADER_LENGTH);

            var msg = new SecsMessage
            {
                DeviceId = (ushort)((header[0] << 8) | header[1]),
                Stream = (byte)(header[3] >> 1),
                ReplyExpected = (header[3] & 0x01) != 0,
                Function = header[4],
                SystemBytes = (uint)((header[5] << 24) | (header[6] << 16) | (header[7] << 8) | header[8])
            };

            int dataLen = raw.Length - HEADER_LENGTH;
            if (dataLen > 0)
            {
                msg.Data = new byte[dataLen];
                Array.Copy(raw, HEADER_LENGTH, msg.Data, 0, dataLen);
            }

            return OperateResult<SecsMessage>.Success(msg);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content.Length > 0 && r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<uint>.Failed("响应数据不足 4 字节");
            return OperateResult<uint>.Success(
                (uint)((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("响应数据不足 8 字节");
            long val = 0;
            for (int i = 0; i < 8; i++) val = (val << 8) | r.Content[i];
            return OperateResult<long>.Success(val);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            if (r.Content.Length < 8) return OperateResult<ulong>.Failed("响应数据不足 8 字节");
            ulong val = 0;
            for (int i = 0; i < 8; i++) val = (val << 8) | r.Content[i];
            return OperateResult<ulong>.Success(val);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("响应数据不足 4 字节");
            int bits = (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3];
            return OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(bits), 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success((double)r.Content);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, 0);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(DataConverter.ToHexString(r.Content));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var dataId = ParseDataId(address);
            var msg = SendPrimaryMessage(dataId.Stream, dataId.Function, dataId.Data);
            if (!msg.IsSuccess) return OperateResult<byte[]>.Failed(msg.Message);
            return OperateResult<byte[]>.Success(msg.Content.Data ?? new byte[0]);
        }

        public override OperateResult Write(string address, bool value)
            => Write(address, new byte[] { (byte)(value ? 1 : 0) });

        public override OperateResult Write(string address, short value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ushort value)
            => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, int value)
            => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value)
            => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, long value)
        {
            byte[] data = new byte[8];
            for (int i = 7; i >= 0; i--) { data[i] = (byte)(value & 0xFF); value >>= 8; }
            return Write(address, data);
        }

        public override OperateResult Write(string address, ulong value)
        {
            byte[] data = new byte[8];
            for (int i = 7; i >= 0; i--) { data[i] = (byte)(value & 0xFF); value >>= 8; }
            return Write(address, data);
        }

        public override OperateResult Write(string address, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return Write(address, bytes);
        }

        public override OperateResult Write(string address, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return Write(address, bytes);
        }

        public override OperateResult Write(string address, string value)
            => Write(address, Encoding.ASCII.GetBytes(value));

        public override OperateResult Write(string address, byte[] data)
        {
            var dataId = ParseDataId(address);
            dataId.Data = data;
            var msg = SendPrimaryMessage(dataId.Stream, dataId.Function, data);
            if (!msg.IsSuccess) return OperateResult.Failed(msg.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  地址解析（格式: S1F1 或 S1F1:hexdata）
        // ═══════════════════════════════════════════

        private static SecsDataId ParseDataId(string address)
        {
            var result = new SecsDataId();
            if (string.IsNullOrEmpty(address)) return result;

            int colonIdx = address.IndexOf(':');
            string sfPart = colonIdx >= 0 ? address.Substring(0, colonIdx) : address;
            string dataHex = colonIdx >= 0 ? address.Substring(colonIdx + 1) : "";

            if (sfPart.StartsWith("S", StringComparison.OrdinalIgnoreCase) && sfPart.ToUpperInvariant().Contains("F"))
            {
                string upper = sfPart.ToUpperInvariant();
                int fIdx = upper.IndexOf('F');
                string sPart = upper.Substring(1, fIdx - 1);
                string fPart = upper.Substring(fIdx + 1);
                if (byte.TryParse(sPart, out byte s) && byte.TryParse(fPart, out byte f))
                {
                    result.Stream = s;
                    result.Function = f;
                }
            }

            if (!string.IsNullOrEmpty(dataHex) && dataHex.Length % 2 == 0)
            {
                result.Data = new byte[dataHex.Length / 2];
                for (int i = 0; i < result.Data.Length; i++)
                    result.Data[i] = Convert.ToByte(dataHex.Substring(i * 2, 2), 16);
            }

            return result;
        }

        // ═══════════════════════════════════════════
        //  工具方法
        // ═══════════════════════════════════════════

        private uint NextSystemBytes()
        {
            lock (_counterLock) { return ++_systemBytesCounter; }
        }

        private int ReadByte(int remainingMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) <= remainingMs)
            {
                try
                {
                    byte[] buf = new byte[1];
                    int read = Port.Read(buf, 0, 1);
                    if (read > 0) return buf[0];
                }
                catch (TimeoutException) { return -1; }
            }
            return -1;
        }

        private static byte[] CombineArrays(byte[] a, byte[] b)
        {
            byte[] result = new byte[a.Length + b.Length];
            Array.Copy(a, 0, result, 0, a.Length);
            Array.Copy(b, 0, result, a.Length, b.Length);
            return result;
        }

        public override string ToString() => $"SecsGemSerial[{Port.PortName}, DevId={DeviceId}]";
    }
}
