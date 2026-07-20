using Nexus;

namespace Nexus.Xinje.Serial
{
    /// <summary>
    /// 信捷 XC/XD 系列 PLC 串口协议客户端 — <b>当前为 stub</b>(无任何协议逻辑)。
    /// </summary>
    /// <remarks>
    /// <b>状态</b>:Phase C 待深化(见 <c>docs/PHASE_C_ROADMAP.md</c>)。本类既无父协议继承,
    /// 也无自己的帧逻辑,是 5 个 Priority-1 stub 中最严重的"占位"。
    /// <para><b>临时替代</b>:信捷 XC/XD 系列同时支持 Modbus RTU,推荐使用
    /// <c>Nexus.Modbus.ModbusRtuClient</c>;若用 TCP 信捷客户端用 <c>Nexus.Xinje</c>。</para>
    /// <para><b>深化计划</b>:信捷 XC/XD 专有串口协议见信捷手册。预计 3-5 天。</para>
    /// </remarks>
    public class XinjeSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        public XinjeSerialClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header) { if (header.Length < 4) return 0; return (header[2] << 8) | header[3]; }

        public override OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Failed("信捷串口协议暂不支持，请使用 TCP 客户端 (XinjeTcpClient)");
        public override OperateResult<short> ReadInt16(string address) => OperateResult<short>.Failed("信捷串口协议暂不支持，请使用 TCP 客户端 (XinjeTcpClient)");
        public override OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("信捷串口协议暂不支持");
        public override OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("信捷串口协议暂不支持");
        public override OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("信捷串口协议暂不支持");
        public override OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("信捷串口协议暂不支持");
        public override OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("信捷串口协议暂不支持");
        public override OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("信捷串口协议暂不支持");
        public override OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("信捷串口协议暂不支持");
        public override OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("信捷串口协议暂不支持");
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, bool value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, short value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, ushort value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, int value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, uint value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, long value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, ulong value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, float value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, double value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, string value) => OperateResult.Failed("信捷串口协议暂不支持");
        public override OperateResult Write(string address, byte[] data) => OperateResult.Failed("信捷串口协议暂不支持");
        public override Task<OperateResult<bool>> ReadBoolAsync(string address) => Task.Run(() => ReadBool(address));
        public override Task<OperateResult<short>> ReadInt16Async(string address) => Task.Run(() => ReadInt16(address));
        public override Task<OperateResult<ushort>> ReadUInt16Async(string address) => Task.Run(() => ReadUInt16(address));
        public override Task<OperateResult<int>> ReadInt32Async(string address) => Task.Run(() => ReadInt32(address));
        public override Task<OperateResult<uint>> ReadUInt32Async(string address) => Task.Run(() => ReadUInt32(address));
        public override Task<OperateResult<long>> ReadInt64Async(string address) => Task.Run(() => ReadInt64(address));
        public override Task<OperateResult<ulong>> ReadUInt64Async(string address) => Task.Run(() => ReadUInt64(address));
        public override Task<OperateResult<float>> ReadFloatAsync(string address) => Task.Run(() => ReadFloat(address));
        public override Task<OperateResult<double>> ReadDoubleAsync(string address) => Task.Run(() => ReadDouble(address));
        public override Task<OperateResult<string>> ReadStringAsync(string address, ushort length) => Task.Run(() => ReadString(address, length));
        public override Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length) => Task.Run(() => ReadBytes(address, length));
        public override Task<OperateResult> WriteAsync(string address, bool value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, short value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ushort value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, int value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, uint value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, long value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, ulong value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, float value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, double value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, string value) => Task.Run(() => Write(address, value));
        public override Task<OperateResult> WriteAsync(string address, byte[] data) => Task.Run(() => Write(address, data));
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, object?>>.Failed("信捷串口协议暂不支持");
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, byte[]>>.Failed("信捷串口协议暂不支持");
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items) => OperateResult.Failed("信捷串口协议暂不支持");
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
