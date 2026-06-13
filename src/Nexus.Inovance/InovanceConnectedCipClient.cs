using Nexus.AllenBradley;

namespace Nexus.Inovance
{
    /// <summary>
    /// 汇川 Connected CIP 客户端 — 通过 EtherNet/IP Connected CIP 协议访问汇川 AM 系列 PLC。
    /// <para>继承 AllenBradleyConnectedCipClient，使用标准 CIP Forward Open/Close 连接管理。</para>
    /// <para>适用: 汇川 AM 系列 PLC，支持 Tag 读写的 CIP 设备。</para>
    /// <para>地址格式: TagName, TagName.member, TagName[index], TagName.member[index]</para>
    /// </summary>
    public class InovanceConnectedCipClient : AllenBradleyConnectedCipClient
    {
        /// <summary>汇川厂商 ID。</summary>
        private const ushort InovanceVendorId = 0x0200;

        /// <summary>
        /// 创建汇川 Connected CIP 客户端。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 44818）。</param>
        /// <param name="slot">机架/槽号（默认 0）。</param>
        /// <param name="timeout">超时（毫秒，默认 5000）。</param>
        public InovanceConnectedCipClient(string ip, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ip, port, slot, timeout)
        {
        }

        public override string ToString() => $"InovanceConnectedCip[{IpAddress}:{Port}, Slot={Slot}]";
    }
}
