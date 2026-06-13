using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Knx
{
    /// <summary>
    /// KNX/IP 楼宇自动化通讯客户端。
    /// <para>通过 UDP 组播与 KNX 网关通讯，支持 Group Read/Write 操作。</para>
    /// <para>KNXnet/IP 帧格式: Header(6) + Host Protocol(4) + Service Type + Data</para>
    /// <para>组地址格式: "1/2/3" (main/middle/sub)</para>
    /// </summary>
    public class KnxClient : UdpDeviceBase, IBatchReadWrite
    {
        private const byte KNX_PROTOCOL_VERSION = 0x10;
        private const ushort SERVICE_CONNECT_REQUEST = 0x0205;
        private const ushort SERVICE_CONNECT_RESPONSE = 0x0206;
        private const ushort SERVICE_DISCONNECT_REQUEST = 0x0209;
        private const ushort SERVICE_DISCONNECT_RESPONSE = 0x020A;
        private const ushort SERVICE_TUNNELING_REQUEST = 0x0420;
        private const ushort SERVICE_TUNNELING_ACK = 0x0421;
        private const ushort SERVICE_SEARCH_REQUEST = 0x0201;
        private const ushort SERVICE_SEARCH_RESPONSE = 0x0202;

        private const byte GROUP_READ = 0x00;
        private const byte GROUP_RESPONSE = 0x01;
        private const byte GROUP_WRITE = 0x00;

        protected override int ResponseHeaderLength => 6;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            int totalLength = (header[4] << 8) | header[5];
            return totalLength > 6 ? totalLength - 6 : 0;
        }

        public KnxClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout) { }

        public OperateResult<byte[]> GroupRead(string groupAddress)
        {
            var ga = ParseGroupAddress(groupAddress);
            if (ga == null)
                return OperateResult<byte[]>.Failed($"组地址格式错误: {groupAddress}");

            ushort gaVal = ga.Value;
            byte[] cemi = BuildCemiFrame(GROUP_READ, gaVal, new byte[] { 0x00 });
            byte[] request = BuildKnxFrame(SERVICE_TUNNELING_REQUEST, cemi);

            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            return ParseKnxResponse(result.Content, SERVICE_TUNNELING_REQUEST);
        }

        public OperateResult GroupWrite(string groupAddress, byte[] value)
        {
            var ga = ParseGroupAddress(groupAddress);
            if (ga == null)
                return OperateResult.Failed($"组地址格式错误: {groupAddress}");

            ushort gaVal = ga.Value;
            byte[] cemi = BuildCemiFrame(GROUP_WRITE, gaVal, value);
            byte[] request = BuildKnxFrame(SERVICE_TUNNELING_REQUEST, cemi);

            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);

            var parsed = ParseKnxResponse(result.Content, SERVICE_TUNNELING_REQUEST);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public OperateResult GroupWriteBool(string groupAddress, bool value)
            => GroupWrite(groupAddress, new byte[] { (byte)(value ? 0x01 : 0x00) });

        public OperateResult GroupWriteByte(string groupAddress, byte value)
            => GroupWrite(groupAddress, new byte[] { value });

        public OperateResult GroupWriteUShort(string groupAddress, ushort value)
            => GroupWrite(groupAddress, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public OperateResult<bool> GroupReadBool(string groupAddress)
        {
            var r = GroupRead(groupAddress);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content.Length > 0 && (r.Content[0] & 0x01) != 0);
        }

        public OperateResult<ushort> GroupReadUShort(string groupAddress)
        {
            var r = GroupRead(groupAddress);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<ushort>.Failed("响应数据不足 2 字节");
            return OperateResult<ushort>.Success((ushort)((r.Content[0] << 8) | r.Content[1]));
        }

        private byte[] BuildKnxFrame(ushort serviceType, byte[] data)
        {
            int totalLength = 6 + 4 + data.Length; // header(6) + host protocol(4) + data
            byte[] frame = new byte[totalLength];
            frame[0] = KNX_PROTOCOL_VERSION;
            frame[1] = 0x00;
            frame[2] = (byte)(serviceType >> 8);
            frame[3] = (byte)(serviceType & 0xFF);
            frame[4] = (byte)(totalLength >> 8);
            frame[5] = (byte)(totalLength & 0xFF);

            // Host Protocol Info
            frame[6] = 0x08;
            frame[7] = 0x01;
            // IP address and port filled by protocol stack
            frame[8] = 0x00;
            frame[9] = 0x00;

            Array.Copy(data, 0, frame, 10, data.Length);
            return frame;
        }

        private byte[] BuildCemiFrame(byte messageCode, ushort groupAddr, byte[] value)
        {
            byte[] cemi = new byte[11 + value.Length];
            cemi[0] = 0x11; // L_Data.req
            cemi[1] = 0x00; // additional info length
            cemi[2] = 0xBC; // control byte
            cemi[3] = 0xE0; // DAF=group, hops=6, prio=low
            cemi[4] = 0x00; // source address (filled by gateway)
            cemi[5] = 0x00;
            cemi[6] = (byte)(groupAddr >> 8);
            cemi[7] = (byte)(groupAddr & 0xFF);
            cemi[8] = (byte)(value.Length & 0x0F);
            Array.Copy(value, 0, cemi, 9, value.Length);
            return cemi;
        }

        private static OperateResult<byte[]> ParseKnxResponse(byte[] response, ushort expectedService)
        {
            if (response == null || response.Length < 10)
                return OperateResult<byte[]>.Failed("KNX 响应过短");

            ushort serviceType = (ushort)((response[2] << 8) | response[3]);
            if (serviceType == SERVICE_TUNNELING_ACK)
                return OperateResult<byte[]>.Success(new byte[0]);

            if (response.Length > 10)
            {
                byte[] data = new byte[response.Length - 10];
                Array.Copy(response, 10, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
            }

            return OperateResult<byte[]>.Success(new byte[0]);
        }

        private static ushort? ParseGroupAddress(string address)
        {
            if (string.IsNullOrEmpty(address)) return null;
            string[] parts = address.Split('/');
            if (parts.Length != 3) return null;

            if (!byte.TryParse(parts[0], out byte main) || main > 31) return null;
            if (!byte.TryParse(parts[1], out byte middle) || middle > 7) return null;
            if (!ushort.TryParse(parts[2], out ushort sub) || sub > 255) return null;

            return (ushort)((main << 11) | (middle << 8) | sub);
        }

        public override OperateResult<bool> ReadBool(string address)
            => GroupReadBool(address);

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = GroupReadUShort(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)r.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
            => GroupReadUShort(address);

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = GroupRead(address);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<int>.Failed("响应数据不足");
            if (r.Content.Length >= 4)
                return OperateResult<int>.Success(
                    (r.Content[0] << 24) | (r.Content[1] << 16) | (r.Content[2] << 8) | r.Content[3]);
            return OperateResult<int>.Success((r.Content[0] << 8) | r.Content[1]);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success((uint)r.Content);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success((long)r.Content);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success((ulong)r.Content);
        }

        public override OperateResult<float> ReadFloat(string address)
            => OperateResult<float>.Failed("KNX 协议不支持浮点读取");

        public override OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("KNX 协议不支持浮点读取");

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadUInt16(address);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(r.Content.ToString());
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
            => GroupRead(address);

        public override OperateResult Write(string address, bool value)
            => GroupWriteBool(address, value);

        public override OperateResult Write(string address, short value)
            => GroupWriteUShort(address, (ushort)value);

        public override OperateResult Write(string address, ushort value)
            => GroupWriteUShort(address, value);

        public override OperateResult Write(string address, int value)
            => GroupWrite(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, uint value)
            => GroupWrite(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, long value)
            => GroupWrite(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, ulong value)
            => GroupWrite(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("KNX 协议不支持浮点写入");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("KNX 协议不支持浮点写入");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("KNX 协议不支持字符串写入");

        public override OperateResult Write(string address, byte[] data)
            => GroupWrite(address, data);

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 0);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    long l => Write(kv.Key, l),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        public override string ToString() => $"KnxClient[{Ip}:{Port}]";
    }
}
