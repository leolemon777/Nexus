// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.

using System;

namespace Nexus.Pipe
{
    /// <summary>
    /// DTU 透明传输管道 — TCP 连到 DTU 服务器(4G/Ethernet serial-over-TCP),
    /// 数据自动透传到目标串口设备。本实现复用 <see cref="PipeTcpNet"/> 的 TCP 传输,
    /// 仅额外保存 DTU ID 用于识别目标设备。
    /// </summary>
    /// <remarks>
    /// DTU 是中国工厂部署常见的方案:PLC 通过 RS485 接 DTU,DTU 通过 4G 拨号上云,
    /// 上位机软件通过 TCP 连 DTU 服务器,数据被路由到对应 DTU。Nexus 已有
    /// <see cref="DtuClient"/> 实现完整 LIST/解析协议;本 Pipe 是更轻量的"已连接 DTU"管道,
    /// 给上层 DeviceCommunication 当传输介质用。
    /// </remarks>
    public class PipeDtuNet : PipeTcpNet
    {
        /// <param name="dtuServerHost">DTU 服务器主机名/IP。</param>
        /// <param name="dtuServerPort">DTU 服务器端口(默认 8899)。</param>
        /// <param name="deviceId">DTU 设备 ID(用于识别目标,可选)。</param>
        public PipeDtuNet(string dtuServerHost, int dtuServerPort, string? deviceId = null)
            : base(dtuServerHost, dtuServerPort)
        {
            DeviceId = deviceId;
        }

        /// <summary>DTU 设备 ID — DTU 服务器据此路由数据到具体 DTU。</summary>
        public string? DeviceId { get; set; }
    }
}
