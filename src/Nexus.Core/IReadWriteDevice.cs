using System;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 统一读写设备接口 — 所有 PLC/设备通讯客户端实现此接口。
    /// 上层只需关注地址和数据类型，无需关心底层协议差异。
    /// </summary>
    public interface IReadWriteDevice : IDisposable
    {
        bool IsConnected { get; }

        // ── 连接管理 ──────────────────────────────
        OperateResult Connect();
        Task<OperateResult> ConnectAsync();
        void Disconnect();

        // ── 读取 ──────────────────────────────────
        OperateResult<bool>   ReadBool(string address);
        OperateResult<short>  ReadInt16(string address);
        OperateResult<ushort> ReadUInt16(string address);
        OperateResult<int>    ReadInt32(string address);
        OperateResult<uint>   ReadUInt32(string address);
        OperateResult<long>   ReadInt64(string address);
        OperateResult<ulong>  ReadUInt64(string address);
        OperateResult<float>  ReadFloat(string address);
        OperateResult<double> ReadDouble(string address);
        OperateResult<string> ReadString(string address, ushort length);
        OperateResult<byte[]> ReadBytes(string address, ushort length);

        // ── 写入 ──────────────────────────────────
        OperateResult Write(string address, bool value);
        OperateResult Write(string address, short value);
        OperateResult Write(string address, ushort value);
        OperateResult Write(string address, int value);
        OperateResult Write(string address, uint value);
        OperateResult Write(string address, long value);
        OperateResult Write(string address, ulong value);
        OperateResult Write(string address, float value);
        OperateResult Write(string address, double value);
        OperateResult Write(string address, string value);
        OperateResult Write(string address, byte[] data);

        // ── Async 读取 ────────────────────────────
        Task<OperateResult<bool>>   ReadBoolAsync(string address);
        Task<OperateResult<short>>  ReadInt16Async(string address);
        Task<OperateResult<ushort>> ReadUInt16Async(string address);
        Task<OperateResult<int>>    ReadInt32Async(string address);
        Task<OperateResult<uint>>   ReadUInt32Async(string address);
        Task<OperateResult<long>>   ReadInt64Async(string address);
        Task<OperateResult<ulong>>  ReadUInt64Async(string address);
        Task<OperateResult<float>>  ReadFloatAsync(string address);
        Task<OperateResult<double>> ReadDoubleAsync(string address);
        Task<OperateResult<string>> ReadStringAsync(string address, ushort length);
        Task<OperateResult<byte[]>> ReadBytesAsync(string address, ushort length);

        // ── Async 写入 ────────────────────────────
        Task<OperateResult> WriteAsync(string address, bool value);
        Task<OperateResult> WriteAsync(string address, short value);
        Task<OperateResult> WriteAsync(string address, int value);
        Task<OperateResult> WriteAsync(string address, float value);
        Task<OperateResult> WriteAsync(string address, string value);
        Task<OperateResult> WriteAsync(string address, byte[] data);
    }
}
