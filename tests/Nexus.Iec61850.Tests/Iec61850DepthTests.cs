using System;
using System.Threading;
using Xunit;
using Nexus.Iec61850;

namespace Nexus.Iec61850.Tests
{
    public class Iec61850DepthTests
    {
        // ═══════════════════════════════════════════
        //  数据模型浏览 — 命令构建
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildGetServerDirectoryRequest_Format()
        {
            byte[] req = Iec61850Client.BuildGetServerDirectoryRequest();
            Assert.Equal(5, req.Length);
            Assert.Equal(0x05, req[0]);
            Assert.Equal(0x00, req[1]); // InvokeId
        }

        [Fact]
        public void BuildGetLogicalDeviceDirectoryRequest_Format()
        {
            byte[] req = Iec61850Client.BuildGetLogicalDeviceDirectoryRequest("LD0");
            Assert.Equal(37, req.Length);
            Assert.Equal(0x06, req[0]);
            // LD name at offset 5, 32 bytes padded
            Assert.Equal((byte)'L', req[5]);
            Assert.Equal((byte)'D', req[6]);
            Assert.Equal((byte)'0', req[7]);
            Assert.Equal(0x00, req[8]); // null padded
        }

        [Fact]
        public void BuildGetLogicalNodeDirectoryRequest_Format()
        {
            byte[] req = Iec61850Client.BuildGetLogicalNodeDirectoryRequest("LD0", "LLN0");
            Assert.Equal(69, req.Length);
            Assert.Equal(0x07, req[0]);
            // LD at offset 5, LN at offset 37
            Assert.Equal((byte)'L', req[37]);
            Assert.Equal((byte)'L', req[38]);
            Assert.Equal((byte)'N', req[39]);
            Assert.Equal((byte)'0', req[40]);
        }

        [Fact]
        public void BuildGetDataDirectoryRequest_Format()
        {
            byte[] req = Iec61850Client.BuildGetDataDirectoryRequest("LD0/LLN0.Beh");
            Assert.Equal(0x08, req[0]);
            Assert.Equal(12, req[4]); // length of "LD0/LLN0.Beh"
        }

        [Fact]
        public void BuildGetDataDirectoryRequest_LongRef()
        {
            byte[] req = Iec61850Client.BuildGetDataDirectoryRequest("LD0/GGIO1.Ind1.stVal");
            Assert.Equal(0x08, req[0]);
            Assert.Equal(20, req[4]); // length of "LD0/GGIO1.Ind1.stVal"
        }

        // ═══════════════════════════════════════════
        //  报告控制块 — 命令构建
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildEnableReportsRequest_Format()
        {
            byte[] req = Iec61850Client.BuildEnableReportsRequest("LD0/LLN0.RP.urcb1", "LD0/LLN0.ds1");
            Assert.Equal(0x09, req[0]);
            Assert.Equal(17, req[4]); // length of "LD0/LLN0.RP.urcb1"
        }

        [Fact]
        public void BuildEnableReportsRequest_ContainsDatasetRef()
        {
            byte[] req = Iec61850Client.BuildEnableReportsRequest("LD0/LLN0.RP.brcb1", "LD0/LLN0.ds1");
            Assert.Equal(0x09, req[0]);
            // After RCB ref (offset 5 + 17 = 22), DS ref length at offset 22
            Assert.Equal(12, req[22]); // length of "LD0/LLN0.ds1"
        }

        [Fact]
        public void BuildDisableReportsRequest_Format()
        {
            byte[] req = Iec61850Client.BuildDisableReportsRequest("LD0/LLN0.RP.urcb1");
            Assert.Equal(0x0A, req[0]);
            Assert.Equal(17, req[4]); // length of "LD0/LLN0.RP.urcb1"
        }

        [Fact]
        public void BuildDisableReportsRequest_ShortRef()
        {
            byte[] req = Iec61850Client.BuildDisableReportsRequest("RP.urcb1");
            Assert.Equal(0x0A, req[0]);
            Assert.Equal(8, req[4]); // length of "RP.urcb1"
        }

        // ═══════════════════════════════════════════
        //  控制操作 — 命令构建
        // ═══════════════════════════════════════════

        [Fact]
        public void BuildSelectRequest_Format()
        {
            byte[] req = Iec61850Client.BuildSelectRequest("LD0/GGIO1.Cmd1");
            Assert.Equal(0x0B, req[0]);
            Assert.Equal(14, req[4]); // length of "LD0/GGIO1.Cmd1"
        }

        [Fact]
        public void BuildOperateRequest_BoolValue()
        {
            byte[] req = Iec61850Client.BuildOperateRequest("LD0/GGIO1.Cmd1", new byte[] { 0x01 });
            Assert.Equal(0x0C, req[0]);
            Assert.Equal(14, req[4]); // length of "LD0/GGIO1.Cmd1"
            Assert.Equal(1, req[14 + 5]); // value length
            Assert.Equal(0x01, req[14 + 6]); // value
        }

        [Fact]
        public void BuildOperateRequest_FloatValue()
        {
            byte[] floatBytes = BitConverter.GetBytes(3.14f);
            byte[] req = Iec61850Client.BuildOperateRequest("LD0/MMXU1.mag.f", floatBytes);
            Assert.Equal(0x0C, req[0]);
            Assert.Equal(15, req[4]); // length of "LD0/MMXU1.mag.f"
            Assert.Equal(4, req[15 + 5]); // value length = 4 bytes for float
        }

        [Fact]
        public void BuildCancelRequest_Format()
        {
            byte[] req = Iec61850Client.BuildCancelRequest("LD0/GGIO1.Cmd1");
            Assert.Equal(0x0D, req[0]);
            Assert.Equal(14, req[4]); // length of "LD0/GGIO1.Cmd1"
        }

        [Fact]
        public void BuildSelectRequest_LongRef()
        {
            byte[] req = Iec61850Client.BuildSelectRequest("LD0/XCBR1.Pos.Oper");
            Assert.Equal(0x0B, req[0]);
            Assert.Equal(18, req[4]); // length of "LD0/XCBR1.Pos.Oper"
        }

        // ═══════════════════════════════════════════
        //  增强类型
        // ═══════════════════════════════════════════

        [Fact]
        public void QualityStamp_Flags()
        {
            QualityStamp q = QualityStamp.Overflow | QualityStamp.OldData;
            Assert.True(q.HasFlag(QualityStamp.Overflow));
            Assert.True(q.HasFlag(QualityStamp.OldData));
            Assert.False(q.HasFlag(QualityStamp.Failure));
        }

        [Fact]
        public void QualityStamp_AllValues()
        {
            Assert.Equal(0x0000, (ushort)QualityStamp.Valid);
            Assert.Equal(0x0001, (ushort)QualityStamp.Overflow);
            Assert.Equal(0x0002, (ushort)QualityStamp.OutOfRange);
            Assert.Equal(0x0004, (ushort)QualityStamp.BadReference);
            Assert.Equal(0x0100, (ushort)QualityStamp.Substituted);
            Assert.Equal(0x0200, (ushort)QualityStamp.Test);
            Assert.Equal(0x0400, (ushort)QualityStamp.Blocked);
        }

        [Fact]
        public void QualityStamp_Combination()
        {
            QualityStamp q = QualityStamp.Substituted | QualityStamp.Failure | QualityStamp.OldData;
            Assert.True(q.HasFlag(QualityStamp.Substituted));
            Assert.True(q.HasFlag(QualityStamp.Failure));
            Assert.True(q.HasFlag(QualityStamp.OldData));
            Assert.False(q.HasFlag(QualityStamp.Test));
        }

        [Fact]
        public void TimestampedValue_Defaults()
        {
            var tv = new TimestampedValue();
            Assert.Equal(QualityStamp.Valid, tv.Quality);
            Assert.Null(tv.Value);
        }

        [Fact]
        public void TimestampedValue_SetProperties()
        {
            var tv = new TimestampedValue
            {
                Value = 42.5f,
                Quality = QualityStamp.Substituted,
                Timestamp = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
                Source = 1,
            };
            Assert.Equal(42.5f, tv.Value);
            Assert.Equal(QualityStamp.Substituted, tv.Quality);
            Assert.Equal(1, tv.Source);
        }

        [Fact]
        public void DataAttributeInfo_Defaults()
        {
            var da = new DataAttributeInfo();
            Assert.Equal("", da.Name);
            Assert.Equal((FunctionalConstraint)0, da.FunctionalConstraint); // default enum value
            Assert.Equal(QualityStamp.Valid, da.Quality);
            Assert.False(da.IsWritable);
        }

        [Fact]
        public void DataAttributeInfo_SetProperties()
        {
            var da = new DataAttributeInfo
            {
                Name = "stVal",
                FunctionalConstraint = FunctionalConstraint.ST,
                DataType = "BOOLEAN",
                IsWritable = false,
                Value = true,
                Quality = QualityStamp.Valid,
                Timestamp = DateTime.UtcNow,
            };
            Assert.Equal("stVal", da.Name);
            Assert.Equal(FunctionalConstraint.ST, da.FunctionalConstraint);
            Assert.Equal("BOOLEAN", da.DataType);
            Assert.False(da.IsWritable);
        }

        [Fact]
        public void ControlResult_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.Success));
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.Negative));
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.Timeout));
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.Locked));
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.ObjectNotFound));
            Assert.True(Enum.IsDefined(typeof(ControlResult), ControlResult.ControlModeUnsupported));
        }

        [Fact]
        public void RcbType_AllDefined()
        {
            Assert.True(Enum.IsDefined(typeof(RcbType), RcbType.Buffered));
            Assert.True(Enum.IsDefined(typeof(RcbType), RcbType.Unbuffered));
        }

        [Fact]
        public void ReportControlBlockInfo_Defaults()
        {
            var rcb = new ReportControlBlockInfo();
            Assert.Equal("", rcb.Reference);
            Assert.False(rcb.IsEnabled);
            Assert.Equal(ReportTriggerOptions.None, rcb.TriggerOptions);
            Assert.Equal(0, rcb.IntegrityPeriod);
        }

        [Fact]
        public void ReportControlBlockInfo_SetProperties()
        {
            var rcb = new ReportControlBlockInfo
            {
                Reference = "LD0/LLN0.RP.brcb1",
                Type = RcbType.Buffered,
                DataSetReference = "LD0/LLN0.ds1",
                IsEnabled = true,
                TriggerOptions = ReportTriggerOptions.DataChanged | ReportTriggerOptions.Integrity,
                IntegrityPeriod = 5000,
            };
            Assert.Equal("LD0/LLN0.RP.brcb1", rcb.Reference);
            Assert.Equal(RcbType.Buffered, rcb.Type);
            Assert.True(rcb.IsEnabled);
            Assert.True(rcb.TriggerOptions.HasFlag(ReportTriggerOptions.DataChanged));
            Assert.Equal(5000, rcb.IntegrityPeriod);
        }

        // ═══════════════════════════════════════════
        //  客户端离线验证
        // ═══════════════════════════════════════════

        [Fact]
        public void Client_DefaultProperties()
        {
            var client = new Iec61850Client("127.0.0.1");
            Assert.Equal("LD0", client.LogicalDevice);
            Assert.False(client.IsConnected);
        }

        [Fact]
        public void Client_Select_WithoutConnect_Throws()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.Select("LD0/GGIO1.Cmd1");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_Operate_WithoutConnect_Throws()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.Operate("LD0/GGIO1.Cmd1", true);
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_Cancel_WithoutConnect_Throws()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.Cancel("LD0/GGIO1.Cmd1");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_GetServerDirectory_WithoutConnect_Fails()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.GetServerDirectory();
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_EnableReports_WithoutConnect_Fails()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.EnableReports("LD0/LLN0.RP.urcb1", "LD0/LLN0.ds1");
            Assert.False(result.IsSuccess);
        }

        [Fact]
        public void Client_DisableReports_WithoutConnect_Fails()
        {
            var client = new Iec61850Client("127.0.0.1");
            var result = client.DisableReports("LD0/LLN0.RP.urcb1");
            Assert.False(result.IsSuccess);
        }
    }
}
