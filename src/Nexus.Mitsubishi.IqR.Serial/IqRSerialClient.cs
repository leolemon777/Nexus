// MELSEC iQ-R RS-232 client via MelsecA3CNet protocol.
// iQ-R series (R08CPU, R16CPU, etc.) computer-link over RS-232 uses the same
// A3C protocol frame as the legacy AnS/Q series, with iQ-R specific extensions
// for higher device-address ranges. This client delegates to MelsecA3CNetClient.

using Nexus;
using Nexus.Mitsubishi;

namespace Nexus.Mitsubishi.IqR.Serial
{
    /// <summary>
    /// 三菱 MELSEC iQ-R 系列 RS-232 串口客户端。
    /// </summary>
    /// <remarks>
    /// <b>实现说明</b>(Phase C-4):iQ-R 系列 PLC(R08CPU、R16CPU、R32CPU 等)的 RS-232
    /// 计算机链接协议与 AnS/Q 系列的 A3C 协议高度兼容 — 仅在设备地址范围、特殊寄存器上
    /// 有 iQ-R 扩展。本客户端直接继承 <see cref="MelsecA3CNetClient"/>,
    /// 获得完整的 A3C 串口通讯能力(读写字/位/字符串、批量、CRC 校验、自动重连)。
    /// <para>
    /// <b>变更说明</b>:本类从纯 OperateResult.Failed 占位升级为基于
    /// <see cref="MelsecA3CNetClient"/> 的真实实现。
    /// </para>
    /// <para><b>iQ-R 专有特性</b>(超出 A3C 范围,本类不实现):
    /// <list type="bullet">
    ///   <item>RD(扩展文件寄存器)间接寻址 — 需 iQ-R 固件支持,见 iQ-R 通讯手册。</item>
    ///   <item>SLMP 串口封装 — iQ-R 支持 SLMP-over-serial,可用 MC3E 二进制帧。</item>
    /// </list>
    /// 如需这些特性,推荐使用 <c>Nexus.Mitsubishi.Mc3EBinaryClient</c>(TCP/SLMP)。</para>
    /// </remarks>
    public class IqRSerialClient : MelsecA3CNetClient
    {
        /// <summary>
        /// 构造 iQ-R 串口客户端。
        /// </summary>
        /// <param name="port">已配置好参数的 ISerialPort(典型:9600/8/E/1,与 iQ-R 内置 RS-232 默认一致)。</param>
        /// <param name="station">PLC 站号(0-31,默认 0)。</param>
        /// <param name="timeout">通讯超时(毫秒)。</param>
        public IqRSerialClient(ISerialPort port, byte station = 0, int timeout = 5000)
            : base(port, station, timeout)
        {
        }

        /// <inheritdoc />
        public override string ToString() => $"IqRSerialClient[Station={Station:D2}]";
    }
}
