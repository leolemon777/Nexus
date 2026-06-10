using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using System.Linq;

namespace Nexus.Bacnet
{
    public class BacnetIpClient : UdpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        public const int DefaultPort = 47808;
        private const byte BvlcType = 0x81;
        private const int BvlcHeaderLength = 4;
        private const int NpduHeaderLength = 2;

        private int _invokeId;
        private readonly Dictionary<byte, BacnetApduResponse> _pendingResponses = new Dictionary<byte, BacnetApduResponse>();
        private readonly object _responseLock = new object();
        private UdpClient? _listenClient;
        private Thread? _listenThread;
        private volatile bool _listening;
        private BacnetObjectId _localDeviceId;

        public BacnetObjectId LocalDeviceId
        {
            get => _localDeviceId;
            set => _localDeviceId = value;
        }

        public event EventHandler<CovNotificationEventArgs>? OnCOVNotification;
        public event EventHandler<IAmEventArgs>? OnIAm;
        public event EventHandler<byte[]>? OnRawFrame;

        public BacnetIpClient(string ip, int port = DefaultPort, int timeout = 5000)
            : base(ip, port, timeout)
        {
            _localDeviceId = new BacnetObjectId(BacnetObjectType.Device, 0);
        }

        protected override int ResponseHeaderLength => BvlcHeaderLength + NpduHeaderLength;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header.Length < 4) return 0;
            return ((header[2] << 8) | header[3]) - 4;
        }

        private byte NextInvokeId()
        {
            return (byte)(Interlocked.Increment(ref _invokeId) & 0xFF);
        }

        // ── BVLC framing ─────────────────────────────

        private static byte[] WrapBvlc(byte[] apdu)
        {
            int totalLength = 4 + apdu.Length;
            var frame = new byte[totalLength];
            frame[0] = BvlcType;
            frame[1] = 0x00;
            frame[2] = (byte)(totalLength >> 8);
            frame[3] = (byte)totalLength;
            Buffer.BlockCopy(apdu, 0, frame, 4, apdu.Length);
            return frame;
        }

        private static byte[] WrapNpdu(byte[] apdu, byte[]? destMac = null)
        {
            int npduLen = destMac != null && destMac.Length > 0 ? 2 + 1 + destMac.Length : 2;
            int totalLen = npduLen + apdu.Length;
            var npdu = new byte[totalLen];
            npdu[0] = 0x01;
            npdu[1] = destMac != null && destMac.Length > 0 ? (byte)0x20 : (byte)0x00;

            int pos = 2;
            if (destMac != null && destMac.Length > 0)
            {
                npdu[pos++] = (byte)destMac.Length;
                Buffer.BlockCopy(destMac, 0, npdu, pos, destMac.Length);
                pos += destMac.Length;
            }

            Buffer.BlockCopy(apdu, 0, npdu, pos, apdu.Length);
            return npdu;
        }

        private static BacnetIpFrame UnwrapBvlc(byte[] data)
        {
            var frame = new BacnetIpFrame();
            if (data.Length < 4)
            {
                frame.IsValid = false;
                return frame;
            }

            frame.Type = data[0];
            frame.Function = data[1];
            frame.Length = (data[2] << 8) | data[3];
            frame.IsValid = frame.Type == BvlcType;

            if (frame.Length <= data.Length)
            {
                frame.Payload = new byte[frame.Length - 4];
                Buffer.BlockCopy(data, 4, frame.Payload, 0, frame.Payload.Length);
            }
            else
            {
                frame.Payload = new byte[0];
                frame.IsValid = false;
            }

            return frame;
        }

        private static BacnetNpduHeader ParseNpdu(byte[] data, out int apduOffset)
        {
            var header = new BacnetNpduHeader();
            apduOffset = 2;

            if (data.Length < 2)
            {
                header.IsValid = false;
                return header;
            }

            header.Version = data[0];
            header.Control = data[1];
            header.IsValid = header.Version == 0x01;

            if ((header.Control & 0x20) != 0 && data.Length > 2)
            {
                byte destLen = data[apduOffset++];
                header.DestinationMac = new byte[destLen];
                Buffer.BlockCopy(data, apduOffset, header.DestinationMac, 0, destLen);
                apduOffset += destLen;
            }

            return header;
        }

        // ── Send / Receive with BVLC ─────────────────

        private OperateResult<byte[]> SendBvlc(byte[] apdu, byte[]? destMac = null)
        {
            byte[] npdu = WrapNpdu(apdu, destMac);
            byte[] frame = WrapBvlc(npdu);
            return base.SendAndReceive(frame);
        }

        private async Task<OperateResult<byte[]>> SendBvlcAsync(byte[] apdu, byte[] destMac = null, CancellationToken ct = default)
        {
            byte[] npdu = WrapNpdu(apdu, destMac);
            byte[] frame = WrapBvlc(npdu);
            return await base.SendAndReceiveAsync(frame, ct).ConfigureAwait(false);
        }

        private BacnetApduResponse ExtractApduResponse(byte[] frame)
        {
            OnRawFrame?.Invoke(this, frame);

            var bvlc = UnwrapBvlc(frame);
            if (!bvlc.IsValid)
            {
                return new BacnetApduResponse { IsValid = false };
            }

            var npdu = ParseNpdu(bvlc.Payload, out int apduOffset);
            if (!npdu.IsValid)
            {
                return new BacnetApduResponse { IsValid = false };
            }

            return BacnetApdu.DecodeApdu(bvlc.Payload, apduOffset, bvlc.Payload.Length - apduOffset);
        }

        // ── Broadcast listener for I-Am responses ────

        private void StartListening()
        {
            if (_listening) return;
            _listening = true;

            try
            {
                _listenClient = new UdpClient(Port);
                _listenClient.Client.EnableBroadcast = true;

                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "BACnet-Listen"
                };
                _listenThread.Start();
            }
            catch
            {
                _listening = false;
            }
        }

        private void StopListening()
        {
            _listening = false;
            try { _listenClient?.Close(); } catch { }
            _listenClient = null;
        }

        private void ListenLoop()
        {
            while (_listening)
            {
                try
                {
                    var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _listenClient!.Receive(ref remoteEp);
                    if (data.Length < 4) continue;

                    var response = ExtractApduResponse(data);
                    if (!response.IsValid) continue;

                    if (response.PduType == BacnetPduType.UnconfirmedRequest)
                    {
                        HandleUnconfirmedRequest(response, remoteEp);
                    }
                    else if (response.PduType == BacnetPduType.SimpleAck ||
                             response.PduType == BacnetPduType.ComplexAck ||
                             response.PduType == BacnetPduType.Error ||
                             response.PduType == BacnetPduType.Reject ||
                             response.PduType == BacnetPduType.Abort)
                    {
                        lock (_responseLock)
                        {
                            _pendingResponses[response.InvokeId] = response;
                        }
                    }
                }
                catch (SocketException) when (!_listening)
                {
                    break;
                }
                catch
                {
                    if (!_listening) break;
                }
            }
        }

        private void HandleUnconfirmedRequest(BacnetApduResponse response, IPEndPoint remoteEp)
        {
            if (response.ServiceChoice == (int)BacnetUnconfirmedService.IAm)
            {
                if (response.Values.Length >= 4)
                {
                    var objectId = (BacnetObjectId)(response.Values[0].Data ?? new BacnetObjectId());
                    uint maxApdu = Convert.ToUInt32(response.Values[1].Data ?? 0u);
                    var segSupport = (BacnetSegmentation)Convert.ToUInt32(response.Values[2].Data ?? 0u);
                    uint vendorId = Convert.ToUInt32(response.Values[3].Data ?? 0u);

                    OnIAm?.Invoke(this, new IAmEventArgs
                    {
                        DeviceId = objectId,
                        MaxApdu = maxApdu,
                        SegmentationSupported = segSupport,
                        VendorId = vendorId,
                        RemoteAddress = remoteEp
                    });
                }
            }
            else if (response.ServiceChoice == (int)BacnetUnconfirmedService.UnconfirmedEventNotification)
            {
                OnCOVNotification?.Invoke(this, new CovNotificationEventArgs
                {
                    Values = response.Values,
                    Timestamp = DateTime.Now
                });
            }
        }

        // ── Who-Is / I-Am ────────────────────────────

        public OperateResult WhoIs(int lowLimit = -1, int highLimit = -1)
        {
            var apdu = BacnetApdu.EncodeWhoIs(lowLimit, highLimit);
            byte[] npdu = WrapNpdu(apdu);
            byte[] frame = WrapBvlc(npdu);

            try
            {
                UdpClient? client;
                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        var conn = Connect();
                        if (!conn.IsSuccess) return conn;
                    }
                    client = GetUdpClient();
                }

                if (client == null) return OperateResult.Failed("UDP 未创建");

                StartListening();

                var broadcastEp = new IPEndPoint(IPAddress.Broadcast, Port);
                client.EnableBroadcast = true;

                Log.Debug($"TX → [BROADCAST] {DataConverter.ToHexString(frame)}");
                RaiseMessageSent(DataConverter.ToHexString(frame));

                client.Send(frame, frame.Length, broadcastEp);

                Log.Info("Who-Is 广播已发送");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"Who-Is 发送失败: {ex.Message}");
                return OperateResult.Failed($"Who-Is 发送失败: {ex.Message}");
            }
        }

        public OperateResult IAm()
        {
            var apdu = BacnetApdu.EncodeIAm(_localDeviceId, 1476, BacnetSegmentation.Both, 0);
            byte[] npdu = WrapNpdu(apdu);
            byte[] frame = WrapBvlc(npdu);

            try
            {
                UdpClient? client;
                lock (_lock)
                {
                    if (!IsConnected)
                    {
                        var conn = Connect();
                        if (!conn.IsSuccess) return conn;
                    }
                    client = GetUdpClient();
                }

                if (client == null) return OperateResult.Failed("UDP 未创建");

                var broadcastEp = new IPEndPoint(IPAddress.Broadcast, Port);
                client.EnableBroadcast = true;

                Log.Debug($"TX → [BROADCAST] {DataConverter.ToHexString(frame)}");
                RaiseMessageSent(DataConverter.ToHexString(frame));

                client.Send(frame, frame.Length, broadcastEp);

                Log.Info("I-Am 广播已发送");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"I-Am 发送失败: {ex.Message}");
                return OperateResult.Failed($"I-Am 发送失败: {ex.Message}");
            }
        }

        // ── ReadProperty ──────────────────────────────

        public OperateResult<BacnetValue[]> ReadProperty(BacnetObjectId objectId, BacnetPropertyId propertyId, uint arrayIndex = uint.MaxValue)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeReadProperty(invokeId, objectId, propertyId, arrayIndex);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return OperateResult<BacnetValue[]>.Failed(result.Message, result.ErrorCode);

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult<BacnetValue[]>.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.Error)
                return OperateResult<BacnetValue[]>.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            if (response.PduType == BacnetPduType.Reject)
                return OperateResult<BacnetValue[]>.Failed($"BACnet 拒绝: {response.RejectReason}");

            if (response.PduType == BacnetPduType.ComplexAck)
                return OperateResult<BacnetValue[]>.Success(response.Values);

            return OperateResult<BacnetValue[]>.Failed("意外的 PDU 类型");
        }

        public OperateResult<float> ReadPresentValue(BacnetObjectId objectId)
        {
            var result = ReadProperty(objectId, BacnetPropertyId.PresentValue);
            if (!result.IsSuccess) return OperateResult<float>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<float>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.Real)
                return OperateResult<float>.Success(Convert.ToSingle(val.Data));
            if (val.Tag == BacnetApplicationTag.Double)
                return OperateResult<float>.Success((float)Convert.ToDouble(val.Data));
            if (val.Tag == BacnetApplicationTag.Unsigned)
                return OperateResult<float>.Success((float)Convert.ToUInt32(val.Data));
            if (val.Tag == BacnetApplicationTag.Signed)
                return OperateResult<float>.Success((float)Convert.ToInt32(val.Data));

            return OperateResult<float>.Failed($"不支持的标签类型: {val.Tag}");
        }

        public OperateResult<string> ReadObjectName(BacnetObjectId objectId)
        {
            var result = ReadProperty(objectId, BacnetPropertyId.ObjectName);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<string>.Success("");
            return OperateResult<string>.Success(Convert.ToString(result.Content[0].Data) ?? "");
        }

        // ── ReadPropertyMultiple ──────────────────────

        public OperateResult<BacnetValue[][]> ReadPropertyMultiple(BacnetPropertyReference[] references)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeReadPropertyMultiple(invokeId, references);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return OperateResult<BacnetValue[][]>.Failed(result.Message, result.ErrorCode);

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult<BacnetValue[][]>.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.Error)
                return OperateResult<BacnetValue[][]>.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            if (response.PduType == BacnetPduType.ComplexAck)
            {
                return OperateResult<BacnetValue[][]>.Success(new[] { response.Values });
            }

            return OperateResult<BacnetValue[][]>.Failed("意外的 PDU 类型");
        }

        // ── WriteProperty ─────────────────────────────

        public OperateResult WriteProperty(BacnetObjectId objectId, BacnetPropertyId propertyId, BacnetValue value, uint priority = 0)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeWriteProperty(invokeId, objectId, propertyId, value, priority);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return result;

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.SimpleAck)
                return OperateResult.Success();

            if (response.PduType == BacnetPduType.Error)
                return OperateResult.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            if (response.PduType == BacnetPduType.Reject)
                return OperateResult.Failed($"BACnet 拒绝: {response.RejectReason}");

            return OperateResult.Failed("意外的 PDU 类型");
        }

        public OperateResult WritePresentValue(BacnetObjectId objectId, float value)
        {
            return WriteProperty(objectId, BacnetPropertyId.PresentValue,
                new BacnetValue(BacnetApplicationTag.Real, value));
        }

        // ── WritePropertyMultiple ─────────────────────

        public OperateResult WritePropertyMultiple(BacnetObjectId objectId, BacnetPropertyValue[] values)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeWritePropertyMultiple(invokeId, objectId, values);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return result;

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.SimpleAck)
                return OperateResult.Success();

            if (response.PduType == BacnetPduType.Error)
                return OperateResult.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            return OperateResult.Failed("意外的 PDU 类型");
        }

        // ── SubscribeCOV ─────────────────────────────

        public OperateResult SubscribeCov(uint subscriberProcessId, BacnetObjectId monitoredObjectId, bool confirmed, uint lifetime)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeSubscribeCov(invokeId, subscriberProcessId, monitoredObjectId, confirmed, lifetime);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return result;

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.SimpleAck)
                return OperateResult.Success();

            if (response.PduType == BacnetPduType.Error)
                return OperateResult.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            return OperateResult.Failed("意外的 PDU 类型");
        }

        public OperateResult UnsubscribeCov(uint subscriberProcessId, BacnetObjectId monitoredObjectId)
        {
            return SubscribeCov(subscriberProcessId, monitoredObjectId, false, 0);
        }

        // ── Device object browsing ────────────────────

        public OperateResult<BacnetObjectId[]> ReadObjectList(BacnetObjectId deviceId)
        {
            var result = ReadProperty(deviceId, BacnetPropertyId.ObjectList);
            if (!result.IsSuccess) return OperateResult<BacnetObjectId[]>.Failed(result.Message, result.ErrorCode);

            var objects = new List<BacnetObjectId>();
            foreach (var val in result.Content)
            {
                if (val.Tag == BacnetApplicationTag.ObjectId || val.Data is BacnetObjectId)
                {
                    if (val.Data is BacnetObjectId id)
                        objects.Add(id);
                }
            }

            return OperateResult<BacnetObjectId[]>.Success(objects.ToArray());
        }

        public OperateResult<BacnetDeviceObject[]> BrowseDeviceObjects(BacnetObjectId deviceId)
        {
            var objListResult = ReadObjectList(deviceId);
            if (!objListResult.IsSuccess) return OperateResult<BacnetDeviceObject[]>.Failed(objListResult.Message);

            var objects = new List<BacnetDeviceObject>();
            foreach (var objId in objListResult.Content)
            {
                var nameResult = ReadObjectName(objId);
                objects.Add(new BacnetDeviceObject
                {
                    ObjectId = objId,
                    ObjectName = nameResult.IsSuccess ? nameResult.Content : "",
                    ObjectType = objId.Type
                });
            }

            return OperateResult<BacnetDeviceObject[]>.Success(objects.ToArray());
        }

        // ── Atomic Read File / Write File ─────────────

        public OperateResult<byte[]> AtomicReadFile(BacnetObjectId fileId, int startPosition, int count)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeAtomicReadFile(invokeId, fileId, false, startPosition, count);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult<byte[]>.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.Error)
                return OperateResult<byte[]>.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            if (response.PduType == BacnetPduType.ComplexAck && response.Values.Length > 0)
            {
                for (int i = 0; i < response.Values.Length; i++)
                {
                    if (response.Values[i].Tag == BacnetApplicationTag.OctetString)
                        return OperateResult<byte[]>.Success((byte[])response.Values[i].Data);
                }
            }

            return OperateResult<byte[]>.Failed("文件读取失败");
        }

        public OperateResult AtomicWriteFile(BacnetObjectId fileId, int startPosition, byte[] data)
        {
            byte invokeId = NextInvokeId();
            var apdu = BacnetApdu.EncodeAtomicWriteFile(invokeId, fileId, false, startPosition, data);
            var result = SendBvlc(apdu);
            if (!result.IsSuccess) return result;

            var response = ExtractApduResponse(result.Content);
            if (!response.IsValid)
                return OperateResult.Failed("响应解析失败");

            if (response.PduType == BacnetPduType.SimpleAck)
                return OperateResult.Success();

            if (response.PduType == BacnetPduType.Error)
                return OperateResult.Failed(
                    $"BACnet 错误: {response.ErrorClass}/{response.ErrorCode}", (int)response.ErrorCode);

            return OperateResult.Failed("文件写入失败");
        }

        // ── IReadWriteDevice overrides (address format: "ObjectType:Instance.PropertyId") ──

        private static BacnetPropertyReference ParseAddress(string address)
        {
            address = address.Trim();

            int dotIdx = address.IndexOf('.');
            string objPart = dotIdx >= 0 ? address.Substring(0, dotIdx) : address;
            string propPart = dotIdx >= 0 ? address.Substring(dotIdx + 1) : "85";

            int colonIdx = objPart.IndexOf(':');
            string typeStr = colonIdx >= 0 ? objPart.Substring(0, colonIdx) : objPart;
            string instStr = colonIdx >= 0 ? objPart.Substring(colonIdx + 1) : "0";

            BacnetObjectType objType;
            if (int.TryParse(typeStr, out int typeNum))
                objType = (BacnetObjectType)typeNum;
            else if (!Enum.TryParse(typeStr, true, out objType))
                objType = BacnetObjectType.AnalogInput;

            uint instance = uint.Parse(instStr);

            BacnetPropertyId propId;
            if (uint.TryParse(propPart, out uint propNum))
                propId = (BacnetPropertyId)propNum;
            else if (!Enum.TryParse(propPart, true, out propId))
                propId = BacnetPropertyId.PresentValue;

            return new BacnetPropertyReference(
                new BacnetObjectId(objType, instance),
                propId);
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var pref = ParseAddress(address);
            return ReadPresentValue(pref.ObjectIdentifier);
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var pref = ParseAddress(address);
            var result = ReadProperty(pref.ObjectIdentifier, pref.PropertyId);
            if (!result.IsSuccess) return OperateResult<double>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<double>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.Double)
                return OperateResult<double>.Success(Convert.ToDouble(val.Data));
            if (val.Tag == BacnetApplicationTag.Real)
                return OperateResult<double>.Success((double)Convert.ToSingle(val.Data));

            return OperateResult<double>.Failed($"不支持的标签类型: {val.Tag}");
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var pref = ParseAddress(address);
            var result = ReadProperty(pref.ObjectIdentifier, pref.PropertyId);
            if (!result.IsSuccess) return OperateResult<int>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<int>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.Unsigned)
                return OperateResult<int>.Success((int)Convert.ToUInt32(val.Data));
            if (val.Tag == BacnetApplicationTag.Signed)
                return OperateResult<int>.Success(Convert.ToInt32(val.Data));

            return OperateResult<int>.Failed($"不支持的标签类型: {val.Tag}");
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<short>.Success((short)r.Content) : OperateResult<short>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<ushort>.Success((ushort)r.Content) : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var pref = ParseAddress(address);
            var result = ReadProperty(pref.ObjectIdentifier, pref.PropertyId);
            if (!result.IsSuccess) return OperateResult<uint>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<uint>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.Unsigned)
                return OperateResult<uint>.Success(Convert.ToUInt32(val.Data));

            return OperateResult<uint>.Failed($"不支持的标签类型: {val.Tag}");
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadInt32(address);
            return r.IsSuccess ? OperateResult<long>.Success((long)r.Content) : OperateResult<long>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadUInt32(address);
            return r.IsSuccess ? OperateResult<ulong>.Success((ulong)r.Content) : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var pref = ParseAddress(address);
            var result = ReadProperty(pref.ObjectIdentifier, pref.PropertyId);
            if (!result.IsSuccess) return OperateResult<bool>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<bool>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.Boolean)
                return OperateResult<bool>.Success(Convert.ToBoolean(val.Data));
            if (val.Tag == BacnetApplicationTag.Enumerated)
                return OperateResult<bool>.Success(Convert.ToUInt32(val.Data) != 0);

            return OperateResult<bool>.Failed($"不支持的标签类型: {val.Tag}");
        }

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var pref = ParseAddress(address);
            BacnetPropertyId propId = pref.PropertyId == BacnetPropertyId.PresentValue
                ? BacnetPropertyId.ObjectName
                : pref.PropertyId;

            var result = ReadProperty(pref.ObjectIdentifier, propId);
            if (!result.IsSuccess) return OperateResult<string>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<string>.Success("");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.CharacterString)
                return OperateResult<string>.Success(Convert.ToString(val.Data) ?? "");

            return OperateResult<string>.Success(val.Data?.ToString() ?? "");
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var pref = ParseAddress(address);
            var result = ReadProperty(pref.ObjectIdentifier, pref.PropertyId);
            if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message, result.ErrorCode);
            if (result.Content.Length == 0) return OperateResult<byte[]>.Failed("无返回数据");

            var val = result.Content[0];
            if (val.Tag == BacnetApplicationTag.OctetString)
                return OperateResult<byte[]>.Success((byte[])val.Data);

            return OperateResult<byte[]>.Failed($"不支持的标签类型: {val.Tag}");
        }

        // ── Write overrides ───────────────────────────

        public override OperateResult Write(string address, float value)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.Real, value));
        }

        public override OperateResult Write(string address, double value)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.Double, value));
        }

        public override OperateResult Write(string address, int value)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.Signed, value));
        }

        public override OperateResult Write(string address, short value) => Write(address, (int)value);
        public override OperateResult Write(string address, ushort value) => Write(address, (int)value);
        public override OperateResult Write(string address, uint value) => Write(address, (int)(uint)value);
        public override OperateResult Write(string address, long value) => Write(address, (int)value);
        public override OperateResult Write(string address, ulong value) => Write(address, (int)value);

        public override OperateResult Write(string address, bool value)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.Boolean, value));
        }

        public override OperateResult Write(string address, string value)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.CharacterString, value));
        }

        public override OperateResult Write(string address, byte[] data)
        {
            var pref = ParseAddress(address);
            return WriteProperty(pref.ObjectIdentifier, pref.PropertyId,
                new BacnetValue(BacnetApplicationTag.OctetString, data));
        }

        // ── Helper to get internal UdpClient ─────────

        private UdpClient? GetUdpClient()
        {
            var field = typeof(UdpDeviceBase).GetField("_client",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(this) as UdpClient;
        }

        // ── Dispose ──────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopListening();
            }
            base.Dispose(disposing);
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
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

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
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

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
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

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }

                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };

                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    public class BacnetIpFrame
    {
        public bool IsValid { get; set; }
        public byte Type { get; set; }
        public byte Function { get; set; }
        public int Length { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();
    }

    public class BacnetNpduHeader
    {
        public bool IsValid { get; set; }
        public byte Version { get; set; }
        public byte Control { get; set; }
        public byte[]? DestinationMac { get; set; }
    }

    public class IAmEventArgs : EventArgs
    {
        public BacnetObjectId DeviceId { get; set; }
        public uint MaxApdu { get; set; }
        public BacnetSegmentation SegmentationSupported { get; set; }
        public uint VendorId { get; set; }
        public IPEndPoint? RemoteAddress { get; set; }
    }

    public class CovNotificationEventArgs : EventArgs
    {
        public BacnetValue[] Values { get; set; } = Array.Empty<BacnetValue>();
        public DateTime Timestamp { get; set; }
    }
}
