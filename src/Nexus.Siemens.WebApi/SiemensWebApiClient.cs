using Nexus;

namespace Nexus.Siemens.WebApi
{
    public class SiemensWebApiClient : TcpDeviceBase, IBatchReadWrite
    {
        public SiemensWebApiClient(string ip, int port = 443, int timeout = 5000) : base(ip, port, timeout) { }
        protected override int ResponseHeaderLength => 4;
        protected override int GetResponsePayloadLength(byte[] header) { if (header.Length < 4) return 0; return (header[2] << 8) | header[3]; }
        public override OperateResult<bool> ReadBool(string address) => OperateResult<bool>.Failed("Siemens Web API 暂不支持，请使用 S7 客户端");
        public override OperateResult<short> ReadInt16(string address) => OperateResult<short>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<ushort> ReadUInt16(string address) => OperateResult<ushort>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<int> ReadInt32(string address) => OperateResult<int>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<uint> ReadUInt32(string address) => OperateResult<uint>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<long> ReadInt64(string address) => OperateResult<long>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<ulong> ReadUInt64(string address) => OperateResult<ulong>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<float> ReadFloat(string address) => OperateResult<float>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<double> ReadDouble(string address) => OperateResult<double>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<string> ReadString(string address, ushort length) => OperateResult<string>.Failed("Siemens Web API 暂不支持");
        public override OperateResult<byte[]> ReadBytes(string address, ushort length) => OperateResult<byte[]>.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, bool value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, short value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, ushort value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, int value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, uint value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, long value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, ulong value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, float value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, double value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, string value) => OperateResult.Failed("Siemens Web API 暂不支持");
        public override OperateResult Write(string address, byte[] data) => OperateResult.Failed("Siemens Web API 暂不支持");
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
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, object?>>.Failed("Siemens Web API 暂不支持");
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(BatchRead(addresses));
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses) => OperateResult<Dictionary<string, byte[]>>.Failed("Siemens Web API 暂不支持");
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(IEnumerable<string> addresses, CancellationToken ct = default) => Task.FromResult(RandomRead(addresses));
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items) => OperateResult.Failed("Siemens Web API 暂不支持");
        public Task<OperateResult> BatchWriteAsync(IEnumerable<KeyValuePair<string, object>> items, CancellationToken ct = default) => Task.FromResult(BatchWrite(items));
    }
}
