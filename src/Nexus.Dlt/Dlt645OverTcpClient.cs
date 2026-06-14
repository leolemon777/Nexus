using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Dlt
{
    /// <summary>
    /// DLT645 over TCP 客户端 — 将 DLT645 串口协议封装在 TCP 连接上。
    /// <para>用于远程抄表场景：集中器通过 TCP/网络 将串口电表数据转发。</para>
    /// <para>帧格式与 DLT645 相同，底层通讯改为 TCP。</para>
    /// </summary>
    public class Dlt645OverTcpClient : TcpDeviceBase, IBatchReadWrite
    {
        private const byte FRAME_HEADER = 0x68;
        private const byte FRAME_END = 0x16;
        private const byte DATA_OFFSET = 0x33;

        private const byte CTRL_READ_DATA = 0x11;
        private const byte CTRL_WRITE_DATA = 0x14;

        protected override int ResponseHeaderLength => 0;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public byte[] MeterAddress { get; set; } = new byte[6];
        public byte[] Password { get; set; } = new byte[4];
        public byte[] OperatorCode { get; set; } = new byte[4];

        public Dlt645OverTcpClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout) { }

        public void SetMeterAddress(string address12)
        {
            if (address12 == null || address12.Length != 12)
                throw new ArgumentException("电表地址必须为 12 位数字");
            for (int i = 0; i < 6; i++)
            {
                string pair = address12.Substring(10 - i * 2, 2);
                MeterAddress[i] = byte.Parse(pair);
            }
        }

        public OperateResult<byte[]> ReadData(byte[] dataId)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult<byte[]>.Failed("数据标识必须为 4 字节");

            var frame = BuildFrame(CTRL_READ_DATA, dataId, null);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

            return Dlt645Client.ParseResponse(result.Content, CTRL_READ_DATA);
        }

        public OperateResult<byte[]> ReadData(string dataIdStr)
        {
            var id = Dlt645Client.ParseDataId(dataIdStr);
            if (id == null) return OperateResult<byte[]>.Failed($"数据标识格式错误: {dataIdStr}");
            return ReadData(id);
        }

        public OperateResult WriteData(byte[] dataId, byte[] data)
        {
            if (dataId == null || dataId.Length != 4)
                return OperateResult.Failed("数据标识必须为 4 字节");

            byte[] payload = new byte[8 + data.Length];
            Array.Copy(Password, 0, payload, 0, 4);
            Array.Copy(OperatorCode, 0, payload, 4, 4);
            Array.Copy(data, 0, payload, 8, data.Length);

            var frame = BuildFrame(CTRL_WRITE_DATA, dataId, payload);
            var result = SendAndReceive(frame);
            if (!result.IsSuccess) return OperateResult.Failed(result.Message);

            var parsed = Dlt645Client.ParseResponse(result.Content, CTRL_WRITE_DATA);
            if (!parsed.IsSuccess) return OperateResult.Failed(parsed.Message);
            return OperateResult.Success();
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
            => ReadData(address);

        public override OperateResult Write(string address, byte[] data)
        {
            var id = Dlt645Client.ParseDataId(address);
            if (id == null) return OperateResult.Failed($"数据标识格式错误: {address}");
            return WriteData(id, data);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadBytes(address, 1);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content[0] != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success(DataConverter.ToInt16(r.Content, 0));
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadBytes(address, 2);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success(DataConverter.ToUInt16(r.Content, 0));
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success(DataConverter.ToInt32(r.Content, 0));
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message);
            return OperateResult<uint>.Success(DataConverter.ToUInt32(r.Content, 0));
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<long>.Failed(r.Message);
            return OperateResult<long>.Success(DataConverter.ToInt64(r.Content, 0));
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<ulong>.Failed(r.Message);
            return OperateResult<ulong>.Success(DataConverter.ToUInt64(r.Content, 0));
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var r = ReadBytes(address, 4);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message);
            return OperateResult<float>.Success(DataConverter.ToFloat(r.Content, 0));
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadBytes(address, 8);
            if (!r.IsSuccess) return OperateResult<double>.Failed(r.Message);
            return OperateResult<double>.Success(DataConverter.ToDouble(r.Content, 0));
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = ReadBytes(address, length);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(DataConverter.ToString(r.Content, 0, r.Content.Length));
        }

        public override OperateResult Write(string address, bool value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, short value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, ushort value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, int value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, uint value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, long value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, ulong value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, float value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, double value)
            => Write(address, DataConverter.GetBytes(value));

        public override OperateResult Write(string address, string value)
            => Write(address, DataConverter.GetBytes(value));

        public override string ToString() => $"Dlt645OverTcpClient[{Ip}:{Port}]";

        private byte[] BuildFrame(byte control, byte[] dataId, byte[]? data)
        {
            int dataLen = 4 + (data?.Length ?? 0);
            byte[] dataField = new byte[dataLen];
            dataField[0] = dataId[0];
            dataField[1] = dataId[1];
            dataField[2] = dataId[2];
            dataField[3] = dataId[3];
            if (data != null) Array.Copy(data, 0, dataField, 4, data.Length);

            byte[] encrypted = new byte[dataLen];
            for (int i = 0; i < dataLen; i++)
                encrypted[i] = (byte)(dataField[i] + DATA_OFFSET);

            // C1 修复：算术累加和（非 XOR）
            byte cs = 0;
            for (int i = 0; i < 6; i++) cs += MeterAddress[i];
            cs += control;
            cs += (byte)dataLen;
            for (int i = 0; i < dataLen; i++) cs += encrypted[i];

            byte[] frame = new byte[] { FRAME_HEADER, 0, 0, 0, 0, 0, 0, FRAME_HEADER, control, (byte)dataLen };
            Array.Copy(MeterAddress, 0, frame, 1, 6);

            byte[] result = new byte[12 + dataLen];
            Array.Copy(frame, 0, result, 0, 10);
            Array.Copy(encrypted, 0, result, 10, dataLen);
            result[10 + dataLen] = cs;
            result[10 + dataLen + 1] = FRAME_END;
            return result;
        }

        protected override OperateResult<byte[]> SendAndReceive(byte[] request)
        {
            try
            {
                if (!IsConnected)
                {
                    var conn = Connect();
                    if (!conn.IsSuccess) return OperateResult<byte[]>.Failed(conn.Message, conn.ErrorCode);
                }

                NetworkStream? ns;
                _asyncLock.Wait();
                try { ns = _stream; }
                finally { _asyncLock.Release(); }
                if (ns == null) return OperateResult<byte[]>.Failed("连接已断开");

                Log.Debug($"TX → {DataConverter.ToHexString(request)}");
                RaiseMessageSent(DataConverter.ToHexString(request));

                ns.Write(request, 0, request.Length);

                var response = new System.Collections.Generic.List<byte>();
                byte[] buf = new byte[256];
                int start = Environment.TickCount;

                while (unchecked(Environment.TickCount - start) < Timeout)
                {
                    int read = ns.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        for (int i = 0; i < read; i++)
                        {
                            response.Add(buf[i]);
                            if (buf[i] == FRAME_END && response.Count >= 12 && response[0] == FRAME_HEADER)
                            {
                                byte[] full = response.ToArray();
                                Log.Debug($"RX ← {DataConverter.ToHexString(full)}");
                                RaiseMessageReceived(DataConverter.ToHexString(full));

                                if (!_persistentMode)
                                {
                                    _asyncLock.Wait();
                                    try { DisconnectCore(); }
                                    finally { _asyncLock.Release(); }
                                }
                                return OperateResult<byte[]>.Success(full);
                            }
                        }
                    }
                }

                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed($"DLT645-TCP 响应超时 ({Timeout}ms)");
            }
            catch (Exception ex)
            {
                Log.Error($"通讯异常 — {ex.Message}");
                RaiseError(ex.Message);
                if (!_persistentMode)
                {
                    _asyncLock.Wait();
                    try { DisconnectCore(); }
                    finally { _asyncLock.Release(); }
                }
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

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
    }
}
