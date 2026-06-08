using System;

namespace Nexus
{
    /// <summary>
    /// 连接池接口 — 管理多个设备连接的获取、归还和生命周期。
    /// </summary>
    public interface IConnectionPool<T> : IDisposable where T : IReadWriteDevice
    {
        /// <summary>
        /// 获取一个已连接的设备实例，若池中无空闲设备则通过工厂创建新实例。
        /// </summary>
        T Acquire(string key);

        /// <summary>
        /// 将设备归还到连接池，若池已满则释放该设备。
        /// </summary>
        void Release(string key, T device);

        /// <summary>
        /// 从池中移除指定 key 的所有连接并释放。
        /// </summary>
        void Remove(string key);

        /// <summary>
        /// 当前活跃（已借出）的设备数量。
        /// </summary>
        int ActiveCount { get; }

        /// <summary>
        /// 当前空闲（池中等待复用）的设备数量。
        /// </summary>
        int IdleCount { get; }

        /// <summary>
        /// 释放池中所有设备并停止清理定时器。
        /// </summary>
        void Clear();
    }
}
