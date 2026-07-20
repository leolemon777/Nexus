using System;
using Nexus.Geniitek;
using Xunit;

namespace Nexus.Geniitek.Tests
{
    public class VibrationSensorClientTests
    {
        [Fact]
        public void ParsePeekValue_ValidData_ReturnsCorrectValues()
        {
            // 构造 48 字节 peek value 数据(小端序)。
            byte[] data = new byte[48];
            // AcceleratedSpeedX = 1.0f at offset 0
            Buffer.BlockCopy(BitConverter.GetBytes(1.0f), 0, data, 0, 4);
            // SpeedX = 2.5f at offset 12
            Buffer.BlockCopy(BitConverter.GetBytes(2.5f), 0, data, 12, 4);
            // Temperature = 25.5f at offset 36
            Buffer.BlockCopy(BitConverter.GetBytes(25.5f), 0, data, 36, 4);
            // Voltage = 3.7f at offset 40
            Buffer.BlockCopy(BitConverter.GetBytes(3.7f), 0, data, 40, 4);

            var v = VibrationSensorClient.ParsePeekValue(data);
            Assert.Equal(1.0f, v.AcceleratedSpeedX);
            Assert.Equal(2.5f, v.SpeedX);
            Assert.Equal(25.5f, v.Temperature);
            Assert.Equal(3.7f, v.Voltage);
        }

        [Fact]
        public void ParsePeekValue_NullData_ReturnsDefault()
        {
            var v = VibrationSensorClient.ParsePeekValue(null!);
            Assert.Equal(0f, v.AcceleratedSpeedX);
            Assert.Equal(0f, v.Temperature);
        }

        [Fact]
        public void ParsePeekValue_ShortData_ReturnsDefault()
        {
            var v = VibrationSensorClient.ParsePeekValue(new byte[10]);
            Assert.Equal(0f, v.AcceleratedSpeedX);
        }

        [Fact]
        public void PeekValue_ToString_ContainsKeyInfo()
        {
            var v = new VibrationSensorPeekValue { Temperature = 30.5f, Voltage = 3.6f };
            string s = v.ToString();
            Assert.Contains("30.5", s);
            Assert.Contains("3.6", s);
        }

        [Fact]
        public void Constructor_Defaults()
        {
            var c = new VibrationSensorClient();
            Assert.Equal(10000, c.ConnectTimeout);
            Assert.Equal((ushort)1, c.Address);
            Assert.False(c.IsConnected);
        }

        [Fact]
        public void Connect_UnreachableServer_ReturnsFailed()
        {
            var c = new VibrationSensorClient { ConnectTimeout = 500 };
            var r = c.Connect("127.0.0.1", 1);
            Assert.False(r.IsSuccess);
        }

        [Fact]
        public void ToString_ContainsAddress()
        {
            var c = new VibrationSensorClient { Address = 42 };
            Assert.Contains("42", c.ToString());
        }
    }
}
