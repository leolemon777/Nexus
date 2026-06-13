using System;
using System.Collections.Generic;
using Xunit;
using Nexus;
using Nexus.Beckhoff;

namespace Nexus.Beckhoff.Tests
{
    public class BeckhoffAdsTests
    {
        [Fact]
        public void Constructor_SetsDefaults()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.Equal("192.168.1.1", client.IpAddress);
            Assert.Equal(48898, client.Port);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Constructor_SetsNetIds()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.Equal("127.0.0.1.1.1", client.LocalNetId);
            Assert.Equal("192.168.1.1.1.1", client.TargetNetId);
        }

        [Fact]
        public void Constructor_CustomPort_SetsPort()
        {
            var client = new BeckhoffAdsClient("192.168.1.1", 851);
            Assert.Equal(851, client.Port);
        }

        [Fact]
        public void Constructor_CustomTimeout_SetsTimeout()
        {
            var client = new BeckhoffAdsClient("192.168.1.1", 48898, 10000);
            Assert.Equal(10000, client.Timeout);
        }

        [Fact]
        public void NetIds_CanBeOverridden()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.LocalNetId = "10.0.0.1.1.1";
            client.TargetNetId = "10.0.0.2.1.1";
            Assert.Equal("10.0.0.1.1.1", client.LocalNetId);
            Assert.Equal("10.0.0.2.1.1", client.TargetNetId);
        }

        [Fact]
        public void TargetPort_DefaultIs851()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.Equal((ushort)851, client.TargetPort);
        }

        [Fact]
        public void SetLogger_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.SetLogger(NullLogger.Instance);
        }

        [Fact]
        public void SetLogger_ConsoleLogger_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.SetLogger(new ConsoleLogger());
        }

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.Dispose();
        }

        [Fact]
        public void Dispose_DoubleDispose_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.Dispose();
            client.Dispose();
        }

        [Fact]
        public void ReadOperations_WhenNotConnected_ReturnError()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.False(client.ReadInt16("MyVar").IsSuccess);
            Assert.False(client.ReadFloat("MyVar").IsSuccess);
            Assert.False(client.ReadBool("MyVar").IsSuccess);
            Assert.False(client.ReadString("MyVar", 10).IsSuccess);
            Assert.False(client.ReadBytes("MyVar", 10).IsSuccess);
        }

        [Fact]
        public void WriteOperations_WhenNotConnected_ReturnError()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            Assert.False(client.Write("MyVar", (short)42).IsSuccess);
            Assert.False(client.Write("MyVar", true).IsSuccess);
            Assert.False(client.Write("MyVar", 3.14f).IsSuccess);
            Assert.False(client.Write("MyVar", "hello").IsSuccess);
        }

        [Fact]
        public void BatchOperations_EmptyInput_ReturnsError()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");

            Assert.False(client.BatchRead(new string[0]).IsSuccess);
            Assert.False(client.RandomRead(new string[0]).IsSuccess);
            Assert.False(client.BatchWrite(Array.Empty<KeyValuePair<string, object>>()).IsSuccess);
        }

        [Fact]
        public void BatchRead_NotConnected_ReturnsError()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            var result = client.BatchRead(new[] { "MyVar" });
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Subscribe_Unsubscribe_NotConnected_DoesNotThrow()
        {
            var client = new BeckhoffAdsClient("192.168.1.1");
            client.Subscribe("MyVar", 1000, "Int16");
            client.Unsubscribe("MyVar");
            client.StartSubscriptions();
            client.StopSubscriptions();
            client.Dispose();
        }
    }

    public class AdsModelTests
    {
        [Fact]
        public void AdsDeviceInfo_DefaultConstruction()
        {
            var info = new AdsDeviceInfo();
            Assert.Equal(0, info.MajorVersion);
            Assert.Equal(0, info.MinorVersion);
            Assert.Equal(0, info.VersionBuild);
            Assert.Equal(string.Empty, info.DeviceName);
        }

        [Fact]
        public void AdsDeviceInfo_PropertiesSet()
        {
            var info = new AdsDeviceInfo
            {
                MajorVersion = 3,
                MinorVersion = 1,
                VersionBuild = 4022,
                DeviceName = "TC3"
            };
            Assert.Equal(3, info.MajorVersion);
            Assert.Equal(1, info.MinorVersion);
            Assert.Equal(4022, info.VersionBuild);
            Assert.Equal("TC3", info.DeviceName);
        }

        [Fact]
        public void AdsState_DefaultConstruction()
        {
            var state = new AdsState();
            Assert.Equal(0, state.AdsStateValue);
            Assert.Equal(0, state.DeviceStateValue);
        }

        [Fact]
        public void AdsState_PropertiesSet()
        {
            var state = new AdsState
            {
                AdsStateValue = 5,   // Run
                DeviceStateValue = 0
            };
            Assert.Equal(5, state.AdsStateValue);
            Assert.Equal(0, state.DeviceStateValue);
        }

        // ── VirtualServer ────────────────────────────

        [Fact]
        public void VirtualServer_StartStop_DoesNotThrow()
        {
            using var server = new BeckhoffAdsVirtualServer(49001);
            server.Start();
            Assert.True(server.IsRunning);
            server.Stop();
            Assert.False(server.IsRunning);
        }

        [Fact]
        public void VirtualServer_SetMemory_GetMemory()
        {
            using var server = new BeckhoffAdsVirtualServer(49002);
            server.SetMemory(0xF000, 0, new byte[] { 0x01, 0x02 });
            server.RegisterSymbol("MyVar", new byte[] { 0x42 });
        }

        [Fact]
        public void VirtualServer_Dispose_CalledTwice_DoesNotThrow()
        {
            var server = new BeckhoffAdsVirtualServer(49003);
            server.Dispose();
            server.Dispose();
        }

        [Fact]
        public void VirtualServer_Integration_ConnectAndRead()
        {
            int port = 49004;
            using var server = new BeckhoffAdsVirtualServer(port);
            server.SetMemory(0xF000, 0, new byte[] { 0x12, 0x34 });
            server.Start();

            try
            {
                var client = new BeckhoffAdsClient("127.0.0.1", port);
                var conn = client.Connect();
                Assert.True(conn.IsSuccess, conn.Message);

                var readState = client.ReadState();
                Assert.True(readState.IsSuccess, readState.Message);

                client.Disconnect();
            }
            finally
            {
                server.Stop();
            }
        }
    }
}
