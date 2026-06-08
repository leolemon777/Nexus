using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus 虚拟服务器 — 用于无硬件环境下的协议测试与调试。
    /// 支持 Modbus TCP，并提供内存映射 (Coils, DiscreteInputs, InputRegisters, HoldingRegisters)。
    /// </summary>
    public class ModbusVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // 内存映射
        private readonly ConcurrentDictionary<ushort, bool> _coils = new();
        private readonly ConcurrentDictionary<ushort, bool> _discreteInputs = new();
        private readonly ConcurrentDictionary<ushort, short> _inputRegisters = new();
        private readonly ConcurrentDictionary<ushort, short> _holdingRegisters = new();

        public int Port { get; }
        public bool IsRunning => _running;

        public event EventHandler<ModbusWriteEventArgs>? OnWriteReceived;

        public ModbusVirtualServer(int port = 502)
        {
            Port = port;
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
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
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        byte[] header = new byte[7];
                        int read = stream.Read(header, 0, 7);
                        if (read < 7) break;

                        int length = (header[4] << 8) | header[5];
                        byte unitId = header[6];

                        byte[] pdu = new byte[length - 1];
                        int pduRead = stream.Read(pdu, 0, pdu.Length);
                        if (pduRead < pdu.Length) break;

                        byte[] responsePdu = ProcessPdu(pdu);
                        
                        byte[] response = new byte[7 + responsePdu.Length];
                        Buffer.BlockCopy(header, 0, response, 0, 4);
                        response[4] = (byte)((responsePdu.Length + 1) >> 8);
                        response[5] = (byte)((responsePdu.Length + 1) & 0xFF);
                        response[6] = unitId;
                        Buffer.BlockCopy(responsePdu, 0, response, 7, responsePdu.Length);

                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] ProcessPdu(byte[] pdu)
        {
            if (pdu.Length < 1) return new byte[] { 0x80, 0x01 }; // Illegal Function

            byte fc = pdu[0];
            ushort address = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);

            switch (fc)
            {
                case 0x01: // Read Coils
                    {
                        byte byteCount = (byte)((count + 7) / 8);
                        byte[] data = new byte[1 + byteCount];
                        data[0] = byteCount;
                        for (int i = 0; i < count; i++)
                        {
                            if (_coils.TryGetValue((ushort)(address + i), out bool val) && val)
                                data[1 + i / 8] |= (byte)(1 << (i % 8));
                        }
                        return data;
                    }
                case 0x02: // Read Discrete Inputs
                    {
                        byte byteCount = (byte)((count + 7) / 8);
                        byte[] data = new byte[1 + byteCount];
                        data[0] = byteCount;
                        for (int i = 0; i < count; i++)
                        {
                            if (_discreteInputs.TryGetValue((ushort)(address + i), out bool val) && val)
                                data[1 + i / 8] |= (byte)(1 << (i % 8));
                        }
                        return data;
                    }
                case 0x03: // Read Holding Registers
                    {
                        byte byteCount = (byte)(count * 2);
                        byte[] data = new byte[1 + byteCount];
                        data[0] = byteCount;
                        for (int i = 0; i < count; i++)
                        {
                            short val = _holdingRegisters.TryGetValue((ushort)(address + i), out short v) ? v : (short)0;
                            data[1 + i * 2] = (byte)(val >> 8);
                            data[2 + i * 2] = (byte)(val & 0xFF);
                        }
                        return data;
                    }
                case 0x04: // Read Input Registers
                    {
                        byte byteCount = (byte)(count * 2);
                        byte[] data = new byte[1 + byteCount];
                        data[0] = byteCount;
                        for (int i = 0; i < count; i++)
                        {
                            short val = _inputRegisters.TryGetValue((ushort)(address + i), out short v) ? v : (short)0;
                            data[1 + i * 2] = (byte)(val >> 8);
                            data[2 + i * 2] = (byte)(val & 0xFF);
                        }
                        return data;
                    }
                case 0x05: // Write Single Coil
                    {
                        bool val = (pdu[3] & 0xFF) == 0xFF;
                        _coils[address] = val;
                        OnWriteReceived?.Invoke(this, new ModbusWriteEventArgs { FunctionCode = fc, Address = address, IsCoil = true, Value = val });
                        return new byte[] { 0x05, pdu[1], pdu[2], pdu[3], pdu[4] };
                    }
                case 0x06: // Write Single Register
                    {
                        short val = (short)((pdu[3] << 8) | pdu[4]);
                        _holdingRegisters[address] = val;
                        OnWriteReceived?.Invoke(this, new ModbusWriteEventArgs { FunctionCode = fc, Address = address, IsCoil = false, Value = val });
                        return new byte[] { 0x06, pdu[1], pdu[2], pdu[3], pdu[4] };
                    }
                case 0x0F: // Write Multiple Coils
                    {
                        ushort qty = (ushort)((pdu[3] << 8) | pdu[4]);
                        byte byteCount = pdu[5];
                        for (int i = 0; i < qty; i++)
                        {
                            bool val = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
                            _coils[(ushort)(address + i)] = val;
                        }
                        OnWriteReceived?.Invoke(this, new ModbusWriteEventArgs { FunctionCode = fc, Address = address, IsCoil = true, Value = qty });
                        return new byte[] { 0x0F, pdu[1], pdu[2], pdu[3], pdu[4] };
                    }
                case 0x10: // Write Multiple Registers
                    {
                        ushort qty = (ushort)((pdu[3] << 8) | pdu[4]);
                        byte byteCount = pdu[5];
                        for (int i = 0; i < qty; i++)
                        {
                            short val = (short)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
                            _holdingRegisters[(ushort)(address + i)] = val;
                        }
                        OnWriteReceived?.Invoke(this, new ModbusWriteEventArgs { FunctionCode = fc, Address = address, IsCoil = false, Value = qty });
                        return new byte[] { 0x10, pdu[1], pdu[2], pdu[3], pdu[4] };
                    }
                default:
                    return new byte[] { (byte)(fc | 0x80), 0x01 }; // Illegal Function
            }
        }

        // ── 内存操作 API ─────────────────────────

        public void SetCoil(ushort address, bool value) => _coils[address] = value;
        public void SetDiscreteInput(ushort address, bool value) => _discreteInputs[address] = value;
        public void SetInputRegister(ushort address, short value) => _inputRegisters[address] = value;
        public void SetHoldingRegister(ushort address, short value) => _holdingRegisters[address] = value;

        public void Dispose() => Stop();
    }

    public class ModbusWriteEventArgs : EventArgs
    {
        public byte FunctionCode { get; set; }
        public ushort Address { get; set; }
        public bool IsCoil { get; set; }
        public object? Value { get; set; }
    }
}