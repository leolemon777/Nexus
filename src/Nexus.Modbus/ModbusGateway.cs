using System;

namespace Nexus.Modbus
{
    /// <summary>
    /// Modbus 网关 — 监听 Modbus TCP 请求并转发到目标设备。
    /// 使用 ModbusTcpServer 接收请求，通过 ModbusTcpClient 转发原始 PDU 到目标设备。
    /// </summary>
    public class ModbusGateway : IDisposable
    {
        private readonly int _listenPort;
        private readonly ModbusTcpClient _targetClient;
        private ModbusTcpServer? _server;
        private readonly object _targetLock = new object();

        /// <summary>事务日志事件 — 每次请求/响应时触发。</summary>
        public event EventHandler<GatewayTransactionEventArgs>? OnTransaction;

        /// <summary>网关是否正在运行。</summary>
        public bool IsRunning => _server?.IsRunning ?? false;

        /// <summary>
        /// 创建 Modbus 网关。
        /// </summary>
        /// <param name="listenPort">监听端口（作为 Modbus TCP 服务器）。</param>
        /// <param name="targetClient">目标 Modbus TCP 客户端（已配置 IP 和端口）。</param>
        public ModbusGateway(int listenPort, ModbusTcpClient targetClient)
        {
            _listenPort = listenPort;
            _targetClient = targetClient ?? throw new ArgumentNullException(nameof(targetClient));
        }

        /// <summary>启动网关。</summary>
        public void Start()
        {
            if (_server != null)
                return;

            _server = new ModbusTcpServer(_listenPort);
            _server.RequestProcessor = HandleForwardRequest;
            _server.Start();
        }

        /// <summary>停止网关。</summary>
        public void Stop()
        {
            if (_server != null)
            {
                _server.RequestProcessor = null;
                _server.Stop();
                _server.Dispose();
                _server = null;
            }
        }

        private byte[]? HandleForwardRequest(byte unitId, byte[] pdu)
        {
            byte[]? responsePdu = null;

            lock (_targetLock)
            {
                try
                {
                    _targetClient.Station = unitId;
                    var result = _targetClient.SendCustomModbus(pdu);
                    if (result.IsSuccess)
                        responsePdu = result.Content;
                }
                catch
                {
                }
            }

            OnTransaction?.Invoke(this, new GatewayTransactionEventArgs
            {
                RequestPdu = (byte[])pdu.Clone(),
                ResponsePdu = responsePdu != null ? (byte[])responsePdu.Clone() : null,
                UnitId = unitId,
                Timestamp = DateTime.Now
            });

            return responsePdu;
        }

        public void Dispose()
        {
            Stop();
        }
    }

    /// <summary>网关事务事件参数。</summary>
    public class GatewayTransactionEventArgs : EventArgs
    {
        /// <summary>请求 PDU。</summary>
        public byte[] RequestPdu { get; set; } = Array.Empty<byte>();
        /// <summary>响应 PDU（null 表示无响应或失败）。</summary>
        public byte[]? ResponsePdu { get; set; }
        /// <summary>从站地址。</summary>
        public byte UnitId { get; set; }
        /// <summary>事务时间戳。</summary>
        public DateTime Timestamp { get; set; }
        /// <summary>事务是否成功。</summary>
        public bool Success => ResponsePdu != null && ResponsePdu.Length > 0;
    }
}
