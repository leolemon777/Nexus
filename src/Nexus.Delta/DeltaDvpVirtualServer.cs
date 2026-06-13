using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Delta
{
    /// <summary>
    /// 台达 DVP 虚拟 PLC 服务器 — 模拟 Modbus RTU over TCP 通讯。
    /// <para>用于集成测试，无需真实台达 PLC 硬件。</para>
    /// <para>支持 FC01/02/03/04/05/06/0F/10 功能码。</para>
    /// </summary>
    public class DeltaDvpVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private readonly object _memLock = new object();

        // 内存模型
        private readonly bool[] _coils = new bool[65536];          // 0x0000 - 线圈
        private readonly bool[] _discreteInputs = new bool[65536]; // 1x - 离散输入
        private readonly ushort[] _inputRegisters = new ushort[32768]; // 3x - 输入寄存器
        private readonly ushort[] _holdingRegisters = new ushort[65536]; // 4x - 保持寄存器

        /// <summary>监听端口。</summary>
        public int Port { get; }

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        public DeltaDvpVirtualServer(int port = 5020)
        {
            Port = port;
        }

        /// <summary>设置保持寄存器值（用于测试预设数据）。</summary>
        public void SetHoldingRegister(ushort address, ushort value)
        {
            lock (_memLock) _holdingRegisters[address] = value;
        }

        /// <summary>设置线圈值。</summary>
        public void SetCoil(ushort address, bool value)
        {
            lock (_memLock) _coils[address] = value;
        }

        /// <summary>设置离散输入值。</summary>
        public void SetDiscreteInput(ushort address, bool value)
        {
            lock (_memLock) _discreteInputs[address] = value;
        }

        /// <summary>设置输入寄存器值。</summary>
        public void SetInputRegister(ushort address, ushort value)
        {
            lock (_memLock) _inputRegisters[address] = value;
        }

        /// <summary>读取保持寄存器值（用于测试验证）。</summary>
        public ushort GetHoldingRegister(ushort address)
        {
            lock (_memLock) return _holdingRegisters[address];
        }

        /// <summary>读取线圈值。</summary>
        public bool GetCoil(ushort address)
        {
            lock (_memLock) return _coils[address];
        }

        /// <summary>启动服务器。</summary>
        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        /// <summary>停止服务器。</summary>
        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
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
            {
                var stream = client.GetStream();

                while (_running && client.Connected)
                {
                    try
                    {
                        var header = new byte[2];
                        if (!ReadExact(stream, header, 0, header.Length)) break;

                        byte station = header[0];
                        byte fc = header[1];
                        byte[] request = ReadRtuRequestPayload(stream, fc);

                        // 处理请求
                        byte[]? response = ProcessRequest(fc, request, 0, request.Length);
                        if (response == null) break;

                        var resp = BuildRtuResponse(station, response);
                        stream.Write(resp, 0, resp.Length);
                    }
                    catch { break; }
                }
            }
        }

        private static byte[] ReadRtuRequestPayload(NetworkStream stream, byte fc)
        {
            switch (fc)
            {
                case 0x01:
                case 0x02:
                case 0x03:
                case 0x04:
                case 0x05:
                case 0x06:
                    var fixedPayload = new byte[4];
                    ReadExact(stream, fixedPayload, 0, fixedPayload.Length);
                    ReadAndDiscardCrc(stream);
                    return fixedPayload;

                case 0x0F:
                case 0x10:
                    var prefix = new byte[5];
                    ReadExact(stream, prefix, 0, prefix.Length);
                    int byteCount = prefix[4];
                    var payload = new byte[prefix.Length + byteCount];
                    Buffer.BlockCopy(prefix, 0, payload, 0, prefix.Length);
                    if (byteCount > 0)
                        ReadExact(stream, payload, prefix.Length, byteCount);
                    ReadAndDiscardCrc(stream);
                    return payload;

                default:
                    ReadAndDiscardCrc(stream);
                    return Array.Empty<byte>();
            }
        }

        private static void ReadAndDiscardCrc(NetworkStream stream)
        {
            var crc = new byte[2];
            ReadExact(stream, crc, 0, crc.Length);
        }

        private static byte[] BuildRtuResponse(byte station, byte[] pdu)
        {
            var response = new byte[1 + pdu.Length + 2];
            response[0] = station;
            Buffer.BlockCopy(pdu, 0, response, 1, pdu.Length);

            ushort crc = CrcCalculator.ComputeCrc16(response, 0, response.Length - 2);
            response[response.Length - 2] = (byte)(crc & 0xFF);
            response[response.Length - 1] = (byte)(crc >> 8);
            return response;
        }

        private byte[]? ProcessRequest(byte fc, byte[] buffer, int offset, int dataLen)
        {
            switch (fc)
            {
                case 0x01: return ProcessReadCoils(buffer, offset);
                case 0x02: return ProcessReadDiscreteInputs(buffer, offset);
                case 0x03: return ProcessReadHoldingRegisters(buffer, offset);
                case 0x04: return ProcessReadInputRegisters(buffer, offset);
                case 0x05: return ProcessWriteSingleCoil(buffer, offset);
                case 0x06: return ProcessWriteSingleRegister(buffer, offset);
                case 0x0F: return ProcessWriteMultipleCoils(buffer, offset, dataLen);
                case 0x10: return ProcessWriteMultipleRegisters(buffer, offset, dataLen);
                default: return BuildException(fc, 0x01);
            }
        }

        private byte[] ProcessReadCoils(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            int byteCount = (count + 7) / 8;
            var data = new byte[byteCount];

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                    if (_coils[addr + i]) data[i / 8] |= (byte)(1 << (i % 8));
            }

            var result = new byte[2 + byteCount];
            result[0] = 0x01;
            result[1] = (byte)byteCount;
            Buffer.BlockCopy(data, 0, result, 2, byteCount);
            return result;
        }

        private byte[] ProcessReadDiscreteInputs(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            int byteCount = (count + 7) / 8;
            var data = new byte[byteCount];

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                    if (_discreteInputs[addr + i]) data[i / 8] |= (byte)(1 << (i % 8));
            }

            var result = new byte[2 + byteCount];
            result[0] = 0x02;
            result[1] = (byte)byteCount;
            Buffer.BlockCopy(data, 0, result, 2, byteCount);
            return result;
        }

        private byte[] ProcessReadHoldingRegisters(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            var result = new byte[2 + count * 2];
            result[0] = 0x03;
            result[1] = (byte)(count * 2);

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    ushort v = _holdingRegisters[addr + i];
                    result[2 + i * 2] = (byte)(v >> 8);
                    result[3 + i * 2] = (byte)(v & 0xFF);
                }
            }
            return result;
        }

        private byte[] ProcessReadInputRegisters(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            var result = new byte[2 + count * 2];
            result[0] = 0x04;
            result[1] = (byte)(count * 2);

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    ushort v = _inputRegisters[addr + i];
                    result[2 + i * 2] = (byte)(v >> 8);
                    result[3 + i * 2] = (byte)(v & 0xFF);
                }
            }
            return result;
        }

        private byte[] ProcessWriteSingleCoil(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            bool value = buf[off + 2] == 0xFF;
            lock (_memLock) _coils[addr] = value;

            var result = new byte[5];
            result[0] = 0x05;
            result[1] = (byte)(addr >> 8); result[2] = (byte)(addr & 0xFF);
            result[3] = value ? (byte)0xFF : (byte)0x00; result[4] = 0x00;
            return result;
        }

        private byte[] ProcessWriteSingleRegister(byte[] buf, int off)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort value = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            lock (_memLock) _holdingRegisters[addr] = value;

            var result = new byte[5];
            result[0] = 0x06;
            result[1] = (byte)(addr >> 8); result[2] = (byte)(addr & 0xFF);
            result[3] = (byte)(value >> 8); result[4] = (byte)(value & 0xFF);
            return result;
        }

        private byte[] ProcessWriteMultipleCoils(byte[] buf, int off, int dataLen)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            int byteCount = buf[off + 4];

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int byteIdx = off + 5 + i / 8;
                    int bitIdx = i % 8;
                    _coils[addr + i] = (buf[byteIdx] & (1 << bitIdx)) != 0;
                }
            }

            var result = new byte[5];
            result[0] = 0x0F;
            result[1] = (byte)(addr >> 8); result[2] = (byte)(addr & 0xFF);
            result[3] = (byte)(count >> 8); result[4] = (byte)(count & 0xFF);
            return result;
        }

        private byte[] ProcessWriteMultipleRegisters(byte[] buf, int off, int dataLen)
        {
            ushort addr = (ushort)((buf[off] << 8) | buf[off + 1]);
            ushort count = (ushort)((buf[off + 2] << 8) | buf[off + 3]);
            int byteCount = buf[off + 4];

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int srcOff = off + 5 + i * 2;
                    _holdingRegisters[addr + i] = (ushort)((buf[srcOff] << 8) | buf[srcOff + 1]);
                }
            }

            var result = new byte[5];
            result[0] = 0x10;
            result[1] = (byte)(addr >> 8); result[2] = (byte)(addr & 0xFF);
            result[3] = (byte)(count >> 8); result[4] = (byte)(count & 0xFF);
            return result;
        }

        private static byte[] BuildException(byte fc, byte errorCode)
        {
            return new byte[] { (byte)(fc | 0x80), errorCode };
        }

        private static bool ReadExact(NetworkStream stream, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, offset + read, count - read);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
