using System;
using System.IO;
using System.Net.Sockets;

namespace Nexus.App.Services
{
    /// <summary>
    /// 中文通讯诊断服务 — 将技术异常翻译为初学者可操作的建议。
    /// 对标 HSL CommunicationDiagnostics，提供更贴心的中文错误诊断。
    /// </summary>
    public static class ChineseDiagnostics
    {
        /// <summary>
        /// 将异常翻译为中文诊断结果。
        /// </summary>
        public static DiagnosticResult Diagnose(Exception exception, string protocolName = "")
        {
            string msg = exception.Message;

            // ── 网络层 ────────────────────────────────
            if (exception is SocketException || exception.InnerException is SocketException)
            {
                var sex = exception as SocketException ?? exception.InnerException as SocketException;
                return sex?.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused =>
                        new("连接被拒绝", $"设备 {protocolName} 拒绝了连接",
                            "✅ 检查设备是否开机\n✅ 检查 IP 地址和端口号\n✅ 检查设备通讯设置是否启用"),
                    SocketError.TimedOut =>
                        new("连接超时", "设备在规定时间内没有响应",
                            "✅ 检查网线是否插好\n✅ ping 一下设备 IP\n✅ 检查防火墙设置"),
                    SocketError.HostUnreachable =>
                        new("主机不可达", "无法找到目标设备",
                            "✅ 检查设备是否在线\n✅ 检查 IP 地址是否正确\n✅ 检查网络路由"),
                    SocketError.NetworkUnreachable =>
                        new("网络不可达", "本地网络未就绪",
                            "✅ 检查网卡是否启用\n✅ 检查网线连接"),
                    _ =>
                        new("网络错误", "TCP 连接失败",
                            "✅ 检查网线、IP、端口、防火墙\n✅ 用 ping 命令测试网络连通性")
                };
            }

            // ── 超时 ──────────────────────────────────
            if (exception is TimeoutException
                || msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("超时", StringComparison.OrdinalIgnoreCase))
            {
                return new DiagnosticResult("通讯超时",
                    "设备在等待时间内没有返回数据",
                    "✅ 确认设备已上电并在线\n✅ 检查 IP、端口、站号\n✅ 串口检查波特率、校验位\n✅ 尝试增大超时时间");
            }

            // ── IO 错误 ───────────────────────────────
            if (exception is IOException)
            {
                if (exception.InnerException is SocketException)
                {
                    return new DiagnosticResult("连接中断",
                        "与设备的 TCP 连接意外断开",
                        "✅ 检查网络连接稳定性\n✅ 检查设备是否重启\n✅ 检查网线是否松动");
                }
                if (msg.Contains("CRC", StringComparison.OrdinalIgnoreCase))
                {
                    return new DiagnosticResult("校验失败",
                        "收到的数据 CRC 校验不一致，可能受干扰",
                        "✅ 检查串口接线\n✅ 检查波特率/校验位设置\n✅ 检查现场电磁干扰\n✅ 尝试缩短通讯线缆");
                }
                return new DiagnosticResult("IO 错误",
                    "通讯过程中数据读写异常",
                    "✅ 检查物理连接\n✅ 检查设备状态");
            }

            // ── Modbus 异常 ───────────────────────────
            if (msg.Contains("Modbus", StringComparison.OrdinalIgnoreCase))
            {
                string detail = msg.Contains("IllegalFunction") ? "功能码不被设备支持"
                    : msg.Contains("IllegalDataAddress") ? "地址不存在或超出范围"
                    : msg.Contains("IllegalDataValue") ? "写入的数据值无效"
                    : msg.Contains("SlaveDeviceFailure") ? "从站设备内部故障"
                    : msg.Contains("SlaveDeviceBusy") ? "从站设备忙碌，稍后重试"
                    : msg.Contains("MemoryParity") ? "从站内存校验错误"
                    : "Modbus 协议异常";

                return new DiagnosticResult("Modbus 异常", detail,
                    "✅ 检查地址格式是否正确\n✅ 检查从站站号\n✅ 参考设备手册支持的地址范围");
            }

            // ── 协议特定 ──────────────────────────────
            if (msg.Contains("Fatek", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("永宏通讯错误",
                    "永宏 PLC 返回错误响应",
                    "✅ 检查站号设置 (01-FF)\n✅ 检查地址格式 (R/D/X/Y/M/T/C)\n✅ 检查通讯线缆");

            if (msg.Contains("S7", StringComparison.OrdinalIgnoreCase) || msg.Contains("Siemens", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("西门子通讯错误",
                    "S7 协议通讯失败",
                    "✅ 检查 Rack/Slot 设置 (S7-1200: 0/1, S7-300: 0/2)\n✅ 检查是否启用 PUT/GET 通讯\n✅ 检查 DB 块是否设置为非优化访问");

            if (msg.Contains("Mitsubishi", StringComparison.OrdinalIgnoreCase) || msg.Contains("MELSEC", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("三菱通讯错误",
                    "MC 协议通讯失败",
                    "✅ 检查网络模块是否支持 MC 协议\n✅ 检查端口 (通常 6000/5007)\n✅ 检查站号设置");

            if (msg.Contains("FINS", StringComparison.OrdinalIgnoreCase) || msg.Contains("Omron", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("欧姆龙通讯错误",
                    "FINS 协议通讯失败",
                    "✅ 检查 FINS 端口 (通常 9600)\n✅ 检查 PLC 的 FINS 通讯设置\n✅ 检查网络号/节点号");

            if (msg.Contains("CIP", StringComparison.OrdinalIgnoreCase) || msg.Contains("AllenBradley", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("AB 通讯错误",
                    "EtherNet/IP (CIP) 通讯失败",
                    "✅ 检查 Slot 号 (通常 0)\n✅ 检查 PLC 是否允许外部访问\n✅ 检查 Tag 名称拼写");

            if (msg.Contains("ADS", StringComparison.OrdinalIgnoreCase) || msg.Contains("Beckhoff", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("倍福通讯错误",
                    "TwinCAT ADS 通讯失败",
                    "✅ 检查 Target NetId 格式\n✅ 检查 ADS 端口 (默认 48898)\n✅ 检查 TwinCAT 路由设置");

            // ── 通用 ──────────────────────────────────
            if (msg.Contains("未连接", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Not connected", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("未连接",
                    "请先连接设备再执行读写操作",
                    "✅ 点击「连接」按钮\n✅ 检查连接参数配置");

            if (msg.Contains("地址", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("address", StringComparison.OrdinalIgnoreCase))
                return new DiagnosticResult("地址错误",
                    "地址格式不正确或不存在",
                    "✅ 检查地址格式 (参考提示)\n✅ 确认设备支持该地址区域\n✅ 参考设备手册");

            return new DiagnosticResult("通讯异常",
                exception.Message,
                "✅ 检查所有连接参数\n✅ 检查设备是否在线\n✅ 查看通讯日志了解详情");
        }
    }

    /// <summary>
    /// 诊断结果 — 包含标题、详细说明和可操作建议。
    /// </summary>
    public sealed record DiagnosticResult(
        string Title,
        string Detail,
        string Suggestions
    );
}
