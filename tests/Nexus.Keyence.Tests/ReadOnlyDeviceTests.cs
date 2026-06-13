using Xunit;
using Nexus.Keyence;

namespace Nexus.Keyence.Tests
{
    public class ReadOnlyDeviceTests
    {
        [Fact]
        public void SR2000_WriteBool_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", true);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteInt16_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", (short)1);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteUInt16_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", (ushort)1);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteInt32_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteUInt32_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1u);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteInt64_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1L);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteUInt64_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1UL);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteFloat_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1.0f);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteDouble_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", 1.0d);
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteString_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", "test");
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_WriteBytes_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.Write("addr", new byte[] { 1, 2 });
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_ReadBool_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.ReadBool("addr");
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_ReadInt16_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.ReadInt16("addr");
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_ReadFloat_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.ReadFloat("addr");
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_ReadDouble_ReturnsNotSupported()
        {
            var client = new KeyenceSR2000TcpClient("127.0.0.1");
            var result = client.ReadDouble("addr");
            Assert.False(result.IsSuccess);
            Assert.Contains("不支持", result.Message);
        }

        [Fact]
        public void SR2000_ToString_ContainsInfo()
        {
            var client = new KeyenceSR2000TcpClient("192.168.1.100", 9004);
            var s = client.ToString();
            Assert.Contains("SR-2000", s);
            Assert.Contains("192.168.1.100", s);
        }

        [Fact]
        public void SR2000_Constructor_SetsDefaults()
        {
            var client = new KeyenceSR2000TcpClient("192.168.1.100");
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void SR2000_Constructor_CustomPort()
        {
            var client = new KeyenceSR2000TcpClient("192.168.1.100", 12345);
            Assert.False(client.IsConnected);
        }
    }
}
