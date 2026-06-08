using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Mitsubishi
{
    /// <summary>
    /// MC-3E Binary 虚拟 PLC 服务器 — 模拟三菱 Q/FX 系列 PLC，用于无硬件测试。
    /// 内存模型: D 寄存器(65536 字), M 继电器(65536 位), X 输入(1024 位), Y 输出(1024 位),
    ///   W 链接寄存器(65536 字), R 文件寄存器(65536 字), B 链接继电器(65536 位),
    ///   L 锁存继电器(65536 位), F 状态(65536 位), V 边沿继电器(65536 位), S 步进继电器(65536 位)。
    /// <para>支持指令: 批量读字/位(0x0401), 批量写字/位(0x1401), 随机读取(0x0403), 随机写入(0x1402),</para>
    /// <para>  RemoteRun(0x1001), RemoteStop(0x1002), RemoteReset(0x1006), ReadPlcType(0x0101), ErrorStateReset(0x1617)。</para>
    /// </summary>
    public class Mc3EVirtuServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        // ── 内存模型 — 字寄存器 ─────────────────────
        private readonly ushort[] _dRegisters = new ushort[65536];    // D 数据寄存器
        private readonly ushort[] _wRegisters = new ushort[65536];    // W 链接寄存器
        private readonly ushort[] _rRegisters = new ushort[65536];    // R 文件寄存器
        private readonly ushort[] _zIndex = new ushort[16];           // Z 变址寄存器
        private readonly ushort[] _zrRegisters = new ushort[65536];   // ZR 文件寄存器(扩展)
        private readonly ushort[] _sdRegisters = new ushort[65536];   // SD 特殊寄存器
        private readonly ushort[] _swRegisters = new ushort[65536];   // SW 直接链接寄存器

        // ── 内存模型 — 位寄存器 ─────────────────────
        private readonly bool[] _mRelays = new bool[65536];           // M 内部继电器
        private readonly bool[] _xInputs = new bool[1024];            // X 输入 (十六进制地址 0-3FF)
        private readonly bool[] _yOutputs = new bool[1024];           // Y 输出
        private readonly bool[] _bRelays = new bool[65536];           // B 链接继电器
        private readonly bool[] _lRelays = new bool[65536];           // L 锁存继电器
        private readonly bool[] _fStates = new bool[65536];           // F 状态
        private readonly bool[] _vEdges = new bool[65536];            // V 边沿继电器
        private readonly bool[] _sSteps = new bool[65536];            // S 步进继电器

        // ── PLC 状态 ────────────────────────────────
        private volatile bool _plcRunning = true;
        private string _plcTypeName = "Q02HCPU";

        private readonly object _memLock = new object();
        private readonly Dictionary<TcpClient, Thread> _clients = new Dictionary<TcpClient, Thread>();
        private readonly object _clientsLock = new object();

        public int Port { get; }
        public bool IsRunning => _running;

        public Mc3EVirtuServer(int port = 5007) { Port = port; }

        // ── 预设/读取数据（测试用）─────────────────

        public void SetDRegister(ushort address, ushort value) { lock (_memLock) _dRegisters[address] = value; }
        public ushort GetDRegister(ushort address) { lock (_memLock) return _dRegisters[address]; }

        public void SetDBytes(int address, byte[] data)
        {
            lock (_memLock)
            {
                for (int i = 0; i < data.Length / 2; i++)
                    _dRegisters[address + i] = (ushort)((data[i * 2] << 8) | data[i * 2 + 1]);
            }
        }
        public byte[] GetDBytes(int address, int wordCount)
        {
            byte[] result = new byte[wordCount * 2];
            lock (_memLock)
            {
                for (int i = 0; i < wordCount; i++)
                {
                    result[i * 2] = (byte)(_dRegisters[address + i] >> 8);
                    result[i * 2 + 1] = (byte)(_dRegisters[address + i] & 0xFF);
                }
            }
            return result;
        }

        public void SetMRelay(ushort address, bool value) { lock (_memLock) _mRelays[address] = value; }
        public bool GetMRelay(ushort address) { lock (_memLock) return _mRelays[address]; }

        public void SetXInput(ushort address, bool value) { lock (_memLock) _xInputs[address] = value; }
        public bool GetXInput(ushort address) { lock (_memLock) return _xInputs[address]; }

        public void SetYOutput(ushort address, bool value) { lock (_memLock) _yOutputs[address] = value; }
        public bool GetYOutput(ushort address) { lock (_memLock) return _yOutputs[address]; }

        public void SetBRelay(ushort address, bool value) { lock (_memLock) _bRelays[address] = value; }
        public bool GetBRelay(ushort address) { lock (_memLock) return _bRelays[address]; }

        public void SetLRelay(ushort address, bool value) { lock (_memLock) _lRelays[address] = value; }
        public void SetFState(ushort address, bool value) { lock (_memLock) _fStates[address] = value; }
        public void SetVEdge(ushort address, bool value) { lock (_memLock) _vEdges[address] = value; }
        public void SetSStep(ushort address, bool value) { lock (_memLock) _sSteps[address] = value; }

        public void SetWRegister(ushort address, ushort value) { lock (_memLock) _wRegisters[address] = value; }
        public void SetRRegister(ushort address, ushort value) { lock (_memLock) _rRegisters[address] = value; }
        public void SetZRRegister(ushort address, ushort value) { lock (_memLock) _zrRegisters[address] = value; }
        public void SetSDRegister(ushort address, ushort value) { lock (_memLock) _sdRegisters[address] = value; }
        public void SetSWRegister(ushort address, ushort value) { lock (_memLock) _swRegisters[address] = value; }

        /// <summary>设置模拟 PLC 型号名称。</summary>
        public void SetPlcTypeName(string name) { lock (_memLock) _plcTypeName = name; }
        /// <summary>获取 PLC 运行状态。</summary>
        public bool IsPlcRunning => _plcRunning;

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
            lock (_clientsLock)
            {
                foreach (var kv in _clients)
                {
                    try { kv.Key.Close(); } catch { }
                }
                _clients.Clear();
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    lock (_clientsLock) { _clients[client] = thread; }
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
                        // MC-3E Binary 请求帧: SubHeader(2) + NetworkNo(1) + PcNo(1) + DstStationNo(2) + WaitTime(2) + Command(2) + SubCommand(2) = 12 bytes header
                        byte[]? reqHeader = ReadExact(stream, 12);
                        if (reqHeader == null) break;

                        ushort command = (ushort)((reqHeader[8] << 8) | reqHeader[9]);
                        ushort subCommand = (ushort)((reqHeader[10] << 8) | reqHeader[11]);

                        // 读取数据部分
                        byte[]? data = ReadRequestData(stream, command, subCommand);
                        if (data == null && command != 0x0101 && command != 0x1617) break;

                        byte[]? responsePdu = ProcessCommand(command, subCommand, data ?? Array.Empty<byte>());
                        if (responsePdu == null) continue;

                        // 构建响应帧: SubHeader(2) + NetworkNo(1) + PcNo(1) + DstStation(2) + CompletionCode(2) + Data
                        byte[] response = new byte[9 + responsePdu.Length];
                        response[0] = 0xD0; response[1] = 0x00;  // SubHeader (响应)
                        response[2] = reqHeader[2];                // NetworkNo
                        response[3] = reqHeader[3];                // PcNo
                        response[4] = reqHeader[4];                // DstStation (回传)
                        response[5] = reqHeader[5];
                        response[6] = 0x00;                       // Reserved
                        response[7] = 0x00; response[8] = 0x00;   // CompletionCode at offset 7-8 (成功)
                        Buffer.BlockCopy(responsePdu, 0, response, 9, responsePdu.Length);

                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
            finally
            {
                lock (_clientsLock) { _clients.Remove(client); }
            }
        }

        /// <summary>根据指令码和子命令读取请求数据部分。</summary>
        private byte[]? ReadRequestData(NetworkStream stream, ushort command, ushort subCommand)
        {
            // ── 随机读/写: 先读 count(2), 再读 items ──
            if (command == 0x0403 || command == 0x1402)
            {
                byte[]? countData = ReadExact(stream, 2);
                if (countData == null) return null;
                ushort itemCount = (ushort)(countData[0] | (countData[1] << 8));

                int itemSize;
                if (command == 0x0403 && subCommand == 0x0002)
                    itemSize = 6;  // 多长度随机读: SubLabel(1) + Address(3) + Length(2)
                else if (command == 0x0403)
                    itemSize = 4;  // 随机读: SubLabel(1) + Address(3)
                else
                    itemSize = 6;  // 随机写: SubLabel(1) + Address(3) + Data(2)

                byte[]? items = ReadExact(stream, itemCount * itemSize);
                if (items == null) return null;
                byte[] data = new byte[2 + items.Length];
                Buffer.BlockCopy(countData, 0, data, 0, 2);
                Buffer.BlockCopy(items, 0, data, 2, items.Length);
                return data;
            }

            // ── PLC 控制指令: 固定数据或无数据 ──
            if (command == 0x0101 || command == 0x1617)
                return Array.Empty<byte>(); // ReadPlcType / ErrorStateReset: 无数据

            if (command == 0x1001) return ReadExact(stream, 4);  // RemoteRun: 4 bytes
            if (command == 0x1002) return ReadExact(stream, 2);  // RemoteStop: 2 bytes
            if (command == 0x1006) return ReadExact(stream, 2);  // RemoteReset: 2 bytes

            // ── 批量读/写 ──
            int headerLen = GetDataHeaderLength(command);
            if (headerLen > 0)
            {
                byte[]? data = ReadExact(stream, headerLen);
                if (data == null) return null;

                // 批量写需要额外读取写入数据
                if (command == 0x1401 && data.Length >= 6)
                {
                    ushort writeCount = (ushort)(data[4] | (data[5] << 8));
                    // 位模式: 1字节/位, 字模式: 2字节/字
                    int extraLen = subCommand == 0x0001 ? writeCount : writeCount * 2;
                    if (extraLen > 0)
                    {
                        byte[]? extra = ReadExact(stream, extraLen);
                        if (extra != null)
                        {
                            byte[] combined = new byte[data.Length + extra.Length];
                            Buffer.BlockCopy(data, 0, combined, 0, data.Length);
                            Buffer.BlockCopy(extra, 0, combined, data.Length, extra.Length);
                            return combined;
                        }
                    }
                }
                return data;
            }

            return Array.Empty<byte>();
        }

        /// <summary>根据指令推算固定数据部分长度（不含动态写入数据）。</summary>
        private int GetDataHeaderLength(ushort command)
        {
            // 批量读/写: SubLabel(1) + Address(3) + Count(2) = 6
            if (command == 0x0401) return 6;
            if (command == 0x1401) return 6;
            return 0;
        }

        private byte[]? ProcessCommand(ushort command, ushort subCommand, byte[]? data)
        {
            try
            {
                return command switch
                {
                    0x0401 => subCommand == 0x0001 ? ProcessBatchReadBit(data) : ProcessBatchRead(data),
                    0x1401 => subCommand == 0x0001 ? ProcessBatchWriteBit(data) : ProcessBatchWrite(data),
                    0x0403 => subCommand == 0x0002 ? ProcessRandomReadMultiLength(data) : ProcessRandomRead(data),
                    0x1402 => ProcessRandomWrite(data),
                    0x0101 => ProcessReadPlcType(),
                    0x1001 => ProcessRemoteRun(data),
                    0x1002 => ProcessRemoteStop(data),
                    0x1006 => ProcessRemoteReset(data),
                    0x1617 => ProcessErrorStateReset(),
                    _ => BuildError(0xC001) // 无法识别的指令
                };
            }
            catch { return BuildError(0xD003); }
        }

        // ── 批量读字 (0x0401, Sub=0x0000) ─────────────

        private byte[] ProcessBatchRead(byte[]? data)
        {
            if (data == null || data.Length < 6) return BuildError(0xC002);
            byte subLabel = data[0];
            uint startAddr = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            ushort[]? regStore = GetWordRegisterStore(subLabel);
            if (regStore == null) return BuildError(0xC002);

            byte[] result = new byte[count * 2];
            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int idx = (int)(startAddr + i);
                    if (idx >= 0 && idx < regStore.Length)
                    {
                        ushort val = regStore[idx];
                        result[i * 2] = (byte)(val >> 8);      // Big-endian
                        result[i * 2 + 1] = (byte)(val & 0xFF);
                    }
                }
            }
            return result;
        }

        // ── 批量写字 (0x1401, Sub=0x0000) ─────────────

        private byte[] ProcessBatchWrite(byte[]? data)
        {
            if (data == null || data.Length < 6) return BuildError(0xC002);
            byte subLabel = data[0];
            uint startAddr = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            ushort[]? regStore = GetWordRegisterStore(subLabel);
            if (regStore == null) return BuildError(0xC002);

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int dataOffset = 6 + i * 2;
                    if (dataOffset + 1 >= data.Length) break;
                    int idx = (int)(startAddr + i);
                    if (idx >= 0 && idx < regStore.Length)
                    {
                        regStore[idx] = (ushort)((data[dataOffset] << 8) | data[dataOffset + 1]);
                    }
                }
            }
            return Array.Empty<byte>(); // 写入成功，无数据返回
        }

        // ── 批量读位 (0x0401, Sub=0x0001) ─────────────

        private byte[] ProcessBatchReadBit(byte[]? data)
        {
            if (data == null || data.Length < 6) return BuildError(0xC002);
            byte subLabel = data[0];
            uint startAddr = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            bool[]? bitStore = GetBitRegisterStore(subLabel);
            if (bitStore == null) return BuildError(0xC002);

            byte[] result = new byte[count];
            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int idx = (int)(startAddr + i);
                    if (idx >= 0 && idx < bitStore.Length)
                        result[i] = (byte)(bitStore[idx] ? 0x01 : 0x00);
                }
            }
            return result;
        }

        // ── 批量写位 (0x1401, Sub=0x0001) ─────────────

        private byte[] ProcessBatchWriteBit(byte[]? data)
        {
            if (data == null || data.Length < 6) return BuildError(0xC002);
            byte subLabel = data[0];
            uint startAddr = (uint)(data[1] | (data[2] << 8) | (data[3] << 16));
            ushort count = (ushort)(data[4] | (data[5] << 8));

            bool[]? bitStore = GetBitRegisterStore(subLabel);
            if (bitStore == null) return BuildError(0xC002);

            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int dataOffset = 6 + i;
                    if (dataOffset >= data.Length) break;
                    int idx = (int)(startAddr + i);
                    if (idx >= 0 && idx < bitStore.Length)
                        bitStore[idx] = data[dataOffset] != 0;
                }
            }
            return Array.Empty<byte>();
        }

        // ── 随机读字 (0x0403, Sub=0x0000) ─────────────

        private byte[] ProcessRandomRead(byte[]? data)
        {
            if (data == null || data.Length < 2) return BuildError(0xC002);
            ushort count = (ushort)(data[0] | (data[1] << 8));
            byte[] result = new byte[count * 2];
            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = 2 + i * 4;
                    if (offset + 3 >= data.Length) break;
                    byte subLabel = data[offset];
                    uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                    ushort[]? regStore = GetWordRegisterStore(subLabel);
                    if (regStore != null && addr < regStore.Length)
                    {
                        ushort val = regStore[addr];
                        result[i * 2] = (byte)(val >> 8);
                        result[i * 2 + 1] = (byte)(val & 0xFF);
                    }
                }
            }
            return result;
        }

        // ── 随机写字 (0x1402, Sub=0x0000) ─────────────

        private byte[] ProcessRandomWrite(byte[]? data)
        {
            if (data == null || data.Length < 2) return BuildError(0xC002);
            ushort count = (ushort)(data[0] | (data[1] << 8));
            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = 2 + i * 6;
                    if (offset + 5 >= data.Length) break;
                    byte subLabel = data[offset];
                    uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                    ushort value = (ushort)((data[offset + 4] << 8) | data[offset + 5]);
                    ushort[]? regStore = GetWordRegisterStore(subLabel);
                    if (regStore != null && addr < regStore.Length)
                    {
                        regStore[addr] = value;
                    }
                }
            }
            return Array.Empty<byte>();
        }

        // ── 多长度随机读 (0x0403, Sub=0x0002) ──────────

        private byte[] ProcessRandomReadMultiLength(byte[]? data)
        {
            if (data == null || data.Length < 2) return BuildError(0xC002);
            ushort count = (ushort)(data[0] | (data[1] << 8));

            // 先计算总返回数据长度
            int totalWords = 0;
            for (int i = 0; i < count; i++)
            {
                int off = 2 + i * 6;
                if (off + 5 >= data.Length) break;
                ushort len = (ushort)(data[off + 4] | (data[off + 5] << 8));
                totalWords += len;
            }

            byte[] result = new byte[totalWords * 2];
            int resultOffset = 0;
            lock (_memLock)
            {
                for (int i = 0; i < count; i++)
                {
                    int offset = 2 + i * 6;
                    if (offset + 5 >= data.Length) break;
                    byte subLabel = data[offset];
                    uint addr = (uint)(data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16));
                    ushort len = (ushort)(data[offset + 4] | (data[offset + 5] << 8));

                    ushort[]? regStore = GetWordRegisterStore(subLabel);
                    for (int w = 0; w < len; w++)
                    {
                        if (regStore != null && addr + w < regStore.Length)
                        {
                            ushort val = regStore[addr + w];
                            result[resultOffset++] = (byte)(val >> 8);
                            result[resultOffset++] = (byte)(val & 0xFF);
                        }
                        else
                        {
                            resultOffset += 2;
                        }
                    }
                }
            }
            return result;
        }

        // ── PLC 控制指令 ──────────────────────────────

        private byte[] ProcessReadPlcType()
        {
            // 返回 16 字节 ASCII 型号名称
            lock (_memLock)
            {
                byte[] result = new byte[16];
                byte[] nameBytes = Encoding.ASCII.GetBytes(_plcTypeName);
                Buffer.BlockCopy(nameBytes, 0, result, 0, Math.Min(nameBytes.Length, 16));
                return result;
            }
        }

        private byte[] ProcessRemoteRun(byte[]? data)
        {
            // 数据格式: Condition(1) + Reserved(3) 或 Condition(1) + Reserved(1)
            if (data != null && data.Length >= 1)
                _plcRunning = true;
            return Array.Empty<byte>();
        }

        private byte[] ProcessRemoteStop(byte[]? data)
        {
            _plcRunning = false;
            return Array.Empty<byte>();
        }

        private byte[] ProcessRemoteReset(byte[]? data)
        {
            // 复位后回到运行状态
            _plcRunning = true;
            return Array.Empty<byte>();
        }

        private byte[] ProcessErrorStateReset()
        {
            // 错误状态复位，本模拟器无实际错误状态，直接返回成功
            return Array.Empty<byte>();
        }

        // ── 寄存器存储映射 ────────────────────────────

        /// <summary>获取字寄存器存储区 (ushort[])。</summary>
        private ushort[]? GetWordRegisterStore(byte subLabel)
        {
            return subLabel switch
            {
                0xA8 => _dRegisters,   // D
                0xB4 => _wRegisters,   // W
                0xAF => _rRegisters,   // R
                0xCC => _zIndex,       // Z
                0xB0 => _zrRegisters,  // ZR
                0xA9 => _sdRegisters,  // SD
                0xB5 => _swRegisters,  // SW
                _ => null
            };
        }

        /// <summary>获取位寄存器存储区 (bool[])。</summary>
        private bool[]? GetBitRegisterStore(byte subLabel)
        {
            return subLabel switch
            {
                0x90 => _mRelays,   // M
                0x9C => _xInputs,   // X
                0x9D => _yOutputs,  // Y
                0xA0 => _bRelays,   // B
                0x92 => _lRelays,   // L
                0x93 => _fStates,   // F
                0x94 => _vEdges,    // V
                0x98 => _sSteps,    // S
                _ => null
            };
        }

        private byte[] BuildError(ushort code) => new byte[] { (byte)(code >> 8), (byte)(code & 0xFF) };

        private static byte[]? ReadExact(NetworkStream stream, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        public void Dispose() => Stop();
    }
}
