using System;

namespace Nexus.AllenBradley
{
    /// <summary>
    /// Allen-Bradley Micro800 系列 CIP 客户端 — 针对 Micro820/850/870 优化。
    /// <para>协议层次: TCP → ENIP → CIP (Class 0, 简化路由)</para>
    /// <para>继承 AllenBradleyCipClient，调整路由路径和程序命名空间。</para>
    /// <para>与 ControlLogix 的主要区别:</para>
    /// <para>  - 路由路径更简单（无背板路由，直接 CIP）</para>
    /// <para>  - 程序命名空间不同（Micro800 使用 Global 和 Program 标签）</para>
    /// <para>  - 不支持某些高级服务（如 Get Instance Attribute List）</para>
    /// <para>  - PDU 大小通常更小（默认 244 字节）</para>
    /// </summary>
    public class AllenBradleyMicroCipClient : AllenBradleyCipClient
    {
        /// <summary>Micro800 默认 PDU 较小。</summary>
        private const int Micro800DefaultPduSize = 244;

        /// <summary>是否使用全局标签命名空间（默认 true）。</summary>
        public bool UseGlobalNamespace { get; set; } = true;

        /// <summary>控制器名称（Micro800 有时需要指定控制器名）。</summary>
        public string? ControllerName { get; set; }

        public AllenBradleyMicroCipClient(string ipAddress, int port = 44818, byte slot = 0, int timeout = 5000)
            : base(ipAddress, port, slot, timeout)
        {
            MaxPduSize = Micro800DefaultPduSize;
        }

        /// <summary>
        /// 构建 Micro800 路由路径 — 无背板路由，仅发送到处理器。
        /// <para>Micro800 通常不需要背板路由，路径更简单。</para>
        /// </summary>
        protected new byte[] BuildPath(byte slot)
        {
            // Micro800: 直接路由到处理器，不需要背板路径
            // 路径: Port 1, Link 0 → 路由到处理器
            return new byte[] { 0x01, 0x00 };
        }

        /// <summary>
        /// 构建 Micro800 连接路径 — 适用于 SendRRData。
        /// <para>Micro800 的路由比 ControlLogix 更简单。</para>
        /// </summary>
        protected new byte[] BuildConnectionPath(byte slot)
        {
            // Micro800: 路由到处理器（Port 1, Link 0, Class 0x02, Instance 0x01）
            return new byte[] { 0x01, 0x00, 0x20, 0x02, 0x24, 0x01 };
        }

        /// <summary>
        /// 读取 Micro800 标签 — 使用简化的路由路径。
        /// <para>Micro800 标签示例: "MyTag", "Program:MainProgram.MyTag"</para>
        /// </summary>
        public OperateResult<object?> ReadMicroTag(string tagName)
        {
            return ReadTagValue(tagName);
        }

        /// <summary>
        /// 读取 Micro800 全局标签。
        /// <para>Micro800 的全局标签不需要 Program: 前缀。</para>
        /// </summary>
        public OperateResult<object?> ReadGlobalTag(string tagName)
        {
            return ReadTagValue(tagName);
        }

        /// <summary>
        /// 检测是否为 Micro800 控制器 — 通过 ListIdentity 判断设备类型。
        /// </summary>
        public OperateResult<bool> IsMicro800()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<bool>.Failed(identity.Message, identity.ErrorCode);

            // Micro800 系列的产品代码范围
            string name = identity.Content.ProductName.ToUpperInvariant();
            bool isMicro = name.Contains("MICRO8") || name.Contains("2080");
            return OperateResult<bool>.Success(isMicro);
        }
    }
}
