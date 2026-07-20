// Derived from HslCommunication (MIT, Copyright (c) Richard.Hu 2017-2025).
// See NOTICE and THIRD_PARTY_NOTICES.md.
//
// Toyota-Puc (丰田工机) computer-link protocol over TCP via 2PORT-EFR module.
// Adapted from HSL's Toyota.ToyoPuc. Simplified address parsing — full version
// in HSL supports 33 device types; this Nexus version supports common D/M/X/Y/S/R.

using System;

namespace Nexus.ToyoPuc
{
    /// <summary>
    /// 丰田工机(Toyota-Puc)PLC 计算机链接协议客户端。
    /// </summary>
    /// <remarks>
    /// <para><b>协议格式</b>(参考 HslCommunication.Profinet.Toyota.ToyoPuc):
    /// <list type="bullet">
    ///   <item>帧头: 4 字节 [0x00 0x00 lenLo lenHi](小端长度)</item>
    ///   <item>命令(PRG&lt;0,读字): [0x1C addrLo addrHi lenLo lenHi]</item>
    ///   <item>命令(PRG≥0,读字): [0x94 prg addrLo addrHi lenLo lenHi]</item>
    ///   <item>响应: [0x80=FT][status=0 OK][...data],status != 0 表示错误</item>
    /// </list>
    /// </para>
    /// </remarks>
    public class ToyoPucClient : TcpDeviceBase
    {
        public ToyoPucClient(string ip, int port = 10000, int timeout = 5000)
            : base(ip, port, timeout)
        {
            SetPersistentConnection();
        }

        protected override int ResponseHeaderLength => 4;

        protected override int GetResponsePayloadLength(byte[] header)
        {
            if (header == null || header.Length < 4) return 0;
            return BitConverter.ToUInt16(header, 2);
        }

        // ── 地址解析 ────────────────────────────

        private static (byte deviceType, ushort addressStart, int prg) ParseAddress(string address)
        {
            int prg = -1;
            int semiIdx = address.IndexOf(';');
            if (semiIdx > 0 && address.StartsWith("prg=", StringComparison.OrdinalIgnoreCase))
            {
                string prgStr = address.Substring(4, semiIdx - 4);
                if (int.TryParse(prgStr, out prg))
                    address = address.Substring(semiIdx + 1);
            }

            char prefix = char.ToUpperInvariant(address[0]);
            // 设备类型只用于校验,实际命令只用 addressStart 和 prg。
            string addrPart;
            switch (prefix)
            {
                case 'D': case 'M': case 'X': case 'Y': case 'S': case 'R':
                    addrPart = address.Substring(1);
                    break;
                default:
                    throw new FormatException($"不支持的设备类型 '{prefix}'(支持 D/M/X/Y/S/R)");
            }

            ushort addrStart;
            if (prefix == 'X' || prefix == 'Y')
                addrStart = (ushort)Convert.ToInt32(addrPart, 8);
            else
                addrStart = ushort.Parse(addrPart);

            return ((byte)prefix, addrStart, prg);
        }

        // ── 命令构造(公开,便于测试)────────────

        public static byte[] BuildReadWordCommand(ushort addressStart, ushort length)
        {
            return new byte[5]
            {
                0x1C,
                (byte)(addressStart & 0xFF),
                (byte)((addressStart >> 8) & 0xFF),
                (byte)(length & 0xFF),
                (byte)((length >> 8) & 0xFF)
            };
        }

        public static byte[] BuildReadWordCommandWithPrg(int prg, ushort addressStart, ushort length)
        {
            return new byte[6]
            {
                0x94,
                (byte)prg,
                (byte)(addressStart & 0xFF),
                (byte)((addressStart >> 8) & 0xFF),
                (byte)(length & 0xFF),
                (byte)((length >> 8) & 0xFF)
            };
        }

        public static byte[] BuildWriteWordCommand(ushort addressStart, byte[] data)
        {
            byte[] cmd = new byte[3 + data.Length];
            cmd[0] = 0x1D;
            cmd[1] = (byte)(addressStart & 0xFF);
            cmd[2] = (byte)((addressStart >> 8) & 0xFF);
            Buffer.BlockCopy(data, 0, cmd, 3, data.Length);
            return cmd;
        }

        public static byte[] PackFrame(byte[] command)
        {
            byte[] frame = new byte[4 + command.Length];
            frame[2] = (byte)(command.Length & 0xFF);
            frame[3] = (byte)((command.Length >> 8) & 0xFF);
            Buffer.BlockCopy(command, 0, frame, 4, command.Length);
            return frame;
        }

        // ── 响应解析 ────────────────────────────

        public static OperateResult<byte[]> ParseResponse(byte[] response)
        {
            if (response == null || response.Length < 4)
                return OperateResult<byte[]>.Failed($"响应过短: {response?.Length ?? 0}");

            if (response[0] != 0x80)
                return OperateResult<byte[]>.Failed($"FT 校验失败: 期望 0x80, 实际 0x{response[0]:X2}");

            if (response[1] != 0)
            {
                byte errCode = response.Length == 4 ? response[1] : response[4];
                return OperateResult<byte[]>.Failed($"ToyoPuc 错误 0x{errCode:X2}: {MapErrorCode(errCode)}");
            }

            if (response.Length > 5)
            {
                byte[] data = new byte[response.Length - 5];
                Buffer.BlockCopy(response, 5, data, 0, data.Length);
                return OperateResult<byte[]>.Success(data);
            }
            return OperateResult<byte[]>.Success(Array.Empty<byte>());
        }

        private static string MapErrorCode(byte code)
        {
            switch (code)
            {
                case 0x11: return "命令不支持";
                case 0x20: return "地址错误";
                case 0x21: return "长度错误";
                case 0x23: return "PRG 错误";
                default: return $"未知错误 0x{code:X2}";
            }
        }

        // ── 高级 API ────────────────────────────

        public OperateResult<byte[]> Read(string address, ushort length)
        {
            try
            {
                var (_, addrStart, prg) = ParseAddress(address);
                byte[] cmd = prg >= 0
                    ? BuildReadWordCommandWithPrg(prg, addrStart, length)
                    : BuildReadWordCommand(addrStart, length);
                var resp = SendAndReceive(PackFrame(cmd));
                if (!resp.IsSuccess) return OperateResult<byte[]>.Failed(resp.Message);
                return ParseResponse(resp.Content);
            }
            catch (Exception ex)
            {
                return OperateResult<byte[]>.Failed($"地址解析失败: {ex.Message}");
            }
        }

        public OperateResult Write(string address, byte[] data)
        {
            try
            {
                var (_, addrStart, prg) = ParseAddress(address);
                byte[] cmd;
                if (prg >= 0)
                {
                    cmd = new byte[4 + data.Length];
                    cmd[0] = 0x95;
                    cmd[1] = (byte)prg;
                    cmd[2] = (byte)(addrStart & 0xFF);
                    cmd[3] = (byte)((addrStart >> 8) & 0xFF);
                    Buffer.BlockCopy(data, 0, cmd, 4, data.Length);
                }
                else
                {
                    cmd = BuildWriteWordCommand(addrStart, data);
                }
                var resp = SendAndReceive(PackFrame(cmd));
                if (!resp.IsSuccess) return resp;
                var parsed = ParseResponse(resp.Content);
                return parsed.IsSuccess ? OperateResult.Success() : OperateResult.Failed(parsed.Message);
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"地址解析失败: {ex.Message}");
            }
        }

        public OperateResult<short> ReadInt16(string address)
        {
            var r = Read(address, 1);
            if (!r.IsSuccess) return OperateResult<short>.Failed(r.Message);
            if (r.Content.Length < 2) return OperateResult<short>.Failed("响应数据不足 2 字节");
            return OperateResult<short>.Success((short)((r.Content[1] << 8) | r.Content[0]));
        }

        public OperateResult<int> ReadInt32(string address)
        {
            var r = Read(address, 2);
            if (!r.IsSuccess) return OperateResult<int>.Failed(r.Message);
            if (r.Content.Length < 4) return OperateResult<int>.Failed("响应数据不足 4 字节");
            return OperateResult<int>.Success(
                (r.Content[3] << 24) | (r.Content[2] << 16) | (r.Content[1] << 8) | r.Content[0]);
        }

        public override string ToString() => $"ToyoPucClient[{Ip}:{Port}]";
    }
}
