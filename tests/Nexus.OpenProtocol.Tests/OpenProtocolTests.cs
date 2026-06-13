using System.Text;
using Nexus;
using Nexus.OpenProtocol;
using Xunit;

namespace Nexus.OpenProtocol.Tests;

public class OpenProtocolBuildMidTests
{
    private static string BuildMidMessage(int mid, int revision, int stationId, int sequenceNumber, string data)
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

    [Fact]
    public void BuildMid_Login_MID0001_Format()
    {
        string msg = BuildMidMessage(1, 1, 0, 0, "user\0pass\0");
        Assert.StartsWith("00", msg);
        Assert.Contains("0001", msg);
        Assert.Equal("001", msg.Substring(8, 3));
    }

    [Fact]
    public void BuildMid_Login_Revision1()
    {
        string msg = BuildMidMessage(1, 1, 0, 0, "");
        string body = msg.Substring(4);
        Assert.Equal("0001", body.Substring(0, 4));
        Assert.Equal("001", body.Substring(4, 3));
    }

    [Fact]
    public void BuildMid_SelectTool_MID0004()
    {
        string msg = BuildMidMessage(4, 1, 0, 0, "01");
        string body = msg.Substring(4);
        Assert.Equal("0004", body.Substring(0, 4));
    }

    [Fact]
    public void BuildMid_TighteningResult_MID0060()
    {
        string msg = BuildMidMessage(60, 1, 0, 0, "");
        string body = msg.Substring(4);
        Assert.Equal("0060", body.Substring(0, 4));
    }

    [Fact]
    public void BuildMid_ControllerInfo_MID0500()
    {
        string msg = BuildMidMessage(500, 1, 0, 0, "");
        string body = msg.Substring(4);
        Assert.Equal("0500", body.Substring(0, 4));
    }

    [Fact]
    public void BuildMid_LengthIncludes4BytesHeader()
    {
        string msg = BuildMidMessage(1, 0, 0, 0, "");
        string lenStr = msg.Substring(0, 4);
        int length = int.Parse(lenStr);
        Assert.Equal(4 + msg.Length - 4, length);
    }

    [Fact]
    public void BuildMid_WithStationAndSequence()
    {
        string msg = BuildMidMessage(60, 1, 5, 3, "");
        string body = msg.Substring(4);
        Assert.Equal("05", body.Substring(8, 2));
        Assert.Equal("03", body.Substring(10, 2));
    }

    [Fact]
    public void BuildMid_NoAckFlag_IsZero()
    {
        string msg = BuildMidMessage(1, 0, 0, 0, "");
        string body = msg.Substring(4);
        Assert.Equal('0', body[7]);
    }
}

public class OpenProtocolParseResponseTests
{
    private static MidResponse ParseFromBytes(byte[] response)
    {
        string lenStr = Encoding.ASCII.GetString(response, 0, 4);
        int length = int.Parse(lenStr);
        string body = Encoding.ASCII.GetString(response, 4, Math.Min(response.Length - 4, length - 4));

        var midResp = new MidResponse();
        if (body.Length >= 4) midResp.Mid = int.Parse(body.Substring(0, 4));
        if (body.Length >= 7) midResp.Revision = int.Parse(body.Substring(4, 3));
        if (body.Length >= 8) midResp.AckFlag = body[7] - '0';
        if (body.Length >= 10) midResp.StationId = int.Parse(body.Substring(8, 2));
        if (body.Length >= 12) midResp.SequenceNumber = int.Parse(body.Substring(10, 2));
        if (body.Length > 12) midResp.Data = body.Substring(12);
        return midResp;
    }

    private static byte[] BuildResponse(int mid, int revision, int ackFlag, int stationId, int seqNum, string data = "")
    {
        string body = $"{mid:D4}{revision:D3}{ackFlag}{stationId:D2}{seqNum:D2}{data}";
        int length = 4 + body.Length;
        return Encoding.ASCII.GetBytes($"{length:D4}{body}");
    }

    [Fact]
    public void Parse_PositiveAck_LoginResponse()
    {
        byte[] resp = BuildResponse(1, 1, 0, 0, 0);
        var r = ParseFromBytes(resp);
        Assert.Equal(1, r.Mid);
        Assert.Equal(1, r.Revision);
        Assert.Equal(0, r.AckFlag);
        Assert.True(r.IsPositiveAck);
    }

    [Fact]
    public void Parse_NegativeAck_LoginFailed()
    {
        byte[] resp = BuildResponse(1, 1, 1, 0, 0);
        var r = ParseFromBytes(resp);
        Assert.Equal(1, r.AckFlag);
        Assert.False(r.IsPositiveAck);
    }

    [Fact]
    public void Parse_ToolSelection_MID0004()
    {
        byte[] resp = BuildResponse(4, 1, 0, 0, 0);
        var r = ParseFromBytes(resp);
        Assert.Equal(4, r.Mid);
        Assert.True(r.IsPositiveAck);
    }

    [Fact]
    public void Parse_WithStationAndSequence()
    {
        byte[] resp = BuildResponse(60, 1, 0, 5, 3);
        var r = ParseFromBytes(resp);
        Assert.Equal(5, r.StationId);
        Assert.Equal(3, r.SequenceNumber);
    }

    [Fact]
    public void Parse_WithDataPayload()
    {
        byte[] resp = BuildResponse(500, 1, 0, 0, 0, "ControllerType123");
        var r = ParseFromBytes(resp);
        Assert.Equal("ControllerType123", r.Data);
    }

    [Fact]
    public void Parse_EmptyData()
    {
        byte[] resp = BuildResponse(60, 1, 0, 0, 0);
        var r = ParseFromBytes(resp);
        Assert.Equal("", r.Data);
    }

    [Fact]
    public void MidResponse_DefaultValues()
    {
        var r = new MidResponse();
        Assert.Equal(0, r.Mid);
        Assert.Equal(0, r.Revision);
        Assert.Equal(0, r.AckFlag);
        Assert.Equal(0, r.StationId);
        Assert.Equal(0, r.SequenceNumber);
        Assert.Equal("", r.Data);
        Assert.True(r.IsPositiveAck);
    }

    [Fact]
    public void Parse_TighteningResult_WithDataFields()
    {
        string tighteningData = "00001 0000200 00300 NORM";
        byte[] resp = BuildResponse(61, 1, 0, 0, 0, tighteningData);
        var r = ParseFromBytes(resp);
        Assert.Equal(61, r.Mid);
        Assert.Contains("NORM", r.Data);
    }
}
