// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
// Rewritten for Nexus: unified virtual-server base. Replaces 41 scattered
// *VirtualServer.cs implementations that each duplicate TcpListener boilerplate.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Device
{
    /// <summary>
    /// 通用虚拟服务器基类 — 用于集成测试、协议模拟器、设备仿真。
    /// 一个 <see cref="DeviceServer"/> 实例代表一个监听 TCP/UDP 端口的虚拟设备,
    /// 子类重写 <see cref="HandleClientAsync"/> 实现具体协议的请求/响应逻辑。
    /// </summary>
    /// <remarks>
    /// <b>设计目的(B5 重构核心)</b>:
    /// <para>
    /// Nexus 当前有 41 个 <c>*VirtualServer.cs</c>(ModbusTcpServer、S7Server、FinsServer...),
    /// 每个都重复实现 TcpListener 生命周期、客户端线程管理、错误处理。
    /// 本类提取这些公共逻辑,新协议只需重写一个方法。
    /// </para>
    /// </remarks>
    public abstract class DeviceServer : IDisposable
    {
        private TcpListener? _tcpListener;
        private readonly ConcurrentDictionary<string, TcpClient> _clients =
            new ConcurrentDictionary<string, TcpClient>();
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private volatile bool _running;
        private volatile bool _disposed;
        private int _port;

        /// <summary>日志器(可选)。</summary>
        public ILogger Log { get; set; } = NullLogger.Instance;

        /// <summary>服务器监听端口。启动前为 0,启动后为实际端口(若用 port=0 则系统分配)。</summary>
        public int Port => _port;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>当前在线客户端数量。</summary>
        public int OnlineCount => _clients.Count;

        /// <summary>是否允许写入(只读服务器时设为 false,拒绝 Write 请求)。</summary>
        public bool EnableWrite { get; set; } = true;

        /// <summary>启动服务器。</summary>
        /// <param name="port">监听端口,0 表示系统自动分配。</param>
        public virtual OperateResult ServerStart(int port = 0)
        {
            if (_disposed) return OperateResult.Failed("服务器已释放");
            if (_running) return OperateResult.Failed("服务器已在运行");

            try
            {
                _cts = new CancellationTokenSource();
                _tcpListener = new TcpListener(IPAddress.Loopback, port);
                _tcpListener.Start();
                _port = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
                _running = true;
                _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                Log.Info($"虚拟服务器已启动,端口 {_port}");
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                Log.Error($"虚拟服务器启动失败 — {ex.Message}");
                return OperateResult.Failed($"服务器启动失败: {ex.Message}");
            }
        }

        /// <summary>停止服务器。</summary>
        public virtual void ServerClose()
        {
            if (!_running) return;
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _tcpListener?.Stop(); } catch { }
            // 关闭所有客户端连接。
            foreach (var kvp in _clients)
            {
                try { kvp.Value.Close(); } catch { }
            }
            _clients.Clear();
            try { _acceptTask?.Wait(1000); } catch { }
            Log.Info("虚拟服务器已停止");
        }

        /// <summary>接受新客户端的循环。</summary>
        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _running)
            {
                TcpClient client;
                try
                {
                    client = await _tcpListener!.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }

                string clientId = client.Client?.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                _clients[clientId] = client;

                // 每个连接独立处理。
                _ = Task.Run(() => HandleClientSafeAsync(client, clientId, cancellationToken));
            }
        }

        private async Task HandleClientSafeAsync(TcpClient client, string clientId, CancellationToken cancellationToken)
        {
            try
            {
                await HandleClientAsync(client, clientId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Debug($"客户端 {clientId} 处理异常 — {ex.Message}");
            }
            finally
            {
                _clients.TryRemove(clientId, out _);
                try { client.Close(); } catch { }
            }
        }

        /// <summary>
        /// 子类实现:处理一个客户端连接。读取请求、生成响应、循环至客户端断开。
        /// 默认实现空(无操作) — 立即关闭连接。
        /// </summary>
        protected abstract Task HandleClientAsync(TcpClient client, string clientId, CancellationToken cancellationToken);

        /// <summary>从 NetworkStream 精确读取 N 字节。返回 false 表示对端关闭。</summary>
        protected static async Task<bool> ReadExactAsync(NetworkStream ns, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int off = 0;
            while (off < count)
            {
                int n = await ns.ReadAsync(buffer, off, count - off, cancellationToken).ConfigureAwait(false);
                if (n == 0) return false;
                off += n;
            }
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                ServerClose();
                _cts?.Dispose();
            }
        }
    }
}
