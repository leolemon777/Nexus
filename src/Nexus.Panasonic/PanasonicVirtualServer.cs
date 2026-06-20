using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Nexus.Panasonic
{
    /// <summary>
    /// 松下 Mewtocol 虚拟服务器 — 模拟 FP 系列 PLC。
    /// <para>帧格式: % [Station] [Command] [Data] [BCC] CR</para>
    /// <para>支持: RCS(读单线圈), WCS(写单线圈), RD(读寄存器), WD(写寄存器)。</para>
    /// </summary>
    public class PanasonicVirtualServer : IDisposable
    {
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _running;

        private readonly ConcurrentDictionary<int, bool> _coils = new();
        private readonly ConcurrentDictionary<int, short> _registers = new();

        public int Port { get; }
        public bool IsRunning => _running;

        public PanasonicVirtualServer(int port = 9094)
        {
            Port = port;
        }

        /// <summary>预设寄存器值。</summary>
        public void SetRegister(int address, short value) => _registers[address] = value;

        /// <summary>预设线圈值。</summary>
        public void SetCoil(int address, bool value) => _coils[address] = value;

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
                    var buffer = new byte[1024];
                    while (_running && client.Connected)
                    {
                        // Read until CR (0x0D)
                        int bufPos = 0;
                        bool foundCr = false;
                        while (!foundCr && bufPos < buffer.Length)
                        {
                            int read = stream.Read(buffer, bufPos, 1);
                            if (read == 0) return;
                            if (buffer[bufPos] == 0x0D) foundCr = true;
                            bufPos++;
                        }

                        if (!foundCr) continue;

                        string request = Encoding.ASCII.GetString(buffer, 0, bufPos).TrimEnd('\r');
                        if (string.IsNullOrEmpty(request) || request[0] != '%') continue;

                        string? response = ProcessRequest(request);
                        if (response != null)
                        {
                            byte[] respBytes = Encoding.ASCII.GetBytes(response + "\r");
                            stream.Write(respBytes, 0, respBytes.Length);
                        }
                    }
                }
            }
            catch { }
        }

        private string? ProcessRequest(string request)
        {
            // %SSCCDD...DBCC
            // SS = station (2 chars), CC = command (2 chars), data, BCC (2 chars)
            if (request.Length < 7) return null;

            // Validate BCC
            string body = request.Substring(1, request.Length - 3); // strip % and BCC
            string bccStr = request.Substring(request.Length - 2);
            byte expectedBcc = ComputeBcc(body);
            byte actualBcc;
            if (!byte.TryParse(bccStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out actualBcc))
                return null;
            if (actualBcc != expectedBcc) return null;

            string station = request.Substring(1, 2);
            string command = request.Substring(3, 2).ToUpperInvariant();
            string data = request.Substring(5, request.Length - 7); // between command and BCC

            string? responseData;
            switch (command)
            {
                case "RD": // Read registers
                    responseData = ProcessRead(data);
                    break;
                case "WD": // Write registers
                    responseData = ProcessWrite(data);
                    break;
                case "RCS": // Read single coil
                    responseData = ProcessReadCoil(data);
                    break;
                case "WCS": // Write single coil
                    responseData = ProcessWriteCoil(data);
                    break;
                default:
                    responseData = null;
                    break;
            }

            if (responseData == null) return null;

            string responseFrame = "%" + station + responseData;
            byte responseBcc = ComputeBcc(responseFrame.Substring(1));
            return responseFrame + responseBcc.ToString("X2");
        }

        private string? ProcessRead(string data)
        {
            // data = "DT0 1" (area + start + count)
            // Simplified: just parse start address and count
            if (data.Length < 4) return null;

            // Try to extract address and count
            string addrStr = data.Trim();
            int spaceIdx = addrStr.IndexOf(' ');
            int startAddr = 0, count = 1;

            if (spaceIdx > 0)
            {
                if (!int.TryParse(addrStr.Substring(spaceIdx + 1), out count)) count = 1;
                addrStr = addrStr.Substring(0, spaceIdx);
            }

            // Strip non-numeric prefix (DT, WR, etc.)
            int numericStart = 0;
            while (numericStart < addrStr.Length && !char.IsDigit(addrStr[numericStart]))
                numericStart++;
            if (numericStart < addrStr.Length)
                int.TryParse(addrStr.Substring(numericStart), out startAddr);

            var sb = new StringBuilder("RD");
            for (int i = 0; i < count; i++)
            {
                short val = _registers.TryGetValue(startAddr + i, out short v) ? v : (short)0;
                sb.Append(val.ToString("X4", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private string? ProcessWrite(string data)
        {
            // data = "DT0 XXXX"
            int spaceIdx = data.IndexOf(' ');
            if (spaceIdx < 0) return null;

            string addrPart = data.Substring(0, spaceIdx);
            string valStr = data.Substring(spaceIdx + 1);

            int numericStart = 0;
            while (numericStart < addrPart.Length && !char.IsDigit(addrPart[numericStart]))
                numericStart++;
            int addr = 0;
            if (numericStart < addrPart.Length)
                int.TryParse(addrPart.Substring(numericStart), out addr);

            short value;
            if (short.TryParse(valStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
            {
                _registers[addr] = value;
            }

            return "WD";
        }

        private string ProcessReadCoil(string data)
        {
            int addr = 0;
            int.TryParse(data.Trim(), out addr);
            bool val = _coils.TryGetValue(addr, out bool v) && v;
            return "RCS" + (val ? "1" : "0");
        }

        private string? ProcessWriteCoil(string data)
        {
            // data = "addr value"
            var parts = data.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;
            int addr = 0;
            int.TryParse(parts[0], out addr);
            bool val = parts[1] == "1";
            _coils[addr] = val;
            return "WCS";
        }

        /// <summary>BCC = XOR of all ASCII bytes in the body.</summary>
        private static byte ComputeBcc(string body)
        {
            byte bcc = 0;
            foreach (char c in body)
                bcc ^= (byte)c;
            return bcc;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
