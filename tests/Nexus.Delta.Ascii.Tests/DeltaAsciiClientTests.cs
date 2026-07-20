using System;
using System.Text;
using Nexus;
using Nexus.Modbus;
using Nexus.Delta.Ascii;
using Xunit;

namespace Nexus.Delta.Ascii.Tests
{
    /// <summary>
    /// Phase C-2 集成测试 — 验证 DeltaAsciiClient 真实读写通过完整 Modbus ASCII 链路。
    /// </summary>
    public class DeltaAsciiClientTests
    {
        // ── 地址映射(纯函数)──────────────────

        [Theory]
        [InlineData("X0", "10001")]       // 8 进制 X0 = 0 → "10001"
        [InlineData("X7", "10008")]
        [InlineData("X10", "10009")]      // 8 进制 10 = 8
        [InlineData("X17", "10016")]      // 8 进制 17 = 15
        public void MapInputX_OctalToModbus(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapInputX(addr));

        [Theory]
        [InlineData("Y0", "00001")]
        [InlineData("Y7", "00008")]
        [InlineData("Y10", "00009")]
        public void MapOutputY_OctalToModbus(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapOutputY(addr));

        [Theory]
        [InlineData("M0", "02049")]       // 0x0800 + 0 + 1 = 2049
        [InlineData("M999", "03048")]     // 0x0800 + 999 + 1 = 30848? 不对,让我重算。
                                           // 0x0800 = 2048, + 999 = 3047, + 1 = 3048 → "03048"
        public void MapAuxM_DecimalToModbus(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapAuxM(addr));

        [Theory]
        [InlineData("S0", "010241")]      // 0x2800 + 0 + 1 = 10241, "0" + "10241" = "010241"
        [InlineData("S255", "010496")]    // 0x2800 + 255 + 1 = 10496
        public void MapStepS_DecimalToModbus(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapStepS(addr));

        [Theory]
        [InlineData("D0", "40001")]
        [InlineData("D100", "40101")]
        [InlineData("D9999", "410000")]
        public void MapDataD_DecimalToModbus(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapDataD(addr));

        [Theory]
        [InlineData("T0", "41537")]       // 0x0600 = 1536, +0+1 = 1537
        [InlineData("T255", "41792")]
        public void MapTimerCurrentValueT(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapTimerCurrentValueT(addr));

        [Theory]
        [InlineData("C0", "43585")]       // 0x0E00 = 3584, +0+1 = 3585
        [InlineData("C255", "43840")]
        public void MapCounterCurrentValueC(string addr, string expected)
            => Assert.Equal(expected, DeltaAsciiClient.MapCounterCurrentValueC(addr));

        [Fact]
        public void MapAddress_InvalidOctal_Throws()
        {
            Assert.Throws<FormatException>(() => DeltaAsciiClient.MapInputX("X8"));  // 8 进制无 8
            Assert.Throws<FormatException>(() => DeltaAsciiClient.MapInputX("X9"));
        }

        [Fact]
        public void MapAddress_InvalidPrefix_Throws()
        {
            Assert.Throws<FormatException>(() => DeltaAsciiClient.MapInputX("Y0"));  // Y 用错前缀
            Assert.Throws<FormatException>(() => DeltaAsciiClient.MapDataD("X0"));
        }

        // ── 真实链路读取(通过 fake ASCII 串口)────────

        private sealed class AsciiFakePort : ISerialPort
        {
            private byte[] _readBuffer = Array.Empty<byte>();
            private int _readPosition;
            private byte[]? _writtenData;

            public string PortName { get; set; } = "COM_DELTA";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.None;
            public int ReadTimeout { get; set; } = 1000;
            public int WriteTimeout { get; set; } = 1000;
            public bool IsOpen { get; private set; }
            public bool DtrEnable { get; set; }
            public bool RtsEnable { get; set; }
            public byte[]? LastWrittenData => _writtenData;

            /// <summary>Setup ASCII 响应:把 responseData(station + PDU)编码为 ASCII 帧。</summary>
            public void SetupAsciiResponse(byte[] responseData)
            {
                byte lrc = CrcCalculator.ComputeLrc(responseData);
                byte[] withLrc = new byte[responseData.Length + 1];
                Buffer.BlockCopy(responseData, 0, withLrc, 0, responseData.Length);
                withLrc[responseData.Length] = lrc;

                string frame = ":" + BytesToHex(withLrc) + "\r\n";
                _readBuffer = Encoding.ASCII.GetBytes(frame);
                _readPosition = 0;
            }

            private static string BytesToHex(byte[] data)
            {
                char[] chars = new char[data.Length * 2];
                for (int i = 0; i < data.Length; i++)
                {
                    byte b = data[i];
                    chars[i * 2] = "0123456789ABCDEF"[(b >> 4) & 0x0F];
                    chars[i * 2 + 1] = "0123456789ABCDEF"[b & 0x0F];
                }
                return new string(chars);
            }

            public void Open() { IsOpen = true; }
            public void Close() { IsOpen = false; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int available = _readBuffer.Length - _readPosition;
                if (available <= 0) return 0;
                int toRead = Math.Min(count, available);
                Buffer.BlockCopy(_readBuffer, _readPosition, buffer, offset, toRead);
                _readPosition += toRead;
                return toRead;
            }

            public void Write(byte[] buffer, int offset, int count)
            {
                _writtenData = new byte[count];
                Buffer.BlockCopy(buffer, offset, _writtenData, 0, count);
            }

            public void Dispose() => Close();
        }

        [Fact]
        public void ReadDataD_GoesThroughFullModbusAsciiChain()
        {
            // D100 → MapDataD("D100") = "40101",ParseAddressEx 前缀 '4' → FC03,addr=101。
            // 服务端 ASCII 响应: Station=1, FC=03, ByteCount=2, DataHi=0xAB, DataLo=0xCD
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0xAB, 0xCD });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.ReadDataD("D100");
                Assert.True(r.IsSuccess, r.Message);
                unchecked { Assert.Equal((short)0xABCD, r.Content); }

                // 验证写出的请求是 ASCII 帧 ":" + Hex(01 03 00 65 00 01 LRC) + CRLF。
                byte[] sent = port.LastWrittenData!;
                string sentText = Encoding.ASCII.GetString(sent);
                Assert.StartsWith(":", sentText);
                Assert.EndsWith("\r\n", sentText);
                // 解析 hex 部分:01 03 00 65 00 01 LRC
                // addr 0x65 = 101
                Assert.Contains("01030065000", sentText);
            }
        }

        [Fact]
        public void ReadOutputY_Fc01Coil()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x01, 0x01, 0x01 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.ReadOutputY("Y0");
                Assert.True(r.IsSuccess, r.Message);
                Assert.True(r.Content);

                byte[] sent = port.LastWrittenData!;
                string sentText = Encoding.ASCII.GetString(sent);
                // FC01, 地址 00001(0-based=0)
                Assert.Contains("0101000", sentText);
            }
        }

        [Fact]
        public void ReadInputX_Fc02Input()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x02, 0x01, 0x01 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.ReadInputX("X0");
                Assert.True(r.IsSuccess, r.Message);
                Assert.True(r.Content);

                byte[] sent = port.LastWrittenData!;
                string sentText = Encoding.ASCII.GetString(sent);
                // FC02, 地址 10001(0-based=0)
                Assert.Contains("0102000", sentText);
            }
        }

        [Fact]
        public void ReadDataD32_ReadsTwoRegisters()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x12, 0x34, 0x56, 0x78 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.ReadDataD32("D0");
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal(0x12345678, r.Content);
            }
        }

        [Fact]
        public void ReadDataDFloat_ReadsTwoRegisters()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x04, 0x3F, 0x80, 0x00, 0x00 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.ReadDataDFloat("D0");
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal(1.0f, r.Content);
            }
        }

        // ── 写入测试 ─────────────────────────────

        [Fact]
        public void WriteDataD_SendsFc06()
        {
            var port = new AsciiFakePort();
            // FC06 写单寄存器响应 = 回显请求。MapDataD("D100")="40101",addr=101=0x65。
            port.SetupAsciiResponse(new byte[] { 0x01, 0x06, 0x00, 0x65, 0x12, 0x34 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.WriteDataD("D100", 0x1234);
                Assert.True(r.IsSuccess, r.Message);

                byte[] sent = port.LastWrittenData!;
                string sentText = Encoding.ASCII.GetString(sent);
                // FC06, addr 0x65, value 0x1234
                Assert.Contains("010600651234", sentText);
            }
        }

        [Fact]
        public void WriteOutputY_SendsFc05()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x05, 0x00, 0x00, 0xFF, 0x00 });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                var r = client.WriteOutputY("Y0", true);
                Assert.True(r.IsSuccess, r.Message);

                byte[] sent = port.LastWrittenData!;
                string sentText = Encoding.ASCII.GetString(sent);
                Assert.Contains("0105000", sentText);
                Assert.Contains("FF00", sentText);
            }
        }

        // ── 构造与继承验证 ───────────────────────

        [Fact]
        public void Constructor_SetsBigEndianByteOrder()
        {
            var port = new AsciiFakePort();
            using (var client = new DeltaAsciiClient(port, station: 2, timeout: 2000))
            {
                Assert.Equal(Endianness.BigEndian, client.ByteOrder);
                Assert.Equal((byte)2, client.Station);
            }
        }

        [Fact]
        public void InheritsFullModbusAsciiApi()
        {
            var port = new AsciiFakePort();
            port.SetupAsciiResponse(new byte[] { 0x01, 0x03, 0x02, 0xAB, 0xCD });
            port.Open();

            using (var client = new DeltaAsciiClient(port, station: 1))
            {
                // 直接用 Modbus 地址 40001 (Holding Register 0,1-based)
                var r = client.ReadUInt16("40001");
                Assert.True(r.IsSuccess, r.Message);
                Assert.Equal((ushort)0xABCD, r.Content);
            }
        }
    }
}
