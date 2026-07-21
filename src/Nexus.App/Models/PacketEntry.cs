using System;

namespace Nexus.App.Models;

/// <summary>
/// 一条报文记录（TX 发送 / RX 接收），用于右侧实时报文监控面板。
/// </summary>
public class PacketEntry
{
    /// <summary>时间戳（精确到毫秒）。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>方向：true = 发送(TX)，false = 接收(RX)。</summary>
    public bool IsTX { get; set; }

    /// <summary>十六进制字符串（空格分隔）。</summary>
    public string HexData { get; set; } = string.Empty;

    /// <summary>本次操作的延迟（毫秒），仅 RX 行有值。</summary>
    public double LatencyMs { get; set; }

    /// <summary>方向显示文本。</summary>
    public string DirectionText => IsTX ? "TX →" : "RX ←";

    /// <summary>时间显示文本（HH:mm:ss.fff）。</summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
}
