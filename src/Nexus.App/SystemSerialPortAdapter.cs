using System;
using IOStopBits = System.IO.Ports.StopBits;
using IOParity = System.IO.Ports.Parity;

namespace Nexus.App.ViewModels
{
    /// <summary>
    /// 将 System.IO.Ports.SerialPort 适配为 ISerialPort 接口。
    /// </summary>
    internal class SystemSerialPortAdapter : ISerialPort
    {
        private readonly System.IO.Ports.SerialPort _port;

        public SystemSerialPortAdapter(System.IO.Ports.SerialPort port)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
        }

        public string PortName
        {
            get => _port.PortName;
            set => _port.PortName = value;
        }

        public int BaudRate
        {
            get => _port.BaudRate;
            set => _port.BaudRate = value;
        }

        public int DataBits
        {
            get => _port.DataBits;
            set => _port.DataBits = value;
        }

        public StopBits StopBits
        {
            get => (StopBits)(int)_port.StopBits;
            set => _port.StopBits = (IOStopBits)(int)value;
        }

        public Parity Parity
        {
            get => (Parity)(int)_port.Parity;
            set => _port.Parity = (IOParity)(int)value;
        }

        public int ReadTimeout
        {
            get => _port.ReadTimeout;
            set => _port.ReadTimeout = value;
        }

        public int WriteTimeout
        {
            get => _port.WriteTimeout;
            set => _port.WriteTimeout = value;
        }

        public bool IsOpen => _port.IsOpen;
        public bool DtrEnable
        {
            get => _port.DtrEnable;
            set => _port.DtrEnable = value;
        }

        public bool RtsEnable
        {
            get => _port.RtsEnable;
            set => _port.RtsEnable = value;
        }

        public void Open() => _port.Open();
        public void Close() => _port.Close();
        public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
        public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
        public void Dispose() => _port.Dispose();
    }
}
