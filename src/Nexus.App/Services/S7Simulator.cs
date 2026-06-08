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
    /// S7 通讯模拟器 — 在本地模拟 S7-1200/1500 PLC 的通讯响应。
    /// <para>支持 S7 通信帧格式 (TPKT + COTP + S7)。</para>
    /// </summary>
    public sealed class S7Simulator : IDisposable
    {
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptTask;
        private bool _isRunning;
        private int _connectionCount;

        public int Port { get; set; } = 102;
        public bool IsRunning => _isRunning;
        public int ConnectionCount => _connectionCount;

        // 模拟数据存储
        private readonly byte[] _db = new byte[65536]; // DB1 data
        private readonly byte[] _markers = new byte[65536]; // Merker
        private readonly byte[] _inputs = new byte[65536]; // Inputs
        private readonly byte[] _outputs = new byte[65536]; // Outputs

        public event EventHandler<string>? OnLog;

        public S7Simulator()
        {
            // Pre-seed some data
            _db[0] = 0x00; _db[1] = 0x80; // DB1.DBD0 = 128 (Int16)
            _db[2] = 0x03; _db[3] = 0xE8; // DB1.DBD2 = 1000
            _db[4] = 0xFF; _db[5] = 0xFF; // DB1.DBD4 = -1
        }

        public void Start()
        {
            if (_isRunning) return;
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
            _isRunning = true;
            _acceptTask = Task.Run(() => AcceptLoop(_cts.Token));
            OnLog?.Invoke(this, $"[S7 Sim] Started on port {Port}");
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _listener?.Stop();
            OnLog?.Invoke(this, "[S7 Sim] Stopped");
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _connectionCount);
                    _ = Task.Run(() => HandleClient(client, ct), ct);
                }
                catch { break; }
            }
        }

        private async Task HandleClient(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buf = new byte[4096];
                OnLog?.Invoke(this, $"[S7 Sim] Client connected from {client.Client.RemoteEndPoint}");

                try
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        // Read TPKT header (4 bytes)
                        int read = await ReadExact(stream, buf, 0, 4, ct).ConfigureAwait(false);
                        if (read < 4) break;

                        int tpktLen = (buf[2] << 8) | buf[3];
                        if (tpktLen < 4 || tpktLen > buf.Length) break;

                        // Read remaining TPKT payload
                        int remaining = tpktLen - 4;
                        if (remaining > 0)
                        {
                            read = await ReadExact(stream, buf, 4, remaining, ct).ConfigureAwait(false);
                            if (read < remaining) break;
                        }

                        // Parse COTP + S7
                        byte cotpType = buf[4]; // COTP PDU type

                        if (cotpType == 0xE0) // Connection Request (CR)
                        {
                            // Send Connection Confirm (CC)
                            var cc = new byte[] { 0x03, 0x00, 0x00, 0x16, 0xD0, 0x00, 0x00, 0x12, 0x01, 0xC0, 0x01, 0x0A, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02, 0xC0, 0x01, 0x09 };
                            await stream.WriteAsync(cc, 0, cc.Length, ct).ConfigureAwait(false);
                            OnLog?.Invoke(this, "[S7 Sim] COTP Connect Confirm sent");
                            continue;
                        }

                        // S7 Communication
                        if (tpktLen >= 17)
                        {
                            byte s7Func = buf[17]; // S7 function code
                            await HandleS7Function(stream, buf, tpktLen, s7Func, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch { }
                finally
                {
                    Interlocked.Decrement(ref _connectionCount);
                    OnLog?.Invoke(this, "[S7 Sim] Client disconnected");
                }
            }
        }

        private async Task HandleS7Function(NetworkStream stream, byte[] buf, int tpktLen, byte func, CancellationToken ct)
        {
            switch (func)
            {
                case 0x04: // Read/Write
                    byte subFunc = buf.Length > 21 ? buf[21] : (byte)0;
                    if (subFunc == 0x04) // Read
                        await HandleRead(stream, buf, ct).ConfigureAwait(false);
                    else if (subFunc == 0x05) // Write
                        await HandleWrite(stream, buf, ct).ConfigureAwait(false);
                    break;

                case 0x01: // Setup Communication
                    var setupResp = BuildSetupResponse();
                    await stream.WriteAsync(setupResp, 0, setupResp.Length, ct).ConfigureAwait(false);
                    OnLog?.Invoke(this, "[S7 Sim] Setup Communication response sent");
                    break;

                default:
                    OnLog?.Invoke(this, $"[S7 Sim] Unknown S7 function: 0x{func:X2}");
                    break;
            }
        }

        private async Task HandleRead(NetworkStream stream, byte[] buf, CancellationToken ct)
        {
            // Parse read request item(s)
            int itemCount = buf.Length > 25 ? buf[25] : 0;
            if (itemCount < 1) return;

            int area = buf[31];
            int dbNumber = (buf[27] << 8) | buf[28];
            int offset = ((buf[32] << 8) | buf[33]) * 8 + (buf[34] & 0x0F);
            int len = (buf[35] << 8) | buf[36];

            byte[] data = ReadMemory(area, dbNumber, offset, len);

            // Build read response
            int respLen = 27 + data.Length;
            var resp = new byte[respLen];
            // TPKT header
            resp[0] = 0x03; resp[1] = 0x00;
            resp[2] = (byte)(respLen >> 8); resp[3] = (byte)(respLen & 0xFF);
            // COTP DT
            resp[4] = 0x02; resp[5] = 0xF0; resp[6] = 0x80;
            // S7 header
            resp[7] = 0x32; // Protocol ID
            resp[8] = 0x01; // Job
            resp[9] = buf[9]; resp[10] = buf[10]; // Copy TID
            resp[11] = 0x00; resp[12] = 0x00; // PDU ref
            resp[13] = 0x00; resp[14] = 0x00; // Parameter length
            resp[15] = (byte)((data.Length + 4) >> 8); resp[16] = (byte)((data.Length + 4) & 0xFF);
            // S7 data
            resp[17] = 0x04; // Function = Read
            resp[18] = 0x01; // Item count
            // Return data
            resp[19] = 0xFF; // Success
            resp[20] = 0x04; // Transport size = byte
            resp[21] = (byte)(data.Length >> 8); resp[22] = (byte)(data.Length & 0xFF);
            resp[23] = 0x00;
            Buffer.BlockCopy(data, 0, resp, 24, data.Length);
            // Add padding if needed for word alignment
            resp[respLen - 3] = 0x00;

            await stream.WriteAsync(resp, 0, respLen, ct).ConfigureAwait(false);
            OnLog?.Invoke(this, $"[S7 Sim] Read area={area} offset={offset} len={len}");
        }

        private async Task HandleWrite(NetworkStream stream, byte[] buf, CancellationToken ct)
        {
            // Simplified write handling
            var writeResp = new byte[27];
            writeResp[0] = 0x03; writeResp[1] = 0x00;
            writeResp[2] = 0x00; writeResp[3] = 0x1B;
            writeResp[4] = 0x02; writeResp[5] = 0xF0; writeResp[6] = 0x80;
            writeResp[7] = 0x32; writeResp[8] = 0x01;
            writeResp[9] = buf[9]; writeResp[10] = buf[10];
            writeResp[17] = 0x05; // Function = Write
            writeResp[18] = 0x01;
            writeResp[19] = 0xFF; // Success

            await stream.WriteAsync(writeResp, 0, writeResp.Length, ct).ConfigureAwait(false);
            OnLog?.Invoke(this, "[S7 Sim] Write response sent");
        }

        private byte[] ReadMemory(int area, int dbNumber, int offset, int len)
        {
            byte[] src = area switch
            {
                0x84 => _db,       // DB
                0x83 => _markers,  // Merker (M)
                0x81 => _inputs,   // Input (I)
                0x82 => _outputs,  // Output (Q)
                _ => _db
            };

            var result = new byte[len];
            if (offset + len <= src.Length)
                Buffer.BlockCopy(src, offset, result, 0, len);
            return result;
        }

        private static byte[] BuildSetupResponse()
        {
            return new byte[] {
                0x03, 0x00, 0x00, 0x1B,
                0x02, 0xF0, 0x80,
                0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08,
                0x00, 0x00, 0x00, 0x08, 0x00, 0x01, 0x00, 0x06,
                0x00, 0x00, 0x01, 0x00
            };
        }

        private static async Task<int> ReadExact(NetworkStream stream, byte[] buf, int offset, int count, CancellationToken ct)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buf, offset + read, count - read, ct).ConfigureAwait(false);
                if (n <= 0) return read;
                read += n;
            }
            return read;
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }
    }
}
