using System;
using Nexus;
using Nexus.Mitsubishi;
using Nexus.Mitsubishi.IqR.Serial;
using Xunit;

namespace Nexus.Mitsubishi.IqR.Serial.Tests
{
    /// <summary>
    /// Phase C-4 测试 — 验证 IqRSerialClient 正确继承 MelsecA3CNetClient 的完整能力。
    /// IqRSerialClient 是品牌入口类,实际通讯由 A3CNet 实现。
    /// </summary>
    public class IqRSerialClientTests
    {
        private sealed class FakePort : ISerialPort
        {
            public string PortName { get; set; } = "COM_IQR";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.Even;
            public int ReadTimeout { get; set; } = 1000;
            public int WriteTimeout { get; set; } = 1000;
            public bool IsOpen { get; private set; }
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }
            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }
            public int Read(byte[] buffer, int offset, int count) => 0;
            public void Write(byte[] buffer, int offset, int count) { }
            public void Dispose() => Close();
        }

        [Fact]
        public void Constructor_InitializesCorrectly()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port, station: 5, timeout: 3000);
            Assert.Equal((byte)5, client.Station);
            Assert.True(client.IsConnected);
        }

        [Fact]
        public void InheritsFromMelsecA3CNetClient()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port);
            // IqRSerialClient 应可赋值给 MelsecA3CNetClient(继承关系)。
            MelsecA3CNetClient baseClient = client;
            Assert.NotNull(baseClient);
            Assert.Equal(client.Station, baseClient.Station);
        }

        [Fact]
        public void ImplementsIReadWriteDevice()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port);
            IReadWriteDevice device = client;
            Assert.True(device.IsConnected);
        }

        [Fact]
        public void ImplementsIBatchReadWrite()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port);
            IBatchReadWrite batch = client;
            Assert.NotNull(batch);
        }

        [Fact]
        public void ToString_IncludesStation()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port, station: 7);
            Assert.Contains("07", client.ToString());
            Assert.Contains("IqR", client.ToString());
        }

        [Fact]
        public void DefaultConstructor_StationZero()
        {
            var port = new FakePort();
            port.Open();
            using var client = new IqRSerialClient(port);
            Assert.Equal((byte)0, client.Station);
        }
    }
}
