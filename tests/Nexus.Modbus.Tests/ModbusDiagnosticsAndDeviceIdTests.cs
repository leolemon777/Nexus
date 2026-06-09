using System;
using System.Net;
using System.Net.Sockets;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Modbus.Tests
{
    /// <summary>
    /// FC08 Diagnostics 和 FC43 Read Device ID 测试。
    /// 包含枚举验证、PDU 构建验证、以及通过虚拟服务器的 round-trip 集成测试。
    /// </summary>
    public class ModbusAdvancedFunctionTests
    {
        private static int GetFreeTcpPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        // ═══════════════════════════════════════════
        //  FC08 Diagnostics — 枚举值验证
        // ═══════════════════════════════════════════

        [Fact]
        public void DiagnosticsSubFunction_Values()
        {
            Assert.Equal(0x0000, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnQueryData);
            Assert.Equal(0x0001, (ushort)ModbusTcpClient.DiagnosticsSubFunction.RestartCommunications);
            Assert.Equal(0x0002, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnDiagnosticRegister);
            Assert.Equal(0x0003, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ChangeAsciiInputDelimiter);
            Assert.Equal(0x0004, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ForceListenOnlyMode);
            Assert.Equal(0x000A, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ClearCounters);
            Assert.Equal(0x000B, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnBusMessageCount);
            Assert.Equal(0x000C, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnBusCommErrorCount);
            Assert.Equal(0x000D, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnBusExceptionErrorCount);
            Assert.Equal(0x000E, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnSlaveMessageCount);
            Assert.Equal(0x000F, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnSlaveNoResponseCount);
            Assert.Equal(0x0010, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnSlaveNAKCount);
            Assert.Equal(0x0011, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnSlaveBusyCount);
            Assert.Equal(0x0012, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnBusCharOverrunCount);
            Assert.Equal(0x0014, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ClearOverrunCounters);
            Assert.Equal(0x0015, (ushort)ModbusTcpClient.DiagnosticsSubFunction.ReturnIopOverrunCount);
        }

        // ═══════════════════════════════════════════
        //  FC43 Read Device ID — 枚举和模型
        // ═══════════════════════════════════════════

        [Fact]
        public void DeviceIdReadLevel_Values()
        {
            Assert.Equal(0x01, (byte)ModbusTcpClient.DeviceIdReadLevel.Basic);
            Assert.Equal(0x02, (byte)ModbusTcpClient.DeviceIdReadLevel.Regular);
            Assert.Equal(0x03, (byte)ModbusTcpClient.DeviceIdReadLevel.Extended);
        }

        [Fact]
        public void DeviceIdentification_Defaults()
        {
            var info = new ModbusTcpClient.DeviceIdentification();
            Assert.Equal(string.Empty, info.VendorName);
            Assert.Equal(string.Empty, info.ProductCode);
            Assert.Equal(string.Empty, info.MajorMinorRevision);
            Assert.Equal(string.Empty, info.DeviceUrl);
            Assert.Equal(string.Empty, info.ProductName);
            Assert.Equal(string.Empty, info.ModelName);
            Assert.Equal(string.Empty, info.UserApplicationName);
            Assert.Equal(0, info.ObjectCount);
            Assert.False(info.MoreFollows);
        }

        [Fact]
        public void DeviceIdentification_PropertiesSettable()
        {
            var info = new ModbusTcpClient.DeviceIdentification
            {
                ReadLevel = 0x01,
                ConformityLevel = 0x02,
                MoreFollows = true,
                NextObjectId = 0x03,
                ObjectCount = 3,
                VendorName = "Nexus",
                ProductCode = "NX-100",
                MajorMinorRevision = "1.0.0"
            };

            Assert.Equal(0x01, info.ReadLevel);
            Assert.Equal(0x02, info.ConformityLevel);
            Assert.True(info.MoreFollows);
            Assert.Equal(0x03, info.NextObjectId);
            Assert.Equal(3, info.ObjectCount);
            Assert.Equal("Nexus", info.VendorName);
            Assert.Equal("NX-100", info.ProductCode);
            Assert.Equal("1.0.0", info.MajorMinorRevision);
        }

        // ═══════════════════════════════════════════
        //  FC08/FC43 枚举完整性
        // ═══════════════════════════════════════════

        [Fact]
        public void DiagnosticsSubFunction_AllDefined()
        {
            var values = Enum.GetValues(typeof(ModbusTcpClient.DiagnosticsSubFunction));
            Assert.True(values.Length >= 16, $"Expected >= 16 sub-function codes, got {values.Length}");
        }

        [Fact]
        public void DeviceIdReadLevel_AllDefined()
        {
            var values = Enum.GetValues(typeof(ModbusTcpClient.DeviceIdReadLevel));
            Assert.Equal(3, values.Length);
        }

        // ═══════════════════════════════════════════
        //  FC08 Loopback — 集成测试 (Round-Trip)
        // ═══════════════════════════════════════════

        [Fact]
        public void LoopbackTest_ReturnsSameData()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            // 默认测试值 0xA5A5
            var result = client.LoopbackTest();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0xA5A5, result.Content);
        }

        [Fact]
        public void LoopbackTest_CustomData_ReturnsSameData()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.LoopbackTest(0x1234);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x1234, result.Content);
        }

        [Fact]
        public void LoopbackTest_Zero_ReturnsZero()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.LoopbackTest(0x0000);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x0000, result.Content);
        }

        [Fact]
        public void Diagnostics_ReturnQueryData_EchoesData()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.Diagnostics(
                ModbusTcpClient.DiagnosticsSubFunction.ReturnQueryData, 0xDEAD);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0xDEAD, result.Content);
        }

        [Fact]
        public void Diagnostics_ClearCounters_ReturnsZero()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.ClearAllCounters();
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal((ushort)0x0000, result.Content);
        }

        [Fact]
        public void Diagnostics_BusMessageCount_ReturnsValue()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            // 先做一次 loopback 让计数器增加
            client.LoopbackTest();

            var result = client.ReadBusMessageCount();
            Assert.True(result.IsSuccess, result.Message);
            // 计数器 >= 2 (loopback + bus message count)
            Assert.True(result.Content >= 2, $"Expected BusMessageCount >= 2, got {result.Content}");
        }

        // ═══════════════════════════════════════════
        //  FC43/14 Read Device ID — 集成测试 (Round-Trip)
        // ═══════════════════════════════════════════

        [Fact]
        public void ReadDeviceId_Basic_ReturnsThreeObjects()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.VendorName = "TestVendor";
            server.ProductCode = "TC-200";
            server.MajorMinorRevision = "2.5.1";
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.ReadDeviceId(ModbusTcpClient.DeviceIdReadLevel.Basic);
            Assert.True(result.IsSuccess, result.Message);

            var info = result.Content;
            Assert.Equal("TestVendor", info.VendorName);
            Assert.Equal("TC-200", info.ProductCode);
            Assert.Equal("2.5.1", info.MajorMinorRevision);
            Assert.Equal(3, info.ObjectCount);
            Assert.False(info.MoreFollows);
        }

        [Fact]
        public void ReadDeviceId_Regular_ReturnsSevenObjects()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.VendorName = "Nexus";
            server.ProductCode = "NX-100";
            server.MajorMinorRevision = "1.0";
            server.ProductName = "TestPLC";
            server.ModelName = "NX-PLC-01";
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.ReadDeviceId(ModbusTcpClient.DeviceIdReadLevel.Regular);
            Assert.True(result.IsSuccess, result.Message);

            var info = result.Content;
            Assert.Equal("Nexus", info.VendorName);
            Assert.Equal("NX-100", info.ProductCode);
            Assert.Equal("1.0", info.MajorMinorRevision);
            Assert.Equal("TestPLC", info.ProductName);
            Assert.Equal("NX-PLC-01", info.ModelName);
            Assert.Equal(7, info.ObjectCount);
        }

        [Fact]
        public void ReadDeviceId_DefaultServer_ReturnsDefaultValues()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.ReadDeviceId();
            Assert.True(result.IsSuccess, result.Message);

            var info = result.Content;
            Assert.Equal("Nexus Virtual", info.VendorName);
            Assert.Equal("NX-SIM", info.ProductCode);
            Assert.Equal("1.0.0", info.MajorMinorRevision);
            Assert.Equal((byte)0x01, info.ReadLevel);
            Assert.True(info.ConformityLevel >= 0x01);
        }

        [Fact]
        public void ReadDeviceId_Extended_ReturnsAllObjects()
        {
            int port = GetFreeTcpPort();
            using var server = new ModbusTcpServer(port);
            server.VendorName = "V";
            server.ProductCode = "P";
            server.MajorMinorRevision = "3.0";
            server.DeviceUrl = "http://test";
            server.ProductName = "PN";
            server.ModelName = "MN";
            server.UserApplicationName = "UA";
            server.ConformityLevel = 0x03;
            server.Start();

            using var client = new ModbusTcpClient("127.0.0.1", port, station: 1);

            var result = client.ReadDeviceId(ModbusTcpClient.DeviceIdReadLevel.Extended);
            Assert.True(result.IsSuccess, result.Message);

            var info = result.Content;
            Assert.Equal("V", info.VendorName);
            Assert.Equal("P", info.ProductCode);
            Assert.Equal("3.0", info.MajorMinorRevision);
            Assert.Equal("http://test", info.DeviceUrl);
            Assert.Equal("PN", info.ProductName);
            Assert.Equal("MN", info.ModelName);
            Assert.Equal("UA", info.UserApplicationName);
            Assert.Equal(7, info.ObjectCount);
            Assert.Equal((byte)0x03, info.ConformityLevel);
        }

        // ═══════════════════════════════════════════
        //  FC08/FC43 Packet Parser 验证
        // ═══════════════════════════════════════════

        [Fact]
        public void PacketParser_FC08_LoopbackRequest()
        {
            // FC08 ReturnQueryData request: FC=0x08, SubFunc=0x0000, Data=0xA5A5
            byte[] pdu = { 0x08, 0x00, 0x00, 0xA5, 0xA5 };
            // Wrap in MBAP header (7 bytes) + UnitId
            byte[] frame = new byte[7 + pdu.Length];
            // TransactionId=0x0001, ProtocolId=0x0000, Length=6, UnitId=1
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = 0x00; frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Request);
            Assert.True(info.IsValid, info.Error ?? "");
            Assert.Equal((byte)0x08, info.FunctionCode);
            Assert.Equal((byte)0x08, info.BaseFunctionCode);
            Assert.Equal((ushort)0x0000, info.SubFunction);
            Assert.Equal(2, info.Data.Length);
            Assert.Equal(0xA5, info.Data[0]);
            Assert.Equal(0xA5, info.Data[1]);
        }

        [Fact]
        public void PacketParser_FC08_LoopbackResponse()
        {
            // FC08 ReturnQueryData response (echo)
            byte[] pdu = { 0x08, 0x00, 0x00, 0xA5, 0xA5 };
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = 0x00; frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Response);
            Assert.True(info.IsValid, info.Error ?? "");
            Assert.Equal((ushort)0x0000, info.SubFunction);
            Assert.Equal(2, info.Data.Length);
        }

        [Fact]
        public void PacketParser_FC43_ReadDeviceIdRequest()
        {
            // FC43/14 request: FC=0x2B, MEI=0x0E, ReadLevel=0x01, ObjectId=0x00
            byte[] pdu = { 0x2B, 0x0E, 0x01, 0x00 };
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = 0x00; frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Request);
            Assert.True(info.IsValid, info.Error ?? "");
            Assert.Equal((byte?)0x2B, info.FunctionCode);
            Assert.Equal((byte?)0x0E, info.MeiType);
            Assert.Equal((byte?)0x01, info.ReadDeviceIdLevel);
        }

        [Fact]
        public void PacketParser_FC43_ReadDeviceIdResponse()
        {
            // FC43/14 response: FC=0x2B, MEI=0x0E, ReadLevel=0x01, Conformity=0x02,
            //   MoreFollows=0, NextObjId=0, ObjCount=3, then 3 objects:
            //   Obj0: Id=0x00, Len=5, "TestV"
            //   Obj1: Id=0x01, Len=2, "PC"
            //   Obj2: Id=0x02, Len=3, "1.0"
            byte[] obj0 = { 0x00, 0x05, (byte)'T', (byte)'e', (byte)'s', (byte)'t', (byte)'V' };
            byte[] obj1 = { 0x01, 0x02, (byte)'P', (byte)'C' };
            byte[] obj2 = { 0x02, 0x03, (byte)'1', (byte)'.', (byte)'0' };

            byte[] pdu = new byte[7 + obj0.Length + obj1.Length + obj2.Length];
            pdu[0] = 0x2B; pdu[1] = 0x0E;
            pdu[2] = 0x01; // ReadLevel
            pdu[3] = 0x02; // ConformityLevel
            pdu[4] = 0x00; // MoreFollows
            pdu[5] = 0x00; // NextObjectId
            pdu[6] = 0x03; // ObjectCount

            int off = 7;
            Buffer.BlockCopy(obj0, 0, pdu, off, obj0.Length); off += obj0.Length;
            Buffer.BlockCopy(obj1, 0, pdu, off, obj1.Length); off += obj1.Length;
            Buffer.BlockCopy(obj2, 0, pdu, off, obj2.Length);

            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame, ModbusPacketDirection.Response);
            Assert.True(info.IsValid, info.Error ?? "");
            Assert.Equal((byte?)0x2B, info.FunctionCode);
            Assert.Equal((byte?)0x0E, info.MeiType);
            Assert.Equal((byte?)0x01, info.ReadDeviceIdLevel);
            Assert.Equal((byte?)0x02, info.ConformityLevel);
            Assert.False(info.MoreFollows);
            Assert.Equal((byte?)0x00, info.NextObjectId);
            Assert.Equal((byte?)0x03, info.ObjectCount);
        }

        [Fact]
        public void PacketParser_FC43_DirectionInference_Request()
        {
            // 4-byte PDU → request
            byte[] pdu = { 0x2B, 0x0E, 0x01, 0x00 };
            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = 0x00; frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame); // direction unknown → infer
            Assert.True(info.IsValid);
            Assert.Equal(ModbusPacketDirection.Request, info.Direction);
        }

        [Fact]
        public void PacketParser_FC43_DirectionInference_Response()
        {
            // 10-byte PDU → response
            byte[] pdu = new byte[10];
            pdu[0] = 0x2B; pdu[1] = 0x0E;
            pdu[2] = 0x01; pdu[3] = 0x02;
            pdu[4] = 0x00; pdu[5] = 0x00;
            pdu[6] = 0x01; // 1 object
            pdu[7] = 0x00; // ObjectId
            pdu[8] = 0x01; // Length
            pdu[9] = (byte)'X';

            byte[] frame = new byte[7 + pdu.Length];
            frame[0] = 0x00; frame[1] = 0x01;
            frame[2] = 0x00; frame[3] = 0x00;
            frame[4] = (byte)((pdu.Length + 1) >> 8); frame[5] = (byte)(pdu.Length + 1);
            frame[6] = 0x01;
            Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

            var info = ModbusPacketParser.ParseTcp(frame); // direction unknown → infer
            Assert.True(info.IsValid);
            Assert.Equal(ModbusPacketDirection.Response, info.Direction);
        }
    }
}
