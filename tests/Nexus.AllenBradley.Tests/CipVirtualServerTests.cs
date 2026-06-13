using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Xunit;

namespace Nexus.AllenBradley.Tests
{
    public class CipVirtualServerTests : IDisposable
    {
        private readonly CipVirtualServer _server;
        private readonly int _port;

        public CipVirtualServerTests()
        {
            _port = GetFreeTcpPort();
            _server = new CipVirtualServer(_port);
        }

        private static int GetFreeTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _server?.Dispose();
        }

        // ── 基础 ─────────────────────────────────

        [Fact]
        public void Constructor_SetsDefaults()
        {
            Assert.Equal(_port, _server.Port);
            Assert.False(_server.IsRunning);
            Assert.Equal("1756-L83E", _server.DeviceName);
        }

        [Fact]
        public void StartStop_Lifecycle()
        {
            _server.Start();
            Assert.True(_server.IsRunning);
            _server.Stop();
            Assert.False(_server.IsRunning);
        }

        [Fact]
        public void Start_Twice_DoesNotThrow()
        {
            _server.Start();
            _server.Start(); // no-op
            Assert.True(_server.IsRunning);
        }

        [Fact]
        public void Stop_WhenNotStarted_DoesNotThrow()
        {
            _server.Stop();
            Assert.False(_server.IsRunning);
        }

        // ── Tag 管理 ─────────────────────────────

        [Fact]
        public void AddTag_AndGetTagValue()
        {
            _server.AddTag("TestDint", 42);
            Assert.Equal(42, _server.GetTagValue("TestDint"));
        }

        [Fact]
        public void AddTag_Bool()
        {
            _server.AddTag("TestBool", true);
            Assert.True((bool)_server.GetTagValue("TestBool")!);
        }

        [Fact]
        public void AddTag_Float()
        {
            _server.AddTag("TestReal", 3.14f);
            Assert.Equal(3.14f, (float)_server.GetTagValue("TestReal")!, 2);
        }

        [Fact]
        public void AddTag_String()
        {
            _server.AddTag("TestStr", "Hello");
            Assert.Equal("Hello", _server.GetTagValue("TestStr"));
        }

        [Fact]
        public void AddTag_Overwrite()
        {
            _server.AddTag("Counter", 1);
            _server.AddTag("Counter", 2);
            Assert.Equal(2, _server.GetTagValue("Counter"));
        }

        [Fact]
        public void SetTagValue_UpdatesExisting()
        {
            _server.AddTag("Val", 10);
            _server.SetTagValue("Val", 20);
            Assert.Equal(20, _server.GetTagValue("Val"));
        }

        [Fact]
        public void SetTagValue_CreatesNew()
        {
            _server.SetTagValue("NewTag", 99);
            Assert.Equal(99, _server.GetTagValue("NewTag"));
        }

        [Fact]
        public void GetTagValue_NotExists_ReturnsNull()
        {
            Assert.Null(_server.GetTagValue("NonExistent"));
        }

        [Fact]
        public void GetTagValue_Generic()
        {
            _server.AddTag("IntVal", 123);
            Assert.Equal(123, _server.GetTagValue<int>("IntVal"));
        }

        [Fact]
        public void TagExists()
        {
            _server.AddTag("Exists", 1);
            Assert.True(_server.TagExists("Exists"));
            Assert.False(_server.TagExists("NotExists"));
        }

        [Fact]
        public void RemoveTag()
        {
            _server.AddTag("ToRemove", 1);
            Assert.True(_server.RemoveTag("ToRemove"));
            Assert.False(_server.TagExists("ToRemove"));
        }

        [Fact]
        public void RemoveTag_NotExists_ReturnsFalse()
        {
            Assert.False(_server.RemoveTag("Ghost"));
        }

        [Fact]
        public void GetTagNames_ReturnsAll()
        {
            _server.AddTag("A", 1);
            _server.AddTag("B", 2);
            _server.AddTag("C", 3);
            var names = _server.GetTagNames();
            Assert.Contains("A", names);
            Assert.Contains("B", names);
            Assert.Contains("C", names);
        }

        [Fact]
        public void TagName_CaseInsensitive()
        {
            _server.AddTag("MyTag", 42);
            Assert.Equal(42, _server.GetTagValue("mytag"));
            Assert.Equal(42, _server.GetTagValue("MYTAG"));
            Assert.True(_server.TagExists("mytag"));
        }

        // ── 数据类型覆盖 ─────────────────────────

        [Fact]
        public void AddTag_AllDataTypes()
        {
            _server.AddTag("b", true);
            _server.AddTag("sb", (sbyte)-1);
            _server.AddTag("by", (byte)255);
            _server.AddTag("s", (short)-100);
            _server.AddTag("us", (ushort)65535);
            _server.AddTag("i", (int)-100000);
            _server.AddTag("ui", (uint)100000);
            _server.AddTag("l", (long)-1L);
            _server.AddTag("f", 1.5f);
            _server.AddTag("d", 2.5);
            _server.AddTag("str", "test");

            Assert.True((bool)_server.GetTagValue("b")!);
            Assert.Equal((sbyte)-1, _server.GetTagValue<sbyte>("sb"));
            Assert.Equal((byte)255, _server.GetTagValue<byte>("by"));
            Assert.Equal((short)-100, _server.GetTagValue<short>("s"));
            Assert.Equal((ushort)65535, _server.GetTagValue<ushort>("us"));
            Assert.Equal(-100000, _server.GetTagValue<int>("i"));
            Assert.Equal(100000u, _server.GetTagValue<uint>("ui"));
            Assert.Equal(-1L, _server.GetTagValue<long>("l"));
            Assert.Equal(1.5f, _server.GetTagValue<float>("f"));
            Assert.Equal(2.5, _server.GetTagValue<double>("d"));
            Assert.Equal("test", _server.GetTagValue<string>("str"));
        }

        // ── 网络通讯 ─────────────────────────────

        [Fact]
        public void Connect_RegisterSession()
        {
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            // 发送 RegisterSession
            byte[] request = new byte[28];
            request[0] = 0x65; request[1] = 0x00; // RegisterSession
            request[2] = 0x04; request[3] = 0x00; // Length = 4
            // Session handle = 0, status = 0, context = 0, options = 0
            request[24] = 0x01; request[25] = 0x00; // Version 1
            request[26] = 0x00; request[27] = 0x00; // Options = 0

            stream.Write(request, 0, request.Length);

            // 读取响应
            byte[] response = new byte[28];
            int read = ReadWithTimeout(stream, response, 28, 3000);
            Assert.Equal(28, read);

            ushort respCmd = (ushort)(response[0] | (response[1] << 8));
            uint respStatus = (uint)(response[8] | (response[9] << 8) | (response[10] << 16) | (response[11] << 24));

            Assert.Equal(0x0065, respCmd);
            Assert.Equal(0u, respStatus);

            // 验证 session handle 非零
            uint sessionHandle = (uint)(response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24));
            Assert.NotEqual(0u, sessionHandle);
        }

        [Fact]
        public void ListIdentity_ReturnsDeviceInfo()
        {
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            // 发送 ListIdentity
            byte[] request = new byte[24];
            request[0] = 0x63; request[1] = 0x00; // ListIdentity

            stream.Write(request, 0, request.Length);

            // 读取响应头
            byte[] header = new byte[24];
            int read = ReadWithTimeout(stream, header, 24, 3000);
            Assert.Equal(24, read);

            ushort respCmd = (ushort)(header[0] | (header[1] << 8));
            ushort respLen = (ushort)(header[2] | (header[3] << 8));
            Assert.Equal(0x0063, respCmd);
            Assert.True(respLen > 0);

            // 读取数据
            byte[] data = new byte[respLen];
            read = ReadWithTimeout(stream, data, respLen, 3000);
            Assert.Equal(respLen, read);
        }

        [Fact]
        public void ReadTag_Dint_ReturnsValue()
        {
            _server.AddTag("TestDint", 12345);
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            // RegisterSession
            uint sessionHandle = RegisterSession(stream);

            // SendRRData with CIP Read Tag
            byte[] tagPath = BuildSymbolicPath("TestDint");
            byte[] cipReq = new byte[2 + tagPath.Length + 2];
            cipReq[0] = 0x4C; // Read Tag
            cipReq[1] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, 2, tagPath.Length);
            cipReq[2 + tagPath.Length] = 0x01; // 1 element
            cipReq[3 + tagPath.Length] = 0x00;

            byte[] response = SendRRData(stream, sessionHandle, cipReq)
                ?? throw new InvalidOperationException("Null response");
            Assert.True(response.Length >= 4);

            // CIP 响应: Service(1) + Reserved(1) + Status(1) + ExtStatusSize(1) + Data(4)
            Assert.Equal((byte)(0x4C | 0x80), response[0]); // Response service
            Assert.Equal(0x00, response[2]); // Status = Success

            // 读取 DINT 值 (little-endian)
            int value = response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24);
            Assert.Equal(12345, value);
        }

        [Fact]
        public void WriteTag_Dint_StoresValue()
        {
            _server.AddTag("WriteTarget", 0);
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint sessionHandle = RegisterSession(stream);

            // 写入 DINT 值 999
            byte[] tagPath = BuildSymbolicPath("WriteTarget");
            byte[] cipReq = new byte[2 + tagPath.Length + 2 + 2 + 4];
            int pos = 0;
            cipReq[pos++] = 0x4D; // Write Tag
            cipReq[pos++] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, pos, tagPath.Length);
            pos += tagPath.Length;
            // Data type: DINT
            cipReq[pos++] = 0xC4;
            cipReq[pos++] = 0x00;
            // Elements: 1
            cipReq[pos++] = 0x01;
            cipReq[pos++] = 0x00;
            // Data: 999 in LE
            cipReq[pos++] = (byte)(999 & 0xFF);
            cipReq[pos++] = (byte)((999 >> 8) & 0xFF);
            cipReq[pos++] = (byte)((999 >> 16) & 0xFF);
            cipReq[pos++] = (byte)((999 >> 24) & 0xFF);

            byte[] response = SendRRData(stream, sessionHandle, cipReq)
                ?? throw new InvalidOperationException("Null response");
            Assert.Equal((byte)(0x4D | 0x80), response[0]);
            Assert.Equal(0x00, response[2]); // Status = Success

            // 验证值已写入
            Assert.Equal(999, _server.GetTagValue<int>("WriteTarget"));
        }

        [Fact]
        public void WriteTag_Bool_StoresValue()
        {
            _server.AddTag("BoolTag", false);
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint sessionHandle = RegisterSession(stream);

            byte[] tagPath = BuildSymbolicPath("BoolTag");
            byte[] cipReq = new byte[2 + tagPath.Length + 2 + 2 + 2];
            int pos = 0;
            cipReq[pos++] = 0x4D;
            cipReq[pos++] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, pos, tagPath.Length);
            pos += tagPath.Length;
            cipReq[pos++] = 0xC1; cipReq[pos++] = 0x00; // BOOL
            cipReq[pos++] = 0x01; cipReq[pos++] = 0x00; // 1 element
            cipReq[pos++] = 0x01; cipReq[pos++] = 0x00; // true

            SendRRData(stream, sessionHandle, cipReq);
            Assert.True((bool)_server.GetTagValue("BoolTag")!);
        }

        [Fact]
        public void WriteTag_String_StoresValue()
        {
            _server.AddTag("StrTag", "");
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint sessionHandle = RegisterSession(stream);

            string testStr = "Hello";
            byte[] strBytes = System.Text.Encoding.ASCII.GetBytes(testStr);
            byte[] tagPath = BuildSymbolicPath("StrTag");

            // CIP Write: Service + PathSize + Path + DataType(2) + Elements(2) + Len(4) + String
            byte[] cipReq = new byte[2 + tagPath.Length + 2 + 2 + 4 + strBytes.Length];
            int pos = 0;
            cipReq[pos++] = 0x4D;
            cipReq[pos++] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, pos, tagPath.Length);
            pos += tagPath.Length;
            cipReq[pos++] = 0xD0; cipReq[pos++] = 0x00; // STRING
            cipReq[pos++] = 0x01; cipReq[pos++] = 0x00; // 1 element
            // Length prefix (LE)
            cipReq[pos++] = (byte)(strBytes.Length & 0xFF);
            cipReq[pos++] = (byte)((strBytes.Length >> 8) & 0xFF);
            cipReq[pos++] = 0; cipReq[pos++] = 0;
            Buffer.BlockCopy(strBytes, 0, cipReq, pos, strBytes.Length);

            SendRRData(stream, sessionHandle, cipReq);
            Assert.Equal("Hello", _server.GetTagValue("StrTag"));
        }

        [Fact]
        public void ReadTag_NotExists_ReturnsError()
        {
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint sessionHandle = RegisterSession(stream);

            byte[] tagPath = BuildSymbolicPath("NoSuchTag");
            byte[] cipReq = new byte[2 + tagPath.Length + 2];
            cipReq[0] = 0x4C;
            cipReq[1] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, 2, tagPath.Length);
            cipReq[2 + tagPath.Length] = 0x01;
            cipReq[3 + tagPath.Length] = 0x00;

            byte[] response = SendRRData(stream, sessionHandle, cipReq)
                ?? throw new InvalidOperationException("Null response");
            Assert.Equal((byte)(0x4C | 0x80), response[0]);
            Assert.Equal(0x14, response[2]); // Tag not found
        }

        [Fact]
        public void OnWriteReceived_FiresOnWrite()
        {
            _server.AddTag("EventTag", 0);
            _server.Start();

            CipWriteEventArgs? receivedArgs = null;
            _server.OnWriteReceived += (_, args) => receivedArgs = args;

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint sessionHandle = RegisterSession(stream);

            byte[] tagPath = BuildSymbolicPath("EventTag");
            byte[] cipReq = new byte[2 + tagPath.Length + 2 + 2 + 4];
            int pos = 0;
            cipReq[pos++] = 0x4D;
            cipReq[pos++] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, pos, tagPath.Length);
            pos += tagPath.Length;
            cipReq[pos++] = 0xC4; cipReq[pos++] = 0x00; // DINT
            cipReq[pos++] = 0x01; cipReq[pos++] = 0x00;
            cipReq[pos++] = 0x39; cipReq[pos++] = 0x05; // 1337 LE
            cipReq[pos++] = 0x00; cipReq[pos++] = 0x00;

            SendRRData(stream, sessionHandle, cipReq);

            Assert.NotNull(receivedArgs);
            Assert.Equal("EventTag", receivedArgs!.TagName);
            Assert.Equal(1337, (int)receivedArgs.Value!);
        }

        [Fact]
        public void ConcurrentClients_NoErrors()
        {
            _server.AddTag("SharedCounter", 0);
            _server.Start();

            int clientCount = 5;
            var threads = new Thread[clientCount];
            var errors = new List<Exception>();

            for (int i = 0; i < clientCount; i++)
            {
                int idx = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        using var client = new TcpClient();
                        client.Connect("127.0.0.1", _port);
                        var stream = client.GetStream();

                        uint session = RegisterSession(stream);

                        // Read the tag
                        byte[] tagPath = BuildSymbolicPath("SharedCounter");
                        byte[] cipReq = new byte[2 + tagPath.Length + 2];
                        cipReq[0] = 0x4C;
                        cipReq[1] = (byte)(tagPath.Length / 2);
                        Buffer.BlockCopy(tagPath, 0, cipReq, 2, tagPath.Length);
                        cipReq[2 + tagPath.Length] = 0x01;
                        cipReq[3 + tagPath.Length] = 0x00;

                        byte[]? resp = SendRRData(stream, session, cipReq);
                        if (resp == null) throw new Exception("Null response");
                        if ((resp[0] & 0x80) == 0) throw new Exception("Not a response service");
                    }
                    catch (Exception ex)
                    {
                        lock (errors) { errors.Add(ex); }
                    }
                });
                threads[i].Start();
            }

            foreach (var t in threads) t.Join(5000);
            Assert.Empty(errors);
        }

        [Fact]
        public void MultipleReadWrite_Sequence()
        {
            _server.AddTag("SeqTag", 0);
            _server.Start();

            using var client = new TcpClient();
            client.Connect("127.0.0.1", _port);
            var stream = client.GetStream();

            uint session = RegisterSession(stream);

            // Write 100
            WriteDintTag(stream, session, "SeqTag", 100);
            Assert.Equal(100, _server.GetTagValue<int>("SeqTag"));

            // Write 200
            WriteDintTag(stream, session, "SeqTag", 200);
            Assert.Equal(200, _server.GetTagValue<int>("SeqTag"));

            // Read back
            int readVal = ReadDintTag(stream, session, "SeqTag");
            Assert.Equal(200, readVal);
        }

        // ── 辅助方法 ─────────────────────────────

        private uint RegisterSession(NetworkStream stream)
        {
            byte[] request = new byte[28];
            request[0] = 0x65; request[1] = 0x00;
            request[2] = 0x04; request[3] = 0x00;
            request[24] = 0x01; request[25] = 0x00;

            stream.Write(request, 0, request.Length);

            byte[] response = new byte[28];
            ReadWithTimeout(stream, response, 28, 3000);
            return (uint)(response[4] | (response[5] << 8) | (response[6] << 16) | (response[7] << 24));
        }

        private byte[]? SendRRData(NetworkStream stream, uint sessionHandle, byte[] cipData)
        {
            // Build SendRRData ENIP frame
            int dataLen = 4 + 2 + 2 + 2 + 2 + 2 + 2 + cipData.Length;
            byte[] frame = new byte[24 + dataLen];

            // ENIP header
            frame[0] = 0x6F; frame[1] = 0x00; // SendRRData
            frame[2] = (byte)(dataLen & 0xFF);
            frame[3] = (byte)((dataLen >> 8) & 0xFF);
            frame[4] = (byte)(sessionHandle & 0xFF);
            frame[5] = (byte)((sessionHandle >> 8) & 0xFF);
            frame[6] = (byte)((sessionHandle >> 16) & 0xFF);
            frame[7] = (byte)((sessionHandle >> 24) & 0xFF);

            // RRData payload
            int i = 24;
            // Interface Handle = 0
            i += 4;
            // Timeout = 0
            i += 2;
            // Item Count = 2
            frame[i++] = 2; frame[i++] = 0;
            // Item 1: Null Address
            frame[i++] = 0x00; frame[i++] = 0x00;
            frame[i++] = 0x00; frame[i++] = 0x00;
            // Item 2: Unconnected Data
            frame[i++] = 0xB2; frame[i++] = 0x00;
            frame[i++] = (byte)(cipData.Length & 0xFF);
            frame[i++] = (byte)((cipData.Length >> 8) & 0xFF);
            Buffer.BlockCopy(cipData, 0, frame, i, cipData.Length);

            stream.Write(frame, 0, frame.Length);

            // Read ENIP response header
            byte[] respHeader = new byte[24];
            int read = ReadWithTimeout(stream, respHeader, 24, 3000);
            if (read < 24) return null;

            ushort respLen = (ushort)(respHeader[2] | (respHeader[3] << 8));
            if (respLen == 0) return null;

            byte[] respData = new byte[respLen];
            read = ReadWithTimeout(stream, respData, respLen, 3000);
            if (read < respLen) return null;

            // Parse RRData → find CIP item (0xB2)
            int off = 6; // InterfaceHandle(4) + Timeout(2)
            if (off + 2 > respData.Length) return null;
            int itemCount = respData[off] | (respData[off + 1] << 8);
            off += 2;

            for (int j = 0; j < itemCount; j++)
            {
                if (off + 4 > respData.Length) break;
                ushort itemType = (ushort)(respData[off] | (respData[off + 1] << 8));
                ushort itemLen2 = (ushort)(respData[off + 2] | (respData[off + 3] << 8));
                off += 4;

                if (itemType == 0xB2 && off + itemLen2 <= respData.Length)
                {
                    byte[] cipResp = new byte[itemLen2];
                    Buffer.BlockCopy(respData, off, cipResp, 0, itemLen2);
                    return cipResp;
                }
                off += itemLen2;
            }

            return null;
        }

        private int ReadDintTag(NetworkStream stream, uint session, string tagName)
        {
            byte[] tagPath = BuildSymbolicPath(tagName);
            byte[] cipReq = new byte[2 + tagPath.Length + 2];
            cipReq[0] = 0x4C;
            cipReq[1] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, 2, tagPath.Length);
            cipReq[2 + tagPath.Length] = 0x01;
            cipReq[3 + tagPath.Length] = 0x00;

            byte[]? resp = SendRRData(stream, session, cipReq);
            if (resp == null || resp.Length < 8) return -1;
            return resp[4] | (resp[5] << 8) | (resp[6] << 16) | (resp[7] << 24);
        }

        private void WriteDintTag(NetworkStream stream, uint session, string tagName, int value)
        {
            byte[] tagPath = BuildSymbolicPath(tagName);
            byte[] cipReq = new byte[2 + tagPath.Length + 2 + 2 + 4];
            int pos = 0;
            cipReq[pos++] = 0x4D;
            cipReq[pos++] = (byte)(tagPath.Length / 2);
            Buffer.BlockCopy(tagPath, 0, cipReq, pos, tagPath.Length);
            pos += tagPath.Length;
            cipReq[pos++] = 0xC4; cipReq[pos++] = 0x00;
            cipReq[pos++] = 0x01; cipReq[pos++] = 0x00;
            cipReq[pos++] = (byte)(value & 0xFF);
            cipReq[pos++] = (byte)((value >> 8) & 0xFF);
            cipReq[pos++] = (byte)((value >> 16) & 0xFF);
            cipReq[pos++] = (byte)((value >> 24) & 0xFF);

            SendRRData(stream, session, cipReq);
        }

        private static byte[] BuildSymbolicPath(string tagName)
        {
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(tagName);
            // CIP Symbolic segment: 0x91 + length + name + pad (if name length is even, per ODVA spec)
            int pathLen = 2 + nameBytes.Length;
            if (nameBytes.Length % 2 == 0) pathLen++; // CIP spec: pad for even name length

            // Word alignment: pathSize is in 16-bit words, so total path bytes must be even
            if (pathLen % 2 != 0) pathLen++;

            byte[] path = new byte[pathLen];
            path[0] = 0x91;
            path[1] = (byte)nameBytes.Length;
            Buffer.BlockCopy(nameBytes, 0, path, 2, nameBytes.Length);
            return path;
        }

        private static int ReadWithTimeout(NetworkStream stream, byte[] buffer, int count, int timeoutMs)
        {
            int offset = 0;
            int deadline = Environment.TickCount + timeoutMs;
            while (offset < count && Environment.TickCount <= deadline)
            {
                if (!stream.DataAvailable)
                {
                    Thread.Sleep(10);
                    continue;
                }
                int n = stream.Read(buffer, offset, count - offset);
                if (n <= 0) break;
                offset += n;
            }
            return offset;
        }
    }
}
