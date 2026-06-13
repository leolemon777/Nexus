using Xunit;
using Nexus.Yamatake;
using System;

namespace Nexus.Yamatake.Tests
{
    public class YamatakeCplAddressTests
    {
        [Fact]
        public void Parse_HexAddress()
        {
            var addr = YamatakeCplAddress.Parse("0100");
            Assert.Equal(0x0100, addr.Address);
            Assert.Equal(1, addr.Station);
        }

        [Fact]
        public void Parse_DecimalAddress()
        {
            var addr = YamatakeCplAddress.Parse("999");
            Assert.Equal(0x999, addr.Address);
        }

        [Fact]
        public void Parse_WithStationPrefix()
        {
            var addr = YamatakeCplAddress.Parse("s=3;0100");
            Assert.Equal(3, addr.Station);
            Assert.Equal(0x0100, addr.Address);
        }

        [Fact]
        public void Parse_DefaultStation()
        {
            var addr = YamatakeCplAddress.Parse("0200", 5);
            Assert.Equal(5, addr.Station);
            Assert.Equal(0x0200, addr.Address);
        }

        [Fact]
        public void Parse_Empty_Throws()
        {
            Assert.Throws<AddressParseException>(() => YamatakeCplAddress.Parse(""));
        }

        [Fact]
        public void Parse_Null_Throws()
        {
            Assert.Throws<AddressParseException>(() => YamatakeCplAddress.Parse(null!));
        }

        [Fact]
        public void Parse_InvalidHex_Throws()
        {
            Assert.Throws<AddressParseException>(() => YamatakeCplAddress.Parse("ZZZZ"));
        }

        [Fact]
        public void Parse_OutOfRange_Throws()
        {
            Assert.Throws<AddressParseException>(() => YamatakeCplAddress.Parse("10000"));
        }

        [Fact]
        public void TryParse_Valid_ReturnsTrue()
        {
            bool ok = YamatakeCplAddress.TryParse("0100", out var parsed);
            Assert.True(ok);
            Assert.NotNull(parsed);
            Assert.Equal(0x0100, parsed!.Address);
        }

        [Fact]
        public void TryParse_Invalid_ReturnsFalse()
        {
            bool ok = YamatakeCplAddress.TryParse("ZZZZ", out var parsed);
            Assert.False(ok);
            Assert.Null(parsed);
        }

        [Fact]
        public void ToHexString()
        {
            var addr = YamatakeCplAddress.Parse("0100");
            Assert.Equal("0100", addr.ToHexString());
        }

        [Fact]
        public void ToHexString_PadsTo4()
        {
            var addr = YamatakeCplAddress.Parse("10");
            Assert.Equal("0010", addr.ToHexString());
        }
    }

    public class YamatakeCplProtocolTests
    {
        [Fact]
        public void BuildReadCommand_Format()
        {
            byte[] cmd = YamatakeCplSerialClient.BuildReadCommand(1, 0x0100, 1);
            Assert.NotNull(cmd);
            Assert.True(cmd.Length > 0);
            Assert.Equal(0x02, cmd[0]);
            Assert.Equal(0x03, cmd[cmd.Length - 3]);
        }

        [Fact]
        public void BuildReadCommand_ContainsStationAndAddress()
        {
            byte[] cmd = YamatakeCplSerialClient.BuildReadCommand(0x01, 0x0100, 1);
            string ascii = System.Text.Encoding.ASCII.GetString(cmd, 1, cmd.Length - 4);
            Assert.StartsWith("01R0100", ascii);
        }

        [Fact]
        public void BuildWriteCommand_Format()
        {
            byte[] cmd = YamatakeCplSerialClient.BuildWriteCommand(1, 0x0100, new short[] { 100 });
            Assert.NotNull(cmd);
            Assert.Equal(0x02, cmd[0]);
            Assert.Equal(0x03, cmd[cmd.Length - 3]);
        }

        [Fact]
        public void BuildWriteCommand_MultipleValues()
        {
            byte[] cmd = YamatakeCplSerialClient.BuildWriteCommand(1, 0x0200, new short[] { 100, 200 });
            Assert.NotNull(cmd);
            Assert.True(cmd.Length > 0);
        }

        [Fact]
        public void ParseReadResponse_ValidResponse()
        {
            string body = "01R000064";
            byte[] bodyBytes = System.Text.Encoding.ASCII.GetBytes(body);
            byte[] response = new byte[1 + bodyBytes.Length + 1 + 2];
            response[0] = 0x02;
            Buffer.BlockCopy(bodyBytes, 0, response, 1, bodyBytes.Length);
            response[1 + bodyBytes.Length] = 0x03;
            byte bcc = 0;
            for (int i = 1; i <= bodyBytes.Length + 1; i++)
                bcc ^= response[i];
            string bccHex = bcc.ToString("X2");
            response[response.Length - 2] = (byte)bccHex[0];
            response[response.Length - 1] = (byte)bccHex[1];

            var result = YamatakeCplSerialClient.ParseReadResponse(response, 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(100, result.Content);
        }

        [Fact]
        public void ParseReadResponseMultiple_TwoWords()
        {
            string body = "01R00006400C8";
            byte[] bodyBytes = System.Text.Encoding.ASCII.GetBytes(body);
            byte[] response = new byte[1 + bodyBytes.Length + 1 + 2];
            response[0] = 0x02;
            Buffer.BlockCopy(bodyBytes, 0, response, 1, bodyBytes.Length);
            response[1 + bodyBytes.Length] = 0x03;
            byte bcc = 0;
            for (int i = 1; i <= bodyBytes.Length + 1; i++)
                bcc ^= response[i];
            string bccHex = bcc.ToString("X2");
            response[response.Length - 2] = (byte)bccHex[0];
            response[response.Length - 1] = (byte)bccHex[1];

            var result = YamatakeCplSerialClient.ParseReadResponseMultiple(response, 2);
            Assert.True(result.IsSuccess);
            Assert.Equal(2, result.Content.Length);
            Assert.Equal(100, result.Content[0]);
            Assert.Equal(200, result.Content[1]);
        }

        [Fact]
        public void ParseWriteResponse_Success()
        {
            string body = "01W00";
            byte[] bodyBytes = System.Text.Encoding.ASCII.GetBytes(body);
            byte[] response = new byte[1 + bodyBytes.Length + 1 + 2];
            response[0] = 0x02;
            Buffer.BlockCopy(bodyBytes, 0, response, 1, bodyBytes.Length);
            response[1 + bodyBytes.Length] = 0x03;
            byte bcc = 0;
            for (int i = 1; i <= bodyBytes.Length + 1; i++)
                bcc ^= response[i];
            string bccHex = bcc.ToString("X2");
            response[response.Length - 2] = (byte)bccHex[0];
            response[response.Length - 1] = (byte)bccHex[1];

            var result = YamatakeCplSerialClient.ParseWriteResponse(response);
            Assert.True(result.IsSuccess);
        }

        [Fact]
        public void ParseReadResponse_TooShort_Fails()
        {
            var result = YamatakeCplSerialClient.ParseReadResponse(new byte[] { 0x02 }, 1);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void ParseWriteResponse_ErrorCode_Fails()
        {
            string body = "01W02";
            byte[] bodyBytes = System.Text.Encoding.ASCII.GetBytes(body);
            byte[] response = new byte[1 + bodyBytes.Length + 1 + 2];
            response[0] = 0x02;
            Buffer.BlockCopy(bodyBytes, 0, response, 1, bodyBytes.Length);
            response[1 + bodyBytes.Length] = 0x03;
            byte bcc = 0;
            for (int i = 1; i <= bodyBytes.Length + 1; i++)
                bcc ^= response[i];
            string bccHex = bcc.ToString("X2");
            response[response.Length - 2] = (byte)bccHex[0];
            response[response.Length - 1] = (byte)bccHex[1];

            var result = YamatakeCplSerialClient.ParseWriteResponse(response);
            Assert.False(result.IsSuccess);
            Assert.Contains("帧格式错误", result.Message);
        }
    }

    public class YamatakeCplClientTests
    {
        [Fact]
        public void SerialClient_ToString()
        {
            var port = new FakeSerialPort();
            var client = new YamatakeCplSerialClient(port) { Station = 3 };
            Assert.Contains("Station=3", client.ToString());
        }

        [Fact]
        public void OverTcpClient_ToString()
        {
            var client = new YamatakeCplOverTcpClient("127.0.0.1", 5000);
            Assert.Contains("127.0.0.1", client.ToString());
        }
    }

    internal class FakeSerialPort : Nexus.ISerialPort
    {
        public string PortName { get; set; } = "COM_TEST";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Nexus.StopBits StopBits { get; set; } = Nexus.StopBits.One;
        public Nexus.Parity Parity { get; set; } = Nexus.Parity.None;
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
        public bool IsOpen => false;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }
        public int Read(byte[] buffer, int offset, int count) => 0;
        public void Write(byte[] buffer, int offset, int count) { }
        public void Open() { }
        public void Close() { }
        public void Dispose() { }
    }
}
