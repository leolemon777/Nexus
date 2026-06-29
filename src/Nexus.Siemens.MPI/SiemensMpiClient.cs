using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Siemens.MPI
{
    /// <summary>
    /// Siemens MPI 客户端 — 通过 RS-485 串口与 S7-200/300/400 PLC 通信。
    /// <para>协议基于 MPI (Multi-Point Interface)，支持 FDL 层连接和 S7 读写。</para>
    /// <para>地址格式: I0.0, Q0.0, M0.0, DB1.DBX0.0, T0, C0, V0</para>
    /// </summary>
    public class SiemensMpiClient : SerialDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public byte LocalAddress { get; set; } = 0;
        public byte RemoteAddress { get; set; } = 2;
        public ushort MaxPduSize { get; set; } = 480;

        private readonly MpiAddressParser _parser = new MpiAddressParser();
        private bool _connected;
        private ushort _pduLength;

        public SiemensMpiClient(ISerialPort port, byte remoteAddress = 2, int timeout = 5000)
            : base(port, timeout)
        {
            RemoteAddress = remoteAddress;
        }

        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            if (header[0] == 0x10) return 0; // SD1: fixed length
            if (header[0] == 0x68)
            {
                int le = header[1];
                return le - 4; // LE includes DA+SA+FC+PDU, minus header overhead
            }
            return 0;
        }

        // ═══════════════════════════════════════════
        //  MPI 帧构建
        // ═══════════════════════════════════════════

        /// <summary>构建 SD1 固定长度帧。</summary>
        private byte[] BuildSd1Frame(byte destination, byte source, byte functionCode)
        {
            byte[] frame = new byte[6];
            frame[0] = 0x10; // SD1
            frame[1] = destination;
            frame[2] = source;
            frame[3] = functionCode;
            frame[4] = (byte)(frame[1] + frame[2] + frame[3]); // FCS
            frame[5] = 0x16;
            return frame;
        }

        /// <summary>构建 SD2 可变长度帧。</summary>
        private byte[] BuildSd2Frame(byte destination, byte source, byte functionCode, byte[] data)
        {
            int dataLen = data.Length;
            int le = dataLen + 3; // DA + SA + FC + PDU
            byte[] frame = new byte[7 + dataLen + 2];
            frame[0] = 0x68; // SD2
            frame[1] = (byte)le;
            frame[2] = (byte)le;
            frame[3] = 0x68; // SD2 repeat
            frame[4] = destination;
            frame[5] = source;
            frame[6] = functionCode;
            Buffer.BlockCopy(data, 0, frame, 7, dataLen);
            byte fcs = 0;
            for (int i = 4; i < 7 + dataLen; i++) fcs += frame[i];
            frame[7 + dataLen] = fcs;
            frame[8 + dataLen] = 0x16;
            return frame;
        }

        /// <summary>计算 FCS (帧校验和)。</summary>
        private static byte ComputeFcs(byte[] data, int offset, int length)
        {
            byte fcs = 0;
            for (int i = offset; i < offset + length; i++) fcs += data[i];
            return fcs;
        }

        /// <summary>验证接收到的帧。</summary>
        private bool VerifyFrame(byte[] response, out byte functionCode, out byte[] data)
        {
            functionCode = 0;
            data = Array.Empty<byte>();

            if (response.Length < 6) return false;

            if (response[0] == 0xE5) // SC: Short Acknowledge
            {
                functionCode = 0xE5;
                return true;
            }

            if (response[0] == 0x10) // SD1
            {
                byte fcs = response[4];
                byte expectedFcs = (byte)(response[1] + response[2] + response[3]);
                if (fcs != expectedFcs) return false;
                if (response[5] != 0x16) return false;
                functionCode = response[3];
                return true;
            }

            if (response[0] == 0x68) // SD2
            {
                if (response[3] != 0x68) return false;
                int le = response[1];
                if (response[2] != le) return false;
                int dataLen = le - 3;
                if (response.Length < 7 + dataLen + 2) return false;

                byte fcs = response[7 + dataLen];
                byte expectedFcs = ComputeFcs(response, 4, 3 + dataLen);
                if (fcs != expectedFcs) return false;
                if (response[8 + dataLen] != 0x16) return false;

                functionCode = response[6];
                if (dataLen > 0)
                {
                    data = new byte[dataLen];
                    Buffer.BlockCopy(response, 7, data, 0, dataLen);
                }
                return true;
            }

            return false;
        }

        // ═══════════════════════════════════════════
        //  MPI 收发
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendMpi(byte[] frame)
        {
            var result = base.SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            if (!VerifyFrame(result.Content, out byte fc, out byte[] data))
                return OperateResult<byte[]>.Failed("MPI 帧校验失败");

            if (fc == 0xE5) // Short Acknowledge
                return OperateResult<byte[]>.Success(Array.Empty<byte>());

            return OperateResult<byte[]>.Success(data);
        }

        // ═══════════════════════════════════════════
        //  MPI 握手
        // ═══════════════════════════════════════════

        public override OperateResult Connect()
        {
            var baseResult = base.Connect();
            if (!baseResult.IsSuccess) return baseResult;

            var handshake = MpiHandshake();
            if (!handshake.IsSuccess) { Disconnect(); return handshake; }

            _connected = true;
            return OperateResult.Success();
        }

        private OperateResult MpiHandshake()
        {
            // Step 1: Send SC (Short Acknowledge) to synchronize
            byte[] sc = new byte[] { 0xE5 };
            var r1 = base.SendAndReceive(sc);
            if (!r1.IsSuccess) return OperateResult.Failed($"MPI 同步失败: {r1.Message}");

            // Step 2: FDL Status Request
            byte[] fdlRequest = BuildSd1Frame(RemoteAddress, LocalAddress, 0x00);
            var r2 = SendMpi(fdlRequest);
            if (!r2.IsSuccess) return OperateResult.Failed($"FDL 状态请求失败: {r2.Message}");

            // Step 3: MPI Setup (negotiate PDU)
            byte[] mpiSetup = BuildMpiSetupPdu();
            var r3 = SendMpi(mpiSetup);
            if (!r3.IsSuccess) return OperateResult.Failed($"MPI Setup 失败: {r3.Message}");

            // Parse negotiated PDU size
            if (r3.Content.Length >= 10)
            {
                MaxPduSize = (ushort)((r3.Content[8] << 8) | r3.Content[9]);
            }

            _pduLength = MaxPduSize;
            return OperateResult.Success();
        }

        private byte[] BuildMpiSetupPdu()
        {
            // MPI Setup: negotiate parameters
            byte[] data = new byte[]
            {
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                (byte)(MaxPduSize >> 8), (byte)(MaxPduSize & 0xFF),
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            };
            return BuildSd2Frame(RemoteAddress, LocalAddress, 0xF0, data);
        }

        // ═══════════════════════════════════════════
        //  S7 读写请求 (通过 MPI)
        // ═══════════════════════════════════════════

        private byte MpiAreaCode(MpiArea area) => area switch
        {
            MpiArea.I => 0x81, MpiArea.Q => 0x82, MpiArea.M => 0x83,
            MpiArea.DB => 0x84, MpiArea.T => 0x1D, MpiArea.C => 0x1C,
            MpiArea.V => 0x84, _ => 0x84
        };

        private OperateResult<byte[]> ReadRaw(MpiAddress addr, ushort byteCount)
        {
            if (!_connected) return OperateResult<byte[]>.Failed("未连接到 PLC");

            byte area = MpiAreaCode(addr.Area);
            ushort db = addr.Area == MpiArea.DB ? addr.DbNumber : (ushort)0;

            // S7 Read Request via MPI
            byte[] readReq = new byte[]
            {
                0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x01, 0x12,
                0x0A, 0x10, 0x02,
                (byte)(byteCount >> 8), (byte)(byteCount & 0xFF),
                (byte)(db >> 8), (byte)(db & 0xFF),
                area,
                (byte)(addr.StartByte >> 5), (byte)((addr.StartByte << 3) | addr.BitOffset)
            };

            byte[] mpiFrame = BuildSd2Frame(RemoteAddress, LocalAddress, 0xF0, readReq);
            var result = SendMpi(mpiFrame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            // Parse S7 response
            byte[] response = result.Content;
            if (response.Length < 18) return OperateResult<byte[]>.Failed("S7 响应过短");

            ushort error = (ushort)((response[10] << 8) | response[11]);
            if (error != 0) return OperateResult<byte[]>.Failed($"S7 错误: 0x{error:X4}");

            // Extract data (after S7 header)
            int dataOffset = response.Length - byteCount;
            if (dataOffset < 0) return OperateResult<byte[]>.Failed("S7 数据不足");

            byte[] data = new byte[byteCount];
            Buffer.BlockCopy(response, dataOffset, data, 0, byteCount);
            return OperateResult<byte[]>.Success(data);
        }

        private OperateResult WriteRaw(MpiAddress addr, byte[] data)
        {
            if (!_connected) return OperateResult.Failed("未连接到 PLC");

            byte area = MpiAreaCode(addr.Area);
            ushort db = addr.Area == MpiArea.DB ? addr.DbNumber : (ushort)0;

            byte[] writeReq = new byte[24 + data.Length];
            writeReq[0] = 0x00; writeReq[1] = 0x01;
            writeReq[6] = 0x00; writeReq[7] = (byte)(8 + data.Length);
            writeReq[12] = 0x00; writeReq[13] = 0x04;
            writeReq[14] = 0x01; writeReq[15] = 0x12;
            writeReq[16] = 0x0A; writeReq[17] = 0x10;
            writeReq[18] = 0x02;
            writeReq[19] = (byte)(data.Length >> 8); writeReq[20] = (byte)(data.Length & 0xFF);
            writeReq[21] = (byte)(db >> 8); writeReq[22] = (byte)(db & 0xFF);
            writeReq[23] = area;
            writeReq[24] = (byte)(addr.StartByte >> 5); writeReq[25] = (byte)((addr.StartByte << 3) | addr.BitOffset);
            Buffer.BlockCopy(data, 0, writeReq, 24, data.Length);

            byte[] mpiFrame = BuildSd2Frame(RemoteAddress, LocalAddress, 0xF0, writeReq);
            var result = SendMpi(mpiFrame);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message);
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        // ═══════════════════════════════════════════

        public override OperateResult<bool> ReadBool(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            return OperateResult<bool>.Success((r.Content[0] & (1 << addr.BitOffset)) != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var addr = _parser.Parse(address);
            var r = ReadRaw(addr, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
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
            var r = ReadRaw(addr, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
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
            var r = ReadRaw(addr, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message, r.ErrorCode);
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
            var r = ReadRaw(addr, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
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
            var r = ReadRaw(addr, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, 0, Math.Min(length, r.Content.Length)).TrimEnd('\0'));
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = _parser.Parse(address);
            return ReadRaw(addr, length);
        }

        // ── Write implementations ──────────────────

        public override OperateResult Write(string address, bool value)
        {
            var addr = _parser.Parse(address);
            var current = ReadRaw(addr, 1);
            if (!current.IsSuccess) return OperateResult.Failed(current.Message);
            byte b = current.Content[0];
            if (value) b |= (byte)(1 << addr.BitOffset);
            else b &= (byte)~(1 << addr.BitOffset);
            return WriteRaw(addr, new byte[] { b });
        }

        public override OperateResult Write(string address, short value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] { (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public override OperateResult Write(string address, int value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }

        public override OperateResult Write(string address, uint value) => Write(address, (int)value);

        public override OperateResult Write(string address, long value)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, new byte[] {
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
            return WriteRaw(addr, System.Text.Encoding.ASCII.GetBytes(value));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var addr = _parser.Parse(address);
            return WriteRaw(addr, data);
        }

        // ── Async (delegate to sync via Task.Run) ──

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

        // ── ISubscribeDevice ──────────────────────

        private readonly Dictionary<string, MpiSubscription> _subs = new Dictionary<string, MpiSubscription>();
        private readonly object _subLock = new object();
        private Timer? _timer;
        private bool _monitoring;

        private class MpiSubscription { public string Address = ""; public string DataType = "Int16"; public object? LastValue; }

        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        { lock (_subLock) { _subs[address] = new MpiSubscription { Address = address, DataType = dataType }; } }

        public void Unsubscribe(string address) { lock (_subLock) { _subs.Remove(address); } }

        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _timer = new Timer(Poll, null, globalIntervalMs, globalIntervalMs);
        }

        public void StopSubscriptions() { _monitoring = false; _timer?.Dispose(); _timer = null; }

        private void Poll(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MpiSubscription> entries;
                lock (_subLock) { entries = new List<MpiSubscription>(_subs.Values); }
                foreach (var e in entries)
                {
                    try
                    {
                        object? cur = e.DataType switch
                        {
                            "Int16" => ReadInt16(e.Address).Content,
                            "UInt16" => ReadUInt16(e.Address).Content,
                            "Int32" => ReadInt32(e.Address).Content,
                            "Float" => ReadFloat(e.Address).Content,
                            "Bool" => ReadBool(e.Address).Content,
                            _ => null
                        };
                        if (cur != null && !Equals(cur, e.LastValue))
                        {
                            if (e.LastValue == null) { e.LastValue = cur; continue; }
                            OnDataChanged?.Invoke(this, new DataChangeEventArgs { Address = e.Address, OldValue = e.LastValue, NewValue = cur, Timestamp = DateTime.Now, Quality = "Good" });
                            e.LastValue = cur;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }
}
