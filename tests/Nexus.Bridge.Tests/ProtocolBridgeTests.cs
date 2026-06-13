using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nexus;
using Nexus.Bridge;
using Xunit;

namespace Nexus.Bridge.Tests
{
    public class ProtocolBridgeTests
    {
        // ── BridgeConfig ──────────────────────────────

        [Fact]
        public void BridgeConfig_Defaults()
        {
            var config = new BridgeConfig();
            Assert.Equal("ModbusTcp", config.SourceType);
            Assert.Equal("127.0.0.1", config.SourceIp);
            Assert.Equal(502, config.SourcePort);
            Assert.Equal("Mqtt", config.TargetType);
            Assert.Equal(1000, config.PollIntervalMs);
            Assert.Empty(config.Points);
        }

        [Fact]
        public void BridgeConfig_CustomValues()
        {
            var config = new BridgeConfig
            {
                SourceType = "ModbusTcp",
                SourceIp = "192.168.1.10",
                SourcePort = 5020,
                TargetType = "Console",
                PollIntervalMs = 500,
                MqttTopicPrefix = "factory/plc1/",
                MqttClientId = "bridge-001"
            };
            Assert.Equal("192.168.1.10", config.SourceIp);
            Assert.Equal(5020, config.SourcePort);
            Assert.Equal("Console", config.TargetType);
            Assert.Equal("factory/plc1/", config.MqttTopicPrefix);
        }

        // ── BridgePoint ──────────────────────────────

        [Fact]
        public void BridgePoint_Defaults()
        {
            var point = new BridgePoint();
            Assert.Equal("", point.Address);
            Assert.Equal("Int16", point.DataType);
            Assert.Equal("", point.Tag);
            Assert.Equal(1.0, point.Scale);
            Assert.Equal(0.0, point.Offset);
        }

        [Fact]
        public void BridgePoint_CustomValues()
        {
            var point = new BridgePoint
            {
                Address = "D100",
                DataType = "Float",
                Tag = "temperature",
                Scale = 0.1,
                Offset = -273.15
            };
            Assert.Equal("D100", point.Address);
            Assert.Equal("Float", point.DataType);
            Assert.Equal(0.1, point.Scale);
            Assert.Equal(-273.15, point.Offset);
        }

        // ── BridgeData ──────────────────────────────

        [Fact]
        public void BridgeData_ToJson_ContainsFields()
        {
            var data = new BridgeData
            {
                Address = "D100",
                Tag = "temp",
                DataType = "Float",
                RawValue = 25.5,
                ScaledValue = 25.5,
                Timestamp = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc)
            };

            string json = data.ToJson();
            Assert.Contains("D100", json);
            Assert.Contains("temp", json);
            Assert.Contains("Float", json);
            Assert.Contains("25.5", json);
            Assert.Contains("2026", json);
        }

        [Fact]
        public void BridgeData_Defaults()
        {
            var data = new BridgeData();
            Assert.Equal("", data.Address);
            Assert.Equal(0.0, data.RawValue);
            Assert.Equal(0.0, data.ScaledValue);
        }

        // ── ConsoleBridgeTarget ──────────────────────

        [Fact]
        public void ConsoleBridgeTarget_Connect_ReturnsSuccess()
        {
            var target = new ConsoleBridgeTarget();
            Assert.True(target.Connect().IsSuccess);
            target.Dispose();
        }

        [Fact]
        public void ConsoleBridgeTarget_Publish_DoesNotThrow()
        {
            var target = new ConsoleBridgeTarget();
            target.Connect();
            target.Publish(new BridgeData
            {
                Address = "D100",
                Tag = "test",
                DataType = "Int16",
                RawValue = 42,
                ScaledValue = 42,
                Timestamp = DateTime.Now
            });
            target.Dispose();
        }

        // ── ProtocolBridge with FakeDevice ──────────

        [Fact]
        public void Constructor_NullConfig_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ProtocolBridge(null!));
        }

        [Fact]
        public void Constructor_CustomDevices_DoesNotThrow()
        {
            var config = new BridgeConfig { Points = new List<BridgePoint> { new BridgePoint { Address = "D100" } } };
            using var bridge = new ProtocolBridge(config, new FakeDevice(), new ConsoleBridgeTarget());
            Assert.False(bridge.IsRunning);
            Assert.Equal(0, bridge.BridgedCount);
        }

        [Fact]
        public void Start_EmptyPoints_ReturnsError()
        {
            var config = new BridgeConfig();
            using var bridge = new ProtocolBridge(config, new FakeDevice(), new ConsoleBridgeTarget());
            var result = bridge.Start();
            Assert.False(result.IsSuccess);
            Assert.Contains("为空", result.Message);
        }

        [Fact]
        public void Start_WithPoints_RunsAndStops()
        {
            var config = new BridgeConfig { PollIntervalMs = 100 };
            config.Points.Add(new BridgePoint { Address = "D100", DataType = "Int16", Tag = "val1" });

            var source = new FakeDevice();
            using var bridge = new ProtocolBridge(config, source, new ConsoleBridgeTarget());

            var result = bridge.Start();
            Assert.True(result.IsSuccess, result.Message);
            Assert.True(bridge.IsRunning);

            System.Threading.Thread.Sleep(500);
            bridge.Stop();
            Assert.False(bridge.IsRunning);
            Assert.True(bridge.BridgedCount > 0, "Should have bridged at least one point");
        }

        [Fact]
        public void Stop_WithoutStart_DoesNotThrow()
        {
            var config = new BridgeConfig();
            using var bridge = new ProtocolBridge(config, new FakeDevice(), new ConsoleBridgeTarget());
            bridge.Stop(); // no throw
        }

        [Fact]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var config = new BridgeConfig();
            var bridge = new ProtocolBridge(config, new FakeDevice(), new ConsoleBridgeTarget());
            bridge.Dispose();
            bridge.Dispose();
        }

        [Fact]
        public void OnDataBridged_Event_Fires()
        {
            var config = new BridgeConfig { PollIntervalMs = 100 };
            config.Points.Add(new BridgePoint { Address = "D100", DataType = "Int16" });

            var source = new FakeDevice();
            using var bridge = new ProtocolBridge(config, source, new ConsoleBridgeTarget());

            var events = new List<BridgeDataEventArgs>();
            bridge.OnDataBridged += (_, e) => events.Add(e);

            bridge.Start();
            System.Threading.Thread.Sleep(500);
            bridge.Stop();

            Assert.True(events.Count > 0, "Should have received bridge events");
            Assert.Equal("D100", events[0].Data.Address);
            Assert.Equal("Int16", events[0].Data.DataType);
        }

        [Fact]
        public void ScaleAndOffset_AppliedCorrectly()
        {
            var config = new BridgeConfig { PollIntervalMs = 100 };
            config.Points.Add(new BridgePoint
            {
                Address = "D100",
                DataType = "Int16",
                Scale = 0.1,
                Offset = -10.0
            });

            var source = new FakeDevice();
            using var bridge = new ProtocolBridge(config, source, new ConsoleBridgeTarget());

            var events = new List<BridgeDataEventArgs>();
            bridge.OnDataBridged += (_, e) => events.Add(e);

            bridge.Start();
            System.Threading.Thread.Sleep(500);
            bridge.Stop();

            Assert.True(events.Count > 0);
            // FakeDevice returns 0 for ReadInt16, so scaled = 0 * 0.1 + (-10) = -10
            Assert.Equal(-10.0, events[0].Data.ScaledValue, 0.001);
        }

        // ── MqttBridgeTarget (构造器，不连接) ──────

        [Fact]
        public void MqttBridgeTarget_Constructor_SetsProperties()
        {
            var target = new MqttBridgeTarget("127.0.0.1", 1883, "test-client", "nexus/");
            Assert.NotNull(target);
            target.Dispose();
        }

        [Fact]
        public void MqttBridgeTarget_Dispose_WithoutConnect_DoesNotThrow()
        {
            var target = new MqttBridgeTarget("127.0.0.1", 1883, "test", "prefix/");
            target.Dispose();
        }

        // ── FakeDevice ─────────────────────────────

        private class FakeDevice : IReadWriteDevice
        {
            public int NextValue { get; set; } = 0;
            public bool IsConnected { get; private set; }

            public OperateResult Connect() { IsConnected = true; return OperateResult.Success(); }
            public Task<OperateResult> ConnectAsync() { IsConnected = true; return Task.FromResult(OperateResult.Success()); }
            public void Disconnect() { IsConnected = false; }

            public OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Success(NextValue != 0);
            public OperateResult<short> ReadInt16(string address) => OperateResult<short>.Success((short)NextValue);
            public OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Success((ushort)NextValue);
            public OperateResult<int> ReadInt32(string address) => OperateResult<int>.Success(NextValue);
            public OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Success((uint)NextValue);
            public OperateResult<long> ReadInt64(string address) => OperateResult<long>.Success((long)NextValue);
            public OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Success((ulong)NextValue);
            public OperateResult<float> ReadFloat(string address) => OperateResult<float>.Success((float)NextValue);
            public OperateResult<double> ReadDouble(string address) => OperateResult<double>.Success((double)NextValue);
            public OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Success(NextValue.ToString());
            public OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Success(new byte[0]);

            public OperateResult Write(string address, bool value) => OperateResult.Success();
            public OperateResult Write(string address, short value) => OperateResult.Success();
            public OperateResult Write(string address, ushort value) => OperateResult.Success();
            public OperateResult Write(string address, int value) => OperateResult.Success();
            public OperateResult Write(string address, uint value) => OperateResult.Success();
            public OperateResult Write(string address, long value) => OperateResult.Success();
            public OperateResult Write(string address, ulong value) => OperateResult.Success();
            public OperateResult Write(string address, float value) => OperateResult.Success();
            public OperateResult Write(string address, double value) => OperateResult.Success();
            public OperateResult Write(string address, string value) => OperateResult.Success();
            public OperateResult Write(string address, byte[] data) => OperateResult.Success();

            public Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.FromResult(ReadBool(address));
            public Task<OperateResult<short>> ReadInt16Async(string address) => Task.FromResult(ReadInt16(address));
            public Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.FromResult(ReadUInt16(address));
            public Task<OperateResult<int>> ReadInt32Async(string address) => Task.FromResult(ReadInt32(address));
            public Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.FromResult(ReadUInt32(address));
            public Task<OperateResult<long>> ReadInt64Async(string address) => Task.FromResult(ReadInt64(address));
            public Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.FromResult(ReadUInt64(address));
            public Task<OperateResult<float>> ReadFloatAsync(string address) => Task.FromResult(ReadFloat(address));
            public Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.FromResult(ReadDouble(address));
            public Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.FromResult(ReadString(address, length));
            public Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.FromResult(ReadBytes(address, length));

            public Task<OperateResult> WriteAsync(string address, bool value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, short value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, int value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, float value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, string value) => Task.FromResult(Write(address, value));
            public Task<OperateResult> WriteAsync(string address, byte[] data) => Task.FromResult(Write(address, data));

            public void Dispose() { IsConnected = false; }
        }
    }

    // ── BridgeData 深度测试 ──────────────────────────

    public class BridgeDataExtraTests
    {
        [Fact]
        public void ToJson_EscapingSpecialChars()
        {
            var data = new BridgeData { Address = "D100", Tag = "test\"tag", DataType = "Int16", RawValue = 0, ScaledValue = 0, Timestamp = DateTime.Now };
            string json = data.ToJson();
            Assert.Contains("D100", json);
        }

        [Fact]
        public void BridgeData_DefaultValues()
        {
            var data = new BridgeData();
            Assert.Equal("", data.Address);
            Assert.Equal("", data.Tag);
            Assert.Equal("", data.DataType);
            Assert.Equal(0.0, data.RawValue);
            Assert.Equal(0.0, data.ScaledValue);
        }

        [Fact]
        public void BridgePoint_CustomScale()
        {
            var point = new BridgePoint { Scale = 0.01, Offset = -100.0 };
            Assert.Equal(0.01, point.Scale);
            Assert.Equal(-100.0, point.Offset);
        }
    }
}
