// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// ShineIn light source controller (昱行智造科技) over RS-232.
// Adapted from HSL's Instrument.Light.ShineInLightSourceController.

using System;

namespace Nexus.ShineIn
{
    /// <summary>
    /// 光源参数 — 颜色、亮度、工作模式、通道等。
    /// </summary>
    public class ShineInLightData
    {
        /// <summary>颜色: 1=红 2=绿 3=蓝 4=白(默认)</summary>
        public byte Color { get; set; } = 4;
        /// <summary>亮度 0x00-0xFF,值越大越亮。</summary>
        public byte Light { get; set; }
        /// <summary>亮度等级 1-3。</summary>
        public byte LightDegree { get; set; } = 1;
        /// <summary>工作模式: 0=延时常亮 1=通道一频闪 2=通道二频闪 3=通道一二频闪 4=普通常亮 5=关闭</summary>
        public byte WorkMode { get; set; }
        /// <summary>控制器地址选择位。</summary>
        public byte Address { get; set; }
        /// <summary>脉冲宽度 0x01-0x14。</summary>
        public byte PulseWidth { get; set; } = 1;
        /// <summary>通道 0x01-0x08。</summary>
        public byte Channel { get; set; }

        public ShineInLightData() { }

        public ShineInLightData(byte[] data)
        {
            ParseFrom(data);
        }

        /// <summary>序列化为 7 字节原始数据。</summary>
        public byte[] GetSourceData() => new byte[] { Color, Light, LightDegree, WorkMode, Address, PulseWidth, Channel };

        /// <summary>从 7 字节原始数据解析。</summary>
        public void ParseFrom(byte[] data)
        {
            if (data != null && data.Length >= 7)
            {
                Color = data[0];
                Light = data[1];
                LightDegree = data[2];
                WorkMode = data[3];
                Address = data[4];
                PulseWidth = data[5];
                Channel = data[6];
            }
        }

        public override string ToString() => $"ShineInLightData[Color={Color}, Light={Light}, Mode={WorkMode}, Ch={Channel}]";
    }

    /// <summary>
    /// 昱行智造光源控制器串口客户端。
    /// </summary>
    /// <remarks>
    /// <para><b>协议格式</b>(参考 HSL ShineInLightSourceController):
    /// <list type="bullet">
    ///   <item>帧头: <c>/*</c>(0x2F 0x2A)+ 固定字节 0xF0 + 命令 + 数据长度</item>
    ///   <item>数据: 命令参数(Color/Light/...)</item>
    ///   <item>校验: XOR(从 0xF0 到 data 末尾)</item>
    ///   <item>帧尾: <c>*/</c>(0x2A 0x2F)</item>
    /// </list>
    /// </para>
    /// <para>默认串口: 57600 波特,8 数据位,1 停止位,偶校验。</para>
    /// </remarks>
    public class ShineInLightSourceController : SerialDeviceBase
    {
        public ShineInLightSourceController(ISerialPort port, int timeout = 3000)
            : base(port, timeout)
        {
            InterFrameDelay = 20;
        }

        protected override int ResponseHeaderLength => 5;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 5) return 0;
            // header[4] = 数据长度(不含头 4 字节和尾 3 字节)。
            // 总 payload = header[4] + 3(xor + */)
            return header[4] + 3;
        }

        // ── 命令打包(公开,便于测试)────────────

        /// <summary>
        /// 将命令和数据打包为完整帧:
        /// [0x2F 0x2A 0xF0 cmd len data... xor 0x2A 0x2F]
        /// </summary>
        public static byte[] PackCommand(byte cmd, byte[]? data)
        {
            if (data == null) data = Array.Empty<byte>();
            byte[] frame = new byte[data.Length + 8];
            frame[0] = 0x2F;  // '/'
            frame[1] = 0x2A;  // '*'
            frame[2] = 0xF0;  // 固定
            frame[3] = cmd;
            frame[4] = (byte)(frame.Length - 4);  // 长度(从 cmd 到末尾)
            Buffer.BlockCopy(data, 0, frame, 5, data.Length);
            frame[frame.Length - 2] = 0x2A;  // '*'
            frame[frame.Length - 1] = 0x2F;  // '/'

            // XOR: 从 0xF0 到 data 末尾(不含 xor/帧尾)。
            int xor = frame[2];
            for (int i = 3; i < frame.Length - 3; i++)
                xor ^= frame[i];
            frame[frame.Length - 3] = (byte)xor;

            return frame;
        }

        /// <summary>构建读通道命令(cmd=2)。</summary>
        public static byte[] BuildReadCommand(byte channel)
            => PackCommand(2, new byte[] { channel });

        /// <summary>构建写光源参数命令(cmd=1)。</summary>
        public static byte[] BuildWriteCommand(ShineInLightData data)
            => PackCommand(1, data.GetSourceData());

        // ── 响应解析 ────────────────────────────

        /// <summary>解析响应,验证帧头帧尾和 XOR,返回实际数据(payload)。</summary>
        public static OperateResult<byte[]> ExtractActualData(byte[] response)
        {
            if (response == null || response.Length < 9)
                return OperateResult<byte[]>.Failed($"响应过短: {response?.Length ?? 0}");

            // 帧头 /* 帧尾 */
            if (response[0] != 0x2F || response[1] != 0x2A
                || response[response.Length - 2] != 0x2A || response[response.Length - 1] != 0x2F)
                return OperateResult<byte[]>.Failed("帧头/帧尾错误: 期望 /* ... */");

            // XOR 校验。
            int xor = response[2];  // 0xF0
            for (int i = 3; i < response.Length - 3; i++)
                xor ^= response[i];
            if ((byte)xor != response[response.Length - 3])
                return OperateResult<byte[]>.Failed($"XOR 校验失败: 期望 0x{(byte)xor:X2}, 实际 0x{response[response.Length - 3]:X2}");

            // 写入响应:cmd=1,响应 [5] = 0xAA 表示成功。
            if (response[3] == 1)
            {
                return response[5] == 0xAA
                    ? OperateResult<byte[]>.Success(Array.Empty<byte>())
                    : OperateResult<byte[]>.Failed($"写入失败,错误码 0x{response[5]:X2}");
            }

            // 读取响应:提取 data 部分([5..len-3])。
            int dataLen = response.Length - 8;
            if (dataLen <= 0) return OperateResult<byte[]>.Success(Array.Empty<byte>());
            byte[] data = new byte[dataLen];
            Buffer.BlockCopy(response, 5, data, 0, dataLen);
            return OperateResult<byte[]>.Success(data);
        }

        // ── 高级 API ────────────────────────────

        /// <summary>读通道的光源参数。</summary>
        public OperateResult<ShineInLightData> Read(byte channel)
        {
            var resp = SendAndReceive(BuildReadCommand(channel));
            if (!resp.IsSuccess) return OperateResult<ShineInLightData>.Failed(resp.Message);
            var data = ExtractActualData(resp.Content);
            if (!data.IsSuccess) return OperateResult<ShineInLightData>.Failed(data.Message);
            return OperateResult<ShineInLightData>.Success(new ShineInLightData(data.Content));
        }

        /// <summary>写光源参数到设备。</summary>
        public OperateResult Write(ShineInLightData data)
        {
            var resp = SendAndReceive(BuildWriteCommand(data));
            if (!resp.IsSuccess) return resp;
            return ExtractActualData(resp.Content);
        }

        /// <summary>快捷开光(亮度 0=关,255=满亮)。</summary>
        public OperateResult SetBrightness(byte channel, byte brightness)
        {
            return Write(new ShineInLightData
            {
                Channel = channel,
                Light = brightness,
                WorkMode = brightness == 0 ? (byte)5 : (byte)4,  // 5=关闭, 4=普通常亮
                Color = 4  // 白色
            });
        }

        /// <summary>快捷关光。</summary>
        public OperateResult TurnOff(byte channel) => SetBrightness(channel, 0);

        /// <summary>快捷开光(满亮度)。</summary>
        public OperateResult TurnOn(byte channel) => SetBrightness(channel, 0xFF);

        public override string ToString() => $"ShineInLightSourceController[{Port.PortName}]";
    }
}
