using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;
using Nexus.Yokogawa;

namespace Nexus.Yokogawa.Tests
{
    /// <summary>
    /// 横河 PLC 二进制链接协议综合测试。
    /// 使用动态端口启动虚拟服务器，避免与本机服务或并行测试冲突。
    /// </summary>
    public class YokogawaTests : IDisposable
    {
        private YokogawaVirtualServer? _server;

        public void Dispose()
        {
            _server?.Stop();
            _server?.Dispose();
        }

        /// <summary>启动虚拟服务器并返回客户端（已连接，长连接模式）。</summary>
        private (YokogawaVirtualServer server, YokogawaClient client) StartServerAndConnect()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            _server = server;

            var client = new YokogawaClient("127.0.0.1", server.Port);
            client.SetPersistentConnection();
            var conn = client.Connect();
            Assert.True(conn.IsSuccess, $"连接失败: {conn.Message}");

            return (server, client);
        }

        #region 地址解析测试

        [Fact]
        public void Address_Parse_D100()
        {
            var result = YokogawaAddress.ParseFrom("D100", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(4, result.Content.DataCode);
            Assert.Equal(100, result.Content.AddressStart);
            Assert.False(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_X0()
        {
            var result = YokogawaAddress.ParseFrom("X0", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(24, result.Content.DataCode);
            Assert.Equal(0, result.Content.AddressStart);
            Assert.True(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_Y10()
        {
            var result = YokogawaAddress.ParseFrom("Y10", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(25, result.Content.DataCode);
            Assert.Equal(10, result.Content.AddressStart);
            Assert.True(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_M50()
        {
            var result = YokogawaAddress.ParseFrom("M50", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(13, result.Content.DataCode);
            Assert.Equal(50, result.Content.AddressStart);
            Assert.True(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_W200()
        {
            var result = YokogawaAddress.ParseFrom("W200", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(23, result.Content.DataCode);
            Assert.Equal(200, result.Content.AddressStart);
            Assert.False(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_TN10()
        {
            var result = YokogawaAddress.ParseFrom("TN10", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(33, result.Content.DataCode);
            Assert.Equal(10, result.Content.AddressStart);
            Assert.False(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_CN5()
        {
            var result = YokogawaAddress.ParseFrom("CN5", 1);
            Assert.True(result.IsSuccess);
            Assert.Equal(49, result.Content.DataCode);
            Assert.Equal(5, result.Content.AddressStart);
            Assert.False(result.Content.IsBitType);
        }

        [Fact]
        public void Address_Parse_AllTypes()
        {
            // 所有支持的地址类型
            var types = new (string prefix, int code, bool isBit)[]
            {
                ("X", 24, true), ("Y", 25, true), ("I", 9, true), ("E", 5, true),
                ("M", 13, true), ("T", 20, true), ("C", 3, true), ("L", 12, true),
                ("D", 4, false), ("B", 2, false), ("F", 6, false), ("R", 18, false),
                ("V", 22, false), ("Z", 26, false), ("W", 23, false),
            };

            foreach (var (prefix, code, isBit) in types)
            {
                var result = YokogawaAddress.ParseFrom($"{prefix}1", 1);
                Assert.True(result.IsSuccess, $"{prefix} 解析失败");
                Assert.Equal(code, result.Content.DataCode);
                Assert.Equal(isBit, result.Content.IsBitType);
            }
        }

        [Fact]
        public void Address_Parse_Empty_Fails()
        {
            var result = YokogawaAddress.ParseFrom("", 1);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Address_Parse_Unsupported_Fails()
        {
            var result = YokogawaAddress.ParseFrom("Q100", 1);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Address_BinaryContent_BigEndian()
        {
            var result = YokogawaAddress.ParseFrom("D100", 1);
            Assert.True(result.IsSuccess);
            byte[] bin = result.Content.GetAddressBinaryContent();
            Assert.Equal(6, bin.Length);
            // DataCode=4 (big-endian): [0x00, 0x04]
            Assert.Equal(0x00, bin[0]);
            Assert.Equal(0x04, bin[1]);
            // Address=100 (big-endian): [0x00, 0x00, 0x00, 0x64]
            Assert.Equal(0x00, bin[2]);
            Assert.Equal(0x00, bin[3]);
            Assert.Equal(0x00, bin[4]);
            Assert.Equal(0x64, bin[5]);
        }

        #endregion

        #region 命令构建测试

        [Fact]
        public void BuildReadCommand_Word()
        {
            // 通过 ReadBytes 触发 BuildReadCommand
            var cmdResult = typeof(YokogawaClient).GetMethod("BuildReadCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(cmdResult);

            // 直接验证通过 CheckResponse 的路径
        }

        [Fact]
        public void BuildWriteCommand_EmptyData_Fails()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var result = client.Write("D100", new byte[0]);
                Assert.False(result.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void BuildWriteCommand_OddLength_Fails()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var result = client.Write("D100", new byte[3]);
                Assert.False(result.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region CDAB 字节序转换测试

        [Fact]
        public void ByteTransform_Int16_BigEndian()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // 写入 (short)0x1234 → 大端序 [0x12, 0x34]
                server.SetWord(4, 100, 0x1234);
                var read = client.ReadInt16("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal((short)0x1234, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void ByteTransform_Int32_CDAB()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // CDAB 存储: Int32=0x12345678 → [0x56, 0x78, 0x12, 0x34]
                server.SetWord32(4, 100, 0x12345678);
                var read = client.ReadInt32("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(0x12345678, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void ByteTransform_Float_CDAB()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                float expected = 3.14f;
                server.SetFloat(4, 100, expected);
                var read = client.ReadFloat("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(expected, read.Content, 0.0001f);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void ByteTransform_Int64_CDAB()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                long expected = 0x0123456789ABCDEF;
                server.SetWord64(4, 100, expected);
                var read = client.ReadInt64("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(expected, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void ByteTransform_Double_CDAB()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                double expected = 3.141592653589793;
                server.SetDouble(4, 100, expected);
                var read = client.ReadDouble("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(expected, read.Content, 0.0000001);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region E2E 字读写测试

        [Fact]
        public void E2E_ReadWrite_Int16()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = 12345;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_UInt16()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                ushort value = 65000;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadUInt16("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Int32()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                int value = -100000;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt32("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_UInt32()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                uint value = 3000000000;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadUInt32("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Int64()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                long value = -9876543210L;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt64("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Float()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                float value = -45.67f;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadFloat("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content, 0.001f);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Double()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                double value = 2.718281828;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadDouble("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content, 0.0000001);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_String()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                string value = "Hello";
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadString("D100", 3); // 3 words = 6 bytes
                Assert.True(read.IsSuccess);
                // "Hello" + null padding
                Assert.StartsWith("Hello", read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Bytes()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                byte[] data = new byte[] { 0x12, 0x34, 0x56, 0x78 };
                var write = client.Write("D100", data);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadBytes("D100", 2); // 2 words = 4 bytes
                Assert.True(read.IsSuccess);
                Assert.Equal(data, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadBytes_MultipleWords()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // 写入 3 个连续 Int16
                client.Write("D100", (short)100);
                client.Write("D101", (short)200);
                client.Write("D102", (short)300);

                // 一次读 3 个字
                var read = client.ReadBytes("D100", 3);
                Assert.True(read.IsSuccess);
                Assert.Equal(6, read.Content.Length);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_NegativeInt16()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = -12345;
                var write = client.Write("D100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region E2E 继电器读写测试

        [Fact]
        public void E2E_ReadWrite_Bool_Relay()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // 写入继电器
                var write = client.Write("M100", true);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadBool("M100");
                Assert.True(read.IsSuccess);
                Assert.True(read.Content);

                // 写入 false
                var write2 = client.Write("M100", false);
                Assert.True(write2.IsSuccess, write2.Message);

                var read2 = client.ReadBool("M100");
                Assert.True(read2.IsSuccess);
                Assert.False(read2.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Bool_X_Relay()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var write = client.Write("X10", true);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadBool("X10");
                Assert.True(read.IsSuccess);
                Assert.True(read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_Bool_Y_Relay()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var write = client.Write("Y20", true);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadBool("Y20");
                Assert.True(read.IsSuccess);
                Assert.True(read.Content);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region E2E 随机读写测试

        [Fact]
        public void E2E_ReadRandomWords()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // 设置数据
                server.SetWord(4, 100, 0x1111);
                server.SetWord(4, 200, 0x2222);
                server.SetWord(4, 300, 0x3333);

                var read = client.ReadRandomWords(new[] { "D100", "D200", "D300" });
                Assert.True(read.IsSuccess);
                Assert.Equal(6, read.Content.Length);

                // 每个地址 1 个字（2 字节），大端序
                Assert.Equal(0x11, read.Content[0]);
                Assert.Equal(0x11, read.Content[1]);
                Assert.Equal(0x22, read.Content[2]);
                Assert.Equal(0x22, read.Content[3]);
                Assert.Equal(0x33, read.Content[4]);
                Assert.Equal(0x33, read.Content[5]);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadRandomInt16()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                server.SetWord(4, 100, 1000);
                server.SetWord(4, 101, -500);

                var read = client.ReadRandomInt16(new[] { "D100", "D101" });
                Assert.True(read.IsSuccess);
                Assert.Equal(2, read.Content.Length);
                Assert.Equal((short)1000, read.Content[0]);
                Assert.Equal((short)-500, read.Content[1]);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_WriteRandomWords()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                byte[][] data = new byte[][]
                {
                    new byte[] { 0xAA, 0xBB },
                    new byte[] { 0xCC, 0xDD }
                };

                var write = client.WriteRandomWords(new[] { "D100", "D101" }, data);
                Assert.True(write.IsSuccess, write.Message);

                // 验证写入结果
                var read1 = client.ReadInt16("D100");
                Assert.True(read1.IsSuccess);
                Assert.Equal(unchecked((short)0xAABB), read1.Content);

                var read2 = client.ReadInt16("D101");
                Assert.True(read2.IsSuccess);
                Assert.Equal(unchecked((short)0xCCDD), read2.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadRandom_MixedAddresses()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                server.SetWord(4, 50, 0x1111);   // D50
                server.SetWord(23, 50, 0x2222);  // W50

                var read = client.ReadRandomWords(new[] { "D50", "W50" });
                Assert.True(read.IsSuccess);
                Assert.Equal(4, read.Content.Length);
                Assert.Equal(0x11, read.Content[0]);
                Assert.Equal(0x11, read.Content[1]);
                Assert.Equal(0x22, read.Content[2]);
                Assert.Equal(0x22, read.Content[3]);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region PLC 控制测试

        [Fact]
        public void E2E_StartStop()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                Assert.True(server.IsPlcRunning);

                var stop = client.Stop();
                Assert.True(stop.IsSuccess, stop.Message);
                Assert.False(server.IsPlcRunning);

                var start = client.Start();
                Assert.True(start.IsSuccess, start.Message);
                Assert.True(server.IsPlcRunning);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region 错误处理测试

        [Fact]
        public void Error_InvalidAddress()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var read = client.ReadInt16("Q100");
                Assert.False(read.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void Error_WriteNullData()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                var write = client.Write("D100", (byte[]?)null!);
                Assert.False(write.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void Error_RandomWrite_DataLengthMismatch()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                byte[][] data = new byte[][] { new byte[] { 0x01 } }; // 1 字节，不是 2
                var write = client.WriteRandomWords(new[] { "D100" }, data);
                Assert.False(write.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void Error_RandomWrite_CountMismatch()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                byte[][] data = new byte[][] { new byte[] { 0x01, 0x02 } };
                var write = client.WriteRandomWords(new[] { "D100", "D101" }, data);
                Assert.False(write.IsSuccess);
            }
            finally { client.Disconnect(); }
        }

        #endregion

        #region 连接/生命周期测试

        [Fact]
        public void Server_StartStop()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            Assert.True(true); // no exception

            server.Stop();
            server.Dispose();
        }

        [Fact]
        public void E2E_NonPersistent()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            _server = server;

            var client = new YokogawaClient("127.0.0.1", server.Port);
            // 非持久模式（默认）— 每次 SendAndReceive 自动连接/断开

            var write = client.Write("D100", (short)42);
            Assert.True(write.IsSuccess, write.Message);

            var read = client.ReadInt16("D100");
            Assert.True(read.IsSuccess);
            Assert.Equal((short)42, read.Content);

            client.Disconnect();
        }

        [Fact]
        public void E2E_MultipleReadWrite()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                // 连续读写多个地址
                for (int i = 0; i < 10; i++)
                {
                    short value = (short)(1000 + i);
                    var write = client.Write($"D{i}", value);
                    Assert.True(write.IsSuccess, $"Write D{i} 失败: {write.Message}");
                }

                for (int i = 0; i < 10; i++)
                {
                    var read = client.ReadInt16($"D{i}");
                    Assert.True(read.IsSuccess, $"Read D{i} 失败: {read.Message}");
                    Assert.Equal((short)(1000 + i), read.Content);
                }
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_CpuNumber_Custom()
        {
            var server = new YokogawaVirtualServer(0) { CpuNumber = 5 };
            server.Start();
            _server = server;

            var client = new YokogawaClient("127.0.0.1", server.Port) { CpuNumber = 5 };
            client.SetPersistentConnection();
            var conn = client.Connect();
            Assert.True(conn.IsSuccess, conn.Message);

            try
            {
                var write = client.Write("D100", (short)99);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("D100");
                Assert.True(read.IsSuccess);
                Assert.Equal((short)99, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void ConnectionPool_ReadWrite_ReusesPersistentConnection()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            _server = server;

            using var pool = new YokogawaConnectionPool("127.0.0.1", server.Port, maxPoolSize: 1);

            var write = pool.Write("D100", (short)1234);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("D100");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)1234, read.Content);
            Assert.Equal(0, pool.ActiveCount);
            Assert.Equal(1, pool.IdleCount);
        }

        [Fact]
        public void ConnectionPool_ForwardsPacketEvents()
        {
            var server = new YokogawaVirtualServer(0);
            server.SetWord(4, 10, 0x1234);
            server.Start();
            _server = server;

            using var pool = new YokogawaConnectionPool("127.0.0.1", server.Port);
            int sent = 0;
            int received = 0;
            pool.OnMessageSent += (_, _) => Interlocked.Increment(ref sent);
            pool.OnMessageReceived += (_, _) => Interlocked.Increment(ref received);

            var read = pool.ReadUInt16("D10");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((ushort)0x1234, read.Content);
            Assert.True(sent > 0);
            Assert.True(received > 0);
        }

        [Fact]
        public void ConnectionPool_RandomAndBatchReadWrite()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            _server = server;

            using var pool = new YokogawaConnectionPool("127.0.0.1", server.Port);
            var items = new[]
            {
                new KeyValuePair<string, object>("D20", (short)111),
                new KeyValuePair<string, object>("D21", (short)222),
            };

            var write = pool.BatchWrite(items);
            Assert.True(write.IsSuccess, write.Message);

            var batchRead = pool.BatchRead(new[] { "D20", "D21" });
            Assert.True(batchRead.IsSuccess, batchRead.Message);
            Assert.Equal((short)111, batchRead.Content["D20"]);
            Assert.Equal((short)222, batchRead.Content["D21"]);

            var randomRead = pool.ReadRandomInt16(new[] { "D20", "D21" });
            Assert.True(randomRead.IsSuccess, randomRead.Message);
            Assert.Equal(new short[] { 111, 222 }, randomRead.Content);
        }

        [Fact]
        public void ConnectionPool_StartStop_ControlsPlcState()
        {
            var server = new YokogawaVirtualServer(0);
            server.Start();
            _server = server;

            using var pool = new YokogawaConnectionPool("127.0.0.1", server.Port);
            Assert.True(server.IsPlcRunning);

            var stop = pool.Stop();
            Assert.True(stop.IsSuccess, stop.Message);
            Assert.False(server.IsPlcRunning);

            var start = pool.Start();
            Assert.True(start.IsSuccess, start.Message);
            Assert.True(server.IsPlcRunning);
        }

        [Fact]
        public void ConnectionPool_UsesCustomCpuNumber()
        {
            var server = new YokogawaVirtualServer(0) { CpuNumber = 5 };
            server.Start();
            _server = server;

            using var pool = new YokogawaConnectionPool("127.0.0.1", server.Port, cpuNumber: 5);

            var write = pool.Write("D30", (short)555);
            Assert.True(write.IsSuccess, write.Message);

            var read = pool.ReadInt16("D30");
            Assert.True(read.IsSuccess, read.Message);
            Assert.Equal((short)555, read.Content);
        }

        #endregion

        #region 错误码文本测试

        [Theory]
        [InlineData((byte)1, "不支持该命令")]
        [InlineData((byte)2, "命令长度错误")]
        [InlineData((byte)5, "地址范围错误")]
        [InlineData((byte)8, "CPU 错误")]
        [InlineData((byte)0x41, "看门狗超时")]
        [InlineData((byte)0x42, "链路错误")]
        [InlineData((byte)0xF1, "命令错误")]
        public void ErrorText_KnownCodes(byte code, string expected)
        {
            string text = YokogawaClient.GetErrorText(code);
            Assert.Contains(expected, text);
        }

        [Fact]
        public void ErrorText_UnknownCode()
        {
            string text = YokogawaClient.GetErrorText(0xFE);
            Assert.Contains("0xFE", text);
        }

        #endregion

        #region 多寄存器区测试

        [Fact]
        public void E2E_ReadWrite_W_Register()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = -9999;
                var write = client.Write("W100", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("W100");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_R_Register()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = 7777;
                var write = client.Write("R50", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("R50");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_TN_Register()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = 500;
                var write = client.Write("TN10", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("TN10");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        [Fact]
        public void E2E_ReadWrite_CN_Register()
        {
            var (server, client) = StartServerAndConnect();
            try
            {
                short value = -100;
                var write = client.Write("CN5", value);
                Assert.True(write.IsSuccess, write.Message);

                var read = client.ReadInt16("CN5");
                Assert.True(read.IsSuccess);
                Assert.Equal(value, read.Content);
            }
            finally { client.Disconnect(); }
        }

        #endregion
    }
}
