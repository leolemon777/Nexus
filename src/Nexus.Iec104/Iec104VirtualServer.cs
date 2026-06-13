using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Nexus.Iec104
{
    public class Iec104VirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;
        public int Port { get; }
        public bool IsRunning => _running;

        public Iec104VirtualServer(int port = 2404) { Port = port; }

        public void Start()
        {
            if (_running) return;
            _listener = new TcpListener(IPAddress.Any, Port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }

        public void Stop() { _running = false; _listener?.Stop(); }

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
                    var buffer = new byte[4096];
                    while (_running && client.Connected)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        // Simple echo: respond with U-STARTDT-CONF for STARTDT-ACT
                        if (read >= 2 && buffer[0] == 0x68)
                        {
                            int apduLen = buffer[1];
                            if (read >= 2 + apduLen && apduLen >= 2)
                            {
                                byte frameType = (byte)(buffer[2] & 0x03);
                                if (frameType == 0x03) // U-Format
                                {
                                    byte uType = buffer[2];
                                    if ((uType & 0x04) != 0) // STARTDT-ACT
                                    {
                                        byte[] confirm = new byte[] { 0x68, 0x02, 0x0B, 0x00 };
                                        stream.Write(confirm, 0, 4);
                                    }
                                }
                                else if (frameType == 0x01) // I-Format
                                {
                                    // Echo back I-Format response
                                    byte[] response = new byte[2 + apduLen];
                                    response[0] = 0x68;
                                    response[1] = (byte)apduLen;
                                    // Swap send/receive sequence numbers
                                    response[2] = (byte)(buffer[3] + 2); // NS = NR
                                    response[3] = buffer[2]; // NR = NS+1 (simplified)
                                    for (int i = 4; i < response.Length; i++)
                                        response[i] = i < read ? buffer[i] : (byte)0;
                                    stream.Write(response, 0, response.Length);
                                }
                                else if (frameType == 0x02) // S-Format
                                {
                                    // Acknowledge S-Format
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        public void Dispose() { Stop(); }
    }
}
