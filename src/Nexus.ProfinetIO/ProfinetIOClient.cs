using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.ProfinetIO
{
    /// <summary>
    /// Profinet IO 客户端 — 支持设备发现、参数读写、IO 数据交换、诊断。
    /// <para>协议基于 Ethernet + RPC (ONC RPC over TCP)。</para>
    /// <para>默认端口: 34964 (TCP/UDP)</para>
    /// <para>地址格式: API:Slot:Subslot:Offset 或 Slot:Offset</para>
    /// </summary>
    public class ProfinetIOClient : TcpDeviceBase, IBatchReadWrite
    {
        public ushort DeviceId { get; set; }
        public ushort VendorId { get; set; }
        public string DeviceName { get; set; } = "";

        private readonly ProfinetAddressParser _parser = new ProfinetAddressParser();
        private uint _sessionHandle;

        /// <summary>
        /// 创建 Profinet IO 客户端。
        /// </summary>
        /// <param name="ip">设备 IP 地址。</param>
        /// <param name="port">端口（默认 34964）。</param>
        /// <param name="timeout">超时（毫秒）。</param>
        public ProfinetIOClient(string ip, int port = 34964, int timeout = 5000)
            : base(ip, port, timeout) { }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return (header[2] << 8) | header[3];
        }

        // ═══════════════════════════════════════════
        //  设备发现 (DCP)
        // ═══════════════════════════════════════════

        /// <summary>
        /// 发现网络上的 Profinet IO 设备。
        /// <para>通过 UDP 广播发送 DCP Identify 请求。</para>
        /// </summary>
        /// <param name="broadcastIp">广播地址（默认 255.255.255.255）。</param>
        /// <param name="timeoutMs">超时（毫秒，默认 3000）。</param>
        /// <returns>发现的设备列表。</returns>
        public static List<ProfinetDevice> DiscoverDevices(string broadcastIp = "255.255.255.255", int timeoutMs = 3000)
        {
            var devices = new List<ProfinetDevice>();

            try
            {
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;

                    // DCP Identify Request (Ethernet frame over UDP)
                    // Frame ID: 0xFEFE (DCP Identify Request)
                    byte[] dcpFrame = BuildDcpIdentifyRequest();

                    udp.Send(dcpFrame, dcpFrame.Length, new IPEndPoint(IPAddress.Parse(broadcastIp), 34964));

                    var endpoint = new IPEndPoint(IPAddress.Any, 0);
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                    while (DateTime.UtcNow < deadline)
                    {
                        if (udp.Available > 0)
                        {
                            byte[] response = udp.Receive(ref endpoint);
                            var device = ParseDcpIdentifyResponse(response, endpoint.Address.ToString());
                            if (device != null)
                                devices.Add(device);
                        }
                        else
                        {
                            Thread.Sleep(10);
                        }
                    }
                }
            }
            catch
            {
                // Discovery failures are non-fatal
            }

            return devices;
        }

        /// <summary>异步发现设备。</summary>
        public static async Task<List<ProfinetDevice>> DiscoverDevicesAsync(string broadcastIp = "255.255.255.255", int timeoutMs = 3000, CancellationToken ct = default)
        {
            return await Task.Run(() => DiscoverDevices(broadcastIp, timeoutMs), ct).ConfigureAwait(false);
        }

        private static byte[] BuildDcpIdentifyRequest()
        {
            // DCP Identify Request over UDP
            // Ethernet header (14) + Profinet header (4) + DCP header (10) + DCP data
            byte[] frame = new byte[64];

            // Destination MAC: Broadcast
            frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0xFF;
            frame[3] = 0xFF; frame[4] = 0xFF; frame[5] = 0xFF;

            // Source MAC: Use local MAC (simplified - use zeros)
            // In real implementation, get from NetworkInterface

            // EtherType: 0x8892 (Profinet)
            frame[12] = 0x88; frame[13] = 0x92;

            // Profinet header
            frame[14] = 0xFE; frame[15] = 0xFE; // Frame ID: DCP Identify
            frame[16] = 0x00; frame[17] = 0x00; // DCP service ID + service type

            // DCP header
            frame[18] = 0x01; // DCP service: Identify
            frame[19] = 0x00; // DCP service type: Request
            frame[20] = 0x00; frame[21] = 0x00; // XID (transaction ID)
            frame[22] = 0x00; frame[23] = 0x00; // Reserved
            frame[24] = 0x00; frame[25] = 0x00; // DCP data length

            return frame;
        }

        private static ProfinetDevice? ParseDcpIdentifyResponse(byte[] response, string sourceIp)
        {
            if (response.Length < 30) return null;

            // Check EtherType: 0x8892
            if (response[12] != 0x88 || response[13] != 0x92) return null;

            var device = new ProfinetDevice
            {
                IpAddress = sourceIp,
                MacAddress = new byte[6]
            };

            // Extract source MAC
            Buffer.BlockCopy(response, 6, device.MacAddress, 0, 6);

            // Parse DCP options (simplified)
            int offset = 26;
            while (offset + 4 < response.Length)
            {
                byte option = response[offset];
                byte suboption = response[offset + 1];
                ushort length = (ushort)((response[offset + 2] << 8) | response[offset + 3]);

                if (option == 0x02 && suboption == 0x01) // Device properties: Name of station
                {
                    if (length > 0 && offset + 4 + length <= response.Length)
                    {
                        device.DeviceName = System.Text.Encoding.ASCII.GetString(response, offset + 4, length).TrimEnd('\0');
                    }
                }
                else if (option == 0x02 && suboption == 0x02) // Device properties: Device ID
                {
                    if (length >= 2 && offset + 4 + 2 <= response.Length)
                    {
                        device.DeviceId = (ushort)((response[offset + 4] << 8) | response[offset + 5]);
                    }
                }

                offset += 4 + length;
                if (length % 2 != 0) offset++; // Padding
            }

            return device;
        }

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            // Profinet IO uses RPC over TCP
            // Send RPC Bind request
            var bindResult = RpcBind();
            if (!bindResult.IsSuccess) { Disconnect(); return bindResult; }

            return OperateResult.Success();
        }

        private OperateResult RpcBind()
        {
            // RPC Bind to Profinet IO Context Manager (UUID: DEA00001-6C97-11D1-8271-00A02442DF7D)
            byte[] bindRequest = BuildRpcBindRequest();
            var result = SendAndReceive(bindRequest);
            if (!result.IsSuccess) return OperateResult.Failed($"RPC Bind 失败: {result.Message}");

            // Parse bind response to get session handle
            if (result.Content.Length >= 20)
            {
                _sessionHandle = (uint)(result.Content[16] | (result.Content[17] << 8) |
                    (result.Content[18] << 16) | (result.Content[19] << 24));
            }

            return OperateResult.Success();
        }

        private byte[] BuildRpcBindRequest()
        {
            // Simplified RPC Bind request for Profinet IO
            byte[] request = new byte[64];

            // RPC header
            request[0] = 0x04; // Version
            request[1] = 0x00; // Packet type: Bind
            request[2] = 0x00; request[3] = 0x00; // Flags
            request[4] = 0x00; request[5] = 0x00; request[6] = 0x00; request[7] = 0x00; // Data representation
            request[8] = 0x10; request[9] = 0x00; request[10] = 0x00; request[11] = 0x00; // Fragment length
            request[12] = 0x00; request[13] = 0x00; request[14] = 0x00; request[15] = 0x00; // Auth length

            // Profinet IO Context Manager UUID
            // DEA00001-6C97-11D1-8271-00A02442DF7D
            request[16] = 0x01; request[17] = 0x00; request[18] = 0xA0; request[19] = 0xDE;
            request[20] = 0x97; request[21] = 0x6C; request[22] = 0xD1; request[23] = 0x11;
            request[24] = 0x82; request[25] = 0x71; request[26] = 0x00; request[27] = 0xA0;
            request[28] = 0x24; request[29] = 0x42; request[30] = 0xDF; request[31] = 0x7D;

            return request;
        }

        // ═══════════════════════════════════════════
        //  记录数据读写 (IODReadReq / IODWriteReq)
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadRecordData(ushort api, ushort slot, ushort subslot, ushort index, ushort length)
        {
            // Build IODReadReq
            byte[] request = BuildIODReadReq(api, slot, subslot, index, length);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            byte[] response = result.Content;
            if (response.Length < 30) return OperateResult<byte[]>.Failed("Profinet IO 响应过短");

            // Check response code (offset 26-27)
            ushort responseCode = (ushort)((response[26] << 8) | response[27]);
            if (responseCode != 0)
                return OperateResult<byte[]>.Failed($"Profinet IO 错误: 0x{responseCode:X4}");

            // Extract data
            int dataLength = (response[28] << 8) | response[29];
            if (dataLength > 0 && response.Length >= 30 + dataLength)
            {
                byte[] data = new byte[dataLength];
                Buffer.BlockCopy(response, 30, data, 0, dataLength);
                return OperateResult<byte[]>.Success(data);
            }

            return OperateResult<byte[]>.Success(Array.Empty<byte>());
        }

        private OperateResult WriteRecordData(ushort api, ushort slot, ushort subslot, ushort index, byte[] data)
        {
            byte[] request = BuildIODWriteReq(api, slot, subslot, index, data);
            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);

            byte[] response = result.Content;
            if (response.Length < 28) return OperateResult.Failed("Profinet IO 响应过短");

            ushort responseCode = (ushort)((response[26] << 8) | response[27]);
            if (responseCode != 0)
                return OperateResult.Failed($"Profinet IO 写入错误: 0x{responseCode:X4}");

            return OperateResult.Success();
        }

        private byte[] BuildIODReadReq(ushort api, ushort slot, ushort subslot, ushort index, ushort length)
        {
            byte[] request = new byte[40];

            // RPC header
            request[0] = 0x04; // Version
            request[1] = 0x00; // Packet type: Request
            request[8] = 0x28; request[9] = 0x00; // Fragment length (40)

            // Profinet IO service: IODReadReq (0x0008)
            request[16] = 0x08; request[17] = 0x00;

            // Block header
            request[18] = 0x00; request[19] = 0x00; // Block type: IODReadReq
            request[20] = 0x00; request[21] = 0x1A; // Block length (26)

            // IODReadReq data
            request[22] = (byte)(api >> 8); request[23] = (byte)api;
            request[24] = (byte)(slot >> 8); request[25] = (byte)slot;
            request[26] = (byte)(subslot >> 8); request[27] = (byte)subslot;
            request[28] = 0x00; request[29] = 0x00; // Padding
            request[30] = (byte)(index >> 8); request[31] = (byte)index;
            request[32] = (byte)(length >> 8); request[33] = (byte)length;
            request[34] = 0x00; request[35] = 0x00; // Sequence number
            request[36] = 0x00; request[37] = 0x00; // Padding
            request[38] = 0x00; request[39] = 0x00; // Padding

            return request;
        }

        private byte[] BuildIODWriteReq(ushort api, ushort slot, ushort subslot, ushort index, byte[] data)
        {
            int totalLength = 40 + data.Length;
            byte[] request = new byte[totalLength];

            // RPC header
            request[0] = 0x04; // Version
            request[1] = 0x00; // Packet type: Request
            request[8] = (byte)(totalLength >> 8); request[9] = (byte)totalLength;

            // Profinet IO service: IODWriteReq (0x0009)
            request[16] = 0x09; request[17] = 0x00;

            // Block header
            request[18] = 0x00; request[19] = 0x01; // Block type: IODWriteReq
            request[20] = (byte)((26 + data.Length) >> 8); request[21] = (byte)(26 + data.Length);

            // IODWriteReq data
            request[22] = (byte)(api >> 8); request[23] = (byte)api;
            request[24] = (byte)(slot >> 8); request[25] = (byte)slot;
            request[26] = (byte)(subslot >> 8); request[27] = (byte)subslot;
            request[28] = 0x00; request[29] = 0x00;
            request[30] = (byte)(index >> 8); request[31] = (byte)index;
            request[32] = (byte)(data.Length >> 8); request[33] = (byte)data.Length;
            request[34] = 0x00; request[35] = 0x00; // Sequence number
            request[36] = 0x00; request[37] = 0x00;
            request[38] = 0x00; request[39] = 0x00;

            // Data
            Buffer.BlockCopy(data, 0, request, 40, data.Length);

            return request;
        }

        // ═══════════════════════════════════════════
        //  设备信息
        // ═══════════════════════════════════════════

        /// <summary>读取设备标识信息。</summary>
        public OperateResult<ProfinetDevice> ReadDeviceIdentity()
        {
            // Read DeviceIdent (API=0, Slot=0, Subslot=1, Index=0x0001)
            var result = ReadRecordData(0, 0, 1, 0x0001, 64);
            if (!result.IsSuccess) return OperateResult<ProfinetDevice>.Failed(result.Message);

            var device = new ProfinetDevice
            {
                IpAddress = Ip,
                MacAddress = new byte[6]
            };

            if (result.Content.Length >= 4)
            {
                device.VendorId = (ushort)((result.Content[0] << 8) | result.Content[1]);
                device.DeviceId = (ushort)((result.Content[2] << 8) | result.Content[3]);
            }

            return OperateResult<ProfinetDevice>.Success(device);
        }

        /// <summary>读取设备名称。</summary>
        public OperateResult<string> ReadDeviceName()
        {
            var result = ReadRecordData(0, 0, 1, 0x0002, 256);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(result.Content).TrimEnd('\0'));
        }

        /// <summary>读取模块列表。</summary>
        public OperateResult<List<ProfinetModule>> ReadModuleList()
        {
            var modules = new List<ProfinetModule>();

            // Read module list from API 0, Slot 0, Subslot 1, Index 0x000F
            var result = ReadRecordData(0, 0, 1, 0x000F, 1024);
            if (!result.IsSuccess) return OperateResult<List<ProfinetModule>>.Failed(result.Message);

            byte[] data = result.Content;
            int offset = 0;
            while (offset + 8 <= data.Length)
            {
                var module = new ProfinetModule
                {
                    SlotNumber = (ushort)((data[offset] << 8) | data[offset + 1]),
                    ModuleId = (ushort)((data[offset + 2] << 8) | data[offset + 3]),
                    InputLength = (ushort)((data[offset + 4] << 8) | data[offset + 5]),
                    OutputLength = (ushort)((data[offset + 6] << 8) | data[offset + 7])
                };
                modules.Add(module);
                offset += 8;
            }

            return OperateResult<List<ProfinetModule>>.Success(modules);
        }

        /// <summary>读取诊断信息。</summary>
        public OperateResult<List<ProfinetDiagnosis>> ReadDiagnosis(ushort slot, ushort subslot)
        {
            var diagnoses = new List<ProfinetDiagnosis>();

            // Read diagnosis data (Index 0x0008)
            var result = ReadRecordData(0, slot, subslot, 0x0008, 1024);
            if (!result.IsSuccess) return OperateResult<List<ProfinetDiagnosis>>.Failed(result.Message);

            byte[] data = result.Content;
            int offset = 0;
            while (offset + 12 <= data.Length)
            {
                var diag = new ProfinetDiagnosis
                {
                    SlotNumber = slot,
                    SubslotNumber = subslot,
                    AlarmType = (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]),
                    ErrorCode = (uint)((data[offset + 4] << 24) | (data[offset + 5] << 16) | (data[offset + 6] << 8) | data[offset + 7]),
                    Timestamp = DateTime.Now
                };
                diagnoses.Add(diag);
                offset += 12;
            }

            return OperateResult<List<ProfinetDiagnosis>>.Success(diagnoses);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        //  通过 Profinet IO 记录数据读写实现标准接口
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success(r.Content.Length > 0 && (r.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("数据不足");
            return OperateResult<short>.Success((short)((r.Content[0] << 8) | r.Content[1]));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("数据不足");
            return OperateResult<int>.Success((r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 8) return OperateResult<long>.Failed("数据不足");
            return OperateResult<long>.Success(
                ((long)r.Content[0] << 56) | ((long)r.Content[1] << 48) | ((long)r.Content[2] << 40) | ((long)r.Content[3] << 32) |
                ((long)r.Content[4] << 24) | ((long)r.Content[5] << 16) | ((long)r.Content[6] << 8) | r.Content[7]);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt64(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 4) return OperateResult<float>.Failed("数据不足");
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadInt64(address);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message, r.ErrorCode);
            return OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(r.Content), 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            var r = ReadRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, length);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, new byte[] { (byte)(value ? 0x01 : 0x00) });
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, new byte[] { (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = _parser.Parse(address);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = _parser.Parse(address);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, new byte[] {
                (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
                (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
            });
        }

        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);

        public override OperateResult Write(string address, float value)
        {
            int bits;
            unsafe { bits = *(int*)&value; }
            return Write(address, bits);
        }

        public override OperateResult Write(string address, double value) => Write(address, BitConverter.DoubleToInt64Bits(value));

        public override OperateResult Write(string address, string value)
        {
            var addr = _parser.Parse(address);
            byte[] data = System.Text.Encoding.ASCII.GetBytes(value);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            return WriteRecordData(addr.Api, addr.Slot, addr.Subslot, addr.Offset, data);
        }

        // ── Async ──────────────────────

        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ── IBatchReadWrite ──────────────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList) { var r = ReadInt16(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList) { var r = ReadBytes(addr, 1); if (!r.IsSuccess) return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            foreach (var kv in items)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b), short s => Write(kv.Key, s), ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i), uint ui => Write(kv.Key, ui), float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s), byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
