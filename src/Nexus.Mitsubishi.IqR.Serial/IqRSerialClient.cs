using Nexus;

namespace Nexus.Mitsubishi.IqR.Serial
{
    /// <summary>
    /// 三菱 MELSEC iQ-R 系列 RS-232 串口客户端 — <b>当前为 stub</b>。
    /// </summary>
    /// <remarks>
    /// <b>状态</b>:Phase C 待深化(见 <c>docs/PHASE_C_ROADMAP.md</c>)。
    /// <para><b>临时替代</b>:iQ-R 系列同时支持 MC3E 二进制协议,推荐使用
    /// <c>Nexus.Mitsubishi</c> 的 TCP 客户端,或在串口场景下使用
    /// <c>Nexus.Mitsubishi.MelsecA3CNet</c>(已实现)。</para>
    /// <para><b>深化计划</b>:iQ-R 串口协议 ≈ MC3E binary 帧封装到 RS-232 链路,
    /// 可复用现有 MC3E 帧构造器。预计 2-3 天。</para>
    /// </remarks>
    public class IqRSerialClient : SerialDeviceBase, IBatchReadWrite
    {
        public IqRSerialClient(ISerialPort port, int timeout = 5000) : base(port, timeout) { }
        protected override int ResponseHeaderLength => 9;
        protected override int GetResponsePayloadLength(byte[] header) => 0;

        public override OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<short> ReadInt16(string address) => OperateResult<short>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Failed("iQ-R 串口协议暂不支持直接读取，请使用 TCP 客户端");

        public override OperateResult Write(string address, bool value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, short value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, ushort value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, int value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, uint value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, long value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, ulong value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, float value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, double value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, string value) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");
        public override OperateResult Write(string address, byte[] data) => OperateResult.Failed("iQ-R 串口协议暂不支持直接写入，请使用 TCP 客户端");

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

        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, object?>>.Failed("iQ-R 串口协议暂不支持批量读取");
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, byte[]>>.Failed("iQ-R 串口协议暂不支持随机读取");
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items) => OperateResult.Failed("iQ-R 串口协议暂不支持批量写入");
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
