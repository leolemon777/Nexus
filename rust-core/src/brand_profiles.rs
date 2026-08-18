//! Modbus 兼容品牌的软元件地址映射层(台达/汇川等国产品牌直接跑 Modbus,
//! 但软元件语法 D100/M100/Y17 需翻译成线圈/寄存器地址)。
//!
//! 设计纪律(与「期望值禁自推」一致):
//! - 映射数值**表驱动**且标注来源与置信度;凡无把握的段返回
//!   `BRAND_MAP_MANUAL` 错误并提示查手册——宁可不支持,不读错数据。
//! - 台达 DVP-ES/EX/SS 的 X/Y/M/D 段来自台达 DVP 操作手册的 Modbus 地址表
//!   (业界稳定常识:X/Y 基址 0x0500、M0-511 基址 0x0800、D 基址 0x1000)。
//!
//! 映射产物是「Modbus FC 类别 + 线性地址」,可直接走现有 modbus_pdu 栈。

use crate::error::CoreError;

/// Modbus 访问类别(供调用方选 FC)。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BrandArea {
    /// 线圈(FC01/05/15)
    Coil,
    /// 离散输入(FC02,只读)
    DiscreteInput,
    /// 保持寄存器(FC03/06/16)
    HoldingRegister,
}

/// 解析结果:品牌软元件 → Modbus 访问
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrandModbusAddress {
    pub area: BrandArea,
    pub modbus_address: u16,
    /// 该软元件是否位访问(true: 单点位;false: 字/寄存器)
    pub is_bit: bool,
}

/// 品牌 × 系列
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BrandProfile {
    /// 台达 DVP ES/EX/SS 系列
    DeltaDvpEs,
}

fn manual_err(what: &str) -> CoreError {
    CoreError::Modbus {
        code: "BRAND_MAP_MANUAL",
        message: format!(
            "{what}:该软元件段的 Modbus 映射未内置(避免猜错读错数据),请查台达 DVP 操作手册的 Modbus 地址对照表,直接用 Modbus 地址访问"
        ),
        details: None,
    }
}

/// 解析品牌软元件地址。
///
/// 支持(台达 ES/EX/SS):
/// - `D0-D1311`  → 保持寄存器 0x1000+(字)
/// - `M0-M511`   → 线圈 0x0800+;`M512-M1535` → 线圈 0x2400+(位)
/// - `Y0-Y177`(八进制) → 线圈 0x0500+(位)
/// - `X0-X177`(八进制) → 离散输入 0x0500+(位,只读)
/// - `T/C/S` 段未内置 → 返回 MANUAL 提示
pub fn parse_brand_address(profile: BrandProfile, input: &str) -> Result<BrandModbusAddress, CoreError> {
    let s = input.trim().to_ascii_uppercase();
    if s.is_empty() {
        return Err(CoreError::Modbus { code: "BRAND_ADDRESS_INVALID", message: "地址为空".into(), details: None });
    }

    let (prefix, rest) = split_alpha_prefix(&s)
        .ok_or_else(|| CoreError::Modbus {
            code: "BRAND_ADDRESS_INVALID",
            message: format!("「{input}」不是合法软元件(形如 D100 / M100 / Y17 / X10)"),
            details: None,
        })?;
    if rest.is_empty() || !rest.chars().all(|c| c.is_ascii_digit()) {
        return Err(CoreError::Modbus {
            code: "BRAND_ADDRESS_INVALID",
            message: format!("「{input}」编号部分「{rest}」应为数字"),
            details: None,
        });
    }

    match profile {
        BrandProfile::DeltaDvpEs => delta_dvp_es(&prefix, &rest, input),
    }
}

fn delta_dvp_es(prefix: &str, num: &str, original: &str) -> Result<BrandModbusAddress, CoreError> {
    // 八进制区(X/Y):编号按 8 进制解释(Y17 → 15)
    let oct = |n: &str| u32::from_str_radix(n, 8).ok();
    match prefix {
        "D" => {
            let n: u32 = num.parse().map_err(|_| manual_err("D 解析失败"))?;
            if n <= 1311 {
                Ok(BrandModbusAddress {
                    area: BrandArea::HoldingRegister,
                    modbus_address: (0x1000 + n) as u16,
                    is_bit: false,
                })
            } else {
                Err(manual_err("D1312 以上"))
            }
        }
        "M" => {
            let n: u32 = num.parse().map_err(|_| manual_err("M 解析失败"))?;
            let base = if n <= 511 { 0x0800u32 } else if n <= 1535 { 0x2400u32 + (n - 512) } else { return Err(manual_err("M1536 以上")) };
            Ok(BrandModbusAddress { area: BrandArea::Coil, modbus_address: base as u16, is_bit: true })
        }
        "Y" => {
            let n = oct(num).ok_or_else(|| manual_err("Y 八进制解析失败"))?;
            if n > 0o177 { return Err(manual_err("Y177 以上")); }
            Ok(BrandModbusAddress { area: BrandArea::Coil, modbus_address: (0x0500 + n) as u16, is_bit: true })
        }
        "X" => {
            let n = oct(num).ok_or_else(|| manual_err("X 八进制解析失败"))?;
            if n > 0o177 { return Err(manual_err("X177 以上")); }
            Ok(BrandModbusAddress { area: BrandArea::DiscreteInput, modbus_address: (0x0500 + n) as u16, is_bit: true })
        }
        "T" | "C" | "S" | "HC" => Err(manual_err(&format!("软元件 {prefix}(原「{original}」)"))),
        _ => Err(CoreError::Modbus {
            code: "BRAND_ADDRESS_INVALID",
            message: format!("台达 ES/EX/SS 不认识的软元件「{original}」(支持 D/M/X/Y;T/C/S 查手册)"),
            details: None,
        }),
    }
}

/// 拆字母前缀与数字(最长前缀匹配 HC/CT 等)。
fn split_alpha_prefix(s: &str) -> Option<(&str, &str)> {
    let idx = s.find(|c: char| c.is_ascii_digit())?;
    Some((&s[..idx], &s[idx..]))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn delta_d_m_x_y_mappings() {
        // D100 → 保持寄存器 0x1064
        let d = parse_brand_address(BrandProfile::DeltaDvpEs, "D100").unwrap();
        assert_eq!((d.area, d.modbus_address, d.is_bit), (BrandArea::HoldingRegister, 0x1064, false));
        // M0 → 线圈 0x0800;M512 → 0x2400;M1535 → 0x2400+1023=0x27FF
        assert_eq!(parse_brand_address(BrandProfile::DeltaDvpEs, "M0").unwrap().modbus_address, 0x0800);
        assert_eq!(parse_brand_address(BrandProfile::DeltaDvpEs, "M512").unwrap().modbus_address, 0x2400);
        assert_eq!(parse_brand_address(BrandProfile::DeltaDvpEs, "M1535").unwrap().modbus_address, 0x27FF);
        // Y17(八进制=15) → 线圈 0x050F;X10(8) → 离散输入 0x0508
        assert_eq!(parse_brand_address(BrandProfile::DeltaDvpEs, "Y17").unwrap().modbus_address, 0x050F);
        let x = parse_brand_address(BrandProfile::DeltaDvpEs, "X10").unwrap();
        assert_eq!((x.area, x.modbus_address), (BrandArea::DiscreteInput, 0x0508));
        // Y0 → 0x0500
        assert_eq!(parse_brand_address(BrandProfile::DeltaDvpEs, "Y0").unwrap().modbus_address, 0x0500);
    }

    #[test]
    fn unsupported_segments_return_manual_not_guess() {
        // T/C/S 与超范围段:明确报 MANUAL,不猜
        for addr in ["T0", "C50", "S3", "D2000", "M2000", "Y200"] {
            let e = parse_brand_address(BrandProfile::DeltaDvpEs, addr).unwrap_err();
            let code = match e { CoreError::Modbus { code, .. } => code, _ => panic!() };
            assert!(code == "BRAND_MAP_MANUAL" || code == "BRAND_ADDRESS_INVALID", "{addr} → {code}");
        }
    }

    #[test]
    fn invalid_input_rejected() {
        assert!(parse_brand_address(BrandProfile::DeltaDvpEs, "").is_err());
        assert!(parse_brand_address(BrandProfile::DeltaDvpEs, "Z10").is_err());
        assert!(parse_brand_address(BrandProfile::DeltaDvpEs, "M").is_err());
    }
}
