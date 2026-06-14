using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus RTU Over TCP 客户端 — 通过 TCP 传输 RTU 格式报文。
    /// <para>RTU-over-TCP = RTU ADU (Station+FC+Data+CRC16) sent over TCP socket, no MBAP header.</para>
    /// <para>支持功能码: FC01, FC02, FC03, FC04, FC05, FC06, FC15, FC16, FC22, FC23。</para>
    /// <para>支持地址前缀（0x/1x/3x/4x）、字节序选项、字符串编码。</para>
    /// </summary>
    public class ModbusRtuOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>站号。</summary>
        public byte Station { get; set; }

        /// <summary>字节序。</summary>
        public Endianness ByteOrder { get; set; } = Endianness.BigEndian;
        protected byte? _addressStationOverride;
        protected Endianness? _addressByteOrderOverride;

        /// <summary>字符串编码选项。</summary>
        public StringEncoding StringEncodingOption { get; set; } = StringEncoding.Ascii;

        /// <summary>
        /// 创建 Modbus RTU Over TCP 客户端。
        /// </summary>
        /// <param name="ip">远程 IP 地址。</param>
        /// <param name="port">远程端口（默认 502）。</param>
        /// <param name="station">从站地址。</param>
        /// <param name="timeout">超时（毫秒）。</param>
        public ModbusRtuOverTcpClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ═══════════════════════════════════════════
        //  TcpDeviceBase 抽象成员实现
        //  RTU-over-TCP 响应通过自定义 RtuSendAndReceive 处理，
        //  此处提供基类要求的实现。
        // ═══════════════════════════════════════════

        /// <summary>默认心跳：RTU 读保持寄存器 0（FC03）。</summary>
        protected override byte[] BuildHeartbeat()
        {
            byte[] pdu = new byte[] { 0x03, 0x00, 0x00, 0x00, 0x01 };
            return BuildRtuFrame(pdu, Station);
        }

        protected override int ResponseHeaderLength => 2; // Station(1) + FC(1)

        protected override int GetResponsePayloadLength(byte[] header)
        {
            // RTU-over-TCP 响应长度可变，通过 RtuSendAndReceive 处理
            return 0;
        }

        // ═══════════════════════════════════════════
        //  RTU 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 RTU 帧: [Station(1)][PDU(N)][CRC16(2)]。</summary>
        private byte[] BuildRtuFrame(byte[] pdu, byte station)
        {
            int dataLen = 1 + pdu.Length; // Station + PDU
            byte[] frame = new byte[dataLen + 2]; // +2 for CRC16
            frame[0] = station;
            Buffer.BlockCopy(pdu, 0, frame, 1, pdu.Length);
            ushort crc = CrcCalculator.ComputeCrc16(frame, 0, dataLen);
            frame[dataLen] = (byte)(crc & 0xFF);
            frame[dataLen + 1] = (byte)((crc >> 8) & 0xFF);
            return frame;
        }

        // ═══════════════════════════════════════════
        //  RTU-over-TCP 收发
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送 RTU 请求并接收响应（通过 TCP）。
        /// 返回 Modbus PDU: [FC(1)][Data(N)]。
        /// </summary>
        private OperateResult<byte[]> RtuSendAndReceive(byte[] pdu)
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

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                byte station = TakeStation();
                byte[] request = BuildRtuFrame(pdu, station);

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                byte[]? response = ReadRtuResponse(ns);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取 RTU 响应超时");

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!CrcCalculator.VerifyCrc16(response))
                    return OperateResult<byte[]>.Failed("RTU 响应 CRC 校验失败");

                if (response[0] != station)
                    return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={station}, 实际={response[0]}");

                if ((response[1] & 0x80) != 0)
                {
                    byte exCode = response.Length > 2 ? response[2] : (byte)0;
                    string msg = exCode switch
                    {
                        1 => "非法功能码",
                        2 => "非法数据地址",
                        3 => "非法数据值",
                        4 => "从站设备故障",
                        _ => $"Modbus异常码: {exCode}"
                    };
                    return OperateResult<byte[]>.Failed(msg, exCode);
                }

                // 返回 PDU（去掉 Station 和 CRC）
                int pduLen = response.Length - 3; // Station(1) + CRC(2)
                byte[] respPdu = new byte[pduLen];
                Buffer.BlockCopy(response, 1, respPdu, 0, pduLen);

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(respPdu);
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 异步发送 RTU 请求并接收响应（通过 TCP）。
        /// 返回 Modbus PDU: [FC(1)][Data(N)]。
        /// </summary>
        private async Task<OperateResult<byte[]>> RtuSendAndReceiveAsync(byte[] pdu, CancellationToken ct = default)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = await ConnectAsync(ct).ConfigureAwait(false);
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                byte station = TakeStation();
                byte[] request = BuildRtuFrame(pdu, station);

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                await ns.WriteAsync(request, 0, request.Length, ct).ConfigureAwait(false);

                byte[]? response = await ReadRtuResponseAsync(ns, ct).ConfigureAwait(false);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取 RTU 响应超时");

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!CrcCalculator.VerifyCrc16(response))
                    return OperateResult<byte[]>.Failed("RTU 响应 CRC 校验失败");

                if (response[0] != station)
                    return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={station}, 实际={response[0]}");

                if ((response[1] & 0x80) != 0)
                {
                    byte exCode = response.Length > 2 ? response[2] : (byte)0;
                    string msg = exCode switch
                    {
                        1 => "非法功能码",
                        2 => "非法数据地址",
                        3 => "非法数据值",
                        4 => "从站设备故障",
                        _ => $"Modbus异常码: {exCode}"
                    };
                    return OperateResult<byte[]>.Failed(msg, exCode);
                }

                int pduLen = response.Length - 3;
                byte[] respPdu = new byte[pduLen];
                Buffer.BlockCopy(response, 1, respPdu, 0, pduLen);

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(respPdu);
            }
            catch (OperationCanceledException)
            {
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed("操作已取消");
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  RTU 响应读取
        // ═══════════════════════════════════════════

        /// <summary>从 TCP 流读取完整 RTU 响应帧。</summary>
        private byte[]? ReadRtuResponse(NetworkStream ns)
        {
            int remaining = Timeout;

            // 读取 Station(1) + FC(1)
            byte[]? header = ReadExactTimeout(ns, 2, ref remaining);
            if (header == null) return null;

            byte fc = header[1];

            if ((fc & 0x80) != 0)
            {
                // 异常响应: [Station][FC|0x80][ExceptionCode][CRC16]
                byte[]? tail = ReadExactTimeout(ns, 3, ref remaining);
                if (tail == null) return null;
                byte[] full = new byte[5];
                full[0] = header[0]; full[1] = header[1];
                Buffer.BlockCopy(tail, 0, full, 2, 3);
                return full;
            }

            switch (fc)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x17:
                {
                    // 读响应: [Station][FC][ByteCount][Data(ByteCount)][CRC16]
                    byte[]? bcBuf = ReadExactTimeout(ns, 1, ref remaining);
                    if (bcBuf == null) return null;
                    byte byteCount = bcBuf[0];

                    int restLen = byteCount + 2; // Data + CRC(2)
                    byte[]? rest = ReadExactTimeout(ns, restLen, ref remaining);
                    if (rest == null) return null;

                    byte[] full = new byte[3 + restLen];
                    full[0] = header[0]; full[1] = header[1]; full[2] = byteCount;
                    Buffer.BlockCopy(rest, 0, full, 3, restLen);
                    return full;
                }

                default:
                {
                    // 写响应 (FC05/06/15/16): [Station][FC][Addr(2)][Value/Count(2)][CRC16]
                    byte[]? tail = ReadExactTimeout(ns, 6, ref remaining);
                    if (tail == null) return null;
                    byte[] full = new byte[8];
                    full[0] = header[0]; full[1] = header[1];
                    Buffer.BlockCopy(tail, 0, full, 2, 6);
                    return full;
                }
            }
        }

        /// <summary>异步从 TCP 流读取完整 RTU 响应帧。</summary>
        private async Task<byte[]?> ReadRtuResponseAsync(NetworkStream ns, CancellationToken ct)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            try
            {
                byte[]? header = await ReadExactAsync(ns, 2, timeoutCts.Token).ConfigureAwait(false);
                if (header == null) return null;

                byte fc = header[1];

                if ((fc & 0x80) != 0)
                {
                    byte[]? tail = await ReadExactAsync(ns, 3, timeoutCts.Token).ConfigureAwait(false);
                    if (tail == null) return null;
                    byte[] full = new byte[5];
                    full[0] = header[0]; full[1] = header[1];
                    Buffer.BlockCopy(tail, 0, full, 2, 3);
                    return full;
                }

                switch (fc)
                {
                    case 0x01:
                    case 0x02:
                    case 0x03:
                    case 0x04:
                    case 0x17:
                    {
                        byte[]? bcBuf = await ReadExactAsync(ns, 1, timeoutCts.Token).ConfigureAwait(false);
                        if (bcBuf == null) return null;
                        byte byteCount = bcBuf[0];

                        int restLen = byteCount + 2;
                        byte[]? rest = await ReadExactAsync(ns, restLen, timeoutCts.Token).ConfigureAwait(false);
                        if (rest == null) return null;

                        byte[] full = new byte[3 + restLen];
                        full[0] = header[0]; full[1] = header[1]; full[2] = byteCount;
                        Buffer.BlockCopy(rest, 0, full, 3, restLen);
                        return full;
                    }

                    default:
                    {
                        byte[]? tail = await ReadExactAsync(ns, 6, timeoutCts.Token).ConfigureAwait(false);
                        if (tail == null) return null;
                        byte[] full = new byte[8];
                        full[0] = header[0]; full[1] = header[1];
                        Buffer.BlockCopy(tail, 0, full, 2, 6);
                        return full;
                    }
                }
            }
            catch (OperationCanceledException) { return null; }
        }

        private byte[]? ReadExactTimeout(NetworkStream ns, int count, ref int remainingMs)
        {
            int start = Environment.TickCount;
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                if (unchecked(Environment.TickCount - start) > remainingMs) return null;
                try
                {
                    int read = ns.Read(buf, offset, count - offset);
                    if (read == 0) return null;
                    offset += read;
                }
                catch (IOException) { return null; }
            }
            // 更新剩余预算：扣除本次已用时间，供后续调用复用同一截止窗口。
            remainingMs -= (int)unchecked(Environment.TickCount - start);
            return buf;
        }

        private static async Task<byte[]?> ReadExactAsync(NetworkStream ns, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await ns.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        // ═══════════════════════════════════════════
        //  地址解析（支持前缀模式）
        //  0xxxx=线圈, 1xxxx=离散输入, 3xxxx=输入寄存器, 4xxxx=保持寄存器
        // ═══════════════════════════════════════════

        protected byte TakeStation()
        {
            byte station = _addressStationOverride ?? Station;
            _addressStationOverride = null;
            return station;
        }

        protected void CaptureAddressContext(string address)
        {
            var context = AddressContext.Parse(address);
            string? stationValue = GetAddressParameter(context, "unit", "station", "slave", "s");
            if (stationValue != null)
            {
                if (!int.TryParse(stationValue, out int station) || station < byte.MinValue || station > byte.MaxValue)
                    throw new AddressParseException(address, $"站号超出范围: {stationValue}");
                _addressStationOverride = (byte)station;
            }
            else
            {
                _addressStationOverride = null;
            }

            string? byteOrderValue = GetAddressParameter(context, "bo", "byteOrder", "byteorder", "endian", "endianness");
            if (byteOrderValue != null)
            {
                if (!TryParseByteOrder(byteOrderValue, out var byteOrder))
                    throw new AddressParseException(address, $"字节序无效: {byteOrderValue}");
                _addressByteOrderOverride = byteOrder;
            }
            else
            {
                _addressByteOrderOverride = null;
            }
        }

        protected static string? GetAddressParameter(AddressContext context, params string[] keys)
        {
            foreach (var key in keys)
                foreach (var kvp in context.Parameters)
                    if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
            return null;
        }

        protected static bool TryParseByteOrder(string value, out Endianness byteOrder)
        {
            string normalized = value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
            switch (normalized)
            {
                case "be":
                case "big":
                case "bigendian":
                case "abcd":
                    byteOrder = Endianness.BigEndian;
                    return true;
                case "le":
                case "little":
                case "littleendian":
                case "dcba":
                    byteOrder = Endianness.LittleEndian;
                    return true;
                case "midbig":
                case "midbigendian":
                case "badc":
                    byteOrder = Endianness.MidBigEndian;
                    return true;
                case "midlittle":
                case "midlittleendian":
                case "cdab":
                    byteOrder = Endianness.MidLittleEndian;
                    return true;
                default:
                    return Enum.TryParse(value, true, out byteOrder);
            }
        }

        protected virtual (ushort address, byte readFc, byte writeFc) ParseAddressEx(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            CaptureAddressContext(address);
            address = AddressContext.ExtractCoreAddress(address).Trim();

            char prefix = address[0];
            string numPart = address.Substring(1);

            if (address.Length >= 5 && char.IsDigit(prefix))
            {
                if (prefix == '0') return (ParseUshort(numPart), 0x01, 0x05);
                if (prefix == '1') return (ParseUshort(numPart), 0x02, (byte)0);
                if (prefix == '3') return (ParseUshort(numPart), 0x04, (byte)0);
                if (prefix == '4') return (ParseUshort(numPart), 0x03, 0x06);
            }

            return (ParseUshort(address), 0x03, 0x06);
        }

        protected static ushort ParseUshort(string s) => ushort.Parse(s.TrimStart('0').Length == 0 ? "0" : s.TrimStart('0'));
        private ushort ParseAddress(string address) => ParseAddressEx(address).address;

        // ═══════════════════════════════════════════
        //  字节序数据转换
        // ═══════════════════════════════════════════

        private Endianness CurrentByteOrder => _addressByteOrderOverride ?? ByteOrder;

        private short ToInt16Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.LittleEndian => (short)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToInt16(data, offset)
        };

        private ushort ToUInt16Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.LittleEndian => (ushort)(data[offset] | (data[offset + 1] << 8)),
            _ => DataConverter.ToUInt16(data, offset)
        };

        private int ToInt32Ordered(byte[] data, int offset) => CurrentByteOrder switch
        {
            Endianness.BigEndian => DataConverter.ToInt32(data, offset),
            Endianness.LittleEndian => data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24),
            Endianness.MidBigEndian => (data[offset + 1] << 24) | (data[offset] << 16) | (data[offset + 3] << 8) | data[offset + 2],
            Endianness.MidLittleEndian => (data[offset + 2] << 24) | (data[offset + 3] << 16) | (data[offset] << 8) | data[offset + 1],
            _ => DataConverter.ToInt32(data, offset)
        };

        private float ToFloatOrdered(byte[] data, int offset)
        {
            int v = ToInt32Ordered(data, offset);
            unsafe { return *(float*)&v; }
        }

        private byte[] GetBytesOrdered(short value) => CurrentByteOrder == Endianness.LittleEndian
            ? new byte[] { (byte)value, (byte)(value >> 8) }
            : DataConverter.GetBytes(value);

        private byte[] GetBytesOrdered(int value)
        {
            var byteOrder = CurrentByteOrder;
            if (byteOrder == Endianness.BigEndian) return DataConverter.GetBytes(value);
            if (byteOrder == Endianness.LittleEndian) return new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };
            if (byteOrder == Endianness.MidBigEndian) return new byte[] { (byte)(value >> 16), (byte)(value >> 24), (byte)value, (byte)(value >> 8) };
            return new byte[] { (byte)(value >> 8), (byte)value, (byte)(value >> 24), (byte)(value >> 16) };
        }

        private byte[] GetBytesOrdered(float value)
        {
            unsafe { int v = *(int*)&value; return GetBytesOrdered(v); }
        }

        private byte[] GetBytesOrdered(long value) => DataConverter.GetBytes(value, CurrentByteOrder);
        private byte[] GetBytesOrdered(ulong value) => DataConverter.GetBytes(value, CurrentByteOrder);
        private byte[] GetBytesOrdered(double value) => DataConverter.GetBytes(value, CurrentByteOrder);

        // ═══════════════════════════════════════════
        //  FC01 — 读线圈 (Read Coils)
        //  FC02 — 读离散输入 (Read Discrete Inputs)
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var result = RtuSendAndReceive(new byte[] { fc, (byte)(addr >> 8), (byte)addr, 0, 1 });
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[2] & 0x01) != 0);
        }

        /// <summary>读取多个线圈/离散输入。</summary>
        public OperateResult<bool[]> ReadBools(string address, ushort count)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x02 ? (byte)0x02 : (byte)0x01;
            var result = RtuSendAndReceive(new byte[] { fc, (byte)(addr >> 8), (byte)addr, (byte)(count >> 8), (byte)count });
            if (!result.IsSuccess) return OperateResult<bool[]>.Failed(result.Message, result.ErrorCode);

            bool[] values = new bool[count];
            for (int i = 0; i < count; i++)
                values[i] = (result.Content[2 + i / 8] & (1 << (i % 8))) != 0;
            return OperateResult<bool[]>.Success(values);
        }

        // ═══════════════════════════════════════════
        //  FC03 — 读保持寄存器 (Read Holding Registers)
        //  FC04 — 读输入寄存器 (Read Input Registers)
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRegistersCore(ushort address, ushort count, byte fc)
        {
            var result = RtuSendAndReceive(new byte[] { fc, (byte)(address >> 8), (byte)address, (byte)(count >> 8), (byte)count });
            if (!result.IsSuccess) return result;
            byte byteCount = result.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 1, fc);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            return OperateResult<short>.Success(ToInt16Ordered(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 1, fc);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            return OperateResult<ushort>.Success(ToUInt16Ordered(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 2, fc);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            return OperateResult<int>.Success(ToInt32Ordered(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 4, fc);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0, CurrentByteOrder));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 2, fc);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            return OperateResult<float>.Success(ToFloatOrdered(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            var r = ReadRegistersCore(addr, 4, fc);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0, CurrentByteOrder));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, length));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            byte[] data = new byte[length];
            Buffer.BlockCopy(r.Content, 0, data, 0, Math.Min(length, r.Content.Length));
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  FC05 — 写单线圈 (Write Single Coil)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, bool value)
        {
            ushort addr = ParseAddress(address);
            var result = RtuSendAndReceive(new byte[] { 0x05, (byte)(addr >> 8), (byte)addr, (byte)(value ? 0xFF : 0x00), 0x00 });
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  FC06 — 写单寄存器 (Write Single Register)
        // ═══════════════════════════════════════════

        public override OperateResult Write(string address, short value)
        {
            ushort addr = ParseAddress(address);
            var vb = GetBytesOrdered(value);
            var result = RtuSendAndReceive(new byte[] { 0x06, (byte)(addr >> 8), (byte)addr, vb[0], vb[1] });
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        // ═══════════════════════════════════════════
        //  FC22 — 掩码写寄存器 (Mask Write Register)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 原子掩码写保持寄存器 (FC22)。
        /// 新值按 Modbus 规范计算为: (当前值 AND andMask) OR (orMask AND NOT andMask)。
        /// </summary>
        public OperateResult MaskWriteRegister(string address, ushort andMask, ushort orMask)
        {
            ushort addr = ParseAddress(address);
            var result = RtuSendAndReceive(new byte[]
            {
                0x16,
                (byte)(addr >> 8), (byte)addr,
                (byte)(andMask >> 8), (byte)andMask,
                (byte)(orMask >> 8), (byte)orMask
            });
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  FC15 — 写多个线圈 (Write Multiple Coils)
        // ═══════════════════════════════════════════

        /// <summary>写入多个线圈值。</summary>
        public OperateResult WriteMultipleCoils(ushort startAddress, bool[] values)
        {
            int byteCount = (values.Length + 7) / 8;
            byte[] coilBytes = new byte[byteCount];
            for (int i = 0; i < values.Length; i++)
                if (values[i]) coilBytes[i / 8] |= (byte)(1 << (i % 8));

            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x0F;
            pdu[1] = (byte)(startAddress >> 8); pdu[2] = (byte)startAddress;
            pdu[3] = (byte)(values.Length >> 8); pdu[4] = (byte)values.Length;
            pdu[5] = (byte)byteCount;
            Buffer.BlockCopy(coilBytes, 0, pdu, 6, byteCount);

            var result = RtuSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  FC16 — 写多个寄存器 (Write Multiple Registers)
        // ═══════════════════════════════════════════

        private OperateResult WriteMultipleRegisters(ushort address, ushort count, byte[] registerData)
        {
            byte byteCount = (byte)(count * 2);
            byte[] pdu = new byte[6 + byteCount];
            pdu[0] = 0x10;
            pdu[1] = (byte)(address >> 8); pdu[2] = (byte)address;
            pdu[3] = (byte)(count >> 8); pdu[4] = (byte)count;
            pdu[5] = byteCount;
            Buffer.BlockCopy(registerData, 0, pdu, 6, byteCount);

            var result = RtuSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message, result.ErrorCode);
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, int value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, ulong value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, float value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 2, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, double value)
        {
            ushort addr = ParseAddress(address);
            return WriteMultipleRegisters(addr, 4, GetBytesOrdered(value));
        }

        public override OperateResult Write(string address, string value)
        {
            ushort addr = ParseAddress(address);
            var data = DataConverter.GetBytes(value);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            ushort addr = ParseAddress(address);
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        // ═══════════════════════════════════════════
        //  FC23 — 读写多个寄存器 (Read/Write Multiple Registers, 原子操作)
        // ═══════════════════════════════════════════

        /// <summary>原子操作：同时写入并读取寄存器（FC23）。</summary>
        public OperateResult<byte[]> ReadWriteMultipleRegisters(
            ushort readAddress, ushort readCount,
            ushort writeAddress, byte[] writeData)
        {
            ushort writeRegCount = (ushort)(writeData.Length / 2);
            byte writeByteCount = (byte)writeData.Length;
            byte[] pdu = new byte[10 + writeByteCount];
            pdu[0] = 0x17;
            pdu[1] = (byte)(readAddress >> 8); pdu[2] = (byte)readAddress;
            pdu[3] = (byte)(readCount >> 8); pdu[4] = (byte)readCount;
            pdu[5] = (byte)(writeAddress >> 8); pdu[6] = (byte)writeAddress;
            pdu[7] = (byte)(writeRegCount >> 8); pdu[8] = (byte)writeRegCount;
            pdu[9] = writeByteCount;
            Buffer.BlockCopy(writeData, 0, pdu, 10, writeByteCount);

            var result = RtuSendAndReceive(pdu);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte byteCount = result.Content[1];
            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(result.Content, 2, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  连接重试
        // ═══════════════════════════════════════════

        /// <summary>连接重试次数（默认 3 次）。</summary>
        public new int RetryCount { get; set; } = 3;

        /// <summary>重试间隔（毫秒，默认 1000ms）。</summary>
        public new int RetryInterval { get; set; } = 1000;

        /// <summary>带重试的连接。</summary>
        public new OperateResult Connect()
        {
            OperateResult? lastResult = null;
            for (int i = 0; i <= RetryCount; i++)
            {
                if (i > 0)
                {
                    Log.Info($"连接重试 ({i}/{RetryCount})，等待 {RetryInterval}ms...");
                    Thread.Sleep(RetryInterval);
                }
                lastResult = base.Connect();
                if (lastResult.IsSuccess) return lastResult;
            }
            return lastResult ?? OperateResult.Failed("连接失败");
        }

        /// <summary>带重试的异步连接。</summary>
        public new async Task<OperateResult> ConnectAsync()
        {
            OperateResult? lastResult = null;
            for (int i = 0; i <= RetryCount; i++)
            {
                if (i > 0)
                {
                    Log.Info($"连接重试 ({i}/{RetryCount})，等待 {RetryInterval}ms...");
                    await Task.Delay(RetryInterval).ConfigureAwait(false);
                }
                lastResult = await base.ConnectAsync().ConfigureAwait(false);
                if (lastResult.IsSuccess) return lastResult;
            }
            return lastResult ?? OperateResult.Failed("连接失败");
        }

        // ═══════════════════════════════════════════
        //  自定义功能码发送
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发送自定义 Modbus PDU（自动添加站号和 CRC16）。
        /// 返回响应 PDU（去掉站号和 CRC）。
        /// <para>使用原始字节读取方式，适用于任意功能码。</para>
        /// </summary>
        public OperateResult<byte[]> SendCustomModbus(byte[] pdu)
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

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                _addressStationOverride = null;
                byte[] request = BuildRtuFrame(pdu, Station);

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                // 原始字节读取 — 适用于未知功能码的任意长度响应
                byte[]? response = ReadRawTcpResponse(ns);
                if (response == null)
                    return OperateResult<byte[]>.Failed("读取自定义 RTU 响应超时");

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!CrcCalculator.VerifyCrc16(response))
                    return OperateResult<byte[]>.Failed("自定义 RTU 响应 CRC 校验失败");

                if (response[0] != Station)
                    return OperateResult<byte[]>.Failed($"响应站号不匹配: 期望={Station}, 实际={response[0]}");

                if ((response[1] & 0x80) != 0)
                {
                    byte exCode = response.Length > 2 ? response[2] : (byte)0;
                    string msg = exCode switch
                    {
                        1 => "非法功能码",
                        2 => "非法数据地址",
                        3 => "非法数据值",
                        4 => "从站设备故障",
                        _ => $"Modbus异常码: {exCode}"
                    };
                    return OperateResult<byte[]>.Failed(msg, exCode);
                }

                int pduLen = response.Length - 3;
                if (pduLen < 0) return OperateResult<byte[]>.Failed("自定义 RTU 响应过短");
                byte[] respPdu = new byte[pduLen];
                Buffer.BlockCopy(response, 1, respPdu, 0, pduLen);

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(respPdu);
            }
            catch (Exception ex)
            {
                Log.Error($"自定义 RTU over TCP 通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        /// <summary>
        /// 原始字节读取 — 从 TCP 流读取所有可用字节直到静默。
        /// 适用于自定义功能码的任意长度响应。
        /// </summary>
        private byte[]? ReadRawTcpResponse(NetworkStream ns)
        {
            int start = Environment.TickCount;
            int window = Timeout;
            var response = new System.Collections.Generic.List<byte>();
            byte[] buf = new byte[256];

            while (unchecked(Environment.TickCount - start) < window)
            {
                if (ns.DataAvailable)
                {
                    int read = ns.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        for (int i = 0; i < read; i++) response.Add(buf[i]);
                        start = Environment.TickCount; window = 50;
                    }
                }
                else if (response.Count > 0)
                {
                    break;
                }
                else
                {
                    Thread.Sleep(1);
                }
            }

            return response.Count > 0 ? response.ToArray() : null;
        }

        // ═══════════════════════════════════════════
        //  字符串编码选项
        // ═══════════════════════════════════════════

        /// <summary>使用配置的编码读取字符串。</summary>
        public OperateResult<string> ReadStringEncoded(string address, ushort length)
        {
            var (addr, readFc, _) = ParseAddressEx(address);
            byte fc = readFc == 0x04 ? (byte)0x04 : (byte)0x03;
            ushort regCount = (ushort)((length + 1) / 2);
            var r = ReadRegistersCore(addr, regCount, fc);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);

            string text = StringEncodingOption switch
            {
                StringEncoding.Utf8 => System.Text.Encoding.UTF8.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0', ' '),
                StringEncoding.Unicode => System.Text.Encoding.Unicode.GetString(r.Content, 0, Math.Min(length * 2, r.Content.Length)).TrimEnd('\0', ' '),
                _ => DataConverter.ToString(r.Content, 0, length)
            };
            return OperateResult<string>.Success(text);
        }

        /// <summary>使用配置的编码写入字符串。</summary>
        public OperateResult WriteStringEncoded(string address, string value)
        {
            ushort addr = ParseAddress(address);
            byte[] data = StringEncodingOption switch
            {
                StringEncoding.Utf8 => System.Text.Encoding.UTF8.GetBytes(value),
                StringEncoding.Unicode => System.Text.Encoding.Unicode.GetBytes(value),
                _ => DataConverter.GetBytes(value)
            };
            ushort regCount = (ushort)((data.Length + 1) / 2);
            if (data.Length % 2 != 0) Array.Resize(ref data, data.Length + 1);
            return WriteMultipleRegisters(addr, regCount, data);
        }

        // ═══════════════════════════════════════════
        //  Async 方法
        // ═══════════════════════════════════════════

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));

        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));
    }
}
