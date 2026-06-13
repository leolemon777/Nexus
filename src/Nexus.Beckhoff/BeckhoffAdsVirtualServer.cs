using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Beckhoff
{
    /// <summary>
    /// Beckhoff ADS 虚拟服务器 — 模拟 TwinCAT ADS 设备。
    /// <para>支持: ReadDeviceInfo, ReadState, Read, Write, ReadWrite, Add/Delete Notification。</para>
    /// <para>内存模型: IndexGroup/IndexOffset → byte[] 字典。</para>
    /// </summary>
    public class BeckhoffAdsVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        private int _nextHandle = 1;

        private readonly ConcurrentDictionary<string, byte[]> _memory = new();
        private readonly ConcurrentDictionary<uint, string> _handles = new();
        private readonly ConcurrentDictionary<string, uint> _nameToHandle = new();

        public int Port { get; }
        public bool IsRunning => _running;

        public BeckhoffAdsVirtualServer(int port = 48898)
        {
            Port = port;
        }

        /// <summary>设置内存区域数据。</summary>
        public void SetMemory(uint indexGroup, uint indexOffset, byte[] data)
        {
            _memory[$"{indexGroup}:{indexOffset}"] = data;
        }

        /// <summary>通过变量名注册符号（模拟 Handle）。</summary>
        public void RegisterSymbol(string name, byte[] data)
        {
            _memory[$"sym:{name}"] = data;
        }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            _listener?.Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener!.AcceptTcpClient();
                    var thread = new Thread(() => HandleClient(client)) { IsBackground = true };
                    thread.Start();
                }
                catch { break; }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    while (_running && client.Connected)
                    {
                        // Read TCP AMS length (4 bytes)
                        byte[] lenBuf = new byte[4];
                        if (!ReadExact(stream, lenBuf, 4)) break;
                        int amsLen = BitConverter.ToInt32(lenBuf, 0);
                        if (amsLen <= 0 || amsLen > 65536) break;

                        // Read AMS data
                        byte[] amsData = new byte[amsLen];
                        if (!ReadExact(stream, amsData, amsLen)) break;

                        // Parse AMS header (32 bytes minimum)
                        if (amsData.Length < 32) continue;

                        // Extract fields
                        ushort command = (ushort)(amsData[16] | (amsData[17] << 8));
                        uint invokeId = BitConverter.ToUInt32(amsData, 28);
                        byte[] adsPayload = new byte[amsData.Length - 32];
                        if (adsPayload.Length > 0)
                            Buffer.BlockCopy(amsData, 32, adsPayload, 0, adsPayload.Length);

                        // Process and build response
                        byte[] response = ProcessCommand(command, invokeId, adsPayload, amsData);

                        // Send response: TCP length prefix + AMS frame
                        byte[] lenBytes = BitConverter.GetBytes(response.Length);
                        stream.Write(lenBytes, 0, 4);
                        stream.Write(response, 0, response.Length);
                    }
                }
            }
            catch { }
        }

        private byte[] ProcessCommand(ushort command, uint invokeId, byte[] payload, byte[] originalAms)
        {
            // Build AMS response header (swap target/source)
            using var ms = new MemoryStream();
            // Target NetId (original source) 6 bytes
            ms.Write(originalAms, 12, 6);
            // Target Port (original source port)
            ms.Write(originalAms, 18, 2);
            // Source NetId (original target) 6 bytes
            ms.Write(originalAms, 0, 6);
            // Source Port (original target port)
            ms.Write(originalAms, 6, 2);
            // Command
            WriteU16(ms, command);
            // StateFlags = response
            WriteU16(ms, 0x0005);
            // DataLength placeholder
            int dataLenPos = (int)ms.Position;
            WriteU32(ms, 0);
            // Error Code = 0 (success)
            WriteU32(ms, 0);
            // Invoke Id
            WriteU32(ms, invokeId);

            // ADS payload depends on command
            int payloadStart = (int)ms.Position;
            switch (command)
            {
                case 0x0001: // ReadDeviceInfo
                    ms.Write(Encoding.ASCII.GetBytes("NexusVS"), 0, Math.Min(7, 16)); // Device name
                    for (int i = 7; i < 16; i++) ms.WriteByte(0);
                    WriteU32(ms, 1); // Major
                    WriteU32(ms, 0); // Minor
                    WriteU16(ms, 1); // Build
                    WriteU16(ms, 1); // Device state
                    break;

                case 0x0004: // ReadState
                    WriteU16(ms, 5); // ADS state = Run
                    WriteU16(ms, 0); // Device state
                    break;

                case 0x0002: // Read
                    ProcessAdsRead(ms, payload);
                    break;

                case 0x0003: // Write
                    ProcessAdsWrite(ms, payload);
                    break;

                case 0x0009: // ReadWrite
                    ProcessAdsReadWrite(ms, payload);
                    break;

                case 0x0006: // AddDeviceNotification
                    {
                        uint handle = (uint)Interlocked.Increment(ref _nextHandle);
                        WriteU32(ms, handle);
                    }
                    break;

                case 0x0007: // DeleteDeviceNotification
                    // Success, no payload
                    break;

                default:
                    // Unknown command - return error
                    break;
            }

            // Fix DataLength
            int dataLen = (int)ms.Position - payloadStart;
            ms.Position = dataLenPos;
            WriteU32(ms, (uint)dataLen);

            return ms.ToArray();
        }

        private void ProcessAdsRead(MemoryStream ms, byte[] payload)
        {
            if (payload.Length < 12) return;
            uint indexGroup = BitConverter.ToUInt32(payload, 0);
            uint indexOffset = BitConverter.ToUInt32(payload, 4);
            uint readLen = BitConverter.ToUInt32(payload, 8);

            byte[] data = ReadMemory(indexGroup, indexOffset, (int)readLen);
            WriteU32(ms, 0); // Result
            ms.Write(data, 0, data.Length);
        }

        private void ProcessAdsWrite(MemoryStream ms, byte[] payload)
        {
            if (payload.Length < 12) return;
            uint indexGroup = BitConverter.ToUInt32(payload, 0);
            uint indexOffset = BitConverter.ToUInt32(payload, 4);
            uint writeLen = BitConverter.ToUInt32(payload, 8);

            if (payload.Length >= 12 + writeLen)
            {
                byte[] data = new byte[writeLen];
                Buffer.BlockCopy(payload, 12, data, 0, data.Length);
                _memory[$"{indexGroup}:{indexOffset}"] = data;
            }
            WriteU32(ms, 0); // Result
        }

        private void ProcessAdsReadWrite(MemoryStream ms, byte[] payload)
        {
            if (payload.Length < 16) return;
            uint indexGroup = BitConverter.ToUInt32(payload, 0);
            uint indexOffset = BitConverter.ToUInt32(payload, 4);
            uint readLen = BitConverter.ToUInt32(payload, 8);
            // uint writeLen = BitConverter.ToUInt32(payload, 12);

            byte[] data = ReadMemory(indexGroup, indexOffset, (int)readLen);
            WriteU32(ms, 0); // Result
            ms.Write(data, 0, data.Length);
        }

        private byte[] ReadMemory(uint indexGroup, uint indexOffset, int length)
        {
            // Symbol handle (0xF003)
            if (indexGroup == 0xF003 && length == 4)
            {
                // Return a handle for the variable name
                string name = "";
                if (_nameToHandle.TryGetValue(name, out uint h))
                    return BitConverter.GetBytes(h);
                uint handle = (uint)Interlocked.Increment(ref _nextHandle);
                return BitConverter.GetBytes(handle);
            }

            // Read by handle (0xF005)
            if (indexGroup == 0xF005)
            {
                byte[] empty = new byte[length];
                return empty;
            }

            string key = $"{indexGroup}:{indexOffset}";
            if (_memory.TryGetValue(key, out byte[] data))
            {
                byte[] result = new byte[length];
                int copy = Math.Min(data.Length, length);
                Buffer.BlockCopy(data, 0, result, 0, copy);
                return result;
            }

            return new byte[length];
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private static void WriteU16(MemoryStream ms, ushort v)
        {
            ms.WriteByte((byte)(v & 0xFF));
            ms.WriteByte((byte)((v >> 8) & 0xFF));
        }

        private static void WriteU32(MemoryStream ms, uint v)
        {
            ms.WriteByte((byte)(v & 0xFF));
            ms.WriteByte((byte)((v >> 8) & 0xFF));
            ms.WriteByte((byte)((v >> 16) & 0xFF));
            ms.WriteByte((byte)((v >> 24) & 0xFF));
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
