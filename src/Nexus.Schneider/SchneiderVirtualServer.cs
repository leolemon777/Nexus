using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Schneider
{
    /// <summary>
    /// 施耐德 Modicon 虚拟服务器 — 模拟 Modicon M580/M340 的 Modbus TCP 行为。
    /// 支持 FC01-06 标准 Modbus 功能码。
    /// </summary>
    public class SchneiderVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        /// <summary>监听端口。</summary>
        public int Port { get; }

        private readonly ushort[] _holdingRegisters = new ushort[65536];
        private readonly bool[] _coils = new bool[65536];
        private readonly ushort[] _inputRegisters = new ushort[65536];
        private readonly bool[] _discreteInputs = new bool[65536];

        public SchneiderVirtualServer(int port = 5020)
        {
            Port = port;
        }

        /// <summary>设置保持寄存器的值。</summary>
        public void SetHoldingRegister(ushort address, ushort value)
        {
            if (address < _holdingRegisters.Length)
                _holdingRegisters[address] = value;
        }

        /// <summary>读取保持寄存器的值。</summary>
        public ushort GetHoldingRegister(ushort address)
        {
            return address < _holdingRegisters.Length ? _holdingRegisters[address] : (ushort)0;
        }

        /// <summary>设置线圈的值。</summary>
        public void SetCoil(ushort address, bool value)
        {
            if (address < _coils.Length)
                _coils[address] = value;
        }

        /// <summary>读取线圈的值。</summary>
        public bool GetCoil(ushort address)
        {
            return address < _coils.Length && _coils[address];
        }

        /// <summary>设置输入寄存器的值。</summary>
        public void SetInputRegister(ushort address, ushort value)
        {
            if (address < _inputRegisters.Length)
                _inputRegisters[address] = value;
        }

        /// <summary>设置离散输入的值。</summary>
        public void SetDiscreteInput(ushort address, bool value)
        {
            if (address < _discreteInputs.Length)
                _discreteInputs[address] = value;
        }

        /// <summary>启动虚拟服务器。</summary>
        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        /// <summary>停止虚拟服务器。</summary>
        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { if (!_running) break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var buffer = new byte[1024];
                while (_running && client.Connected)
                {
                    try
                    {
                        // 读取 MBAP 头 (7 字节)
                        int read = ReadExact(stream, buffer, 0, 7);
                        if (read < 7) break;

                        int length = (buffer[4] << 8) | buffer[5];
                        int pduLen = length - 1; // 去掉 UnitId
                        read = ReadExact(stream, buffer, 7, pduLen);
                        if (read < pduLen) break;

                        byte unitId = buffer[6];
                        byte fc = buffer[7];

                        byte[]? response = ProcessPdu(fc, buffer, 8, pduLen - 1);
                        if (response != null)
                        {
                            // 构造 MBAP 响应
                            byte[] mbapResp = new byte[7 + response.Length];
                            Buffer.BlockCopy(buffer, 0, mbapResp, 0, 4); // 复制 TxId + ProtocolId
                            int respLen = response.Length + 1;
                            mbapResp[4] = (byte)(respLen >> 8);
                            mbapResp[5] = (byte)respLen;
                            mbapResp[6] = unitId;
                            Buffer.BlockCopy(response, 0, mbapResp, 7, response.Length);
                            stream.Write(mbapResp, 0, mbapResp.Length);
                        }
                    }
                    catch { break; }
                }
            }
        }

        private byte[]? ProcessPdu(byte fc, byte[] buffer, int offset, int dataLen)
        {
            switch (fc)
            {
                case 0x01: // Read Coils
                case 0x02: // Read Discrete Inputs
                    return ProcessReadBits(fc, buffer, offset);

                case 0x03: // Read Holding Registers
                case 0x04: // Read Input Registers
                    return ProcessReadRegisters(fc, buffer, offset);

                case 0x05: // Write Single Coil
                    return ProcessWriteSingleCoil(buffer, offset);

                case 0x06: // Write Single Register
                    return ProcessWriteSingleRegister(buffer, offset);

                case 0x10: // Write Multiple Registers
                    return ProcessWriteMultipleRegisters(buffer, offset);

                case 0x0F: // Write Multiple Coils
                    return ProcessWriteMultipleCoils(buffer, offset);

                default:
                    return new byte[] { (byte)(fc | 0x80), 0x01 }; // 非法功能码
            }
        }

        private byte[] ProcessReadBits(byte fc, byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            ushort count = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);

            bool[] src = fc == 0x01 ? _coils : _discreteInputs;
            int byteCount = (count + 7) / 8;
            byte[] resp = new byte[2 + byteCount];
            resp[0] = fc;
            resp[1] = (byte)byteCount;

            for (int i = 0; i < count; i++)
            {
                int idx = addr + i;
                if (idx < src.Length && src[idx])
                    resp[2 + i / 8] |= (byte)(1 << (i % 8));
            }
            return resp;
        }

        private byte[] ProcessReadRegisters(byte fc, byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            ushort count = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);

            ushort[] src = fc == 0x03 ? _holdingRegisters : _inputRegisters;
            int byteCount = count * 2;
            byte[] resp = new byte[2 + byteCount];
            resp[0] = fc;
            resp[1] = (byte)byteCount;

            for (int i = 0; i < count; i++)
            {
                int idx = addr + i;
                ushort val = idx < src.Length ? src[idx] : (ushort)0;
                resp[2 + i * 2] = (byte)(val >> 8);
                resp[3 + i * 2] = (byte)val;
            }
            return resp;
        }

        private byte[] ProcessWriteSingleCoil(byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            bool value = buffer[offset + 2] == 0xFF;
            if (addr < _coils.Length) _coils[addr] = value;
            return new byte[] { 0x05, buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };
        }

        private byte[] ProcessWriteSingleRegister(byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            ushort value = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
            if (addr < _holdingRegisters.Length) _holdingRegisters[addr] = value;
            return new byte[] { 0x06, buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };
        }

        private byte[] ProcessWriteMultipleRegisters(byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            ushort count = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
            byte byteCount = buffer[offset + 4];

            for (int i = 0; i < count; i++)
            {
                int idx = addr + i;
                if (idx < _holdingRegisters.Length && offset + 5 + i * 2 + 1 < buffer.Length)
                {
                    _holdingRegisters[idx] = (ushort)((buffer[offset + 5 + i * 2] << 8) | buffer[offset + 6 + i * 2]);
                }
            }

            return new byte[] { 0x10, buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };
        }

        private byte[] ProcessWriteMultipleCoils(byte[] buffer, int offset)
        {
            ushort addr = (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
            ushort count = (ushort)((buffer[offset + 2] << 8) | buffer[offset + 3]);
            byte byteCount = buffer[offset + 4];

            for (int i = 0; i < count; i++)
            {
                int idx = addr + i;
                int byteIdx = offset + 5 + i / 8;
                if (idx < _coils.Length && byteIdx < buffer.Length)
                {
                    _coils[idx] = (buffer[byteIdx] & (1 << (i % 8))) != 0;
                }
            }

            return new byte[] { 0x0F, buffer[offset], buffer[offset + 1], buffer[offset + 2], buffer[offset + 3] };
        }

        private static int ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
