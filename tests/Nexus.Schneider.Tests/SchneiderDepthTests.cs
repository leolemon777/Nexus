using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using Nexus.Schneider;

namespace Nexus.Schneider.Tests
{
    public class SchneiderIecAddressTests
    {
        [Theory]
        [InlineData("%MW100", SchneiderArea.InternalWord, 100, 0x03)]
        [InlineData("%M50", SchneiderArea.InternalBit, 50, 0x01)]
        [InlineData("%IW10", SchneiderArea.InputWord, 10, 0x04)]
        [InlineData("%QW20", SchneiderArea.OutputWord, 1556, 0x03)]
        [InlineData("%KW50", SchneiderArea.ConstantWord, 2098, 0x03)]
        [InlineData("%S0", SchneiderArea.SystemBit, 0, 0x01)]
        [InlineData("%SW100", SchneiderArea.SystemWord, 1124, 0x03)]
        public void IecAddress_FullFormat(string raw, SchneiderArea area, ushort addr, byte fc)
        {
            var parsed = SchneiderAddress.TryParse(raw);
            Assert.NotNull(parsed);
            Assert.Equal(area, parsed.Area);
            Assert.Equal(addr, parsed.AddressValue);
            Assert.Equal(fc, parsed.FunctionCode);
        }

        [Fact]
        public void IecAddress_M0Dot3_BitAddressing()
        {
            var addr = SchneiderAddress.TryParse("%M0.3");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalBit, addr.Area);
            Assert.Equal(3, addr.AddressValue); // 0 * 16 + 3
            Assert.Equal(0x01, addr.FunctionCode);
        }

        [Fact]
        public void IecAddress_M1Dot7_BitAddressing()
        {
            var addr = SchneiderAddress.TryParse("%M1.7");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalBit, addr.Area);
            Assert.Equal(23, addr.AddressValue); // 1 * 16 + 7
        }

        [Fact]
        public void IecAddress_I3Dot12_BitAddressing()
        {
            var addr = SchneiderAddress.TryParse("%I3.12");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InputBit, addr.Area);
            Assert.Equal(60, addr.AddressValue); // 3 * 16 + 12
        }

        [Fact]
        public void IecAddress_Q2Dot5_BitAddressing()
        {
            var addr = SchneiderAddress.TryParse("%Q2.5");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.OutputBit, addr.Area);
            Assert.Equal(37, addr.AddressValue); // 2 * 16 + 5
        }

        [Fact]
        public void IecAddress_WithoutPercent()
        {
            var addr = SchneiderAddress.TryParse("MW100");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalWord, addr.Area);
            Assert.Equal(100, addr.AddressValue);
        }

        [Fact]
        public void IecAddress_CaseInsensitive()
        {
            var addr = SchneiderAddress.TryParse("%mw100");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalWord, addr.Area);
            Assert.Equal(100, addr.AddressValue);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Z100")]
        [InlineData("%")]
        [InlineData("X")]
        public void IecAddress_Invalid(string? input)
        {
            Assert.Null(SchneiderAddress.TryParse(input!));
        }
    }

    public class SchneiderBatchGroupingTests
    {
        [Fact]
        public void GroupAddresses_SameArea_MergesContinuous()
        {
            var addresses = new[] { "%MW100", "%MW101", "%MW102" };
            var groups = SchneiderModiconClient.GroupAddressesForBatch(addresses);
            Assert.Single(groups);
            Assert.Equal(0x03, groups[0].Fc);
            Assert.Equal(100, groups[0].Start);
            Assert.Equal(3, groups[0].Count);
        }

        [Fact]
        public void GroupAddresses_SameArea_SplitsNonContinuous()
        {
            var addresses = new[] { "%MW100", "%MW101", "%MW200", "%MW201" };
            var groups = SchneiderModiconClient.GroupAddressesForBatch(addresses);
            Assert.Equal(2, groups.Count);
            Assert.Equal(100, groups[0].Start);
            Assert.Equal(2, groups[0].Count);
            Assert.Equal(200, groups[1].Start);
            Assert.Equal(2, groups[1].Count);
        }

        [Fact]
        public void GroupAddresses_DifferentAreas_SeparatesGroups()
        {
            var addresses = new[] { "%MW100", "%IW10", "%M50" };
            var groups = SchneiderModiconClient.GroupAddressesForBatch(addresses);
            Assert.Equal(3, groups.Count);
        }

        [Fact]
        public void GroupAddresses_Empty_ReturnsEmpty()
        {
            var groups = SchneiderModiconClient.GroupAddressesForBatch(Array.Empty<string>());
            Assert.Empty(groups);
        }

        [Fact]
        public void GroupAddresses_MixedBitsAndWords()
        {
            var addresses = new[] { "%MW100", "%MW101", "%M50", "%M51", "%IW10" };
            var groups = SchneiderModiconClient.GroupAddressesForBatch(addresses);
            // MW100-MW101 (FC03), M50-M51 (FC01), IW10 (FC04)
            Assert.Equal(3, groups.Count);
        }

        [Fact]
        public void GroupAddresses_GapOfOne_DoesNotMerge()
        {
            var addresses = new[] { "%MW100", "%MW102" };
            var groups = SchneiderModiconClient.GroupAddressesForBatch(addresses);
            // gap=2, not merged
            Assert.Equal(2, groups.Count);
            Assert.Equal(1, groups[0].Count);
            Assert.Equal(1, groups[1].Count);
        }
    }

    public class SchneiderStringTests : IDisposable
    {
        private static int _portCounter = 15300;
        private readonly int _port;
        private SchneiderVirtualServer? _server;

        public SchneiderStringTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (SchneiderVirtualServer server, SchneiderModiconClient client) StartServerAndConnect()
        {
            _server = new SchneiderVirtualServer(_port);
            _server.Start();
            var client = new SchneiderModiconClient("127.0.0.1", _port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void ReadString_ReadsAsciiFromRegisters()
        {
            var (server, client) = StartServerAndConnect();
            // "HI" = 0x48 0x49 → register 100 = 0x4849
            server.SetHoldingRegister(100, 0x4849);

            var result = client.ReadString("%MW100", 2);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal("HI", result.Content);
        }

        [Fact]
        public void WriteString_WritesAsciiToRegisters()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.WriteString("%MW200", "AB", 10);
            Assert.True(result.IsSuccess, result.Message);
        }

        [Fact]
        public void WriteString_TooLong_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();

            var result = client.WriteString("%MW200", "ABCDEFGHIJK", 5); // 11 chars > 5*2=10 bytes
            Assert.False(result.IsSuccess);
            Assert.Contains("超出", result.Message);
        }

        [Fact]
        public void WriteString_Null_ReturnsError()
        {
            var (server, client) = StartServerAndConnect();
            var result = client.WriteString("%MW200", null!, 10);
            Assert.False(result.IsSuccess);
        }
    }

    public class SchneiderDiagnosticTests : IDisposable
    {
        private static int _portCounter = 15400;
        private readonly int _port;
        private SchneiderVirtualServer? _server;

        public SchneiderDiagnosticTests()
        {
            _port = Interlocked.Increment(ref _portCounter);
        }

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        private (SchneiderVirtualServer server, SchneiderModiconClient client) StartServerAndConnect()
        {
            _server = new SchneiderVirtualServer(_port);
            _server.Start();
            var client = new SchneiderModiconClient("127.0.0.1", _port);
            client.SetPersistentConnection();
            var connect = client.Connect();
            Assert.True(connect.IsSuccess, connect.Message);
            return (_server, client);
        }

        [Fact]
        public void ReadPlcInfo_ReturnsDeviceType()
        {
            var (server, client) = StartServerAndConnect();
            // SW0 = device type, mapped to 0x0400
            server.SetHoldingRegister(0x0400, 0x0058); // M580 = 0x58

            var result = client.ReadPlcInfo();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(0x0058, result.Content.DeviceType);
        }

        [Fact]
        public void ReadDiagnostics_ReturnsCounters()
        {
            var (server, client) = StartServerAndConnect();
            // SW100 = 0x0400 + 100 = 0x0464
            server.SetHoldingRegister(0x0464, 5);  // CommErrorCount
            server.SetHoldingRegister(0x0465, 3);  // CrcErrorCount
            server.SetHoldingRegister(0x0466, 1);  // TimeoutCount
            server.SetHoldingRegister(0x0467, 2);  // ExceptionCount

            var result = client.ReadDiagnostics();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(5, result.Content.CommErrorCount);
            Assert.Equal(3, result.Content.CrcErrorCount);
            Assert.Equal(1, result.Content.TimeoutCount);
            Assert.Equal(2, result.Content.ExceptionCount);
        }

        [Fact]
        public void ReadSystemWord_ReturnsValue()
        {
            var (server, client) = StartServerAndConnect();
            server.SetHoldingRegister(0x0405, 42);

            var result = client.ReadSystemWord(5);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(42, result.Content);
        }

        [Fact]
        public void ReadSystemBit_ReturnsValue()
        {
            var (server, client) = StartServerAndConnect();
            server.SetCoil(3, true);

            var result = client.ReadSystemBit(3);
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(result.Content);
        }
    }
}
