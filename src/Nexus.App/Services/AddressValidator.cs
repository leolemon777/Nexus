using System;
using System.Text.RegularExpressions;

namespace Nexus.App.Services
{
    /// <summary>
    /// 地址格式验证器 — 按协议验证用户输入的 PLC 地址格式。
    /// <para>对标 HSL AddressValidation，提供实时地址校验提示。</para>
    /// </summary>
    public static class AddressValidator
    {
        /// <summary>验证结果</summary>
        public sealed class ValidationResult
        {
            public bool IsValid { get; init; }
            public string Message { get; init; } = string.Empty;
            public string Normalized { get; init; } = string.Empty;
            public string Area { get; init; } = string.Empty;

            public static ValidationResult Ok(string normalized, string area)
                => new() { IsValid = true, Message = "地址格式正确", Normalized = normalized, Area = area };

            public static ValidationResult Fail(string message)
                => new() { IsValid = false, Message = message };
        }

        /// <summary>按协议名验证地址格式</summary>
        public static ValidationResult Validate(string protocolName, string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return ValidationResult.Fail("地址不能为空");

            string addr = address.Trim().ToUpperInvariant();

            return protocolName switch
            {
                // Modbus 系列
                "Modbus TCP" => ValidateModbus(addr),
                "Modbus UDP" => ValidateModbus(addr),
                "Modbus RTU" => ValidateModbus(addr),
                "Modbus RTU Over TCP" => ValidateModbus(addr),
                "Modbus ASCII" => ValidateModbus(addr),
                "Modbus ASCII Over TCP" => ValidateModbus(addr),
                "信捷 Xinje" => ValidateXinje(addr),

                // 西门子
                "S7-1200/1500" or "Siemens S7" => ValidateSiemens(addr),

                // 三菱
                "MC 协议 (Q/L/FX5U)" or "Mitsubishi MC" or "Mitsubishi MC / A1E" => ValidateMitsubishi(addr),
                "FX 串口协议" or "Mitsubishi FX" => ValidateMitsubishiFx(addr),

                // 欧姆龙
                "FINS-TCP" or "Omron FINS" => ValidateOmron(addr),

                // AB
                "CIP (ControlLogix)" or "Allen-Bradley CIP" => ValidateAllenBradley(addr),

                // 松下
                "Mewtocol (FP 系列)" or "Panasonic Mewtocol" => ValidatePanasonic(addr),

                // 基恩士
                "KV 系列上位通讯" or "Keyence KV" => ValidateKeyence(addr),

                // 倍福
                "TwinCAT ADS" or "Beckhoff ADS" => ValidateBeckhoff(addr),

                // 台达
                "DVP/AS 系列" or "Delta DVP" => ValidateDelta(addr),

                // 富士
                "SPH/SPB 系列" or "Fuji SPH" => ValidateModbus(addr),

                // LS 产电
                "XGT 协议" or "LS XGT" => ValidateLs(addr),

                // 永宏
                "FBs 系列" or "Fatek FBs" => ValidateFatek(addr),

                // FANUC
                "FANUC FOCAS" => ValidateFatek(addr), // Similar prefix-style

                // GE
                "GE SRTP" => ValidateGe(addr),

                // KUKA
                "KUKA EKI" => ValidateKuka(addr),

                // OPC UA
                "OPC UA" => ValidateOpcUa(addr),

                // 欧陆
                "2400/2500 调节器" or "Eurotherm" => ValidateModbus(addr),

                // 汇川
                "H3U/AM 系列" or "Inovance" => ValidateModbus(addr),

                _ => new ValidationResult { IsValid = true, Message = "未定义验证规则，跳过校验", Normalized = address }
            };
        }

        // ── Modbus: 线圈/寄存器 ──────────────
        private static ValidationResult ValidateModbus(string addr)
        {
            var m = Regex.Match(addr, @"^(?<prefix>[0-9XIDQCWMF])(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / X0 / M100 / 0=线圈 / 4=寄存器");
            char p = addr[0];
            string area = char.IsDigit(p) ? (p == '0' ? "线圈" : "寄存器") :
                          p == 'X' ? "输入" : p == 'D' ? "数据寄存器" : p == 'M' ? "内部继电器" : "其他";
            return ValidationResult.Ok(addr, area);
        }

        // ── 信捷: D/HD/SD/Y/X/M/T/C/S ──────
        private static ValidationResult ValidateXinje(string addr)
        {
            var m = Regex.Match(addr, @"^(HD|SD|[DYXMTCSE])(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / HD100 / Y0 / X0 / M100 / T0 / C100");
            return ValidationResult.Ok(addr, addr.Substring(0, 1) + "区");
        }

        // ── 西门子: DB1.DBD0 / I0.0 / Q0.0 / M0.0 ──────
        private static ValidationResult ValidateSiemens(string addr)
        {
            var m = Regex.Match(addr, @"^(DB\d+\.(DB[DXWB]\d+(\.\d+)?)|[IQML](\d+(\.\d+)?)|V\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: DB1.DBD0 / DB1.DBX0.0 / I0.0 / Q0.0 / M0.0 / V100");
            string area = addr.StartsWith("DB") ? "DB块" : addr[0] == 'I' ? "输入" : addr[0] == 'Q' ? "输出" : "M区/V区";
            return ValidationResult.Ok(addr, area);
        }

        // ── 三菱 MC: D100 / M100 / X0 / Y0 / R100 ──────
        private static ValidationResult ValidateMitsubishi(string addr)
        {
            var m = Regex.Match(addr, @"^(TS|TC|TN|CS|CC|CN|SM|SD|SW|ZR|DX|[DMXYZRBLFSVW])([0-9A-F]+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / M100 / X0 / Y0 / W100 / ZR100 / TS0 / CN0");

            string prefix = m.Groups[1].Value;
            string number = m.Groups[2].Value;
            bool hexArea = prefix is "X" or "Y" or "B" or "W" or "DX";
            string pattern = hexArea ? "^[0-9A-F]+$" : "^\\d+$";
            if (!Regex.IsMatch(number, pattern))
                return ValidationResult.Fail(hexArea ? $"{prefix} 区地址使用十六进制数字" : $"{prefix} 区地址使用十进制数字");

            return ValidationResult.Ok(addr, prefix + "区");
        }

        // ── 三菱 FX: D100 / M100 / X0 / Y0 / C0 / T0 ──────
        private static ValidationResult ValidateMitsubishiFx(string addr)
        {
            var m = Regex.Match(addr, @"^[DMXYZRSTCB](\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / M100 / X0 / Y0 / T0 / C0");
            return ValidationResult.Ok(addr, addr[0] + "区");
        }

        // ── 欧姆龙: D100 / CIO100 / W100 / H100 / A100 ──────
        private static ValidationResult ValidateOmron(string addr)
        {
            var m = Regex.Match(addr, @"^(CIO|WR|HR|AR|DM|EM|C?)(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / CIO100 / W100 / H100 / A100 / C100");
            return ValidationResult.Ok(addr, "数据区");
        }

        // ── AB: Tag1 / Program:Main.Tag / DINT[0] ──────
        private static ValidationResult ValidateAllenBradley(string addr)
        {
            if (addr.Length < 1) return ValidationResult.Fail("格式: MyTag / Program:Main.Tag / DINT_Array[0]");
            return ValidationResult.Ok(addr, "Tag");
        }

        // ── 松下: DT100 / DD100 / X0 / Y0 / R100 ──────
        private static ValidationResult ValidatePanasonic(string addr)
        {
            var m = Regex.Match(addr, @"^(DD|DT|LD|LT|X|Y|R|C|T|[SD]V)(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: DT100 / DD100 / X0 / Y0 / R100");
            return ValidationResult.Ok(addr, "数据区");
        }

        // ── 基恩士: DM100 / MR100 / CR100 / R100 ──────
        private static ValidationResult ValidateKeyence(string addr)
        {
            var m = Regex.Match(addr, @"^(DM|MR|CR|EM|FM|ZR|WR|LR|[RATCM])(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: DM100 / R100 / MR100 / CR100");
            return ValidationResult.Ok(addr, "数据区");
        }

        // ── 倍福: Main.instance.value / %IX0.0 ──────
        private static ValidationResult ValidateBeckhoff(string addr)
        {
            if (addr.StartsWith("%") || addr.Contains(".") || char.IsLetter(addr[0]))
                return ValidationResult.Ok(addr, "ADS Symbol");
            return ValidationResult.Fail("格式: Main.g_var / %IX0.0 / %MW100");
        }

        // ── 台达: D100 / Y0 / X0 / M100 / T0 / C0 ──────
        private static ValidationResult ValidateDelta(string addr)
        {
            var m = Regex.Match(addr, @"^[DMXYZRSTCB](\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / Y0 / X0 / M100 / T0 / C0");
            return ValidationResult.Ok(addr, addr[0] + "区");
        }

        // ── LS: %MW100 / %IX0.0 / D100 ──────
        private static ValidationResult ValidateLs(string addr)
        {
            if (addr.StartsWith("%")) return ValidationResult.Ok(addr, "IEC地址");
            var m = Regex.Match(addr, @"^[DMXYZRSTCBP](\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / %MW100 / %IX0.0 / X0 / P100");
            return ValidationResult.Ok(addr, addr[0] + "区");
        }

        // ── 永宏: D100 / R0 / Y0 / X0 / M100 ──────
        private static ValidationResult ValidateFatek(string addr)
        {
            var m = Regex.Match(addr, @"^[RDXYMTC](\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: D100 / R0 / Y0 / X0 / M100 / T0 / C0");
            return ValidationResult.Ok(addr, addr[0] + "区");
        }

        // ── GE: R100 / AI10 / AQ10 / %I10 / %Q10 ──────
        private static ValidationResult ValidateGe(string addr)
        {
            var m = Regex.Match(addr, @"^(AI|AQ|[%]?)(R|I|Q|M|T|AI|AQ)(\d+)$");
            if (!m.Success) return ValidationResult.Fail("格式: R100 / AI10 / AQ10 / %I10 / %Q10 / %M10");
            return ValidationResult.Ok(addr, "GE数据区");
        }

        // ── KUKA: 变量名 ──────
        private static ValidationResult ValidateKuka(string addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return ValidationResult.Fail("请输入 KUKA 变量名");
            return ValidationResult.Ok(addr, "EKI变量");
        }

        // ── OPC UA: NodeId ──────
        private static ValidationResult ValidateOpcUa(string addr)
        {
            if (string.IsNullOrWhiteSpace(addr)) return ValidationResult.Fail("请输入 NodeId，如 ns=2;s=Temperature");
            return ValidationResult.Ok(addr, "NodeId");
        }
    }
}
