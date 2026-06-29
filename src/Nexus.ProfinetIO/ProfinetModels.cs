using System;
using Nexus;

namespace Nexus.ProfinetIO
{
    /// <summary>
    /// Profinet IO 设备发现结果。
    /// </summary>
    public sealed class ProfinetDevice
    {
        /// <summary>设备 MAC 地址。</summary>
        public byte[] MacAddress { get; set; } = Array.Empty<byte>();

        /// <summary>设备 IP 地址。</summary>
        public string IpAddress { get; set; } = "";

        /// <summary>设备名称。</summary>
        public string DeviceName { get; set; } = "";

        /// <summary>设备 ID。</summary>
        public ushort DeviceId { get; set; }

        /// <summary>供应商 ID。</summary>
        public ushort VendorId { get; set; }

        /// <summary>设备类型。</summary>
        public string DeviceType { get; set; } = "";

        public override string ToString() => $"{DeviceName} ({IpAddress}) [{BitConverter.ToString(MacAddress)}]";
    }

    /// <summary>
    /// Profinet IO 模块信息。
    /// </summary>
    public sealed class ProfinetModule
    {
        public ushort SlotNumber { get; set; }
        public ushort ModuleId { get; set; }
        public string ModuleName { get; set; } = "";
        public ushort InputLength { get; set; }
        public ushort OutputLength { get; set; }
    }

    /// <summary>
    /// Profinet IO 诊断信息。
    /// </summary>
    public sealed class ProfinetDiagnosis
    {
        public ushort SlotNumber { get; set; }
        public ushort SubslotNumber { get; set; }
        public uint AlarmType { get; set; }
        public uint ErrorCode { get; set; }
        public string Description { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Profinet IO 地址 — 用于标识 IO 数据的读写位置。
    /// </summary>
    public sealed class ProfinetAddress : IDataAddress
    {
        public string Original { get; }
        public ushort Api { get; }
        public ushort Slot { get; }
        public ushort Subslot { get; }
        public ushort Offset { get; }
        public ushort Length { get; }

        public ProfinetAddress(string original, ushort api, ushort slot, ushort subslot, ushort offset, ushort length)
        {
            Original = original;
            Api = api;
            Slot = slot;
            Subslot = subslot;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// Profinet IO 地址解析器。
    /// <para>地址格式: API:Slot:Subslot:Offset 或 Slot:Offset</para>
    /// <para>示例: "0:1:0:0" (API0, Slot1, Subslot0, Offset0)</para>
    /// </summary>
    public sealed class ProfinetAddressParser : IAddressParser<ProfinetAddress>
    {
        public ProfinetAddress Parse(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new AddressParseException(address, "地址不能为空");

            string original = address;
            address = address.Trim();

            string[] parts = address.Split(':');

            if (parts.Length == 4)
            {
                ushort api = ushort.Parse(parts[0]);
                ushort slot = ushort.Parse(parts[1]);
                ushort subslot = ushort.Parse(parts[2]);
                ushort offset = ushort.Parse(parts[3]);
                return new ProfinetAddress(original, api, slot, subslot, offset, 1);
            }

            if (parts.Length == 2)
            {
                ushort slot = ushort.Parse(parts[0]);
                ushort offset = ushort.Parse(parts[1]);
                return new ProfinetAddress(original, 0, slot, 0, offset, 1);
            }

            if (parts.Length == 3)
            {
                ushort slot = ushort.Parse(parts[0]);
                ushort subslot = ushort.Parse(parts[1]);
                ushort offset = ushort.Parse(parts[2]);
                return new ProfinetAddress(original, 0, slot, subslot, offset, 1);
            }

            throw new AddressParseException(address, "Profinet IO 地址格式: [API:]Slot[:Subslot]:Offset");
        }

        public bool TryParse(string address, out ProfinetAddress? parsed)
        {
            try { parsed = Parse(address); return true; }
            catch { parsed = null; return false; }
        }
    }
}
