using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 批量读写接口 — 一次请求读取/写入多个地址。
    /// 比逐个调用更高效，减少网络往返。
    /// </summary>
    public interface IBatchReadWrite : IReadWriteDevice
    {
        /// <summary>批量读取多个地址的值（返回地址→值的字典）。</summary>
        OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses);

        /// <summary>批量读取多个地址的值（异步）。</summary>
        Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default);

        /// <summary>随机读取多个不连续地址（单次网络请求，协议支持时使用 FC23 或多次读取组合）。</summary>
        OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses);

        /// <summary>随机读取多个不连续地址（异步）。</summary>
        Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default);

        /// <summary>批量写入多个地址的值。</summary>
        OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items);

        /// <summary>批量写入多个地址的值（异步）。</summary>
        Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default);
    }
}
