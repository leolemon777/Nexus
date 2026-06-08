using System;
using System.Text;

namespace Nexus.Dlt
{
    /// <summary>
    /// DLT645-2007 电能表通讯协议客户端。
    /// <para>帧格式: 68H + A0..A5(地址) + 68H + C(控制) + L(长度) + DI0..DI3(数据标识) + DATA + CS + 16H</para>
    /// <para>所有数据域加 33H 传输，地址域低字节在前。</para>
    /// </summary>
    public class Dlt645Client : SerialDeviceBase
    {
        // ── SerialDeviceBase 抽象实现（串口协议自定义收发，不使用基类 SendAndReceive）──
        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;
        // ── 帧常量 ──────────────────────────────
        private const byte FRAME_HEADER = 0x68;
        private const byte FRAME_END = 0x16;
        private const byte DATA_OFFSET = 0x33;

        // ── 控制码 ──────────────────────────────
        private const byte CTRL_READ_DATA = 0x11;
        private const byte CTRL_READ_FOLLOW = 0x12;
        private const byte CTRL_READ_BLOCK = 0x13;
        private const byte CTRL_WRITE_DATA = 0x14;
        private const byte CTRL_WRITE_FOLLOW = 0x15;
        private const byte CTRL_BROAD_WRITE = 0x16;
        private const byte CTRL_FREEZE = 0x17;

        // ── 属性 ─────────────────────────────────

        /// <summary>电表通信地址（12位BCD，6字节）。</summary>
        public byte[] MeterAddress { get; set; } = new byte[6];

        /// <summary>密码（4字节BCD，用于写入）。</summary>
        public byte[] Password { get; set; } = new byte[4];

        /// <summary>操作者代码（4字节BCD，用于写入）。</summary>
        public byte[] OperatorCode { get; set; } = new byte[4];

        private readonly object _serialLock = new object();

        // ── 构造 ────────────────────────────────

        public Dlt645Client(ISerialPort serialPort, int timeout = 5000)
            : base(serialPort, timeout) { }

        /// <summary>通过电表地址字符串设置 MeterAddress（12位数字如 "000000000001"）。</summary>
        public void SetMeterAddress(string address12)
        {
            if (address12 == null || address12.Length != 12)
                throw new ArgumentException("电表地址必须为 12 位数字");

            // BCD 编码，低字节在前
            for (int i = 0; i < 6; i++)
            {
                string pair = address12.Substring(10 - i * 2, 2);
                MeterAddress[i] = byte.Parse(pair);
            }
        }

        // ═══════════════════════════════════════════
        //  读取数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 读取指定数据标识的原始数据。
        /// </summary>
        /// <param name="dataId">数据标识（DI0..DI3，4字节）。</param>
        /// <returns>解密后的原始数据。</returns>
        public OperateResult<byte[]> ReadData(byte[] dataId)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult<byte[]>.Failed("数据标识必须为 4 字节");

            var frame = BuildReadFrame(CTRL_READ_DATA, dataId, null);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

            return ParseResponse(recv.Content, CTRL_READ_DATA);
        }

        /// <summary>
        /// 通过数据标识字符串读取（格式 "00010000" 或 "00-01-00-00"）。
        /// </summary>
        public OperateResult<byte[]> ReadData(string dataIdStr)
        {
            var id = ParseDataId(dataIdStr);
            if (id == null) return OperateResult<byte[]>.Failed($"数据标识格式错误: {dataIdStr}");
            return ReadData(id);
        }

        /// <summary>读取当前正向有功总电能（kWh）。数据标识: 00-00-00-00。</summary>
        public OperateResult<decimal> ReadActiveEnergy()
        {
            var r = ReadData(new byte[] { 0x00, 0x00, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 4, 2);
        }

        /// <summary>读取当前反向有功总电能（kWh）。数据标识: 00-00-00-01。</summary>
        public OperateResult<decimal> ReadReverseActiveEnergy()
        {
            var r = ReadData(new byte[] { 0x01, 0x00, 0x00, 0x00 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 4, 2);
        }

        /// <summary>读取 A 相电压（V）。数据标识: 02-01-01-00。</summary>
        public OperateResult<decimal> ReadVoltageA()
        {
            var r = ReadData(new byte[] { 0x00, 0x01, 0x01, 0x02 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 2, 1);
        }

        /// <summary>读取 A 相电流（A）。数据标识: 02-02-01-00。</summary>
        public OperateResult<decimal> ReadCurrentA()
        {
            var r = ReadData(new byte[] { 0x00, 0x01, 0x02, 0x02 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 3, 3);
        }

        /// <summary>读取瞬时有功功率（kW）。数据标识: 02-03-00-00。</summary>
        public OperateResult<decimal> ReadInstantPower()
        {
            var r = ReadData(new byte[] { 0x00, 0x00, 0x03, 0x02 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 3, 4);
        }

        /// <summary>读取总功率因数。数据标识: 02-06-00-00。</summary>
        public OperateResult<decimal> ReadPowerFactor()
        {
            var r = ReadData(new byte[] { 0x00, 0x00, 0x06, 0x02 });
            if (!r.IsSuccess) return OperateResult<decimal>.Failed(r.Message);
            return BcdToDecimal(r.Content, 2, 3);
        }

        /// <summary>读取电表表号。数据标识: 03-03-00-02。</summary>
        public OperateResult<string> ReadMeterNumber()
        {
            var r = ReadData(new byte[] { 0x02, 0x00, 0x03, 0x03 });
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(BcdToString(r.Content));
        }

        // ═══════════════════════════════════════════
        //  写入数据
        // ═══════════════════════════════════════════

        /// <summary>
        /// 写入数据到电表（需密码和操作者代码）。
        /// </summary>
        public OperateResult WriteData(byte[] dataId, byte[] data)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult.Failed("数据标识必须为 4 字节");

            // 写入帧数据区 = PA + PAS + OP + DATA（加 33H）
            byte[] payload = new byte[2 + Password.Length + OperatorCode.Length + data.Length];
            payload[0] = Password[0];
            payload[1] = Password[1];
            payload[2] = Password[2];
            payload[3] = Password[3];
            payload[4] = OperatorCode[0];
            payload[5] = OperatorCode[1];
            payload[6] = OperatorCode[2];
            payload[7] = OperatorCode[3];
            Array.Copy(data, 0, payload, 8, data.Length);

            var frame = BuildReadFrame(CTRL_WRITE_DATA, dataId, payload);
            var recv = SendAndReceiveSerial(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = ParseResponse(recv.Content, CTRL_WRITE_DATA);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        // ═══════════════════════════════════════════
        //  广播校时
        // ═══════════════════════════════════════════

        /// <summary>
        /// 广播校时（无应答）。
        /// </summary>
        public OperateResult BroadcastTime(DateTime time)
        {
            byte[] data = new byte[6];
            data[0] = DecimalToBcd((byte)time.Second);
            data[1] = DecimalToBcd((byte)time.Minute);
            data[2] = DecimalToBcd((byte)time.Hour);
            data[3] = DecimalToBcd((byte)time.Day);
            data[4] = DecimalToBcd((byte)time.Month);
            data[5] = DecimalToBcd((byte)(time.Year % 100));

            var frame = BuildReadFrame(CTRL_BROAD_WRITE, new byte[] { 0x04, 0x00, 0x01, 0x04 }, data);
            try
            {
                lock (_serialLock)
                {
                    Port.Write(frame, 0, frame.Length);
                    RaiseMessageSent(DataConverter.ToHexString(frame));
                }
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"广播发送失败: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 基础实现
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
            => ReadData(address);

        public override OperateResult Write(string address, byte[] data)
        {
            var id = ParseDataId(address);
            if (id == null) return OperateResult.Failed($"数据标识格式错误: {address}");
            return WriteData(id, data);
        }

        public override string ToString() => $"Dlt645Client[Addr={BcdToString(MeterAddress)}]";

        // ═══════════════════════════════════════════
        //  帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 DLT645 帧。</summary>
        public byte[] BuildReadFrame(byte control, byte[] dataId, byte[]? data)
        {
            // 数据域 = DI0..DI3 + [附加数据]
            int dataLen = 4 + (data?.Length ?? 0);
            byte[] dataField = new byte[dataLen];
            dataField[0] = dataId[0];
            dataField[1] = dataId[1];
            dataField[2] = dataId[2];
            dataField[3] = dataId[3];
            if (data != null) Array.Copy(data, 0, dataField, 4, data.Length);

            // 加 33H 加密
            byte[] encrypted = new byte[dataLen];
            for (int i = 0; i < dataLen; i++)
                encrypted[i] = (byte)(dataField[i] + DATA_OFFSET);

            // 计算校验
            byte cs = (byte)(control ^ dataLen);
            for (int i = 0; i < 6; i++) cs ^= MeterAddress[i];
            for (int i = 0; i < dataLen; i++) cs ^= encrypted[i];

            // 组帧: 68H + A(6) + 68H + C + L + DATA + CS + 16H
            var frame = new byte[] { FRAME_HEADER, 0, 0, 0, 0, 0, 0, FRAME_HEADER, control, (byte)dataLen };
            Array.Copy(MeterAddress, 0, frame, 1, 6);

            var result = new byte[12 + dataLen];
            Array.Copy(frame, 0, result, 0, 10);
            Array.Copy(encrypted, 0, result, 10, dataLen);
            result[10 + dataLen] = cs;
            result[10 + dataLen + 1] = FRAME_END;

            return result;
        }

        /// <summary>解析 DLT645 响应帧，校验并提取解密后的数据。</summary>
        public static OperateResult<byte[]> ParseResponse(byte[] response, byte expectedCtrl)
        {
            if (response == null || response.Length < 12)
                return OperateResult<byte[]>.Failed($"响应帧过短 ({response?.Length ?? 0} 字节)");

            if (response[0] != FRAME_HEADER || response[7] != FRAME_HEADER)
                return OperateResult<byte[]>.Failed("帧头不匹配");
            if (response[response.Length - 1] != FRAME_END)
                return OperateResult<byte[]>.Failed("帧尾不匹配");

            byte ctrl = response[8];
            byte dataLen = response[9];

            // 错误响应检查
            if ((ctrl & 0x80) != 0)
            {
                byte errCode = (byte)(response[14] - DATA_OFFSET);
                return OperateResult<byte[]>.Failed($"电表错误: {GetErrorText(errCode)} (0x{errCode:X2})", errCode);
            }

            if (response.Length < 10 + dataLen + 2)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            // 校验和
            byte cs = 0;
            for (int i = 0; i < 6; i++) cs ^= response[1 + i];
            cs ^= ctrl;
            cs ^= dataLen;
            for (int i = 0; i < dataLen; i++) cs ^= response[10 + i];

            if (cs != response[10 + dataLen])
                return OperateResult<byte[]>.Failed($"校验和不匹配: 计算 0x{cs:X2}, 接收 0x{response[10 + dataLen]:X2}");

            // 解密数据域（减 33H）
            byte[] data = new byte[dataLen];
            for (int i = 0; i < dataLen; i++)
                data[i] = (byte)(response[10 + i] - DATA_OFFSET);

            // 跳过前 4 字节数据标识，返回纯数据
            if (dataLen > 4)
            {
                byte[] pureData = new byte[dataLen - 4];
                Array.Copy(data, 4, pureData, 0, pureData.Length);
                return OperateResult<byte[]>.Success(pureData);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        // ═══════════════════════════════════════════
        //  串口通讯
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendAndReceiveSerial(byte[] frame)
        {
            lock (_serialLock)
            {
                try
                {
                    RaiseMessageSent(DataConverter.ToHexString(frame));
                    Port.Write(frame, 0, frame.Length);

                    // 读取直到 16H (FRAME_END)
                    var response = new System.Collections.Generic.List<byte>();
                    byte[] buf = new byte[256];
                    int deadline = Environment.TickCount + Timeout;

                    while (Environment.TickCount < deadline)
                    {
                        int read = Port.Read(buf, 0, buf.Length);
                        if (read > 0)
                        {
                            for (int i = 0; i < read; i++)
                            {
                                response.Add(buf[i]);
                                // DLT645: 第二个 68H 后的完整帧
                                if (buf[i] == FRAME_END && response.Count >= 12)
                                {
                                    // 验证帧格式
                                    if (response[0] == FRAME_HEADER)
                                    {
                                        byte[] result = response.ToArray();
                                        RaiseMessageReceived(DataConverter.ToHexString(result));
                                        return OperateResult<byte[]>.Success(result);
                                    }
                                }
                            }
                        }
                    }

                    return OperateResult<byte[]>.Failed($"DLT645 响应超时 ({Timeout}ms)");
                }
                catch (Exception ex)
                {
                    RaiseError($"DLT645 通讯异常: {ex.Message}");
                    return OperateResult<byte[]>.Failed($"DLT645 通讯异常: {ex.Message}");
                }
            }
        }

        // ═══════════════════════════════════════════
        //  BCD 转换工具
        // ═══════════════════════════════════════════

        /// <summary>BCD 字节数组转十进制值。</summary>
        public static OperateResult<decimal> BcdToDecimal(byte[] data, int digits, int decimalPlaces)
        {
            if (data == null || data.Length == 0)
                return OperateResult<decimal>.Failed("数据为空");

            string bcdStr = BcdToString(data);
            if (bcdStr.Length > digits)
                bcdStr = bcdStr.Substring(0, digits);

            if (decimal.TryParse(bcdStr.Insert(bcdStr.Length - decimalPlaces, "."), out decimal result))
                return OperateResult<decimal>.Success(result);

            return OperateResult<decimal>.Failed($"BCD 解析失败: {bcdStr}");
        }

        /// <summary>BCD 字节数组转字符串（每字节两位十进制数）。</summary>
        public static string BcdToString(byte[] data)
        {
            var sb = new StringBuilder(data.Length * 2);
            // 低字节在前，逆序显示
            for (int i = data.Length - 1; i >= 0; i--)
            {
                sb.Append((data[i] >> 4).ToString());
                sb.Append((data[i] & 0x0F).ToString());
            }
            return sb.ToString();
        }

        /// <summary>十进制字节转 BCD。</summary>
        public static byte DecimalToBcd(byte value)
        {
            return (byte)(((value / 10) << 4) | (value % 10));
        }

        /// <summary>解析数据标识字符串。</summary>
        public static byte[]? ParseDataId(string dataIdStr)
        {
            if (string.IsNullOrEmpty(dataIdStr) || dataIdStr.Length != 8)
                return null;

            try
            {
                return new byte[]
                {
                    Convert.ToByte(dataIdStr.Substring(6, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(4, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(2, 2), 16),
                    Convert.ToByte(dataIdStr.Substring(0, 2), 16)
                };
            }
            catch { return null; }
        }

        /// <summary>获取 DLT645 错误码描述。</summary>
        public static string GetErrorText(byte errCode)
        {
            switch (errCode)
            {
                case 0x01: return "非法数据标识";
                case 0x02: return "非法数据格式";
                case 0x03: return "非法数据长度";
                case 0x04: return "非法数据序列号";
                case 0x05: return "通讯速率不能更改";
                case 0x06: return "年时区数超";
                case 0x07: return "日时段数超";
                case 0x08: return "费率数超";
                case 0x09: return "密码错/未授权";
                case 0x0A: return "通信速率不能更改";
                case 0x0B: return "超载";
                case 0x0C: return "最大需量清零失败";
                case 0x0D: return "时间超限";
                case 0x0E: return "电表清零失败";
                case 0x0F: return "其他错误";
                default: return $"未知错误 (0x{errCode:X2})";
            }
        }
    }
}
