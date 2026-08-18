use serde::{Deserialize, Serialize};

use crate::error::CoreError;

#[derive(Debug, Clone, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SerialConfig {
    pub port_name: String,
    pub baud_rate: u32,
    pub data_bits: u8,
    pub parity: String,
    pub stop_bits: String,
    pub flow_control: String,
    pub read_timeout_ms: u32,
    pub write_timeout_ms: u32,
    #[serde(default = "preserve_line_mode")]
    pub dtr_mode: String,
    #[serde(default = "preserve_line_mode")]
    pub rts_mode: String,
}

impl SerialConfig {
    pub fn validate_and_normalize(mut self) -> Result<Self, CoreError> {
        self.port_name = self.port_name.trim().to_string();
        self.parity = self.parity.trim().to_ascii_lowercase();
        self.stop_bits = self.stop_bits.trim().to_string();
        self.flow_control = self.flow_control.trim().to_ascii_lowercase();
        self.dtr_mode = self.dtr_mode.trim().to_ascii_lowercase();
        self.rts_mode = self.rts_mode.trim().to_ascii_lowercase();

        if !is_windows_com_port(&self.port_name) {
            return Err(invalid(
                "portName",
                "串口名称必须是有效的 Windows COM 端口，例如 COM3",
            ));
        }
        if !(1..=12_000_000).contains(&self.baud_rate) {
            return Err(invalid("baudRate", "波特率必须在 1 到 12000000 之间"));
        }
        if !(5..=8).contains(&self.data_bits) {
            return Err(invalid("dataBits", "数据位只允许 5、6、7 或 8"));
        }
        if !matches!(self.parity.as_str(), "none" | "odd" | "even") {
            return Err(invalid("parity", "当前传输层只支持 none、odd 或 even"));
        }
        if !matches!(self.stop_bits.as_str(), "1" | "2") {
            return Err(invalid("stopBits", "当前传输层只支持 1 或 2 个停止位"));
        }
        if !matches!(self.flow_control.as_str(), "none" | "rts-cts" | "xon-xoff") {
            return Err(invalid("flowControl", "流控参数无效"));
        }
        if !(1..=600_000).contains(&self.read_timeout_ms) {
            return Err(invalid(
                "readTimeoutMs",
                "读取超时必须在 1 到 600000 毫秒之间",
            ));
        }
        if !(1..=600_000).contains(&self.write_timeout_ms) {
            return Err(invalid(
                "writeTimeoutMs",
                "写入超时必须在 1 到 600000 毫秒之间",
            ));
        }
        if !matches!(self.dtr_mode.as_str(), "preserve" | "high" | "low") {
            return Err(invalid("dtrMode", "DTR 控制模式无效"));
        }
        if !matches!(self.rts_mode.as_str(), "preserve" | "high" | "low" | "auto-toggle") {
            return Err(invalid("rtsMode", "RTS 控制模式无效"));
        }
        if self.flow_control == "rts-cts" && self.rts_mode != "preserve" {
            return Err(invalid(
                "rtsMode",
                "启用 RTS/CTS 流控时，RTS 必须由驱动管理",
            ));
        }

        Ok(self)
    }
}

fn invalid(field: &'static str, message: impl Into<String>) -> CoreError {
    CoreError::InvalidSerialConfig {
        field,
        message: message.into(),
    }
}

fn is_windows_com_port(value: &str) -> bool {
    let upper = value.to_ascii_uppercase();
    let Some(suffix) = upper.strip_prefix("COM") else {
        return false;
    };
    !suffix.is_empty()
        && suffix.bytes().all(|byte| byte.is_ascii_digit())
        && suffix.parse::<u32>().is_ok_and(|number| number > 0)
}

fn preserve_line_mode() -> String {
    "preserve".to_owned()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid_config() -> SerialConfig {
        SerialConfig {
            port_name: " com3 ".into(),
            baud_rate: 9_600,
            data_bits: 8,
            parity: " NONE ".into(),
            stop_bits: "1".into(),
            flow_control: "none".into(),
            read_timeout_ms: 1_000,
            write_timeout_ms: 1_000,
            dtr_mode: "preserve".into(),
            rts_mode: "preserve".into(),
        }
    }

    #[test]
    fn normalizes_a_safe_configuration() {
        let config = valid_config().validate_and_normalize().unwrap();
        assert_eq!(config.port_name, "com3");
        assert_eq!(config.parity, "none");
    }

    #[test]
    fn rejects_manual_rts_with_hardware_flow_control() {
        let mut config = valid_config();
        config.flow_control = "rts-cts".into();
        config.rts_mode = "high".into();
        assert!(matches!(
            config.validate_and_normalize(),
            Err(CoreError::InvalidSerialConfig {
                field: "rtsMode",
                ..
            })
        ));
    }
}
