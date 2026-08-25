/**
 * Nexus 2.0 当前桌面协议注册表 MVP。
 *
 * 这是产品能力元数据，不等同于“真实设备已验证”：
 * - maturity 只描述当前软件实现阶段。
 * - evidence 明确目前是否只有软件/虚拟证据。
 * - commandFamily 是 UI、IPC 和 Rust 路由审计使用的稳定分类。
 */

const GROUPS = Object.freeze({
  master: {
    family: "modbus",
    label: "Modbus 主站",
    maturity: "S4-S5",
    transport: "tcp/udp/serial",
    roles: ["client", "scanner", "parser"],
    capabilities: ["connect", "read", "write", "poll", "scan", "offline-parse"],
    commandFamily: "modbus",
  },
  slave: {
    family: "modbus",
    label: "Modbus 从站/模拟器",
    maturity: "S4-S5",
    transport: "tcp/serial",
    roles: ["server", "simulator"],
    capabilities: ["start", "stop", "read", "write", "fault-injection"],
    commandFamily: "modbus-slave",
  },
  debug: {
    family: "serial-debug",
    label: "串口调试",
    maturity: "S3-S4",
    transport: "serial",
    roles: ["client", "parser"],
    capabilities: ["open", "send", "receive", "checksum"],
    commandFamily: "serial-debug",
  },
  melsec: {
    family: "melsec",
    label: "三菱 PLC",
    maturity: "S4-S5",
    transport: "tcp/udp/serial",
    roles: ["client", "simulator", "parser"],
    capabilities: ["connect", "read", "write", "diagnostics", "offline-parse"],
    commandFamily: "melsec",
  },
  siemens: {
    family: "siemens",
    label: "西门子 PLC",
    maturity: "S4-S5",
    transport: "tcp/serial/https",
    roles: ["client", "simulator", "parser"],
    capabilities: ["connect", "read", "write", "offline-parse"],
    commandFamily: "siemens",
  },
  omron: {
    family: "omron",
    label: "欧姆龙 PLC 通讯",
    maturity: "S4-S5",
    transport: "tcp/udp",
    roles: ["client", "simulator", "parser"],
    capabilities: ["connect", "read", "write", "offline-parse"],
    commandFamily: "omron",
  },
  "allen-bradley": {
    family: "allen-bradley",
    label: "Allen-Bradley PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "enip-cip",
  },
  beckhoff: {
    family: "beckhoff",
    label: "Beckhoff PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "ads-ams",
  },
  keyence: {
    family: "keyence",
    label: "Keyence PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "keyence-kv-hostlink",
  },
  "ls-electric": {
    family: "ls-electric",
    label: "LS Electric PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "xgt-fenet",
  },
  panasonic: {
      family: "panasonic",
      label: "Panasonic PLC",
      maturity: "S3-S4",
      transport: "serial",
      roles: ["client", "parser"],
      capabilities: ["connect", "offline-parse", "read"],
    commandFamily: "mewtocol-com",
  },
  delta: {
    family: "delta",
    label: "Delta PLC",
    maturity: "S4",
    transport: "modbus-profile",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "delta-modbus-profile",
  },
  inovance: {
    family: "inovance",
    label: "汇川 PLC",
    maturity: "S2-S3",
    transport: "modbus-profile",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "inovance-modbus-profile",
  },
  xinje: {
    family: "xinje",
    label: "信捷 PLC",
    maturity: "S2-S3",
    transport: "modbus-profile",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "xinjie-modbus-profile",
  },
  fatek: {
    family: "fatek",
    label: "FATEK PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "fatek-ascii",
  },
  fuji: {
    family: "fuji",
    label: "Fuji PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "fuji-sph",
  },
  ge: {
    family: "ge",
    label: "GE PLC",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "ge-srtp",
  },
  mqtt: {
    family: "mqtt",
    label: "MQTT 工业上行",
    maturity: "S2-S4b",
    transport: "tcp",
    roles: ["client", "parser"],
    capabilities: ["offline-parse", "read"],
    commandFamily: "mqtt-311",
  },
  iec: {
    family: "power-utility",
    label: "IEC 60870-5-104 电力规约",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "master", "parser"],
    capabilities: ["connect", "read", "interrogate", "offline-parse"],
    commandFamily: "iec104",
  },
  dnp: {
    family: "power-utility",
    label: "DNP3 电力规约",
    maturity: "S2-S4a",
    transport: "tcp",
    roles: ["client", "master", "parser"],
    capabilities: ["connect", "read", "integrity-poll", "class-scan", "offline-parse"],
    commandFamily: "dnp3",
  },
  dlt: {
    family: "power-utility",
    label: "DL/T 645 电能表规约",
    maturity: "S2-S4a",
    transport: "serial",
    roles: ["client", "master", "parser"],
    capabilities: ["connect", "read", "offline-parse"],
    commandFamily: "dlt645",
  },
  cjt: {
    family: "utility-meter",
    label: "CJ/T 188 水气热表规约",
    maturity: "S1-S3",
    transport: "serial",
    roles: ["parser"],
    capabilities: ["offline-parse"],
    commandFamily: "cjt188",
  },
  bacnet: {
    family: "building-automation",
    label: "BACnet/IP 楼宇自控",
    maturity: "S1-S4b",
    transport: "udp",
    roles: ["client", "parser"],
    capabilities: ["connect", "discover", "read", "offline-parse"],
    commandFamily: "bacnet-ip",
  },
  knx: {
    family: "building-automation",
    label: "KNXnet/IP 楼宇自控",
    maturity: "S1-S4b",
    transport: "udp",
    roles: ["client", "parser"],
    capabilities: ["connect", "read", "offline-parse"],
    commandFamily: "knxnet-ip-tunneling",
  },
});

const VARIANTS = Object.freeze({
  master: ["rtu", "ascii", "tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"],
  slave: ["tcp", "serial"],
  debug: ["serial"],
  melsec: ["3e", "4e", "ascii-3e", "ascii-4e", "mc-udp-3e", "mc-udp-4e", "mc-1e", "mc-c24", "fx-links", "fx-prog"],
  siemens: ["s7comm", "smart", "ppi", "ppi-serial", "fw", "uss", "rk512", "webapi"],
  omron: ["tcp", "udp", "hostlink-serial", "hostlink-fins-serial"],
  "allen-bradley": ["cip"],
  beckhoff: ["ads"],
  keyence: ["kv-host-link"],
  "ls-electric": ["xgt-fenet"],
  panasonic: ["mewtocol-com"],
  delta: ["dvp-modbus", "as-modbus"],
  inovance: ["h3u-modbus", "h5u-modbus"],
  xinje: ["xc-modbus", "xd-modbus"],
  fatek: ["ascii"],
  fuji: ["sph"],
  ge: ["srtp"],
  mqtt: ["mqtt-311"],
  iec: ["iec-60870-5-104"],
  dnp: ["dnp3-tcp"],
  dlt: ["dlt645-2007", "dlt645-1997"],
  cjt: ["cjt188-2004"],
  bacnet: ["bacnet-ip"],
  knx: ["tunneling-v1"],
});

const VARIANT_LABELS = Object.freeze({
  "master/rtu": "Modbus RTU",
  "master/ascii": "Modbus ASCII",
  "master/tcp": "Modbus TCP",
  "master/udp": "Modbus UDP",
  "master/rtu-over-tcp": "Modbus RTU over TCP",
  "master/ascii-over-tcp": "Modbus ASCII over TCP",
  "slave/tcp": "Modbus TCP 从站",
  "slave/serial": "Modbus RTU 从站",
  "debug/serial": "通用串口调试",
  "melsec/3e": "MC Binary 3E",
  "melsec/4e": "MC Binary 4E",
  "melsec/ascii-3e": "MC ASCII 3E",
  "melsec/ascii-4e": "MC ASCII 4E",
  "melsec/mc-udp-3e": "MC UDP 3E",
  "melsec/mc-udp-4e": "MC UDP 4E",
  "melsec/mc-1e": "A-1E / SLMP-1E",
  "melsec/mc-c24": "MC-C24 串口",
  "melsec/fx-links": "FX Computer Link",
  "melsec/fx-prog": "FX 编程口",
  "siemens/s7comm": "S7comm ISO-on-TCP",
  "siemens/smart": "S7-200 SMART",
  "siemens/ppi": "PPI over TCP 网关",
  "siemens/ppi-serial": "PPI 原生 COM（只读）",
  "siemens/fw": "S5 Fetch/Write",
  "siemens/uss": "USS",
  "siemens/rk512": "3964R / RK512",
  "siemens/webapi": "S7-1500 Web API",
  "omron/tcp": "FINS/TCP",
  "omron/udp": "FINS/UDP",
  "omron/hostlink-serial": "HostLink C-mode 串口（只读）",
  "omron/hostlink-fins-serial": "HostLink FINS 串口（只读）",
  "allen-bradley/cip": "EtherNet/IP · CIP Explicit（TCP 只读 + 编解码）",
  "beckhoff/ads": "Beckhoff ADS/AMS（TCP 只读 + 编解码）",
  "keyence/kv-host-link": "Keyence KV Host Link ASCII（TCP 只读 + 编解码）",
  "ls-electric/xgt-fenet": "LS Electric XGT FEnet（TCP 只读 + 编解码）",
    "panasonic/mewtocol-com": "Panasonic MEWTOCOL-COM（共享 COM 只读）",
  "delta/dvp-modbus": "Delta DVP/ES Modbus 地址 profile",
  "delta/as-modbus": "Delta AS/DVP-ES3 Modbus 地址 profile",
  "inovance/h3u-modbus": "汇川 H3U Modbus 地址 profile",
  "inovance/h5u-modbus": "汇川 H5U Modbus 地址 profile",
  "xinje/xc-modbus": "信捷 XC Modbus 地址 profile（仅 D 区确认）",
  "xinje/xd-modbus": "信捷 XD/XL Modbus 地址 profile（仅 D 区确认）",
  "fatek/ascii": "FATEK FBs 原生 ASCII（TCP 只读 + 编解码）",
  "fuji/sph": "Fuji MICREX-SX SPH Loader Command（TCP 只读 + 编解码）",
  "ge/srtp": "GE Series 90 / PACSystems SRTP（TCP 只读 + 编解码）",
  "mqtt/mqtt-311": "MQTT 3.1.1（TCP 只读订阅 + 编解码）",
  "iec/iec-60870-5-104": "IEC 60870-5-104（TCP 只读主站 + 总召）",
  "dnp/dnp3-tcp": "DNP3（TCP 只读 Master + Class 0/1/2/3）",
  "dlt/dlt645-2007": "DL/T 645-2007（共享 COM 只读，默认）",
  "dlt/dlt645-1997": "DL/T 645-1997（共享 COM 只读，旧版）",
  "cjt/cjt188-2004": "CJ/T 188-2004（离线只读编解码）",
  "bacnet/bacnet-ip": "BACnet/IP（Who-Is/I-Am + ReadProperty 只读会话/离线编解码）",
  "knx/tunneling-v1": "KNXnet/IP Tunneling v1（离线只读编解码）",
});

function descriptor(source, variant) {
  const group = GROUPS[source];
  if (!group) return null;
  const key = source + "/" + variant;
    const serialReadOnly = ["siemens/ppi-serial", "siemens/uss", "siemens/rk512", "melsec/mc-c24", "omron/hostlink-serial", "omron/hostlink-fins-serial", "panasonic/mewtocol-com", "dlt/dlt645-2007", "dlt/dlt645-1997"].includes(key);
  const state = serialReadOnly
    ? "online-readonly"
    : source === "cjt"
      ? "offline-codec"
      : (group.capabilities.includes("write") ? "online-readwrite" : "online-readonly");
  return Object.freeze({
    id: key,
    source,
    variant,
    label: VARIANT_LABELS[key] || (group.label + " · " + variant),
    family: group.family,
    maturity: source === "dlt" ? group.maturity : (serialReadOnly ? "S3-S4" : group.maturity),
    evidence: "software-and-virtual; L2 pending",
    state,
    transport: serialReadOnly ? "serial" : group.transport,
    roles: serialReadOnly ? ["client", "parser"] : [...group.roles],
    capabilities: serialReadOnly ? ["connect", "read", "offline-parse"] : [...group.capabilities],
    commandFamily: group.commandFamily,
    safety: serialReadOnly || source === "bacnet" || source === "knx" ? "read-only" : (group.capabilities.includes("write") ? "explicit-confirmation" : "read-only-or-debug"),
  });
}

export const PROTOCOL_REGISTRY_VERSION = 1;
export const PROTOCOL_REGISTRY = Object.freeze(
  Object.entries(VARIANTS).flatMap(([source, variants]) => variants.map((variant) => descriptor(source, variant))),
);

export function listProtocolVariants(source) {
  return PROTOCOL_REGISTRY.filter((entry) => entry.source === source).map((entry) => ({ ...entry }));
}

export function getProtocolDescriptor(source, variant) {
  return PROTOCOL_REGISTRY.find((entry) => entry.source === source && entry.variant === variant) || null;
}

export function listProtocolSources() {
  return Object.keys(GROUPS);
}
