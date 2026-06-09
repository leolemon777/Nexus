using Xunit;
using Nexus.Omron;

namespace Nexus.Omron.Tests
{
    public class FinsSerialClientTests
    {
        // ═══════════════════════════════════════════
        //  BuildReadCommand — 报文构建
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildReadCommand_DM100()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] cmd = client.BuildReadCommand(FinsMemoryArea.DM, 100, 0, 10);
                // 应包含 12 字节帧头 + 6 字节数据
                Assert.Equal(18, cmd.Length);
                // 最后 6 字节: area + addressHi + addressLo + bitOff + countHi + countLo
                Assert.Equal((byte)FinsMemoryArea.DM, cmd[12]);
                Assert.Equal(0, cmd[13]);   // addrHi
                Assert.Equal(100, cmd[14]); // addrLo
                Assert.Equal(0, cmd[15]);   // bitOffset
                Assert.Equal(0, cmd[16]);   // countHi
                Assert.Equal(10, cmd[17]);  // countLo
            }
        }

        [Fact]
        public void BuildReadCommand_CIO50_Bit3()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] cmd = client.BuildReadCommand(FinsMemoryArea.CIO, 50, 3, 1);
                Assert.Equal(18, cmd.Length);
                Assert.Equal((byte)FinsMemoryArea.CIO, cmd[12]);
                Assert.Equal(3, cmd[15]); // bitOffset
            }
        }

        [Fact]
        public void BuildWriteCommand_DM200()
        {
            using (var fake = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(fake);
                byte[] data = new byte[] { 0x00, 0x64 }; // 100
                byte[] cmd = client.BuildWriteCommand(FinsMemoryArea.DM, 200, 0, data);
                // 12 字节帧头 + 6 字节地址 + 2 字节数据
                Assert.Equal(20, cmd.Length);
                Assert.Equal((byte)FinsMemoryArea.DM, cmd[12]);
                Assert.Equal(200, cmd[14]); // addrLo
                Assert.Equal(0, cmd[16]);   // wordCount Hi
                Assert.Equal(1, cmd[17]);   // wordCount Lo (2 bytes = 1 word)
            }
        }

        [Fact]
        public void FinsSerialClient_IsConnected()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream);
                Assert.True(client.IsConnected);
            }
        }

        [Fact]
        public void FinsSerialClient_DefaultProperties()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream, destNode: 5);
                Assert.Equal(5, client.DestNode);
                Assert.Equal(0, client.DestNetwork);
                Assert.Equal(0, client.DestUnit);
            }
        }

        [Fact]
        public void FinsSerialClient_ConnectReturnsSuccess()
        {
            using (var stream = new System.IO.MemoryStream())
            {
                var client = new FinsSerialClient(stream);
                var result = client.Connect();
                Assert.True(result.IsSuccess);
            }
        }

        [Fact]
        public void FinsSerialClient_DisposeDoesNotThrow()
        {
            var stream = new System.IO.MemoryStream();
            var client = new FinsSerialClient(stream);
            client.Dispose();
        }
    }

    public class OmronModelTests
    {
        [Fact]
        public void OmronModel_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CJ2M));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CP1H));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NJ501));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NX1P2));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.NX102));
            Assert.True(Enum.IsDefined(typeof(OmronModel), OmronModel.CS1G));
        }

        [Fact]
        public void FinsConstants_DefaultValues()
        {
            Assert.Equal(9600, FinsConstants.DefaultTcpPort);
            Assert.Equal(9600, FinsConstants.DefaultUdpPort);
            Assert.Equal(10, FinsConstants.FinsHeaderLength);
            Assert.Equal(500, FinsConstants.MaxReadWords);
            Assert.Equal(500, FinsConstants.MaxWriteWords);
        }

        [Fact]
        public void FinsMemoryArea_Values()
        {
            Assert.Equal(0xB0, (byte)FinsMemoryArea.CIO);
            Assert.Equal(0xB1, (byte)FinsMemoryArea.WR);
            Assert.Equal(0xB2, (byte)FinsMemoryArea.HR);
            Assert.Equal(0xB3, (byte)FinsMemoryArea.AR);
            Assert.Equal(0x82, (byte)FinsMemoryArea.DM);
            Assert.Equal(0x98, (byte)FinsMemoryArea.EM);
            Assert.Equal(0x91, (byte)FinsMemoryArea.TimerPV);
            Assert.Equal(0xA1, (byte)FinsMemoryArea.CounterPV);
        }

        [Fact]
        public void FinsDiscoveredDevice_Defaults()
        {
            var device = new FinsDiscoveredDevice();
            Assert.Equal(string.Empty, device.ControllerModel);
            Assert.Equal(0, device.NetworkAddress);
            Assert.Equal(0, device.NodeNumber);
            Assert.Equal(0, device.UnitNumber);
        }
    }
}
