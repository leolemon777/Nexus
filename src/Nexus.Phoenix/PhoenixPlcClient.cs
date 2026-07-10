using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Phoenix
{
    /// <summary>
    /// Phoenix Contact AXC PLC TCP 客户端 — 继承 TcpDeviceBase，复用连接管理。
    /// </summary>
    /// <remarks>
    /// <para>Phoenix Contact AXC 系列 PLC（AXC F 2152 / AXC F 3152 等，运行 PLCnext Technology）
    /// 原生作为标准 Modbus TCP Server（PLCnext 工程中配置并映射变量）。</para>
    /// <para>本客户端封装 IEC 61131-3 地址（%MW/%IW/%QW/%IX/%QX/%M）到标准 Modbus TCP 的转换。</para>
    /// <para>默认端口 502，默认站号 1。</para>
    /// <para><b>协议本质</b>：Modbus TCP variant（非 Phoenix 私有协议）。</para>
    /// <para><b>不支持</b>：<c>%IB</c> / <c>%QB</c> 字节寻址。Phoenix PLCnext 的字节寻址无固定 Modbus 映射，
    /// 请在 PLCnext 程序侧显式映射到寄存器/线圈后访问。</para>
    /// </remarks>
    public class PhoenixPlcClient : TcpDeviceBase, IBatchReadWrite
    {
        /// <summary>站号（Modbus Unit ID，默认 1）。</summary>
        public byte Station { get; set; } = 1;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 7;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 7) return 0;
            int respLen = (header[4] << 8) | header[5];
            return respLen - 1;
        }

        private static int _transactionId;

        private byte[] BuildMbapFrame(byte[] pdu)
        {
            ushort tid = unchecked((ushort)Interlocked.Increment(ref _transactionId));
            int len = pdu.Length + 1;
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = (byte)(tid >> 8);
            frame[1] = (byte)tid;
            frame[2] = 0x00;
            frame[3] = 0x00;
            frame[4] = (byte)(len >> 8);
            frame[5] = (byte)len;
            frame[6] = Station;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);
            return frame;
        }

        /// <summary>
        /// 创建 Phoenix Contact AXC PLC TCP 客户端实例。
        /// </summary>
        /// <param name="ip">PLC IP 地址。</param>
        /// <param name="port">端口号（默认 502）。</param>
        /// <param name="station">Modbus Unit ID（默认 1）。</param>
        /// <param name="timeout">超时时间（毫秒，默认 5000）。</param>
        public PhoenixPlcClient(string ip, int port = 502, byte station = 1, int timeout = 5000)
            : base(ip, port, timeout)
        {
            Station = station;
        }

        // ── 帧构造（静态，便于离线测试） ──

        public static byte[] BuildReadPdu(ushort startAddr, byte function, ushort count)
        {
            return new byte[]
            {
                function,
                (byte)(startAddr >> 8), (byte)startAddr,
                (byte)(count >> 8), (byte)count
            };
        }

        public static byte[] BuildWriteSingleCoilPdu(ushort addr, bool value)
        {
            return new byte[]
            {
                0x05,
                (byte)(addr >> 8), (byte)addr,
                (byte)(value ? 0xFF : 0x00), 0x00
            };
        }

        public static byte[] BuildWriteMultipleRegistersPdu(ushort startAddr, byte[] data)
        {
            ushort wordCount = (ushort)(data.Length / 2);
            byte byteCount = (byte)data.Length;
            byte[] pdu = new byte[6 + data.Length];
            pdu[0] = 0x10;
            pdu[1] = (byte)(startAddr >> 8);
            pdu[2] = (byte)startAddr;
            pdu[3] = (byte)(wordCount >> 8);
            pdu[4] = (byte)wordCount;
            pdu[5] = byteCount;
            Buffer.BlockCopy(data, 0, pdu, 6, data.Length);
            return pdu;
        }

        // ── IReadWriteDevice ──

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = PhoenixAddress.TryParse(address);
            if (addr == null)
                return OperateResult<byte[]>.Failed($"无法解析 Phoenix 地址: {address}");

            byte[] pdu = BuildReadPdu(addr.Address, addr.ReadFunctionCode, length);
            byte[] frame = BuildMbapFrame(pdu);

            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 10)
                return OperateResult<byte[]>.Failed("响应长度不足");

            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult<byte[]>.Failed($"Modbus异常: 0x{errCode:X2}", errCode);
            }

            int byteCount = resp[8];
            if (resp.Length < 9 + byteCount)
                return OperateResult<byte[]>.Failed("响应数据长度不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(resp, 9, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null) return OperateResult.Failed("写入数据不能为空");

            var addr = PhoenixAddress.TryParse(address);
            if (addr == null) return OperateResult.Failed($"无法解析 Phoenix 地址: {address}");
            if (addr.WriteFunctionCode == 0)
                return OperateResult.Failed($"地址 {address} 为只读区域");

            byte[] pdu = BuildWriteMultipleRegistersPdu(addr.Address, data);
            byte[] frame = BuildMbapFrame(pdu);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12) return OperateResult.Failed("写入响应长度不足");
            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult.Failed($"Modbus异常: 0x{errCode:X2}", errCode);
            }
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            return OperateResult<bool>.Success((result.Content[0] & 0x01) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var result = ReadBytes(address, 1);
            if (!result.IsSuccess) return OperateResult<short>.Failed(result.Message, result.ErrorCode);
            return OperateResult<short>.Success(DataConverter.ToInt16(result.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        { var r = ReadInt16(address); return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message); }

        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => DataConverter.ToInt32(d, 0));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => DataConverter.ToUInt32(d, 0));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => DataConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => DataConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => DataConverter.ToFloat(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => DataConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message, result.ErrorCode);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = PhoenixAddress.TryParse(address);
            if (addr == null) return OperateResult.Failed($"无法解析 Phoenix 地址: {address}");
            if (addr.WriteFunctionCode == 0)
                return OperateResult.Failed($"地址 {address} 为只读区域");

            byte[] pdu = BuildWriteSingleCoilPdu(addr.Address, value);
            var result = SendAndReceive(BuildMbapFrame(pdu));
            if (!result.IsSuccess) return result;

            byte[] resp = result.Content;
            if (resp == null || resp.Length < 12) return OperateResult.Failed("写入响应长度不足");
            if ((resp[7] & 0x80) != 0)
            {
                byte errCode = resp.Length > 8 ? resp[8] : (byte)0;
                return OperateResult.Failed($"Modbus异常: 0x{errCode:X2}", errCode);
            }
            return OperateResult.Success();
        }

        public override OperateResult Write(string address, short value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, DataConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

        // ── IBatchReadWrite ──

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
                var r = ReadBytes(addr, 1);
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
                    ulong ul => Write(kv.Key, ul),
                    float f => Write(kv.Key, f),
                    double d => Write(kv.Key, d),
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

        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildMbapFrame(BuildReadPdu(0, 0x03, 1)); }
            catch { return null; }
        }
    }
}
