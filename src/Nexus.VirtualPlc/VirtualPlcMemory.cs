using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nexus.VirtualPlc
{
    /// <summary>
    /// 统一虚拟 PLC 内存模型 — 提供跨协议共享的地址空间。
    /// <para>支持 Bool (线圈)、Int16 (寄存器) 两种基础区域，</para>
    /// <para>可通过 <see cref="GetFloat"/>/<see cref="GetDouble"/> 等方法读取多字组合值。</para>
    /// <para>线程安全，所有操作使用锁或 ConcurrentDictionary。</para>
    /// </summary>
    public class VirtualPlcMemory : IDisposable
    {
        private readonly ConcurrentDictionary<int, bool> _coils = new();
        private readonly ConcurrentDictionary<int, short> _registers = new();
        private readonly object _multiWordLock = new object();
        private int _disposed;

        /// <summary>内存写入事件。</summary>
        public event EventHandler<VirtualPlcWriteEventArgs>? OnWrite;

        // ── Bool (线圈) 操作 ────────────────────────

        /// <summary>读取线圈。</summary>
        public bool GetBool(int address)
        {
            return _coils.TryGetValue(address, out var value) && value;
        }

        /// <summary>写入线圈。</summary>
        public void SetBool(int address, bool value)
        {
            _coils[address] = value;
            FireWrite(address, value ? 1 : 0, VirtualPlcDataType.Bool);
        }

        // ── Int16 (寄存器) 操作 ─────────────────────

        /// <summary>读取 Int16 寄存器。</summary>
        public short GetInt16(int address)
        {
            return _registers.TryGetValue(address, out var value) ? value : (short)0;
        }

        /// <summary>写入 Int16 寄存器。</summary>
        public void SetInt16(int address, short value)
        {
            _registers[address] = value;
            FireWrite(address, value, VirtualPlcDataType.Int16);
        }

        // ── UInt16 操作 ────────────────────────────

        /// <summary>读取 UInt16 寄存器。</summary>
        public ushort GetUInt16(int address)
        {
            return (ushort)GetInt16(address);
        }

        /// <summary>写入 UInt16 寄存器。</summary>
        public void SetUInt16(int address, ushort value)
        {
            SetInt16(address, (short)value);
        }

        // ── Int32 操作（2 个寄存器）───────────────────

        /// <summary>读取 Int32（地址 + 地址+1 组合，高字在前）。</summary>
        public int GetInt32(int address)
        {
            lock (_multiWordLock)
            {
                int hi = (ushort)GetInt16(address);
                int lo = (ushort)GetInt16(address + 1);
                return (hi << 16) | lo;
            }
        }

        /// <summary>写入 Int32。</summary>
        public void SetInt32(int address, int value)
        {
            lock (_multiWordLock)
            {
                SetInt16(address, (short)(value >> 16));
                SetInt16(address + 1, (short)(value & 0xFFFF));
            }
        }

        // ── Float 操作（2 个寄存器）──────────────────

        /// <summary>读取 Float（IEEE 754，2 个寄存器）。</summary>
        public float GetFloat(int address)
        {
            int raw = GetInt32(address);
            unsafe { return *(float*)&raw; }
        }

        /// <summary>写入 Float。</summary>
        public void SetFloat(int address, float value)
        {
            int raw;
            unsafe { raw = *(int*)&value; }
            SetInt32(address, raw);
        }

        // ── Double 操作（4 个寄存器）─────────────────

        /// <summary>读取 Double（IEEE 754，4 个寄存器）。</summary>
        public double GetDouble(int address)
        {
            long raw = GetInt64(address);
            unsafe { return *(double*)&raw; }
        }

        /// <summary>写入 Double。</summary>
        public void SetDouble(int address, double value)
        {
            long raw;
            unsafe { raw = *(long*)&value; }
            SetInt64(address, raw);
        }

        // ── Int64 操作（4 个寄存器）───────────────────

        /// <summary>读取 Int64（4 个寄存器，高字在前）。</summary>
        public long GetInt64(int address)
        {
            lock (_multiWordLock)
            {
                long v = 0;
                for (int i = 0; i < 4; i++)
                    v = (v << 16) | (ushort)GetInt16(address + i);
                return v;
            }
        }

        /// <summary>写入 Int64。</summary>
        public void SetInt64(int address, long value)
        {
            lock (_multiWordLock)
            {
                SetInt16(address, (short)(value >> 48));
                SetInt16(address + 1, (short)(value >> 32));
                SetInt16(address + 2, (short)(value >> 16));
                SetInt16(address + 3, (short)(value & 0xFFFF));
            }
        }

        // ── 批量操作 ──────────────────────────────

        /// <summary>批量读取 Bool。</summary>
        public bool[] GetBools(int startAddress, int count)
        {
            var result = new bool[count];
            for (int i = 0; i < count; i++)
                result[i] = GetBool(startAddress + i);
            return result;
        }

        /// <summary>批量写入 Bool。</summary>
        public void SetBools(int startAddress, bool[] values)
        {
            for (int i = 0; i < values.Length; i++)
                SetBool(startAddress + i, values[i]);
        }

        /// <summary>批量读取 Int16。</summary>
        public short[] GetInt16s(int startAddress, int count)
        {
            var result = new short[count];
            for (int i = 0; i < count; i++)
                result[i] = GetInt16(startAddress + i);
            return result;
        }

        /// <summary>批量写入 Int16。</summary>
        public void SetInt16s(int startAddress, short[] values)
        {
            for (int i = 0; i < values.Length; i++)
                SetInt16(startAddress + i, values[i]);
        }

        // ── 字节数组操作 ──────────────────────────

        /// <summary>读取寄存器为字节数组（每个寄存器 2 字节，大端序）。</summary>
        public byte[] GetBytes(int startAddress, int registerCount)
        {
            var bytes = new byte[registerCount * 2];
            for (int i = 0; i < registerCount; i++)
            {
                short reg = GetInt16(startAddress + i);
                bytes[i * 2] = (byte)(reg >> 8);
                bytes[i * 2 + 1] = (byte)(reg & 0xFF);
            }
            return bytes;
        }

        /// <summary>写入字节数组到寄存器（大端序）。</summary>
        public void SetBytes(int startAddress, byte[] data)
        {
            int count = data.Length / 2;
            for (int i = 0; i < count; i++)
            {
                short value = (short)((data[i * 2] << 8) | data[i * 2 + 1]);
                SetInt16(startAddress + i, value);
            }
        }

        // ── S7 风格内存区域 ────────────────────────

        private readonly ConcurrentDictionary<long, byte> _dbBytes = new();

        /// <summary>设置 S7 DB 区域字节值。</summary>
        public void SetDbValue(int dbNumber, int offset, byte[] value)
        {
            for (int i = 0; i < value.Length; i++)
                _dbBytes[((long)dbNumber << 32) | (uint)(offset + i)] = value[i];
        }

        /// <summary>获取 S7 DB 区域字节值。</summary>
        public byte[] GetDbValue(int dbNumber, int offset, int length)
        {
            var result = new byte[length];
            for (int i = 0; i < length; i++)
                _dbBytes.TryGetValue(((long)dbNumber << 32) | (uint)(offset + i), out result[i]);
            return result;
        }

        /// <summary>设置 S7 Merker（标志位）区域。</summary>
        public void SetMerker(int offset, byte[] value)
        {
            SetDbValue(-1, offset, value);
        }

        /// <summary>设置 S7 输入区域。</summary>
        public void SetInput(int offset, byte[] value)
        {
            SetDbValue(-2, offset, value);
        }

        /// <summary>设置 S7 输出区域。</summary>
        public void SetOutput(int offset, byte[] value)
        {
            SetDbValue(-3, offset, value);
        }

        // ── Modbus 风格内存区域 ─────────────────────

        private readonly ConcurrentDictionary<int, ushort> _holdingRegisters = new();
        private readonly ConcurrentDictionary<int, bool> _modbusCoils = new();
        private readonly ConcurrentDictionary<int, ushort> _inputRegisters = new();
        private readonly ConcurrentDictionary<int, bool> _discreteInputs = new();

        /// <summary>设置 Modbus 保持寄存器。</summary>
        public void SetHoldingRegister(ushort address, ushort value)
        {
            _holdingRegisters[address] = value;
        }

        /// <summary>获取 Modbus 保持寄存器。</summary>
        public ushort GetHoldingRegister(ushort address)
        {
            return _holdingRegisters.TryGetValue(address, out var value) ? value : (ushort)0;
        }

        /// <summary>设置 Modbus 线圈。</summary>
        public void SetCoil(ushort address, bool value)
        {
            _modbusCoils[address] = value;
        }

        /// <summary>获取 Modbus 线圈。</summary>
        public bool GetCoil(ushort address)
        {
            return _modbusCoils.TryGetValue(address, out var value) && value;
        }

        /// <summary>设置 Modbus 输入寄存器。</summary>
        public void SetInputRegister(ushort address, ushort value)
        {
            _inputRegisters[address] = value;
        }

        /// <summary>获取 Modbus 输入寄存器。</summary>
        public ushort GetInputRegister(ushort address)
        {
            return _inputRegisters.TryGetValue(address, out var value) ? value : (ushort)0;
        }

        /// <summary>设置 Modbus 离散输入。</summary>
        public void SetDiscreteInput(ushort address, bool value)
        {
            _discreteInputs[address] = value;
        }

        /// <summary>获取 Modbus 离散输入。</summary>
        public bool GetDiscreteInput(ushort address)
        {
            return _discreteInputs.TryGetValue(address, out var value) && value;
        }

        // ── 清除 ─────────────────────────────────

        /// <summary>清除所有内存。</summary>
        public void Clear()
        {
            _coils.Clear();
            _registers.Clear();
            _dbBytes.Clear();
            _holdingRegisters.Clear();
            _modbusCoils.Clear();
            _inputRegisters.Clear();
            _discreteInputs.Clear();
        }

        /// <summary>清除指定范围的寄存器。</summary>
        public void ClearRegisters(int startAddress, int count)
        {
            for (int i = 0; i < count; i++)
                _registers.TryRemove(startAddress + i, out _);
        }

        /// <summary>清除指定范围的线圈。</summary>
        public void ClearCoils(int startAddress, int count)
        {
            for (int i = 0; i < count; i++)
                _coils.TryRemove(startAddress + i, out _);
        }

        // ── 内部方法 ─────────────────────────────

        private void FireWrite(int address, int value, VirtualPlcDataType dataType)
        {
            OnWrite?.Invoke(this, new VirtualPlcWriteEventArgs(address, value, dataType));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            Clear();
        }
    }

    /// <summary>虚拟 PLC 数据类型。</summary>
    public enum VirtualPlcDataType
    {
        /// <summary>线圈/位。</summary>
        Bool = 0,
        /// <summary>16位整数。</summary>
        Int16 = 1,
        /// <summary>32位整数。</summary>
        Int32 = 2,
        /// <summary>浮点数。</summary>
        Float = 3,
        /// <summary>双精度。</summary>
        Double = 4,
        /// <summary>64位整数。</summary>
        Int64 = 5
    }

    /// <summary>内存写入事件参数。</summary>
    public class VirtualPlcWriteEventArgs : EventArgs
    {
        /// <summary>写入地址。</summary>
        public int Address { get; }

        /// <summary>写入值（整型表示）。</summary>
        public int Value { get; }

        /// <summary>数据类型。</summary>
        public VirtualPlcDataType DataType { get; }

        /// <summary>写入时间戳。</summary>
        public DateTime Timestamp { get; }

        public VirtualPlcWriteEventArgs(int address, int value, VirtualPlcDataType dataType)
        {
            Address = address;
            Value = value;
            DataType = dataType;
            Timestamp = DateTime.Now;
        }
    }
}
