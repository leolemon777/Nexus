using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.BacnetIp
{
    /// <summary>
    /// BACnet IP 客户端 — 通过 UDP 与 BACnet 设备通信。
    /// <para>协议栈: BVLC (UDP:47808) → NPDU → APDU (ReadProperty/WriteProperty)</para>
    /// <para>地址格式: device.objectType:instance.property 或 network:device.objectType:instance.property</para>
    /// </summary>
    public class BacnetIpClient : IDisposable, IBatchReadWrite
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly int _timeout;
        private UdpClient? _udp;
        private IPEndPoint? _remoteEp;
        private int _invokeId;
        private bool _disposed;

        public ILogger Log { get; set; }

        /// <summary>本地设备 ID。</summary>
        public uint LocalDeviceId { get; set; } = 0;

        /// <summary>远程设备 ID。</summary>
        public uint RemoteDeviceId { get; set; } = 0;

        /// <summary>网络号。</summary>
        public ushort NetworkNumber { get; set; } = 0;

        /// <summary>是否已连接。</summary>
        public bool IsConnected => _udp != null;

        public BacnetIpClient(string ip, int port = 47808, int timeout = 5000)
        {
            _ip = ip;
            _port = port;
            _timeout = timeout;
            Log = NullLogger.Instance;
        }

        public void SetLogger(ILogger logger) => Log = logger ?? NullLogger.Instance;

        // ═══════════════════════════════════════════
        //  连接管理
        // ═══════════════════════════════════════════

        public OperateResult Connect()
        {
            try
            {
                _udp = new UdpClient();
                _remoteEp = new IPEndPoint(IPAddress.Parse(_ip), _port);
                _udp.Connect(_remoteEp);
                _udp.Client.ReceiveTimeout = _timeout;
                _udp.Client.SendTimeout = _timeout;

                // Send Who-Is to discover device
                SendWhoIs();
                Thread.Sleep(100);
                DrainResponses();

                Log.Info($"BACnet IP 已连接 {_ip}:{_port}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"BACnet IP 连接失败: {ex.Message}");
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        public async Task<OperateResult> ConnectAsync(CancellationToken ct = default)
        {
            return await Task.Run(() => Connect(), ct).ConfigureAwait(false);
        }

        // IReadWriteDevice.ConnectAsync() (no params)
        Task<OperateResult> IReadWriteDevice.ConnectAsync() => ConnectAsync(CancellationToken.None);

        public void Disconnect()
        {
            _udp?.Close();
            _udp = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }

        // ═══════════════════════════════════════════
        //  BVLC (BACnet Virtual Link Control)
        // ═══════════════════════════════════════════

        private byte[] BuildBvlc(byte[] npdu)
        {
            int totalLen = 4 + npdu.Length; // BVLC header (4) + NPDU
            byte[] bvlc = new byte[totalLen];
            bvlc[0] = 0x81; // BACnet/IP (IPv4)
            bvlc[1] = 0x0A; // Original-Unicast-NPDU
            bvlc[2] = (byte)(totalLen >> 8);
            bvlc[3] = (byte)(totalLen & 0xFF);
            Buffer.BlockCopy(npdu, 0, bvlc, 4, npdu.Length);
            return bvlc;
        }

        private byte[] ParseBvlc(byte[] response, out int npduOffset)
        {
            npduOffset = 0;
            if (response.Length < 4) return response;
            if (response[0] != 0x81) return response;

            byte function = response[1];
            int length = (response[2] << 8) | response[3];

            if (function == 0x0A || function == 0x0B) // Original-Unicast or Broadcast
            {
                npduOffset = 4;
                byte[] npdu = new byte[response.Length - 4];
                Buffer.BlockCopy(response, 4, npdu, 0, npdu.Length);
                return npdu;
            }

            return response;
        }

        // ═══════════════════════════════════════════
        //  NPDU (Network Protocol Data Unit)
        // ═══════════════════════════════════════════

        private byte[] BuildNpdu(byte[] apdu, ushort destNetwork = 0, byte destAddress = 0xFF)
        {
            // NPDU: Version(1) + Control(1) + [DestNetwork(2) + DestAddrLen(1) + DestAddr(N) + SrcNetwork(2) + SrcAddrLen(1) + SrcAddr(N)] + APDU
            bool hasDest = destNetwork != 0 || destAddress != 0xFF;
            int npduLen = 2 + (hasDest ? 7 : 0) + apdu.Length;
            byte[] npdu = new byte[npduLen];
            npdu[0] = 0x01; // Version 1
            npdu[1] = hasDest ? (byte)0x20 : (byte)0x00; // Control: destination specifier

            int offset = 2;
            if (hasDest)
            {
                npdu[offset++] = (byte)(destNetwork >> 8);
                npdu[offset++] = (byte)(destNetwork & 0xFF);
                npdu[offset++] = 0x01; // Address length
                npdu[offset++] = destAddress;
                npdu[offset++] = 0x00; npdu[offset++] = 0x00; // Source network (local)
                npdu[offset++] = 0x00; // Source address length (local)
            }

            Buffer.BlockCopy(apdu, 0, npdu, offset, apdu.Length);
            return npdu;
        }

        // ═══════════════════════════════════════════
        //  APDU (Application Protocol Data Unit)
        // ═══════════════════════════════════════════

        private byte NextInvokeId() => (byte)(Interlocked.Increment(ref _invokeId) & 0xFF);

        // ── Who-Is / I-Am ──────────────────

        private void SendWhoIs()
        {
            // Who-Is: Confirmed Request, service=0x08
            byte[] apdu = new byte[] { 0x10, NextInvokeId(), 0x08 };
            byte[] npdu = BuildNpdu(apdu);
            byte[] bvlc = BuildBvlc(npdu);
            _udp?.Send(bvlc, bvlc.Length);
        }

        private void DrainResponses()
        {
            if (_udp == null) return;
            var deadline = DateTime.UtcNow.AddMilliseconds(500);
            while (DateTime.UtcNow < deadline && _udp.Available > 0)
            {
                try
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    _udp.Receive(ref ep);
                }
                catch { break; }
            }
        }

        // ── ReadProperty ──────────────────

        private OperateResult<byte[]> ReadProperty(uint deviceId, ushort objectType, uint instance, byte propertyId)
        {
            if (_udp == null) return OperateResult<byte[]>.Failed("未连接");

            byte invokeId = NextInvokeId();

            // Build APDU: Confirmed Request
            // PDU Type: 0x00 (Confirmed Request)
            // Service: 0x0C (ReadProperty)
            byte[] objectIdentifier = new byte[]
            {
                (byte)((objectType >> 2) | 0xC0), // Tag 0, context, length 2
                (byte)(((objectType & 0x03) << 6) | ((instance >> 16) & 0x3F)),
                (byte)((instance >> 8) & 0xFF),
                (byte)(instance & 0xFF)
            };

            byte[] propertyIdentifier = new byte[]
            {
                (byte)(0x91), // Tag 1, context, length 1
                propertyId
            };

            int apduLen = 4 + objectIdentifier.Length + propertyIdentifier.Length;
            byte[] apdu = new byte[apduLen];
            apdu[0] = 0x00; // Confirmed Request
            apdu[1] = invokeId;
            apdu[2] = 0x0C; // ReadProperty
            apdu[3] = (byte)(0x0C); // Max segments / max response
            Buffer.BlockCopy(objectIdentifier, 0, apdu, 4, objectIdentifier.Length);
            Buffer.BlockCopy(propertyIdentifier, 0, apdu, 4 + objectIdentifier.Length, propertyIdentifier.Length);

            byte[] npdu = BuildNpdu(apdu);
            byte[] bvlc = BuildBvlc(npdu);

            return SendAndReceive(bvlc, invokeId);
        }

        // ── WriteProperty ──────────────────

        private OperateResult WriteProperty(uint deviceId, ushort objectType, uint instance, byte propertyId, byte[] value)
        {
            if (_udp == null) return OperateResult.Failed("未连接");

            byte invokeId = NextInvokeId();

            byte[] objectIdentifier = new byte[]
            {
                (byte)((objectType >> 2) | 0xC0),
                (byte)(((objectType & 0x03) << 6) | ((instance >> 16) & 0x3F)),
                (byte)((instance >> 8) & 0xFF),
                (byte)(instance & 0xFF)
            };

            byte[] propertyIdentifier = new byte[]
            {
                0x91,
                propertyId
            };

            // Value: opening tag 3 + value + closing tag 3
            byte[] valueTag = new byte[] { 0x3E }; // Opening tag 3
            byte[] closingTag = new byte[] { 0x3F }; // Closing tag 3

            int apduLen = 4 + objectIdentifier.Length + propertyIdentifier.Length + valueTag.Length + value.Length + closingTag.Length;
            byte[] apdu = new byte[apduLen];
            apdu[0] = 0x00; // Confirmed Request
            apdu[1] = invokeId;
            apdu[2] = 0x0F; // WriteProperty
            apdu[3] = 0x0C;
            int offset = 4;
            Buffer.BlockCopy(objectIdentifier, 0, apdu, offset, objectIdentifier.Length); offset += objectIdentifier.Length;
            Buffer.BlockCopy(propertyIdentifier, 0, apdu, offset, propertyIdentifier.Length); offset += propertyIdentifier.Length;
            Buffer.BlockCopy(valueTag, 0, apdu, offset, valueTag.Length); offset += valueTag.Length;
            Buffer.BlockCopy(value, 0, apdu, offset, value.Length); offset += value.Length;
            Buffer.BlockCopy(closingTag, 0, apdu, offset, closingTag.Length);

            byte[] npdu = BuildNpdu(apdu);
            byte[] bvlc = BuildBvlc(npdu);

            var result = SendAndReceive(bvlc, invokeId);
            return result.IsSuccess ? OperateResult.Success() : OperateResult.Failed(result.Message, result.ErrorCode);
        }

        // ═══════════════════════════════════════════
        //  收发
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> SendAndReceive(byte[] request, byte expectedInvokeId)
        {
            if (_udp == null) return OperateResult<byte[]>.Failed("未连接");

            try
            {
                _udp.Send(request, request.Length);
                Log.Debug($"BACnet TX → {DataConverter.ToHexString(request)}");

                var deadline = DateTime.UtcNow.AddMilliseconds(_timeout);
                while (DateTime.UtcNow < deadline)
                {
                    if (_udp.Available > 0)
                    {
                        var ep = new IPEndPoint(IPAddress.Any, 0);
                        byte[] response = _udp.Receive(ref ep);
                        Log.Debug($"BACnet RX ← {DataConverter.ToHexString(response)}");

                        byte[] npdu = ParseBvlc(response, out int npduOffset);
                        if (npdu.Length < 2) continue;

                        // Skip NPDU header
                        int apduOffset = npduOffset + 2;
                        if (npdu.Length > 1 && (npdu[1] & 0x20) != 0)
                            apduOffset += 7; // Skip destination specifier

                        if (apduOffset >= response.Length) continue;

                        byte[] apdu = new byte[response.Length - apduOffset];
                        Buffer.BlockCopy(response, apduOffset, apdu, 0, apdu.Length);

                        if (apdu.Length < 2) continue;

                        byte pduType = (byte)(apdu[0] & 0xF0);

                        // Simple ACK (0x50)
                        if (pduType == 0x50 && apdu.Length >= 2)
                        {
                            if (apdu[1] == expectedInvokeId)
                                return OperateResult<byte[]>.Success(Array.Empty<byte>());
                            continue;
                        }

                        // Complex ACK (0x30)
                        if (pduType == 0x30 && apdu.Length >= 4)
                        {
                            if (apdu[2] == expectedInvokeId)
                            {
                                byte[] data = new byte[apdu.Length - 4];
                                Buffer.BlockCopy(apdu, 4, data, 0, data.Length);
                                return OperateResult<byte[]>.Success(data);
                            }
                            continue;
                        }

                        // Error (0x50)
                        if (pduType == 0x50 && apdu.Length >= 4)
                        {
                            ushort errorClass = (ushort)((apdu[2] << 8) | apdu[3]);
                            ushort errorCode = apdu.Length > 4 ? (ushort)((apdu[4] << 8) | apdu[5]) : (ushort)0;
                            return OperateResult<byte[]>.Failed($"BACnet 错误: class={errorClass} code={errorCode}");
                        }

                        // Reject (0x60)
                        if (pduType == 0x60)
                        {
                            return OperateResult<byte[]>.Failed("BACnet 请求被拒绝");
                        }
                    }
                    else
                    {
                        Thread.Sleep(10);
                    }
                }

                return OperateResult<byte[]>.Failed("BACnet 响应超时");
            }
            catch (Exception ex)
            {
                Log.Error($"BACnet 通讯异常: {ex.Message}");
                return OperateResult<byte[]>.Failed($"通讯异常: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════
        //  BACnet 值编码/解码
        // ═══════════════════════════════════════════

        private byte[] EncodeReal(float value)
        {
            int bits;
            unsafe { bits = *(int*)&value; }
            return new byte[] { 0x44, (byte)(bits >> 24), (byte)(bits >> 16), (byte)(bits >> 8), (byte)bits };
        }

        private float DecodeReal(byte[] data, int offset)
        {
            if (offset + 4 > data.Length) return 0;
            int bits = (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
            unsafe { return *(float*)&bits; }
        }

        private byte[] EncodeUnsigned(uint value)
        {
            if (value <= 0xFF) return new byte[] { 0x21, (byte)value };
            if (value <= 0xFFFF) return new byte[] { 0x22, (byte)(value >> 8), (byte)value };
            return new byte[] { 0x23, (byte)(value >> 16), (byte)(value >> 8), (byte)value };
        }

        private uint DecodeUnsigned(byte[] data, int offset)
        {
            if (offset >= data.Length) return 0;
            byte tag = data[offset];
            int len = (tag & 0x07);
            if (len == 0) return 0;
            uint result = 0;
            for (int i = 0; i < len && offset + 1 + i < data.Length; i++)
                result = (result << 8) | data[offset + 1 + i];
            return result;
        }

        private byte[] EncodeBoolean(bool value)
        {
            return new byte[] { (byte)(value ? 0x11 : 0x10) };
        }

        private bool DecodeBoolean(byte[] data, int offset)
        {
            if (offset >= data.Length) return false;
            return (data[offset] & 0x01) != 0;
        }

        private byte[] EncodeEnumerated(uint value)
        {
            if (value <= 0xFF) return new byte[] { 0x91, (byte)value };
            return new byte[] { 0x92, (byte)(value >> 8), (byte)value };
        }

        private int FindValueOffset(byte[] apdu)
        {
            // Skip object identifier (4 bytes) + property identifier (2 bytes) + opening tag
            for (int i = 0; i < apdu.Length; i++)
            {
                if (apdu[i] == 0x3E) // Opening tag 3
                    return i + 1;
            }
            return 0;
        }

        // ═══════════════════════════════════════════
        //  设备发现
        // ═══════════════════════════════════════════

        /// <summary>发送 Who-Is 并收集 I-Am 响应。</summary>
        public List<uint> DiscoverDevices(int timeoutMs = 3000)
        {
            var devices = new List<uint>();
            if (_udp == null) return devices;

            SendWhoIs();
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (_udp.Available > 0)
                {
                    var ep = new IPEndPoint(IPAddress.Any, 0);
                    byte[] response = _udp.Receive(ref ep);
                    byte[] npdu = ParseBvlc(response, out int npduOffset);
                    if (npdu.Length > 4)
                    {
                        int apduOffset = npduOffset + 2;
                        if (apduOffset < response.Length)
                        {
                            byte[] apdu = new byte[response.Length - apduOffset];
                            Buffer.BlockCopy(response, apduOffset, apdu, 0, apdu.Length);
                            if (apdu.Length >= 2 && (apdu[0] & 0xF0) == 0x10) // Unconfirmed Request
                            {
                                if (apdu.Length > 2 && apdu[2] == 0x00) // I-Am
                                {
                                    if (apdu.Length >= 7)
                                    {
                                        uint id = (uint)((apdu[4] << 16) | (apdu[5] << 8) | apdu[6]);
                                        if (!devices.Contains(id)) devices.Add(id);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }

            return devices;
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice 实现
        // ═══════════════════════════════════════════

        private BacnetIpAddress ParseAddr(string address)
        {
            var parser = new BacnetIpAddressParser();
            return parser.Parse(address);
        }

        public OperateResult<bool> ReadBool(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            return OperateResult<bool>.Success(DecodeBoolean(r.Content, offset));
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            return OperateResult<short>.Success((short)DecodeUnsigned(r.Content, offset));
        }

        public OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt16(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            return OperateResult<int>.Success((int)DecodeUnsigned(r.Content, offset));
        }

        public OperateResult<uint> ReadUInt32(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<uint>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            return OperateResult<uint>.Success(DecodeUnsigned(r.Content, offset));
        }

        public OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadUInt32(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<float> ReadFloat(string address)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<float>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            return OperateResult<float>.Success(DecodeReal(r.Content, offset));
        }

        public OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess ? OperateResult<double>.Success((double)r.Content) : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public OperateResult<string> ReadString(string address, ushort length)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message, r.ErrorCode);
            int offset = FindValueOffset(r.Content);
            if (offset >= r.Content.Length) return OperateResult<string>.Success("");
            int strLen = Math.Min(length, r.Content.Length - offset);
            return OperateResult<string>.Success(System.Text.Encoding.ASCII.GetString(r.Content, offset, strLen).TrimEnd('\0'));
        }

        public OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var addr = ParseAddr(address);
            var r = ReadProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message, r.ErrorCode);
            return OperateResult<byte[]>.Success(r.Content);
        }

        // ── Write implementations ──────────────────

        public OperateResult Write(string address, bool value)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, EncodeBoolean(value));
        }

        public OperateResult Write(string address, short value)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, EncodeUnsigned((uint)value));
        }

        public OperateResult Write(string address, ushort value) => Write(address, (short)value);

        public OperateResult Write(string address, int value)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, EncodeUnsigned((uint)value));
        }

        public OperateResult Write(string address, uint value)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, EncodeUnsigned(value));
        }

        public OperateResult Write(string address, long value) => Write(address, (int)value);
        public OperateResult Write(string address, ulong value) => Write(address, (uint)value);

        public OperateResult Write(string address, float value)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, EncodeReal(value));
        }

        public OperateResult Write(string address, double value) => Write(address, (float)value);

        public OperateResult Write(string address, string value)
        {
            var addr = ParseAddr(address);
            byte[] strData = System.Text.Encoding.ASCII.GetBytes(value);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, strData);
        }

        public OperateResult Write(string address, byte[] data)
        {
            var addr = ParseAddr(address);
            return WriteProperty(addr.DeviceId, addr.ObjectType, addr.Instance, addr.PropertyId, data);
        }

        // ── Async ──────────────────────

        public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));

        // ── IBatchReadWrite ──────────────────────

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0) return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList) { var r = ReadFloat(addr); if (!r.IsSuccess) return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode); result[addr] = r.Content; }
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
