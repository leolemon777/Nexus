using System;
using System.Net.Sockets;

namespace Nexus.Modbus
{
    /// <summary>
    /// Adapts a TCP socket to the serial-port abstraction used by ASCII-style protocols.
    /// </summary>
    public sealed class TcpStreamSerialPortAdapter : ISerialPort
    {
        private readonly string _host;
        private readonly int _port;
        private TcpClient? _client;
        private NetworkStream? _stream;

        public TcpStreamSerialPortAdapter(string host, int port, int timeout)
        {
            _host = host;
            _port = port;
            ReadTimeout = timeout;
            WriteTimeout = timeout;
            PortName = host + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public string PortName { get; set; }
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 7;
        public StopBits StopBits { get; set; } = StopBits.One;
        public Parity Parity { get; set; } = Parity.Even;
        public int ReadTimeout { get; set; }
        public int WriteTimeout { get; set; }
        public bool IsOpen => _client != null && _client.Connected && _stream != null;
        public bool DtrEnable { get; set; }
        public bool RtsEnable { get; set; }

        public void Open()
        {
            if (IsOpen) return;

            Close();
            _client = new TcpClient();
            _client.ReceiveTimeout = ReadTimeout;
            _client.SendTimeout = WriteTimeout;
            _client.Connect(_host, _port);
            _stream = _client.GetStream();
            _stream.ReadTimeout = ReadTimeout;
            _stream.WriteTimeout = WriteTimeout;
        }

        public void Close()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_stream == null) throw new InvalidOperationException("TCP stream is not open.");
            return _stream.Read(buffer, offset, count);
        }

        public void Write(byte[] buffer, int offset, int count)
        {
            if (_stream == null) throw new InvalidOperationException("TCP stream is not open.");
            _stream.Write(buffer, offset, count);
        }

        public void Dispose()
        {
            Close();
        }
    }
}
