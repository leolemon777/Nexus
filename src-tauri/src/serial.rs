use std::{path::PathBuf, sync::Arc};

use serde::{Deserialize, Serialize};
use serial2_tokio::{CharSize, FlowControl, Parity, SerialPort, Settings, StopBits};
use tauri::State;
use thiserror::Error;
use tokio::sync::Mutex;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct SerialConfig {
    pub port_name: String,
    pub baud_rate: u32,
    pub data_bits: u8,
    pub parity: String,
    pub stop_bits: String,
    pub flow_control: String,
    pub read_timeout_ms: u64,
    pub write_timeout_ms: u64,
    pub dtr_mode: String,
    pub rts_mode: String,
}

impl SerialConfig {
    fn validate(&self) -> Result<(), SerialError> {
        if self.port_name.trim().is_empty() {
            return Err(SerialError::InvalidConfig("串口名称不能为空".into()));
        }
        let normalized_port = self.port_name.trim().to_ascii_uppercase();
        let is_windows_com = normalized_port
            .strip_prefix("COM")
            .and_then(|suffix| suffix.parse::<u32>().ok())
            .is_some_and(|number| number > 0);
        if !is_windows_com {
            return Err(SerialError::InvalidConfig(
                "Windows 串口名称必须采用 COM 加正整数的格式".into(),
            ));
        }
        if self.baud_rate == 0 || self.baud_rate > 12_000_000 {
            return Err(SerialError::InvalidConfig(
                "波特率必须在 1 到 12000000 之间".into(),
            ));
        }
        if !matches!(self.data_bits, 5..=8) {
            return Err(SerialError::InvalidConfig(
                "数据位必须是 5、6、7 或 8".into(),
            ));
        }
        if !matches!(
            self.parity.as_str(),
            "none" | "odd" | "even" | "mark" | "space"
        ) {
            return Err(SerialError::InvalidConfig("未知的奇偶校验方式".into()));
        }
        if !matches!(self.stop_bits.as_str(), "1" | "1.5" | "2") {
            return Err(SerialError::InvalidConfig(
                "停止位必须是 1、1.5 或 2".into(),
            ));
        }
        if !matches!(self.flow_control.as_str(), "none" | "rts-cts" | "xon-xoff") {
            return Err(SerialError::InvalidConfig("未知的流控方式".into()));
        }
        if !(1..=600_000).contains(&self.read_timeout_ms)
            || !(1..=600_000).contains(&self.write_timeout_ms)
        {
            return Err(SerialError::InvalidConfig(
                "读写超时必须在 1 到 600000 毫秒之间".into(),
            ));
        }
        if !matches!(self.dtr_mode.as_str(), "preserve" | "high" | "low") {
            return Err(SerialError::InvalidConfig("未知的 DTR 控制方式".into()));
        }
        if !matches!(self.rts_mode.as_str(), "preserve" | "high" | "low") {
            return Err(SerialError::InvalidConfig("未知的 RTS 控制方式".into()));
        }
        Ok(())
    }
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SerialPortSummary {
    pub name: String,
    pub display_name: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SerialStatus {
    pub is_open: bool,
    pub config: Option<SerialConfig>,
}

#[derive(Debug, Error)]
enum SerialError {
    #[error("参数错误：{0}")]
    InvalidConfig(String),
    #[error("当前串口后端暂不支持 {0}；该参数已保留，将由 Windows 原生适配层补齐")]
    UnsupportedSetting(String),
    #[error("串口已经打开，请先关闭当前连接")]
    AlreadyOpen,
    #[error("串口操作失败：{0}")]
    Io(#[from] std::io::Error),
    #[error("串口后台任务失败：{0}")]
    Join(String),
}

impl Serialize for SerialError {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        serializer.serialize_str(&self.to_string())
    }
}

struct OpenSerial {
    #[allow(dead_code)]
    port: Arc<SerialPort>,
    config: SerialConfig,
}

#[derive(Default)]
pub struct SerialState {
    connection: Mutex<Option<OpenSerial>>,
}

fn display_name(path: &PathBuf) -> String {
    let name = path.to_string_lossy().into_owned();
    format!("{name} · 串口设备")
}

#[tauri::command]
pub async fn list_serial_ports() -> Result<Vec<SerialPortSummary>, SerialError> {
    let paths = tauri::async_runtime::spawn_blocking(SerialPort::available_ports)
        .await
        .map_err(|error| SerialError::Join(error.to_string()))??;

    let mut ports: Vec<_> = paths
        .into_iter()
        .map(|path| SerialPortSummary {
            name: path.to_string_lossy().into_owned(),
            display_name: display_name(&path),
        })
        .collect();
    ports.sort_by(|left, right| natural_port_key(&left.name).cmp(&natural_port_key(&right.name)));
    Ok(ports)
}

#[tauri::command]
pub async fn get_serial_status(state: State<'_, SerialState>) -> Result<SerialStatus, SerialError> {
    let connection = state.connection.lock().await;
    Ok(status_from(&connection))
}

fn validate_backend_support(config: &SerialConfig) -> Result<(), SerialError> {
    if matches!(config.parity.as_str(), "mark" | "space") {
        return Err(SerialError::UnsupportedSetting(format!(
            "{} 校验",
            config.parity
        )));
    }
    if config.stop_bits == "1.5" {
        return Err(SerialError::UnsupportedSetting("1.5 停止位".into()));
    }
    Ok(())
}

fn ensure_can_open(is_connected: bool) -> Result<(), SerialError> {
    if is_connected {
        Err(SerialError::AlreadyOpen)
    } else {
        Ok(())
    }
}

#[tauri::command]
pub async fn open_serial_port(
    config: SerialConfig,
    state: State<'_, SerialState>,
) -> Result<SerialStatus, SerialError> {
    config.validate()?;
    validate_backend_support(&config)?;

    let mut connection = state.connection.lock().await;
    ensure_can_open(connection.is_some())?;

    let open_config = config.clone();
    let port = tauri::async_runtime::spawn_blocking(move || {
        SerialPort::open(&open_config.port_name, |mut settings: Settings| {
            settings.set_raw();
            settings.set_baud_rate(open_config.baud_rate)?;
            settings.set_char_size(match open_config.data_bits {
                5 => CharSize::Bits5,
                6 => CharSize::Bits6,
                7 => CharSize::Bits7,
                _ => CharSize::Bits8,
            });
            settings.set_parity(match open_config.parity.as_str() {
                "odd" => Parity::Odd,
                "even" => Parity::Even,
                _ => Parity::None,
            });
            settings.set_stop_bits(if open_config.stop_bits == "2" {
                StopBits::Two
            } else {
                StopBits::One
            });
            settings.set_flow_control(match open_config.flow_control.as_str() {
                "rts-cts" => FlowControl::RtsCts,
                "xon-xoff" => FlowControl::XonXoff,
                _ => FlowControl::None,
            });
            Ok(settings)
        })
    })
    .await
    .map_err(|error| SerialError::Join(error.to_string()))??;

    match config.dtr_mode.as_str() {
        "high" => port.set_dtr(true)?,
        "low" => port.set_dtr(false)?,
        _ => {}
    }
    if config.flow_control != "rts-cts" {
        match config.rts_mode.as_str() {
            "high" => port.set_rts(true)?,
            "low" => port.set_rts(false)?,
            _ => {}
        }
    }

    *connection = Some(OpenSerial {
        port: Arc::new(port),
        config,
    });
    Ok(status_from(&connection))
}

#[tauri::command]
pub async fn close_serial_port(state: State<'_, SerialState>) -> Result<SerialStatus, SerialError> {
    let mut connection = state.connection.lock().await;
    Ok(close_connection(&mut connection))
}

fn close_connection(connection: &mut Option<OpenSerial>) -> SerialStatus {
    *connection = None;
    status_from(connection)
}

fn status_from(connection: &Option<OpenSerial>) -> SerialStatus {
    status_from_config(connection.as_ref().map(|open| &open.config))
}

fn status_from_config(config: Option<&SerialConfig>) -> SerialStatus {
    SerialStatus {
        is_open: config.is_some(),
        config: config.cloned(),
    }
}

fn natural_port_key(name: &str) -> (u8, u32, String) {
    let uppercase = name.trim().to_ascii_uppercase();
    let number = uppercase
        .strip_prefix("COM")
        .and_then(|suffix| suffix.parse::<u32>().ok());
    match number {
        Some(number) => (0, number, uppercase),
        None => (1, u32::MAX, uppercase),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn valid_config() -> SerialConfig {
        SerialConfig {
            port_name: "COM3".into(),
            baud_rate: 9_600,
            data_bits: 8,
            parity: "none".into(),
            stop_bits: "1".into(),
            flow_control: "none".into(),
            read_timeout_ms: 1_000,
            write_timeout_ms: 1_000,
            dtr_mode: "preserve".into(),
            rts_mode: "preserve".into(),
        }
    }

    fn assert_invalid_config(config: SerialConfig, expected_message: &str) {
        match config.validate() {
            Err(SerialError::InvalidConfig(message)) => assert_eq!(message, expected_message),
            result => panic!("expected InvalidConfig, got {result:?}"),
        }
    }

    fn assert_unsupported(config: SerialConfig, expected_setting: &str) {
        assert!(config.validate().is_ok());
        match validate_backend_support(&config) {
            Err(SerialError::UnsupportedSetting(setting)) => {
                assert_eq!(setting, expected_setting);
            }
            result => panic!("expected UnsupportedSetting, got {result:?}"),
        }
    }

    #[test]
    fn accepts_a_valid_serial_configuration() {
        assert!(valid_config().validate().is_ok());
    }

    #[test]
    fn validates_port_name_boundary() {
        let mut config = valid_config();
        config.port_name = " \t\r\n ".into();
        assert_invalid_config(config, "串口名称不能为空");

        let mut config = valid_config();
        config.port_name = " COM3 ".into();
        assert!(config.validate().is_ok());

        let mut config = valid_config();
        config.port_name = "COM0".into();
        assert_invalid_config(config, "Windows 串口名称必须采用 COM 加正整数的格式");

        let mut config = valid_config();
        config.port_name = "ttyUSB0".into();
        assert_invalid_config(config, "Windows 串口名称必须采用 COM 加正整数的格式");
    }

    #[test]
    fn validates_baud_rate_boundaries() {
        for baud_rate in [1, 12_000_000] {
            let mut config = valid_config();
            config.baud_rate = baud_rate;
            assert!(config.validate().is_ok(), "baud rate {baud_rate}");
        }

        for baud_rate in [0, 12_000_001] {
            let mut config = valid_config();
            config.baud_rate = baud_rate;
            assert_invalid_config(config, "波特率必须在 1 到 12000000 之间");
        }
    }

    #[test]
    fn validates_data_bit_boundaries() {
        for data_bits in 5..=8 {
            let mut config = valid_config();
            config.data_bits = data_bits;
            assert!(config.validate().is_ok(), "data bits {data_bits}");
        }

        for data_bits in [4, 9] {
            let mut config = valid_config();
            config.data_bits = data_bits;
            assert_invalid_config(config, "数据位必须是 5、6、7 或 8");
        }
    }

    #[test]
    fn validates_all_domain_parity_values() {
        for parity in ["none", "odd", "even", "mark", "space"] {
            let mut config = valid_config();
            config.parity = parity.into();
            assert!(config.validate().is_ok(), "parity {parity}");
        }

        let mut config = valid_config();
        config.parity = "unknown".into();
        assert_invalid_config(config, "未知的奇偶校验方式");
    }

    #[test]
    fn validates_all_domain_stop_bit_values() {
        for stop_bits in ["1", "1.5", "2"] {
            let mut config = valid_config();
            config.stop_bits = stop_bits.into();
            assert!(config.validate().is_ok(), "stop bits {stop_bits}");
        }

        let mut config = valid_config();
        config.stop_bits = "3".into();
        assert_invalid_config(config, "停止位必须是 1、1.5 或 2");
    }

    #[test]
    fn validates_all_flow_control_values() {
        for flow_control in ["none", "rts-cts", "xon-xoff"] {
            let mut config = valid_config();
            config.flow_control = flow_control.into();
            assert!(config.validate().is_ok(), "flow control {flow_control}");
        }

        let mut config = valid_config();
        config.flow_control = "unknown".into();
        assert_invalid_config(config, "未知的流控方式");
    }

    #[test]
    fn validates_timeout_boundaries() {
        for timeout in [1, 600_000] {
            let mut config = valid_config();
            config.read_timeout_ms = timeout;
            config.write_timeout_ms = timeout;
            assert!(config.validate().is_ok(), "timeout {timeout}");
        }

        for timeout in [0, 600_001] {
            let mut config = valid_config();
            config.read_timeout_ms = timeout;
            assert_invalid_config(config, "读写超时必须在 1 到 600000 毫秒之间");

            let mut config = valid_config();
            config.write_timeout_ms = timeout;
            assert_invalid_config(config, "读写超时必须在 1 到 600000 毫秒之间");
        }
    }

    #[test]
    fn preserves_all_line_control_modes() {
        for dtr_mode in ["preserve", "high", "low"] {
            for rts_mode in ["preserve", "high", "low"] {
                let mut config = valid_config();
                config.dtr_mode = dtr_mode.into();
                config.rts_mode = rts_mode.into();
                assert!(config.validate().is_ok());
            }
        }

        let mut config = valid_config();
        config.dtr_mode = "toggle".into();
        assert_invalid_config(config, "未知的 DTR 控制方式");

        let mut config = valid_config();
        config.rts_mode = "toggle".into();
        assert_invalid_config(config, "未知的 RTS 控制方式");
    }

    #[test]
    fn backend_explicitly_rejects_domain_valid_mark_and_space_parity() {
        for parity in ["mark", "space"] {
            let mut config = valid_config();
            config.parity = parity.into();
            assert_unsupported(config, &format!("{parity} 校验"));
        }
    }

    #[test]
    fn backend_explicitly_rejects_domain_valid_one_point_five_stop_bits() {
        let mut config = valid_config();
        config.stop_bits = "1.5".into();
        assert_unsupported(config, "1.5 停止位");
    }

    #[test]
    fn backend_accepts_currently_implemented_formats() {
        for parity in ["none", "odd", "even"] {
            for stop_bits in ["1", "2"] {
                let mut config = valid_config();
                config.parity = parity.into();
                config.stop_bits = stop_bits.into();
                assert!(validate_backend_support(&config).is_ok());
            }
        }
    }

    #[test]
    fn serial_error_messages_are_stable() {
        assert_eq!(
            SerialError::InvalidConfig("测试参数".into()).to_string(),
            "参数错误：测试参数"
        );
        assert_eq!(
            SerialError::UnsupportedSetting("mark 校验".into()).to_string(),
            "当前串口后端暂不支持 mark 校验；该参数已保留，将由 Windows 原生适配层补齐"
        );
        assert_eq!(
            SerialError::AlreadyOpen.to_string(),
            "串口已经打开，请先关闭当前连接"
        );
    }

    #[test]
    fn status_model_is_testable_without_real_hardware() {
        let disconnected = status_from_config(None);
        assert!(!disconnected.is_open);
        assert_eq!(disconnected.config, None);

        let config = valid_config();
        let connected = status_from_config(Some(&config));
        assert!(connected.is_open);
        assert_eq!(connected.config, Some(config));
    }

    #[test]
    fn duplicate_open_policy_is_testable_without_real_hardware() {
        assert!(ensure_can_open(false).is_ok());
        assert!(matches!(
            ensure_can_open(true),
            Err(SerialError::AlreadyOpen)
        ));
    }

    #[test]
    fn closing_an_already_closed_connection_is_idempotent() {
        let mut connection = None;
        let first = close_connection(&mut connection);
        let second = close_connection(&mut connection);
        assert!(!first.is_open);
        assert!(!second.is_open);
        assert!(connection.is_none());
    }

    #[test]
    fn serial_ports_sort_by_numeric_suffix() {
        let mut ports = ["COM10", "COM2", "COM1"];
        ports.sort_by_key(|port| natural_port_key(port));
        assert_eq!(ports, ["COM1", "COM2", "COM10"]);
    }
}
