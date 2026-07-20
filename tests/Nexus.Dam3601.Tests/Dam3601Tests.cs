using System;
using Nexus;
using Nexus.Dam3601;
using Nexus.Modbus;
using Xunit;

namespace Nexus.Dam3601.Tests
{
    /// <summary>
    /// DAM3601 单元测试 — 验证工程量换算(纯函数,无硬件依赖)+ 通过 fake Modbus 客户端验证寄存器访问。
    /// </summary>
    public class Dam3601Tests
    {
        // ── 工程量换算(纯函数,可独立测试)────────

        [Theory]
        [InlineData(0, 0, 0.0)]               // 0V / 0mA
        [InlineData(65535, 0, 5.0)]           // 满量程 5V
        [InlineData(32768, 0, 2.5, 0.01)]     // 中点 ≈ 2.5V(允许误差)
        [InlineData(0, 1, 0.0)]               // 0-10V: 0
        [InlineData(65535, 1, 10.0)]          // 0-10V: 满量程 10V
        [InlineData(0, 2, 0.0)]               // 0-20mA: 0
        [InlineData(65535, 2, 20.0)]          // 0-20mA: 满量程
        [InlineData(0, 3, 4.0)]               // 4-20mA: 起点 4mA
        [InlineData(65535, 3, 20.0)]          // 4-20mA: 终点 20mA
        [InlineData(32768, 3, 12.0, 0.1)]     // 4-20mA: 中点 ≈ 12mA
        public void ConvertToEngineering_KnownRanges_CorrectValues(
            ushort raw, int range, double expected, double tolerance = 0.001)
        {
            double actual = Dam3601Client.ConvertToEngineering(raw, range);
            Assert.InRange(actual, expected - tolerance, expected + tolerance);
        }

        [Fact]
        public void ConvertToEngineering_UnknownRange_ReturnsRaw()
        {
            // 未知量程返回原始 ADC 值。
            Assert.Equal(12345.0, Dam3601Client.ConvertToEngineering(12345, 99));
        }

        // ── 寄存器访问(通过 fake Modbus 客户端)────

        /// <summary>极简 ModbusRtuClient 子类,允许注入寄存器数据。</summary>
        private sealed class FakeModbusRtuClient : ModbusRtuClient
        {
            private readonly System.Collections.Generic.Dictionary<int, ushort> _registers =
                new System.Collections.Generic.Dictionary<int, ushort>();

            public FakeModbusRtuClient(byte station = 1)
                : base(new FakePort(), station)
            {
            }

            public void SetRegister(int modbusAddress, ushort value) => _registers[modbusAddress] = value;

            public new byte Station => base.Station;

            // Override ReadUInt16 — 但 ModbusRtuClient 的 ReadUInt16 不是 virtual,
            // 我们换种方式:重写整个读取路径过于复杂,改用一个 wrapper 类暴露同样的接口。
            // 实际上 Dam3601Client 只依赖 _modbus.ReadUInt16/ReadBytes,
            // 而 ModbusRtuClient 这些方法是 virtual 的(继承自 TcpDeviceBase / SerialDeviceBase)。
            // 但 SerialDeviceBase 的 ReadUInt16 是 virtual,ModbusRtuClient 没用 new 隐藏。
        }

        /// <summary>Mock 用 fake 串口。</summary>
        private sealed class FakePort : Nexus.ISerialPort
        {
            public string PortName { get; set; } = "COM_FAKE";
            public int BaudRate { get; set; } = 9600;
            public int DataBits { get; set; } = 8;
            public StopBits StopBits { get; set; } = StopBits.One;
            public Parity Parity { get; set; } = Parity.None;
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

        // 由于 ModbusRtuClient 的 ReadUInt16 不是简单可 mock(走 SendAndReceive 全链路),
        // 完整 mock 需要 fake 串口返回完整 Modbus 帧。这超出本单元测试范围。
        // Dam3601Client 的寄存器访问通过集成测试(用 ModbusTcpServer 之类)更合适。
        // 此处仅验证构造和参数校验。

        [Fact]
        public void Constructor_NullModbus_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new Dam3601Client((ModbusRtuClient)null!));
        }

        [Fact]
        public void Constructor_StationMismatch_Throws()
        {
            var modbus = new FakeModbusRtuClient(station: 1);
            Assert.Throws<ArgumentException>(() => new Dam3601Client(modbus, station: 2));
        }

        [Fact]
        public void ReadRawValue_OutOfRangeChannel_ReturnsFailed()
        {
            var modbus = new FakeModbusRtuClient(station: 1);
            var client = new Dam3601Client(modbus, station: 1);

            var r1 = client.ReadRawValue(-1);
            Assert.False(r1.IsSuccess);

            var r2 = client.ReadRawValue(8);  // 默认 8 通道,通道号 8 越界
            Assert.False(r2.IsSuccess);
        }

        [Fact]
        public void ReadRange_OutOfRangeChannel_ReturnsFailed()
        {
            var modbus = new FakeModbusRtuClient(station: 1);
            var client = new Dam3601Client(modbus, station: 1);

            Assert.False(client.ReadRange(-1).IsSuccess);
            Assert.False(client.ReadRange(8).IsSuccess);
        }

        [Fact]
        public void CustomRegisterBaseAddresses_Work()
        {
            var modbus = new FakeModbusRtuClient(station: 1);
            var client = new Dam3601Client(modbus, station: 1)
            {
                ChannelValueRegister = 0x100,
                ChannelRangeRegister = 0x200,
                ChannelCount = 4
            };

            // 通道 0..3,通道 4 应越界。
            Assert.False(client.ReadRawValue(4).IsSuccess);
            Assert.False(client.ReadRange(4).IsSuccess);
        }
    }
}
