using System;
using Nexus;
using Nexus.ShineIn;
using Xunit;

namespace Nexus.ShineIn.Tests
{
    public class ShineInLightSourceControllerTests
    {
        // ── PackCommand(纯函数)──────────────

        [Fact]
        public void PackCommand_ReadCommand_FormatCorrect()
        {
            byte[] frame = ShineInLightSourceController.PackCommand(2, new byte[] { 0x01 });
            // 期望: [2F 2A F0 02 len=05 01 xor 2A 2F]
            Assert.Equal(0x2F, frame[0]);
            Assert.Equal(0x2A, frame[1]);
            Assert.Equal(0xF0, frame[2]);
            Assert.Equal(0x02, frame[3]);  // cmd = read
            Assert.Equal(5, frame[4]);     // len = frame.Length - 4 = 5
            Assert.Equal(0x01, frame[5]);  // channel
            Assert.Equal(0x2A, frame[frame.Length - 2]);
            Assert.Equal(0x2F, frame[frame.Length - 1]);
            Assert.Equal(9, frame.Length);
        }

        [Fact]
        public void PackCommand_WriteCommand_FormatCorrect()
        {
            var data = new ShineInLightData { Color = 1, Light = 100, Channel = 1 };
            byte[] frame = ShineInLightSourceController.BuildWriteCommand(data);
            Assert.Equal(0x2F, frame[0]);
            Assert.Equal(0x01, frame[3]);   // cmd = write
            Assert.Equal(15, frame.Length);  // 8 + 7 = 15
            Assert.Equal(1, frame[5]);       // Color = 1
        }

        [Fact]
        public void PackCommand_XorCorrect()
        {
            byte[] frame = ShineInLightSourceController.PackCommand(2, new byte[] { 0x03 });
            // 重新计算 XOR 验证。
            int xor = frame[2];  // 0xF0
            for (int i = 3; i < frame.Length - 3; i++)
                xor ^= frame[i];
            Assert.Equal((byte)xor, frame[frame.Length - 3]);
        }

        [Fact]
        public void PackCommand_EmptyData_Valid()
        {
            byte[] frame = ShineInLightSourceController.PackCommand(2, null!);
            Assert.Equal(0x2F, frame[0]);
            Assert.Equal(8, frame.Length);  // 8 字节:头 5 + xor + */
        }

        // ── BuildReadCommand/BuildWriteCommand ──

        [Fact]
        public void BuildReadCommand_ChannelInData()
        {
            byte[] cmd = ShineInLightSourceController.BuildReadCommand(5);
            Assert.Equal(5, cmd[5]);  // channel 在 data[0]
        }

        [Fact]
        public void BuildWriteCommand_ContainsAll7Bytes()
        {
            var data = new ShineInLightData { Color = 2, Light = 200, LightDegree = 3, WorkMode = 4, Address = 5, PulseWidth = 6, Channel = 7 };
            byte[] cmd = ShineInLightSourceController.BuildWriteCommand(data);
            // data 从 cmd[5] 开始,7 字节。
            Assert.Equal(2, cmd[5]);    // Color
            Assert.Equal(200, cmd[6]);  // Light
            Assert.Equal(3, cmd[7]);    // LightDegree
            Assert.Equal(4, cmd[8]);    // WorkMode
            Assert.Equal(5, cmd[9]);    // Address
            Assert.Equal(6, cmd[10]);   // PulseWidth
            Assert.Equal(7, cmd[11]);   // Channel
        }

        // ── ShineInLightData ─────────────────

        [Fact]
        public void LightData_Defaults()
        {
            var d = new ShineInLightData();
            Assert.Equal((byte)4, d.Color);    // 白色
            Assert.Equal((byte)1, d.LightDegree);
            Assert.Equal((byte)1, d.PulseWidth);
        }

        [Fact]
        public void LightData_RoundTrip()
        {
            var d1 = new ShineInLightData { Color = 3, Light = 0xAB, Channel = 2, WorkMode = 1 };
            byte[] raw = d1.GetSourceData();
            Assert.Equal(7, raw.Length);

            var d2 = new ShineInLightData(raw);
            Assert.Equal(d1.Color, d2.Color);
            Assert.Equal(d1.Light, d2.Light);
            Assert.Equal(d1.Channel, d2.Channel);
            Assert.Equal(d1.WorkMode, d2.WorkMode);
        }

        [Fact]
        public void LightData_ParseFrom_Null_DoesNotThrow()
        {
            var d = new ShineInLightData();
            d.ParseFrom(null);  // 不抛异常,保持默认值
            Assert.Equal((byte)4, d.Color);
        }

        // ── ExtractActualData ────────────────

        [Fact]
        public void ExtractActualData_WriteSuccess()
        {
            // 构造写入成功响应:[2F 2A F0 01 len AA xor 2A 2F]
            byte[] resp = ShineInLightSourceController.PackCommand(1, new byte[] { 0xAA });
            // PackCommand 会把 0xAA 当 data,但 ExtractActualData 检查 cmd=1 时 [5]=0xAA。
            // 实际响应的 cmd 字段在 [3] = 1(因为 PackCommand cmd=1)。
            var r = ShineInLightSourceController.ExtractActualData(resp);
            Assert.True(r.IsSuccess, r.Message);
        }

        [Fact]
        public void ExtractActualData_ReadResponse()
        {
            // 构造读取响应:cmd=2,data = 7 bytes(光源参数)。
            byte[] src = new byte[] { 4, 100, 1, 4, 0, 1, 1 };  // 白色/亮度100/常亮/通道1
            byte[] resp = ShineInLightSourceController.PackCommand(2, src);
            // 但 PackCommand 构造的是"请求"格式;响应格式相同(设备回一样的帧格式)。
            var r = ShineInLightSourceController.ExtractActualData(resp);
            Assert.True(r.IsSuccess, r.Message);
            // data = src 的 7 字节(cmd != 1 时,ExtractActualData 返回 [5..len-3])。
            Assert.Equal(src, r.Content);
        }

        [Fact]
        public void ExtractActualData_TooShort_ReturnsFailed()
        {
            var r = ShineInLightSourceController.ExtractActualData(new byte[] { 1, 2, 3 });
            Assert.False(r.IsSuccess);
        }

        [Fact]
        public void ExtractActualData_BadFrame_ReturnsFailed()
        {
            byte[] bad = new byte[] { 0x00, 0x00, 0xF0, 0x02, 5, 1, 0, 0x2A, 0x2F };
            var r = ShineInLightSourceController.ExtractActualData(bad);
            Assert.False(r.IsSuccess);
            Assert.Contains("帧头", r.Message);
        }

        [Fact]
        public void ExtractActualData_NullResponse_ReturnsFailed()
        {
            var r = ShineInLightSourceController.ExtractActualData(null!);
            Assert.False(r.IsSuccess);
        }
    }
}
