using System;
using System.IO;

namespace Nexus.Modbus.IntegrationTest
{
    /// <summary>
    /// 将 Stream 适配为 ISerialPort，用于集成测试。
    /// </summary>
    internal class StreamSerialPortAdapter : ISerialPort
    {
        private readonly Stream _stream;

        public StreamSerialPortAdapter(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        public string PortName { get; set; } = "STREAM_TEST";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.None;
        public int ReadTimeout { get; set; } = 5000;
        public int WriteTimeout { get; set; } = 5000;
        public bool IsOpen => _stream.CanRead && _stream.CanWrite;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }

        public void Open() { }
        public void Close() { }
        public int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public void Dispose() => _stream.Dispose();
    }
}
