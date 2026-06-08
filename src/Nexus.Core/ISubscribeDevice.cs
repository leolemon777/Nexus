using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus
{
    /// <summary>
    /// 订阅式设备接口 — 数据变化时主动通知，而非轮询。
    /// 用于实时监控、数据采集等场景。
    /// </summary>
    public interface ISubscribeDevice : IReadWriteDevice
    {
        /// <summary>订阅指定地址的数据变化。</summary>
        /// <param name="address">设备地址</param>
        /// <param name="intervalMs">轮询间隔（毫秒）</param>
        /// <param name="dataType">数据类型名称（如 "Int16", "Float", "Bool"）</param>
        void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16");

        /// <summary>取消订阅。</summary>
        void Unsubscribe(string address);

        /// <summary>启动所有订阅的轮询。</summary>
        void StartSubscriptions(int globalIntervalMs = 500);

        /// <summary>停止所有订阅。</summary>
        void StopSubscriptions();

        /// <summary>数据变化事件。</summary>
        event EventHandler<DataChangeEventArgs>? OnDataChanged;
    }

    /// <summary>数据变化事件参数。</summary>
    public class DataChangeEventArgs : EventArgs
    {
        /// <summary>变化地址。</summary>
        public string Address { get; set; } = "";

        /// <summary>旧值。</summary>
        public object? OldValue { get; set; }

        /// <summary>新值。</summary>
        public object? NewValue { get; set; }

        /// <summary>变化时间戳。</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>数据质量（Good/Uncertain/Bad）。</summary>
        public string Quality { get; set; } = "Good";
    }
}
