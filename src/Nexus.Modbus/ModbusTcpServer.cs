using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Modbus
{
    /// <summary>
    /// 虚拟 Modbus TCP 服务器 — 模拟真实 PLC，用于无硬件测试。
    /// 内置线圈、离散输入、保持寄存器、输入寄存器四区内存。
    /// 支持功能码 01-06, 08, 15, 16, 22, 23, 43/14。
    /// 支持从站地址过滤和请求日志。
    /// </summary>
    public class ModbusTcpServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        /// <summary>允许的从站地址列表。为空时允许所有。</summary>
        public HashSet<byte> AllowedStationIds { get; } = new HashSet<byte>();

        /// <summary>请求接收事件（用于调试和日志）。</summary>
        public event EventHandler<ModbusRequestEventArgs>? OnRequestReceived;

        // 四区内存模型
        private readonly bool[] _coils = new bool[65536];           // 0xxxx 线圈
        private readonly bool[] _discreteInputs = new bool[65536];  // 1xxxx 离散输入
        private readonly ushort[] _holdingRegisters = new ushort[65536];  // 4xxxx 保持寄存器
        private readonly ushort[] _inputRegisters = new ushort[65536];    // 3xxxx 输入寄存器
        private readonly ConcurrentDictionary<TcpClient, Thread> _clients = new ConcurrentDictionary<TcpClient, Thread>();

        // FC08 诊断计数器
        private int _busMessageCount;
        private int _slaveMessageCount;

        public int Port { get; }
        public bool IsRunning => _running;

        public ModbusTcpServer(int port = 502) { Port = port; }

        // ── 设备标识（FC43/14）───────────────────

        /// <summary>厂商名称 (ObjectId=0x00)。</summary>
        public string VendorName { get; set; } = "Nexus Virtual";

        /// <summary>产品代码 (ObjectId=0x01)。</summary>
        public string ProductCode { get; set; } = "NX-SIM";

        /// <summary>主/次版本 (ObjectId=0x02)。</summary>
        public string MajorMinorRevision { get; set; } = "1.0.0";

        /// <summary>设备 URL (ObjectId=0x03)。</summary>
        public string DeviceUrl { get; set; } = "https://github.com/nexus";

        /// <summary>产品名称 (ObjectId=0x04)。</summary>
        public string ProductName { get; set; } = "Nexus Modbus Simulator";

        /// <summary>设备型号 (ObjectId=0x05)。</summary>
        public string ModelName { get; set; } = "NX-VIRTUAL";

        /// <summary>用户应用名称 (ObjectId=0x06)。</summary>
        public string UserApplicationName { get; set; } = "Modbus Test Server";

        /// <summary>符合性等级 (0x01=Basic, 0x02=Regular, 0x03=Extended, 0x81..=Extended+)</summary>
        public byte ConformityLevel { get; set; } = 0x02; // Regular

        // ── 预设/读取数据（测试用）─────────────────

        public void SetCoil(ushort address, bool value) => _coils[address] = value;
        public bool GetCoil(ushort address) => _coils[address];

        public void SetDiscreteInput(ushort address, bool value) => _discreteInputs[address] = value;
        public bool GetDiscreteInput(ushort address) => _discreteInputs[address];

        public void SetHoldingRegister(ushort address, ushort value) => _holdingRegisters[address] = value;
        public ushort GetHoldingRegister(ushort address) => _holdingRegisters[address];
        // Alias
        public void SetRegister(ushort address, ushort value) => _holdingRegisters[address] = value;
        public ushort GetRegister(ushort address) => _holdingRegisters[address];

        public void SetInputRegister(ushort address, ushort value) => _inputRegisters[address] = value;
        public ushort GetInputRegister(ushort address) => _inputRegisters[address];

        // ── 服务器控制 ────────────────────────────

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
            foreach (var kv in _clients)
            {
                try { kv.Key.Close(); } catch { }
            }
            _clients.Clear();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    _clients[client] = thread;
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
                        if (!ReadExact(stream, header)) break;

                        int length = (header[4] << 8) | header[5];
                        int pduLen = length - 1;
                        if (pduLen <= 0 || pduLen > 256) break;

                        byte unitId = header[6];
                        byte[] pdu = new byte[pduLen];
                        if (!ReadExact(stream, pdu)) break;

                        byte[]? responsePdu = ProcessPduWithLog(unitId, pdu);
                        if (responsePdu == null) continue;

                        int respLen = responsePdu.Length + 1;
                        byte[] response = new byte[7 + responsePdu.Length];
                        response[0] = header[0]; response[1] = header[1];
                        response[2] = 0; response[3] = 0;
                        response[4] = (byte)(respLen >> 8); response[5] = (byte)respLen;
                        response[6] = unitId;
                        Buffer.BlockCopy(responsePdu, 0, response, 7, responsePdu.Length);

                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
            finally { _clients.TryRemove(client, out _); }
        }

        private byte[]? ProcessPdu(byte[] pdu)
        {
            if (pdu.Length < 1) return null;
            byte func = pdu[0];
            try
            {
                return func switch
                {
                    0x01 => ReadBits(pdu, _coils),
                    0x02 => ReadBits(pdu, _discreteInputs),
                    0x03 => ReadRegisters(pdu, _holdingRegisters),
                    0x04 => ReadRegisters(pdu, _inputRegisters),
                    0x05 => WriteSingleCoil(pdu),
                    0x06 => WriteSingleRegister(pdu),
                    0x08 => Diagnostics(pdu),
                    0x0F => WriteMultipleCoils(pdu),
                    0x10 => WriteMultipleRegisters(pdu),
                    0x16 => MaskWriteRegister(pdu),
                    0x17 => ReadWriteMultipleRegisters(pdu),
                    0x2B => EncapsulatedInterface(pdu),
                    _ => BuildException(func, 1)
                };
            }
            catch { return BuildException(func, 4); }
        }

        private byte[]? ProcessPduWithLog(byte unitId, byte[] pdu)
        {
            // 从站地址过滤
            if (AllowedStationIds.Count > 0 && !AllowedStationIds.Contains(unitId))
                return BuildException(pdu[0], 2); // 非法数据地址

            // 更新诊断计数器
            Interlocked.Increment(ref _busMessageCount);
            Interlocked.Increment(ref _slaveMessageCount);

            // 触发请求日志事件
            OnRequestReceived?.Invoke(this, new ModbusRequestEventArgs
            {
                FunctionCode = pdu[0],
                StationId = unitId,
                RawData = pdu,
                Timestamp = DateTime.Now
            });

            return ProcessPdu(pdu);
        }

        // ── FC01/02 — 读位数据 ───────────────────

        private byte[] ReadBits(byte[] pdu, bool[] bitStore)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            if (count < 1 || count > 2000) return BuildException(pdu[0], 3);

            int byteCount = (count + 7) / 8;
            byte[] data = new byte[byteCount];
            for (int i = 0; i < count; i++)
            {
                if (bitStore[addr + i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }
            byte[] result = new byte[2 + byteCount];
            result[0] = pdu[0]; // keep original FC
            result[1] = (byte)byteCount;
            Buffer.BlockCopy(data, 0, result, 2, byteCount);
            return result;
        }

        // ── FC03/04 — 读寄存器 ───────────────────

        private byte[] ReadRegisters(byte[] pdu, ushort[] regStore)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            if (count < 1 || count > 125) return BuildException(pdu[0], 3);

            int byteCount = count * 2;
            byte[] result = new byte[2 + byteCount];
            result[0] = pdu[0]; // keep original FC
            result[1] = (byte)byteCount;
            for (int i = 0; i < count; i++)
            {
                ushort val = regStore[addr + i];
                result[2 + i * 2] = (byte)(val >> 8);
                result[3 + i * 2] = (byte)val;
            }
            return result;
        }

        // ── FC05 — 写单线圈 ──────────────────────

        private byte[] WriteSingleCoil(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            bool value = pdu[3] == 0xFF;
            _coils[addr] = value;
            return pdu;
        }

        // ── FC06 — 写单寄存器 ────────────────────

        private byte[] WriteSingleRegister(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
            _holdingRegisters[addr] = value;
            return pdu;
        }

        // ── FC08 — 诊断 (Diagnostics) ────────────

        private byte[] Diagnostics(byte[] pdu)
        {
            // PDU: FC(1) + SubFunction(2) + Data(2)
            if (pdu.Length < 4)
                return BuildException(0x08, 3);

            ushort subFunc = (ushort)((pdu[1] << 8) | pdu[2]);

            switch (subFunc)
            {
                case 0x0000: // Return Query Data (回环)
                    // Echo: FC + SubFunction + Data (exactly what was sent)
                    byte[] echo = new byte[pdu.Length];
                    Buffer.BlockCopy(pdu, 0, echo, 0, pdu.Length);
                    return echo;

                case 0x0001: // Restart Communications
                    return BuildDiagnosticsResponse(subFunc, 0x0000);

                case 0x0002: // Return Diagnostic Register
                    return BuildDiagnosticsResponse(subFunc, 0x0000);

                case 0x000A: // Clear Counters + Diagnostic Register
                    Interlocked.Exchange(ref _busMessageCount, 0);
                    Interlocked.Exchange(ref _slaveMessageCount, 0);
                    return BuildDiagnosticsResponse(subFunc, 0x0000);

                case 0x000B: // Return Bus Message Count
                    return BuildDiagnosticsResponse(subFunc, (ushort)_busMessageCount);

                case 0x000E: // Return Slave Message Count
                    return BuildDiagnosticsResponse(subFunc, (ushort)_slaveMessageCount);

                case 0x000C: // Return Bus Comm Error Count
                case 0x000D: // Return Bus Exception Error Count
                case 0x000F: // Return Slave No Response Count
                case 0x0010: // Return Slave NAK Count
                case 0x0011: // Return Slave Busy Count
                case 0x0012: // Return Bus Char Overrun Count
                case 0x0014: // Clear Overrun Counters
                case 0x0015: // Return IOP Overrun Count
                    return BuildDiagnosticsResponse(subFunc, 0x0000);

                default:
                    return BuildException(0x08, 1);
            }
        }

        private static byte[] BuildDiagnosticsResponse(ushort subFunction, ushort data)
        {
            return new byte[]
            {
                0x08,
                (byte)(subFunction >> 8), (byte)subFunction,
                (byte)(data >> 8), (byte)data
            };
        }

        // ── FC15 — 写多个线圈 ────────────────────

        private byte[] WriteMultipleCoils(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            for (int i = 0; i < count; i++)
                _coils[addr + i] = (pdu[6 + i / 8] & (1 << (i % 8))) != 0;
            return new byte[] { 0x0F, pdu[1], pdu[2], pdu[3], pdu[4] };
        }

        // ── FC16 — 写多个寄存器 ──────────────────

        private byte[] WriteMultipleRegisters(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            for (int i = 0; i < count; i++)
                _holdingRegisters[addr + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
            return new byte[] { 0x10, pdu[1], pdu[2], pdu[3], pdu[4] };
        }

        // ── FC22 — 掩码写保持寄存器 ───────────────

        private byte[] MaskWriteRegister(byte[] pdu)
        {
            if (pdu.Length < 7) return BuildException(0x16, 3);

            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort andMask = (ushort)((pdu[3] << 8) | pdu[4]);
            ushort orMask = (ushort)((pdu[5] << 8) | pdu[6]);
            ushort current = _holdingRegisters[addr];
            _holdingRegisters[addr] = (ushort)((current & andMask) | (orMask & ~andMask));

            return new byte[] { 0x16, pdu[1], pdu[2], pdu[3], pdu[4], pdu[5], pdu[6] };
        }

        // ── FC23 — 读写多个寄存器（原子操作）──

        private byte[] ReadWriteMultipleRegisters(byte[] pdu)
        {
            // PDU: FC(1) + ReadAddr(2) + ReadCount(2) + WriteAddr(2) + WriteCount(2) + WriteByteCount(1) + Data
            if (pdu.Length < 10) return BuildException(0x17, 3);
            ushort readAddr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort readCount = (ushort)((pdu[3] << 8) | pdu[4]);
            ushort writeAddr = (ushort)((pdu[5] << 8) | pdu[6]);
            ushort writeCount = (ushort)((pdu[7] << 8) | pdu[8]);
            byte writeByteCount = pdu[9];

            // Write first
            for (int i = 0; i < writeCount; i++)
            {
                int dataOffset = 10 + i * 2;
                if (dataOffset + 1 >= pdu.Length) break;
                _holdingRegisters[writeAddr + i] = (ushort)((pdu[dataOffset] << 8) | pdu[dataOffset + 1]);
            }

            // Then read
            int readByteCount = readCount * 2;
            byte[] result = new byte[2 + readByteCount];
            result[0] = 0x17;
            result[1] = (byte)readByteCount;
            for (int i = 0; i < readCount; i++)
            {
                ushort val = _holdingRegisters[readAddr + i];
                result[2 + i * 2] = (byte)(val >> 8);
                result[3 + i * 2] = (byte)val;
            }
            return result;
        }

        // ── FC43 — 封装接口传输 (Encapsulated Interface Transport) ──

        private byte[] EncapsulatedInterface(byte[] pdu)
        {
            if (pdu.Length < 3) return BuildException(0x2B, 3);

            byte meiType = pdu[1];
            switch (meiType)
            {
                case 0x0E: // Read Device Identification
                    return ReadDeviceIdentification(pdu);
                default:
                    return BuildException(0x2B, 1);
            }
        }

        private byte[] ReadDeviceIdentification(byte[] pdu)
        {
            // Request PDU: FC(1) + MEI(1) + ReadLevel(1) + ObjectId(1) = 4 bytes
            if (pdu.Length < 4) return BuildException(0x2B, 3);

            byte readLevel = pdu[2];
            byte startObjectId = pdu[3];

            // 构建标识对象列表
            var objects = BuildDeviceIdObjects(readLevel, startObjectId);

            // 计算响应大小
            int objectsSize = 0;
            for (int i = 0; i < objects.Count; i++)
                objectsSize += 2 + objects[i].Value.Length; // ObjectId(1) + Length(1) + Value

            // Response PDU: FC(1) + MEI(1) + ReadLevel(1) + Conformity(1) + MoreFollows(1) + NextObjId(1) + ObjCount(1) + Objects
            byte[] result = new byte[7 + objectsSize];
            result[0] = 0x2B;    // FC
            result[1] = 0x0E;    // MEI type
            result[2] = readLevel;
            result[3] = ConformityLevel;
            result[4] = 0x00;    // More follows (we return all in one response)
            result[5] = 0x00;    // Next object ID
            result[6] = (byte)objects.Count;

            int offset = 7;
            for (int i = 0; i < objects.Count; i++)
            {
                result[offset++] = objects[i].Id;
                result[offset++] = (byte)objects[i].Value.Length;
                Buffer.BlockCopy(objects[i].Value, 0, result, offset, objects[i].Value.Length);
                offset += objects[i].Value.Length;
            }

            return result;
        }

        private List<DeviceIdObject> BuildDeviceIdObjects(byte readLevel, byte startObjectId)
        {
            var allObjects = new List<DeviceIdObject>();

            // 基本标识 (ObjectId 0x00-0x02)
            allObjects.Add(new DeviceIdObject(0x00, VendorName));
            allObjects.Add(new DeviceIdObject(0x01, ProductCode));
            allObjects.Add(new DeviceIdObject(0x02, MajorMinorRevision));

            if (readLevel >= 0x02)
            {
                // 常规标识 (ObjectId 0x03-0x06)
                allObjects.Add(new DeviceIdObject(0x03, DeviceUrl));
                allObjects.Add(new DeviceIdObject(0x04, ProductName));
                allObjects.Add(new DeviceIdObject(0x05, ModelName));
                allObjects.Add(new DeviceIdObject(0x06, UserApplicationName));
            }

            // 按 startObjectId 过滤
            if (startObjectId > 0x00)
            {
                int index = allObjects.FindIndex(o => o.Id >= startObjectId);
                if (index >= 0)
                    return allObjects.GetRange(index, allObjects.Count - index);
                return new List<DeviceIdObject>();
            }

            return allObjects;
        }

        private struct DeviceIdObject
        {
            public byte Id;
            public byte[] Value;

            public DeviceIdObject(byte id, string text)
            {
                Id = id;
                Value = Encoding.ASCII.GetBytes(text);
            }
        }

        private byte[] BuildException(byte func, byte code) => new byte[] { (byte)(func | 0x80), code };

        private static bool ReadExact(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        public void Dispose() => Stop();
    }

    /// <summary>Modbus 请求事件参数。</summary>
    public class ModbusRequestEventArgs : EventArgs
    {
        public byte FunctionCode { get; set; }
        public byte StationId { get; set; }
        public byte[] RawData { get; set; } = Array.Empty<byte>();
        public DateTime Timestamp { get; set; }
    }
}
