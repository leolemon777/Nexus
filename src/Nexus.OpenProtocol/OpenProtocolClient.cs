using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.OpenProtocol
{
    /// <summary>
    /// Atlas Copco Open Protocol 通讯客户端。
    /// <para>帧格式: Length(4) + MID(4) + Revision(3) + NoAckFlag(1) + StationID(2) + SequenceNumber(2) + Data(N)</para>
    /// <para>Length 包含自身 4 字节。MID 0001=登录, MID 0004=选择工具, MID 0060=拧紧结果等。</para>
    /// </summary>
    public class OpenProtocolClient : TcpDeviceBase
    {
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header)
        {
            string lenStr = Encoding.ASCII.GetString(header, 0, 4);
            if (int.TryParse(lenStr, out int length) && length > 4)
                return length - 4;
            return 0;
        }

        public OpenProtocolClient(string ip, int port, int timeout = 5000)
            : base(ip, port, timeout) { }

        public OperateResult<MidResponse> SendMid(int mid, int revision = 0, int stationId = 0,
            int sequenceNumber = 0, string data = "")
        {
            string message = BuildMidMessage(mid, revision, stationId, sequenceNumber, data);
            byte[] request = Encoding.ASCII.GetBytes(message);

            var result = SendAndReceive(request);
            if (!result.IsSuccess) return OperateResult<MidResponse>.Failed(result.Message);

            return ParseMidResponse(result.Content);
        }

        public OperateResult Login(string username, string password)
        {
            string data = $"{username}\0{password}\0";
            var r = SendMid(1, 1, 0, 0, data);
            if (!r.IsSuccess) return OperateResult.Failed(r.Message);
            return r.Content.IsPositiveAck
                ? OperateResult.Success()
                : OperateResult.Failed($"登录失败: MID{r.Content.Mid:D4}, Ack={r.Content.AckFlag}");
        }

        public OperateResult SelectTool(int toolNumber)
        {
            var r = SendMid(4, 1, 0, 0, toolNumber.ToString("D2"));
            if (!r.IsSuccess) return OperateResult.Failed(r.Message);
            return r.Content.IsPositiveAck
                ? OperateResult.Success()
                : OperateResult.Failed($"选择工具失败: MID{r.Content.Mid:D4}");
        }

        public OperateResult<MidResponse> GetTighteningResult()
        {
            return SendMid(60, 1);
        }

        public OperateResult<MidResponse> GetControllerInfo()
        {
            return SendMid(500, 1);
        }

        public OperateResult<MidResponse> GetToolData()
        {
            return SendMid(501, 1);
        }

        public OperateResult<MidResponse> SendCustomMid(int mid, string data = "", int revision = 0)
        {
            return SendMid(mid, revision, 0, 0, data);
        }

        private string BuildMidMessage(int mid, int revision, int stationId, int sequenceNumber, string data)
        {
            string midStr = mid.ToString("D4");
            string revStr = revision.ToString("D3");
            string noAck = "0";
            string stationStr = stationId.ToString("D2");
            string seqStr = sequenceNumber.ToString("D2");

            string body = midStr + revStr + noAck + stationStr + seqStr + data;
            int length = 4 + body.Length;
            return length.ToString("D4") + body;
        }

        private static OperateResult<MidResponse> ParseMidResponse(byte[] response)
        {
            if (response == null || response.Length < 4)
                return OperateResult<MidResponse>.Failed("响应过短");

            string lenStr = Encoding.ASCII.GetString(response, 0, 4);
            if (!int.TryParse(lenStr, out int length))
                return OperateResult<MidResponse>.Failed("长度字段格式错误");

            string body = Encoding.ASCII.GetString(response, 4, Math.Min(response.Length - 4, length - 4));

            var midResp = new MidResponse();
            if (body.Length >= 4) midResp.Mid = int.TryParse(body.Substring(0, 4), out int m) ? m : 0;
            if (body.Length >= 7) midResp.Revision = int.TryParse(body.Substring(4, 3), out int r) ? r : 0;
            if (body.Length >= 8) midResp.AckFlag = body[7] - '0';
            if (body.Length >= 10) midResp.StationId = int.TryParse(body.Substring(8, 2), out int s) ? s : 0;
            if (body.Length >= 12) midResp.SequenceNumber = int.TryParse(body.Substring(10, 2), out int q) ? q : 0;
            if (body.Length > 12) midResp.Data = body.Substring(12);

            return OperateResult<MidResponse>.Success(midResp);
        }

        public override OperateResult<bool> ReadBool(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<bool>.Failed(r.Message);
            return OperateResult<bool>.Success(r.Content != 0);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            return OperateResult<short>.Success((short)r.Content);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadInt32(address);
            if (!r.IsSuccess) return OperateResult<ushort>.Failed(r.Message);
            return OperateResult<ushort>.Success((ushort)r.Content);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = GetTighteningResult();
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            return OperateResult<int>.Success(0);
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
            => OperateResult<float>.Failed("Open Protocol 不支持浮点读取，请使用 ReadString 获取拧紧结果");

        public override OperateResult<double> ReadDouble(string address)
            => OperateResult<double>.Failed("Open Protocol 不支持浮点读取，请使用 ReadString 获取拧紧结果");

        public override OperateResult<string> ReadString(string address, ushort length)
        {
            var r = GetControllerInfo();
            if (!r.IsSuccess) return OperateResult<string>.Failed(r.Message);
            return OperateResult<string>.Success(r.Content.Data ?? "");
        }

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
        {
            var r = ReadString(address, length);
            if (!r.IsSuccess) return OperateResult<byte[]>.Failed(r.Message);
            return OperateResult<byte[]>.Success(Encoding.ASCII.GetBytes(r.Content));
        }

        public override OperateResult Write(string address, bool value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, short value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, ushort value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, int value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, uint value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, long value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, ulong value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, float value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, double value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("Open Protocol 不支持写入操作");

        public override string ToString() => $"OpenProtocolClient[{Ip}:{Port}]";
    }

    public class MidResponse
    {
        public int Mid { get; set; }
        public int Revision { get; set; }
        public int AckFlag { get; set; }
        public int StationId { get; set; }
        public int SequenceNumber { get; set; }
        public string Data { get; set; } = "";

        public bool IsPositiveAck => AckFlag == 0;
    }
}
