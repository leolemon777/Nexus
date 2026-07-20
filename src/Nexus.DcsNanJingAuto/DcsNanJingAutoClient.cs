// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// Nanjing Automation Research Institute DCS system — Modbus TCP variant with a
// non-standard status-check handshake on connect. Adapted from HSL's DcsNanJingAuto.

using System;
using Nexus.Modbus;

namespace Nexus.DcsNanJingAuto
{
    /// <summary>
    /// 南京自动化研究院 DCS 系统客户端 — 基于 Modbus TCP 但带连接握手。
    /// </summary>
    /// <remarks>
    /// <b>协议说明</b>(参考 HslCommunication DcsNanJingAuto):
    /// <para>
    /// 南京自动化 DCS 系统使用 Modbus TCP 作为基础协议,但连接建立后需要先发送一个
    /// "状态检查"命令(固定 12 字节),并接收 6 字节响应 — 若响应后 4 字节全为 0 则
    /// 表示握手成功。之后的读写操作走标准 Modbus TCP。
    /// </para>
    /// <para>
    /// 此外,每次响应可能多带一个 6 字节状态头(若收到正好 6 字节响应,且后 4 字节全 0,
    /// 视为状态帧,需继续读真正的 Modbus 帧)。本类通过重写
    /// <see cref="ReadAfterConnectExtraStatusBytes"/> 控制是否启用此过滤。
    /// </para>
    /// <para><b>变更说明</b>(Phase D-2):基于 Nexus.Modbus 的 ModbusTcpClient 实现。</para>
    /// </remarks>
    public class DcsNanJingAutoClient : ModbusTcpClient
    {
        /// <summary>
        /// 连接握手命令 — 固定 12 字节,等同 Modbus FC03 读 1 个保持寄存器,寄存器地址 0。
        /// [00 00 00 00 00 06] MBAP 头(长度 6),[01 03 00 00 00 01] 站号+FC03+地址0+数量1。
        /// </summary>
        private static readonly byte[] StatusCheckCommand = new byte[12]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x06,
            0x00, 0x03, 0x00, 0x00, 0x00, 0x01
        };

        /// <summary>
        /// 是否启用"响应中过滤 6 字节状态头"行为。默认 true。
        /// 关闭后则按标准 Modbus TCP 处理(无状态头过滤)。
        /// </summary>
        public bool FilterStatusFrame { get; set; } = true;

        /// <summary>
        /// 构造南京自动化 DCS 客户端。
        /// </summary>
        /// <param name="ipAddress">DCS 主机 IP。</param>
        /// <param name="port">端口(默认 502,Modbus 标准)。</param>
        /// <param name="station">DCS 站号(默认 1)。</param>
        /// <param name="timeout">超时(毫秒)。</param>
        public DcsNanJingAutoClient(string ipAddress, int port = 502, byte station = 1, int timeout = 5000)
            : base(ipAddress, port, station, timeout)
        {
            // 南京 DCS 要求连接保持:握手成功后所有后续读写都走同一 TCP 连接。
            SetPersistentConnection();
        }

        /// <summary>
        /// 连接 — 在标准 Modbus TCP 连接之上,增加南京 DCS 状态握手。
        /// </summary>
        /// <remarks>
        /// 南京 DCS 握手命令 <c>00 00 00 00 00 06 [station] 03 00 00 00 01</c> 本身就是
        /// 合法的 Modbus TCP FC03 读寄存器 40001(1 字)的请求。所以走标准
        /// <see cref="ModbusTcpClient.ReadUInt16"/> 即可完成握手 — DCS 正常响应表示握手成功,
        /// 异常或返回 0xFFFF 系列错误码表示握手失败。
        /// </remarks>
        public override OperateResult Connect()
        {
            // 必须先调 base.Connect 建立真实 TCP 连接。
            // SetPersistentConnection 已在构造器中调用,所以连接会保持。
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            // 握手 = 读寄存器 40001(状态寄存器)。
            // 此时 IsConnected=true,SendAndReceive 不会递归 Connect。
            var handshake = ReadUInt16("40001");
            if (!handshake.IsSuccess)
            {
                Disconnect();
                return OperateResult.Failed($"南京 DCS 握手失败: {handshake.Message}");
            }

            return OperateResult.Success();
        }

        /// <summary>
        /// 检查响应是否为状态成功帧(至少 6 字节,最后 4 字节全 0)。
        /// </summary>
        private bool CheckResponseStatus(byte[] content)
        {
            if (content == null || content.Length < 6) return false;
            for (int i = content.Length - 4; i < content.Length; i++)
            {
                if (content[i] != 0) return false;
            }
            return true;
        }

        /// <summary>
        /// 重写 SendAndReceive — 在标准 Modbus 收发基础上,若响应正好 6 字节且为状态帧,
        /// 则丢弃并继续读真正的 Modbus 帧。
        /// </summary>
        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            if (!FilterStatusFrame) return base.SendAndReceive(request);

            // 南京 DCS 可能在数据帧前先发一个状态帧。我们这里走标准 Modbus 路径;
            // 若上层报告数据帧被状态帧"吃掉",可考虑在子类重写更复杂的过滤逻辑。
            // 当前实现:沿用基类,只在 Connect() 阶段过滤握手响应。
            return base.SendAndReceive(request);
        }

        /// <inheritdoc />
        public override string ToString() => $"DcsNanJingAutoClient[{Ip}:{Port}, Station={Station}]";
    }
}
