using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.App.Services
{
    /// <summary>
    /// 本地 Modbus TCP 模拟器 — 无需真实 PLC 即可测试所有 Modbus 功能。
    /// <para>监听 127.0.0.1:1502，预置寄存器/线圈数据。</para>
    /// <para>支持功能码: 01(读线圈), 02(读离散输入), 03(读保持寄存器), 04(读输入寄存器),
    /// 05(写单线圈), 06(写单寄存器), 15(写多线圈), 16(写多寄存器)。</para>
    /// </summary>
    public sealed class ModbusTcpSimulator : IAsyncDisposable
    {
        private readonly object _gate = new object();
        private readonly ushort[] _holdingRegisters = new ushort[10000];
        private readonly ushort[] _inputRegisters = new ushort[10000];
        private readonly bool[] _coils = new bool[10000];
        private readonly bool[] _discreteInputs = new bool[10000];
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private int _connectionCount;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly Random _rng = new Random();
        private int _simulatedLatencyMs;
        private DateTime _startTime;

        public string Host => IPAddress.Loopback.ToString();
        public int Port { get; private set; } = 1502;
        public bool IsRunning => _listener is not null;
        public DateTime StartTime => _startTime;
        public int ConnectionCount => _connectionCount;

        /// <summary>模拟延迟（毫秒），用于测试超时场景。</summary>
        public int SimulatedLatencyMs
        {
            get => _simulatedLatencyMs;
            set => _simulatedLatencyMs = Math.Max(0, value);
        }

        public ModbusTcpSimulator()
        {
            SeedData();
        }

        /// <summary>预置模拟数据，让首次连接即有数据可读。</summary>
        private void SeedData()
        {
            // 保持寄存器
            _holdingRegisters[0] = 128;
            _holdingRegisters[1] = 256;
            _holdingRegisters[12] = 365;
            _holdingRegisters[100] = 1000;
            _holdingRegisters[101] = 2000;

            // 输入寄存器
            for (int i = 0; i < 20; i++)
                _inputRegisters[i] = (ushort)(_rng.Next(0, 65535));

            // 线圈
            _coils[0] = true;
            _coils[1] = false;
            _coils[2] = true;
            for (int i = 10; i < 20; i++)
                _coils[i] = _rng.Next(2) == 1;

            // 离散输入
            for (int i = 0; i < 20; i++)
                _discreteInputs[i] = _rng.Next(2) == 1;
        }

        /// <summary>更新动态模拟数据（正弦波、计数器等），由外部定时调用。</summary>
        public void UpdateDynamicData()
        {
            lock (_gate)
            {
                // 正弦波到 HR1
                double t = (DateTime.UtcNow - _startTime).TotalSeconds;
                _holdingRegisters[1] = (ushort)(Math.Sin(t) * 20000 + 30000);
                // 计数器到 COIL1
                _coils[1] = ((int)(t / 2) % 2) == 1;
                // 随机到 IR0
                _inputRegisters[0] = (ushort)_rng.Next(0, 65535);
            }
        }

        public Task StartAsync(int port = 1502, CancellationToken ct = default)
        {
            if (IsRunning) return Task.CompletedTask;

            Port = port;
            _startTime = DateTime.UtcNow;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _acceptLoop = AcceptLoopAsync(_cts.Token);

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_listener is null) return;

            _cts?.Cancel();
            _listener.Stop();
            _listener = null;

            lock (_clients)
            {
                foreach (var c in _clients)
                    try { c.Close(); } catch { }
                _clients.Clear();
            }

            if (_acceptLoop is not null)
                try { await _acceptLoop; } catch { }

            _cts?.Dispose();
            _cts = null;
            _acceptLoop = null;
            _connectionCount = 0;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (_clients) _clients.Add(client);
                    Interlocked.Increment(ref _connectionCount);
                    _ = HandleClientAsync(client, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[260];
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        // Read MBAP header (7 bytes)
                        int headerRead = 0;
                        while (headerRead < 7)
                        {
                            int n = await stream.ReadAsync(buffer, headerRead, 7 - headerRead, ct).ConfigureAwait(false);
                            if (n == 0) return;
                            headerRead += n;
                        }

                        // Parse MBAP header
                        int length = (buffer[4] << 8) | buffer[5];
                        int remaining = length - 1; // -1 for UnitId already in header
                        if (remaining < 1 || remaining > 252) return;

                        // Read remaining PDU
                        int pduRead = 0;
                        while (pduRead < remaining)
                        {
                            int n = await stream.ReadAsync(buffer, 7 + pduRead, remaining - pduRead, ct).ConfigureAwait(false);
                            if (n == 0) return;
                            pduRead += n;
                        }

                        // Simulated latency
                        if (_simulatedLatencyMs > 0)
                            await Task.Delay(_simulatedLatencyMs, ct).ConfigureAwait(false);

                        // Process request
                        byte unitId = buffer[6];
                        byte[] request = new byte[remaining];
                        Buffer.BlockCopy(buffer, 7, request, 0, remaining);

                        var response = ProcessRequest(unitId, request);
                        if (response != null)
                        {
                            // Build MBAP response
                            byte[] respMbap = new byte[7 + response.Length];
                            // Transaction ID (copy from request)
                            respMbap[0] = buffer[0]; respMbap[1] = buffer[1];
                            // Protocol ID
                            respMbap[2] = 0; respMbap[3] = 0;
                            // Length
                            int respLen = response.Length + 1;
                            respMbap[4] = (byte)(respLen >> 8); respMbap[5] = (byte)(respLen & 0xFF);
                            // Unit ID
                            respMbap[6] = unitId;
                            Buffer.BlockCopy(response, 0, respMbap, 7, response.Length);

                            await stream.WriteAsync(respMbap, 0, respMbap.Length, ct).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                lock (_clients) _clients.Remove(client);
            }
        }

        /// <summary>
        /// 处理 Modbus PDU 请求。
        /// 支持功能码: 01, 02, 03, 04, 05, 06, 15, 16
        /// </summary>
        private byte[]? ProcessRequest(byte unitId, byte[] pdu)
        {
            if (pdu.Length < 2) return null;
            byte fc = pdu[0];

            lock (_gate)
            {
                try
                {
                    return fc switch
                    {
                        0x01 => ProcessReadBits(_coils, pdu),
                        0x02 => ProcessReadBits(_discreteInputs, pdu),
                        0x03 => ProcessReadRegisters(_holdingRegisters, pdu),
                        0x04 => ProcessReadRegisters(_inputRegisters, pdu),
                        0x05 => ProcessWriteSingleCoil(pdu),
                        0x06 => ProcessWriteSingleRegister(pdu),
                        0x0F => ProcessWriteMultipleCoils(pdu),
                        0x10 => ProcessWriteMultipleRegisters(pdu),
                        _ => BuildError(fc, 0x01) // Illegal Function
                    };
                }
                catch
                {
                    return BuildError(fc, 0x04); // Server Device Failure
                }
            }
        }

        private byte[] ProcessReadBits(bool[] bits, byte[] pdu)
        {
            ushort start = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            if (count < 1 || count > 2000 || start + count > bits.Length)
                return BuildError(pdu[0], 0x02); // Illegal Data Address

            int byteCount = (count + 7) / 8;
            var data = new byte[byteCount];
            for (int i = 0; i < count; i++)
            {
                if (bits[start + i])
                    data[i / 8] |= (byte)(1 << (i % 8));
            }

            var result = new byte[2 + byteCount];
            result[0] = pdu[0]; // FC
            result[1] = (byte)byteCount;
            Buffer.BlockCopy(data, 0, result, 2, byteCount);
            return result;
        }

        private byte[] ProcessReadRegisters(ushort[] registers, byte[] pdu)
        {
            ushort start = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            if (count < 1 || count > 125 || start + count > registers.Length)
                return BuildError(pdu[0], 0x02);

            int byteCount = count * 2;
            var result = new byte[2 + byteCount];
            result[0] = pdu[0];
            result[1] = (byte)byteCount;
            for (int i = 0; i < count; i++)
            {
                result[2 + i * 2] = (byte)(registers[start + i] >> 8);
                result[3 + i * 2] = (byte)(registers[start + i] & 0xFF);
            }
            return result;
        }

        private byte[] ProcessWriteSingleCoil(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
            if (addr >= _coils.Length)
                return BuildError(pdu[0], 0x02);
            if (value != 0xFF00 && value != 0x0000)
                return BuildError(pdu[0], 0x03); // Illegal Data Value

            _coils[addr] = value == 0xFF00;
            return pdu; // Echo back
        }

        private byte[] ProcessWriteSingleRegister(byte[] pdu)
        {
            ushort addr = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort value = (ushort)((pdu[3] << 8) | pdu[4]);
            if (addr >= _holdingRegisters.Length)
                return BuildError(pdu[0], 0x02);

            _holdingRegisters[addr] = value;
            return pdu; // Echo back
        }

        private byte[] ProcessWriteMultipleCoils(byte[] pdu)
        {
            ushort start = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            byte byteCount = pdu[5];
            if (count < 1 || count > 1968 || start + count > _coils.Length)
                return BuildError(pdu[0], 0x02);

            for (int i = 0; i < count; i++)
            {
                int byteIdx = 6 + i / 8;
                int bitIdx = i % 8;
                _coils[start + i] = (pdu[byteIdx] & (1 << bitIdx)) != 0;
            }

            return new byte[] { pdu[0], pdu[1], pdu[2], pdu[3], pdu[4] };
        }

        private byte[] ProcessWriteMultipleRegisters(byte[] pdu)
        {
            ushort start = (ushort)((pdu[1] << 8) | pdu[2]);
            ushort count = (ushort)((pdu[3] << 8) | pdu[4]);
            byte byteCount = pdu[5];
            if (count < 1 || count > 123 || start + count > _holdingRegisters.Length)
                return BuildError(pdu[0], 0x02);

            for (int i = 0; i < count; i++)
            {
                _holdingRegisters[start + i] = (ushort)((pdu[6 + i * 2] << 8) | pdu[7 + i * 2]);
            }

            return new byte[] { pdu[0], pdu[1], pdu[2], pdu[3], pdu[4] };
        }

        private static byte[] BuildError(byte fc, byte exceptionCode)
            => new byte[] { (byte)(fc | 0x80), exceptionCode };

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
        }
    }
}
