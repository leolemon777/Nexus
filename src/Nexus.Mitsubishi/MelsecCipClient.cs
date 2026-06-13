using System;
using System.Threading.Tasks;
using Nexus.AllenBradley;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// 三菱 R 系列 EtherNet/IP CIP 客户端 — 继承 Allen-Bradley CIP 客户端。
    /// <para>三菱 iQ-R/iQ-F 系列 PLC 通过 EtherNet/IP 接口支持标准 CIP 协议。</para>
    /// <para>CIP 协议与 Allen-Bradley 完全相同，区别在于连接路径和默认参数。</para>
    /// <para>支持 Tag 读写、分段读写、批量读写、PLC 控制命令。</para>
    /// </summary>
    public class MelsecCipClient : AllenBradleyCipClient
    {
        /// <summary>
        /// 初始化三菱 CIP 客户端。
        /// </summary>
        /// <param name="ipAddress">PLC IP 地址。</param>
        /// <param name="port">EtherNet/IP 端口号（默认 44818）。</param>
        /// <param name="slot">目标模块所在插槽号（默认 2，三菱 R 系列典型值）。</param>
        /// <param name="timeout">通讯超时（毫秒）。</param>
        public MelsecCipClient(string ipAddress, int port = 44818, byte slot = 2, int timeout = 5000)
            : base(ipAddress, port, slot, timeout)
        {
        }

        // ── 三菱 CIP 扩展方法 ──────────────────

        /// <summary>
        /// 读取 PLC 运行状态 (CIP GetAttributeSingle)。
        /// <para>通过 CIP Identity 对象获取设备状态。</para>
        /// </summary>
        public OperateResult<bool> IsRun()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<bool>.Failed(identity.Message, identity.ErrorCode);

            return OperateResult<bool>.Success(identity.Content.Status == 0);
        }

        /// <summary>异步读取 PLC 运行状态。</summary>
        public Task<OperateResult<bool>> IsRunAsync()
            => Task.Run(() => IsRun());

        /// <summary>
        /// 读取 PLC 型号信息。
        /// <para>返回 ProductName，如 "iQ-R Series"。</para>
        /// </summary>
        public OperateResult<string> ReadPlcType()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<string>.Failed(identity.Message, identity.ErrorCode);

            return OperateResult<string>.Success(identity.Content.ProductName);
        }

        /// <summary>异步读取 PLC 型号信息。</summary>
        public Task<OperateResult<string>> ReadPlcTypeAsync()
            => Task.Run(() => ReadPlcType());

        /// <summary>
        /// 读取 PLC 固件版本。
        /// </summary>
        public OperateResult<string> ReadFirmwareVersion()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<string>.Failed(identity.Message, identity.ErrorCode);

            return OperateResult<string>.Success(identity.Content.FirmwareVersion);
        }

        /// <summary>异步读取 PLC 固件版本。</summary>
        public Task<OperateResult<string>> ReadFirmwareVersionAsync()
            => Task.Run(() => ReadFirmwareVersion());

        /// <summary>
        /// 读取 PLC 序列号。
        /// </summary>
        public OperateResult<uint> ReadSerialNumber()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<uint>.Failed(identity.Message, identity.ErrorCode);

            return OperateResult<uint>.Success(identity.Content.SerialNumber);
        }

        /// <summary>异步读取 PLC 序列号。</summary>
        public Task<OperateResult<uint>> ReadSerialNumberAsync()
            => Task.Run(() => ReadSerialNumber());

        /// <summary>
        /// 读取三菱 PLC 扩展型号信息。
        /// <para>返回 VendorName + ProductName + FirmwareVersion。</para>
        /// </summary>
        public OperateResult<MitsubishiDeviceInfo> ReadDeviceInfo()
        {
            var identity = ReadDeviceIdentity();
            if (!identity.IsSuccess)
                return OperateResult<MitsubishiDeviceInfo>.Failed(identity.Message, identity.ErrorCode);

            var info = new MitsubishiDeviceInfo
            {
                ProductName = identity.Content.ProductName,
                VendorName = identity.Content.VendorName,
                FirmwareVersion = identity.Content.FirmwareVersion,
                SerialNumber = identity.Content.SerialNumber,
                DeviceType = identity.Content.DeviceType,
                ProductCode = identity.Content.ProductCode,
                Status = identity.Content.Status
            };

            return OperateResult<MitsubishiDeviceInfo>.Success(info);
        }

        /// <summary>异步读取三菱 PLC 扩展型号信息。</summary>
        public Task<OperateResult<MitsubishiDeviceInfo>> ReadDeviceInfoAsync()
            => Task.Run(() => ReadDeviceInfo());

        public override string ToString() => $"MelsecCipClient[{IpAddress}:{Port} Slot={Slot}]";
    }

    /// <summary>
    /// 三菱 PLC 设备信息（通过 CIP Identity 获取）。
    /// </summary>
    public class MitsubishiDeviceInfo
    {
        /// <summary>产品名称。</summary>
        public string ProductName { get; set; } = string.Empty;
        /// <summary>厂商名称。</summary>
        public string VendorName { get; set; } = string.Empty;
        /// <summary>固件版本。</summary>
        public string FirmwareVersion { get; set; } = string.Empty;
        /// <summary>序列号。</summary>
        public uint SerialNumber { get; set; }
        /// <summary>设备类型。</summary>
        public ushort DeviceType { get; set; }
        /// <summary>产品代码。</summary>
        public ushort ProductCode { get; set; }
        /// <summary>设备状态。</summary>
        public ushort Status { get; set; }

        public override string ToString() => $"{ProductName} ({VendorName}) v{FirmwareVersion} SN:{SerialNumber:X8}";
    }
}
