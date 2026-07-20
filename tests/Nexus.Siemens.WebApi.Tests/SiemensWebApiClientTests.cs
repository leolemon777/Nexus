using System;
using Nexus.Siemens.WebApi;
using Xunit;

namespace Nexus.Siemens.WebApi.Tests
{
    /// <summary>
    /// Phase C-5 测试 — 验证 S7 Web API 客户端的 JSON 提取器(纯函数)和构造/配置。
    /// 不需要真实 S7 PLC 或 HTTP 服务器。
    /// </summary>
    public class SiemensWebApiClientTests
    {
        // ── JSON 提取器(纯函数,无 IO)────────────

        [Theory]
        [InlineData("{\"DB1.DBW2\": 1234}", "DB1.DBW2", "1234")]              // 整数
        [InlineData("{\"DB1.DBD4\": 3.14}", "DB1.DBD4", "3.14")]              // 浮点
        [InlineData("{\"M0.0\": true}", "M0.0", "true")]                       // 布尔 true
        [InlineData("{\"M0.0\": false}", "M0.0", "false")]                     // 布尔 false
        [InlineData("{\"name\": \"hello\"}", "name", "hello")]                 // 字符串
        [InlineData("{\"DB1.DBW2\": 1234, \"DB1.DBW4\": 5678}", "DB1.DBW4", "5678")] // 多键取第二个
        [InlineData("{\"Param\": -42}", "Param", "-42")]                       // 负数
        [InlineData("{\"Param\":0}", "Param", "0")]                            // 零(无空格)
        [InlineData("  {  \"Param\"  :  123  }  ", "Param", "123")]            // 大量空格
        public void ExtractJsonValue_StandardCases(string json, string key, string expected)
        {
            Assert.Equal(expected, SiemensWebApiClient.ExtractJsonValue(json, key));
        }

        [Fact]
        public void ExtractJsonValue_MissingKey_ReturnsNull()
        {
            Assert.Null(SiemensWebApiClient.ExtractJsonValue("{\"foo\": 1}", "bar"));
        }

        [Fact]
        public void ExtractJsonValue_PartialKeyMatch_DoesNotMatch()
        {
            // "Param" 不应匹配 "Parameter"。
            string json = "{\"Parameter\": 1}";
            Assert.Null(SiemensWebApiClient.ExtractJsonValue(json, "Param"));
        }

        [Fact]
        public void ExtractJsonValue_EmptyInputs_ReturnsNull()
        {
            Assert.Null(SiemensWebApiClient.ExtractJsonValue("", "key"));
            Assert.Null(SiemensWebApiClient.ExtractJsonValue("{\"k\":1}", ""));
        }

        // 注:ExtractJsonValue 的参数是 string 非 nullable,传 null 会 NRE,
        // 这是预期行为(调用方不应传 null),本测试不覆盖该场景。

        [Fact]
        public void ExtractJsonValue_KeyWithDots()
        {
            // S7 地址含特殊字符(点),要确认 pattern 匹配。
            string json = "{\"DB1.DBX0.0\": true}";
            Assert.Equal("true", SiemensWebApiClient.ExtractJsonValue(json, "DB1.DBX0.0"));
        }

        // ── 构造与配置 ─────────────────────────

        [Fact]
        public void Constructor_StoresConfig()
        {
            var client = new SiemensWebApiClient("192.168.1.100", port: 8080, userName: "admin", password: "secret", timeout: 8000);
            Assert.Equal("192.168.1.100", client.IpAddress);
            Assert.Equal(8080, client.Port);
            Assert.Equal("admin", client.UserName);
            Assert.Equal("secret", client.Password);
            Assert.Equal(8000, client.Timeout);
        }

        [Fact]
        public void Constructor_NullIp_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SiemensWebApiClient(null!));
        }

        [Fact]
        public void Constructor_DefaultPort80()
        {
            var client = new SiemensWebApiClient("192.168.1.100");
            Assert.Equal(80, client.Port);
            Assert.Equal("admin", client.UserName);
            Assert.Equal(string.Empty, client.Password);
        }

        [Fact]
        public void IsConnected_AlwaysTrue_HttpStateless()
        {
            // HTTP 是无连接的,Web API 客户端视为始终"已连接"。
            var client = new SiemensWebApiClient("192.168.1.100");
            Assert.True(client.IsConnected);
        }

        // ── IDisposable ────────────────────────

        [Fact]
        public void Dispose_DoesNotThrow()
        {
            var client = new SiemensWebApiClient("192.168.1.100");
            client.Dispose();
        }

        [Fact]
        public void ImplementsExpectedInterfaces()
        {
            var client = new SiemensWebApiClient("192.168.1.100");
            Assert.IsAssignableFrom<Nexus.IReadWriteDevice>(client);
            Assert.IsAssignableFrom<Nexus.IBatchReadWrite>(client);
        }

        // ── 网络失败优雅处理 ────────────────────

        [Fact]
        public void ReadRaw_ConnectionFailure_ReturnsFailed()
        {
            // 用一个不存在的 IP,触发网络异常,验证优雅返回 OperateResult.Failed。
            var client = new SiemensWebApiClient("192.0.2.1", port: 1) { Timeout = 500 };
            var r = client.ReadRaw("DB1.DBW0");
            Assert.False(r.IsSuccess);
        }
    }
}
