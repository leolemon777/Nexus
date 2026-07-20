// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// Geniitek VB31 vibration sensor client — event-driven TCP.
// Adapted from HSL's Profinet.Geniitek.VibrationSensorClient (504 lines).

using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.Geniitek
{
    /// <summary>
    /// 振动传感器峰值数据(X/Y/Z 轴加速度、速度、位移、温度、电压)。
    /// </summary>
    public class VibrationSensorPeekValue
    {
        public float AcceleratedSpeedX { get; set; }
        public float AcceleratedSpeedY { get; set; }
        public float AcceleratedSpeedZ { get; set; }
        public float SpeedX { get; set; }
        public float SpeedY { get; set; }
        public float SpeedZ { get; set; }
        public int OffsetX { get; set; }
        public int OffsetY { get; set; }
        public int OffsetZ { get; set; }
        public float Temperature { get; set; }
        public float Voltage { get; set; }
        public int SendingInterval { get; set; }

        public override string ToString()
            => $"Peek[Acc=({AcceleratedSpeedX:F2},{AcceleratedSpeedY:F2},{AcceleratedSpeedZ:F2}) " +
               $"Spd=({SpeedX:F2},{SpeedY:F2},{SpeedZ:F2}) T={Temperature:F1}°C V={Voltage:F2}V]";
    }

    /// <summary>
    /// Geniitek VB31 智能无线振动传感器客户端。
    /// </summary>
    /// <remarks>
    /// VB31 是事件驱动的:传感器主动推送数据,客户端只需连上服务器端口即可。
    /// 帧格式: 9 字节头 + 3 字节子命令 + N 字节数据 + 2 字节 CRC。
    /// </remarks>
    public class VibrationSensorClient
    {
        /// <summary>峰值数据接收事件。</summary>
        public event Action<VibrationSensorPeekValue>? OnPeekValueReceive;

        /// <summary>连接成功事件。</summary>
        public event Action? OnClientConnected;

        /// <summary>连接超时(毫秒)。</summary>
        public int ConnectTimeout { get; set; } = 10000;

        /// <summary>设备地址。</summary>
        public ushort Address { get; set; } = 1;

        /// <summary>当前是否已连接。</summary>
        public bool IsConnected => _client?.Connected == true;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private volatile bool _closed;

        /// <summary>连接到传感器服务器。</summary>
        public OperateResult Connect(string ipAddress, int port = 1883)
        {
            try
            {
                _client = new TcpClient { SendTimeout = ConnectTimeout, ReceiveTimeout = ConnectTimeout };
                var ar = _client.BeginConnect(ipAddress, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeout, true))
                    return OperateResult.Failed($"连接超时: {ipAddress}:{port}");
                _client.EndConnect(ar);
                _stream = _client.GetStream();
                _cts = new CancellationTokenSource();
                _closed = false;
                OnClientConnected?.Invoke();
                _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
                return OperateResult.Success();
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"连接失败: {ex.Message}");
            }
        }

        /// <summary>断开连接。</summary>
        public void Close()
        {
            _closed = true;
            _cts?.Cancel();
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var stream = _stream;
            if (stream == null) return;

            while (!ct.IsCancellationRequested && !_closed)
            {
                try
                {
                    // 读 9 字节帧头。
                    byte[] header = new byte[9];
                    if (!await ReadExactAsync(stream, header, 9, ct)) break;

                    // 读 3 字节子命令。
                    byte[] subCmd = new byte[3];
                    if (!await ReadExactAsync(stream, subCmd, 3, ct)) break;

                    // 子命令决定后续数据长度。
                    int dataLen = GetSubCommandDataLength(subCmd);
                    if (dataLen > 0)
                    {
                        byte[] data = new byte[dataLen + 4]; // data + CRC(2) + extra(2)
                        if (!await ReadExactAsync(stream, data, dataLen + 4, ct)) break;

                        // 如果是 peek value 命令 (subCmd = [0x01, 0x00, 0x00]),解析。
                        if (subCmd[0] == 0x01)
                        {
                            var peek = ParsePeekValue(data);
                            OnPeekValueReceive?.Invoke(peek);
                        }
                    }
                }
                catch { break; }
            }
        }

        /// <summary>根据子命令返回数据长度。</summary>
        private static int GetSubCommandDataLength(byte[] subCmd)
        {
            // 0x01 = peek value, 固定 48 字节(3×3 float + 3×int + 2 float + 1 int = 9*4 + 3*4 + 2*4 + 4 = 48)。
            if (subCmd[0] == 0x01) return 48;
            return 0;
        }

        /// <summary>解析峰值数据(48 字节 → VibrationSensorPeekValue)。</summary>
        public static VibrationSensorPeekValue ParsePeekValue(byte[] data)
        {
            var v = new VibrationSensorPeekValue();
            if (data == null || data.Length < 48) return v;

            // 小端序浮点。
            v.AcceleratedSpeedX = BitConverter.ToSingle(data, 0);
            v.AcceleratedSpeedY = BitConverter.ToSingle(data, 4);
            v.AcceleratedSpeedZ = BitConverter.ToSingle(data, 8);
            v.SpeedX = BitConverter.ToSingle(data, 12);
            v.SpeedY = BitConverter.ToSingle(data, 16);
            v.SpeedZ = BitConverter.ToSingle(data, 20);
            v.OffsetX = BitConverter.ToInt32(data, 24);
            v.OffsetY = BitConverter.ToInt32(data, 28);
            v.OffsetZ = BitConverter.ToInt32(data, 32);
            v.Temperature = BitConverter.ToSingle(data, 36);
            v.Voltage = BitConverter.ToSingle(data, 40);
            v.SendingInterval = BitConverter.ToInt32(data, 44);
            return v;
        }

        private static async Task<bool> ReadExactAsync(NetworkStream ns, byte[] buf, int count, CancellationToken ct)
        {
            int off = 0;
            while (off < count)
            {
                int n = await ns.ReadAsync(buf, off, count - off, ct).ConfigureAwait(false);
                if (n == 0) return false;
                off += n;
            }
            return true;
        }

        public override string ToString() => $"VibrationSensorClient[Addr={Address}, Connected={IsConnected}]";
    }
}
