// DAM3601 8-channel analog input module.
// Protocol: standard Modbus RTU; this class adds DAM3601-specific register map
// and type-safe accessors. Derived from HslCommunication (MIT, Richard.Hu 2017-2025).

using System;
using Nexus.Modbus;

namespace Nexus.Dam3601
{
    /// <summary>
    /// DAM3601 8 通道模拟量采集模块客户端(基于 Modbus RTU)。
    /// </summary>
    /// <remarks>
    /// DAM3601 是常见的工业模拟量采集模块(Modbus RTU 接口),8 通道,
    /// 支持 0-5V / 0-10V / 0-20mA / 4-20mA 输入。
    /// <para>
    /// <b>寄存器映射</b>(常见出厂默认):
    /// <list type="table">
    ///   <listheader><term>地址</term><description>含义</description></listheader>
    ///   <item><term>0x00 (40001)</term><description>通道 0 当前值(16-bit)</description></item>
    ///   <item><term>0x01 (40002)</term><description>通道 1 当前值</description></item>
    ///   <item><term>...</term><description>...</description></item>
    ///   <item><term>0x07 (40008)</term><description>通道 7 当前值</description></item>
    ///   <item><term>0x10</term><description>通道 0 量程(0=0-5V,1=0-10V,2=0-20mA,3=4-20mA)</description></item>
    ///   <item><term>0x11..0x17</term><description>通道 1..7 量程</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>注意</b>:不同厂家的 DAM3601 寄存器布局可能略有差异。本类使用最常见的
    /// 出厂默认布局,如需调整可重写 <see cref="ChannelValueRegister"/> / <see cref="ChannelRangeRegister"/>。
    /// </para>
    /// </remarks>
    public class Dam3601Client
    {
        private readonly ModbusRtuClient _modbus;
        private readonly byte _station;

        /// <summary>构造,基于已有的 Modbus RTU 客户端。</summary>
        /// <param name="modbus">已配置好串口的 ModbusRtuClient。</param>
        /// <param name="station">DAM3601 模块站号(默认 1)。</param>
        public Dam3601Client(ModbusRtuClient modbus, byte station = 1)
        {
            _modbus = modbus ?? throw new ArgumentNullException(nameof(modbus));
            _station = station;
            if (modbus.Station != station)
                throw new ArgumentException($"Modbus 客户端站号({modbus.Station})与 DAM3601 站号({station})不一致", nameof(station));
        }

        /// <summary>直接构造(便利方法)。</summary>
        public Dam3601Client(Nexus.ISerialPort port, byte station = 1, int timeout = 5000)
            : this(new ModbusRtuClient(port, station) { /* 默认配置 */ }, station)
        {
            _modbus.GetType();  // 防止编译器警告未使用
        }

        /// <summary>通道值寄存器基地址(默认 0,通道 N 的寄存器 = base + N)。</summary>
        public int ChannelValueRegister { get; set; } = 0;

        /// <summary>通道量程寄存器基地址(默认 0x10)。</summary>
        public int ChannelRangeRegister { get; set; } = 0x10;

        /// <summary>支持的通道数(默认 8)。</summary>
        public int ChannelCount { get; set; } = 8;

        /// <summary>读取单个通道的原始 ADC 值(16-bit)。</summary>
        /// <param name="channel">通道号 0..7。</param>
        public OperateResult<ushort> ReadRawValue(int channel)
        {
            if (channel < 0 || channel >= ChannelCount)
                return OperateResult<ushort>.Failed($"通道号 {channel} 越界(0..{ChannelCount - 1})");
            return _modbus.ReadUInt16((ChannelValueRegister + channel + 1).ToString());
        }

        /// <summary>读取所有通道的原始 ADC 值(一次 Modbus 读多寄存器)。</summary>
        public OperateResult<ushort[]> ReadAllRawValues()
        {
            // Modbus 地址 = base + 1(Modbus 协议地址从 1 开始,寄存器 0 是 40001)。
            var r = _modbus.ReadBytes((ChannelValueRegister + 1).ToString(), (ushort)(ChannelCount * 2));
            if (!r.IsSuccess) return OperateResult<ushort[]>.Failed(r.Message);
            if (r.Content.Length < ChannelCount * 2)
                return OperateResult<ushort[]>.Failed($"读取字节不足: {r.Content.Length}/{ChannelCount * 2}");

            ushort[] values = new ushort[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
            {
                // Modbus RTU 默认大端
                values[i] = (ushort)((r.Content[i * 2] << 8) | r.Content[i * 2 + 1]);
            }
            return OperateResult<ushort[]>.Success(values);
        }

        /// <summary>读取通道量程配置。</summary>
        /// <returns>0=0-5V, 1=0-10V, 2=0-20mA, 3=4-20mA。</returns>
        public OperateResult<int> ReadRange(int channel)
        {
            if (channel < 0 || channel >= ChannelCount)
                return OperateResult<int>.Failed($"通道号 {channel} 越界(0..{ChannelCount - 1})");
            return _modbus.ReadUInt16((ChannelRangeRegister + channel + 1).ToString())
                .Map(v => (int)v);
        }

        /// <summary>读取所有通道量程配置。</summary>
        public OperateResult<int[]> ReadAllRanges()
        {
            var r = _modbus.ReadBytes((ChannelRangeRegister + 1).ToString(), (ushort)(ChannelCount * 2));
            if (!r.IsSuccess) return OperateResult<int[]>.Failed(r.Message);
            int[] ranges = new int[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
                ranges[i] = (r.Content[i * 2] << 8) | r.Content[i * 2 + 1];
            return OperateResult<int[]>.Success(ranges);
        }

        /// <summary>根据通道量程将原始 ADC 值转换为工程量。</summary>
        /// <param name="rawValue">原始 ADC 值(0..65535)。</param>
        /// <param name="range">量程:0=0-5V, 1=0-10V, 2=0-20mA, 3=4-20mA。</param>
        public static double ConvertToEngineering(ushort rawValue, int range)
        {
            // 16-bit ADC 满量程 = 65535
            const double FullScale = 65535.0;
            switch (range)
            {
                case 0: return rawValue / FullScale * 5.0;        // 0-5V
                case 1: return rawValue / FullScale * 10.0;       // 0-10V
                case 2: return rawValue / FullScale * 20.0;       // 0-20mA
                case 3: return rawValue / FullScale * 16.0 + 4.0; // 4-20mA
                default: return rawValue; // 未知量程,返回原始值
            }
        }

        /// <summary>读取通道工程量(自动按量程换算)。</summary>
        public OperateResult<double> ReadEngineeringValue(int channel)
        {
            var rawR = ReadRawValue(channel);
            if (!rawR.IsSuccess) return OperateResult<double>.Failed(rawR.Message);

            var rangeR = ReadRange(channel);
            if (!rangeR.IsSuccess) return OperateResult<double>.Failed(rangeR.Message);

            return OperateResult<double>.Success(ConvertToEngineering(rawR.Content, rangeR.Content));
        }

        /// <summary>读取所有通道的工程量。</summary>
        public OperateResult<double[]> ReadAllEngineeringValues()
        {
            var rawR = ReadAllRawValues();
            if (!rawR.IsSuccess) return OperateResult<double[]>.Failed(rawR.Message);

            var rangeR = ReadAllRanges();
            if (!rangeR.IsSuccess) return OperateResult<double[]>.Failed(rangeR.Message);

            double[] result = new double[ChannelCount];
            for (int i = 0; i < ChannelCount; i++)
                result[i] = ConvertToEngineering(rawR.Content[i], rangeR.Content[i]);
            return OperateResult<double[]>.Success(result);
        }
    }

    /// <summary>OperateResult 映射扩展(本地 helper,避免污染 Nexus.Core)。</summary>
    internal static class OperateResultMapExtensions
    {
        public static OperateResult<TOut> Map<TIn, TOut>(this OperateResult<TIn> r, Func<TIn, TOut> f)
        {
            if (!r.IsSuccess) return OperateResult<TOut>.Failed(r.Message);
            return OperateResult<TOut>.Success(f(r.Content));
        }
    }
}
