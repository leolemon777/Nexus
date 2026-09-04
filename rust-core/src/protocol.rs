use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use crate::{
    PROTOCOL_VERSION,
    error::CoreError,
    modbus_ascii, modbus_pdu as pdu,
    modbus_rtu::{
        self, RtuError, build_read_holding_registers_request, build_read_input_registers_request,
        crc16_modbus, modbus_exception_name, parse_read_holding_registers_response,
        parse_read_input_registers_response,
    },
    serial_config::SerialConfig,
    session::Session,
};

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct RequestEnvelope {
    protocol_version: u16,
    request_id: String,
    command: String,
    payload: Value,
}

#[derive(Debug, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResponseEnvelope {
    pub protocol_version: u16,
    pub request_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stream_id: Option<String>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub stream_end: Option<bool>,
    pub ok: bool,
    pub result: Option<Value>,
    pub error: Option<crate::error::ErrorBody>,
}

#[derive(Debug)]
pub struct CommandOutcome {
    pub response: ResponseEnvelope,
    pub shutdown: bool,
}

// =============================================================================
// Payload 结构(按命令分组)
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct ValidateSerialConfigPayload {
    config: Value,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildReadRegistersPayload {
    unit_id: u8,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseReadRegistersPayload {
    response: Vec<u8>,
    unit_id: u8,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildReadBitsPayload {
    unit_id: u8,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseReadBitsPayload {
    response: Vec<u8>,
    unit_id: u8,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteSingleCoilPayload {
    unit_id: u8,
    address: u16,
    value: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteSingleRegisterPayload {
    unit_id: u8,
    address: u16,
    value: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteMultipleCoilsPayload {
    unit_id: u8,
    address: u16,
    values: Vec<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BuildWriteMultipleRegistersPayload {
    unit_id: u8,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseWriteResponsePayload {
    response: Vec<u8>,
    unit_id: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenConnectionPayload {
    connection_id: String,
    host: String,
    port: u16,
    unit_id: u8,
    #[serde(default = "default_framing")]
    framing: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenGeSrtpConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_ge_srtp_port")]
    port: u16,
}

fn default_ge_srtp_port() -> u16 {
    crate::ge_srtp::DEFAULT_PORT
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct GeSrtpReadPayload {
    connection_id: String,
    address: String,
    element_count: u16,
    #[serde(default)]
    bit_access: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenFujiSphConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_fuji_sph_port")]
    port: u16,
    #[serde(default = "default_fuji_sph_connection_id")]
    connection_id_byte: u8,
}

fn default_fuji_sph_port() -> u16 {
    crate::fuji_sph::DEFAULT_PORT
}

fn default_fuji_sph_connection_id() -> u8 {
    0xFE
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FujiSphReadPayload {
    connection_id: String,
    address: String,
    words: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenFatekConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_fatek_port")]
    port: u16,
    #[serde(default = "default_fatek_station")]
    station: u8,
}

fn default_fatek_port() -> u16 {
    5000
}

fn default_fatek_station() -> u8 {
    1
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FatekReadPayload {
    connection_id: String,
    address: String,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenKeyenceConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_keyence_port")]
    port: u16,
    #[serde(default)]
    use_station: bool,
    #[serde(default)]
    station: u8,
}

fn default_keyence_port() -> u16 {
    crate::keyence::DEFAULT_PORT
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KeyenceReadPayload {
    connection_id: String,
    address: String,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenLsXgtConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_ls_xgt_port")]
    port: u16,
    #[serde(default = "default_ls_xgt_cpu")]
    cpu: u8,
    #[serde(default)]
    base_no: u8,
    #[serde(default = "default_ls_xgt_slot_no")]
    slot_no: u8,
    #[serde(default = "default_ls_xgt_company_id")]
    company_id: String,
}

fn default_ls_xgt_port() -> u16 {
    crate::ls_xgt::DEFAULT_PORT
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LsXgtReadPayload {
    connection_id: String,
    variable_name: String,
    #[serde(default = "default_ls_xgt_data_type")]
    data_type: u8,
}

fn default_ls_xgt_data_type() -> u8 {
    crate::ls_xgt::INDIVIDUAL_WORD
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LsXgtContinuousReadPayload {
    connection_id: String,
    variable_name: String,
    byte_count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenAdsConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_ads_port")]
    port: u16,
    #[serde(default = "default_ads_target_net_id")]
    target_net_id: String,
    #[serde(default = "default_ads_target_port")]
    target_port: u16,
    #[serde(default = "default_ads_source_net_id")]
    source_net_id: String,
    #[serde(default = "default_ads_source_port")]
    source_port: u16,
}

fn default_ads_port() -> u16 {
    crate::ads::ADS_TCP_PORT
}

fn default_ads_target_net_id() -> String {
    "5.72.144.1.1.1".to_string()
}

fn default_ads_target_port() -> u16 {
    851
}

fn default_ads_source_net_id() -> String {
    "5.72.144.2.1.1".to_string()
}

fn default_ads_source_port() -> u16 {
    32905
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct AdsReadPayload {
    connection_id: String,
    index_group: u32,
    index_offset: u32,
    read_length: u32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenEnipConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_enip_port")]
    port: u16,
}

fn default_enip_port() -> u16 {
    44818
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct EnipReadTagPayload {
    connection_id: String,
    tag: String,
    #[serde(default = "default_enip_elements")]
    elements: u16,
}

fn default_enip_elements() -> u16 {
    1
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenMqttConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_mqtt_port")]
    port: u16,
    #[serde(default = "default_mqtt_client_id")]
    client_id: String,
    #[serde(default = "default_mqtt_keep_alive")]
    keep_alive: u16,
    #[serde(default = "default_mqtt_clean_session")]
    clean_session: bool,
}

fn default_mqtt_port() -> u16 {
    crate::mqtt::DEFAULT_PORT
}

fn default_mqtt_client_id() -> String {
    "nexus-readonly".to_string()
}

fn default_mqtt_keep_alive() -> u16 {
    30
}

fn default_mqtt_clean_session() -> bool {
    true
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MqttSubscribePayload {
    connection_id: String,
    topic_filter: String,
    #[serde(default)]
    qos: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MqttConnectionIdPayload {
    connection_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MqttBuildConnectPayload {
    #[serde(default = "default_mqtt_client_id")]
    client_id: String,
    #[serde(default = "default_mqtt_keep_alive")]
    keep_alive: u16,
    #[serde(default = "default_mqtt_clean_session")]
    clean_session: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MqttBuildSubscribePayload {
    packet_id: u16,
    topic_filter: String,
    #[serde(default)]
    qos: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct MqttFramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenIec104ConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_iec104_port")]
    port: u16,
    #[serde(default = "default_iec104_common_address")]
    common_address: u16,
    #[serde(default)]
    originator_address: u8,
}

fn default_iec104_port() -> u16 {
    crate::iec104::DEFAULT_PORT
}

fn default_iec104_common_address() -> u16 {
    1
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104ConnectionIdPayload {
    connection_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104InterrogationPayload {
    connection_id: String,
    #[serde(default)]
    group: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104BuildIFramePayload {
    send_sequence: u16,
    receive_sequence: u16,
    asdu: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104BuildSFramePayload {
    receive_sequence: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104BuildUFramePayload {
    function: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104FramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104AsduPayload {
    asdu: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Iec104BuildInterrogationPayload {
    #[serde(default = "default_iec104_common_address")]
    common_address: u16,
    #[serde(default)]
    group: u8,
    #[serde(default)]
    originator_address: u8,
    #[serde(default)]
    send_sequence: u16,
    #[serde(default)]
    receive_sequence: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenDnp3ConnectionPayload {
    connection_id: String,
    host: String,
    #[serde(default = "default_dnp3_port")]
    port: u16,
    #[serde(default = "default_dnp3_master_address")]
    master_address: u16,
    #[serde(default = "default_dnp3_outstation_address")]
    outstation_address: u16,
}

fn default_dnp3_port() -> u16 {
    crate::dnp3::DEFAULT_PORT
}

fn default_dnp3_master_address() -> u16 {
    crate::dnp3::DEFAULT_MASTER_ADDRESS
}

fn default_dnp3_outstation_address() -> u16 {
    crate::dnp3::DEFAULT_OUTSTATION_ADDRESS
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3ConnectionIdPayload {
    connection_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3ClassScanPayload {
    connection_id: String,
    #[serde(default)]
    class0: bool,
    #[serde(default)]
    class1: bool,
    #[serde(default)]
    class2: bool,
    #[serde(default)]
    class3: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3ReadPayload {
    connection_id: String,
    group: u8,
    variation: u8,
    #[serde(default)]
    start: Option<u16>,
    #[serde(default)]
    stop: Option<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3BuildLinkPayload {
    control: u8,
    destination: u16,
    source: u16,
    #[serde(default)]
    user_data: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3FramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3BuildClassScanPayload {
    #[serde(default)]
    sequence: u8,
    #[serde(default)]
    class0: bool,
    #[serde(default)]
    class1: bool,
    #[serde(default)]
    class2: bool,
    #[serde(default)]
    class3: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3BuildReadPayload {
    #[serde(default)]
    sequence: u8,
    group: u8,
    variation: u8,
    #[serde(default)]
    start: Option<u16>,
    #[serde(default)]
    stop: Option<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3ApplicationPayload {
    pdu: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dnp3ConfirmPayload {
    sequence: u8,
    #[serde(default)]
    unsolicited: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dlt645AddressPayload {
    address: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dlt645DataIdPayload {
    version: String,
    data_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dlt645BuildReadPayload {
    version: String,
    address: String,
    data_id: String,
    #[serde(default = "default_dlt645_preamble_count")]
    preamble_count: u8,
}

fn default_dlt645_preamble_count() -> u8 {
    crate::dlt645::MAX_PREAMBLE_COUNT
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dlt645FramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Dlt645ParseReadPayload {
    version: String,
    address: String,
    data_id: String,
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188MeterTypePayload {
    meter_type: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188AddressPayload {
    address: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188DataIdPayload {
    data_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188BuildReadPayload {
    meter_type: String,
    address: String,
    data_id: String,
    #[serde(default = "default_cjt188_sequence")]
    sequence: u8,
    #[serde(default)]
    preamble_count: u8,
}

fn default_cjt188_sequence() -> u8 {
    0x01
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188FramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct Cjt188ParseReadPayload {
    meter_type: String,
    address: String,
    data_id: String,
    #[serde(default = "default_cjt188_sequence")]
    sequence: u8,
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpWhoIsPayload {
    #[serde(default)]
    low_limit: Option<u32>,
    #[serde(default)]
    high_limit: Option<u32>,
    #[serde(default = "default_bacnet_broadcast")]
    broadcast: bool,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpIamPayload {
    device_instance: u32,
    max_apdu: u32,
    segmentation: String,
    vendor_id: u16,
    #[serde(default = "default_bacnet_broadcast")]
    broadcast: bool,
}

fn default_bacnet_broadcast() -> bool {
    true
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpFramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpReadPropertyRequestPayload {
    object_type: u16,
    object_instance: u32,
    property_identifier: u32,
    #[serde(default)]
    property_array_index: Option<u32>,
    invoke_id: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpReadPropertyFramePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpReadPropertyAckPayload {
    frame: Vec<u8>,
    #[serde(default)]
    expected_request: Option<BacnetIpReadPropertyRequestPayload>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxGroupAddressPayload {
    address: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxConnectRequestPayload {
    local_ip: String,
    local_port: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxConnectResponsePayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxGroupReadPayload {
    channel_id: u8,
    sequence: u8,
    address: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxTunnelingAckPayload {
    channel_id: u8,
    sequence: u8,
    status: u8,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxTunnelingRequestPayload {
    frame: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxOpenLivePayload {
    connection_id: String,
    host: String,
    port: u16,
    #[serde(default = "default_knx_live_timeout")]
    timeout_ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxGroupReadLivePayload {
    connection_id: String,
    address: String,
    #[serde(default = "default_knx_live_timeout")]
    timeout_ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxDisconnectLivePayload {
    connection_id: String,
    #[serde(default = "default_knx_live_timeout")]
    timeout_ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxConnectionStatePayload {
    connection_id: String,
    #[serde(default = "default_knx_live_timeout")]
    timeout_ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct KnxKeepalivePayload {
    connection_id: String,
    #[serde(default = "default_knx_keepalive_interval")]
    interval_ms: u32,
}

fn default_knx_keepalive_interval() -> u32 {
    60_000
}

fn default_knx_live_timeout() -> u64 {
    1500
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpOpenPayload {
    connection_id: String,
    host: String,
    port: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpWhoisLivePayload {
    connection_id: String,
    #[serde(default)]
    low_limit: Option<u32>,
    #[serde(default)]
    high_limit: Option<u32>,
    #[serde(default = "default_bacnet_live_timeout")]
    timeout_ms: u64,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct BacnetIpReadPropertyLivePayload {
    connection_id: String,
    object_type: u16,
    object_instance: u32,
    property_identifier: u32,
    #[serde(default)]
    property_array_index: Option<u32>,
    #[serde(default = "default_bacnet_live_timeout")]
    timeout_ms: u64,
}

fn default_bacnet_live_timeout() -> u64 {
    1500
}

fn default_framing() -> String {
    "standard".to_string()
}

fn parse_framing(s: &str) -> Result<crate::session::TcpFraming, CoreError> {
    match s.to_lowercase().as_str() {
        "standard" | "tcp" => Ok(crate::session::TcpFraming::Standard),
        "rtu-over-tcp" | "rtuovertcp" => Ok(crate::session::TcpFraming::RtuOverTcp),
        "ascii-over-tcp" | "asciiovertcp" => Ok(crate::session::TcpFraming::AsciiOverTcp),
        _ => Err(CoreError::InvalidSerialConfig {
            field: "framing",
            message: format!("不支持的 framing 模式: {s}"),
        }),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CloseConnectionPayload {
    connection_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadPayload {
    connection_id: String,
    start_address: u16,
    quantity: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpWriteSinglePayload {
    connection_id: String,
    address: u16,
    value: Value,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpWriteMultiplePayload {
    connection_id: String,
    address: u16,
    values: Vec<Value>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpMaskWriteRegisterPayload {
    connection_id: String,
    address: u16,
    and_mask: u16,
    or_mask: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadWriteMultiplePayload {
    connection_id: String,
    read_address: u16,
    read_quantity: u16,
    write_address: u16,
    write_values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpReadDeviceIdPayload {
    connection_id: String,
    #[serde(default = "default_read_dev_id_code")]
    read_device_id_code: u8,
    #[serde(default)]
    object_id: u8,
}

fn default_read_dev_id_code() -> u8 {
    1
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct TcpDiagnosticsPayload {
    connection_id: String,
    sub_function: u8,
    #[serde(default = "default_diag_data")]
    data: u16,
}

fn default_diag_data() -> u16 {
    0
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct DecodeValuesPayload {
    registers: Vec<u16>,
    data_type: String,
    #[serde(default)]
    offset: Option<usize>,
    #[serde(default)]
    count: Option<usize>,
    #[serde(default)]
    scale: Option<f64>,
    #[serde(default)]
    offset_value: Option<f64>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ScanStationIdsPayload {
    connection_id: String,
    #[serde(default = "default_scan_start")]
    range_start: u8,
    #[serde(default = "default_scan_end")]
    range_end: u8,
    #[serde(default = "default_scan_timeout")]
    timeout_ms: u32,
}

fn default_scan_start() -> u8 {
    1
}
fn default_scan_end() -> u8 {
    247
}
fn default_scan_timeout() -> u32 {
    500
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StartTcpSlavePayload {
    slave_id: String,
    port: u16,
    #[serde(default)]
    allowed_station_ids: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveIdPayload {
    slave_id: String,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveSetValuePayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveSetCoilPayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<bool>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveClearPayload {
    slave_id: String,
    #[serde(default)]
    area: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveGetMemoryPayload {
    slave_id: String,
    area: String,
    address: u16,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SlaveHandleSerialBytesPayload {
    slave_id: String,
    bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SerialSlaveSetValuePayload {
    slave_id: String,
    area: String,
    address: u16,
    values: Vec<u16>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct SerialSlaveGetMemoryPayload {
    slave_id: String,
    area: String,
    address: u16,
    count: u16,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ChecksumPayload {
    bytes: Vec<u8>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct ParseFrameOnlinePayload {
    bytes: Vec<u8>,
    #[serde(default = "default_transport")]
    transport: String,
}

fn default_transport() -> String {
    "rtu".to_string()
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StartPollStreamPayload {
    stream_id: String,
    connection_id: String,
    fc: u8,
    start_address: u16,
    quantity: u16,
    #[serde(default = "default_interval")]
    interval_ms: u32,
}

fn default_interval() -> u32 {
    1000
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct StopPollStreamPayload {
    stream_id: String,
}

// =============================================================================
// 核心分发
// =============================================================================

pub fn handle_line(session: &mut Session, line: &str) -> CommandOutcome {
    let raw: Value = match serde_json::from_str(line) {
        Ok(value) => value,
        Err(_) => return failure(None, CoreError::InvalidJson),
    };
    let request_id = raw
        .get("requestId")
        .and_then(Value::as_str)
        .map(str::to_owned);
    let request: RequestEnvelope = match serde_json::from_value(raw) {
        Ok(request) => request,
        Err(_) => return failure(request_id, CoreError::InvalidEnvelope),
    };

    if request.request_id.is_empty()
        || request.request_id.len() > 128
        || request.request_id.chars().any(char::is_control)
    {
        return failure(Some(request.request_id), CoreError::InvalidEnvelope);
    }
    if request.protocol_version != PROTOCOL_VERSION {
        return failure(
            Some(request.request_id),
            CoreError::UnsupportedProtocolVersion {
                received: request.protocol_version,
                supported: PROTOCOL_VERSION,
            },
        );
    }

    dispatch(
        session,
        &request.request_id,
        &request.command,
        request.payload,
    )
}

fn dispatch(
    session: &mut Session,
    request_id: &str,
    command: &str,
    payload: Value,
) -> CommandOutcome {
    match command {
        "hello" => {
            // 版本协商:v1 客户端发 protocolVersion=1;v2 客户端可发 clientVersion
            let client_version = payload
                .get("clientVersion")
                .and_then(Value::as_u64)
                .map(|v| v as u16);
            success(
                request_id.to_string(),
                json!({
                    "service": "nexus-rust-core",
                    "serviceVersion": env!("CARGO_PKG_VERSION"),
                    "protocolVersion": PROTOCOL_VERSION,
                    "supportedVersions": [1, 2],
                    "clientVersion": client_version,
                    "features": ["streaming"],
                    "capabilities": all_capabilities()
                }),
                false,
            )
        }
        "validate_serial_config" => handle_validate_serial_config(request_id, payload),
        // === 串口路径:RTU build/parse(向后兼容) ===
        "build_read_holding_registers" => {
            handle_build_read_registers_rtu(request_id, payload, 0x03)
        }
        "build_read_input_registers" => handle_build_read_registers_rtu(request_id, payload, 0x04),
        "parse_read_holding_registers" => {
            handle_parse_read_registers_rtu(request_id, payload, 0x03)
        }
        "parse_read_input_registers" => handle_parse_read_registers_rtu(request_id, payload, 0x04),
        // === 串口路径:RTU build/parse — 新增读位(FC01/FC02) ===
        "build_read_coils" => handle_build_read_bits_rtu(request_id, payload, 0x01),
        "build_read_discrete_inputs" => handle_build_read_bits_rtu(request_id, payload, 0x02),
        "parse_read_coils" => handle_parse_read_bits_rtu(request_id, payload, 0x01),
        "parse_read_discrete_inputs" => handle_parse_read_bits_rtu(request_id, payload, 0x02),
        // === 串口路径:RTU build/parse — 写操作(FC05/06/15/16) ===
        "build_write_single_coil" => handle_build_write_single_coil_rtu(request_id, payload),
        "build_write_single_register" => {
            handle_build_write_single_register_rtu(request_id, payload)
        }
        "build_write_multiple_coils" => handle_build_write_multiple_coils_rtu(request_id, payload),
        "build_write_multiple_registers" => {
            handle_build_write_multiple_registers_rtu(request_id, payload)
        }
        "parse_write_single_coil" => handle_parse_write_single_coil_rtu(request_id, payload),
        "parse_write_single_register" => {
            handle_parse_write_single_register_rtu(request_id, payload)
        }
        "parse_write_multiple_coils" => handle_parse_write_multiple_coils_rtu(request_id, payload),
        "parse_write_multiple_registers" => {
            handle_parse_write_multiple_registers_rtu(request_id, payload)
        }
        // === 串口路径:ASCII build/parse(FC01-06,15,16) ===
        "build_ascii_read_holding_registers" => {
            handle_build_read_registers_ascii(request_id, payload, 0x03)
        }
        "parse_ascii_read_holding_registers" => {
            handle_parse_read_registers_ascii(request_id, payload, 0x03)
        }
        "build_ascii_read_input_registers" => {
            handle_build_read_registers_ascii(request_id, payload, 0x04)
        }
        "parse_ascii_read_input_registers" => {
            handle_parse_read_registers_ascii(request_id, payload, 0x04)
        }
        "build_ascii_read_coils" => handle_build_read_bits_ascii(request_id, payload, 0x01),
        "parse_ascii_read_coils" => handle_parse_read_bits_ascii(request_id, payload, 0x01),
        "build_ascii_read_discrete_inputs" => {
            handle_build_read_bits_ascii(request_id, payload, 0x02)
        }
        "parse_ascii_read_discrete_inputs" => {
            handle_parse_read_bits_ascii(request_id, payload, 0x02)
        }
        "build_ascii_write_single_coil" => {
            handle_build_write_single_coil_ascii(request_id, payload)
        }
        "parse_ascii_write_single_coil" => {
            handle_parse_write_single_coil_ascii(request_id, payload)
        }
        "build_ascii_write_single_register" => {
            handle_build_write_single_register_ascii(request_id, payload)
        }
        "parse_ascii_write_single_register" => {
            handle_parse_write_single_register_ascii(request_id, payload)
        }
        "build_ascii_write_multiple_coils" => {
            handle_build_write_multiple_coils_ascii(request_id, payload)
        }
        "parse_ascii_write_multiple_coils" => {
            handle_parse_write_multiple_coils_ascii(request_id, payload)
        }
        "build_ascii_write_multiple_registers" => {
            handle_build_write_multiple_registers_ascii(request_id, payload)
        }
        "parse_ascii_write_multiple_registers" => {
            handle_parse_write_multiple_registers_ascii(request_id, payload)
        }
        // === TCP/UDP 路径:连接管理 ===
        "open_tcp_connection" => handle_open_tcp(session, request_id, payload),
        "open_udp_connection" => handle_open_udp(session, request_id, payload),
        "close_connection" => handle_close_connection(session, request_id, payload),
        // === TCP/UDP 路径:端到端读写 ===
        "tcp_read_coils" => handle_tcp_read_bits(session, request_id, payload, 0x01),
        "tcp_read_discrete_inputs" => handle_tcp_read_bits(session, request_id, payload, 0x02),
        "tcp_read_holding_registers" => {
            handle_tcp_read_registers(session, request_id, payload, 0x03)
        }
        "tcp_read_input_registers" => handle_tcp_read_registers(session, request_id, payload, 0x04),
        "tcp_write_single_coil" => handle_tcp_write_single_coil(session, request_id, payload),
        "tcp_write_single_register" => {
            handle_tcp_write_single_register(session, request_id, payload)
        }
        "tcp_write_multiple_coils" => handle_tcp_write_multiple_coils(session, request_id, payload),
        "tcp_write_multiple_registers" => {
            handle_tcp_write_multiple_registers(session, request_id, payload)
        }
        // === 高级 FC 端到端(FC22/23/43/08)===
        "tcp_mask_write_register" => handle_tcp_mask_write_register(session, request_id, payload),
        "tcp_read_write_multiple" => handle_tcp_read_write_multiple(session, request_id, payload),
        "tcp_read_device_id" => handle_tcp_read_device_id(session, request_id, payload),
        "tcp_diagnostics" => handle_tcp_diagnostics(session, request_id, payload),
        // === 诊断类 FC(FC07/11/12/17)— 仅串行线,但通过 TCP 也能发 ===
        "tcp_read_exception_status" => handle_tcp_simple_fc(session, request_id, payload, 0x07),
        "tcp_get_comm_event_counter" => handle_tcp_simple_fc(session, request_id, payload, 0x0B),
        "tcp_get_comm_event_log" => handle_tcp_simple_fc(session, request_id, payload, 0x0C),
        "tcp_report_slave_id" => handle_tcp_simple_fc(session, request_id, payload, 0x11),
        // === UDP 端到端读写(framing 由 open_udp_connection 决定)===
        "udp_read_coils" => handle_udp_read_bits(session, request_id, payload, 0x01),
        "udp_read_discrete_inputs" => handle_udp_read_bits(session, request_id, payload, 0x02),
        "udp_read_holding_registers" => {
            handle_udp_read_registers(session, request_id, payload, 0x03)
        }
        "udp_read_input_registers" => handle_udp_read_registers(session, request_id, payload, 0x04),
        "udp_write_single_coil" => handle_udp_write_single_coil(session, request_id, payload),
        "udp_write_single_register" => {
            handle_udp_write_single_register(session, request_id, payload)
        }
        "udp_write_multiple_coils" => handle_udp_write_multiple_coils(session, request_id, payload),
        "udp_write_multiple_registers" => {
            handle_udp_write_multiple_registers(session, request_id, payload)
        }
        // === 值解码(纯计算,对标 28 种显示格式)===
        "decode_values" => handle_decode_values(request_id, payload),
        // === 扫描(仅 TCP/UDP 连接,串口由 Electron 驱动)===
        "scan_station_ids" => handle_scan_station_ids(session, request_id, payload),
        // === 三菱 MC 协议 ===
        "mc_parse_address" => handle_mc_parse_address(request_id, payload),
        "mc_build_read" => handle_mc_build_read(request_id, payload),
        "mc_build_write" => handle_mc_build_write(request_id, payload),
        "mc_parse_response" => handle_mc_parse_response(request_id, payload),
        "open_mc_tcp_connection" => handle_open_mc_tcp(session, request_id, payload),
        "mc_tcp_read" => handle_mc_tcp_read(session, request_id, payload),
        "mc_tcp_write" => handle_mc_tcp_write(session, request_id, payload),
        "start_mc_tcp_slave" => handle_start_mc_tcp_slave(session, request_id, payload),
        "stop_mc_slave" => handle_stop_mc_slave(session, request_id, payload),
        "mc_slave_set" => handle_mc_slave_set(session, request_id, payload),
        // 三菱 MC 进阶(M2)
        "mc_tcp_read_random" => handle_mc_tcp_read_random(session, request_id, payload),
        "mc_tcp_write_random" => handle_mc_tcp_write_random(session, request_id, payload),
        "mc_tcp_read_blocks" => handle_mc_tcp_read_blocks(session, request_id, payload),
        "mc_remote_run" => handle_mc_remote(session, request_id, 0x1002, payload),
        "mc_remote_stop" => handle_mc_remote(session, request_id, 0x1006, payload),
        "mc_remote_reset" => handle_mc_remote(session, request_id, 0x1001, payload),
        "mc_remote_pause" => handle_mc_remote(session, request_id, 0x1003, payload),
        "mc_read_clock" => handle_mc_read_clock(session, request_id, payload),
        "mc_echo_test" => handle_mc_echo_test(session, request_id, payload),
        "mc_read_cpu_type" => handle_mc_cpu_info(session, request_id, "type", payload),
        "mc_read_cpu_status" => handle_mc_cpu_info(session, request_id, "status", payload),
        "mc_build_ascii_read" => handle_mc_build_ascii_read(request_id, payload),
        "open_mc_ascii_connection" => handle_open_mc_ascii(session, request_id, payload),
        "mc_ascii_read" => handle_mc_ascii_read(session, request_id, payload),
        "mc_ascii_write" => handle_mc_ascii_write(session, request_id, payload),
        // === 三菱 MC 串口 C24(3C/4C 离线组帧,§3.1)===
        "mc_serial_build_3c" => handle_mc_serial_build_3c(request_id, payload),
        "mc_serial_parse_3c" => handle_mc_serial_parse_3c(request_id, payload),
        "mc_c24_read" => handle_mc_c24_read(request_id, payload),
        "mc_c24_parse_read" => handle_mc_c24_parse_read(request_id, payload),
        // === 三菱 A-1E / SLMP-1E 帧(离线组帧+解析,§3.4)===
        "mc_1e_build_read" => handle_mc_1e_build_read(request_id, payload),
        "mc_1e_build_write" => handle_mc_1e_build_write(request_id, payload),
        "mc_1e_parse" => handle_mc_1e_parse(request_id, payload),
        "open_mc_udp_connection" => handle_open_mc_udp(session, request_id, payload),
        "mc_udp_read" => handle_mc_udp_read(session, request_id, payload),
        "mc_udp_write" => handle_mc_udp_write(session, request_id, payload),
        "open_mc_1e_tcp" => handle_open_mc_1e(session, request_id, payload),
        "mc_1e_read" => handle_mc_1e_read(session, request_id, payload),
        "mc_1e_write" => handle_mc_1e_write(session, request_id, payload),
        // === 三菱 FX 串口协议(Computer Link / 编程口)===
        "brand_parse_address" => handle_brand_parse_address(request_id, payload),
        "delta_parse_address" => handle_delta_parse_address(request_id, payload),
        "inovance_parse_address" => handle_inovance_parse_address(request_id, payload),
        "xinjie_parse_address" => handle_xinjie_parse_address(request_id, payload),
        // === FATEK FBs 原生 ASCII（TCP 只读会话 + 编解码）===
        "open_fatek_connection" => handle_open_fatek_connection(session, request_id, payload),
        "fatek_read_words" => handle_fatek_read_words(session, request_id, payload),
        "fatek_read_discrete" => handle_fatek_read_discrete(session, request_id, payload),
        "fatek_parse_address" => handle_fatek_parse_address(request_id, payload),
        "fatek_pack_command" => handle_fatek_pack_command(request_id, payload),
        "fatek_build_read_discrete" => handle_fatek_build_read_discrete(request_id, payload),
        "fatek_build_write_discrete" => handle_fatek_build_write_discrete(request_id, payload),
        "fatek_build_read_words" => handle_fatek_build_read_words(request_id, payload),
        "fatek_build_write_words" => handle_fatek_build_write_words(request_id, payload),
        "fatek_parse_response" => handle_fatek_parse_response(request_id, payload),
        // === Keyence KV Host Link ASCII（TCP 只读会话 + 编解码）===
        "open_keyence_connection" => handle_open_keyence_connection(session, request_id, payload),
        "keyence_read_words" => handle_keyence_read_words(session, request_id, payload),
        "keyence_read_bits" => handle_keyence_read_bits(session, request_id, payload),
        // === Fuji MICREX-SX SPH Loader Command（TCP 只读会话 + 编解码）===
        "open_fuji_sph_connection" => handle_open_fuji_sph_connection(session, request_id, payload),
        "fuji_sph_read" => handle_fuji_sph_read(session, request_id, payload),
        "fuji_sph_parse_address" => handle_fuji_sph_parse_address(request_id, payload),
        "fuji_sph_build_read" => handle_fuji_sph_build_read(request_id, payload),
        "fuji_sph_build_write" => handle_fuji_sph_build_write(request_id, payload),
        "fuji_sph_parse_response" => handle_fuji_sph_parse_response(request_id, payload),
        // === GE SRTP（TCP 只读会话 + 编解码）===
        "open_ge_srtp_connection" => handle_open_ge_srtp_connection(session, request_id, payload),
        "ge_srtp_read" => handle_ge_srtp_read(session, request_id, payload),
        "ge_srtp_parse_address" => handle_ge_srtp_parse_address(request_id, payload),
        "ge_srtp_build_handshake" => handle_ge_srtp_build_handshake(request_id, payload),
        "ge_srtp_parse_handshake" => handle_ge_srtp_parse_handshake(request_id, payload),
        "ge_srtp_build_read" => handle_ge_srtp_build_read(request_id, payload),
        "ge_srtp_build_write" => handle_ge_srtp_build_write(request_id, payload),
        "ge_srtp_parse_response" => handle_ge_srtp_parse_response(request_id, payload),
        "fins_parse_address" => handle_fins_parse_address(request_id, payload),
        "open_fins_tcp" => handle_open_fins_tcp(session, request_id, payload),
        "open_fins_udp" => handle_open_fins_udp(session, request_id, payload),
        "fins_read" => handle_fins_read(session, request_id, payload),
        "fins_write" => handle_fins_write(session, request_id, payload),
        "start_fins_slave" => handle_start_fins_slave(session, request_id, payload),
        "stop_fins_slave" => handle_stop_fins_slave(session, request_id, payload),
        "fins_slave_set" => handle_fins_slave_set(session, request_id, payload),
        "fins_slave_get" => handle_fins_slave_get(session, request_id, payload),
        "s7_parse_address" => handle_s7_parse_address(request_id, payload),
        "open_s7_connection" => handle_open_s7_connection(session, request_id, payload),
        "close_s7_connection" => handle_close_connection(session, request_id, payload),
        "s7_read" => handle_s7_read(session, request_id, payload),
        "s7_write" => handle_s7_write(session, request_id, payload),
        "start_s7_slave" => handle_start_s7_slave(session, request_id, payload),
        "stop_s7_slave" => handle_stop_s7_slave(session, request_id, payload),
        "s7_slave_set" => handle_s7_slave_set(session, request_id, payload),
        "s7_slave_get" => handle_s7_slave_get(session, request_id, payload),
        "s7_cpu_control" => handle_s7_cpu_control(session, request_id, payload),
        "s7_read_status" => handle_s7_read_status(session, request_id, payload),
        "s7_password" => handle_s7_password(session, request_id, payload),
        "open_fw_tcp" => handle_open_fw_tcp(session, request_id, payload),
        "fw_read" => handle_fw_read(session, request_id, payload),
        "fw_write" => handle_fw_write(session, request_id, payload),
        "start_fw_slave" => handle_start_fw_slave(session, request_id, payload),
        "stop_fw_slave" => handle_stop_fw_slave(session, request_id, payload),
        "open_ppi_tcp" => handle_open_ppi_tcp(session, request_id, payload),
        "ppi_read" => handle_ppi_read(session, request_id, payload),
        "ppi_write" => handle_ppi_write(session, request_id, payload),
        "ppi_build_read" => handle_ppi_build_read(request_id, payload),
        "ppi_build_sa_confirm" => handle_ppi_build_sa_confirm(request_id, payload),
        "ppi_parse_read_response" => handle_ppi_parse_read_response(request_id, payload),
        "start_ppi_slave" => handle_start_ppi_slave(session, request_id, payload),
        "stop_ppi_slave" => handle_stop_ppi_slave(session, request_id, payload),
        "hostlink_build_fins" => handle_hostlink_build_fins(request_id, payload),
        "hostlink_parse_fins" => handle_hostlink_parse_fins(request_id, payload),
        "hostlink_build_cmode_read" => handle_hostlink_build_cmode_read(request_id, payload),
        "hostlink_parse_cmode_read" => handle_hostlink_parse_cmode_read(request_id, payload),
        "uss_build_request" => handle_uss_build_request(request_id, payload),
        "uss_parse_response" => handle_uss_parse_response(request_id, payload),
        "rk512_build_read" => handle_rk512_build_read(request_id, payload),
        "rk512_build_write" => handle_rk512_build_write(request_id, payload),
        "rk512_parse_response" => handle_rk512_parse_response(request_id, payload),
        // === Allen-Bradley EtherNet/IP + CIP（TCP 只读会话 + explicit codec）===
        "open_enip_connection" => handle_open_enip_connection(session, request_id, payload),
        "enip_read_tag" => handle_enip_read_tag(session, request_id, payload),
        "enip_build_register_session" => handle_enip_build_register_session(request_id, payload),
        "enip_build_unregister_session" => {
            handle_enip_build_unregister_session(request_id, payload)
        }
        "enip_build_read_tag" => handle_enip_build_read_tag(request_id, payload),
        "enip_parse_frame" => handle_enip_parse_frame(request_id, payload),
        "enip_parse_cip_response" => handle_enip_parse_cip_response(request_id, payload),
        // === Beckhoff ADS/AMS over TCP（TCP 只读会话 + 编解码）===
        "open_ads_connection" => handle_open_ads_connection(session, request_id, payload),
        "ads_read" => handle_ads_read(session, request_id, payload),
        "ads_read_device_info" => handle_ads_read_device_info(session, request_id, payload),
        "ads_read_state" => handle_ads_read_state(session, request_id, payload),
        "ads_build_read" => handle_ads_build_read(request_id, payload),
        "ads_build_write" => handle_ads_build_write(request_id, payload),
        "ads_build_readwrite" => handle_ads_build_readwrite(request_id, payload),
        "ads_build_read_device_info" => handle_ads_build_read_device_info(request_id, payload),
        "ads_build_read_state" => handle_ads_build_read_state(request_id, payload),
        "ads_parse_frame" => handle_ads_parse_frame(request_id, payload),
        "ads_parse_response" => handle_ads_parse_response(request_id, payload),
        // === MQTT 3.1.1（TCP 只读订阅 + 编解码）===
        "open_mqtt_connection" => handle_open_mqtt_connection(session, request_id, payload),
        "mqtt_subscribe" => handle_mqtt_subscribe(session, request_id, payload),
        "mqtt_read_publish" => handle_mqtt_read_publish(session, request_id, payload),
        "mqtt_ping" => handle_mqtt_ping(session, request_id, payload),
        "mqtt_build_connect" => handle_mqtt_build_connect(request_id, payload),
        "mqtt_parse_connack" => handle_mqtt_parse_connack(request_id, payload),
        "mqtt_build_subscribe" => handle_mqtt_build_subscribe(request_id, payload),
        "mqtt_parse_suback" => handle_mqtt_parse_suback(request_id, payload),
        "mqtt_parse_publish" => handle_mqtt_parse_publish(request_id, payload),
        "mqtt_build_pingreq" => handle_mqtt_build_pingreq(request_id),
        "mqtt_parse_pingresp" => handle_mqtt_parse_pingresp(request_id, payload),
        "mqtt_build_disconnect" => handle_mqtt_build_disconnect(request_id),
        // === IEC 60870-5-104（TCP 只读 Client/Master + 总召）===
        "open_iec104_connection" => handle_open_iec104_connection(session, request_id, payload),
        "iec104_general_interrogation" => {
            handle_iec104_general_interrogation(session, request_id, payload)
        }
        "iec104_test_frame" => handle_iec104_test_frame(session, request_id, payload),
        "iec104_build_i_frame" => handle_iec104_build_i_frame(request_id, payload),
        "iec104_build_s_frame" => handle_iec104_build_s_frame(request_id, payload),
        "iec104_build_u_frame" => handle_iec104_build_u_frame(request_id, payload),
        "iec104_parse_apdu" => handle_iec104_parse_apdu(request_id, payload),
        "iec104_build_general_interrogation" => {
            handle_iec104_build_general_interrogation(request_id, payload)
        }
        "iec104_parse_asdu" => handle_iec104_parse_asdu(request_id, payload),
        // === DNP3（TCP 只读 Master + Class 0/1/2/3）===
        "open_dnp3_connection" => handle_open_dnp3_connection(session, request_id, payload),
        "dnp3_integrity_poll" => handle_dnp3_integrity_poll(session, request_id, payload),
        "dnp3_class_scan" => handle_dnp3_class_scan(session, request_id, payload),
        "dnp3_read" => handle_dnp3_read(session, request_id, payload),
        "dnp3_build_link_frame" => handle_dnp3_build_link_frame(request_id, payload),
        "dnp3_parse_link_frame" => handle_dnp3_parse_link_frame(request_id, payload),
        "dnp3_build_class_scan" => handle_dnp3_build_class_scan(request_id, payload),
        "dnp3_build_read_request" => handle_dnp3_build_read_request(request_id, payload),
        "dnp3_parse_application_response" => {
            handle_dnp3_parse_application_response(request_id, payload)
        }
        "dnp3_build_confirm" => handle_dnp3_build_confirm(request_id, payload),
        // === DL/T 645-1997/2007（RS-485 只读电表编解码）===
        "dlt645_parse_address" => handle_dlt645_parse_address(request_id, payload),
        "dlt645_parse_data_id" => handle_dlt645_parse_data_id(request_id, payload),
        "dlt645_build_read_request" => handle_dlt645_build_read_request(request_id, payload),
        "dlt645_parse_frame" => handle_dlt645_parse_frame(request_id, payload),
        "dlt645_parse_read_response" => handle_dlt645_parse_read_response(request_id, payload),
        "cjt188_parse_meter_type" => handle_cjt188_parse_meter_type(request_id, payload),
        "cjt188_parse_address" => handle_cjt188_parse_address(request_id, payload),
        "cjt188_parse_data_id" => handle_cjt188_parse_data_id(request_id, payload),
        "cjt188_build_read_request" => handle_cjt188_build_read_request(request_id, payload),
        "cjt188_parse_frame" => handle_cjt188_parse_frame(request_id, payload),
        "cjt188_parse_read_response" => handle_cjt188_parse_read_response(request_id, payload),
        "bacnet_ip_build_whois" => handle_bacnet_ip_build_whois(request_id, payload),
        "bacnet_ip_build_iam" => handle_bacnet_ip_build_iam(request_id, payload),
        "bacnet_ip_parse_frame" => handle_bacnet_ip_parse_frame(request_id, payload),
        "bacnet_ip_build_read_property_request" => {
            handle_bacnet_ip_build_read_property_request(request_id, payload)
        }
        "bacnet_ip_parse_read_property_request" => {
            handle_bacnet_ip_parse_read_property_request(request_id, payload)
        }
        "bacnet_ip_parse_read_property_ack" => {
            handle_bacnet_ip_parse_read_property_ack(request_id, payload)
        }
        "open_bacnet_ip_connection" => {
            handle_open_bacnet_ip_connection(session, request_id, payload)
        }
        "bacnet_ip_whois" => handle_bacnet_ip_whois(session, request_id, payload),
        "bacnet_ip_read_property_live" => {
            handle_bacnet_ip_read_property_live(session, request_id, payload)
        }
        "knx_parse_group_address" => handle_knx_parse_group_address(request_id, payload),
        "knx_build_connect_request" => handle_knx_build_connect_request(request_id, payload),
        "knx_parse_connect_response" => handle_knx_parse_connect_response(request_id, payload),
        "knx_build_group_read_request" => handle_knx_build_group_read_request(request_id, payload),
        "knx_parse_tunneling_request" => handle_knx_parse_tunneling_request(request_id, payload),
        "knx_parse_group_value_response" => {
            handle_knx_parse_group_value_response(request_id, payload)
        }
        "knx_parse_tunneling_ack" => handle_knx_parse_tunneling_ack(request_id, payload),
        "knx_build_tunneling_ack" => handle_knx_build_tunneling_ack(request_id, payload),
        "open_knx_connection" => handle_open_knx_connection(session, request_id, payload),
        "knx_group_read" => handle_knx_group_read(session, request_id, payload),
        "knx_disconnect" => handle_knx_disconnect(session, request_id, payload),
        "knx_connection_state" => handle_knx_connection_state(session, request_id, payload),
        "knx_start_keepalive" => handle_knx_start_keepalive(session, request_id, payload),
        "knx_stop_keepalive" => handle_knx_stop_keepalive(session, request_id, payload),
        "knx_keepalive_status" => handle_knx_keepalive_status(session, request_id, payload),
        // === Keyence KV Host Link ASCII（TCP 只读会话 + 编解码）===
        "keyence_parse_address" => handle_keyence_parse_address(request_id, payload),
        "keyence_build_connect" => handle_keyence_build_connect(request_id, payload),
        "keyence_build_read_words" => handle_keyence_build_read_words(request_id, payload),
        "keyence_build_read_bits" => handle_keyence_build_read_bits(request_id, payload),
        "keyence_build_write_words" => handle_keyence_build_write_words(request_id, payload),
        "keyence_build_write_bit" => handle_keyence_build_write_bit(request_id, payload),
        "keyence_parse_connect" => handle_keyence_parse_connect(request_id, payload),
        "keyence_parse_words" => handle_keyence_parse_words(request_id, payload),
        "keyence_parse_bits" => handle_keyence_parse_bits(request_id, payload),
        "keyence_parse_write" => handle_keyence_parse_write(request_id, payload),
        // === LS Electric XGT FEnet（TCP 只读会话 + 编解码）===
        "open_ls_xgt_connection" => handle_open_ls_xgt_connection(session, request_id, payload),
        "ls_xgt_read" => handle_ls_xgt_read(session, request_id, payload),
        "ls_xgt_read_continuous" => handle_ls_xgt_read_continuous(session, request_id, payload),
        "ls_xgt_parse_address" => handle_ls_xgt_parse_address(request_id, payload),
        "ls_xgt_build_read" => handle_ls_xgt_build_read(request_id, payload),
        "ls_xgt_build_continuous_read" => handle_ls_xgt_build_continuous_read(request_id, payload),
        "ls_xgt_build_write" => handle_ls_xgt_build_write(request_id, payload),
        "ls_xgt_build_continuous_write" => {
            handle_ls_xgt_build_continuous_write(request_id, payload)
        }
        "ls_xgt_parse_response" => handle_ls_xgt_parse_response(request_id, payload),
        // === Panasonic MEWTOCOL-COM（首轮离线编解码）===
        "panasonic_parse_data_address" => handle_panasonic_parse_data_address(request_id, payload),
        "panasonic_parse_contact_address" => {
            handle_panasonic_parse_contact_address(request_id, payload)
        }
        "panasonic_build_read" => handle_panasonic_build_read(request_id, payload),
        "panasonic_build_write" => handle_panasonic_build_write(request_id, payload),
        "panasonic_build_read_contact" => handle_panasonic_build_read_contact(request_id, payload),
        "panasonic_build_write_contact" => {
            handle_panasonic_build_write_contact(request_id, payload)
        }
        "panasonic_parse_response" => handle_panasonic_parse_response(request_id, payload),
        "fx_links_build" => handle_fx_links_build(request_id, payload),
        "fx_links_parse" => handle_fx_links_parse(request_id, payload),
        "fx_links_read" => handle_fx_links_read(request_id, payload),
        "fx_links_write_bits" => handle_fx_links_write_bits(request_id, payload),
        "fx_links_write_words" => handle_fx_links_write_words(request_id, payload),
        "fx_prog_build_read" => handle_fx_prog_build_read(request_id, payload),
        "fx_prog_build_write" => handle_fx_prog_build_write(request_id, payload),
        "fx_prog_parse" => handle_fx_prog_parse(request_id, payload),
        // === 从站模拟 ===
        "start_tcp_slave" => handle_start_tcp_slave(session, request_id, payload),
        "stop_slave" => handle_stop_slave(session, request_id, payload),
        "slave_set_value" => handle_slave_set_value(session, request_id, payload),
        "slave_set_coil" => handle_slave_set_coil(session, request_id, payload),
        "slave_clear" => handle_slave_clear(session, request_id, payload),
        "slave_get_memory" => handle_slave_get_memory(session, request_id, payload),
        // === 串口从站模拟(Electron 持 COM 句柄)===
        "start_serial_slave" => handle_start_serial_slave(session, request_id, payload),
        "stop_serial_slave" => handle_stop_serial_slave(session, request_id, payload),
        "slave_handle_serial_bytes" => {
            handle_slave_handle_serial_bytes(session, request_id, payload)
        }
        "serial_slave_set_value" => handle_serial_slave_set_value(session, request_id, payload),
        "serial_slave_get_memory" => handle_serial_slave_get_memory(session, request_id, payload),
        // === 校验 + 在线解析(串口调试) ===
        "compute_crc16" => handle_compute_crc16(request_id, payload),
        "compute_lrc" => handle_compute_lrc(request_id, payload),
        "parse_frame_online" => handle_parse_frame_online(request_id, payload),
        // === 离线解析器(对标 ModbusPacketParser)===
        "parse_frame_offline" => handle_parse_frame_offline(request_id, payload),
        // === 自定义帧解析(串口可视化批次 2)===
        "custom_frame_parse" => handle_custom_frame_parse(request_id, payload),
        "custom_frame_validate" => handle_custom_frame_validate(request_id, payload),
        // === 流式轮询(v2 协议)===
        "start_poll_stream" => handle_start_poll_stream(session, request_id, payload),
        "stop_poll_stream" => handle_stop_poll_stream(session, request_id, payload),
        "shutdown" => success(request_id.to_string(), json!({ "accepted": true }), true),
        unknown => failure(
            Some(request_id.to_string()),
            CoreError::UnknownCommand(unknown.to_owned()),
        ),
    }
}

fn all_capabilities() -> Vec<&'static str> {
    vec![
        "hello",
        "validate_serial_config",
        "build_read_holding_registers",
        "parse_read_holding_registers",
        "build_read_input_registers",
        "parse_read_input_registers",
        "build_read_coils",
        "parse_read_coils",
        "build_read_discrete_inputs",
        "parse_read_discrete_inputs",
        "build_write_single_coil",
        "parse_write_single_coil",
        "build_write_single_register",
        "parse_write_single_register",
        "build_write_multiple_coils",
        "parse_write_multiple_coils",
        "build_write_multiple_registers",
        "parse_write_multiple_registers",
        "build_ascii_read_holding_registers",
        "parse_ascii_read_holding_registers",
        "build_ascii_read_input_registers",
        "parse_ascii_read_input_registers",
        "build_ascii_read_coils",
        "parse_ascii_read_coils",
        "build_ascii_read_discrete_inputs",
        "parse_ascii_read_discrete_inputs",
        "build_ascii_write_single_coil",
        "parse_ascii_write_single_coil",
        "build_ascii_write_single_register",
        "parse_ascii_write_single_register",
        "build_ascii_write_multiple_coils",
        "parse_ascii_write_multiple_coils",
        "build_ascii_write_multiple_registers",
        "parse_ascii_write_multiple_registers",
        "open_tcp_connection",
        "open_udp_connection",
        "close_connection",
        "tcp_read_coils",
        "tcp_read_discrete_inputs",
        "tcp_read_holding_registers",
        "tcp_read_input_registers",
        "tcp_write_single_coil",
        "tcp_write_single_register",
        "tcp_write_multiple_coils",
        "tcp_write_multiple_registers",
        "tcp_mask_write_register",
        "tcp_read_write_multiple",
        "tcp_read_device_id",
        "tcp_diagnostics",
        "tcp_read_exception_status",
        "tcp_get_comm_event_counter",
        "tcp_get_comm_event_log",
        "tcp_report_slave_id",
        "udp_read_coils",
        "udp_read_discrete_inputs",
        "udp_read_holding_registers",
        "udp_read_input_registers",
        "udp_write_single_coil",
        "udp_write_single_register",
        "udp_write_multiple_coils",
        "udp_write_multiple_registers",
        "decode_values",
        "scan_station_ids",
        // 三菱 MC 协议
        "mc_parse_address",
        "mc_build_read",
        "mc_build_write",
        "mc_parse_response",
        "open_mc_tcp_connection",
        "mc_tcp_read",
        "mc_tcp_write",
        "start_mc_tcp_slave",
        "stop_mc_slave",
        "mc_slave_set",
        // 三菱 MC 进阶(M2)
        "mc_tcp_read_random",
        "mc_tcp_write_random",
        "mc_tcp_read_blocks",
        "mc_remote_run",
        "mc_remote_stop",
        "mc_remote_reset",
        "mc_remote_pause",
        "mc_read_clock",
        "mc_echo_test",
        "mc_read_cpu_type",
        "mc_read_cpu_status",
        "mc_build_ascii_read",
        "open_mc_ascii_connection",
        "mc_ascii_read",
        "mc_ascii_write",
        // 三菱 MC 串口 C24(3C/4C 离线组帧)
        "mc_serial_build_3c",
        "mc_serial_parse_3c",
        "mc_c24_read",
        "mc_c24_parse_read",
        // 三菱 A-1E / SLMP-1E 帧
        "mc_1e_build_read",
        "mc_1e_build_write",
        "mc_1e_parse",
        "open_mc_udp_connection",
        "mc_udp_read",
        "mc_udp_write",
        "open_mc_1e_tcp",
        "mc_1e_read",
        "mc_1e_write",
        // 三菱 FX 串口协议(Computer Link / 编程口)
        "brand_parse_address",
        "delta_parse_address",
        "inovance_parse_address",
        "xinjie_parse_address",
        "fatek_parse_address",
        "fatek_pack_command",
        "fatek_build_read_discrete",
        "fatek_build_write_discrete",
        "fatek_build_read_words",
        "fatek_build_write_words",
        "fatek_parse_response",
        "open_fatek_connection",
        "fatek_read_words",
        "fatek_read_discrete",
        "fuji_sph_parse_address",
        "fuji_sph_build_read",
        "fuji_sph_build_write",
        "fuji_sph_parse_response",
        "open_fuji_sph_connection",
        "fuji_sph_read",
        "ge_srtp_parse_address",
        "ge_srtp_build_handshake",
        "ge_srtp_parse_handshake",
        "ge_srtp_build_read",
        "ge_srtp_build_write",
        "ge_srtp_parse_response",
        "open_ge_srtp_connection",
        "ge_srtp_read",
        "fins_parse_address",
        "open_fins_tcp",
        "open_fins_udp",
        "fins_read",
        "fins_write",
        "start_fins_slave",
        "stop_fins_slave",
        "fins_slave_set",
        "fins_slave_get",
        "s7_parse_address",
        "open_s7_connection",
        "close_s7_connection",
        "s7_read",
        "s7_write",
        "start_s7_slave",
        "stop_s7_slave",
        "s7_slave_set",
        "s7_slave_get",
        "s7_cpu_control",
        "s7_read_status",
        "s7_password",
        "open_fw_tcp",
        "fw_read",
        "fw_write",
        "start_fw_slave",
        "stop_fw_slave",
        "open_ppi_tcp",
        "ppi_read",
        "ppi_write",
        "ppi_build_read",
        "ppi_build_sa_confirm",
        "ppi_parse_read_response",
        "start_ppi_slave",
        "stop_ppi_slave",
        "hostlink_build_fins",
        "hostlink_parse_fins",
        "hostlink_build_cmode_read",
        "hostlink_parse_cmode_read",
        "uss_build_request",
        "uss_parse_response",
        "rk512_build_read",
        "rk512_build_write",
        "rk512_parse_response",
        "open_enip_connection",
        "enip_read_tag",
        "enip_build_register_session",
        "enip_build_unregister_session",
        "enip_build_read_tag",
        "enip_parse_frame",
        "enip_parse_cip_response",
        "open_ads_connection",
        "ads_read",
        "ads_read_device_info",
        "ads_read_state",
        "ads_build_read",
        "ads_build_write",
        "ads_build_readwrite",
        "ads_build_read_device_info",
        "ads_build_read_state",
        "ads_parse_frame",
        "ads_parse_response",
        "open_mqtt_connection",
        "mqtt_subscribe",
        "mqtt_read_publish",
        "mqtt_ping",
        "mqtt_build_connect",
        "mqtt_parse_connack",
        "mqtt_build_subscribe",
        "mqtt_parse_suback",
        "mqtt_parse_publish",
        "mqtt_build_pingreq",
        "mqtt_parse_pingresp",
        "mqtt_build_disconnect",
        "open_iec104_connection",
        "iec104_general_interrogation",
        "iec104_test_frame",
        "iec104_build_i_frame",
        "iec104_build_s_frame",
        "iec104_build_u_frame",
        "iec104_parse_apdu",
        "iec104_build_general_interrogation",
        "iec104_parse_asdu",
        "open_dnp3_connection",
        "dnp3_integrity_poll",
        "dnp3_class_scan",
        "dnp3_read",
        "dnp3_build_link_frame",
        "dnp3_parse_link_frame",
        "dnp3_build_class_scan",
        "dnp3_build_read_request",
        "dnp3_parse_application_response",
        "dnp3_build_confirm",
        "dlt645_parse_address",
        "dlt645_parse_data_id",
        "dlt645_build_read_request",
        "dlt645_parse_frame",
        "dlt645_parse_read_response",
        "cjt188_parse_meter_type",
        "cjt188_parse_address",
        "cjt188_parse_data_id",
        "cjt188_build_read_request",
        "cjt188_parse_frame",
        "cjt188_parse_read_response",
        "bacnet_ip_build_whois",
        "bacnet_ip_build_iam",
        "bacnet_ip_parse_frame",
        "bacnet_ip_build_read_property_request",
        "bacnet_ip_parse_read_property_request",
        "bacnet_ip_parse_read_property_ack",
        "open_bacnet_ip_connection",
        "bacnet_ip_whois",
        "bacnet_ip_read_property_live",
        "knx_parse_group_address",
        "knx_build_connect_request",
        "knx_parse_connect_response",
        "knx_build_group_read_request",
        "knx_parse_tunneling_request",
        "knx_parse_group_value_response",
        "knx_parse_tunneling_ack",
        "knx_build_tunneling_ack",
        "open_knx_connection",
        "knx_group_read",
        "knx_disconnect",
        "knx_connection_state",
        "knx_start_keepalive",
        "knx_stop_keepalive",
        "knx_keepalive_status",
        "keyence_parse_address",
        "keyence_build_connect",
        "keyence_build_read_words",
        "keyence_build_read_bits",
        "keyence_build_write_words",
        "keyence_build_write_bit",
        "keyence_parse_connect",
        "keyence_parse_words",
        "keyence_parse_bits",
        "keyence_parse_write",
        "open_keyence_connection",
        "keyence_read_words",
        "keyence_read_bits",
        "open_ls_xgt_connection",
        "ls_xgt_read",
        "ls_xgt_read_continuous",
        "ls_xgt_parse_address",
        "ls_xgt_build_read",
        "ls_xgt_build_continuous_read",
        "ls_xgt_build_write",
        "ls_xgt_build_continuous_write",
        "ls_xgt_parse_response",
        "panasonic_parse_data_address",
        "panasonic_parse_contact_address",
        "panasonic_build_read",
        "panasonic_build_write",
        "panasonic_build_read_contact",
        "panasonic_build_write_contact",
        "panasonic_parse_response",
        "fx_links_build",
        "fx_links_parse",
        "fx_links_read",
        "fx_links_write_bits",
        "fx_links_write_words",
        "fx_prog_build_read",
        "fx_prog_build_write",
        "fx_prog_parse",
        "start_tcp_slave",
        "stop_slave",
        "slave_set_value",
        "slave_set_coil",
        "slave_clear",
        "slave_get_memory",
        "start_serial_slave",
        "stop_serial_slave",
        "slave_handle_serial_bytes",
        "serial_slave_set_value",
        "serial_slave_get_memory",
        "compute_crc16",
        "compute_lrc",
        "parse_frame_online",
        "parse_frame_offline",
        "custom_frame_parse",
        "custom_frame_validate",
        "start_poll_stream",
        "stop_poll_stream",
        "shutdown",
    ]
}

// =============================================================================
// 命令处理函数
// =============================================================================

fn handle_validate_serial_config(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ValidateSerialConfigPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let config: SerialConfig = match serde_json::from_value(payload.config) {
        Ok(config) => config,
        Err(error) => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "config",
                    message: error.to_string(),
                },
            );
        }
    };
    match config.validate_and_normalize() {
        Ok(config) => success(
            request_id.to_string(),
            serde_json::to_value(config).expect("SerialConfig must serialize"),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// --- RTU 读寄存器(向后兼容) ---

fn handle_build_read_registers_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        build_read_holding_registers_request
    } else {
        build_read_input_registers_request
    };
    match builder(payload.unit_id, payload.start_address, payload.quantity) {
        Ok(built) => success(
            request_id.to_string(),
            json!({
                "adu": built.adu,
                "requestHex": format_hex(&built.adu),
                "expectedResponseLength": built.expected_response_len,
                "exceptionResponseLength": built.exception_response_len,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error.into()),
    }
}

fn handle_parse_read_registers_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let parser = if fc == 0x03 {
        parse_read_holding_registers_response
    } else {
        parse_read_input_registers_response
    };
    match parser(&payload.response, payload.unit_id, payload.quantity) {
        Ok(parsed) => success(
            request_id.to_string(),
            format_read_registers_result(&parsed),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error.into()),
    }
}

// --- RTU 读位(FC01/FC02,新增) ---

fn handle_build_read_bits_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let pdu_bytes = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    // pdu_bytes 已含 FC 首字节,RtuFrame::request 会再写一次 FC——不剥离会产出双 FC 帧
    //(从站把第二字节当地址,读错地址 256 倍偏移)。与 build_and_encode_rtu 的剥离逻辑一致。
    let data = if pdu_bytes.first() == Some(&fc) {
        &pdu_bytes[1..]
    } else {
        &pdu_bytes[..]
    };
    let adu = match modbus_rtu::RtuFrame::request(payload.unit_id, fc, data) {
        Ok(frame) => frame.encode(),
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let expected_response_len = 5 + usize::from(payload.quantity.div_ceil(8));
    success(
        request_id.to_string(),
        json!({
            "adu": adu,
            "requestHex": format_hex(&adu),
            "expectedResponseLength": expected_response_len,
            "exceptionResponseLength": 5,
        }),
        false,
    )
}

fn handle_parse_read_bits_rtu(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame =
        match modbus_rtu::RtuFrame::decode(&payload.response, modbus_rtu::RtuFrameRole::Response) {
            Ok(f) => f,
            Err(e) => return failure(Some(request_id.to_string()), e.into()),
        };
    if frame.unit_id() != payload.unit_id {
        return failure(
            Some(request_id.to_string()),
            RtuError::UnitIdMismatch {
                expected: payload.unit_id,
                received: frame.unit_id(),
            }
            .into(),
        );
    }
    let base_fc = frame.function_code() & 0x7F;
    if base_fc != fc {
        return failure(
            Some(request_id.to_string()),
            RtuError::FunctionCodeMismatch {
                expected: fc,
                received: frame.function_code(),
            }
            .into(),
        );
    }
    if frame.is_exception() {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": frame.exception_code(),
                "exceptionName": frame.exception_code().map(modbus_exception_name),
                "coils": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(frame.data(), payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "coils": bits,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- RTU 写操作(FC05/06/15/16,新增) ---

fn handle_build_write_single_coil_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_coil_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x05, &pdu_bytes)
}

fn handle_build_write_single_register_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_register_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x06, &pdu_bytes)
}

fn handle_build_write_multiple_coils_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleCoilsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_coils_pdu(payload.address, &payload.values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x0F, &pdu_bytes)
}

fn handle_build_write_multiple_registers_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_registers_pdu(payload.address, &payload.values)
    {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_and_encode_rtu(request_id, payload.unit_id, 0x10, &pdu_bytes)
}

fn build_and_encode_rtu(request_id: &str, unit_id: u8, fc: u8, pdu_bytes: &[u8]) -> CommandOutcome {
    // pdu_bytes 的第一个字节是 FC,但 RtuFrame 会自己加 FC,所以 data 要去掉首字节
    let data = if pdu_bytes.first() == Some(&fc) {
        &pdu_bytes[1..]
    } else {
        pdu_bytes
    };
    let frame = match modbus_rtu::RtuFrame::request(unit_id, fc, data) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let adu = frame.encode();
    let expect_response = unit_id != 0;
    success(
        request_id.to_string(),
        json!({
            "adu": adu,
            "requestHex": format_hex(&adu),
            "expectedResponseLength": if expect_response { adu.len() } else { 0 },
            "exceptionResponseLength": if expect_response { 5 } else { 0 },
            "expectResponse": expect_response,
        }),
        false,
    )
}

fn handle_parse_write_single_coil_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x05) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, value) = match pdu::parse_write_single_coil_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "value": value,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_single_register_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x06) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, value) = match pdu::parse_write_single_register_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "value": value,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_multiple_coils_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x0F) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, qty) = match pdu::parse_write_multiple_coils_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "quantity": qty,
            "exceptionCode": null,
        }),
        false,
    )
}

fn handle_parse_write_multiple_registers_rtu(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match decode_rtu_response(&payload.response, payload.unit_id, 0x10) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = unit_id;
    let (addr, qty) = match pdu::parse_write_multiple_registers_response(&pdu_data) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "address": addr,
            "quantity": qty,
            "exceptionCode": null,
        }),
        false,
    )
}

/// 解码 RTU 响应帧,返回 (unit_id, pdu_data)。处理异常码。
fn decode_rtu_response(
    response: &[u8],
    expected_unit_id: u8,
    expected_fc: u8,
) -> Result<(u8, Vec<u8>), CoreError> {
    let frame = modbus_rtu::RtuFrame::decode(response, modbus_rtu::RtuFrameRole::Response)?;
    if frame.unit_id() != expected_unit_id {
        return Err(RtuError::UnitIdMismatch {
            expected: expected_unit_id,
            received: frame.unit_id(),
        }
        .into());
    }
    let base_fc = frame.function_code() & 0x7F;
    if base_fc != expected_fc {
        return Err(RtuError::FunctionCodeMismatch {
            expected: expected_fc,
            received: frame.function_code(),
        }
        .into());
    }
    let mut pdu = Vec::with_capacity(frame.data().len() + 1);
    pdu.push(frame.function_code());
    pdu.extend_from_slice(frame.data());
    Ok((frame.unit_id(), pdu))
}

// --- ASCII 读寄存器 ---

fn handle_build_read_registers_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let pdu_bytes = match pdu_builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let frame = modbus_ascii::build_ascii_frame(payload.unit_id, &pdu_bytes);
    success(
        request_id.to_string(),
        json!({
            "adu": frame,
            "requestHex": format_hex(&frame),
            "expectedResponseLength": 0,
            "exceptionResponseLength": 0,
        }),
        false,
    )
}

fn handle_parse_read_registers_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (unit_id, pdu_data) = match modbus_ascii::parse_ascii_frame(&payload.response) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    if unit_id != payload.unit_id {
        return failure(
            Some(request_id.to_string()),
            RtuError::UnitIdMismatch {
                expected: payload.unit_id,
                received: unit_id,
            }
            .into(),
        );
    }
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, fc) {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": exc,
                "exceptionName": modbus_exception_name(exc),
                "registers": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&pdu_data, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "registers": registers,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 读位(FC01/FC02) ---

fn handle_build_read_bits_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: BuildReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if payload.unit_id == 0 {
        return failure(
            Some(request_id.to_string()),
            RtuError::BroadcastReadNotAllowed.into(),
        );
    }
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let pdu_bytes = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_parse_read_bits_ascii(request_id: &str, payload: Value, fc: u8) -> CommandOutcome {
    let payload: ParseReadBitsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, fc) {
        return success(
            request_id.to_string(),
            json!({
                "status": "exception",
                "exceptionCode": exc,
                "exceptionName": modbus_exception_name(exc),
                "coils": [],
            }),
            false,
        );
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&pdu_data, payload.quantity) {
        Ok(coils) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "exceptionCode": null,
                "exceptionName": null,
                "coils": coils,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 写操作(FC05/06/0F/10) ---

fn handle_build_write_single_coil_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_coil_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_single_register_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteSingleRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_single_register_pdu(payload.address, payload.value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_multiple_coils_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleCoilsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_coils_pdu(payload.address, &payload.values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_build_write_multiple_registers_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: BuildWriteMultipleRegistersPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_bytes = match pdu::build_write_multiple_registers_pdu(payload.address, &payload.values)
    {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    build_ascii_adu(request_id, payload.unit_id, &pdu_bytes)
}

fn handle_parse_write_single_coil_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x05) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_single_coil_response(&pdu_data) {
        Ok((addr, value)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "value": value,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_single_register_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x06) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_single_register_response(&pdu_data) {
        Ok((addr, value)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "value": value,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_multiple_coils_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x0F) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_multiple_coils_response(&pdu_data) {
        Ok((addr, qty)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "quantity": qty,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_parse_write_multiple_registers_ascii(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseWriteResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let pdu_data = match decode_ascii_response(&payload.response, payload.unit_id) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Ok(Some(exc)) = pdu::check_exception(&pdu_data, 0x10) {
        return ascii_exception_outcome(request_id, exc);
    }
    match pdu::parse_write_multiple_registers_response(&pdu_data) {
        Ok((addr, qty)) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "address": addr,
                "quantity": qty,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

// --- ASCII 共用辅助 ---

/// 用 ASCII 帧包装 PDU(unit_id + pdu_bytes),返回标准 ADU 结果。
fn build_ascii_adu(request_id: &str, unit_id: u8, pdu_bytes: &[u8]) -> CommandOutcome {
    let frame = modbus_ascii::build_ascii_frame(unit_id, pdu_bytes);
    let expect_response = unit_id != 0;
    success(
        request_id.to_string(),
        json!({
            "adu": frame,
            "requestHex": format_hex(&frame),
            "expectedResponseLength": 0,
            "exceptionResponseLength": 0,
            "expectResponse": expect_response,
        }),
        false,
    )
}

/// 解码 ASCII 响应帧,校验站号,返回 PDU(含 FC 首字节)。
fn decode_ascii_response(response: &[u8], expected_unit_id: u8) -> Result<Vec<u8>, CoreError> {
    let (unit_id, pdu_data) = modbus_ascii::parse_ascii_frame(response)?;
    if unit_id != expected_unit_id {
        return Err(RtuError::UnitIdMismatch {
            expected: expected_unit_id,
            received: unit_id,
        }
        .into());
    }
    Ok(pdu_data)
}

/// 构造 ASCII 写操作的异常响应 outcome(写响应无数据载荷)。
fn ascii_exception_outcome(request_id: &str, exception_code: u8) -> CommandOutcome {
    success(
        request_id.to_string(),
        json!({
            "status": "exception",
            "exceptionCode": exception_code,
            "exceptionName": modbus_exception_name(exception_code),
        }),
        false,
    )
}

// --- TCP/UDP 连接管理 ---

fn handle_open_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let framing = match parse_framing(&payload.framing) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_tcp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        payload.unit_id,
        framing,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connected": true, "connectionId": payload.connection_id, "framing": payload.framing }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let framing = match parse_framing(&payload.framing) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_udp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        payload.unit_id,
        framing,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connected": true, "connectionId": payload.connection_id, "framing": payload.framing }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_close_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: CloseConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.close_connection(&payload.connection_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "closed": true, "connectionId": payload.connection_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// --- TCP 端到端读写 ---

fn handle_tcp_read_bits(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    // 检查异常
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "coils": bits,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({
                "status": "ok",
                "registers": registers,
                "exceptionCode": null,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_write_single_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_bool() {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC05 值必须是布尔".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_coil_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x05) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_tcp_write_single_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_u64().and_then(|v| u16::try_from(v).ok()) {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC06 值必须是 0-65535".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_register_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x06) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_tcp_write_multiple_coils(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<bool> = payload
        .values
        .iter()
        .map(|v| v.as_bool().unwrap_or(false))
        .collect();
    let request_pdu = match pdu::build_write_multiple_coils_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x0F) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

fn handle_tcp_write_multiple_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<u16> = payload
        .values
        .iter()
        .map(|v| v.as_u64().and_then(|n| u16::try_from(n).ok()).unwrap_or(0))
        .collect();
    let request_pdu = match pdu::build_write_multiple_registers_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x10) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

// --- 高级 FC 端到端(FC22/23/43/08) ---

fn handle_tcp_mask_write_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpMaskWriteRegisterPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = match pdu::build_mask_write_register_pdu(
        payload.address,
        payload.and_mask,
        payload.or_mask,
    ) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x16) {
        return exc;
    }
    match pdu::parse_mask_write_register_response(&response_pdu) {
        Ok((addr, and_mask, or_mask)) => success(
            request_id.to_string(),
            json!({ "status": "ok", "address": addr, "andMask": and_mask, "orMask": or_mask }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_write_multiple(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpReadWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = match pdu::build_read_write_multiple_registers_pdu(
        payload.read_address,
        payload.read_quantity,
        payload.write_address,
        &payload.write_values,
    ) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x17) {
        return exc;
    }
    match pdu::parse_read_write_multiple_registers_response(&response_pdu, payload.read_quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({ "status": "ok", "registers": registers }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_read_device_id(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpReadDeviceIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };

    // FC43/14 续传循环:设备返回 moreFollows=0xFF 时,用 nextObjectId 继续请求,
    // 直到 moreFollows=0 或达到最大迭代次数(防死循环)。
    let mut all_responses: Vec<Vec<u8>> = Vec::new();
    let mut next_object_id = payload.object_id;
    const MAX_ITERATIONS: u8 = 32;

    for _ in 0..MAX_ITERATIONS {
        let request_pdu =
            pdu::build_read_device_id_pdu(payload.read_device_id_code, next_object_id);
        let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
            Ok(p) => p,
            Err(e) => return failure(Some(request_id.to_string()), e),
        };
        if let Some(exc) = check_pdu_exception(&response_pdu, 0x2B) {
            return exc;
        }

        // 检查 moreFollows 字节(响应 PDU 偏移 4)
        let more_follows = response_pdu.get(4).copied().unwrap_or(0);
        all_responses.push(response_pdu.clone());

        if more_follows == 0xFF {
            // 还有更多:用 nextObjectId 继续请求
            next_object_id = response_pdu.get(5).copied().unwrap_or(next_object_id);
        } else {
            break; // 数据已完整
        }
    }

    // 返回所有页的合并结果(前端可遍历 pages 数组逐页解析对象)
    let pages_json: Vec<Value> = all_responses
        .iter()
        .map(|resp| {
            json!({
                "rawResponse": resp,
                "functionCode": resp.first().copied().unwrap_or(0),
                "meiType": resp.get(1).copied(),
                "readDeviceIdCode": resp.get(2).copied(),
                "conformityLevel": resp.get(3).copied(),
                "moreFollows": resp.get(4).copied(),
                "nextObjectId": resp.get(5).copied(),
                "objectCount": resp.get(6).copied(),
            })
        })
        .collect();

    let first = all_responses.first();
    success(
        request_id.to_string(),
        json!({
            "status": "ok",
            "pages": pages_json,
            "pageCount": all_responses.len(),
            "functionCode": first.and_then(|r| r.first().copied()).unwrap_or(0),
            "meiType": first.and_then(|r| r.get(1).copied()),
            "readDeviceIdCode": first.and_then(|r| r.get(2).copied()),
            "conformityLevel": first.and_then(|r| r.get(3).copied()),
            "moreFollows": 0, // 已完成续传,最终 moreFollows=0
        }),
        false,
    )
}

fn handle_tcp_diagnostics(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpDiagnosticsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request_pdu = pdu::build_diagnostics_pdu(payload.sub_function, payload.data);
    let response_pdu = match session.transact_tcp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x08) {
        return exc;
    }
    match pdu::parse_diagnostics_response(&response_pdu) {
        Ok((sub_function, data)) => success(
            request_id.to_string(),
            json!({ "status": "ok", "subFunction": sub_function, "data": data }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_tcp_simple_fc(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let p: OpenConnectionPayload = match serde_json::from_value(payload.clone()) {
        Ok(p) => p,
        Err(_) => {
            // FC07/11/12/17 可能只传 connectionId
            #[derive(Deserialize)]
            #[serde(rename_all = "camelCase", deny_unknown_fields)]
            struct ConnOnly {
                connection_id: String,
            }
            match serde_json::from_value::<ConnOnly>(payload) {
                Ok(c) => OpenConnectionPayload {
                    connection_id: c.connection_id,
                    host: String::new(),
                    port: 0,
                    unit_id: 1,
                    framing: default_framing(),
                },
                Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
            }
        }
    };
    let request_pdu = match fc {
        0x07 => pdu::build_read_exception_status_pdu(),
        0x0B => pdu::build_get_comm_event_counter_pdu(),
        0x0C => pdu::build_get_comm_event_log_pdu(),
        0x11 => pdu::build_report_slave_id_pdu(),
        _ => {
            return failure(
                Some(request_id.to_string()),
                CoreError::UnknownCommand(format!("unknown simple fc {fc:#04X}")),
            );
        }
    };
    let response_pdu = match session.transact_tcp(&p.connection_id, &request_pdu) {
        Ok(resp) => resp,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    // 返回解析后的结构化数据
    let parsed = match fc {
        0x07 => match pdu::parse_read_exception_status_response(&response_pdu) {
            Ok(status) => json!({ "status": "ok", "exceptionStatus": status }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x0B => match pdu::parse_get_comm_event_counter_response(&response_pdu) {
            Ok((status, count)) => {
                json!({ "status": "ok", "commStatus": status, "eventCount": count })
            }
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x0C => match pdu::parse_get_comm_event_log_response(&response_pdu) {
            Ok((status, event_count, msg_count, events)) => json!({
                "status": "ok", "commStatus": status, "eventCount": event_count,
                "messageCount": msg_count, "events": events
            }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        0x11 => match pdu::parse_report_slave_id_response(&response_pdu) {
            Ok((slave_id, run)) => json!({
                "status": "ok",
                "slaveId": slave_id,
                "slaveIdString": String::from_utf8_lossy(&slave_id).to_string(),
                "runStatus": run,
                "runStatusName": if run == 0xFF { "ON" } else { "OFF" },
            }),
            Err(_) => json!({ "status": "ok", "rawResponse": response_pdu }),
        },
        _ => json!({ "rawResponse": response_pdu }),
    };
    success(request_id.to_string(), parsed, false)
}

// --- UDP 端到端读写(逻辑与 TCP 版本相同,只是用 transact_udp) ---

fn handle_udp_read_bits(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x01 {
        pdu::build_read_coils_pdu
    } else {
        pdu::build_read_discrete_inputs_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x01 {
        pdu::parse_read_coils_response
    } else {
        pdu::parse_read_discrete_inputs_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(bits) => success(
            request_id.to_string(),
            json!({ "status": "ok", "coils": bits, "exceptionCode": null }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_udp_read_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
    fc: u8,
) -> CommandOutcome {
    let payload: TcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let builder = if fc == 0x03 {
        pdu::build_read_holding_registers_pdu
    } else {
        pdu::build_read_input_registers_pdu
    };
    let request_pdu = match builder(payload.start_address, payload.quantity) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, fc) {
        return exc;
    }
    let parser = if fc == 0x03 {
        pdu::parse_read_holding_registers_response
    } else {
        pdu::parse_read_input_registers_response
    };
    match parser(&response_pdu, payload.quantity) {
        Ok(registers) => success(
            request_id.to_string(),
            json!({ "status": "ok", "registers": registers, "exceptionCode": null }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e.into()),
    }
}

fn handle_udp_write_single_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_bool() {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC05 值必须是布尔".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_coil_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x05) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_udp_write_single_register(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteSinglePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let value = match payload.value.as_u64().and_then(|v| u16::try_from(v).ok()) {
        Some(v) => v,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "value",
                    message: "FC06 值必须是 0-65535".into(),
                },
            );
        }
    };
    let request_pdu = match pdu::build_write_single_register_pdu(payload.address, value) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x06) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "value": value }),
        false,
    )
}

fn handle_udp_write_multiple_coils(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<bool> = payload
        .values
        .iter()
        .map(|v| v.as_bool().unwrap_or(false))
        .collect();
    let request_pdu = match pdu::build_write_multiple_coils_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x0F) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

fn handle_udp_write_multiple_registers(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: TcpWriteMultiplePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let values: Vec<u16> = payload
        .values
        .iter()
        .map(|v| v.as_u64().and_then(|n| u16::try_from(n).ok()).unwrap_or(0))
        .collect();
    let request_pdu = match pdu::build_write_multiple_registers_pdu(payload.address, &values) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    let response_pdu = match session.transact_udp(&payload.connection_id, &request_pdu) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if let Some(exc) = check_pdu_exception(&response_pdu, 0x10) {
        return exc;
    }
    success(
        request_id.to_string(),
        json!({ "status": "ok", "address": payload.address, "quantity": values.len() }),
        false,
    )
}

// --- 值解码(纯计算,对标 28 种显示格式) ---

fn handle_decode_values(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: DecodeValuesPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let data_type = match crate::value_codec::DataType::parse(&payload.data_type) {
        Some(t) => t,
        None => {
            return failure(
                Some(request_id.to_string()),
                CoreError::InvalidSerialConfig {
                    field: "dataType",
                    message: format!("不支持的数据类型: {}", payload.data_type),
                },
            );
        }
    };
    let reg_per_elem = data_type.register_count().max(1);
    let count = payload.count.unwrap_or_else(|| {
        if reg_per_elem == 0 {
            1
        } else {
            payload.registers.len() / reg_per_elem
        }
    });
    let offset = payload.offset.unwrap_or(0);
    let values = crate::value_codec::decode_values(
        &payload.registers,
        offset,
        count,
        data_type,
        payload.scale,
        payload.offset_value,
    );
    success(
        request_id.to_string(),
        json!({ "values": values, "dataType": payload.data_type }),
        false,
    )
}

// --- 扫描站号(仅 TCP/UDP 连接) ---

// =============================================================================
// 三菱 MC 协议命令
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McAddressPayload {
    address: String,
}

/// mc_parse_address:富文本地址 → 结构化(device_code/head/is_bit)。
fn handle_mc_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McAddressPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "deviceCode": a.device_code,
                "headNumber": a.head_number,
                "isBit": a.is_bit,
                "headBytes": crate::mc_address::encode_head_number(a.head_number),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McReadPayload {
    address: String,
    points: u16,
}

/// mc_build_read:地址+点数 → 完整 3E/4E 请求帧(hex 数组)。
fn handle_mc_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let frame = crate::mc_frame::build_request_frame(
        crate::mc_frame::FrameType::Type3E,
        &crate::mc_frame::AccessRoute::default(),
        0x0010,
        &req_data,
        0,
    );
    success(request_id.to_string(), json!({ "frame": frame }), false)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McWritePayload {
    address: String,
    values: Vec<u16>,
}

/// mc_build_write:地址+值 → 完整 3E/4E 写请求帧。
fn handle_mc_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &payload.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let frame = crate::mc_frame::build_request_frame(
        crate::mc_frame::FrameType::Type3E,
        &crate::mc_frame::AccessRoute::default(),
        0x0010,
        &req_data,
        0,
    );
    success(request_id.to_string(), json!({ "frame": frame }), false)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McParseResponsePayload {
    frame: Vec<u8>,
    points: u16,
    #[serde(default)]
    is_bit: bool,
}

/// mc_parse_response:响应帧字节 → 结束代码 + 解析值。
fn handle_mc_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McParseResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let resp = match crate::mc_frame::parse_response_frame(&payload.frame) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let values = if resp.end_code == 0x0000 {
        match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, payload.is_bit) {
            Ok(v) => v,
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    } else {
        Vec::new()
    };
    success(
        request_id.to_string(),
        json!({
            "endCode": resp.end_code,
            "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
            "values": values,
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxLinksBuildPayload {
    station: u8,
    cmd: String,
    #[serde(default)]
    delay: u8,
    #[serde(default)]
    data: String,
}

/// fx_links_build:站号+命令+延时+数据 → FX Computer Link 请求帧(§3.2)。
fn handle_fx_links_build(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxLinksBuildPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_request(
        payload.station,
        &payload.cmd,
        payload.delay,
        &payload.data,
    ) {
        Ok(frame) => {
            // 帧尾固定为 SUM(2)+CRLF(2),和校验范围 = 站号首字符 ~ ETX(即 frame[1..len-4])
            let checksum = crate::fx_links::fx_links_checksum(&frame[1..frame.len() - 4]);
            success(
                request_id.to_string(),
                json!({
                    "frame": frame,
                    "frameHex": format_hex(&frame),
                    "checksum": format!("{checksum:02X}"),
                }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxLinksParsePayload {
    response: Vec<u8>,
}

/// fx_links_parse:PLC 响应 → STX 数据 / ACK / NAK 错误码(§3.2.4)。
fn handle_fx_links_parse(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxLinksParsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::parse_fx_links_response(&payload.response) {
        Ok(crate::fx_links::FxLinksResponse::ReadData { station, pc, data }) => success(
            request_id.to_string(),
            json!({
                "status": "data",
                "station": station,
                "pc": pc,
                "data": data,
                "dataAscii": String::from_utf8_lossy(&data),
            }),
            false,
        ),
        Ok(crate::fx_links::FxLinksResponse::Ack) => success(
            request_id.to_string(),
            json!({ "status": "ack", "station": null, "pc": null, "data": [], "errorCode": null }),
            false,
        ),
        Ok(crate::fx_links::FxLinksResponse::Nak {
            station,
            error_code,
        }) => success(
            request_id.to_string(),
            json!({
                "status": "nak",
                "station": station,
                "pc": null,
                "data": [],
                "errorCode": error_code,
                "errorMessage": crate::fx_links::fx_links_error_message(error_code),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgBuildReadPayload {
    device: String,
    /// 软元件编号(X/Y 八进制书写,如 "17";其余十进制)
    address: String,
    words: u16,
}

/// fx_prog_build_read:软元件+编号+字数 → FX 编程口 DEVICE READ 帧(§3.3)。
/// fx_links_read:站号+软元件+首地址+点数 → BR/WR 读请求帧(§3.2)。
fn handle_fx_links_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        points: u16,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_read(p.station, &p.device, p.head, p.points, p.delay) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// fx_links_write_bits:BW 位写帧。
fn handle_fx_links_write_bits(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        values: Vec<u16>,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bits: Vec<bool> = p.values.iter().map(|v| *v != 0).collect();
    match crate::fx_links::build_fx_links_write_bits(p.station, &p.device, p.head, &bits, p.delay) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// fx_links_write_words:WW 字写帧。
fn handle_fx_links_write_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        device: String,
        head: u16,
        values: Vec<u16>,
        #[serde(default)]
        delay: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_links::build_fx_links_write_words(
        p.station, &p.device, p.head, &p.values, p.delay,
    ) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fx_prog_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgBuildReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let number =
        match crate::fx_programming::fx_prog_parse_number(&payload.device, &payload.address) {
            Ok(n) => n,
            Err(e) => return failure(Some(request_id.to_string()), e),
        };
    match crate::fx_programming::build_fx_prog_read(&payload.device, number, payload.words) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgBuildWritePayload {
    device: String,
    /// 软元件编号(X/Y 八进制书写,其余十进制)
    address: String,
    values: Vec<u16>,
}

/// fx_prog_build_write:软元件+编号+字值 → FX 编程口 DEVICE WRITE 帧(低字节在前)。
fn handle_fx_prog_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgBuildWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let number =
        match crate::fx_programming::fx_prog_parse_number(&payload.device, &payload.address) {
            Ok(n) => n,
            Err(e) => return failure(Some(request_id.to_string()), e),
        };
    match crate::fx_programming::build_fx_prog_write(&payload.device, number, &payload.values) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FxProgParsePayload {
    frame: Vec<u8>,
}

/// fx_prog_parse:FX 编程口响应 → STX 数据(含字解码)/ ACK / NAK 错误码(§3.3.2)。
fn handle_fx_prog_parse(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: FxProgParsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fx_programming::parse_fx_prog_response(&payload.frame) {
        Ok(crate::fx_programming::FxProgResponse::Data(data)) => {
            let words = crate::fx_programming::decode_fx_prog_word_data(&data).unwrap_or_default();
            success(
                request_id.to_string(),
                json!({
                    "status": "data",
                    "data": data,
                    "dataAscii": String::from_utf8_lossy(&data),
                    "words": words,
                }),
                false,
            )
        }
        Ok(crate::fx_programming::FxProgResponse::Ack) => success(
            request_id.to_string(),
            json!({ "status": "ack", "data": [], "words": [], "errorCode": null }),
            false,
        ),
        Ok(crate::fx_programming::FxProgResponse::Nak { error_code }) => success(
            request_id.to_string(),
            json!({
                "status": "nak",
                "data": [],
                "words": [],
                "errorCode": error_code,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct OpenMcTcpPayload {
    connection_id: String,
    host: String,
    port: u16,
    #[serde(default = "default_mc_network_no")]
    network_no: u8,
    #[serde(default = "default_mc_pc_no")]
    pc_no: u8,
    #[serde(default = "default_mc_module_io")]
    module_io: u16,
    #[serde(default)]
    station_no: u8,
    #[serde(default = "default_mc_frame_type")]
    frame_type: String,
    #[serde(default = "default_mc_watchdog")]
    watchdog: u16,
}

fn default_mc_network_no() -> u8 {
    0x00
}
fn default_mc_pc_no() -> u8 {
    0xFF
}
fn default_mc_module_io() -> u16 {
    0x03FF
}
fn default_mc_frame_type() -> String {
    "3e".into()
}
fn default_mc_watchdog() -> u16 {
    0x0010
}

fn handle_open_mc_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "MC_BAD_FRAME_TYPE",
                    message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                    details: None,
                },
            );
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_tcp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        route,
        frame_type,
        payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "frameType": payload.frame_type }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McTcpReadPayload {
    connection_id: String,
    address: String,
    points: u16,
}

/// mc_tcp_read:在线成批读——地址 → 帧 → 收发 → 解析值。
fn handle_mc_tcp_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({
                "endCode": resp.end_code,
                "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
            }),
            false,
        );
    }
    let values =
        match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, addr.is_bit) {
            Ok(v) => v,
            Err(e) => return failure(Some(request_id.to_string()), e),
        };
    success(
        request_id.to_string(),
        json!({
            "endCode": 0,
            "isBit": addr.is_bit,
            "values": values,
        }),
        false,
    )
}

/// mc_tcp_write:在线成批写。
fn handle_mc_tcp_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct McTcpWritePayload {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let payload: McTcpWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &payload.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({
            "endCode": resp.end_code,
            "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code),
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McSlavePayload {
    slave_id: String,
    #[serde(default = "default_mc_port")]
    port: u16,
    #[serde(default = "default_mc_seed")]
    seed: bool,
}

fn default_mc_port() -> u16 {
    5000
}
fn default_mc_seed() -> bool {
    true
}

fn handle_start_mc_tcp_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: McSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_mc_tcp_slave(&payload.slave_id, payload.port, payload.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": payload.slave_id, "port": payload.port, "running": true }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_mc_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_mc_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": payload.slave_id, "running": false }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McSlaveSetPayload {
    slave_id: String,
    device: String,
    start: u32,
    values: Vec<u16>,
}

fn handle_mc_slave_set(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McSlaveSetPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_slave_set(
        &payload.slave_id,
        &payload.device,
        payload.start,
        &payload.values,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "device": payload.device, "start": payload.start }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// =============================================================================
// 三菱 MC 进阶命令(M2):随机/多块读写、CPU 控制、时钟、回送、型号
// =============================================================================

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomReadPayload {
    connection_id: String,
    addresses: Vec<String>,
}

fn handle_mc_tcp_read_random(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: McRandomReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut addrs = Vec::with_capacity(payload.addresses.len());
    for a in &payload.addresses {
        match crate::mc_address::parse_mc_address(a) {
            Ok(addr) => addrs.push(addr),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let is_bit = addrs.first().map(|a| a.is_bit).unwrap_or(false);
    let req_data = match crate::mc_pdu::build_read_random_pdu(&addrs) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_random_response(&resp.data, addrs.len(), is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": is_bit, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomWritePayload {
    connection_id: String,
    /// 地址 → 值 的有序对
    entries: Vec<McRandomWriteEntry>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McRandomWriteEntry {
    address: String,
    value: u16,
}

fn handle_mc_tcp_write_random(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: McRandomWritePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items = Vec::with_capacity(payload.entries.len());
    for e in &payload.entries {
        match crate::mc_address::parse_mc_address(&e.address) {
            Ok(a) => items.push((a, e.value)),
            Err(err) => return failure(Some(request_id.to_string()), err),
        }
    }
    let req_data = match crate::mc_pdu::build_write_random_word_pdu(&items) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McBlocksReadPayload {
    connection_id: String,
    blocks: Vec<McBlockPayload>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McBlockPayload {
    address: String,
    points: u16,
}

fn handle_mc_tcp_read_blocks(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: McBlocksReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut blocks = Vec::with_capacity(payload.blocks.len());
    for b in &payload.blocks {
        match crate::mc_address::parse_mc_address(&b.address) {
            Ok(a) => blocks.push(crate::mc_pdu::McBlock {
                address: a,
                points: b.points,
            }),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let req_data = match crate::mc_pdu::build_read_blocks_pdu(&blocks) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_blocks_response(&resp.data, &blocks) {
        Ok(chunks) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "blocks": chunks }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McConnOnlyPayload {
    connection_id: String,
}

fn handle_mc_remote(
    session: &mut Session,
    request_id: &str,
    cmd: u16,
    payload: Value,
) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = match crate::mc_pdu::build_remote_control_pdu(cmd) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    success(
        request_id.to_string(),
        json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
        false,
    )
}

fn handle_mc_read_clock(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = crate::mc_pdu::build_read_clock_pdu();
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_clock_response(&resp.data) {
        Ok(c) => success(
            request_id.to_string(),
            json!({
                "endCode": 0,
                "clock": {
                    "yearBCD": c.year, "monthBCD": c.month, "dayBCD": c.day,
                    "hourBCD": c.hour, "minuteBCD": c.minute, "secondBCD": c.second, "weekdayBCD": c.weekday,
                    "year": bcd(c.year), "month": bcd(c.month), "day": bcd(c.day),
                    "hour": bcd(c.hour), "minute": bcd(c.minute), "second": bcd(c.second), "weekday": bcd(c.weekday),
                }
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn bcd(v: u8) -> u8 {
    (v >> 4) * 10 + (v & 0x0F)
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct McEchoPayload {
    connection_id: String,
    #[serde(default = "default_echo_payload")]
    data: Vec<u8>,
}

fn default_echo_payload() -> Vec<u8> {
    vec![0xAB, 0xCD, 0xEF]
}

fn handle_mc_echo_test(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McEchoPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = crate::mc_pdu::build_echo_test_pdu(&payload.data);
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    let matched =
        crate::mc_pdu::parse_echo_test_response(&resp.data, &payload.data).unwrap_or(false);
    success(
        request_id.to_string(),
        json!({ "endCode": 0, "matched": matched, "echoed": resp.data }),
        false,
    )
}

fn handle_mc_cpu_info(
    session: &mut Session,
    request_id: &str,
    kind: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: McConnOnlyPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let req_data = if kind == "type" {
        crate::mc_pdu::build_read_cpu_type_pdu()
    } else {
        crate::mc_pdu::build_read_cpu_status_pdu()
    };
    let resp = match session.mc_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    if kind == "type" {
        match crate::mc_pdu::parse_read_cpu_type_response(&resp.data) {
            Ok(t) => success(
                request_id.to_string(),
                json!({ "endCode": 0, "cpuType": t }),
                false,
            ),
            Err(e) => failure(Some(request_id.to_string()), e),
        }
    } else {
        match crate::mc_pdu::parse_read_cpu_status_response(&resp.data) {
            Ok(s) => {
                let status = match s {
                    crate::mc_pdu::CpuStatus::Run => "RUN",
                    crate::mc_pdu::CpuStatus::Stop => "STOP",
                    crate::mc_pdu::CpuStatus::Pause => "PAUSE",
                    crate::mc_pdu::CpuStatus::Other(v) => {
                        return success(
                            request_id.to_string(),
                            json!({ "endCode": 0, "cpuStatus": format!("OTHER({v:#04x})") }),
                            false,
                        );
                    }
                };
                success(
                    request_id.to_string(),
                    json!({ "endCode": 0, "cpuStatus": status }),
                    false,
                )
            }
            Err(e) => failure(Some(request_id.to_string()), e),
        }
    }
}

fn handle_mc_build_ascii_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        points: u16,
        #[serde(default = "default_ascii_watchdog")]
        watchdog: u16,
    }
    fn default_ascii_watchdog() -> u16 {
        0x0010
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_ascii::build_ascii_read_request(
        crate::mc_frame::FrameType::Type3E,
        0,
        &crate::mc_frame::AccessRoute::default(),
        p.watchdog,
        &p.address,
        p.points,
    ) {
        Ok(s) => success(request_id.to_string(), json!({ "ascii": s }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_mc_ascii(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "MC_BAD_FRAME_TYPE",
                    message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                    details: None,
                },
            );
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_tcp_ascii(
        &payload.connection_id,
        &payload.host,
        payload.port,
        route,
        frame_type,
        payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "mode": "ascii" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_ascii_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_transact_ascii_read(&payload.connection_id, &payload.address, payload.points) {
        Ok((end_code, is_bit, values)) => {
            if end_code != 0 {
                return success(
                    request_id.to_string(),
                    json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
                    false,
                );
            }
            success(
                request_id.to_string(),
                json!({ "endCode": 0, "isBit": is_bit, "values": values }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// open_mc_1e_tcp:A-1E/SLMP-1E TCP 连接(A 系列 E71 / FX3U-ENET / FX5U)。
// ============ 西门子 S7comm ============

fn handle_brand_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        brand: String,
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let profile = match p.brand.as_str() {
        "delta-es" => crate::brand_profiles::BrandProfile::DeltaDvpEs,
        "inovance-h3u" => crate::brand_profiles::BrandProfile::InovanceH3u,
        "inovance-h5u" => crate::brand_profiles::BrandProfile::InovanceH5u,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "BRAND_UNKNOWN",
                    message: format!(
                        "未知品牌「{other}」(当前支持: delta-es / inovance-h3u / inovance-h5u)·汇川/信捷映射待手册确认后加入"
                    ),
                    details: None,
                },
            );
        }
    };
    match crate::brand_profiles::parse_brand_address(profile, &p.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "area": match a.area {
                    crate::brand_profiles::BrandArea::Coil => "coil",
                    crate::brand_profiles::BrandArea::DiscreteInput => "discrete",
                    crate::brand_profiles::BrandArea::HoldingRegister => "holding",
                },
                "modbusAddress": a.modbus_address,
                "modbusAddressHex": format!("0x{:04X}", a.modbus_address),
                "isBit": a.is_bit,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_delta_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        series: String,
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let series = match p.series.trim().to_ascii_lowercase().as_str() {
        "dvp" | "delta-dvp" => crate::delta::Series::Dvp,
        "as" | "delta-as" => crate::delta::Series::As,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "DELTA_SERIES_INVALID",
                    message: format!("未知 Delta 系列「{other}」(当前支持 DVP / AS)"),
                    details: None,
                },
            );
        }
    };
    match crate::delta::parse_address(series, &p.address) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "series": crate::delta::series_name(parsed.series),
                "canonical": parsed.canonical,
                "area": parsed.area,
                "modbusAddress": parsed.modbus_address,
                "modbusAddressHex": format!("0x{:04X}", parsed.modbus_address),
                "readFunction": parsed.read_function,
                "writeFunction": parsed.write_function,
                "isBit": parsed.is_bit,
                "readOnly": parsed.read_only,
                "transport": "modbus-profile",
                "evidence": "software-address-map; model-and-L2-pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// ============ 欧姆龙 FINS ============

fn fins_nodes_from_payload(payload: &Value) -> crate::fins_frame::FinsNodes {
    let mut n = crate::fins_frame::FinsNodes::default();
    if let Some(d) = payload.get("destNode").and_then(|v| v.as_u64()) {
        n.da1 = d as u8;
    }
    if let Some(s) = payload.get("sourceNode").and_then(|v| v.as_u64()) {
        n.sa1 = s as u8;
    }
    n
}

fn handle_inovance_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        series: String,
        address: String,
        #[serde(default)]
        kind: Option<String>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let series_text = p.series.trim().to_ascii_lowercase();
    let series = match series_text.as_str() {
        "h3u" | "inovance-h3u" => crate::inovance::Series::H3u,
        "h5u" | "inovance-h5u" => crate::inovance::Series::H5u,
        "am" | "am-series" | "inovance-am" => crate::inovance::Series::Am,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "INOVANCE_SERIES_INVALID",
                    message: format!(
                        "未知汇川系列「{other}」(当前边界 H3U / H5U；AM 显式保留为未确认)"
                    ),
                    details: None,
                },
            );
        }
    };
    let kind_text = p
        .kind
        .as_deref()
        .unwrap_or("auto")
        .trim()
        .to_ascii_lowercase();
    let kind = match kind_text.as_str() {
        "auto" | "" => crate::inovance::AccessKind::Auto,
        "bit" | "bool" | "coil" => crate::inovance::AccessKind::Bit,
        "word" | "register" | "holding" => crate::inovance::AccessKind::Word,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "INOVANCE_KIND_INVALID",
                    message: format!("未知汇川访问类型「{other}」(使用 auto / bit / word)"),
                    details: None,
                },
            );
        }
    };
    match crate::inovance::parse_address(series, &p.address, kind) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "series": crate::inovance::series_name(parsed.series),
                "canonical": parsed.canonical,
                "area": parsed.area,
                "modbusAddress": parsed.modbus_address,
                "modbusAddressHex": format!("0x{:04X}", parsed.modbus_address),
                "readFunction": parsed.read_function,
                "writeFunction": parsed.write_function,
                "isBit": parsed.is_bit,
                "readOnly": parsed.read_only,
                "registerWidth": parsed.register_width,
                "underlyingProtocol": "Modbus RTU / Modbus TCP",
                "evidence": "vendor-address-profile; L2 pending",
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_xinjie_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        series: String,
        address: String,
        #[serde(default)]
        kind: Option<String>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let series_text = p.series.trim().to_ascii_lowercase();
    let series = match series_text.as_str() {
        "xc" | "xinje-xc" => crate::xinjie::Series::Xc,
        "xd" | "xl" | "xinje-xd" | "xinje-xl" => crate::xinjie::Series::Xd,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "XINJE_SERIES_INVALID",
                    message: format!("未知信捷系列「{other}」(当前边界 XC / XD-XL)"),
                    details: None,
                },
            );
        }
    };
    let kind_text = p
        .kind
        .as_deref()
        .unwrap_or("auto")
        .trim()
        .to_ascii_lowercase();
    let kind = match kind_text.as_str() {
        "auto" | "" => crate::xinjie::AccessKind::Auto,
        "word" | "register" | "holding" => crate::xinjie::AccessKind::Word,
        "bit" | "bool" | "coil" => crate::xinjie::AccessKind::Bit,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "XINJE_KIND_INVALID",
                    message: format!("未知信捷访问类型「{other}」(使用 auto / word)"),
                    details: None,
                },
            );
        }
    };
    match crate::xinjie::parse_address(series, &p.address, kind) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "series": crate::xinjie::series_name(parsed.series),
                "canonical": parsed.canonical,
                "area": parsed.area,
                "modbusAddress": parsed.modbus_address,
                "modbusAddressHex": format!("0x{:04X}", parsed.modbus_address),
                "readFunction": parsed.read_function,
                "writeFunction": parsed.write_function,
                "isBit": parsed.is_bit,
                "readOnly": parsed.read_only,
                "registerWidth": parsed.register_width,
                "underlyingProtocol": "Modbus RTU / Modbus TCP",
                "evidence": "D-register-confirmed; other-areas-model-manual-pending",
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fatek_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        #[serde(default)]
        kind: Option<String>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let kind = match p
        .kind
        .as_deref()
        .unwrap_or("auto")
        .trim()
        .to_ascii_lowercase()
        .as_str()
    {
        "auto" | "" => crate::fatek::AccessKind::Auto,
        "bit" | "bool" => crate::fatek::AccessKind::Bit,
        "word" | "register" => crate::fatek::AccessKind::Word,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "FATEK_KIND_INVALID",
                    message: format!("未知 FATEK 访问类型「{other}」(使用 auto / bit / word)"),
                    details: None,
                },
            );
        }
    };
    match crate::fatek::parse_address(&p.address, kind) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "canonical": address.canonical,
                "dataCode": address.data_code,
                "number": address.number,
                "isDiscrete": address.is_discrete,
                "kind": if address.is_discrete { "bit" } else { "word" },
                "underlyingProtocol": "FATEK native ASCII over TCP",
                "evidence": "documented-codec; L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_open_fatek_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenFatekConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_fatek(&p.connection_id, &p.host, p.port, p.station) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "station": p.station,
                "stationHex": format!("0x{:02X}", p.station),
                "transport": "tcp",
                "sessionInitialized": true,
                "handshake": false,
                "readOnly": true,
                "underlyingProtocol": "FATEK native ASCII over TCP",
                "l2Evidence": "independent TCP endpoint only; FBs/Gateway L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_read_words(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: FatekReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fatek_read_words(&p.connection_id, &p.address, p.count) {
        Ok((request, parsed)) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "request": request,
                "requestHex": format_hex(&request),
                "command": parsed.command,
                "station": parsed.station,
                "status": parsed.status.to_string(),
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "dataAscii": String::from_utf8_lossy(&parsed.data),
                "expectedDataBytes": usize::from(p.count) * 4,
                "address": p.address,
                "count": p.count,
                "readOnly": true,
                "underlyingProtocol": "FATEK native ASCII over TCP",
                "l2Evidence": "independent TCP endpoint only; FBs/Gateway L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_read_discrete(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: FatekReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fatek_read_discrete(&p.connection_id, &p.address, p.count) {
        Ok((request, parsed)) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "request": request,
                "requestHex": format_hex(&request),
                "command": parsed.command,
                "station": parsed.station,
                "status": parsed.status.to_string(),
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "dataAscii": String::from_utf8_lossy(&parsed.data),
                "expectedDataBytes": p.count,
                "address": p.address,
                "count": p.count,
                "readOnly": true,
                "underlyingProtocol": "FATEK native ASCII over TCP",
                "l2Evidence": "independent TCP endpoint only; FBs/Gateway L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_pack_command(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        command: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::pack_command(p.station, &p.command) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_build_read_discrete(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::build_read_discrete(p.station, &p.address, p.count) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "command": "44" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_build_write_discrete(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        values: Vec<bool>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::build_write_discrete(p.station, &p.address, &p.values) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "command": "45" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_build_read_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::build_read_words(p.station, &p.address, p.count) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "command": "46" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_build_write_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::build_write_words(p.station, &p.address, &p.data) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "command": "47" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fatek_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        command: String,
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fatek::parse_response(&p.response, p.station, &p.command) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "station": parsed.station,
                "command": parsed.command,
                "status": parsed.status.to_string(),
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "dataAscii": String::from_utf8_lossy(&parsed.data),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fuji_sph_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fuji_sph::parse_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "canonical": address.canonical,
                "area": address.area,
                "typeCode": address.type_code,
                "typeCodeHex": format!("0x{:02X}", address.type_code),
                "wordAddress": address.word_address,
                "bitIndex": address.bit_index,
                "underlyingProtocol": "Fuji MICREX-SX SPH Loader Command",
                "defaultPort": crate::fuji_sph::DEFAULT_PORT,
                "evidence": "documented-codec; L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_open_fuji_sph_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenFujiSphConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_fuji_sph(&p.connection_id, &p.host, p.port, p.connection_id_byte) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "connectionIdByte": p.connection_id_byte,
                "connectionIdHex": format!("0x{:02X}", p.connection_id_byte),
                "transport": "tcp",
                "sessionInitialized": true,
                "handshake": false,
                "readOnly": true,
                "underlyingProtocol": "Fuji MICREX-SX SPH Loader Command",
                "l2Evidence": "independent TCP endpoint only; SPH CPU/firmware L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fuji_sph_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: FujiSphReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let expected_data_bytes = usize::from(p.words) * 2;
    match session.fuji_sph_read(&p.connection_id, &p.address, p.words) {
        Ok((request, parsed)) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "request": request,
                "requestHex": format_hex(&request),
                "responseLength": parsed.data.len() + crate::fuji_sph::FRAME_BYTES,
                "expectedDataBytes": expected_data_bytes,
                "cpuErrorCode": parsed.error_code,
                "typeCode": parsed.type_code,
                "wordAddress": parsed.word_address,
                "words": parsed.words,
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "address": p.address,
                "readOnly": true,
                "underlyingProtocol": "Fuji MICREX-SX SPH Loader Command",
                "l2Evidence": "independent TCP endpoint only; SPH CPU/firmware L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fuji_sph_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: u8,
        address: String,
        words: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fuji_sph::build_read(p.connection_id, &p.address, p.words) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
                "command": crate::fuji_sph::READ,
                "connectionId": p.connection_id,
                "underlyingProtocol": "Fuji MICREX-SX SPH Loader Command",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fuji_sph_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: u8,
        address: String,
        data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fuji_sph::build_write(p.connection_id, &p.address, &p.data) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
                "command": crate::fuji_sph::WRITE,
                "connectionId": p.connection_id,
                "underlyingProtocol": "Fuji MICREX-SX SPH Loader Command",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fuji_sph_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: u8,
        command: u8,
        expected_data_bytes: usize,
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fuji_sph::parse_response(
        &p.response,
        p.connection_id,
        p.command,
        p.expected_data_bytes,
    ) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "connectionId": parsed.connection_id,
                "command": parsed.command,
                "errorCode": parsed.error_code,
                "typeCode": parsed.type_code,
                "wordAddress": parsed.word_address,
                "words": parsed.words,
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ge_srtp::parse_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "canonical": address.canonical,
                "area": address.area,
                "byteDataCode": address.byte_data_code,
                "byteDataCodeHex": format!("0x{:02X}", address.byte_data_code),
                "bitDataCode": address.bit_data_code,
                "bitDataCodeHex": address.bit_data_code.map(|code| format!("0x{code:02X}")),
                "isWordArea": address.is_word_area,
                "offset": address.offset,
                "reference": u32::from(address.offset) + 1,
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
                "defaultPort": crate::ge_srtp::DEFAULT_PORT,
                "evidence": "documented-codec; L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_open_ge_srtp_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenGeSrtpConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_ge_srtp(&p.connection_id, &p.host, p.port) {
        Ok(handshake) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "transport": "tcp",
                "sessionInitialized": true,
                "responseType": handshake.response_type,
                "marker": handshake.marker,
                "payloadLength": handshake.payload_length,
                "readOnly": true,
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
                "l2Evidence": "independent TCP endpoint only; real PLC L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: GeSrtpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let expected_data_length =
        match crate::ge_srtp::expected_read_data_bytes(&p.address, p.element_count, p.bit_access) {
            Ok(length) => length,
            Err(error) => return failure(Some(request_id.to_string()), error),
        };
    match session.ge_srtp_read(&p.connection_id, &p.address, p.element_count, p.bit_access) {
        Ok((request, parsed)) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "request": request,
                "requestHex": format_hex(&request),
                "transactionId": parsed.transaction_id,
                "responseForm": parsed.response_form,
                "declaredLength": parsed.declared_length,
                "expectedDataLength": expected_data_length,
                "plcStatus": parsed.plc_status,
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "address": p.address,
                "elementCount": p.element_count,
                "bitAccess": p.bit_access,
                "readOnly": true,
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
                "l2Evidence": "independent TCP endpoint only; real PLC L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_build_handshake(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {}
    if serde_json::from_value::<P>(payload).is_err() {
        return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope);
    }
    let frame = crate::ge_srtp::build_handshake();
    success(
        request_id.to_string(),
        json!({
            "frame": frame,
            "frameHex": format_hex(&frame),
            "headerBytes": crate::ge_srtp::HEADER_BYTES,
            "defaultPort": crate::ge_srtp::DEFAULT_PORT,
            "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
        }),
        false,
    )
}

fn handle_ge_srtp_parse_handshake(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ge_srtp::parse_handshake(&p.response) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "responseType": parsed.response_type,
                "marker": parsed.marker,
                "payloadLength": parsed.payload_length,
                "sessionInitialized": true,
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        transaction_id: u16,
        address: String,
        element_count: u16,
        #[serde(default)]
        bit_access: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ge_srtp::build_read(p.transaction_id, &p.address, p.element_count, p.bit_access) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
                "command": crate::ge_srtp::READ,
                "transactionId": p.transaction_id,
                "bitAccess": p.bit_access,
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        transaction_id: u16,
        address: String,
        data: Vec<u8>,
        element_count: u16,
        #[serde(default)]
        bit_access: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ge_srtp::build_write(
        p.transaction_id,
        &p.address,
        &p.data,
        p.element_count,
        p.bit_access,
    ) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": format_hex(&frame),
                "command": crate::ge_srtp::WRITE,
                "transactionId": p.transaction_id,
                "bitAccess": p.bit_access,
                "dataBytes": p.data.len(),
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ge_srtp_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        transaction_id: u16,
        expected_data_length: usize,
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ge_srtp::parse_response(&p.response, p.transaction_id, p.expected_data_length) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "transactionId": parsed.transaction_id,
                "responseForm": parsed.response_form,
                "declaredLength": parsed.declared_length,
                "plcStatus": parsed.plc_status,
                "data": parsed.data,
                "dataHex": format_hex(&parsed.data),
                "underlyingProtocol": "GE Series 90 / PACSystems SRTP",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_fins_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::fins_address::parse_fins_address(&p.address) {
        Ok(a) => success(
            request_id.to_string(),
            json!({
                "areaCode": format!("0x{:02X}", a.area_code),
                "address": a.address,
                "kind": format!("{:?}", a.kind),
                "wordBitFlag": a.word_bit_flag(),
                "encoded": a.encode().iter().map(|b| format!("{b:02X}")).collect::<String>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_fins_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default)]
        dest_node: Option<u8>,
        #[serde(default)]
        source_node: Option<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut nodes = fins_nodes_from_payload(&json!({
        "destNode": p.dest_node, "sourceNode": p.source_node
    }));
    nodes.da1 = p.dest_node.unwrap_or(0);
    nodes.sa1 = p.source_node.unwrap_or(0);
    match session.open_fins_tcp(&p.connection_id, &p.host, p.port, nodes) {
        Ok(_) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "transport": "tcp", "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_fins_port() -> u16 {
    9600
}

fn handle_open_fins_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default)]
        dest_node: Option<u8>,
        #[serde(default)]
        source_node: Option<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut nodes = crate::fins_frame::FinsNodes::default();
    nodes.da1 = p.dest_node.unwrap_or(0);
    nodes.sa1 = p.source_node.unwrap_or(0);
    match session.open_fins_udp(&p.connection_id, &p.host, p.port, nodes) {
        Ok(_) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "transport": "udp", "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        #[serde(default = "one_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_read(&p.connection_id, &p.address, p.count) {
        Ok((end_code, data)) => {
            if end_code != 0 {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "FINS_CPU_ERROR",
                        message: format!(
                            "FINS 结束码 0x{end_code:04X}:{}",
                            crate::fins_frame::end_code_message(end_code)
                        ),
                        details: Some(json!({ "endCode": end_code })),
                    },
                );
            }
            // 字访问:字节 → u16 大端;位访问:每字节 0/1
            let addr = crate::fins_address::parse_fins_address(&p.address);
            let is_bit = matches!(&addr, Ok(a) if a.kind == crate::fins_address::FinsKind::Bit);
            let values: Vec<u16> = if is_bit {
                data.iter().map(|b| *b as u16).collect()
            } else {
                data.chunks_exact(2)
                    .map(|c| u16::from_be_bytes([c[0], c[1]]))
                    .collect()
            };
            success(
                request_id.to_string(),
                json!({ "endCode": end_code, "data": data, "values": values, "isBit": is_bit }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn one_count() -> u16 {
    1
}

fn handle_fins_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::fins_address::parse_fins_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = addr.kind == crate::fins_address::FinsKind::Bit;
    let count = match u16::try_from(p.values.len()) {
        Ok(count) => count,
        Err(_) => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "FINS_BATCH_LIMIT",
                    message: format!(
                        "FINS 写入点数超过软件安全上限 {}",
                        crate::fins_frame::FINS_MAX_POINTS
                    ),
                    details: Some(
                        json!({ "maximum": crate::fins_frame::FINS_MAX_POINTS, "actual": p.values.len() }),
                    ),
                },
            );
        }
    };
    if let Err(e) = crate::fins_frame::validate_access_window(&addr, count) {
        return failure(Some(request_id.to_string()), e);
    }
    if is_bit && p.values.iter().any(|value| *value > 1) {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "FINS_BIT_VALUE_INVALID",
                message: "FINS 位写入值只能是 0 或 1".to_string(),
                details: Some(json!({ "allowed": [0, 1] })),
            },
        );
    }
    let data: Vec<u8> = if is_bit {
        p.values.iter().map(|v| *v as u8).collect()
    } else {
        p.values.iter().flat_map(|v| v.to_be_bytes()).collect()
    };
    match session.fins_write(&p.connection_id, &p.address, count, &data) {
        Ok(end_code) => {
            if end_code != 0 {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "FINS_CPU_ERROR",
                        message: format!(
                            "FINS 结束码 0x{end_code:04X}:{}",
                            crate::fins_frame::end_code_message(end_code)
                        ),
                        details: Some(json!({ "endCode": end_code })),
                    },
                );
            }
            success(
                request_id.to_string(),
                json!({ "endCode": end_code }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_fins_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        #[serde(default = "default_fins_port")]
        port: u16,
        #[serde(default = "default_true_fins")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_fins_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "fins", "transports": ["tcp", "udp"] }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_true_fins() -> bool {
    true
}

fn handle_stop_fins_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_fins_slave(&p.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": p.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_slave_set(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_slave_set(&p.slave_id, &p.address, &p.values) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fins_slave_get(session: &Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        #[serde(default = "one_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.fins_slave_get(&p.slave_id, &p.address, p.count) {
        Ok(values) => success(request_id.to_string(), json!({ "values": values }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::s7_address::parse_s7_address(&p.address) {
        Ok(addr) => success(
            request_id.to_string(),
            json!({
                "area": addr.area,
                "areaName": crate::s7_address::area_name(addr.area),
                "db": addr.db,
                "byte": addr.byte,
                "bit": addr.bit,
                "kind": format!("{:?}", addr.kind),
                "elemBytes": addr.kind.elem_bytes(),
                "anyAddressHex": addr.encode_any_address().iter().map(|b| format!("{b:02X}")).collect::<String>(),
                "display": addr.display(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_s7_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        #[serde(default = "default_s7_port")]
        port: u16,
        #[serde(default)]
        rack: u8,
        #[serde(default = "default_s7_slot")]
        slot: u8,
        /// 1=PG(默认) 2=OP 3=S7 Basic
        #[serde(default)]
        conn_type: u8,
        /// 十六进制 TSAP 覆盖(如 "0100");缺省用 rack/slot 公式
        #[serde(default)]
        local_tsap: Option<String>,
        #[serde(default)]
        remote_tsap: Option<String>,
        /// 0 = 默认 480
        #[serde(default)]
        pdu_request: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let parse_tsap = |s: &Option<String>, field: &str| -> Result<Option<u16>, CoreError> {
        match s {
            None => Ok(None),
            Some(hex) => u16::from_str_radix(hex.trim_start_matches("0x"), 16)
                .map(Some)
                .map_err(|_| CoreError::Modbus {
                    code: "S7_TSAP_INVALID",
                    message: format!("{field} 应为十六进制(如 0100),实际「{}」", hex),
                    details: None,
                }),
        }
    };
    let local = match parse_tsap(&p.local_tsap, "localTsap") {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let remote = match parse_tsap(&p.remote_tsap, "remoteTsap") {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.open_s7_connection(
        &p.connection_id,
        &p.host,
        p.port,
        p.rack,
        p.slot,
        p.conn_type,
        local,
        remote,
        p.pdu_request,
    ) {
        Ok(pdu_size) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "pduSize": pdu_size,
                "maxReadBytes": crate::s7_pdu::max_read_bytes(pdu_size),
                "maxWriteBytes": crate::s7_pdu::max_write_bytes(pdu_size),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// s7_read 请求项。
#[derive(Debug, Deserialize, Clone)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct S7ItemPayload {
    address: String,
    #[serde(default = "default_s7_count")]
    count: u16,
}

fn default_s7_count() -> u16 {
    1
}
fn default_s7_port() -> u16 {
    102
}
fn default_s7_seed() -> bool {
    true
}
fn default_s7_slot() -> u8 {
    1
}

/// 按 PDU 预算与 20 项上限把请求项拆成多轮。
/// 每轮元素为 (原始项索引, 子项):分片子项共享原始索引,供结果合并。
fn s7_chunk_items(
    items: &[crate::s7_pdu::S7Item],
    budget_bytes: usize,
) -> Vec<Vec<(usize, crate::s7_pdu::S7Item)>> {
    let mut chunks: Vec<Vec<(usize, crate::s7_pdu::S7Item)>> = Vec::new();
    let mut cur: Vec<(usize, crate::s7_pdu::S7Item)> = Vec::new();
    let mut cur_bytes = 0usize;
    for (origin, item) in items.iter().enumerate() {
        let bytes = item.data_bytes();
        if bytes > budget_bytes {
            // 单项超预算:按元素宽度拆成多个子项(每子项独占一轮)
            let per = (budget_bytes / item.addr.kind.elem_bytes() as usize).max(1);
            let mut remaining = item.count as usize;
            let mut consumed = 0usize;
            while remaining > 0 {
                let take = remaining.min(per).min(u16::MAX as usize) as u16;
                let mut sub = item.clone();
                sub.count = take;
                // 每个后续分片必须从前一片末尾继续，不能重复读取原始起点。
                match item.addr.kind {
                    crate::s7_address::S7Kind::Bit => {
                        let absolute_bit =
                            item.addr.byte as usize * 8 + item.addr.bit as usize + consumed;
                        sub.addr.byte = (absolute_bit / 8) as u32;
                        sub.addr.bit = (absolute_bit % 8) as u8;
                    }
                    crate::s7_address::S7Kind::Timer | crate::s7_address::S7Kind::Counter => {
                        sub.addr.byte = item.addr.byte + consumed as u32;
                    }
                    _ => {
                        sub.addr.byte = item.addr.byte
                            + (consumed * item.addr.kind.elem_bytes() as usize) as u32;
                    }
                }
                remaining -= take as usize;
                consumed += take as usize;
                chunks.push(vec![(origin, sub)]);
            }
            continue;
        }
        if cur.len() >= crate::s7_pdu::MAX_ITEMS || cur_bytes + bytes > budget_bytes {
            if !cur.is_empty() {
                chunks.push(std::mem::take(&mut cur));
                cur_bytes = 0;
            }
        }
        cur_bytes += bytes;
        cur.push((origin, item.clone()));
    }
    if !cur.is_empty() {
        chunks.push(cur);
    }
    chunks
}

fn handle_s7_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        /// 单项:[{address, count}];字符串简写 "DB1.DBW0" 等价 {address, count:1}
        items: Vec<serde_json::Value>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items: Vec<crate::s7_pdu::S7Item> = Vec::with_capacity(p.items.len());
    for raw in &p.items {
        let item_payload: S7ItemPayload = match raw {
            Value::String(s) => S7ItemPayload {
                address: s.clone(),
                count: 1,
            },
            other => match serde_json::from_value(other.clone()) {
                Ok(v) => v,
                Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
            },
        };
        match crate::s7_pdu::S7Item::new(&item_payload.address, item_payload.count) {
            Ok(it) => items.push(it),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let pdu_size = match session.s7_pdu_size(&p.connection_id) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let budget = crate::s7_pdu::max_read_bytes(pdu_size);
    let item_count = items.len();
    let chunks = s7_chunk_items(&items, budget);
    // 原始项索引 → (returnCode, 数据拼接)
    let mut merged: Vec<(u8, Vec<u8>)> = vec![(0xFF, Vec::new()); item_count];
    for chunk in chunks {
        let sub_items: Vec<crate::s7_pdu::S7Item> =
            chunk.iter().map(|(_, it)| it.clone()).collect();
        match session.s7_read(&p.connection_id, &sub_items) {
            Ok(parts) => {
                for (i, part) in parts.iter().enumerate() {
                    let (origin, sub) = match chunk.get(i) {
                        Some(v) => v,
                        None => continue, // 防御:响应 item 数 < 请求数时跳过(不 panic)
                    };
                    // 分片子项:期望字节数按子项 count 计
                    let exp = sub.data_bytes();
                    let mut data = part.data.clone();
                    data.truncate(exp);
                    merged[*origin].0 = part.return_code;
                    merged[*origin].1.extend(data);
                }
            }
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let results: Vec<Value> = merged
        .into_iter()
        .map(|(rc, data)| {
            json!({
                "returnCode": rc,
                "returnCodeMessage": crate::s7_pdu::item_return_code_message(rc),
                "data": data,
            })
        })
        .collect();
    success(request_id.to_string(), json!({ "items": results }), false)
}

fn handle_s7_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct ItemIn {
        address: String,
        #[serde(default)]
        count: Option<u16>,
        /// 字节数组(10 进制);位写时每字节 1 个位
        values: Option<Vec<u8>>,
        /// 或 hex 字符串(优先级低于 values)
        hex: Option<String>,
    }
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        items: Vec<ItemIn>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let mut items: Vec<crate::s7_pdu::S7Item> = Vec::new();
    let mut blocks: Vec<Vec<u8>> = Vec::new();
    for it in &p.items {
        let data: Vec<u8> = if let Some(v) = &it.values {
            v.clone()
        } else if let Some(hex) = &it.hex {
            match hex_parse_bytes(hex) {
                Some(b) => b,
                None => {
                    return failure(
                        Some(request_id.to_string()),
                        CoreError::Modbus {
                            code: "S7_WRITE_MISMATCH",
                            message: format!("hex「{}」不合法", hex),
                            details: None,
                        },
                    );
                }
            }
        } else {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "S7_WRITE_MISMATCH",
                    message: format!("项 {} 缺少 values/hex", it.address),
                    details: None,
                },
            );
        };
        let count =
            it.count
                .unwrap_or_else(|| match crate::s7_address::parse_s7_address(&it.address) {
                    Ok(a) if a.kind == crate::s7_address::S7Kind::Bit => data.len() as u16,
                    Ok(a) => (data.len() as u16) / a.kind.elem_bytes() as u16,
                    Err(_) => 1,
                });
        match crate::s7_pdu::S7Item::new(&it.address, count) {
            Ok(item) => {
                items.push(item);
                blocks.push(data);
            }
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
    }
    let pdu_size = match session.s7_pdu_size(&p.connection_id) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let budget = crate::s7_pdu::max_write_bytes(pdu_size);
    // 简化:按 20 项一轮直接写(单轮超限由上层避免;S1 UI 单项写)
    let mut index = 0usize;
    let mut codes: Vec<u8> = Vec::new();
    while index < items.len() {
        let end = (index + crate::s7_pdu::MAX_ITEMS).min(items.len());
        let chunk = &items[index..end];
        let blocks_chunk = &blocks[index..end];
        let total: usize = chunk.iter().map(|i| i.data_bytes()).sum();
        if total > budget {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "S7_DATA_OVER_PDU",
                    message: format!(
                        "写数据总量 {total} 字节超过 PDU 预算 {budget}(协商 PDU={pdu_size}),请分次写"
                    ),
                    details: None,
                },
            );
        }
        match session.s7_write(&p.connection_id, chunk, blocks_chunk) {
            Ok(rcs) => codes.extend(rcs),
            Err(e) => return failure(Some(request_id.to_string()), e),
        }
        index = end;
    }
    success(
        request_id.to_string(),
        json!({
            "returnCodes": codes,
            "returnCodeMessages": codes
                .iter()
                .map(|c| crate::s7_pdu::item_return_code_message(*c))
                .collect::<Vec<_>>(),
        }),
        false,
    )
}

fn hex_parse_bytes(hex: &str) -> Option<Vec<u8>> {
    let clean: String = hex.chars().filter(|c| !c.is_whitespace()).collect();
    if clean.len() % 2 != 0 {
        return None;
    }
    (0..clean.len())
        .step_by(2)
        .map(|i| u8::from_str_radix(&clean[i..i + 2], 16).ok())
        .collect()
}

fn handle_start_s7_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        #[serde(default = "default_s7_port")]
        port: u16,
        #[serde(default = "default_s7_seed")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_s7_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "s7comm", "pduLimit": crate::s7_slave::SLAVE_PDU_LIMIT }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_s7_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_s7_slave(&p.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": p.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_slave_set(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        values: Option<Vec<u8>>,
        hex: Option<String>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bytes: Vec<u8> = if let Some(v) = p.values {
        v
    } else if let Some(hex) = p.hex {
        match hex_parse_bytes(&hex) {
            Some(b) => b,
            None => {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "S7_SLAVE_WRITE_FAILED",
                        message: format!("hex「{hex}」不合法"),
                        details: None,
                    },
                );
            }
        }
    } else {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_SLAVE_WRITE_FAILED",
                message: "缺少 values/hex".to_string(),
                details: None,
            },
        );
    };
    match session.s7_slave_set(&p.slave_id, &p.address, &bytes) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "ok": true, "address": p.address }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_slave_get(session: &Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        address: String,
        #[serde(default = "default_s7_count")]
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_slave_get(&p.slave_id, &p.address, p.count) {
        Ok(data) => success(
            request_id.to_string(),
            json!({ "address": p.address, "data": data }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_ppi_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        port: u16,
        #[serde(default = "default_ppi_station")]
        station: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_ppi_tcp(&p.connection_id, &p.host, p.port, p.station) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "station": p.station, "note": "串口形态请用主站页串口 + ppi framing" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn default_ppi_station() -> u8 {
    2
}

/// 构造原生 COM PPI 只读第一拍。Electron 负责串口句柄和 E5/SA 双拍时序，
/// Rust 负责 S7 地址、PDU 和 PPI FCS，避免 UI/Node 各自复制一份帧规则。
fn handle_ppi_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(default = "default_ppi_station")]
        station: u8,
        #[serde(default)]
        master: u8,
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if p.station > 126 || p.master > 126 || p.count == 0 {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_PPI_PARAM_INVALID",
                message: "PPI 站号/主站号必须是 0..126，读取数量必须大于 0".to_string(),
                details: None,
            },
        );
    }
    let item = match crate::s7_pdu::S7Item::new(&p.address, p.count) {
        Ok(item) => item,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let pdu = match crate::s7_pdu::build_read_request(0, &[item]) {
        Ok(pdu) => pdu,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let frame = crate::ppi_frame::build_sd2(p.station, p.master, crate::ppi_frame::FC_READ, &pdu);
    success(
        request_id.to_string(),
        json!({
            "station": p.station,
            "master": p.master,
            "address": p.address,
            "count": p.count,
            "pdu": pdu,
            "frame": frame,
            "frameHex": format_hex(&frame),
        }),
        false,
    )
}

fn handle_ppi_build_sa_confirm(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(default = "default_ppi_station")]
        station: u8,
        #[serde(default)]
        master: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if p.station > 126 || p.master > 126 {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_PPI_PARAM_INVALID",
                message: "PPI 站号/主站号必须是 0..126".to_string(),
                details: None,
            },
        );
    }
    let frame = crate::ppi_frame::build_sa_confirm(p.station, p.master);
    success(
        request_id.to_string(),
        json!({ "station": p.station, "master": p.master, "frame": frame, "frameHex": format_hex(&frame) }),
        false,
    )
}

/// 解析原生 COM PPI 第二拍返回的 SD2，并保留每个 S7 数据项返回码。
fn handle_ppi_parse_read_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (destination, source, function_code, response_pdu) =
        match crate::ppi_frame::parse_sd2(&p.response) {
            Ok(value) => value,
            Err(error) => return failure(Some(request_id.to_string()), error),
        };
    let ack = match crate::s7_pdu::parse_ack(&response_pdu) {
        Ok(ack) => ack,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    if ack.error != 0 {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_CPU_ERROR",
                message: format!(
                    "PPI S7 响应错误 0x{:04X}:{}",
                    ack.error,
                    crate::s7_pdu::header_error_message(ack.error)
                ),
                details: Some(
                    json!({ "destination": destination, "source": source, "functionCode": function_code, "error": ack.error }),
                ),
            },
        );
    }
    let items = match crate::s7_pdu::parse_read_response(&ack) {
        Ok(items) => items,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    success(
        request_id.to_string(),
        json!({
            "destination": destination,
            "source": source,
            "functionCode": function_code,
            "pduRef": ack.pdu_ref,
            "items": items.iter().map(|item| json!({
                "returnCode": item.return_code,
                "returnCodeMessage": crate::s7_pdu::item_return_code_message(item.return_code),
                "data": item.data,
            })).collect::<Vec<_>>(),
        }),
        false,
    )
}

fn handle_ppi_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ppi_read(&p.connection_id, &p.address, p.count) {
        Ok(items) => success(
            request_id.to_string(),
            json!({
                "items": items.iter().map(|it| json!({
                    "returnCode": it.return_code,
                    "returnCodeMessage": crate::s7_pdu::item_return_code_message(it.return_code),
                    "data": it.data,
                })).collect::<Vec<_>>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_ppi_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        count: u16,
        values: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ppi_write(&p.connection_id, &p.address, p.count, &p.values) {
        Ok(codes) => success(
            request_id.to_string(),
            json!({
                "returnCodes": codes,
                "returnCodeMessages": codes.iter().map(|c| crate::s7_pdu::item_return_code_message(*c)).collect::<Vec<_>>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_ppi_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        port: u16,
        #[serde(default = "default_true_fins")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_ppi_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port, "protocol": "ppi-over-tcp" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_ppi_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_ppi_slave(&p.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": p.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_hostlink_build_fins(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        #[serde(default)]
        area: String,
        #[serde(default)]
        byte: u32,
        count: Option<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if p.station > 31 || p.byte > 0x00FF_FFFF {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "HOSTLINK_PARAM_INVALID",
                message: "HostLink FINS 站号必须是 0..31，地址必须是 0..0xFFFFFF".to_string(),
                details: None,
            },
        );
    }
    let area_code = match p.area.to_ascii_uppercase().as_str() {
        "" | "DM" => crate::fins_address::area::DM_WORD,
        "CIO" => crate::fins_address::area::CIO_WORD,
        "W" | "WR" => crate::fins_address::area::W_WORD,
        "H" | "HR" => crate::fins_address::area::H_WORD,
        "A" | "AR" => crate::fins_address::area::A_WORD,
        _ => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "HOSTLINK_PARAM_INVALID",
                    message: "HostLink FINS 首轮只支持 DM/CIO/W/WR/H/HR/A/AR 字区".to_string(),
                    details: None,
                },
            );
        }
    };
    let count = p.count.unwrap_or(1);
    if !(1..=100).contains(&count) {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "HOSTLINK_PARAM_INVALID",
                message: "HostLink FINS 首轮字读取数量必须是 1..100".to_string(),
                details: None,
            },
        );
    }
    let fins_addr = crate::fins_address::FinsAddress {
        area_code,
        address: p.byte,
        kind: crate::fins_address::FinsKind::Word,
    };
    if let Err(error) = crate::fins_frame::validate_access_window(&fins_addr, count) {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "HOSTLINK_PARAM_INVALID",
                message: format!("HostLink FINS 地址窗口无效: {error}"),
                details: Some(json!({ "address": p.byte, "count": count })),
            },
        );
    }
    let fins = crate::fins_frame::build_read_frame(
        &crate::fins_frame::FinsNodes::default(),
        1,
        &fins_addr,
        count,
    );
    let frame = crate::hostlink::build_hostlink_fins(p.station, &fins);
    success(
        request_id.to_string(),
        json!({
            "frame": frame,
            "frameText": String::from_utf8_lossy(&frame),
        }),
        false,
    )
}

fn handle_hostlink_parse_fins(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::hostlink::parse_hostlink_fins(&p.frame) {
        Ok(fins) => {
            let ack =
                crate::fins_frame::parse_response_frame(&fins).map_err(|e| CoreError::Modbus {
                    code: "HOSTLINK_FINS_INVALID",
                    message: e.to_string(),
                    details: None,
                });
            match ack {
                Ok(resp) => success(
                    request_id.to_string(),
                    json!({ "sid": resp.sid, "endCode": resp.end_code, "data": resp.data }),
                    false,
                ),
                Err(e) => failure(Some(request_id.to_string()), e),
            }
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_hostlink_build_cmode_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        dm_start: u16,
        word_count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let last = u32::from(p.dm_start) + u32::from(p.word_count).saturating_sub(1);
    if p.station > 31 || !(1..=100).contains(&p.word_count) || last > u32::from(u16::MAX) {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "HOSTLINK_PARAM_INVALID",
                message:
                    "HostLink C-mode 首轮要求站号 0..31、字数 1..100，且 DM 地址窗口不得超过 65535"
                        .to_string(),
                details: Some(
                    json!({ "station": p.station, "dmStart": p.dm_start, "wordCount": p.word_count }),
                ),
            },
        );
    }
    let frame = crate::hostlink::build_cmode_read_dm(p.station, p.dm_start, p.word_count);
    success(
        request_id.to_string(),
        json!({
            "frame": frame,
            "frameText": String::from_utf8_lossy(&frame),
        }),
        false,
    )
}

fn handle_hostlink_parse_cmode_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::hostlink::parse_cmode_read_dm(&p.frame) {
        Ok(words) => success(request_id.to_string(), json!({ "words": words }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_uss_build_request(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        #[serde(default)]
        param: Option<u16>,
        #[serde(default)]
        value: Option<u16>,
        #[serde(default)]
        pzd: Option<Vec<u8>>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let param = p.param.unwrap_or(0);
    let pzd = p.pzd.unwrap_or_default();
    if p.station > 30 || param > 0x0FFF || ![0, 2, 4, 8].contains(&pzd.len()) {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "USS_INVALID",
                message: "USS 参数读取要求站号 0..30、PNU 0..4095，PZD 长度为 0/2/4/8".to_string(),
                details: Some(
                    json!({ "station": p.station, "param": param, "pzdBytes": pzd.len() }),
                ),
            },
        );
    }
    let (pke, ind) = if let Some(v) = p.value {
        crate::uss_frame::pke_write_16(param, v)
    } else {
        (crate::uss_frame::pke_read(param), [0u8, 0])
    };
    let frame = crate::uss_frame::build_uss_request(p.station, pke, ind, &pzd);
    success(
        request_id.to_string(),
        json!({
            "frame": frame,
            "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
            "pkeAk": format!("0x{:X}", pke[0] >> 4),
            "pkeAkMessage": crate::uss_frame::ak_message(pke[0] >> 4),
        }),
        false,
    )
}

fn handle_uss_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::uss_frame::parse_uss_response(&p.frame) {
        Ok((station, pke, ind, pzd)) => success(
            request_id.to_string(),
            json!({
                "station": station,
                "pkeAkCode": pke[0] >> 4,
                "pkeAk": format!("0x{:X}", pke[0] >> 4),
                "pkeAkMessage": crate::uss_frame::ak_message(pke[0] >> 4),
                "pkePnu": ((pke[0] as u16 & 0x0F) << 8) | pke[1] as u16,
                "ind": ind,
                "pzd": pzd,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        area: String,
        db: u16,
        offset: u16,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::build_rk512_read(&p.area, p.db, p.offset, p.count) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        area: String,
        db: u16,
        offset: u16,
        values: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::build_rk512_write(&p.area, p.db, p.offset, &p.values) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": frame.iter().map(|b| format!("{b:02X}")).collect::<String>(),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_rk512_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::rk512::parse_rk512_response(&p.frame) {
        Ok((resp, data)) => {
            if resp.error == 0 && data.len() != usize::from(resp.count) * 2 {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::Modbus {
                        code: "RK512_LENGTH_MISMATCH",
                        message: format!(
                            "RK512 成功响应数据长度不匹配:期望 {}B,收到 {}B",
                            usize::from(resp.count) * 2,
                            data.len()
                        ),
                        details: Some(
                            json!({ "expectedBytes": usize::from(resp.count) * 2, "actualBytes": data.len(), "count": resp.count }),
                        ),
                    },
                );
            }
            success(
                request_id.to_string(),
                json!({
                    "error": resp.error,
                    "errorMessage": crate::rk512::rk512_error_message(resp.error),
                    "func": resp.func,
                    "count": resp.count,
                    "db": resp.db,
                    "offset": resp.offset,
                    "data": data,
                }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_enip_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenEnipConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_enip(&p.connection_id, &p.host, p.port) {
        Ok(registered) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "transport": "tcp",
                "encapsulation": "RegisterSession",
                "sessionInitialized": true,
                "sessionHandle": registered.session_handle,
                "senderContext": registered.sender_context,
                "protocolVersion": registered.protocol_version,
                "options": registered.options,
                "readOnly": true,
                "underlyingProtocol": "Allen-Bradley EtherNet/IP/CIP explicit",
                "l2Evidence": "independent TCP endpoint only; CompactLogix/ControlLogix model and L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_enip_read_tag(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: EnipReadTagPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.enip_read_tag(&p.connection_id, &p.tag, p.elements) {
        Ok((request, response, sender_context)) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "tag": p.tag,
                "elements": p.elements,
                "request": request,
                "requestHex": crate::enip::frame_hex(&request),
                "senderContext": sender_context,
                "service": response.service,
                "serviceHex": format!("0x{:02X}", response.service),
                "generalStatus": response.general_status,
                "generalStatusMessage": crate::enip::cip_status_message(response.general_status),
                "additionalStatus": response.additional_status,
                "cpfItemType": response.cpf_item_type,
                "data": response.data,
                "dataHex": crate::enip::frame_hex(&response.data),
                "readOnly": true,
                "underlyingProtocol": "Allen-Bradley EtherNet/IP/CIP explicit",
                "l2Evidence": "independent TCP endpoint only; CompactLogix/ControlLogix model and L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_enip_build_register_session(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(default)]
        session_handle: u32,
        #[serde(default)]
        sender_context: u64,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame = crate::enip::build_register_session(p.session_handle, p.sender_context);
    success(
        request_id.to_string(),
        json!({
            "command": crate::enip::ENIP_REGISTER_SESSION,
            "sessionHandle": p.session_handle,
            "senderContext": p.sender_context,
            "frame": frame,
            "frameHex": crate::enip::frame_hex(&frame),
        }),
        false,
    )
}

fn handle_enip_build_unregister_session(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        session_handle: u32,
        #[serde(default)]
        sender_context: u64,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame = crate::enip::build_unregister_session(p.session_handle, p.sender_context);
    success(
        request_id.to_string(),
        json!({
            "command": crate::enip::ENIP_UNREGISTER_SESSION,
            "sessionHandle": p.session_handle,
            "senderContext": p.sender_context,
            "frame": frame,
            "frameHex": crate::enip::frame_hex(&frame),
        }),
        false,
    )
}

fn handle_enip_build_read_tag(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(default)]
        session_handle: u32,
        #[serde(default)]
        sender_context: u64,
        tag: String,
        #[serde(default = "default_enip_elements")]
        elements: u16,
    }
    fn default_enip_elements() -> u16 {
        1
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let path = match crate::enip::encode_tag_path(&p.tag) {
        Ok(path) => path,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let frame =
        match crate::enip::build_read_tag(p.session_handle, p.sender_context, &p.tag, p.elements) {
            Ok(frame) => frame,
            Err(error) => return failure(Some(request_id.to_string()), error),
        };
    success(
        request_id.to_string(),
        json!({
            "command": crate::enip::ENIP_SEND_RR_DATA,
            "cipService": crate::enip::CIP_READ_TAG,
            "sessionHandle": p.session_handle,
            "senderContext": p.sender_context,
            "tag": p.tag,
            "elements": p.elements,
            "tagPath": path,
            "tagPathHex": crate::enip::frame_hex(&path),
            "frame": frame,
            "frameHex": crate::enip::frame_hex(&frame),
        }),
        false,
    )
}

fn handle_enip_parse_frame(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::enip::parse_encapsulation(&p.frame) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "command": frame.command,
                "length": frame.length,
                "sessionHandle": frame.session_handle,
                "status": frame.status,
                "senderContext": frame.sender_context,
                "senderContextHex": crate::enip::frame_hex(&frame.sender_context),
                "options": frame.options,
                "payload": frame.payload,
                "payloadHex": crate::enip::frame_hex(&frame.payload),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_enip_parse_cip_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::enip::parse_cip_response(&p.frame) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "service": response.service,
                "serviceHex": format!("0x{:02X}", response.service),
                "expectedReadReply": response.service == crate::enip::CIP_READ_TAG_REPLY,
                "replyPathSize": response.reply_path_size,
                "generalStatus": response.general_status,
                "generalStatusHex": format!("0x{:02X}", response.general_status),
                "generalStatusMessage": crate::enip::cip_status_message(response.general_status),
                "cipOk": response.general_status == 0,
                "additionalStatus": response.additional_status,
                "cpfItemType": response.cpf_item_type,
                "data": response.data,
                "dataHex": crate::enip::frame_hex(&response.data),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// === Beckhoff ADS/AMS（TCP 只读会话 + 编解码）===

fn ads_live_result(
    request_id: &str,
    connection_id: &str,
    mode: &str,
    request: Vec<u8>,
    response_frame: Vec<u8>,
    response: crate::ads::AdsResponse,
) -> CommandOutcome {
    let mut result = json!({
        "connectionId": connection_id,
        "mode": mode,
        "request": request,
        "requestHex": crate::ads::frame_hex(&request),
        "response": response_frame,
        "responseHex": crate::ads::frame_hex(&response_frame),
        "command": response.frame.command,
        "commandHex": format!("0x{:04X}", response.frame.command),
        "targetNetId": crate::ads::net_id_string(&response.frame.target_net_id),
        "targetPort": response.frame.target_port,
        "sourceNetId": crate::ads::net_id_string(&response.frame.source_net_id),
        "sourcePort": response.frame.source_port,
        "stateFlags": response.frame.state_flags,
        "invokeId": response.frame.invoke_id,
        "amsError": response.frame.ams_error,
        "adsResult": response.ads_result,
        "adsResultHex": format!("0x{:08X}", response.ads_result),
        "adsResultMessage": crate::ads::ads_error_message(response.ads_result),
        "adsOk": response.ads_result == 0,
        "data": response.data,
        "dataHex": crate::ads::frame_hex(&response.data),
        "declaredDataLength": response.declared_data_length,
        "readOnly": true,
        "underlyingProtocol": "Beckhoff ADS/AMS over TCP",
        "l2Evidence": "independent TCP endpoint only; AMS Route/TwinCAT model and L2 pending",
    });
    if response.ads_result == 0 && response.frame.command == crate::ads::ADS_READ_DEVICE_INFO {
        if let Ok((major, minor, build, device_name)) = crate::ads::parse_device_info(&response) {
            result["deviceInfo"] = json!({ "majorVersion": major, "minorVersion": minor, "versionBuild": build, "deviceName": device_name });
        }
    }
    if response.ads_result == 0 && response.frame.command == crate::ads::ADS_READ_STATE {
        if let Ok((ads_state, device_state)) = crate::ads::parse_state(&response) {
            result["state"] = json!({ "adsState": ads_state, "deviceState": device_state, "isRunning": ads_state == 5 });
        }
    }
    success(request_id.to_string(), result, false)
}

fn handle_open_ads_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenAdsConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let target_net_id = match crate::ads::parse_net_id(&p.target_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let source_net_id = match crate::ads::parse_net_id(&p.source_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match session.open_ads(
        &p.connection_id,
        &p.host,
        p.port,
        target_net_id,
        p.target_port,
        source_net_id,
        p.source_port,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "targetNetId": p.target_net_id,
                "targetPort": p.target_port,
                "sourceNetId": p.source_net_id,
                "sourcePort": p.source_port,
                "transport": "tcp",
                "handshake": false,
                "sessionInitialized": true,
                "amsRoute": "caller-supplied endpoint; no automatic route creation",
                "readOnly": true,
                "underlyingProtocol": "Beckhoff ADS/AMS over TCP",
                "l2Evidence": "independent TCP endpoint only; AMS Route/TwinCAT model and L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: AdsReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ads_read(
        &p.connection_id,
        p.index_group,
        p.index_offset,
        p.read_length,
    ) {
        Ok((request, response_frame, response)) => ads_live_result(
            request_id,
            &p.connection_id,
            "read",
            request,
            response_frame,
            response,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_read_device_info(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ads_read_device_info(&p.connection_id) {
        Ok((request, response_frame, response)) => ads_live_result(
            request_id,
            &p.connection_id,
            "readDeviceInfo",
            request,
            response_frame,
            response,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_read_state(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ads_read_state(&p.connection_id) {
        Ok((request, response_frame, response)) => ads_live_result(
            request_id,
            &p.connection_id,
            "readState",
            request,
            response_frame,
            response,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn mqtt_live_boundary(connection_id: &str) -> serde_json::Value {
    json!({
        "connectionId": connection_id,
        "readOnly": true,
        "underlyingProtocol": "MQTT 3.1.1 over TCP",
        "l2Evidence": "independent TCP peer plus Aedes MQTT 3.1.1 broker interoperability; production broker/auth/TLS/Sparkplug L2 pending"
    })
}

fn handle_open_mqtt_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenMqttConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_mqtt(
        &p.connection_id,
        &p.host,
        p.port,
        &p.client_id,
        p.keep_alive,
        p.clean_session,
    ) {
        Ok(connack) => {
            let mut result = mqtt_live_boundary(&p.connection_id);
            result["host"] = json!(p.host);
            result["port"] = json!(p.port);
            result["transport"] = json!("tcp");
            result["clientId"] = json!(p.client_id);
            result["keepAlive"] = json!(p.keep_alive);
            result["cleanSession"] = json!(p.clean_session);
            result["sessionPresent"] = json!(connack.session_present);
            result["returnCode"] = json!(connack.return_code);
            result["returnCodeMessage"] =
                json!(crate::mqtt::return_code_message(connack.return_code));
            result["handshake"] = json!("CONNECT/CONNACK");
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_subscribe(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: MqttSubscribePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mqtt_subscribe(&p.connection_id, &p.topic_filter, p.qos) {
        Ok((request, response, suback)) => {
            let mut result = mqtt_live_boundary(&p.connection_id);
            result["topicFilter"] = json!(p.topic_filter);
            result["qos"] = json!(p.qos);
            result["packetId"] = json!(suback.packet_id);
            result["returnCode"] = json!(suback.return_code);
            result["returnCodeMessage"] =
                json!(crate::mqtt::return_code_message(suback.return_code));
            result["request"] = json!(request);
            result["requestHex"] = json!(crate::mqtt::frame_hex(&request));
            result["response"] = json!(response);
            result["responseHex"] = json!(crate::mqtt::frame_hex(&response));
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_read_publish(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: MqttConnectionIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mqtt_read_publish(&p.connection_id) {
        Ok((frame, publish)) => {
            let mut result = mqtt_live_boundary(&p.connection_id);
            result["packetType"] = json!(crate::mqtt::PACKET_PUBLISH);
            result["topic"] = json!(publish.topic);
            result["qos"] = json!(publish.qos);
            result["dup"] = json!(publish.dup);
            result["retain"] = json!(publish.retain);
            result["packetId"] = json!(publish.packet_id);
            result["payload"] = json!(publish.payload);
            result["payloadHex"] = json!(crate::mqtt::frame_hex(&publish.payload));
            result["payloadUtf8"] = std::str::from_utf8(&publish.payload)
                .ok()
                .map(str::to_owned)
                .into();
            result["frame"] = json!(frame);
            result["frameHex"] = json!(crate::mqtt::frame_hex(&frame));
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_ping(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttConnectionIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mqtt_ping(&p.connection_id) {
        Ok((request, response)) => {
            let mut result = mqtt_live_boundary(&p.connection_id);
            result["request"] = json!(request);
            result["requestHex"] = json!(crate::mqtt::frame_hex(&request));
            result["response"] = json!(response);
            result["responseHex"] = json!(crate::mqtt::frame_hex(&response));
            result["ping"] = json!(true);
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_build_connect(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttBuildConnectPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::build_connect(&p.client_id, p.keep_alive, p.clean_session) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "packetType": crate::mqtt::PACKET_CONNECT,
                "protocolName": "MQTT",
                "protocolLevel": crate::mqtt::MQTT_PROTOCOL_LEVEL_311,
                "clientId": p.client_id,
                "keepAlive": p.keep_alive,
                "cleanSession": p.clean_session,
                "frame": frame,
                "frameHex": crate::mqtt::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_parse_connack(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::parse_connack(&p.frame) {
        Ok(connack) => success(
            request_id.to_string(),
            json!({
                "packetType": crate::mqtt::PACKET_CONNACK,
                "sessionPresent": connack.session_present,
                "returnCode": connack.return_code,
                "returnCodeMessage": crate::mqtt::return_code_message(connack.return_code),
                "frameHex": crate::mqtt::frame_hex(&p.frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_build_subscribe(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttBuildSubscribePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::build_subscribe(p.packet_id, &p.topic_filter, p.qos) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "packetType": crate::mqtt::PACKET_SUBSCRIBE,
                "packetId": p.packet_id,
                "topicFilter": p.topic_filter,
                "qos": p.qos,
                "frame": frame,
                "frameHex": crate::mqtt::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_parse_suback(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::parse_suback(&p.frame) {
        Ok(suback) => success(
            request_id.to_string(),
            json!({
                "packetType": crate::mqtt::PACKET_SUBACK,
                "packetId": suback.packet_id,
                "returnCode": suback.return_code,
                "returnCodeMessage": crate::mqtt::return_code_message(suback.return_code),
                "frameHex": crate::mqtt::frame_hex(&p.frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_parse_publish(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::parse_publish(&p.frame) {
        Ok(publish) => success(
            request_id.to_string(),
            json!({
                "packetType": crate::mqtt::PACKET_PUBLISH,
                "topic": publish.topic,
                "qos": publish.qos,
                "dup": publish.dup,
                "retain": publish.retain,
                "packetId": publish.packet_id,
                "payload": publish.payload,
                "payloadHex": crate::mqtt::frame_hex(&publish.payload),
                "payloadUtf8": std::str::from_utf8(&publish.payload).ok(),
                "frameHex": crate::mqtt::frame_hex(&p.frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_build_pingreq(request_id: &str) -> CommandOutcome {
    let frame = crate::mqtt::build_pingreq();
    success(
        request_id.to_string(),
        json!({ "packetType": crate::mqtt::PACKET_PINGREQ, "frame": frame, "frameHex": crate::mqtt::frame_hex(&frame), "readOnly": true }),
        false,
    )
}

fn handle_mqtt_parse_pingresp(request_id: &str, payload: Value) -> CommandOutcome {
    let p: MqttFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mqtt::parse_pingresp(&p.frame) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "packetType": crate::mqtt::PACKET_PINGRESP, "ping": true, "frameHex": crate::mqtt::frame_hex(&p.frame), "readOnly": true }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_mqtt_build_disconnect(request_id: &str) -> CommandOutcome {
    let frame = crate::mqtt::build_disconnect();
    success(
        request_id.to_string(),
        json!({ "packetType": crate::mqtt::PACKET_DISCONNECT, "frame": frame, "frameHex": crate::mqtt::frame_hex(&frame), "readOnly": true }),
        false,
    )
}

fn iec104_live_boundary(connection_id: &str) -> serde_json::Value {
    json!({
        "connectionId": connection_id,
        "role": "clientMaster",
        "readOnly": true,
        "underlyingProtocol": "IEC 60870-5-104 over TCP",
        "controlsEnabled": false,
        "clockSyncEnabled": false,
        "l2Evidence": "independent scripted TCP outstation peer; production RTU/IED L2 pending"
    })
}

fn handle_open_iec104_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenIec104ConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_iec104(
        &p.connection_id,
        &p.host,
        p.port,
        p.common_address,
        p.originator_address,
    ) {
        Ok((request, response)) => {
            let mut result = iec104_live_boundary(&p.connection_id);
            result["host"] = json!(p.host);
            result["port"] = json!(p.port);
            result["transport"] = json!("tcp");
            result["commonAddress"] = json!(p.common_address);
            result["originatorAddress"] = json!(p.originator_address);
            result["handshake"] = json!("STARTDT_ACT/STARTDT_CON");
            result["request"] = json!(request);
            result["requestHex"] = json!(crate::iec104::frame_hex(&request));
            result["response"] = json!(response);
            result["responseHex"] = json!(crate::iec104::frame_hex(&response));
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_general_interrogation(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: Iec104InterrogationPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.iec104_general_interrogation(&p.connection_id, p.group) {
        Ok(interrogation) => {
            let request_hex = crate::iec104::frame_hex(&interrogation.request_frame);
            let received_hex = interrogation
                .received_frames
                .iter()
                .map(|frame| crate::iec104::frame_hex(frame))
                .collect::<Vec<_>>();
            let acknowledgement_hex = interrogation
                .acknowledgement_frames
                .iter()
                .map(|frame| crate::iec104::frame_hex(frame))
                .collect::<Vec<_>>();
            let point_count = interrogation.points.len();
            let mut result = iec104_live_boundary(&p.connection_id);
            result["operation"] = json!("generalInterrogation");
            result["group"] = json!(interrogation.group);
            result["qualifier"] = json!(interrogation.qualifier);
            result["request"] = json!(interrogation.request_frame);
            result["requestHex"] = json!(request_hex);
            result["receivedFrames"] = json!(interrogation.received_frames);
            result["receivedFrameHex"] = json!(received_hex);
            result["acknowledgementFrames"] = json!(interrogation.acknowledgement_frames);
            result["acknowledgementFrameHex"] = json!(acknowledgement_hex);
            result["points"] = json!(interrogation.points);
            result["pointCount"] = json!(point_count);
            result["activationConfirmed"] = json!(interrogation.activation_confirmed);
            result["activationTerminated"] = json!(interrogation.activation_terminated);
            result["finalSendSequence"] = json!(interrogation.final_send_sequence);
            result["finalReceiveSequence"] = json!(interrogation.final_receive_sequence);
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_test_frame(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: Iec104ConnectionIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.iec104_test_frame(&p.connection_id) {
        Ok((request, response)) => {
            let mut result = iec104_live_boundary(&p.connection_id);
            result["operation"] = json!("testFrame");
            result["confirmed"] = json!(true);
            result["request"] = json!(request);
            result["requestHex"] = json!(crate::iec104::frame_hex(&request));
            result["response"] = json!(response);
            result["responseHex"] = json!(crate::iec104::frame_hex(&response));
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_build_i_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104BuildIFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::iec104::build_i_frame(p.send_sequence, p.receive_sequence, &p.asdu) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "format": "I",
                "sendSequence": p.send_sequence,
                "receiveSequence": p.receive_sequence,
                "asdu": p.asdu,
                "frame": frame,
                "frameHex": crate::iec104::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_build_s_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104BuildSFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::iec104::build_s_frame(p.receive_sequence) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "format": "S",
                "receiveSequence": p.receive_sequence,
                "frame": frame,
                "frameHex": crate::iec104::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn parse_iec104_u_function(value: &str) -> Result<crate::iec104::UFunction, CoreError> {
    let normalized = value.trim().to_ascii_lowercase().replace(['_', '-'], "");
    match normalized.as_str() {
        "startdtact" | "startdatatransferactivation" => {
            Ok(crate::iec104::UFunction::StartDataTransferActivation)
        }
        "startdtcon" | "startdatatransferconfirmation" => {
            Ok(crate::iec104::UFunction::StartDataTransferConfirmation)
        }
        "stopdtact" | "stopdatatransferactivation" => {
            Ok(crate::iec104::UFunction::StopDataTransferActivation)
        }
        "stopdtcon" | "stopdatatransferconfirmation" => {
            Ok(crate::iec104::UFunction::StopDataTransferConfirmation)
        }
        "testfract" | "testframeactivation" => Ok(crate::iec104::UFunction::TestFrameActivation),
        "testfrcon" | "testframeconfirmation" => {
            Ok(crate::iec104::UFunction::TestFrameConfirmation)
        }
        _ => Err(CoreError::Modbus {
            code: "IEC104_PARAM_INVALID",
            message: format!("未知 IEC104 U 帧功能: {value}"),
            details: Some(json!({ "function": value })),
        }),
    }
}

fn handle_iec104_build_u_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104BuildUFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match parse_iec104_u_function(&p.function) {
        Ok(function) => {
            let frame = crate::iec104::build_u_frame(function);
            success(
                request_id.to_string(),
                json!({
                    "format": "U",
                    "function": function,
                    "functionName": function.name(),
                    "frame": frame,
                    "frameHex": crate::iec104::frame_hex(&frame),
                    "readOnly": true
                }),
                false,
            )
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_parse_apdu(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104FramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::iec104::parse_apdu(&p.frame) {
        Ok(apdu) => success(
            request_id.to_string(),
            json!({
                "format": apdu.format(),
                "apdu": apdu,
                "frameHex": crate::iec104::frame_hex(&p.frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_build_general_interrogation(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104BuildInterrogationPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let asdu = match crate::iec104::build_general_interrogation_asdu(
        p.common_address,
        p.group,
        p.originator_address,
    ) {
        Ok(asdu) => asdu,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::iec104::build_i_frame(p.send_sequence, p.receive_sequence, &asdu) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "operation": "generalInterrogation",
                "commonAddress": p.common_address,
                "originatorAddress": p.originator_address,
                "group": p.group,
                "qualifier": 20 + p.group,
                "sendSequence": p.send_sequence,
                "receiveSequence": p.receive_sequence,
                "asdu": asdu,
                "asduHex": crate::iec104::frame_hex(&asdu),
                "frame": frame,
                "frameHex": crate::iec104::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_iec104_parse_asdu(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Iec104AsduPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::iec104::parse_asdu(&p.asdu) {
        Ok(asdu) => {
            let points = if crate::iec104::is_monitoring_type(asdu.type_id) {
                match crate::iec104::decode_telemetry(&asdu) {
                    Ok(points) => points,
                    Err(error) => return failure(Some(request_id.to_string()), error),
                }
            } else {
                Vec::new()
            };
            success(
                request_id.to_string(),
                json!({
                    "asdu": asdu,
                    "asduHex": crate::iec104::frame_hex(&p.asdu),
                    "points": points,
                    "pointCount": points.len(),
                    "readOnly": true
                }),
                false,
            )
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn dnp3_live_boundary(connection_id: &str) -> serde_json::Value {
    json!({
        "connectionId": connection_id,
        "role": "master",
        "readOnly": true,
        "underlyingProtocol": "DNP3 over TCP",
        "controlsEnabled": false,
        "timeSyncEnabled": false,
        "secureAuthenticationEnabled": false,
        "serialEnabled": false,
        "stackSelection": "Nexus MIT bounded implementation; maintained Step Function stack evaluated but non-production license not accepted",
        "l2Evidence": "independent scripted TCP outstation peer only; third-party production stack and real RTU/IED L2 pending"
    })
}

fn dnp3_scan_outcome(
    request_id: &str,
    connection_id: &str,
    result: crate::dnp3::ScanResult,
) -> CommandOutcome {
    let request_hex = result
        .request_frames
        .iter()
        .map(|frame| crate::dnp3::frame_hex(frame))
        .collect::<Vec<_>>();
    let response_hex = result
        .response_frames
        .iter()
        .map(|frame| crate::dnp3::frame_hex(frame))
        .collect::<Vec<_>>();
    let confirmation_hex = result
        .confirmation_frames
        .iter()
        .map(|frame| crate::dnp3::frame_hex(frame))
        .collect::<Vec<_>>();
    let point_count = result.points.len();
    let unsolicited_count = result.unsolicited_responses.len();
    let mut value = dnp3_live_boundary(connection_id);
    value["operation"] = json!(result.operation);
    value["classes"] = json!(result.classes);
    value["requestFrames"] = json!(result.request_frames);
    value["requestFrameHex"] = json!(request_hex);
    value["responseFrames"] = json!(result.response_frames);
    value["responseFrameHex"] = json!(response_hex);
    value["confirmationFrames"] = json!(result.confirmation_frames);
    value["confirmationFrameHex"] = json!(confirmation_hex);
    value["initialApplicationSequence"] = json!(result.initial_application_sequence);
    value["finalApplicationSequence"] = json!(result.final_application_sequence);
    value["finalTransportSequence"] = json!(result.final_transport_sequence);
    value["iin"] = json!(result.iin);
    value["points"] = json!(result.points);
    value["pointCount"] = json!(point_count);
    value["unsolicitedResponses"] = json!(result.unsolicited_responses);
    value["unsolicitedResponseCount"] = json!(unsolicited_count);
    success(request_id.to_string(), value, false)
}

fn handle_open_dnp3_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenDnp3ConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_dnp3(
        &p.connection_id,
        &p.host,
        p.port,
        p.master_address,
        p.outstation_address,
    ) {
        Ok(()) => {
            let mut result = dnp3_live_boundary(&p.connection_id);
            result["host"] = json!(p.host);
            result["port"] = json!(p.port);
            result["transport"] = json!("tcp");
            result["masterAddress"] = json!(p.master_address);
            result["outstationAddress"] = json!(p.outstation_address);
            result["tcpConnected"] = json!(true);
            result["applicationHandshake"] = json!(false);
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_integrity_poll(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: Dnp3ConnectionIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.dnp3_integrity_poll(&p.connection_id) {
        Ok(result) => dnp3_scan_outcome(request_id, &p.connection_id, result),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_class_scan(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: Dnp3ClassScanPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.dnp3_class_scan(
        &p.connection_id,
        p.class0,
        p.class1,
        p.class2,
        p.class3,
        "classScan",
    ) {
        Ok(result) => dnp3_scan_outcome(request_id, &p.connection_id, result),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3ReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.dnp3_read(&p.connection_id, p.group, p.variation, p.start, p.stop) {
        Ok(result) => dnp3_scan_outcome(request_id, &p.connection_id, result),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_build_link_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3BuildLinkPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dnp3::build_link_frame(p.control, p.destination, p.source, &p.user_data) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "control": p.control,
                "destination": p.destination,
                "source": p.source,
                "userData": p.user_data,
                "frame": frame,
                "frameHex": crate::dnp3::frame_hex(&frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_parse_link_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3FramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dnp3::parse_link_frame(&p.frame) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "link": frame,
                "frameHex": crate::dnp3::frame_hex(&p.frame),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_build_class_scan(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3BuildClassScanPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dnp3::build_class_scan(p.sequence, p.class0, p.class1, p.class2, p.class3) {
        Ok(pdu) => success(
            request_id.to_string(),
            json!({
                "sequence": p.sequence,
                "classes": [p.class0, p.class1, p.class2, p.class3],
                "function": crate::dnp3::FUNCTION_READ,
                "pdu": pdu,
                "pduHex": crate::dnp3::frame_hex(&pdu),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_build_read_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3BuildReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let request = match (p.start, p.stop) {
        (Some(start), Some(stop)) => {
            crate::dnp3::build_read_request(p.sequence, p.group, p.variation, start, stop)
        }
        (None, None) => crate::dnp3::build_read_all_request(p.sequence, p.group, p.variation),
        _ => Err(CoreError::Modbus {
            code: "DNP3_RANGE_INCOMPLETE",
            message: "DNP3 start/stop 必须同时提供或同时省略".into(),
            details: Some(json!({ "start": p.start, "stop": p.stop })),
        }),
    };
    match request {
        Ok(pdu) => success(
            request_id.to_string(),
            json!({
                "sequence": p.sequence,
                "group": p.group,
                "variation": p.variation,
                "start": p.start,
                "stop": p.stop,
                "function": crate::dnp3::FUNCTION_READ,
                "pdu": pdu,
                "pduHex": crate::dnp3::frame_hex(&pdu),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_parse_application_response(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3ApplicationPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dnp3::parse_application_response(&p.pdu) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "response": response,
                "pduHex": crate::dnp3::frame_hex(&p.pdu),
                "pointCount": response.points.len(),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dnp3_build_confirm(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dnp3ConfirmPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dnp3::build_confirm(p.sequence, p.unsolicited) {
        Ok(pdu) => success(
            request_id.to_string(),
            json!({
                "sequence": p.sequence,
                "unsolicited": p.unsolicited,
                "function": crate::dnp3::FUNCTION_CONFIRM,
                "pdu": pdu,
                "pduHex": crate::dnp3::frame_hex(&pdu),
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dlt645_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dlt645AddressPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dlt645::parse_address(&p.address) {
        Ok(address_bytes) => success(
            request_id.to_string(),
            json!({
                "address": crate::dlt645::format_address(&address_bytes)
                    .expect("validated address must format"),
                "addressBytes": address_bytes,
                "addressHex": crate::dlt645::frame_hex(&address_bytes),
                "byteOrder": "leastSignificantPairFirst",
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dlt645_parse_data_id(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dlt645DataIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let version = match p.version.parse::<crate::dlt645::Version>() {
        Ok(version) => version,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::dlt645::parse_data_identifier(version, &p.data_id) {
        Ok(identifier) => success(
            request_id.to_string(),
            json!({
                "identifier": identifier,
                "wireHexBeforeOffset": crate::dlt645::frame_hex(&identifier.wire_bytes),
                "offset": crate::dlt645::DATA_OFFSET,
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dlt645_build_read_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dlt645BuildReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let version = match p.version.parse::<crate::dlt645::Version>() {
        Ok(version) => version,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::dlt645::build_read_request(version, &p.address, &p.data_id, p.preamble_count) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "version": version,
                "address": p.address.trim(),
                "dataId": p.data_id.to_ascii_uppercase(),
                "preambleCount": p.preamble_count,
                "control": version.read_request_control(),
                "frame": frame,
                "frameHex": crate::dlt645::frame_hex(&frame),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dlt645_parse_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dlt645FramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::dlt645::parse_frame(&p.frame) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "parsed": frame,
                "frameHex": crate::dlt645::frame_hex(&p.frame),
                "offsetDecoded": true,
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_dlt645_parse_read_response(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Dlt645ParseReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let version = match p.version.parse::<crate::dlt645::Version>() {
        Ok(version) => version,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::dlt645::parse_read_response(&p.frame, version, &p.address, &p.data_id) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "response": response,
                "frameHex": crate::dlt645::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_parse_meter_type(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188MeterTypePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match p.meter_type.parse::<crate::cjt188::MeterType>() {
        Ok(meter_type) => success(
            request_id.to_string(),
            json!({
                "meterType": meter_type,
                "meterTypeCode": meter_type.code(),
                "label": meter_type.label(),
                "cumulativeFlowUnit": meter_type.cumulative_flow_unit(),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188AddressPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::cjt188::parse_address(&p.address) {
        Ok(address_bytes) => success(
            request_id.to_string(),
            json!({
                "address": crate::cjt188::format_address(&address_bytes)
                    .expect("validated address must format"),
                "addressBytes": address_bytes,
                "addressHex": crate::cjt188::frame_hex(&address_bytes),
                "byteOrder": "leastSignificantPairFirst",
                "broadcast": address_bytes == [0xAA; 7],
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_parse_data_id(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188DataIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::cjt188::parse_data_identifier(&p.data_id) {
        Ok(identifier) => success(
            request_id.to_string(),
            json!({
                "identifier": identifier,
                "wireHex": crate::cjt188::frame_hex(&identifier.wire_bytes),
                "byteOrder": "canonicalHighByteFirst",
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_build_read_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188BuildReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let meter_type = match p.meter_type.parse::<crate::cjt188::MeterType>() {
        Ok(meter_type) => meter_type,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::cjt188::build_read_request(
        meter_type,
        &p.address,
        &p.data_id,
        p.sequence,
        p.preamble_count,
    ) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "meterType": meter_type,
                "address": p.address.trim().to_ascii_uppercase(),
                "dataId": p.data_id.to_ascii_uppercase(),
                "sequence": p.sequence,
                "preambleCount": p.preamble_count,
                "control": crate::cjt188::READ_DATA_CONTROL,
                "frame": frame,
                "frameHex": crate::cjt188::frame_hex(&frame),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_parse_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188FramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::cjt188::parse_frame(&p.frame) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "parsed": frame,
                "frameHex": crate::cjt188::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_cjt188_parse_read_response(request_id: &str, payload: Value) -> CommandOutcome {
    let p: Cjt188ParseReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let meter_type = match p.meter_type.parse::<crate::cjt188::MeterType>() {
        Ok(meter_type) => meter_type,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::cjt188::parse_read_response(
        &p.frame, meter_type, &p.address, &p.data_id, p.sequence,
    ) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "response": response,
                "frameHex": crate::cjt188::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "serial"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn parse_bacnet_segmentation(value: &str) -> Result<crate::bacnet::Segmentation, CoreError> {
    match value.trim().to_ascii_lowercase().as_str() {
        "both" | "segmented-both" | "0" => Ok(crate::bacnet::Segmentation::Both),
        "transmit" | "segmented-transmit" | "1" => Ok(crate::bacnet::Segmentation::Transmit),
        "receive" | "segmented-receive" | "2" => Ok(crate::bacnet::Segmentation::Receive),
        "none" | "no-segmentation" | "3" => Ok(crate::bacnet::Segmentation::None),
        other => Err(CoreError::Modbus {
            code: "BACNET_SEGMENTATION_INVALID",
            message: "BACnet Segmentation 只能是 both、transmit、receive 或 none".into(),
            details: Some(serde_json::json!({ "segmentation": other })),
        }),
    }
}

fn handle_bacnet_ip_build_whois(request_id: &str, payload: Value) -> CommandOutcome {
    let p: BacnetIpWhoIsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::bacnet::build_whois_request(p.low_limit, p.high_limit, p.broadcast) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "service": "who-is",
                "lowLimit": p.low_limit,
                "highLimit": p.high_limit,
                "global": p.low_limit.is_none() && p.high_limit.is_none(),
                "broadcast": p.broadcast,
                "bvlcFunction": if p.broadcast {
                    crate::bacnet::BVLC_ORIGINAL_BROADCAST_NPDU
                } else {
                    crate::bacnet::BVLC_ORIGINAL_UNICAST_NPDU
                },
                "frame": frame,
                "frameHex": crate::bacnet::frame_hex(&frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_build_iam(request_id: &str, payload: Value) -> CommandOutcome {
    let p: BacnetIpIamPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let segmentation = match parse_bacnet_segmentation(&p.segmentation) {
        Ok(segmentation) => segmentation,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::bacnet::build_iam_request(
        p.device_instance,
        p.max_apdu,
        segmentation,
        p.vendor_id,
        p.broadcast,
    ) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "service": "i-am",
                "deviceInstance": p.device_instance,
                "maxApdu": p.max_apdu,
                "segmentation": segmentation,
                "vendorId": p.vendor_id,
                "broadcast": p.broadcast,
                "bvlcFunction": if p.broadcast {
                    crate::bacnet::BVLC_ORIGINAL_BROADCAST_NPDU
                } else {
                    crate::bacnet::BVLC_ORIGINAL_UNICAST_NPDU
                },
                "frame": frame,
                "frameHex": crate::bacnet::frame_hex(&frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_parse_frame(request_id: &str, payload: Value) -> CommandOutcome {
    let p: BacnetIpFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::bacnet::parse_frame(&p.frame) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "parsed": parsed,
                "frameHex": crate::bacnet::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn bacnet_read_property_request(
    p: &BacnetIpReadPropertyRequestPayload,
) -> crate::bacnet::ReadPropertyRequest {
    crate::bacnet::ReadPropertyRequest {
        object_type: p.object_type,
        object_instance: p.object_instance,
        property_identifier: p.property_identifier,
        property_array_index: p.property_array_index,
        invoke_id: p.invoke_id,
    }
}

fn handle_bacnet_ip_build_read_property_request(
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: BacnetIpReadPropertyRequestPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::bacnet::build_read_property_request(
        p.object_type,
        p.object_instance,
        p.property_identifier,
        p.property_array_index,
        p.invoke_id,
    ) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "request": bacnet_read_property_request(&p),
                "frame": frame,
                "frameHex": crate::bacnet::frame_hex(&frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_parse_read_property_request(
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: BacnetIpReadPropertyFramePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::bacnet::parse_read_property_request(&p.frame) {
        Ok(request) => success(
            request_id.to_string(),
            json!({
                "request": request,
                "frameHex": crate::bacnet::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_parse_read_property_ack(request_id: &str, payload: Value) -> CommandOutcome {
    let p: BacnetIpReadPropertyAckPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let expected = p
        .expected_request
        .as_ref()
        .map(bacnet_read_property_request);
    match crate::bacnet::parse_read_property_ack(&p.frame, expected.as_ref()) {
        Ok(ack) => success(
            request_id.to_string(),
            json!({
                "ack": ack,
                "frameHex": crate::bacnet::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn bacnet_live_boundary(connection_id: &str) -> serde_json::Value {
    json!({
        "connectionId": connection_id,
        "role": "client",
        "readOnly": true,
        "underlyingProtocol": "BACnet/IPv4 over UDP",
        "controlsEnabled": false,
        "writePropertyEnabled": false,
        "readPropertyMultipleEnabled": false,
        "covEnabled": false,
        "bbmdEnabled": false,
        "foreignDeviceEnabled": false,
        "l2Evidence": "bacstack 0.0.1-beta.14 independent stack interoperability passed; real building device L2 pending"
    })
}

fn handle_open_bacnet_ip_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: BacnetIpOpenPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_bacnet_ip(&p.connection_id, &p.host, p.port) {
        Ok(()) => {
            let mut result = bacnet_live_boundary(&p.connection_id);
            result["host"] = json!(p.host);
            result["port"] = json!(p.port);
            result["transport"] = json!("udp");
            result["udpConnected"] = json!(true);
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_whois(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: BacnetIpWhoisLivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.bacnet_whois(&p.connection_id, p.low_limit, p.high_limit, p.timeout_ms) {
        Ok(result) => {
            let mut value = bacnet_live_boundary(&p.connection_id);
            value["operation"] = json!("who-is");
            value["requestFrame"] = json!(result.request_frame);
            value["requestFrameHex"] = json!(crate::bacnet::frame_hex(&result.request_frame));
            value["responseFrames"] = json!(result.response_frames);
            value["responseFrameHex"] = json!(
                result
                    .response_frames
                    .iter()
                    .map(|frame| crate::bacnet::frame_hex(frame))
                    .collect::<Vec<_>>()
            );
            value["responses"] = json!(result.responses);
            value["responseCount"] = json!(result.response_count);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_bacnet_ip_read_property_live(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: BacnetIpReadPropertyLivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.bacnet_read_property(
        &p.connection_id,
        p.object_type,
        p.object_instance,
        p.property_identifier,
        p.property_array_index,
        p.timeout_ms,
    ) {
        Ok(result) => {
            let mut value = bacnet_live_boundary(&p.connection_id);
            value["operation"] = json!("read-property");
            value["request"] = json!(result.request);
            value["requestFrame"] = json!(result.request_frame);
            value["requestFrameHex"] = json!(crate::bacnet::frame_hex(&result.request_frame));
            value["responseFrame"] = json!(result.response_frame);
            value["responseFrameHex"] = json!(crate::bacnet::frame_hex(&result.response_frame));
            value["ack"] = json!(result.ack);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_parse_group_address(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxGroupAddressPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::parse_group_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "address": address,
                "text": crate::knx::format_group_address(address),
                "wireBytes": address.value.to_be_bytes(),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_build_connect_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxConnectRequestPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::build_connect_request(&p.local_ip, p.local_port) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": crate::knx::frame_hex(&frame),
                "defaultPort": crate::knx::DEFAULT_PORT,
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_parse_connect_response(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxConnectResponsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::parse_connect_response(&p.frame) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "response": response,
                "frameHex": crate::knx::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_build_group_read_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxGroupReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let address = match crate::knx::parse_group_address(&p.address) {
        Ok(address) => address,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::knx::build_group_read_request(p.channel_id, p.sequence, address) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "address": address,
                "frame": frame,
                "frameHex": crate::knx::frame_hex(&frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_parse_tunneling_request(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxTunnelingRequestPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::parse_tunneling_request(&p.frame) {
        Ok(request) => success(
            request_id.to_string(),
            json!({
                "request": request,
                "frameHex": crate::knx::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_parse_group_value_response(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxTunnelingRequestPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::parse_group_value_response(&p.frame) {
        Ok(response) => {
            let ack = crate::knx::build_tunneling_ack(response.channel_id, response.sequence, 0);
            match ack {
                Ok(ack) => success(
                    request_id.to_string(),
                    json!({
                        "response": response,
                        "suggestedAck": ack,
                        "suggestedAckHex": crate::knx::frame_hex(&ack),
                        "frameHex": crate::knx::frame_hex(&p.frame),
                        "readOnly": true,
                        "transport": "udp-offline"
                    }),
                    false,
                ),
                Err(error) => failure(Some(request_id.to_string()), error),
            }
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_parse_tunneling_ack(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxTunnelingRequestPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::parse_tunneling_ack(&p.frame) {
        Ok(ack) => success(
            request_id.to_string(),
            json!({
                "ack": ack,
                "frameHex": crate::knx::frame_hex(&p.frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_build_tunneling_ack(request_id: &str, payload: Value) -> CommandOutcome {
    let p: KnxTunnelingAckPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::knx::build_tunneling_ack(p.channel_id, p.sequence, p.status) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "frame": frame,
                "frameHex": crate::knx::frame_hex(&frame),
                "readOnly": true,
                "transport": "udp-offline"
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn knx_live_boundary(connection_id: &str) -> serde_json::Value {
    json!({
        "connectionId": connection_id,
        "role": "client",
        "readOnly": true,
        "underlyingProtocol": "KNXnet/IP Tunneling v1 over UDP/IPv4",
        "groupWriteEnabled": false,
        "sceneControlEnabled": false,
        "routingEnabled": false,
        "deviceManagementEnabled": false,
        "secureEnabled": false,
        "l2Evidence": "knx 2.5.4 independent-stack codec interoperability passed in development test; real interface/router L2 pending"
    })
}

fn handle_open_knx_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxOpenLivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_knx_tunnel(&p.connection_id, &p.host, p.port, p.timeout_ms) {
        Ok(result) => {
            let mut value = knx_live_boundary(&p.connection_id);
            value["host"] = json!(p.host);
            value["port"] = json!(p.port);
            value["transport"] = json!("udp");
            value["udpConnected"] = json!(true);
            value["requestFrame"] = json!(result.request_frame);
            value["requestFrameHex"] = json!(crate::knx::frame_hex(&result.request_frame));
            value["response"] = json!(result.response);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_group_read(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxGroupReadLivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.knx_group_read(&p.connection_id, &p.address, p.timeout_ms) {
        Ok(result) => {
            let mut value = knx_live_boundary(&p.connection_id);
            value["operation"] = json!("group-value-read");
            value["requestFrame"] = json!(result.request_frame);
            value["requestFrameHex"] = json!(crate::knx::frame_hex(&result.request_frame));
            value["acknowledgementFrame"] = json!(result.acknowledgement_frame);
            value["acknowledgementFrameHex"] =
                json!(crate::knx::frame_hex(&result.acknowledgement_frame));
            value["responseFrame"] = json!(result.response_frame);
            value["responseFrameHex"] = json!(crate::knx::frame_hex(&result.response_frame));
            value["responseAckFrame"] = json!(result.response_ack_frame);
            value["responseAckFrameHex"] = json!(crate::knx::frame_hex(&result.response_ack_frame));
            value["address"] = json!(result.address);
            value["source"] = json!(result.source);
            value["payload"] = json!(result.payload);
            value["normalizedSmallValue"] = json!(result.normalized_small_value);
            value["sequence"] = json!(result.sequence);
            value["attempts"] = json!(result.attempts);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_disconnect(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxDisconnectLivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.knx_disconnect(&p.connection_id, p.timeout_ms) {
        Ok(response) => {
            let mut value = knx_live_boundary(&p.connection_id);
            value["operation"] = json!("disconnect");
            value["disconnected"] = json!(true);
            value["response"] = json!(response);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_connection_state(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxConnectionStatePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.knx_connection_state(&p.connection_id, p.timeout_ms) {
        Ok(result) => {
            let mut value = knx_live_boundary(&p.connection_id);
            value["operation"] = json!("connection-state");
            value["requestFrame"] = json!(result.request_frame);
            value["requestFrameHex"] = json!(crate::knx::frame_hex(&result.request_frame));
            value["response"] = json!(result.response);
            value["attempts"] = json!(result.attempts);
            success(request_id.to_string(), value, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_start_keepalive(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxKeepalivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_knx_keepalive(&p.connection_id, p.interval_ms) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "keepaliveEnabled": true,
                "intervalMs": p.interval_ms.max(100),
                "automatic": true,
                "reconnectPolicy": "explicit-connect-only",
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_stop_keepalive(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxKeepalivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_knx_keepalive(&p.connection_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "keepaliveEnabled": false,
                "automatic": false,
                "readOnly": true
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_knx_keepalive_status(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KnxKeepalivePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let enabled = session.knx_keepalive_status(&p.connection_id);
    success(
        request_id.to_string(),
        json!({
            "connectionId": p.connection_id,
            "keepaliveEnabled": enabled,
            "automatic": enabled,
            "reconnectPolicy": "explicit-connect-only",
            "readOnly": true
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct AdsContextPayload {
    target_net_id: String,
    target_port: u16,
    source_net_id: String,
    source_port: u16,
    #[serde(default)]
    invoke_id: u32,
}

fn ads_context(
    p: &AdsContextPayload,
) -> Result<
    (
        [u8; crate::ads::AMS_NET_ID_BYTES],
        [u8; crate::ads::AMS_NET_ID_BYTES],
    ),
    CoreError,
> {
    Ok((
        crate::ads::parse_net_id(&p.target_net_id)?,
        crate::ads::parse_net_id(&p.source_net_id)?,
    ))
}

fn ads_endpoint_expectation(
    net_id: Option<&str>,
    port: Option<u16>,
) -> Result<Option<crate::ads::EndpointExpectation>, CoreError> {
    if net_id.is_none() && port.is_none() {
        return Ok(None);
    }
    Ok(Some(crate::ads::EndpointExpectation {
        net_id: net_id.map(crate::ads::parse_net_id).transpose()?,
        port,
    }))
}

fn ads_frame_result(request_id: &str, frame: Vec<u8>) -> CommandOutcome {
    match crate::ads::parse_frame(&frame) {
        Ok(parsed) => success(
            request_id.to_string(),
            json!({
                "command": parsed.command,
                "commandHex": format!("0x{:04X}", parsed.command),
                "targetNetId": crate::ads::net_id_string(&parsed.target_net_id),
                "targetPort": parsed.target_port,
                "sourceNetId": crate::ads::net_id_string(&parsed.source_net_id),
                "sourcePort": parsed.source_port,
                "invokeId": parsed.invoke_id,
                "stateFlags": parsed.state_flags,
                "stateFlagsHex": format!("0x{:04X}", parsed.state_flags),
                "dataLength": parsed.data_length,
                "amsError": parsed.ams_error,
                "payload": parsed.payload,
                "payloadHex": crate::ads::frame_hex(&parsed.payload),
                "frame": frame,
                "frameHex": crate::ads::frame_hex(&frame),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        target_net_id: String,
        target_port: u16,
        source_net_id: String,
        source_port: u16,
        #[serde(default)]
        invoke_id: u32,
        index_group: u32,
        index_offset: u32,
        read_length: u32,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let target = match crate::ads::parse_net_id(&p.target_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let source = match crate::ads::parse_net_id(&p.source_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::ads::build_read(
        &target,
        p.target_port,
        &source,
        p.source_port,
        p.invoke_id,
        p.index_group,
        p.index_offset,
        p.read_length,
    ) {
        Ok(frame) => ads_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        target_net_id: String,
        target_port: u16,
        source_net_id: String,
        source_port: u16,
        #[serde(default)]
        invoke_id: u32,
        index_group: u32,
        index_offset: u32,
        #[serde(default)]
        data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let target = match crate::ads::parse_net_id(&p.target_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let source = match crate::ads::parse_net_id(&p.source_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::ads::build_write(
        &target,
        p.target_port,
        &source,
        p.source_port,
        p.invoke_id,
        p.index_group,
        p.index_offset,
        &p.data,
    ) {
        Ok(frame) => ads_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_build_readwrite(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        target_net_id: String,
        target_port: u16,
        source_net_id: String,
        source_port: u16,
        #[serde(default)]
        invoke_id: u32,
        index_group: u32,
        index_offset: u32,
        read_length: u32,
        #[serde(default)]
        write_data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let target = match crate::ads::parse_net_id(&p.target_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    let source = match crate::ads::parse_net_id(&p.source_net_id) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::ads::build_read_write(
        &target,
        p.target_port,
        &source,
        p.source_port,
        p.invoke_id,
        p.index_group,
        p.index_offset,
        p.read_length,
        &p.write_data,
    ) {
        Ok(frame) => ads_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_build_read_device_info(request_id: &str, payload: Value) -> CommandOutcome {
    let p: AdsContextPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (target, source) = match ads_context(&p) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::ads::build_read_device_info(
        &target,
        p.target_port,
        &source,
        p.source_port,
        p.invoke_id,
    ) {
        Ok(frame) => ads_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_build_read_state(request_id: &str, payload: Value) -> CommandOutcome {
    let p: AdsContextPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (target, source) = match ads_context(&p) {
        Ok(value) => value,
        Err(error) => return failure(Some(request_id.to_string()), error),
    };
    match crate::ads::build_read_state(&target, p.target_port, &source, p.source_port, p.invoke_id)
    {
        Ok(frame) => ads_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_parse_frame(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ads::parse_frame(&p.frame) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({
                "command": frame.command,
                "commandHex": format!("0x{:04X}", frame.command),
                "targetNetId": crate::ads::net_id_string(&frame.target_net_id),
                "targetPort": frame.target_port,
                "sourceNetId": crate::ads::net_id_string(&frame.source_net_id),
                "sourcePort": frame.source_port,
                "stateFlags": frame.state_flags,
                "stateFlagsHex": format!("0x{:04X}", frame.state_flags),
                "dataLength": frame.data_length,
                "amsError": frame.ams_error,
                "invokeId": frame.invoke_id,
                "payload": frame.payload,
                "payloadHex": crate::ads::frame_hex(&frame.payload),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ads_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        #[serde(default)]
        expected_invoke_id: Option<u32>,
        #[serde(default)]
        expected_command: Option<u16>,
        #[serde(default)]
        expected_target_net_id: Option<String>,
        #[serde(default)]
        expected_target_port: Option<u16>,
        #[serde(default)]
        expected_source_net_id: Option<String>,
        #[serde(default)]
        expected_source_port: Option<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let expected_target =
        match ads_endpoint_expectation(p.expected_target_net_id.as_deref(), p.expected_target_port)
        {
            Ok(value) => value,
            Err(error) => return failure(Some(request_id.to_string()), error),
        };
    let expected_source =
        match ads_endpoint_expectation(p.expected_source_net_id.as_deref(), p.expected_source_port)
        {
            Ok(value) => value,
            Err(error) => return failure(Some(request_id.to_string()), error),
        };
    match crate::ads::parse_response_with_endpoints(
        &p.frame,
        p.expected_invoke_id,
        p.expected_command,
        expected_target.as_ref(),
        expected_source.as_ref(),
    ) {
        Ok(response) => {
            let mut result = json!({
                "command": response.frame.command,
                "commandHex": format!("0x{:04X}", response.frame.command),
                "targetNetId": crate::ads::net_id_string(&response.frame.target_net_id),
                "targetPort": response.frame.target_port,
                "sourceNetId": crate::ads::net_id_string(&response.frame.source_net_id),
                "sourcePort": response.frame.source_port,
                "stateFlags": response.frame.state_flags,
                "invokeId": response.frame.invoke_id,
                "amsError": response.frame.ams_error,
                "adsResult": response.ads_result,
                "adsResultHex": format!("0x{:08X}", response.ads_result),
                "adsResultMessage": crate::ads::ads_error_message(response.ads_result),
                "adsOk": response.ads_result == 0,
                "data": response.data,
                "dataHex": crate::ads::frame_hex(&response.data),
                "declaredDataLength": response.declared_data_length,
            });
            if response.ads_result == 0
                && response.frame.command == crate::ads::ADS_READ_DEVICE_INFO
            {
                if let Ok((major, minor, build, device_name)) =
                    crate::ads::parse_device_info(&response)
                {
                    result["deviceInfo"] = json!({ "majorVersion": major, "minorVersion": minor, "versionBuild": build, "deviceName": device_name });
                }
            }
            if response.ads_result == 0 && response.frame.command == crate::ads::ADS_READ_STATE {
                if let Ok((ads_state, device_state)) = crate::ads::parse_state(&response) {
                    result["state"] = json!({ "adsState": ads_state, "deviceState": device_state, "isRunning": ads_state == 5 });
                }
            }
            success(request_id.to_string(), result, false)
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// === Keyence KV Host Link ASCII（TCP 只读会话 + 编解码）===

fn keyence_frame_result(request_id: &str, frame: Vec<u8>) -> CommandOutcome {
    let text = String::from_utf8_lossy(&frame).to_string();
    success(
        request_id.to_string(),
        json!({
            "port": crate::keyence::DEFAULT_PORT,
            "frame": frame,
            "text": text,
            "frameHex": crate::keyence::frame_hex(&frame),
        }),
        false,
    )
}

fn handle_open_keyence_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenKeyenceConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let station = if p.use_station { Some(p.station) } else { None };
    match session.open_keyence(&p.connection_id, &p.host, p.port, station) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "station": station,
                "useStation": station.is_some(),
                "transport": "tcp",
                "handshake": if station.is_some() { "CR NN -> CC" } else { "CR -> CC" },
                "handshakeValidated": true,
                "sessionInitialized": true,
                "readOnly": true,
                "underlyingProtocol": "Keyence KV Host Link ASCII over TCP",
                "l2Evidence": "independent TCP endpoint only; KV model/firmware/L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_read_words(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KeyenceReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.keyence_read_words(&p.connection_id, &p.address, p.count) {
        Ok((request, values, station)) => {
            let data_ascii = values
                .iter()
                .map(u16::to_string)
                .collect::<Vec<_>>()
                .join(" ");
            let data_hex = values
                .iter()
                .map(|value| format!("{value:04X}"))
                .collect::<Vec<_>>()
                .join(" ");
            success(
                request_id.to_string(),
                json!({
                    "connectionId": p.connection_id,
                    "request": request,
                    "requestText": String::from_utf8_lossy(&request),
                    "requestHex": crate::keyence::frame_hex(&request),
                    "station": station,
                    "address": p.address,
                    "count": p.count,
                    "data": values,
                    "dataAscii": data_ascii,
                    "dataHex": data_hex,
                    "readOnly": true,
                    "underlyingProtocol": "Keyence KV Host Link ASCII over TCP",
                    "l2Evidence": "independent TCP endpoint only; KV model/firmware/L2 pending",
                }),
                false,
            )
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_read_bits(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: KeyenceReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.keyence_read_bits(&p.connection_id, &p.address, p.count) {
        Ok((request, values, station)) => {
            let data_ascii = values
                .iter()
                .map(|value| if *value { '1' } else { '0' })
                .collect::<Vec<_>>()
                .into_iter()
                .map(|value| value.to_string())
                .collect::<Vec<_>>()
                .join(" ");
            success(
                request_id.to_string(),
                json!({
                    "connectionId": p.connection_id,
                    "request": request,
                    "requestText": String::from_utf8_lossy(&request),
                    "requestHex": crate::keyence::frame_hex(&request),
                    "station": station,
                    "address": p.address,
                    "count": p.count,
                    "data": values,
                    "dataAscii": data_ascii,
                    "readOnly": true,
                    "underlyingProtocol": "Keyence KV Host Link ASCII over TCP",
                    "l2Evidence": "independent TCP endpoint only; KV model/firmware/L2 pending",
                }),
                false,
            )
        }
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::parse_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "device": address.device,
                "offset": address.offset,
                "isBit": address.is_bit,
                "commandAddress": address.command_address,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_build_connect(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(default)]
        station: Option<u8>,
        #[serde(default)]
        use_station: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if p.station.unwrap_or(0) > 31 {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "KEYENCE_PARAM_INVALID",
                message: "Keyence Host Link 站号必须是 0..31".to_string(),
                details: Some(json!({ "station": p.station })),
            },
        );
    }
    keyence_frame_result(
        request_id,
        crate::keyence::build_connect(if p.use_station {
            Some(p.station.unwrap_or(0))
        } else {
            None
        }),
    )
}

fn handle_keyence_build_read_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::build_read_words(&p.address, p.count) {
        Ok(frame) => keyence_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_build_read_bits(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::build_read_bits(&p.address, p.count) {
        Ok(frame) => keyence_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_build_write_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::build_write_words(&p.address, &p.values) {
        Ok(frame) => keyence_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_build_write_bit(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        value: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::build_write_bit(&p.address, p.value) {
        Ok(frame) => keyence_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_parse_connect(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::parse_connect_response(&p.response) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "ok": true, "response": "CC" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_parse_words(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: String,
        expected_count: usize,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::parse_word_response(&p.response, p.expected_count) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "values": values, "count": values.len() }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_parse_bits(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: String,
        expected_count: usize,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::parse_bit_response(&p.response, p.expected_count) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "values": values, "count": values.len() }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_keyence_parse_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        response: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::keyence::parse_write_response(&p.response) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "ok": true, "response": "OK" }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// === LS Electric XGT FEnet（TCP 只读会话 + 编解码）===

fn handle_open_ls_xgt_connection(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: OpenLsXgtConnectionPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_ls_xgt(
        &p.connection_id,
        &p.host,
        p.port,
        p.cpu,
        p.base_no,
        p.slot_no,
        &p.company_id,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({
                "connectionId": p.connection_id,
                "host": p.host,
                "port": p.port,
                "companyId": p.company_id,
                "cpu": p.cpu,
                "cpuHex": format!("0x{:02X}", p.cpu),
                "baseNo": p.base_no,
                "slotNo": p.slot_no,
                "transport": "tcp",
                "handshake": false,
                "sessionInitialized": true,
                "readOnly": true,
                "underlyingProtocol": "LS Electric XGT FEnet over TCP",
                "l2Evidence": "independent TCP endpoint only; XGK/XGI/XGR model/firmware/L2 pending",
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn ls_xgt_live_result(
    request_id: &str,
    connection_id: &str,
    variable_name: &str,
    mode: &str,
    request: Vec<u8>,
    response: crate::ls_xgt::XgtResponse,
) -> CommandOutcome {
    success(
        request_id.to_string(),
        json!({
            "connectionId": connection_id,
            "variableName": variable_name,
            "mode": mode,
            "request": request,
            "requestHex": crate::ls_xgt::frame_hex(&request),
            "companyId": response.company_id,
            "cpu": response.cpu,
            "source": response.source,
            "invokeId": response.invoke_id,
            "applicationLength": response.application_length,
            "baseNo": response.base_no,
            "slotNo": response.slot_no,
            "command": response.command,
            "dataType": response.data_type,
            "blockCount": response.block_count,
            "dataLength": response.data_length,
            "data": response.data,
            "dataHex": crate::ls_xgt::frame_hex(&response.data),
            "readOnly": true,
            "underlyingProtocol": "LS Electric XGT FEnet over TCP",
            "l2Evidence": "independent TCP endpoint only; XGK/XGI/XGR model/firmware/L2 pending",
        }),
        false,
    )
}

fn handle_ls_xgt_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: LsXgtReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ls_xgt_read(&p.connection_id, &p.variable_name, p.data_type) {
        Ok((request, response)) => ls_xgt_live_result(
            request_id,
            &p.connection_id,
            &p.variable_name,
            "individual",
            request,
            response,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_read_continuous(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let p: LsXgtContinuousReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.ls_xgt_read_continuous(&p.connection_id, &p.variable_name, p.byte_count) {
        Ok((request, response)) => ls_xgt_live_result(
            request_id,
            &p.connection_id,
            &p.variable_name,
            "continuous",
            request,
            response,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn ls_xgt_frame_result(request_id: &str, frame: Vec<u8>) -> CommandOutcome {
    success(
        request_id.to_string(),
        json!({
            "port": crate::ls_xgt::DEFAULT_PORT,
            "frame": frame,
            "frameHex": crate::ls_xgt::frame_hex(&frame),
        }),
        false,
    )
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct LsXgtFrameContext {
    #[serde(default = "default_ls_xgt_invoke_id")]
    invoke_id: u16,
    #[serde(default = "default_ls_xgt_cpu")]
    cpu: u8,
    #[serde(default)]
    base_no: u8,
    #[serde(default = "default_ls_xgt_slot_no")]
    slot_no: u8,
    #[serde(default = "default_ls_xgt_company_id")]
    company_id: String,
}

fn default_ls_xgt_invoke_id() -> u16 {
    1
}

fn default_ls_xgt_cpu() -> u8 {
    crate::ls_xgt::CPU_XGK
}

fn default_ls_xgt_slot_no() -> u8 {
    3
}

fn default_ls_xgt_company_id() -> String {
    "LSIS-XGT".to_string()
}

fn handle_ls_xgt_parse_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::parse_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "area": address.area.to_string(),
                "dataType": address.data_type,
                "dataTypeHex": format!("0x{:02X}", address.data_type),
                "offset": address.offset,
                "variableName": address.variable_name,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(flatten)]
        context: LsXgtFrameContext,
        variable_name: String,
        data_type: u8,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::build_individual_read(
        &p.variable_name,
        p.data_type,
        p.context.invoke_id,
        p.context.cpu,
        p.context.base_no,
        p.context.slot_no,
        &p.context.company_id,
    ) {
        Ok(frame) => ls_xgt_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_build_continuous_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(flatten)]
        context: LsXgtFrameContext,
        variable_name: String,
        byte_count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::build_continuous_read(
        &p.variable_name,
        p.byte_count,
        p.context.invoke_id,
        p.context.cpu,
        p.context.base_no,
        p.context.slot_no,
        &p.context.company_id,
    ) {
        Ok(frame) => ls_xgt_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(flatten)]
        context: LsXgtFrameContext,
        variable_name: String,
        data_type: u8,
        #[serde(default)]
        value: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::build_individual_write(
        &p.variable_name,
        p.data_type,
        &p.value,
        p.context.invoke_id,
        p.context.cpu,
        p.context.base_no,
        p.context.slot_no,
        &p.context.company_id,
    ) {
        Ok(frame) => ls_xgt_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_build_continuous_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        #[serde(flatten)]
        context: LsXgtFrameContext,
        variable_name: String,
        #[serde(default)]
        value: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::build_continuous_write(
        &p.variable_name,
        &p.value,
        p.context.invoke_id,
        p.context.cpu,
        p.context.base_no,
        p.context.slot_no,
        &p.context.company_id,
    ) {
        Ok(frame) => ls_xgt_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_ls_xgt_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        #[serde(default)]
        expected_invoke_id: Option<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::ls_xgt::parse_response(&p.frame, p.expected_invoke_id) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "companyId": response.company_id,
                "cpu": response.cpu,
                "source": response.source,
                "invokeId": response.invoke_id,
                "applicationLength": response.application_length,
                "baseNo": response.base_no,
                "slotNo": response.slot_no,
                "command": response.command,
                "commandHex": format!("0x{:02X}", response.command),
                "dataType": response.data_type,
                "dataTypeHex": format!("0x{:02X}", response.data_type),
                "errorStatus": response.error_status,
                "errorStatusHex": format!("0x{:04X}", response.error_status),
                "blockCount": response.block_count,
                "dataLength": response.data_length,
                "data": response.data,
                "dataHex": crate::ls_xgt::frame_hex(&response.data),
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

// === Panasonic MEWTOCOL-COM（首轮只做软件编解码；不建立串口）===

fn panasonic_frame_result(request_id: &str, frame: Vec<u8>) -> CommandOutcome {
    success(
        request_id.to_string(),
        json!({
            "transport": "serial",
            "frame": frame,
            "text": crate::panasonic::frame_text(&frame),
            "frameHex": crate::panasonic::frame_hex(&frame),
        }),
        false,
    )
}

fn panasonic_station(value: u8) -> Result<u8, CoreError> {
    if !(crate::panasonic::MIN_STATION..=crate::panasonic::MAX_STATION).contains(&value) {
        return Err(CoreError::Modbus {
            code: "PANASONIC_PARAM_INVALID",
            message: "MEWTOCOL 站号必须为 1..32".to_string(),
            details: Some(json!({ "station": value })),
        });
    }
    Ok(value)
}

fn handle_panasonic_parse_data_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::panasonic::parse_data_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "areaCode": address.area_code.to_string(),
                "address": address.address,
                "canonical": address.canonical,
                "isContact": false,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_parse_contact_address(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::panasonic::parse_contact_address(&p.address) {
        Ok(address) => success(
            request_id.to_string(),
            json!({
                "areaCode": address.area_code.to_string(),
                "linearAddress": address.linear_address,
                "contactNumber": address.contact_number,
                "canonical": address.canonical,
                "isContact": true,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        word_count: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if let Err(error) = panasonic_station(p.station) {
        return failure(Some(request_id.to_string()), error);
    }
    match crate::panasonic::build_read(p.station, &p.address, p.word_count) {
        Ok(frame) => panasonic_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        #[serde(default)]
        data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if let Err(error) = panasonic_station(p.station) {
        return failure(Some(request_id.to_string()), error);
    }
    match crate::panasonic::build_write(p.station, &p.address, &p.data) {
        Ok(frame) => panasonic_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_build_read_contact(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if let Err(error) = panasonic_station(p.station) {
        return failure(Some(request_id.to_string()), error);
    }
    match crate::panasonic::build_read_contact(p.station, &p.address) {
        Ok(frame) => panasonic_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_build_write_contact(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        address: String,
        value: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    if let Err(error) = panasonic_station(p.station) {
        return failure(Some(request_id.to_string()), error);
    }
    match crate::panasonic::build_write_contact(p.station, &p.address, p.value) {
        Ok(frame) => panasonic_frame_result(request_id, frame),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_panasonic_parse_response(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        station: u8,
        #[serde(default)]
        expected_command: String,
        #[serde(default)]
        expected_header: Option<String>,
        #[serde(default)]
        response: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let expected_header = match p.expected_header.as_deref() {
        None | Some("") => None,
        Some(value) if value.chars().count() == 1 => value.chars().next(),
        Some(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::panasonic::parse_response(
        &p.response,
        p.station,
        &p.expected_command,
        expected_header,
    ) {
        Ok(response) => success(
            request_id.to_string(),
            json!({
                "header": response.header.to_string(),
                "station": response.station,
                "command": response.command,
                "payload": response.payload,
                "errorCode": response.error_code,
                "raw": response.raw,
            }),
            false,
        ),
        Err(error) => failure(Some(request_id.to_string()), error),
    }
}

fn handle_s7_cpu_control(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        action: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_cpu_control(&p.connection_id, &p.action) {
        Ok((code, msg)) => success(
            request_id.to_string(),
            json!({ "result": code, "message": msg }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_read_status(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_read_status(&p.connection_id) {
        Ok(mode) => success(request_id.to_string(), json!({ "mode": mode }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_s7_password(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        password: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.s7_password(&p.connection_id, &p.password) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "ok": true, "note": "S7-1200/1500 无会话密码机制,仅 300/400 有效" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_fw_tcp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        port: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_fw_tcp(&p.connection_id, &p.host, p.port) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct FwAddrPayload {
    connection_id: String,
    /// 区:"DB"/"M"/"I"/"Q"/"C"/"T"
    area: String,
    #[serde(default)]
    db: u8,
    address: u16,
    length: Option<u16>,
    values: Option<Vec<u8>>,
}

fn handle_fw_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    // 注意:不用 serde(flatten) —— flatten 与 deny_unknown_fields 组合存在字段丢失的已知问题
    let p: FwAddrPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let Some(org) = crate::s7_fetchwrite::fw_area_code(&p.area) else {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_FW_INVALID",
                message: format!("区「{}」不支持(DB/M/I/Q/C/T)", p.area),
                details: None,
            },
        );
    };
    match session.fw_read(
        &p.connection_id,
        org,
        p.db,
        p.address,
        p.length.unwrap_or(0),
    ) {
        Ok(data) => success(request_id.to_string(), json!({ "data": data }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_fw_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let p: FwAddrPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let Some(org) = crate::s7_fetchwrite::fw_area_code(&p.area) else {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "S7_FW_INVALID",
                message: format!("区「{}」不支持", p.area),
                details: None,
            },
        );
    };
    match session.fw_write(
        &p.connection_id,
        org,
        p.db,
        p.address,
        &p.values.unwrap_or_default(),
    ) {
        Ok(()) => success(request_id.to_string(), json!({ "ok": true }), false),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_start_fw_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
        port: u16,
        #[serde(default = "default_true_fins")]
        seed: bool,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_fw_slave(&p.slave_id, p.port, p.seed) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "slaveId": p.slave_id, "port": p.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_fw_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        slave_id: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_fw_slave(&p.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": p.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_open_mc_1e(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        host: String,
        port: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.open_mc_1e_tcp(&p.connection_id, &p.host, p.port) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": p.connection_id, "frame": "1e" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn mc_1e_split(address: &str) -> Result<(String, u32), CoreError> {
    let a = address.trim();
    let split = a
        .find(|c: char| c.is_ascii_digit())
        .ok_or_else(|| CoreError::Modbus {
            code: "MC_ADDRESS_INVALID",
            message: format!("「{a}」不是有效的软元件地址(如 D100/M100/X17)"),
            details: None,
        })?;
    let (prefix, num_str) = a.split_at(split);
    let num = crate::fx_programming::fx_prog_parse_number(prefix, num_str)?;
    Ok((prefix.to_uppercase(), num))
}

fn handle_mc_1e_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        points: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (prefix, num) = match mc_1e_split(&p.address) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = matches!(
        prefix.as_str(),
        "X" | "Y" | "M" | "S" | "B" | "TS" | "TC" | "CS" | "CC" | "SS" | "SC"
    );
    let cmd = if is_bit {
        crate::mc_1e::CMD1E_BIT_READ
    } else {
        crate::mc_1e::CMD1E_WORD_READ
    };
    let req = match crate::mc_1e::build_1e_read(cmd, &prefix, num, p.points, 10) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_1e_transact(&p.connection_id, &req) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_1e::parse_1e_response(&resp, cmd, p.points) {
        Ok(crate::mc_1e::OneEResponse::Words(w)) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": false, "values": w }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Bits(b)) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": true, "values": b.iter().map(|v| if *v { 1 } else { 0 }).collect::<Vec<u16>>() }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::WriteAck) => {
            success(request_id.to_string(), json!({ "endCode": 0 }), false)
        }
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({ "endCode": code, "detail": detail, "message": crate::mc_1e::onee_error_message(code, detail) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_1e_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let (prefix, num) = match mc_1e_split(&p.address) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let is_bit = matches!(
        prefix.as_str(),
        "X" | "Y" | "M" | "S" | "B" | "TS" | "TC" | "CS" | "CC" | "SS" | "SC"
    );
    let count = u16::try_from(p.values.len()).unwrap_or(0);
    let req = if is_bit {
        let bits: Vec<bool> = p.values.iter().map(|v| *v != 0).collect();
        crate::mc_1e::build_1e_write(crate::mc_1e::CMD1E_BIT_WRITE, &prefix, num, &[], &bits, 10)
    } else {
        crate::mc_1e::build_1e_write(
            crate::mc_1e::CMD1E_WORD_WRITE,
            &prefix,
            num,
            &p.values,
            &[],
            10,
        )
    };
    let req = match req {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let _ = count;
    let resp = match session.mc_1e_transact(&p.connection_id, &req) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_1e::parse_1e_response(
        &resp,
        if is_bit {
            crate::mc_1e::CMD1E_BIT_WRITE
        } else {
            crate::mc_1e::CMD1E_WORD_WRITE
        },
        0,
    ) {
        Ok(crate::mc_1e::OneEResponse::WriteAck) => {
            success(request_id.to_string(), json!({ "endCode": 0 }), false)
        }
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({ "endCode": code, "detail": detail, "message": crate::mc_1e::onee_error_message(code, detail) }),
            false,
        ),
        _ => failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "MC_1E_UNEXPECTED_RESPONSE",
                message: "1E 写响应格式异常".into(),
                details: None,
            },
        ),
    }
}

/// open_mc_udp_connection:MC/SLMP over UDP(§2.5,PLC 侧打开设置选 UDP)。
fn handle_open_mc_udp(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: OpenMcTcpPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let frame_type = match payload.frame_type.to_lowercase().as_str() {
        "3e" => crate::mc_frame::FrameType::Type3E,
        "4e" => crate::mc_frame::FrameType::Type4E,
        other => {
            return failure(
                Some(request_id.to_string()),
                CoreError::Modbus {
                    code: "MC_BAD_FRAME_TYPE",
                    message: format!("帧类型「{other}」无效(支持 3e/4e)"),
                    details: None,
                },
            );
        }
    };
    let route = crate::mc_frame::AccessRoute {
        network_no: payload.network_no,
        pc_no: payload.pc_no,
        module_io: payload.module_io,
        station_no: payload.station_no,
    };
    match session.open_mc_udp(
        &payload.connection_id,
        &payload.host,
        payload.port,
        route,
        frame_type,
        payload.watchdog,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "connectionId": payload.connection_id, "transport": "udp" }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_udp_read(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: McTcpReadPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&payload.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_read_batch_pdu(&addr, payload.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let resp = match session.mc_udp_transact(&payload.connection_id, &req_data) {
        Ok(r) => r,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    if resp.end_code != 0x0000 {
        return success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_batch_response(&resp.data, payload.points, addr.is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "isBit": addr.is_bit, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_udp_write(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let req_data = match crate::mc_pdu::build_write_batch_pdu(&addr, &p.values) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match session.mc_udp_transact(&p.connection_id, &req_data) {
        Ok(resp) => success(
            request_id.to_string(),
            json!({ "endCode": resp.end_code, "endCodeMessage": crate::mc_frame::end_code_message(resp.end_code) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_mc_ascii_write(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        connection_id: String,
        address: String,
        values: Vec<u16>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.mc_transact_ascii_write(&p.connection_id, &p.address, &p.values) {
        Ok(end_code) => success(
            request_id.to_string(),
            json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_c24_read:地址+点数+格式+站号 → C24 完整读请求帧(一步到位,供 Electron 串口事务)。
fn handle_mc_c24_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        address: String,
        points: u16,
        #[serde(default = "default_c24_format")]
        format: String,
        #[serde(default)]
        station: u8,
    }
    fn default_c24_format() -> String {
        "1".into()
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let addr = match crate::mc_address::parse_mc_address(&p.address) {
        Ok(a) => a,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let app = match crate::mc_pdu::build_read_batch_pdu(&addr, p.points) {
        Ok(d) => d,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::build_mc_serial_3c(p.station, format, &app) {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame), "isBit": addr.is_bit }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_c24_parse_read:C24 响应帧 → 去封装 → 应用区(结束码+数据) → 解出值。
fn handle_mc_c24_parse_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        points: u16,
        is_bit: bool,
        #[serde(default = "default_c24_format")]
        format: String,
    }
    fn default_c24_format() -> String {
        "1".into()
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    let (_station, app) = match crate::mc_serial::parse_mc_serial_3c_response(&p.frame, format) {
        Ok(v) => v,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    // 应用区 = 结束代码(2 LE) + 数据
    if app.len() < 2 {
        return failure(
            Some(request_id.to_string()),
            CoreError::Modbus {
                code: "MC_SERIAL_FRAME_TOO_SHORT",
                message: format!("C24 应用区 {} 字节,短于结束代码 2 字节", app.len()),
                details: None,
            },
        );
    }
    let end_code = u16::from_le_bytes([app[0], app[1]]);
    if end_code != 0 {
        return success(
            request_id.to_string(),
            json!({ "endCode": end_code, "endCodeMessage": crate::mc_frame::end_code_message(end_code) }),
            false,
        );
    }
    match crate::mc_pdu::parse_read_batch_response(&app[2..], p.points, p.is_bit) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "endCode": 0, "values": values }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// === 三菱 MC 串口 C24(3C/4C 离线组帧,§3.1)与 A-1E 帧(§3.4)===

/// 数据格式字符串 → McSerialFormat("1"/"3"/"4")。
fn mc_serial_format_from_str(format: &str) -> Result<crate::mc_serial::McSerialFormat, CoreError> {
    use crate::mc_serial::McSerialFormat;
    match format {
        "1" => Ok(McSerialFormat::Format1Ascii),
        "3" => Ok(McSerialFormat::Format3Binary),
        "4" => Ok(McSerialFormat::Format4BinaryNoChecksum),
        other => Err(CoreError::Modbus {
            code: "MC_SERIAL_BAD_FORMAT",
            message: format!(
                "数据格式「{other}」无效(支持 1=ASCII和校验 / 3=二进制和校验 / 4=二进制无校验)"
            ),
            details: None,
        }),
    }
}

/// mc_serial_build_3c:站号 + 3E 应用区(mc_pdu 产出)→ 3C/4C 串口帧。
fn handle_mc_serial_build_3c(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        format: String,
        station: u8,
        /// 3E 应用数据区(指令+子命令+软元件+点数…,mc_build_read 产出的 data 区)
        mc_app_data: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::build_mc_serial_3c(p.station, format, &p.mc_app_data) {
        Ok(frame) => {
            // 和校验:格式1 = 帧去掉尾部 SUM(2)+CRLF(2) 后低 8 位;格式3 = 帧尾 2 字节 LE
            let checksum = match format {
                crate::mc_serial::McSerialFormat::Format1Ascii => {
                    let sum = crate::mc_serial::mc_serial_checksum_ascii(&frame[..frame.len() - 4]);
                    Some(format!("{sum:02X}"))
                }
                crate::mc_serial::McSerialFormat::Format3Binary => {
                    let n = frame.len();
                    Some(format!(
                        "{:04X}",
                        u16::from_le_bytes([frame[n - 2], frame[n - 1]])
                    ))
                }
                crate::mc_serial::McSerialFormat::Format4BinaryNoChecksum => None,
            };
            success(
                request_id.to_string(),
                json!({
                    "frame": frame,
                    "frameHex": format_hex(&frame),
                    "checksum": checksum,
                }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_serial_parse_3c:PLC 串口响应 → (站号, 3E 应用区响应体)。
fn handle_mc_serial_parse_3c(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        format: String,
        frame: Vec<u8>,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let format = match mc_serial_format_from_str(&p.format) {
        Ok(f) => f,
        Err(e) => return failure(Some(request_id.to_string()), e),
    };
    match crate::mc_serial::parse_mc_serial_3c_response(&p.frame, format) {
        Ok((station, mc_app_data)) => success(
            request_id.to_string(),
            json!({
                "station": station,
                "mcAppData": mc_app_data,
                "mcAppDataHex": format_hex(&mc_app_data),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_build_read:A-1E 读请求帧(命令 00 位读 / 01 字读)。
fn handle_mc_1e_build_read(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        /// 0x00 位读 / 0x01 字读(§3.4.2 命令字节表)
        cmd: u8,
        device: String,
        head: u32,
        points: u16,
        #[serde(default = "default_1e_watchdog")]
        watchdog: u16,
    }
    fn default_1e_watchdog() -> u16 {
        10
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_1e::build_1e_read(p.cmd, &p.device, p.head, p.points, p.watchdog) {
        Ok(frame) => {
            let code = crate::mc_1e::device_code_1e_ascii(&p.device)
                .map(|c| String::from_utf8_lossy(&c).into_owned())
                .unwrap_or_default();
            success(
                request_id.to_string(),
                json!({ "frame": frame, "frameHex": format_hex(&frame), "deviceCode": code }),
                false,
            )
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_build_write:A-1E 写请求帧(命令 02 位写 / 03 字写)。
fn handle_mc_1e_build_write(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        /// 0x02 位写 / 0x03 字写(§3.4.2 命令字节表)
        cmd: u8,
        device: String,
        head: u32,
        #[serde(default)]
        values_words: Vec<u16>,
        /// 位值 0/1(内部转 bool)
        #[serde(default)]
        values_bits: Vec<u16>,
        #[serde(default = "default_1e_watchdog")]
        watchdog: u16,
    }
    fn default_1e_watchdog() -> u16 {
        10
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bits: Vec<bool> = p.values_bits.iter().map(|v| *v != 0).collect();
    match crate::mc_1e::build_1e_write(p.cmd, &p.device, p.head, &p.values_words, &bits, p.watchdog)
    {
        Ok(frame) => success(
            request_id.to_string(),
            json!({ "frame": frame, "frameHex": format_hex(&frame) }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// mc_1e_parse:A-1E 响应帧 → 字值 / 位值 / 写确认 / 异常(5BH 详细码)。
fn handle_mc_1e_parse(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Debug, Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        frame: Vec<u8>,
        /// 原请求命令字节(决定数据区解释:位打包/字小端/写确认)
        cmd: u8,
        #[serde(default)]
        points: u16,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match crate::mc_1e::parse_1e_response(&p.frame, p.cmd, p.points) {
        Ok(crate::mc_1e::OneEResponse::Words(values)) => success(
            request_id.to_string(),
            json!({ "status": "words", "values": values }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Bits(bits)) => success(
            request_id.to_string(),
            json!({
                "status": "bits",
                "values": bits.iter().map(|b| u16::from(*b)).collect::<Vec<u16>>(),
            }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::WriteAck) => success(
            request_id.to_string(),
            json!({ "status": "writeAck" }),
            false,
        ),
        Ok(crate::mc_1e::OneEResponse::Error { code, detail }) => success(
            request_id.to_string(),
            json!({
                "status": "error",
                "errorCode": code,
                "detailCode": detail,
                "message": crate::mc_1e::onee_error_message(code, detail),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_scan_station_ids(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: ScanStationIdsPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let started_at = std::time::Instant::now();
    let request_pdu = match pdu::build_read_holding_registers_pdu(0, 1) {
        Ok(p) => p,
        Err(e) => return failure(Some(request_id.to_string()), e.into()),
    };
    // 接通 timeout_ms:按用户参数缩短每站探测等待(默认 500ms)。
    // 旧实现忽略该参数,247 空站 × 5s 默认超时 = 约 20 分钟主循环假死。
    let _ = session.set_connection_read_timeout_ms(&payload.connection_id, payload.timeout_ms);
    let mut found: Vec<Value> = Vec::new();
    for station_id in payload.range_start..=payload.range_end {
        // 临时切换 unit_id:用 transact_tcp/udp 发请求,看是否成功
        // 由于 Session 的连接已绑定 unit_id,我们直接发 PDU,通过是否收到有效响应判断
        match session.probe_station(&payload.connection_id, station_id, &request_pdu) {
            Ok(response_ms) => {
                found.push(json!({
                    "stationId": station_id,
                    "firstResponseMs": response_ms,
                }));
            }
            Err(_) => { /* 超时或错误,跳过 */ }
        }
    }
    // 恢复默认超时
    let _ = session.set_connection_read_timeout_ms(&payload.connection_id, 0);
    let elapsed_ms = started_at.elapsed().as_millis();
    success(
        request_id.to_string(),
        json!({
            "found": found,
            "scanned": (payload.range_end as usize).saturating_sub(payload.range_start as usize) + 1,
            "elapsedMs": elapsed_ms,
        }),
        false,
    )
}

// --- 从站模拟 ---

fn handle_start_tcp_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StartTcpSlavePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_tcp_slave(&payload.slave_id, payload.port, payload.allowed_station_ids) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "running": true, "slaveId": payload.slave_id, "port": payload.port }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_slave(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_set_value(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveSetValuePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_set_value(
        &payload.slave_id,
        &payload.area,
        payload.address,
        &payload.values,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id, "area": payload.area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_set_coil(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveSetCoilPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_set_coil(
        &payload.slave_id,
        &payload.area,
        payload.address,
        &payload.values,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id, "area": payload.area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_clear(session: &mut Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: SlaveClearPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let area = payload.area.as_deref().unwrap_or("holding");
    match session.slave_clear(&payload.slave_id, area) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "cleared": true, "slaveId": payload.slave_id, "area": area }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

// --- 串口从站模拟 ---

fn handle_start_serial_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_serial_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "running": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_serial_slave(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveIdPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_serial_slave(&payload.slave_id) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "stopped": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_handle_serial_bytes(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SlaveHandleSerialBytesPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_handle_serial_bytes(&payload.slave_id, &payload.bytes) {
        Ok((should_respond, response_bytes)) => success(
            request_id.to_string(),
            json!({
                "shouldRespond": should_respond,
                "responseBytes": response_bytes,
                "responseHex": format_hex(&response_bytes),
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_serial_slave_set_value(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SerialSlaveSetValuePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.serial_slave_set_value(
        &payload.slave_id,
        &payload.area,
        payload.address,
        &payload.values,
    ) {
        Ok(()) => success(
            request_id.to_string(),
            json!({ "set": true, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_serial_slave_get_memory(
    session: &Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: SerialSlaveGetMemoryPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.serial_slave_get_memory(
        &payload.slave_id,
        &payload.area,
        payload.address,
        payload.count,
    ) {
        Ok(values) => success(
            request_id.to_string(),
            json!({ "values": values, "slaveId": payload.slave_id }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_slave_get_memory(session: &Session, request_id: &str, payload: Value) -> CommandOutcome {
    let payload: SlaveGetMemoryPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.slave_get_memory(
        &payload.slave_id,
        &payload.area,
        payload.address,
        payload.count,
    ) {
        Ok(values) => success(
            request_id.to_string(),
            json!({
                "values": values,
                "slaveId": payload.slave_id,
                "area": payload.area,
                "address": payload.address,
            }),
            false,
        ),
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_compute_crc16(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ChecksumPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let crc = crc16_modbus(&payload.bytes);
    let bytes = crc.to_le_bytes();
    success(
        request_id.to_string(),
        json!({
            "crc": crc,
            "crcHex": format!("0x{:04X}", crc),
            "crcHexLo": format!("0x{:02X}", bytes[0]),
            "crcHexHi": format!("0x{:02X}", bytes[1]),
        }),
        false,
    )
}

fn handle_compute_lrc(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ChecksumPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let lrc = crate::modbus_ascii::compute_lrc(&payload.bytes);
    success(
        request_id.to_string(),
        json!({
            "lrc": lrc,
            "lrcHex": format!("0x{:02X}", lrc),
        }),
        false,
    )
}

fn handle_parse_frame_online(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: ParseFrameOnlinePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let transport = payload.transport.as_str();
    // 解析帧
    let (unit_id, _pdu_data, fc, checksum_status) = if transport == "ascii" {
        match crate::modbus_ascii::parse_ascii_frame(&payload.bytes) {
            Ok((uid, pdu)) => {
                let fc_val = pdu.first().copied().unwrap_or(0);
                (uid, pdu, fc_val, "valid")
            }
            Err(_) => (0, vec![], 0, "invalid"),
        }
    } else {
        // RTU 或 TCP(尝试 RTU 解析)
        match crate::modbus_rtu::RtuFrame::decode(
            &payload.bytes,
            crate::modbus_rtu::RtuFrameRole::Response,
        ) {
            Ok(frame) => {
                let mut pdu = vec![frame.function_code()];
                pdu.extend_from_slice(frame.data());
                (frame.unit_id(), pdu, frame.function_code(), "valid")
            }
            Err(_) => (0, vec![], 0, "invalid"),
        }
    };

    let function_name = fc_name(fc & 0x7F);
    let is_exception = fc & 0x80 != 0;
    success(
        request_id.to_string(),
        json!({
            "isValid": checksum_status == "valid",
            "transport": transport,
            "unitId": unit_id,
            "functionCode": fc,
            "functionName": function_name,
            "isException": is_exception,
            "checksumStatus": checksum_status,
            "summary": format!("站号{} FC{:02X} {} {}", unit_id, fc, function_name, if is_exception { "(异常)" } else { "" }),
        }),
        false,
    )
}

fn handle_parse_frame_offline(request_id: &str, payload: Value) -> CommandOutcome {
    #[derive(Deserialize)]
    #[serde(rename_all = "camelCase", deny_unknown_fields)]
    struct P {
        hex: Option<String>,
        bytes: Option<Vec<u8>>,
        #[serde(default = "default_transport")]
        transport: String,
    }
    let p: P = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let bytes = if let Some(ref hex) = p.hex {
        match crate::frame_parser::parse_hex_string(hex) {
            Ok(b) => b,
            Err(e) => {
                return failure(
                    Some(request_id.to_string()),
                    CoreError::InvalidSerialConfig {
                        field: "hex",
                        message: e,
                    },
                );
            }
        }
    } else if let Some(b) = p.bytes {
        b
    } else {
        return failure(
            Some(request_id.to_string()),
            CoreError::InvalidSerialConfig {
                field: "hex",
                message: "必须提供 hex 或 bytes".into(),
            },
        );
    };
    let info = crate::frame_parser::parse_frame(&bytes, &p.transport);
    success(
        request_id.to_string(),
        serde_json::to_value(info).unwrap_or(json!({})),
        false,
    )
}

fn fc_name(fc: u8) -> &'static str {
    match fc {
        0x01 => "读线圈",
        0x02 => "读离散输入",
        0x03 => "读保持寄存器",
        0x04 => "读输入寄存器",
        0x05 => "写单线圈",
        0x06 => "写单寄存器",
        0x0F => "写多线圈",
        0x10 => "写多寄存器",
        0x16 => "屏蔽写寄存器",
        0x17 => "读写多寄存器",
        _ => "未知功能码",
    }
}

// --- 流式轮询(v2 协议) ---

fn handle_start_poll_stream(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StartPollStreamPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.start_poll_stream(
        &payload.stream_id,
        &payload.connection_id,
        payload.fc,
        payload.start_address,
        payload.quantity,
        payload.interval_ms,
    ) {
        Ok(()) => {
            // 返回首个响应(带 streamId),后续推送由 serve 循环检查到期后发送
            let mut outcome = success(
                request_id.to_string(),
                json!({
                    "streamId": payload.stream_id,
                    "started": true,
                    "intervalMs": payload.interval_ms,
                }),
                false,
            );
            outcome.response.stream_id = Some(payload.stream_id.clone());
            outcome
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

fn handle_stop_poll_stream(
    session: &mut Session,
    request_id: &str,
    payload: Value,
) -> CommandOutcome {
    let payload: StopPollStreamPayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    match session.stop_poll_stream(&payload.stream_id) {
        Ok(()) => {
            let mut outcome = success(
                request_id.to_string(),
                json!({ "streamId": payload.stream_id, "stopped": true }),
                false,
            );
            outcome.response.stream_id = Some(payload.stream_id.clone());
            outcome.response.stream_end = Some(true);
            outcome
        }
        Err(e) => failure(Some(request_id.to_string()), e),
    }
}

/// 检查 PDU 是否为异常响应,如果是返回对应的 failure outcome。
fn check_pdu_exception(pdu_bytes: &[u8], _expected_fc: u8) -> Option<CommandOutcome> {
    if pdu_bytes.is_empty() {
        return None;
    }
    let fc = pdu_bytes[0];
    if fc & 0x80 != 0 {
        let exception_code = pdu_bytes.get(1).copied().unwrap_or(0);
        Some(success(
            String::new(), // 会由调用者覆盖
            json!({
                "status": "exception",
                "exceptionCode": exception_code,
                "exceptionName": modbus_exception_name(exception_code),
            }),
            false,
        ))
    } else {
        None
    }
}

// =============================================================================
// 辅助
// =============================================================================

fn format_hex(bytes: &[u8]) -> String {
    bytes
        .iter()
        .map(|byte| format!("{byte:02X}"))
        .collect::<Vec<_>>()
        .join(" ")
}

fn format_read_registers_result(parsed: &modbus_rtu::ParsedReadHoldingRegistersResponse) -> Value {
    match parsed.exception_code {
        Some(code) => json!({
            "status": "exception",
            "exceptionCode": code,
            "exceptionName": modbus_exception_name(code),
            "registers": [],
        }),
        None => json!({
            "status": "ok",
            "exceptionCode": null,
            "exceptionName": null,
            "registers": parsed.registers,
        }),
    }
}

pub fn line_too_long() -> CommandOutcome {
    failure(None, CoreError::LineTooLong(crate::MAX_LINE_BYTES))
}

pub fn invalid_json() -> CommandOutcome {
    failure(None, CoreError::InvalidJson)
}

/// 构造一个流式推送帧(无 request_id,有 stream_id)。
/// 轮询流出错时的推送帧(带 stream_end=true,告知 JS 该流已终止)。
pub fn stream_error_outcome(stream_id: &str, e: &CoreError) -> CommandOutcome {
    let (code, message) = match e {
        CoreError::Modbus { code, message, .. } => (*code, message.clone()),
        other => ("POLL_STREAM_FAILED", other.to_string()),
    };
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: None,
            stream_id: Some(stream_id.to_string()),
            stream_end: Some(true),
            ok: false,
            result: None,
            error: Some(crate::error::ErrorBody {
                code,
                message,
                details: None,
            }),
        },
        shutdown: false,
    }
}

pub fn stream_push_outcome(stream_id: &str, result: Value) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: None,
            stream_id: Some(stream_id.to_string()),
            stream_end: Some(false),
            ok: true,
            result: Some(result),
            error: None,
        },
        shutdown: false,
    }
}

fn success(request_id: String, result: Value, shutdown: bool) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id: Some(request_id),
            stream_id: None,
            stream_end: None,
            ok: true,
            result: Some(result),
            error: None,
        },
        shutdown,
    }
}

fn failure(request_id: Option<String>, error: CoreError) -> CommandOutcome {
    CommandOutcome {
        response: ResponseEnvelope {
            protocol_version: PROTOCOL_VERSION,
            request_id,
            stream_id: None,
            stream_end: None,
            ok: false,
            result: None,
            error: Some(error.body()),
        },
        shutdown: false,
    }
}

// === 自定义帧解析(串口可视化批次 2): definition 每次随请求内联传入,不进 Session 状态 ===

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CustomFrameParsePayload {
    definition: Value,
    bytes: Vec<u8>,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
struct CustomFrameValidatePayload {
    definition: Value,
}

fn handle_custom_frame_parse(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: CustomFrameParsePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let def: crate::frame_definition::FrameDefinition =
        match serde_json::from_value(payload.definition) {
            Ok(d) => d,
            Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
        };
    match crate::frame_definition::parse_custom_frame(&payload.bytes, &def) {
        Ok(fields) => success(
            request_id.to_string(),
            json!({ "status": "ok", "fields": fields }),
            false,
        ),
        Err(e) => success(
            request_id.to_string(),
            json!({ "status": "error", "error": e }),
            false,
        ),
    }
}

fn handle_custom_frame_validate(request_id: &str, payload: Value) -> CommandOutcome {
    let payload: CustomFrameValidatePayload = match serde_json::from_value(payload) {
        Ok(p) => p,
        Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
    };
    let def: crate::frame_definition::FrameDefinition =
        match serde_json::from_value(payload.definition) {
            Ok(d) => d,
            Err(_) => return failure(Some(request_id.to_string()), CoreError::InvalidEnvelope),
        };
    let issues = crate::frame_definition::validate_definition(&def);
    success(
        request_id.to_string(),
        json!({ "status": "ok", "valid": issues.is_empty(), "issues": issues }),
        false,
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn run(session: &mut Session, line: &str) -> CommandOutcome {
        handle_line(session, line)
    }

    #[test]
    fn hello_reports_the_protocol_contract() {
        let mut session = Session::new();
        let outcome = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"request-1","command":"hello","payload":{}}"#,
        );
        assert!(outcome.response.ok);
        assert_eq!(outcome.response.request_id.as_deref(), Some("request-1"));
        let capabilities = outcome.response.result.unwrap()["capabilities"]
            .as_array()
            .unwrap()
            .clone();
        assert!(capabilities.contains(&json!("build_read_holding_registers")));
        assert!(capabilities.contains(&json!("tcp_write_multiple_registers")));
        assert!(capabilities.contains(&json!("build_write_single_coil")));
        assert!(!outcome.shutdown);
    }

    #[test]
    fn s7_large_read_chunks_advance_the_source_address() {
        let item = crate::s7_pdu::S7Item::new("DB1.DBB1000", 600).unwrap();
        let chunks = s7_chunk_items(&[item], 449);
        assert_eq!(chunks.len(), 2);
        assert_eq!(chunks[0][0].1.addr.byte, 1000);
        assert_eq!(chunks[0][0].1.count, 449);
        assert_eq!(chunks[1][0].1.addr.byte, 1449);
        assert_eq!(chunks[1][0].1.count, 151);
    }

    #[test]
    fn malformed_json_has_no_request_id() {
        let mut session = Session::new();
        let outcome = run(&mut session, "{");
        assert_eq!(outcome.response.request_id, None);
        assert_eq!(outcome.response.error.unwrap().code, "INVALID_JSON");
    }

    #[test]
    fn fc03_commands_build_and_parse_a_complete_transaction() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"build-1","command":"build_read_holding_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["adu"], json!([1, 3, 0, 0, 0, 2, 196, 11]));
        assert_eq!(result["expectedResponseLength"], 9);

        let mut response = vec![1, 3, 4, 0x12, 0x34, 0xAB, 0xCD];
        let crc = crc16_modbus(&response);
        response.extend_from_slice(&crc.to_le_bytes());
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "parse-1",
                "command": "parse_read_holding_registers",
                "payload": { "response": response, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        assert_eq!(result["registers"], json!([0x1234, 0xABCD]));
    }

    #[test]
    fn fc04_commands_build_and_parse_a_complete_transaction() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"build-4","command":"build_read_input_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["adu"], json!([1, 4, 0, 0, 0, 2, 113, 203]));
    }

    #[test]
    fn write_single_coil_build_command_produces_correct_adu() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"w1","command":"build_write_single_coil","payload":{"unitId":1,"address":10,"value":true}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        let adu = result["adu"].as_array().unwrap();
        let adu_bytes: Vec<u8> = adu.iter().map(|v| v.as_u64().unwrap() as u8).collect();
        // unit=1, fc=0x05, addr=0x000A, value=0xFF00 + CRC
        assert_eq!(adu_bytes[0], 1);
        assert_eq!(adu_bytes[1], 0x05);
        assert_eq!(adu_bytes[2], 0);
        assert_eq!(adu_bytes[3], 10);
        assert_eq!(adu_bytes[4], 0xFF);
        assert_eq!(adu_bytes[5], 0x00);
        // 最后 2 字节是 CRC(非零)
        assert_eq!(adu_bytes.len(), 8);
    }

    #[test]
    fn broadcast_write_reports_no_expected_response() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"wb","command":"build_write_single_register","payload":{"unitId":0,"address":0,"value":1234}}"#,
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(result["expectResponse"], false);
    }

    // === ASCII 串口主站端到端(FC01-06,15,16)===

    fn adu_bytes(outcome: &CommandOutcome) -> Vec<u8> {
        outcome.response.result.as_ref().unwrap()["adu"]
            .as_array()
            .unwrap()
            .iter()
            .map(|v| v.as_u64().unwrap() as u8)
            .collect()
    }

    #[test]
    fn ascii_fc03_build_matches_canonical_vector() {
        // 标准向量:站号1 FC03 读地址0数量2 → :010300000002FA\r\n
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"a1","command":"build_ascii_read_holding_registers","payload":{"unitId":1,"startAddress":0,"quantity":2}}"#,
        );
        assert!(built.response.ok);
        assert_eq!(adu_bytes(&built), b":010300000002FA\r\n".to_vec());
    }

    #[test]
    fn ascii_fc03_round_trip_parses_registers() {
        let mut session = Session::new();
        // 响应:unit=1 FC=03 byte_count=4 数据 1234 ABCD
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "a2",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        assert_eq!(result["registers"], json!([0x1234, 0xABCD]));
    }

    #[test]
    fn ascii_fc04_read_input_registers_round_trip() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x04, 0x02, 0x00, 0x05]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "a4",
                "command": "parse_ascii_read_input_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["registers"], json!([5]));
    }

    #[test]
    fn ascii_fc01_read_coils_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"ac1","command":"build_ascii_read_coils","payload":{"unitId":1,"startAddress":0,"quantity":8}}"#,
        );
        assert!(built.response.ok);
        // 响应:unit=1 FC=01 byte_count=1 数据 0xA5(8 个线圈)
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x01, 0x01, 0xA5]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "ac1p",
                "command": "parse_ascii_read_coils",
                "payload": { "response": resp, "unitId": 1, "quantity": 8 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "ok");
        // 0xA5 = 1010 0101(位顺序:低位在前)
        assert_eq!(
            result["coils"],
            json!([true, false, true, false, false, true, false, true])
        );
    }

    #[test]
    fn ascii_fc02_read_discrete_inputs_builds() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"ad2","command":"build_ascii_read_discrete_inputs","payload":{"unitId":2,"startAddress":10,"quantity":1}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        // 帧应以 ':' 开头,CRLF 结尾,FC=02
        assert_eq!(bytes[0], b':');
        assert_eq!(&bytes[bytes.len() - 2..], b"\r\n");
        // : 02 02 00 0A 00 01 LRC
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(2, &[0x02, 0x00, 0x0A, 0x00, 0x01])
        );
    }

    #[test]
    fn ascii_fc05_write_single_coil_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw5","command":"build_ascii_write_single_coil","payload":{"unitId":1,"address":10,"value":true}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        assert_eq!(bytes[0], b':');
        // : 01 05 00 0A FF 00 LRC —— 与裸帧构造器一致
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(1, &[0x05, 0x00, 0x0A, 0xFF, 0x00])
        );
        // 响应回显
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x05, 0x00, 0x0A, 0xFF, 0x00]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw5p",
                "command": "parse_ascii_write_single_coil",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 10);
        assert_eq!(result["value"], true);
    }

    #[test]
    fn ascii_fc06_write_single_register_round_trip() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x06, 0x00, 0x05, 0x12, 0x34]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw6",
                "command": "parse_ascii_write_single_register",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 5);
        assert_eq!(result["value"], 0x1234);
    }

    #[test]
    fn ascii_fc10_write_multiple_registers_round_trip() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw10","command":"build_ascii_write_multiple_registers","payload":{"unitId":1,"address":0,"values":[1,2]}}"#,
        );
        assert!(built.response.ok);
        // 响应:unit=1 FC=10 addr=0 qty=2
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x10, 0x00, 0x00, 0x00, 0x02]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aw10p",
                "command": "parse_ascii_write_multiple_registers",
                "payload": { "response": resp, "unitId": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["address"], 0);
        assert_eq!(result["quantity"], 2);
    }

    #[test]
    fn ascii_fc15_write_multiple_coils_builds() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"aw15","command":"build_ascii_write_multiple_coils","payload":{"unitId":1,"address":0,"values":[true,false,true]}}"#,
        );
        assert!(built.response.ok);
        let bytes = adu_bytes(&built);
        assert_eq!(bytes[0], b':');
        // : 01 0F 00 00 00 03 01 05 LRC(3 线圈 → 1 字节 0x05)
        assert_eq!(
            bytes,
            crate::modbus_ascii::build_ascii_frame(1, &[0x0F, 0x00, 0x00, 0x00, 0x03, 0x01, 0x05])
        );
    }

    #[test]
    fn ascii_parse_handles_exception_response() {
        let mut session = Session::new();
        // 从站返回异常:FC=0x83(FC03|0x80) + 异常码 0x02(非法数据地址)
        let resp = crate::modbus_ascii::build_ascii_frame(1, &[0x83, 0x02]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "aexc",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], "exception");
        assert_eq!(result["exceptionCode"], 2);
        assert_eq!(result["registers"], json!([]));
    }

    #[test]
    fn ascii_parse_rejects_unit_id_mismatch() {
        let mut session = Session::new();
        let resp = crate::modbus_ascii::build_ascii_frame(2, &[0x03, 0x04, 0x12, 0x34, 0xAB, 0xCD]);
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "auid",
                "command": "parse_ascii_read_holding_registers",
                "payload": { "response": resp, "unitId": 1, "quantity": 2 }
            })
            .to_string(),
        );
        assert!(!parsed.response.ok);
        assert_eq!(parsed.response.error.unwrap().code, "UNIT_ID_MISMATCH");
    }

    /// FX Computer Link:fx_links_build 构造 + fx_links_parse 解析 NAK
    #[test]
    fn fx_links_commands_build_and_parse() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxl-1",
                "command": "fx_links_build",
                "payload": { "station": 0, "cmd": "WR", "delay": 0, "data": "D00C8000A" }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        // 文档 §3.2.4 示例:WR 读 D200 起 10 字
        assert_eq!(
            result["frame"],
            json!([
                0x05, 0x30, 0x30, 0x46, 0x46, 0x57, 0x52, 0x30, 0x44, 0x30, 0x30, 0x43, 0x38, 0x30,
                0x30, 0x30, 0x41, 0x03, 0x42, 0x38, 0x0D, 0x0A
            ])
        );
        assert_eq!(result["checksum"], json!("B8"));

        // NAK:NAK "00" 错误码 "06"
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxl-2",
                "command": "fx_links_parse",
                "payload": { "response": [0x15, 0x30, 0x30, 0x30, 0x36, 0x0D, 0x0A] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("nak"));
        assert_eq!(result["errorCode"], json!(6));
    }

    /// MC 串口 C24:mc_serial_build_3c 构造(格式1 手算校验和 4D)+ mc_serial_parse_3c 还原
    #[test]
    fn mc_serial_commands_build_and_parse() {
        let mut session = Session::new();
        // mc_app_data = mc_pdu 读 D100 1 字的 3E 应用区(§2.1.4-(2))
        let app_data = [0x01, 0x04, 0x01, 0x00, 0x64, 0x00, 0x00, 0xA8, 0x01, 0x00];
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-1",
                "command": "mc_serial_build_3c",
                "payload": { "format": "1", "station": 0, "mcAppData": app_data }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        // "00"(站号)+"01040100640000A80100"(报文体)+ETX+"4D"(和校验)+CR LF
        let mut expect: Vec<u8> = b"0001040100640000A80100".to_vec();
        expect.push(0x03);
        expect.extend_from_slice(b"4D");
        expect.push(0x0D);
        expect.push(0x0A);
        assert_eq!(result["frame"], json!(expect));
        assert_eq!(
            result["checksum"],
            json!("4D"),
            "手算:0x60+0x3EA+0x03=0x44D→4D"
        );

        // 响应解析还原应用区(格式3 二进制 + 和校验)
        let resp_app: Vec<u8> = vec![0x00, 0x00, 0x34, 0x12];
        let resp_frame = crate::mc_serial::build_mc_serial_3c(
            0x0A,
            crate::mc_serial::McSerialFormat::Format3Binary,
            &resp_app,
        )
        .unwrap();
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-2",
                "command": "mc_serial_parse_3c",
                "payload": { "format": "3", "frame": resp_frame }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["station"], json!(0x0A));
        assert_eq!(result["mcAppData"], json!(resp_app));
        assert_eq!(result["mcAppDataHex"], json!("00 00 34 12"));

        // 能力表已注册
        let hello = run(
            &mut session,
            r#"{"protocolVersion":1,"requestId":"mcs-cap","command":"hello","payload":{}}"#,
        );
        let caps = hello.response.result.unwrap()["capabilities"]
            .as_array()
            .unwrap()
            .clone();
        assert!(caps.contains(&json!("mc_serial_build_3c")));
        assert!(caps.contains(&json!("mc_serial_parse_3c")));
        assert!(caps.contains(&json!("mc_1e_build_read")));

        // 非法格式拒绝
        let bad = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "mcs-3",
                "command": "mc_serial_build_3c",
                "payload": { "format": "2", "station": 0, "mcAppData": [0x01] }
            })
            .to_string(),
        );
        assert!(!bad.response.ok);
        assert_eq!(bad.response.error.unwrap().code, "MC_SERIAL_BAD_FORMAT");
    }

    /// A-1E:mc_1e_build_read 文档 §3.4.2 示例向量(字读 D100 起 12 点)
    #[test]
    fn mc_1e_build_read_matches_doc_vector() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-1",
                "command": "mc_1e_build_read",
                "payload": { "cmd": 1, "device": "D", "head": 100, "points": 12, "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(
            result["frameHex"],
            json!("01 FF 0A 00 64 00 00 00 44 2A 0C 00")
        );
        assert_eq!(result["deviceCode"], json!("D*"));
        assert_eq!(
            result["frame"],
            json!([
                0x01, 0xFF, 0x0A, 0x00, 0x64, 0x00, 0x00, 0x00, 0x44, 0x2A, 0x0C, 0x00
            ])
        );
    }

    /// A-1E:mc_1e_build_write(位打包)+ mc_1e_parse(字读/写确认/5BH 详细码)
    #[test]
    fn mc_1e_commands_write_and_parse() {
        let mut session = Session::new();
        // 位写 M0 起 3 点 [1,0,1] → 数据区 05 00(每 16 点 2 字节打包)
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-2",
                "command": "mc_1e_build_write",
                "payload": { "cmd": 2, "device": "M", "head": 0, "valuesBits": [1, 0, 1], "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(
            result["frameHex"],
            json!("02 FF 0A 00 00 00 00 00 4D 2A 03 00 05 00")
        );

        // 字读响应:81 00 + 34 12 → D100=0x1234
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-3",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00, 0x34, 0x12], "cmd": 1, "points": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("words"));
        assert_eq!(result["values"], json!([0x1234]));

        // 位读响应:81 00 05 00 → 前 3 位 [1,0,1]
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-4",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00, 0x05, 0x00], "cmd": 0, "points": 3 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("bits"));
        assert_eq!(result["values"], json!([1, 0, 1]));

        // 写确认:81 00
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-5",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x00], "cmd": 3, "points": 0 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        assert_eq!(parsed.response.result.unwrap()["status"], json!("writeAck"));

        // 异常:5BH + 详细代码 11H(软元件代码异常)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-6",
                "command": "mc_1e_parse",
                "payload": { "frame": [0x81, 0x5B, 0x11, 0x00], "cmd": 1, "points": 1 }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("error"));
        assert_eq!(result["errorCode"], json!(0x5B));
        assert_eq!(result["detailCode"], json!(0x11));
        assert!(result["message"].as_str().unwrap().contains("软元件代码"));

        // 位/字类别不匹配拒绝(字读命令配位元件)
        let bad = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "e1-7",
                "command": "mc_1e_build_read",
                "payload": { "cmd": 1, "device": "M", "head": 0, "points": 1, "watchdog": 10 }
            })
            .to_string(),
        );
        assert!(!bad.response.ok);
        assert_eq!(
            bad.response.error.unwrap().code,
            "MC_1E_DEVICE_CLASS_MISMATCH"
        );
    }

    /// FX 编程口:fx_prog_build_read 构造(文档 §3.3.5(1) 向量)+ fx_prog_parse 解析
    #[test]
    fn fx_prog_commands_build_and_parse() {
        let mut session = Session::new();
        let built = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-1",
                "command": "fx_prog_build_read",
                "payload": { "device": "D", "address": "123", "words": 2 }
            })
            .to_string(),
        );
        assert!(built.response.ok);
        let result = built.response.result.unwrap();
        assert_eq!(
            result["frame"],
            json!([
                0x02, 0x30, 0x31, 0x30, 0x46, 0x36, 0x30, 0x34, 0x03, 0x37, 0x34
            ])
        );

        // 读响应:数据 "3412"(D123 = 0x1234,低字节在前)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-2",
                "command": "fx_prog_parse",
                "payload": { "frame": [0x02, 0x33, 0x34, 0x31, 0x32, 0x03, 0x43, 0x44] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        let result = parsed.response.result.unwrap();
        assert_eq!(result["status"], json!("data"));
        assert_eq!(result["dataAscii"], json!("3412"));
        assert_eq!(result["words"], json!([0x1234]));

        // ACK(写成功)
        let parsed = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fxp-3",
                "command": "fx_prog_parse",
                "payload": { "frame": [0x06] }
            })
            .to_string(),
        );
        assert!(parsed.response.ok);
        assert_eq!(parsed.response.result.unwrap()["status"], json!("ack"));
    }

    #[test]
    fn hostlink_fins_command_keeps_area_codes_distinct_and_rejects_unsafe_input() {
        let mut session = Session::new();
        let w = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "hl-w",
                "command": "hostlink_build_fins",
                "payload": { "station": 0, "area": "W", "byte": 0, "count": 1 }
            })
            .to_string(),
        );
        assert!(w.response.ok);
        let w_text = w.response.result.unwrap()["frameText"]
            .as_str()
            .unwrap()
            .to_string();
        assert!(
            w_text.contains("B1"),
            "W word area must use FINS 0xB1: {w_text}"
        );

        let cio = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "hl-cio",
                "command": "hostlink_build_fins",
                "payload": { "station": 0, "area": "CIO", "byte": 0, "count": 1 }
            })
            .to_string(),
        );
        assert!(cio.response.ok);
        let cio_text = cio.response.result.unwrap()["frameText"]
            .as_str()
            .unwrap()
            .to_string();
        assert!(
            cio_text.contains("B0"),
            "CIO word area must use FINS 0xB0: {cio_text}"
        );

        let bad = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "hl-bad",
                "command": "hostlink_build_fins",
                "payload": { "station": 32, "area": "W", "byte": 0, "count": 1 }
            })
            .to_string(),
        );
        assert!(!bad.response.ok);
        assert_eq!(bad.response.error.unwrap().code, "HOSTLINK_PARAM_INVALID");

        let overflow = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "hl-range",
                "command": "hostlink_build_fins",
                "payload": { "station": 0, "area": "DM", "byte": 0xFFFFFF, "count": 2 }
            })
            .to_string(),
        );
        assert!(!overflow.response.ok);
        assert_eq!(
            overflow.response.error.unwrap().code,
            "HOSTLINK_PARAM_INVALID"
        );

        let cmode_overflow = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "cmode-range",
                "command": "hostlink_build_cmode_read",
                "payload": { "station": 0, "dmStart": 65535, "wordCount": 2 }
            })
            .to_string(),
        );
        assert!(!cmode_overflow.response.ok);
        assert_eq!(
            cmode_overflow.response.error.unwrap().code,
            "HOSTLINK_PARAM_INVALID"
        );
    }

    #[test]
    fn fins_network_commands_fail_closed_on_batch_window_and_bit_values() {
        let mut session = Session::new();
        let oversized_read = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fins-limit-read",
                "command": "fins_read",
                "payload": { "connectionId": "missing", "address": "D100", "count": 513 }
            })
            .to_string(),
        );
        assert!(!oversized_read.response.ok);
        assert_eq!(
            oversized_read.response.error.unwrap().code,
            "FINS_BATCH_LIMIT"
        );

        let overflowing_window = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fins-range-read",
                "command": "fins_read",
                "payload": { "connectionId": "missing", "address": "D16777215", "count": 2 }
            })
            .to_string(),
        );
        assert!(!overflowing_window.response.ok);
        assert_eq!(
            overflowing_window.response.error.unwrap().code,
            "FINS_ADDRESS_RANGE"
        );

        let bad_bit_write = run(
            &mut session,
            &json!({
                "protocolVersion": 1,
                "requestId": "fins-bit-value",
                "command": "fins_write",
                "payload": { "connectionId": "missing", "address": "CIO0.00", "values": [2] }
            })
            .to_string(),
        );
        assert!(!bad_bit_write.response.ok);
        assert_eq!(
            bad_bit_write.response.error.unwrap().code,
            "FINS_BIT_VALUE_INVALID"
        );
    }
}
