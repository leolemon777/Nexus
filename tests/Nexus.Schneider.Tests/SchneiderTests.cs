using Xunit;
using Nexus.Schneider;

namespace Nexus.Schneider.Tests
{
    public class SchneiderAddressTests
    {
        // ═══════════════════════════════════════════
        //  内部字 (%MW)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_MW100()
        {
            var addr = SchneiderAddress.TryParse("%MW100");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalWord, addr.Area);
            Assert.Equal(100, addr.AddressValue);
            Assert.Equal(0x03, addr.FunctionCode);
        }

        [Fact]
        public void TryParse_MW0()
        {
            var addr = SchneiderAddress.TryParse("MW0");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalWord, addr.Area);
            Assert.Equal(0, addr.AddressValue);
        }

        [Fact]
        public void TryParse_MW32767()
        {
            var addr = SchneiderAddress.TryParse("%MW32767");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalWord, addr.Area);
            Assert.Equal(32767, addr.AddressValue);
        }

        // ═══════════════════════════════════════════
        //  内部位 (%M)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_M50()
        {
            var addr = SchneiderAddress.TryParse("%M50");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InternalBit, addr.Area);
            Assert.Equal(50, addr.AddressValue);
            Assert.Equal(0x01, addr.FunctionCode);
        }

        // ═══════════════════════════════════════════
        //  输入 (%I / %IW)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_I_Dot()
        {
            var addr = SchneiderAddress.TryParse("%I0.5");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InputBit, addr.Area);
            Assert.Equal(5, addr.AddressValue); // 0 * 16 + 5
            Assert.Equal(0x02, addr.FunctionCode);
        }

        [Fact]
        public void TryParse_IW10()
        {
            var addr = SchneiderAddress.TryParse("%IW10");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.InputWord, addr.Area);
            Assert.Equal(10, addr.AddressValue);
            Assert.Equal(0x04, addr.FunctionCode);
        }

        // ═══════════════════════════════════════════
        //  输出 (%Q / %QW)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Q_Dot()
        {
            var addr = SchneiderAddress.TryParse("%Q1.2");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.OutputBit, addr.Area);
            Assert.Equal(18, addr.AddressValue); // 1 * 16 + 2
            Assert.Equal(0x01, addr.FunctionCode);
        }

        [Fact]
        public void TryParse_QW20()
        {
            var addr = SchneiderAddress.TryParse("%QW20");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.OutputWord, addr.Area);
            // QW20 → 20 + 0x0600 = 1556
            Assert.Equal(1556, addr.AddressValue);
        }

        // ═══════════════════════════════════════════
        //  系统 (%S / %SW)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_S0()
        {
            var addr = SchneiderAddress.TryParse("%S0");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.SystemBit, addr.Area);
            Assert.Equal(0, addr.AddressValue);
        }

        [Fact]
        public void TryParse_SW100()
        {
            var addr = SchneiderAddress.TryParse("%SW100");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.SystemWord, addr.Area);
            // SW100 → 100 + 0x0400 = 1124
            Assert.Equal(1124, addr.AddressValue);
        }

        // ═══════════════════════════════════════════
        //  常量字 (%KW)
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_KW50()
        {
            var addr = SchneiderAddress.TryParse("%KW50");
            Assert.NotNull(addr);
            Assert.Equal(SchneiderArea.ConstantWord, addr.Area);
            // KW50 → 50 + 0x0800 = 2098
            Assert.Equal(2098, addr.AddressValue);
        }

        // ═══════════════════════════════════════════
        //  无效输入
        // ═══════════════════════════════════════════

        [Fact]
        public void TryParse_Null() => Assert.Null(SchneiderAddress.TryParse(null!));

        [Fact]
        public void TryParse_Empty() => Assert.Null(SchneiderAddress.TryParse(""));

        [Fact]
        public void TryParse_Unknown() => Assert.Null(SchneiderAddress.TryParse("Z100"));
    }

    public class SchneiderModelTests
    {
        [Fact]
        public void Constants_DefaultValues()
        {
            Assert.Equal(502, SchneiderConstants.DefaultPort);
            Assert.Equal(125, SchneiderConstants.MaxReadRegisters);
            Assert.Equal(100, SchneiderConstants.MaxWriteRegisters);
            Assert.Equal(2000, SchneiderConstants.MaxReadBits);
        }

        [Fact]
        public void Constants_FunctionCodes()
        {
            Assert.Equal(0x68, SchneiderConstants.FcReadOfs);
            Assert.Equal(0x69, SchneiderConstants.FcWriteOfs);
            Assert.Equal(0x03, SchneiderConstants.Fc03ReadHolding);
            Assert.Equal(0x01, SchneiderConstants.Fc01ReadCoil);
        }

        [Theory]
        [InlineData(0x01, "非法功能码")]
        [InlineData(0x02, "非法数据地址")]
        [InlineData(0x03, "非法数据值")]
        [InlineData(0x04, "从站设备故障")]
        [InlineData(0x06, "从站设备忙")]
        [InlineData(0x41, "Modicon 扩展错误")]
        [InlineData(0x45, "写入保护")]
        [InlineData(0xFF, "未知错误")]
        public void ErrorCodes_Description(byte code, string expected)
        {
            Assert.Contains(expected, SchneiderErrorCodes.GetDescription(code));
        }

        [Theory]
        [InlineData(SchneiderModel.M580)]
        [InlineData(SchneiderModel.M340)]
        [InlineData(SchneiderModel.M221)]
        [InlineData(SchneiderModel.M241)]
        [InlineData(SchneiderModel.Premium)]
        [InlineData(SchneiderModel.Quantum)]
        public void PlcModel_AllDefined(SchneiderModel model)
        {
            Assert.True(Enum.IsDefined(typeof(SchneiderModel), model));
        }
    }

    public class SchneiderVirtualServerTests
    {
        [Fact]
        public void VirtualServer_SetGetHoldingRegister()
        {
            using (var server = new SchneiderVirtualServer(0))
            {
                server.SetHoldingRegister(100, 12345);
                Assert.Equal(12345, server.GetHoldingRegister(100));
            }
        }

        [Fact]
        public void VirtualServer_SetGetCoil()
        {
            using (var server = new SchneiderVirtualServer(0))
            {
                server.SetCoil(50, true);
                Assert.True(server.GetCoil(50));
                Assert.False(server.GetCoil(51));
            }
        }

        [Fact]
        public void VirtualServer_StartStop()
        {
            using (var server = new SchneiderVirtualServer(0))
            {
                server.Start();
                Assert.True(true); // 没有异常即成功
                server.Stop();
            }
        }
    }

    public class SchneiderBuildCommandTests
    {
        [Fact]
        public void BuildReadPdu_FC03()
        {
            byte[] pdu = SchneiderModiconClient.BuildReadPdu(0x03, 100, 10);
            Assert.Equal(5, pdu.Length);
            Assert.Equal(0x03, pdu[0]);
            Assert.Equal(0, pdu[1]);       // addrHi
            Assert.Equal(100, pdu[2]);     // addrLo
            Assert.Equal(0, pdu[3]);       // countHi
            Assert.Equal(10, pdu[4]);      // countLo
        }

        [Fact]
        public void BuildWriteSingleRegisterPdu()
        {
            byte[] pdu = SchneiderModiconClient.BuildWriteSingleRegisterPdu(200, -1234);
            Assert.Equal(5, pdu.Length);
            Assert.Equal(0x06, pdu[0]);
            Assert.Equal(0, pdu[1]);        // addrHi
            Assert.Equal(200, pdu[2]);      // addrLo
            Assert.Equal(0xFB, pdu[3]);     // -1234 high byte
            Assert.Equal(0x2E, pdu[4]);     // -1234 low byte
        }

        [Fact]
        public void BuildWriteMultipleRegistersPdu()
        {
            byte[] data = new byte[] { 0x00, 0x64, 0x01, 0x90 }; // 100, 400
            byte[] pdu = SchneiderModiconClient.BuildWriteMultipleRegistersPdu(300, data);
            Assert.Equal(10, pdu.Length); // FC(1) + addr(2) + count(2) + byteCount(1) + data(4)
            Assert.Equal(0x10, pdu[0]);     // FC16
            Assert.Equal(1, pdu[1]);        // addrHi (300 >> 8 = 1)
            Assert.Equal(44, pdu[2]);       // addrLo (300 & 0xFF = 44)
            Assert.Equal(0, pdu[3]);        // wordCountHi
            Assert.Equal(2, pdu[4]);        // wordCountLo
            Assert.Equal(4, pdu[5]);        // byteCount
            Assert.Equal(0x00, pdu[6]);
            Assert.Equal(0x64, pdu[7]);
        }

        [Fact]
        public void BuildWriteMultipleRegistersPdu_WithEightBytes_WritesFourRegisters()
        {
            byte[] data = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88 };
            byte[] pdu = SchneiderModiconClient.BuildWriteMultipleRegistersPdu(100, data);

            Assert.Equal(14, pdu.Length);
            Assert.Equal(0x10, pdu[0]);
            Assert.Equal(0, pdu[3]);
            Assert.Equal(4, pdu[4]);
            Assert.Equal(8, pdu[5]);
            Assert.Equal(data, pdu[6..14]);
        }
    }
}
