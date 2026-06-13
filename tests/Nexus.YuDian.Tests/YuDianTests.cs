using Xunit;
using Nexus.YuDian;
using System;

namespace Nexus.YuDian.Tests
{
    public class YuDianClientTests
    {
        [Fact]
        public void Constructor_DefaultStation()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            Assert.Equal(1, client.Station);
        }

        [Fact]
        public void Constructor_CustomStation()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port, station: 5);
            Assert.Equal(5, client.Station);
        }

        [Fact]
        public void ToString_Format()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port, station: 3);
            Assert.Equal("YuDianClient[Station=3]", client.ToString());
        }

        [Fact]
        public void Write_Bool_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.Write("0", true);
            Assert.False(result.IsSuccess);
            Assert.Contains("布尔", result.Message);
        }

        [Fact]
        public void Write_ByteArray_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.Write("0", new byte[] { 1, 2 });
            Assert.False(result.IsSuccess);
            Assert.Contains("字节数组", result.Message);
        }

        [Fact]
        public void Write_String_InvalidValue_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.Write("0", "not_a_number");
            Assert.False(result.IsSuccess);
            Assert.Contains("无法解析", result.Message);
        }

        [Fact]
        public void BatchRead_EmptyList_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.BatchRead(Array.Empty<string>());
            Assert.False(result.IsSuccess);
            Assert.Contains("不能为空", result.Message);
        }

        [Fact]
        public void BatchWrite_EmptyList_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.BatchWrite(Array.Empty<KeyValuePair<string, object>>());
            Assert.False(result.IsSuccess);
            Assert.Contains("不能为空", result.Message);
        }

        [Fact]
        public void RandomRead_EmptyList_ReturnsFailed()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port);
            var result = client.RandomRead(Array.Empty<string>());
            Assert.False(result.IsSuccess);
            Assert.Contains("不能为空", result.Message);
        }

        [Fact]
        public void Station_Settable()
        {
            var port = new FakeSerialPort();
            var client = new YuDianClient(port) { Station = 10 };
            Assert.Equal(10, client.Station);
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
