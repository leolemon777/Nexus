using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Omron
{
    /// <summary>
    /// 欧姆龙 HostLink C-Mode 协议客户端（TCP 模式）。
    /// <para>通过 TCP 连接发送 C-Mode ASCII 命令，帧格式与串口版相同。</para>
    /// <para>默认端口 9600。</para>
    /// </summary>
    public class OmronHostLinkCModeOverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        private const byte STX = (byte)'@';
        private const byte ETX = (byte)'*';
        private const byte CR  = 0x0D;

        public byte UnitNumber { get; set; } = 0;
        public int ReadSplits { get; set; } = 260;

        private static readonly OmronHostLinkCModeAddressParser _addressParser = new OmronHostLinkCModeAddressParser();

        protected override byte[]? BuildHeartbeat()
        {
            try
            {
                var addr = new OmronHostLinkCModeAddress("D0", CModeArea.DM, 0);
                return BuildReadCommand(addr, 0, 1);
            }
            catch { return null; }
        }

        protected override int ResponseHeaderLength => 1;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                bool wasConnected;
                lock (_lock) { wasConnected = IsConnected; }

                if (!wasConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                lock (_lock) { ns = _stream; }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                var ms = new MemoryStream();
                int b;
                while ((b = ns.ReadByte()) != -1)
                {
                    ms.WriteByte((byte)b);
                    if (b == CR) break;
                }

                if (ms.Length == 0)
                    return OperateResult<byte[]>.Failed("未收到 C-Mode 响应");

                byte[] response = ms.ToArray();

                Log.Debug($"RX ← {DataConverter.ToHexString(response)}");
                RaiseMessageReceived(DataConverter.ToHexString(response));

                if (!_persistentMode) lock (_lock) DisconnectCore();

                return OperateResult<byte[]>.Success(response);
            }
            catch (Exception ex)
            {
                Log.Error($"C-Mode 通讯异常 — {ex.Message}");
                RaiseError($"C-Mode 通讯异常 — {ex.Message}");
                if (!_persistentMode) lock (_lock) DisconnectCore();
                return OperateResult<byte[]>.Failed($"C-Mode 通讯异常: {ex.Message}");
            }
        }

        public OmronHostLinkCModeOverTcpClient(string ip, int port = 9600, int timeout = 5000)
            : base(ip, port, timeout) { }

        // 内部辅助实例，仅用于 BuildReadCommand 等帧构建方法
        private readonly OmronHostLinkCModeClient _cModeClientInstance = CreateDummyInstance();
        private static OmronHostLinkCModeClient CreateDummyInstance()
        {
            return new OmronHostLinkCModeClient(new DummySerialPort(), 0);
        }

        private byte[] BuildReadCommand(OmronHostLinkCModeAddress addr, ushort startWord, ushort wordCount)
        {
            _cModeClientInstance.UnitNumber = UnitNumber;
            return _cModeClientInstance.BuildReadCommand(addr, startWord, wordCount);
        }

        private byte[] BuildWriteCommand(OmronHostLinkCModeAddress addr, byte[] data)
        {
            _cModeClientInstance.UnitNumber = UnitNumber;
            return _cModeClientInstance.BuildWriteCommand(addr, data);
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _addressParser.Parse(address);
            ushort wordCount = (ushort)((length + 1) / 2);

            var result = new List<byte>();
            int remaining = wordCount;
            ushort currentWord = addr.WordAddress;

            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, ReadSplits);
                var frame = BuildReadCommand(addr, currentWord, (ushort)chunk);
                var recv = SendAndReceive(frame);
                if (!recv.IsSuccess) return OperateResult<byte[]>.Failed(recv.Message);

                var parsed = OmronHostLinkCModeClient.ParseResponse(recv.Content);
                if (!parsed.IsSuccess) return OperateResult<byte[]>.Failed(parsed.Message);

                result.AddRange(parsed.Content);
                currentWord += (ushort)chunk;
                remaining -= chunk;
            }

            byte[] final = result.ToArray();
            if (final.Length > length)
            {
                var trimmed = new byte[length];
                Array.Copy(final, 0, trimmed, 0, length);
                final = trimmed;
            }
            return OperateResult<byte[]>.Success(final);
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (data == null)
                return OperateResult.Failed("写入数据不能为空");

            var addr = _addressParser.Parse(address);
            var frame = BuildWriteCommand(addr, data);
            var recv = SendAndReceive(frame);
            if (!recv.IsSuccess) return OperateResult.Failed(recv.Message);

            var parsed = OmronHostLinkCModeClient.ParseResponse(recv.Content);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _addressParser.Parse(address);
            var word = ReadWordForBool(addr);
            if (!word.IsSuccess) return OperateResult<bool>.Failed(word.Message, word.ErrorCode);

            if (addr.BitOffset >= 0)
                return OperateResult<bool>.Success((word.Content & (1 << addr.BitOffset)) != 0);

            return OperateResult<bool>.Success(word.Content != 0);
        }

        public override OperateResult Write(string address, bool value)
        {
            var addr = _addressParser.Parse(address);
            if (addr.BitOffset >= 0)
            {
                var word = ReadWordForBool(addr);
                if (!word.IsSuccess) return OperateResult.Failed(word.Message, word.ErrorCode);

                ushort mask = (ushort)(1 << addr.BitOffset);
                ushort updated = value
                    ? (ushort)(word.Content | mask)
                    : (ushort)(word.Content & ~mask);

                return Write(ToWordAddress(addr).ToString(), ToWordBytes(updated));
            }

            return Write(address, new byte[] { 0, (byte)(value ? 1 : 0) });
        }

        private OperateResult<ushort> ReadWordForBool(OmronHostLinkCModeAddress addr)
        {
            var r = ReadBytes(ToWordAddress(addr).ToString(), 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
            if (r.Content.Length < 2)
                return OperateResult<ushort>.Failed($"C-Mode 位读取响应长度不足: {r.Content.Length} 字节");

            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        private static OmronHostLinkCModeAddress ToWordAddress(OmronHostLinkCModeAddress addr)
        {
            return addr.BitOffset < 0
                ? addr
                : new OmronHostLinkCModeAddress(addr.Original, addr.Area, addr.WordAddress, -1, addr.EmBank);
        }

        private static byte[] ToWordBytes(ushort value)
        {
            return new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) };
        }

        public override OperateResult<short> ReadInt16(string address)
        { var r = ReadBytes(address, 2); return r.IsSuccess ? OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0)) : OperateResult<short>.Failed(r.Message); }

        public override OperateResult<ushort> ReadUInt16(string address)
        { var r = ReadBytes(address, 2); return r.IsSuccess ? OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0)) : OperateResult<ushort>.Failed(r.Message); }

        public override OperateResult<int> ReadInt32(string address)
        { var r = ReadBytes(address, 4); return r.IsSuccess ? OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0)) : OperateResult<int>.Failed(r.Message); }

        public override OperateResult<uint> ReadUInt32(string address)
        { var r = ReadInt32(address); return r.IsSuccess ? OperateResult<uint>.Success((uint)r.Content) : OperateResult<uint>.Failed(r.Message); }

        public override OperateResult<long> ReadInt64(string address)
        { var r = ReadBytes(address, 8); return r.IsSuccess ? OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0)) : OperateResult<long>.Failed(r.Message); }

        public override OperateResult<ulong> ReadUInt64(string address)
        { var r = ReadInt64(address); return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message); }

        public override OperateResult<float> ReadFloat(string address)
        { var r = ReadBytes(address, 4); return r.IsSuccess ? OperateResult<float>.Success(BitConverter.ToSingle(BitConverter.GetBytes(DataConverter.ToInt32(r.Content, 0)), 0)) : OperateResult<float>.Failed(r.Message); }

        public override OperateResult<double> ReadDouble(string address)
        { var r = ReadBytes(address, 8); return r.IsSuccess ? OperateResult<double>.Success(BitConverter.ToDouble(BitConverter.GetBytes(DataConverter.ToInt64(r.Content, 0)), 0)) : OperateResult<double>.Failed(r.Message); }

        public override OperateResult<string> ReadString(string address, ushort length)
        { var r = ReadBytes(address, length); return r.IsSuccess ? OperateResult<string>.Success(Encoding.ASCII.GetString(r.Content).TrimEnd('\0')) : OperateResult<string>.Failed(r.Message); }

        public override OperateResult Write(string address, short value) => Write(address, new byte[] { (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);
        public override OperateResult Write(string address, int value) => Write(address, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, uint value) => Write(address, (int)value);
        public override OperateResult Write(string address, long value) => Write(address, new byte[] { (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)(value & 0xFF) });
        public override OperateResult Write(string address, ulong value) => Write(address, (long)value);
        public override OperateResult Write(string address, float value) { int bits = BitConverter.ToInt32(BitConverter.GetBytes(value), 0); return Write(address, new byte[] { (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, double value) { long bits = BitConverter.DoubleToInt64Bits(value); return Write(address, new byte[] { (byte)(bits >> 56), (byte)(bits >> 48), (byte)(bits >> 40), (byte)(bits >> 32), (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)(bits & 0xFF) }); }
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value ?? string.Empty));

        public override string ToString() => $"OmronHostLinkCModeTcp[{Ip}:{Port}]";

        // ═══════════════════════════════════════════
        //  IBatchReadWrite
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
            => Task.Run(() => BatchRead(addresses), cancellationToken);

        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 2);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.Run(() => RandomRead(addresses), cancellationToken);

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
                    float f => Write(kv.Key, f),
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
            => Task.Run(() => BatchWrite(items), cancellationToken);

        /// <summary>
        /// 虚拟串口，用于 TCP 客户端中复用 CModeClient 的帧构建方法。
        /// </summary>
        private class DummySerialPort : ISerialPort
        {
            public string PortName { get; set; } = "";
            public int BaudRate { get; set; }
            public int DataBits { get; set; }
            public StopBits StopBits { get; set; }
            public Parity Parity { get; set; }
            public int ReadTimeout { get; set; }
            public int WriteTimeout { get; set; }
            public bool IsOpen => false;
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }
            public void Open() { }
            public void Close() { }
            public int Read(byte[] buffer, int offset, int count) => 0;
            public void Write(byte[] buffer, int offset, int count) { }
            public void Dispose() { }
        }
    }
}
