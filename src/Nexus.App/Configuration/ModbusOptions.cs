namespace Nexus.App.Configuration;

/// <summary>
/// Modbus 协议相关配置。从 <c>appsettings.json</c> 的 <c>Modbus</c> 节绑定。
/// 通过 <c>IOptions&lt;ModbusOptions&gt;</c> 注入到 ViewModel。
/// </summary>
public sealed class ModbusOptions
{
    public const string SectionName = "Modbus";

    /// <summary>默认连接 IP（调试器初始值）。</summary>
    public string DefaultIp { get; set; } = "127.0.0.1";

    /// <summary>默认 Modbus TCP 端口（真实 PLC 通常为 502）。</summary>
    public int DefaultPort { get; set; } = 502;

    /// <summary>默认站号 / Slave ID。0 表示广播，1-247 是合法单播范围。</summary>
    public byte DefaultSlaveId { get; set; } = 1;

    /// <summary>默认超时（毫秒）。</summary>
    public int DefaultTimeoutMs { get; set; } = 5000;

    /// <summary>内置虚拟 Server 启动端口（避开 502 避免需要 root/管理员权限）。</summary>
    public int VirtualServerPort { get; set; } = 15020;

    /// <summary>是否在启动 App 时自动启动内置虚拟 Server。</summary>
    public bool AutoStartVirtualServer { get; set; } = false;

    /// <summary>默认字节序（BigEndian / LittleEndian / MidBigEndian / MidLittleEndian）。</summary>
    public string DefaultByteOrder { get; set; } = "BigEndian";
}
