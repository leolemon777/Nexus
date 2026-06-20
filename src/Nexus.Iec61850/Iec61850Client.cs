using System;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nexus.Iec61850
{
    /// <summary>
    /// IEC 61850 MMS 客户端 — 支持 IEC 61850变电站自动化通信。
    /// <para>通过 MMS (Manufacturing Message Specification) 协议与 IED 通信。</para>
    /// <para>支持数据模型浏览、数据读写、报告订阅、控制操作。</para>
    /// <para>当 UseRealMms=true 时，使用 TPKT+COTP+ASN.1 BER 编码与真实 IED 通信。</para>
    /// </summary>
    public class Iec61850Client : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        /// <summary>IED 逻辑设备名称。</summary>
        public string LogicalDevice { get; set; } = "LD0";

        /// <summary>默认报告触发选项。</summary>
        public ReportTriggerOptions TriggerOptions { get; set; } = ReportTriggerOptions.DataChanged | ReportTriggerOptions.QualityChanged;

        /// <summary>
        /// 是否使用真实的 MMS/BER 编码（TPKT+COTP+ASN.1 BER）。
        /// <para>false（默认）：使用简化的自定义二进制格式（仅兼容虚拟服务器）。</para>
        /// <para>true：使用 ISO COTP/TPKT + MMS with ASN.1 BER 编码（兼容真实 IED）。</para>
        /// </summary>
        public bool UseRealMms { get; set; }

        /// <summary>COTP 协议类别。</summary>
        public CotpClass CotpProtocolClass { get; set; } = CotpClass.Class4;

        /// <summary>已协商的最大 MMS PDU 大小。</summary>
        public int NegotiatedMaxPduSize { get; private set; } = 65000;

        /// <summary>MMS InvokeId 计数器。</summary>
        private ushort _invokeId;

        /// <inheritdoc/>
        protected override int ResponseHeaderLength => 8;

        /// <inheritdoc/>
        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 8) return 0;
            int len = (header[6] << 8) | header[7];
            return len - 6;
        }

        public Iec61850Client(string ip, int port = 102, int timeout = 5000)
            : base(ip, port, timeout)
        {
        }

        // ═══════════════════════════════════════════
        //  TPKT + COTP 传输层（UseRealMms=true 时使用）
        // ═══════════════════════════════════════════

        /// <summary>构建 TPKT 帧（RFC 1006）。</summary>
        private byte[] BuildTPKT(byte[] payload)
        {
            int total = 4 + payload.Length;
            byte[] frame = new byte[total];
            frame[0] = 0x03;
            frame[1] = 0x00;
            frame[2] = (byte)(total >> 8);
            frame[3] = (byte)total;
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        /// <summary>构建 COTP Connection Request。</summary>
        private byte[] BuildCotpConnectionRequest()
        {
            // COTP CR: DST-REF(2) + SRC-REF(2) + CLASS(1) + params
            byte[] cr = new byte[]
            {
                0x11,       // COTP length
                0xE0,       // CR PDU type
                0x00, 0x00, // DST-REF
                0x00, 0x01, // SRC-REF
                0x00,       // Class + options
                // TPDU size parameter
                0xC0, 0x01, 0x0A, // TPDU size = 1024
                // TSEL calling
                0xC1, 0x02, 0x00, 0x01,
                // TSEL called
                0xC2, 0x02, 0x00, 0x01,
            };

            // Set class bits
            cr[6] = (byte)((byte)CotpProtocolClass & 0x0F);

            return BuildTPKT(cr);
        }

        /// <summary>构建 COTP Data 帧。</summary>
        private byte[] BuildCotpData(byte[] mmsPayload)
        {
            byte[] cotpData = new byte[3 + mmsPayload.Length];
            cotpData[0] = 0x02;   // COTP DT length
            cotpData[1] = 0xF0;   // DT PDU type
            cotpData[2] = 0x80;   // TPDU number
            Buffer.BlockCopy(mmsPayload, 0, cotpData, 3, mmsPayload.Length);
            return BuildTPKT(cotpData);
        }

        /// <summary>读取 TPKT 帧。</summary>
        private OperateResult<byte[]> ReadTPKT()
        {
            if (_stream == null)
                return OperateResult<byte[]>.Failed("未连接");

            byte[] header = new byte[4];
            int read = 0;
            while (read < 4)
            {
                int n = _stream.Read(header, read, 4 - read);
                if (n == 0) return OperateResult<byte[]>.Failed("读取 TPKT 头失败");
                read += n;
            }

            if (header[0] != 0x03)
                return OperateResult<byte[]>.Failed($"TPKT 版本错误: 0x{header[0]:X2}");

            int totalLen = (header[2] << 8) | header[3];
            if (totalLen < 4 || totalLen > 65535)
                return OperateResult<byte[]>.Failed($"TPKT 长度异常: {totalLen}");

            int payloadLen = totalLen - 4;
            byte[] payload = new byte[payloadLen];
            read = 0;
            while (read < payloadLen)
            {
                int n = _stream.Read(payload, read, payloadLen - read);
                if (n == 0) return OperateResult<byte[]>.Failed("读取 TPKT 载荷失败");
                read += n;
            }

            return OperateResult<byte[]>.Success(payload);
        }

        /// <summary>通过 COTP 数据通道发送 MMS PDU 并接收响应。</summary>
        private OperateResult<byte[]> SendMmsPdu(byte[] mmsPdu)
        {
            byte[] frame = BuildCotpData(mmsPdu);
            var sendResult = SendAndReceive(frame);
            if (!sendResult.IsSuccess) return sendResult;

            // 响应格式: COTP DT (3 bytes) + MMS PDU
            byte[] resp = sendResult.Content;
            if (resp.Length < 3)
                return OperateResult<byte[]>.Failed("COTP 响应过短");

            // 跳过 COTP DT 头
            byte[] mmsResp = new byte[resp.Length - 3];
            Buffer.BlockCopy(resp, 3, mmsResp, 0, mmsResp.Length);
            return OperateResult<byte[]>.Success(mmsResp);
        }

        /// <summary>递增 InvokeId。</summary>
        private ushort NextInvokeId()
        {
            return _invokeId++;
        }

        // ═══════════════════════════════════════════
        //  连接建立（重写 Connect）
        // ═══════════════════════════════════════════

        /// <summary>
        /// 连接到 IED。
        /// <para>当 UseRealMms=true 时，执行完整的 TCP + COTP + MMS Associate 握手。</para>
        /// </summary>
        public override OperateResult Connect()
        {
            var conn = base.Connect();
            if (!conn.IsSuccess) return conn;

            if (!UseRealMms)
                return OperateResult.Success();

            bool wasPersistent = _persistentMode;
            _persistentMode = true;
            try
            {
                // 阶段1: COTP Connection Request
                var crReq = BuildCotpConnectionRequest();
                var crResp = SendAndReceive(crReq);
                if (!crResp.IsSuccess)
                    return OperateResult.Failed("COTP 连接失败: " + crResp.Message);

                // 阶段2: MMS Associate Request
                var assocPdu = Asn1BerCodec.BuildAssociateRequest(NextInvokeId(), "1.0.9506.2.1");
                byte[] assocFrame = BuildCotpData(assocPdu);
                var assocResp = SendAndReceive(assocFrame);
                if (!assocResp.IsSuccess)
                    return OperateResult.Failed("MMS Associate 失败: " + assocResp.Message);

                // 解析 Associate 响应（可选 — 提取协商的 PDU 大小）
                byte[] assocData = assocResp.Content;
                if (assocData.Length > 3)
                {
                    byte[] mmsResp = new byte[assocData.Length - 3];
                    Buffer.BlockCopy(assocData, 3, mmsResp, 0, mmsResp.Length);
                    try
                    {
                        var pduInfo = Asn1BerCodec.DecodeMmsPdu(mmsResp);
                        if (pduInfo.PduType == MmsPduType.ConfirmedResponse)
                        {
                            // 成功 — IED 接受了关联
                        }
                    }
                    catch
                    {
                        // 解析失败但连接已建立，继续
                    }
                }

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed("MMS Associate 异常: " + ex.Message);
            }
            finally
            {
                _persistentMode = wasPersistent;
            }
        }

        /// <summary>
        /// 释放 MMS 连接（发送 Release-Request）。
        /// </summary>
        public OperateResult Release()
        {
            if (!UseRealMms) return OperateResult.Success();

            try
            {
                var releasePdu = Asn1BerCodec.BuildReleaseRequest(NextInvokeId());
                byte[] releaseFrame = BuildCotpData(releasePdu);
                var resp = SendAndReceive(releaseFrame);
                if (!resp.IsSuccess)
                    return OperateResult.Failed("MMS Release 失败: " + resp.Message);
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ═══════════════════════════════════════════
        //  MMS 请求构建
        // ═══════════════════════════════════════════

        /// <summary>构建 MMS GetDataValues 请求。</summary>
        public static byte[] BuildGetDataValuesRequest(string ldName, string lnName, string dataName, FunctionalConstraint fc)
        {
            // 简化的 MMS 帧: 服务类型(1) + InvokeId(4) + LD(32-len) + LN(32-len) + Data(32-len) + FC(1)
            byte[] request = new byte[102];
            request[0] = 0x03; // GetDataValues service
            // Invoke ID (4 bytes)
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x01;
            // LD name (32 bytes, null-padded)
            WritePaddedString(request, 5, 32, ldName);
            // LN name (32 bytes)
            WritePaddedString(request, 37, 32, lnName);
            // Data name (32 bytes)
            WritePaddedString(request, 69, 32, dataName);
            // FC (1 byte)
            request[101] = (byte)fc;
            return request;
        }

        /// <summary>构建 MMS SetDataValues 请求。</summary>
        public static byte[] BuildSetDataValuesRequest(string ldName, string lnName, string dataName, FunctionalConstraint fc, byte[] value)
        {
            byte[] request = new byte[103 + value.Length];
            request[0] = 0x04; // SetDataValues service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x02;
            WritePaddedString(request, 5, 32, ldName);
            WritePaddedString(request, 37, 32, lnName);
            WritePaddedString(request, 69, 32, dataName);
            request[101] = (byte)fc;
            // Value length + value
            request[102] = (byte)value.Length;
            if (value.Length > 0)
                Buffer.BlockCopy(value, 0, request, 103, value.Length);
            return request;
        }

        private static void WritePaddedString(byte[] buffer, int offset, int length, string value)
        {
            byte[] strBytes = Encoding.ASCII.GetBytes(value ?? "");
            int copyLen = Math.Min(strBytes.Length, length);
            if (copyLen > 0)
                Buffer.BlockCopy(strBytes, 0, buffer, offset, copyLen);
            for (int i = copyLen; i < length; i++)
                buffer[offset + i] = 0x00;
        }

        // ═══════════════════════════════════════════
        //  数据模型操作
        // ═══════════════════════════════════════════

        /// <summary>构建对象引用路径 (LD/LN.DO.DA)。</summary>
        public static string BuildObjectReference(string ld, string ln, string dataName, string? daName = null)
        {
            string ref_ = $"{ld}/{ln}.{dataName}";
            if (daName != null) ref_ += $".{daName}";
            return ref_;
        }

        /// <summary>解析对象引用路径。</summary>
        public static (string ld, string ln, string data, string? da) ParseObjectReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("对象引用不能为空");

            string[] parts = reference.Split('/');
            if (parts.Length < 2)
                throw new FormatException($"无效的对象引用格式: {reference}");

            string ld = parts[0];
            string rest = parts[1];
            string[] dotParts = rest.Split('.');
            if (dotParts.Length < 2)
                throw new FormatException($"缺少数据名称: {reference}");

            string ln = dotParts[0];
            string data = dotParts[1];
            string? da = dotParts.Length > 2 ? dotParts[2] : null;

            return (ld, ln, data, da);
        }

        // ═══════════════════════════════════════════
        //  数据模型浏览
        // ═══════════════════════════════════════════

        /// <summary>构建 MMS GetServerDirectory 请求。</summary>
        public static byte[] BuildGetServerDirectoryRequest()
        {
            byte[] request = new byte[5];
            request[0] = 0x05; // GetServerDirectory service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x03;
            return request;
        }

        /// <summary>构建 MMS GetLogicalDeviceDirectory 请求。</summary>
        public static byte[] BuildGetLogicalDeviceDirectoryRequest(string ldName)
        {
            byte[] request = new byte[37];
            request[0] = 0x06; // GetLogicalDeviceDirectory service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x04;
            WritePaddedString(request, 5, 32, ldName);
            return request;
        }

        /// <summary>构建 MMS GetLogicalNodeDirectory 请求。</summary>
        public static byte[] BuildGetLogicalNodeDirectoryRequest(string ldName, string lnName)
        {
            byte[] request = new byte[69];
            request[0] = 0x07; // GetLogicalNodeDirectory service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = 0x05;
            WritePaddedString(request, 5, 32, ldName);
            WritePaddedString(request, 37, 32, lnName);
            return request;
        }

        /// <summary>构建 MMS GetDataDirectory 请求。</summary>
        public static byte[] BuildGetDataDirectoryRequest(string objectRef)
        {
            byte[] refBytes = Encoding.ASCII.GetBytes(objectRef ?? "");
            byte[] request = new byte[5 + refBytes.Length];
            request[0] = 0x08; // GetDataDirectory service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)refBytes.Length;
            if (refBytes.Length > 0)
                Buffer.BlockCopy(refBytes, 0, request, 5, refBytes.Length);
            return request;
        }

        /// <summary>获取服务器目录（所有逻辑设备名称）。</summary>
        public OperateResult<string[]> GetServerDirectory()
        {
            try
            {
                byte[] request = BuildGetServerDirectoryRequest();
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<string[]>.Failed(result.Message);

                byte[] resp = result.Content;
                return ParseDirectoryResponse(resp, 0x05);
            }
            catch (Exception ex) { return OperateResult<string[]>.Failed(ex.Message); }
        }

        /// <summary>获取逻辑设备目录（所有逻辑节点名称）。</summary>
        public OperateResult<string[]> GetLogicalDeviceDirectory(string ldName)
        {
            try
            {
                byte[] request = BuildGetLogicalDeviceDirectoryRequest(ldName);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<string[]>.Failed(result.Message);

                byte[] resp = result.Content;
                return ParseDirectoryResponse(resp, 0x06);
            }
            catch (Exception ex) { return OperateResult<string[]>.Failed(ex.Message); }
        }

        /// <summary>获取逻辑节点目录（所有数据对象名称）。</summary>
        public OperateResult<string[]> GetLogicalNodeDirectory(string ldName, string lnName)
        {
            try
            {
                byte[] request = BuildGetLogicalNodeDirectoryRequest(ldName, lnName);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<string[]>.Failed(result.Message);

                byte[] resp = result.Content;
                return ParseDirectoryResponse(resp, 0x07);
            }
            catch (Exception ex) { return OperateResult<string[]>.Failed(ex.Message); }
        }

        /// <summary>获取数据目录（所有数据属性名称）。</summary>
        public OperateResult<string[]> GetDataDirectory(string objectRef)
        {
            try
            {
                byte[] request = BuildGetDataDirectoryRequest(objectRef);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<string[]>.Failed(result.Message);

                byte[] resp = result.Content;
                return ParseDirectoryResponse(resp, 0x08);
            }
            catch (Exception ex) { return OperateResult<string[]>.Failed(ex.Message); }
        }

        private static OperateResult<string[]> ParseDirectoryResponse(byte[] resp, byte expectedService)
        {
            if (resp == null || resp.Length < 10)
                return OperateResult<string[]>.Failed("目录响应长度不足");

            if (resp[0] != expectedService)
                return OperateResult<string[]>.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

            int count = (resp[8] << 8) | resp[9];
            if (count == 0)
                return OperateResult<string[]>.Success(Array.Empty<string>());

            var names = new List<string>();
            int pos = 10;
            for (int i = 0; i < count && pos < resp.Length; i++)
            {
                if (pos >= resp.Length) break;
                int nameLen = resp[pos++];
                if (nameLen > 0 && pos + nameLen <= resp.Length)
                {
                    names.Add(Encoding.ASCII.GetString(resp, pos, nameLen));
                    pos += nameLen;
                }
            }
            return OperateResult<string[]>.Success(names.ToArray());
        }

        // ═══════════════════════════════════════════
        //  报告控制块操作
        // ═══════════════════════════════════════════

        /// <summary>构建 EnableReports 请求。</summary>
        public static byte[] BuildEnableReportsRequest(string rcbReference, string datasetReference)
        {
            byte[] rcbBytes = Encoding.ASCII.GetBytes(rcbReference ?? "");
            byte[] dsBytes = Encoding.ASCII.GetBytes(datasetReference ?? "");
            byte[] request = new byte[5 + rcbBytes.Length + 1 + dsBytes.Length];
            request[0] = 0x09; // EnableReports service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)rcbBytes.Length;
            if (rcbBytes.Length > 0)
                Buffer.BlockCopy(rcbBytes, 0, request, 5, rcbBytes.Length);
            int pos = 5 + rcbBytes.Length;
            request[pos] = (byte)dsBytes.Length;
            pos++;
            if (dsBytes.Length > 0)
                Buffer.BlockCopy(dsBytes, 0, request, pos, dsBytes.Length);
            return request;
        }

        /// <summary>构建 DisableReports 请求。</summary>
        public static byte[] BuildDisableReportsRequest(string rcbReference)
        {
            byte[] rcbBytes = Encoding.ASCII.GetBytes(rcbReference ?? "");
            byte[] request = new byte[5 + rcbBytes.Length];
            request[0] = 0x0A; // DisableReports service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)rcbBytes.Length;
            if (rcbBytes.Length > 0)
                Buffer.BlockCopy(rcbBytes, 0, request, 5, rcbBytes.Length);
            return request;
        }

        /// <summary>启用报告。</summary>
        public OperateResult EnableReports(string rcbReference, string datasetReference)
        {
            try
            {
                byte[] request = BuildEnableReportsRequest(rcbReference, datasetReference);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("启用报告响应长度不足");

                if (resp[0] != 0x09)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>禁用报告。</summary>
        public OperateResult DisableReports(string rcbReference)
        {
            try
            {
                byte[] request = BuildDisableReportsRequest(rcbReference);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("禁用报告响应长度不足");

                if (resp[0] != 0x0A)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  控制操作
        // ═══════════════════════════════════════════

        /// <summary>构建 Select 请求。</summary>
        public static byte[] BuildSelectRequest(string objectRef)
        {
            byte[] refBytes = Encoding.ASCII.GetBytes(objectRef ?? "");
            byte[] request = new byte[5 + refBytes.Length];
            request[0] = 0x0B; // Select service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)refBytes.Length;
            if (refBytes.Length > 0)
                Buffer.BlockCopy(refBytes, 0, request, 5, refBytes.Length);
            return request;
        }

        /// <summary>构建 Operate 请求。</summary>
        public static byte[] BuildOperateRequest(string objectRef, byte[] value)
        {
            byte[] refBytes = Encoding.ASCII.GetBytes(objectRef ?? "");
            byte[] request = new byte[5 + refBytes.Length + 1 + value.Length];
            request[0] = 0x0C; // Operate service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)refBytes.Length;
            if (refBytes.Length > 0)
                Buffer.BlockCopy(refBytes, 0, request, 5, refBytes.Length);
            int pos = 5 + refBytes.Length;
            request[pos] = (byte)value.Length;
            pos++;
            if (value.Length > 0)
                Buffer.BlockCopy(value, 0, request, pos, value.Length);
            return request;
        }

        /// <summary>构建 Cancel 请求。</summary>
        public static byte[] BuildCancelRequest(string objectRef)
        {
            byte[] refBytes = Encoding.ASCII.GetBytes(objectRef ?? "");
            byte[] request = new byte[5 + refBytes.Length];
            request[0] = 0x0D; // Cancel service
            request[1] = 0x00;
            request[2] = 0x00;
            request[3] = 0x00;
            request[4] = (byte)refBytes.Length;
            if (refBytes.Length > 0)
                Buffer.BlockCopy(refBytes, 0, request, 5, refBytes.Length);
            return request;
        }

        /// <summary>选择对象（SBO 模式）。</summary>
        public OperateResult Select(string objectRef)
        {
            try
            {
                byte[] request = BuildSelectRequest(objectRef);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("选择响应长度不足");

                if (resp[0] != 0x0B)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>执行控制操作。</summary>
        public OperateResult Operate(string objectRef, object value)
        {
            try
            {
                byte[] valueBytes = ConvertControlValue(value);
                byte[] request = BuildOperateRequest(objectRef, valueBytes);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("操作响应长度不足");

                if (resp[0] != 0x0C)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        /// <summary>取消控制操作。</summary>
        public OperateResult Cancel(string objectRef)
        {
            try
            {
                byte[] request = BuildCancelRequest(objectRef);
                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("取消响应长度不足");

                if (resp[0] != 0x0D)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex) { return OperateResult.Failed(ex.Message); }
        }

        private static byte[] ConvertControlValue(object value)
        {
            return value switch
            {
                bool b => new byte[] { (byte)(b ? 1 : 0) },
                short s => BitConverter.GetBytes(s),
                ushort us => BitConverter.GetBytes(us),
                int i => BitConverter.GetBytes(i),
                uint ui => BitConverter.GetBytes(ui),
                float f => BitConverter.GetBytes(f),
                double d => BitConverter.GetBytes(d),
                byte[] b => b,
                string s => Encoding.ASCII.GetBytes(s),
                _ => throw new ArgumentException($"不支持的控制值类型: {value?.GetType().Name}")
            };
        }

        // ═══════════════════════════════════════════
        //  增强数据读取
        // ═══════════════════════════════════════════

        /// <summary>读取带质量戳和时间戳的数据值。</summary>
        public OperateResult<TimestampedValue> ReadTimestamped(string address, FunctionalConstraint fc = FunctionalConstraint.MX)
        {
            try
            {
                var (ld, ln, data, da) = ParseObjectReference(address);
                byte[] request = BuildGetDataValuesRequest(ld, ln, data, fc);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<TimestampedValue>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 10)
                    return OperateResult<TimestampedValue>.Failed("MMS 响应长度不足");

                if (resp[0] != 0x03)
                    return OperateResult<TimestampedValue>.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                QualityStamp quality = QualityStamp.Valid;
                DateTime timestamp = DateTime.UtcNow;
                object? value = null;

                if (resp.Length >= 12)
                {
                    quality = (QualityStamp)((resp[10] << 8) | resp[11]);
                }
                if (resp.Length >= 20)
                {
                    long ticks = 0;
                    for (int i = 12; i < 20; i++)
                        ticks = (ticks << 8) | resp[i];
                    timestamp = new DateTime(ticks, DateTimeKind.Utc);
                }

                int dataLen = resp.Length - 10;
                if (dataLen > 0)
                {
                    byte[] dataBytes = new byte[dataLen];
                    Buffer.BlockCopy(resp, 10, dataBytes, 0, dataLen);
                    value = dataBytes;
                }

                return OperateResult<TimestampedValue>.Success(new TimestampedValue
                {
                    Value = value,
                    Quality = quality,
                    Timestamp = timestamp,
                });
            }
            catch (Exception ex) { return OperateResult<TimestampedValue>.Failed(ex.Message); }
        }

        // ═══════════════════════════════════════════
        //  BER 模式数据读写（UseRealMms=true）
        // ═══════════════════════════════════════════

        private OperateResult<byte[]> ReadBytesViaBer(string address, ushort length)
        {
            try
            {
                var (ld, ln, data, da) = ParseObjectReference(address);
                string objectRef = BuildObjectReference(ld, ln, data, da);

                byte[] servicePdu = Asn1BerCodec.BuildGetDataValuesRequest(objectRef);
                byte[] mmsPdu = Asn1BerCodec.BuildConfirmedRequest(NextInvokeId(), servicePdu);

                var result = SendMmsPdu(mmsPdu);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp.Length < 4)
                    return OperateResult<byte[]>.Failed("MMS 响应过短");

                var pduInfo = Asn1BerCodec.DecodeMmsPdu(resp);
                if (pduInfo.PduType == MmsPduType.ConfirmedError)
                    return OperateResult<byte[]>.Failed("MMS 服务错误");

                // 解析 Read-Response 中的数据值
                int pos = pduInfo.ContentOffset;
                if (pos >= resp.Length)
                    return OperateResult<byte[]>.Success(new byte[0]);

                // 跳过 invokeId TLV
                var idTag = Asn1BerCodec.DecodeTag(resp, pos);
                pos = idTag.ContentOffset + idTag.Length;

                // Read Response body: listOfAccessResult
                if (pos >= resp.Length)
                    return OperateResult<byte[]>.Success(new byte[0]);

                var resultTag = Asn1BerCodec.DecodeTag(resp, pos);
                if (resultTag.Tag == 0xA0) // listOfAccessResult
                {
                    int innerPos = resultTag.ContentOffset;
                    if (innerPos < resp.Length)
                    {
                        var dataTag = Asn1BerCodec.DecodeTag(resp, innerPos);
                        if (dataTag.Tag == 0xA0 || dataTag.Tag == Asn1BerCodec.TagOctetString)
                        {
                            byte[] value = Asn1BerCodec.DecodeOctetString(resp, dataTag.ContentOffset, dataTag.Length);
                            if (value.Length >= length * 2)
                                return OperateResult<byte[]>.Success(value);
                            return OperateResult<byte[]>.Success(value);
                        }
                    }
                }

                return OperateResult<byte[]>.Success(new byte[0]);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        private OperateResult WriteViaBer(string address, byte[] data)
        {
            try
            {
                var (ld, ln, dataName, da) = ParseObjectReference(address);
                string objectRef = BuildObjectReference(ld, ln, dataName, da);

                byte[] servicePdu = Asn1BerCodec.BuildSetDataValuesRequest(objectRef, data);
                byte[] mmsPdu = Asn1BerCodec.BuildConfirmedRequest(NextInvokeId(), servicePdu);

                var result = SendMmsPdu(mmsPdu);
                if (!result.IsSuccess) return OperateResult.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp.Length < 4)
                    return OperateResult.Failed("MMS 响应过短");

                var pduInfo = Asn1BerCodec.DecodeMmsPdu(resp);
                if (pduInfo.PduType == MmsPduType.ConfirmedError)
                    return OperateResult.Failed("MMS 写入服务错误");

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ═══════════════════════════════════════════
        //  IReadWriteDevice
        // ═══════════════════════════════════════════

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            if (UseRealMms)
                return ReadBytesViaBer(address, length);

            try
            {
                var (ld, ln, data, da) = ParseObjectReference(address);
                byte[] request = BuildGetDataValuesRequest(ld, ln, data, FunctionalConstraint.MX);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return OperateResult<byte[]>.Failed(result.Message);

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 10)
                    return OperateResult<byte[]>.Failed("MMS 响应长度不足");

                // 检查服务错误
                if (resp[0] != 0x03)
                    return OperateResult<byte[]>.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                // 提取数据值
                int dataLen = resp.Length - 10;
                if (dataLen <= 0) return OperateResult<byte[]>.Success(new byte[0]);
                byte[] value = new byte[dataLen];
                Buffer.BlockCopy(resp, 10, value, 0, dataLen);

                int requestedBytes = Math.Max(1, (int)length) * 2;
                if (value.Length < requestedBytes)
                    return OperateResult<byte[]>.Failed($"MMS 响应数据不足，需要 {requestedBytes} 字节，实际 {value.Length} 字节");
                if (value.Length > requestedBytes)
                {
                    byte[] sliced = new byte[requestedBytes];
                    Buffer.BlockCopy(value, 0, sliced, 0, requestedBytes);
                    value = sliced;
                }

                return OperateResult<byte[]>.Success(value);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed(ex.Message);
            }
        }

        public override OperateResult Write(string address, byte[] data)
        {
            if (UseRealMms)
                return WriteViaBer(address, data);

            try
            {
                var (ld, ln, dataName, da) = ParseObjectReference(address);
                byte[] request = BuildSetDataValuesRequest(ld, ln, dataName, FunctionalConstraint.SP, data);

                var result = SendAndReceive(request);
                if (!result.IsSuccess) return result;

                byte[] resp = result.Content;
                if (resp == null || resp.Length < 2)
                    return OperateResult.Failed("MMS 写入响应长度不足");

                if (resp[0] != 0x04)
                    return OperateResult.Failed(Iec61850ErrorCodes.GetServiceErrorDescription(resp[1]));

                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed(ex.Message);
            }
        }

        // ── 高层方法 ──

        public override OperateResult<bool> ReadBool(string address) => ReadValueSafe<bool>(address, 1, d => d[0] != 0);
        public override OperateResult<short> ReadInt16(string address) => ReadValueSafe<short>(address, 1, d => BitConverter.ToInt16(d, 0));
        public override OperateResult<ushort> ReadUInt16(string address) => ReadValueSafe<ushort>(address, 1, d => BitConverter.ToUInt16(d, 0));
        public override OperateResult<int> ReadInt32(string address) => ReadValueSafe<int>(address, 2, d => BitConverter.ToInt32(d, 0));
        public override OperateResult<uint> ReadUInt32(string address) => ReadValueSafe<uint>(address, 2, d => BitConverter.ToUInt32(d, 0));
        public override OperateResult<long> ReadInt64(string address) => ReadValueSafe<long>(address, 4, d => BitConverter.ToInt64(d, 0));
        public override OperateResult<ulong> ReadUInt64(string address) => ReadValueSafe<ulong>(address, 4, d => BitConverter.ToUInt64(d, 0));
        public override OperateResult<float> ReadFloat(string address) => ReadValueSafe<float>(address, 2, d => BitConverter.ToSingle(d, 0));
        public override OperateResult<double> ReadDouble(string address) => ReadValueSafe<double>(address, 4, d => BitConverter.ToDouble(d, 0));
        public override OperateResult<string> ReadString(string address, ushort length) => ReadValueSafe<string>(address, length, d => Encoding.ASCII.GetString(d).TrimEnd('\0'));

        public override OperateResult Write(string address, bool value) => Write(address, new byte[] { (byte)(value ? 1 : 0) });
        public override OperateResult Write(string address, short value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ushort value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, int value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, uint value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, long value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, ulong value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, float value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, double value) => Write(address, BitConverter.GetBytes(value));
        public override OperateResult Write(string address, string value) => Write(address, Encoding.ASCII.GetBytes(value));

        private OperateResult<T> ReadValueSafe<T>(string address, ushort length, Func<byte[], T> converter)
        {
            var result = ReadBytes(address, length);
            if (!result.IsSuccess) return OperateResult<T>.Failed(result.Message);
            try { return OperateResult<T>.Success(converter(result.Content)); }
            catch (Exception ex) { return OperateResult<T>.Failed(ex.Message); }
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

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildGetDataValuesRequest("LD0", "LLN0", "NamPlt", FunctionalConstraint.DC); }
            catch { return null; }
        }
    }
}
