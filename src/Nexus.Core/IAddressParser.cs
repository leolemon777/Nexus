using System;

namespace Nexus
{
    /// <summary>
    /// 通用地址解析接口 — 把用户地址字符串解析为协议内部地址结构。
    /// 每种协议实现自己的 Address 类型（如 ModbusAddress、S7Address）。
    /// </summary>
    public interface IDataAddress
    {
        /// <summary>用户输入的原始地址字符串。</summary>
        string Original { get; }
    }

    /// <summary>
    /// 地址解析器 — 将字符串地址转为强类型地址对象。
    /// </summary>
    public interface IAddressParser<TAddress> where TAddress : IDataAddress
    {
        /// <summary>解析地址字符串为协议内部地址。</summary>
        /// <exception cref="AddressParseException">地址格式无效时抛出。</exception>
        TAddress Parse(string address);

        /// <summary>尝试解析，不抛异常。</summary>
        bool TryParse(string address, out TAddress? parsed);
    }

    /// <summary>地址解析异常。</summary>
    public sealed class AddressParseException : Exception
    {
        public string Address { get; }

        public AddressParseException(string address, string message)
            : base($"Invalid address '{address}': {message}")
        {
            Address = address;
        }
    }
}
