const NETWORK_PC_STEPS = [
  "在“本机接口”页确认实际网卡和 IPv4；直连时把电脑设为与设备同网段且不重复的静态 IP。",
  "隔离调试网卡通常不填网关和 DNS；关闭 TUN/全局代理，或给 PLC 子网设置精确直连。",
  "Nexus 的主机/IP 填设备或网关地址，不要误填电脑自己的地址。",
];

const NETWORK_CHECKS = [
  "设备和电脑网口 LINK 灯亮，IP、掩码、端口及 TCP/UDP 类型完全一致。",
  "Ping 或 TCP 端口可达只算网络参考；必须收到正确协议响应才算通讯成功。",
  "同一网段没有重复 IP，防火墙没有拦截当前应用或端口。",
];

const SERIAL_PC_STEPS = [
  "在“本机接口”页或 Windows 设备管理器确认 USB 转换器对应的真实 COM 号。",
  "COM、波特率、数据位、校验和停止位必须与设备完全一致；同一 COM 同时只能被一个程序占用。",
  "RS-485 核对 A/B（D+/D-）和 GND；RS-232/RS-422/RS-485 电气层不能只靠改软件选项互换。",
];

const SERIAL_CHECKS = [
  "设备供电正常，转换器驱动正常，COM 号没有因重新插拔而变化。",
  "无响应时依次核对站号、波特率、校验、A/B 极性和寄存器/软元件地址。",
  "串口打开成功不等于协议成功；必须获得校验正确且站号匹配的响应帧。",
];

const READ_ONLY_WARNING = "首次连接真实 PLC/仪表先只读；未确认地址用途前，不写输出、不强制线圈、不远程启停。";
const MANUAL_WARNING = "这里的端口和串口格式是常用值或演示值，最终以 CPU、通讯模块、仪表手册及现有工程参数为准。";

function networkGuide({ label, summary, parameters, deviceSteps, pcSteps = [], checks = [], warnings = [] }) {
  return {
    label,
    medium: "network",
    summary,
    parameters,
    deviceSteps,
    pcSteps: [...NETWORK_PC_STEPS, ...pcSteps],
    checks: [...NETWORK_CHECKS, ...checks],
    warnings: [...warnings, MANUAL_WARNING, READ_ONLY_WARNING],
  };
}

function serialGuide({ label, summary, parameters, deviceSteps, pcSteps = [], checks = [], warnings = [] }) {
  return {
    label,
    medium: "serial",
    summary,
    parameters,
    deviceSteps,
    pcSteps: [...SERIAL_PC_STEPS, ...pcSteps],
    checks: [...SERIAL_CHECKS, ...checks],
    warnings: [...warnings, MANUAL_WARNING, READ_ONLY_WARNING],
  };
}

const modbusSerialParameters = (frame) => [
  ["COM", "Windows 实际端口", "例如 COM4；重新插拔后重新确认"],
  ["串口格式", "常见 9600 8N1", "波特率/数据位/校验/停止位必须与设备一致"],
  ["接口", "RS-232 或 RS-485", "485 常见两线 A/B，可加 GND"],
  ["站号", "1～247", "同一总线站号不可重复；广播站 0 不用于读取"],
  ["帧格式", frame, frame === "RTU（二进制+CRC）" ? "CRC-16 由协议自动处理" : "ASCII 文本帧+LRC"],
];

import { listProtocolVariants } from "./protocol-registry.js";

const modbusNetworkParameters = (transport) => [
  ["设备 IP", "设备实际 IPv4", "例如 192.168.1.30"],
  ["端口", "常用 502", "设备可配置为其他端口"],
  ["传输", transport, "客户端与设备设置必须一致"],
  ["站号", "常见 1", "网关后多从站时尤其重要"],
];

const mcNetworkParameters = (frame, transport = "TCP") => [
  ["PLC/模块 IP", "GX Works2 中的实际 IP", "电脑与 PLC 需同网段"],
  ["开放端口", "以 PLC Open settings 为准", "演示常用 5000；ASCII 常见另开 5001"],
  ["传输", transport, "PLC 开放设置与 Nexus 必须一致"],
  ["帧型", frame, "Binary/ASCII、3E/4E/1E 不能混用"],
  ["网络号 / PC号", "常见 0 / 255", "跨网络或专用模块按工程设置"],
];

export const PROTOCOL_GUIDE_SOURCES = {
  master: {
    label: "Modbus 主站",
    variants: {
      rtu: serialGuide({
        label: "Modbus RTU 串口",
        summary: "上位机通过 RS-232 或 RS-485 主动读取仪表、温度模块、变频器或 PLC 的 Modbus 从站。",
        parameters: modbusSerialParameters("RTU（二进制+CRC）"),
        deviceSteps: [
          "从设备铭牌、拨码或厂家软件中记录站号、波特率、校验、寄存器表和数据倍率。",
          "RS-485 总线两端按需要加终端电阻；所有从站使用相同串口格式，但站号必须唯一。",
        ],
      }),
      ascii: serialGuide({
        label: "Modbus ASCII 串口",
        summary: "Modbus ASCII 仍使用串口电气层，但帧为可读十六进制文本并使用 LRC 校验。",
        parameters: modbusSerialParameters("ASCII 文本+LRC"),
        deviceSteps: [
          "在仪表中明确选择 Modbus ASCII；仅设置成 RTU 时不能通讯。",
          "确认设备要求 7E1、7E2 或 8N1，ASCII 设备的串口格式差异较大。",
        ],
      }),
      tcp: networkGuide({
        label: "Modbus TCP",
        summary: "Nexus 作为 TCP 客户端，通过网线连接 PLC、仪表、网关或上位机的 Modbus TCP Server。",
        parameters: modbusNetworkParameters("TCP"),
        deviceSteps: [
          "在设备厂家软件中启用 Modbus TCP Server，记录 IP、掩码、端口和 Unit ID。",
          "若设备是 PLC，确认程序中的 Modbus Server 功能块已经运行且没有被其他连接占满。",
        ],
      }),
      udp: networkGuide({
        label: "Modbus UDP",
        summary: "使用 UDP 数据报承载 Modbus；无 TCP 连接握手，必须由设备明确支持。",
        parameters: modbusNetworkParameters("UDP"),
        deviceSteps: [
          "在设备中启用 Modbus UDP，并确认监听端口；支持 Modbus TCP 不代表支持 UDP。",
          "跨网段或广播场景需核对交换机、路由和防火墙的 UDP 策略。",
        ],
      }),
      "rtu-over-tcp": networkGuide({
        label: "Modbus RTU over TCP",
        summary: "通过串口服务器/网关透明传输完整 RTU 帧；它不是带 MBAP 头的标准 Modbus TCP。",
        parameters: [
          ["网关 IP/端口", "串口服务器实际值", "常见 4001、502、8899，但无统一默认"],
          ["下游串口", "设备实际格式", "例如 9600 8N1 / RS-485"],
          ["站号", "下游从站地址", "同一 485 总线不可重复"],
          ["封装", "RTU 原帧透明传输", "必须包含 CRC"],
        ],
        deviceSteps: [
          "把串口服务器设置为 TCP Server/透明传输，串口侧参数与下游 Modbus RTU 设备一致。",
          "确认网关没有启用 Modbus TCP 协议转换；协议转换模式应改用标准 Modbus TCP。",
        ],
      }),
      "ascii-over-tcp": networkGuide({
        label: "Modbus ASCII over TCP",
        summary: "通过 TCP 透明传输 Modbus ASCII 文本帧，常用于串口服务器连接老式 ASCII 仪表。",
        parameters: [
          ["网关 IP/端口", "串口服务器实际值", "无统一默认端口"],
          ["下游串口", "设备实际格式", "常见 7E1/7E2，也可能是 8N1"],
          ["站号", "下游从站地址", "1～247"],
          ["封装", "ASCII 文本透明传输", "LRC 校验，不是 CRC"],
        ],
        deviceSteps: [
          "把网关设为 TCP Server/透明传输，确认下游设备确实使用 Modbus ASCII。",
          "关闭会自动增加回车换行或修改报文内容的串口服务器文本模式。",
        ],
      }),
    },
  },

  slave: {
    label: "Modbus 从站",
    variants: {
      tcp: networkGuide({
        label: "Modbus TCP 从站模拟",
        summary: "Nexus 在本机回环地址启动 Modbus TCP Server，供同一台电脑上的主站或对照程序联调。",
        parameters: [
          ["监听地址", "127.0.0.1", "当前实现仅供本机闭环，不对局域网开放"],
          ["监听端口", "页面填写值", "标准端口 502 可能需要权限，也可用 5020"],
          ["允许站号", "空=全部", "也可填写 1,2,3-10"],
        ],
        deviceSteps: [
          "外部主站若与 Nexus 在同一电脑，目标填写 127.0.0.1 和相同端口。",
          "先在 Nexus 内存区写入测试值，再由主站读取对账。",
        ],
        pcSteps: ["确认监听端口未被其他 Modbus 模拟器或上位机占用。"],
        warnings: ["当前 TCP 从站绑定 127.0.0.1；另一台电脑不能直接连接，不能把本机回环当作局域网从站。"],
      }),
      serial: serialGuide({
        label: "Modbus RTU 串口从站模拟",
        summary: "Nexus 使用已打开的 COM 口模拟一个 Modbus RTU 从站，供另一台主站通过串口读取。",
        parameters: modbusSerialParameters("RTU 从站响应"),
        deviceSteps: [
          "对端主站的 COM 参数与 Nexus 主站页已经打开的串口完全一致。",
          "对端主站发送的 Unit ID 必须命中允许站号；先用保持寄存器测试值做只读对账。",
        ],
        warnings: ["Windows COM 口独占；Nexus 主站、从站和串口调试不能同时占用同一个物理端口。"],
      }),
    },
  },

  debug: {
    label: "串口调试",
    variants: {
      serial: serialGuide({
        label: "通用串口调试",
        summary: "透明收发 HEX/ASCII 原始字节，适合确认接线、回显、帧边界、CRC/LRC 和未知设备响应。",
        parameters: [
          ["COM", "Windows 实际端口", "USB 转换器重新插拔后需复核"],
          ["串口格式", "设备手册值", "波特率/数据位/校验/停止位必须一致"],
          ["接口电气层", "RS-232 / RS-422 / RS-485", "软件模式不能替代正确转换器和接线"],
          ["帧超时", "常用 10 ms 起", "低波特率或长帧可适当增大"],
          ["发送模式", "HEX / ASCII / Modbus RTU", "自动 CRC 只在确认需要时开启"],
        ],
        deviceSteps: [
          "记录设备协议帧、校验方式和结束条件；不知道时先只发送厂家手册给出的只读查询帧。",
          "RS-485 半双工确认收发方向由转换器自动控制，避免手动 RTS 设置错误。",
        ],
        warnings: ["串口调试允许发送任意字节；不要向生产设备发送来源不明、写入或控制类帧。"],
      }),
    },
  },

  melsec: {
    label: "三菱 PLC",
    variants: {
      "3e": networkGuide({
        label: "MC Binary 3E",
        summary: "用于支持 SLMP/MC 3E Binary 的 Q/L/iQ-R/iQ-F/FX5U 网络服务。",
        parameters: mcNetworkParameters("Binary 3E"),
        deviceSteps: [
          "在 GX Works2/GX Works3 的 Ethernet/Open settings 中新增 MC/SLMP Binary TCP 通道。",
          "记录 CPU/模块的 IP、开放端口、网络号和 PC 号；下载参数并按机型要求重启。",
        ],
        warnings: ["Q 系列 E71 的传统 MC 3E 子命令语义与当前 SLMP 语义存在差异；未完成对应变体验收前不要宣称通用兼容。"],
      }),
      "4e": networkGuide({
        label: "MC Binary 4E",
        summary: "4E 在 3E 基础上增加序列号，适合需要请求/响应配对的 SLMP 网络设备。",
        parameters: mcNetworkParameters("Binary 4E"),
        deviceSteps: [
          "在 PLC 开放设置中选择支持 4E/SLMP Binary 的通道，并记录实际端口。",
          "确认设备固件支持 4E；只支持 3E 的模块不能通过改下拉框升级为 4E。",
        ],
      }),
      "ascii-3e": networkGuide({
        label: "MC ASCII 3E",
        summary: "使用可读 ASCII 十六进制字段的 3E 帧；PLC 侧必须明确开放 ASCII MC 通道。",
        parameters: mcNetworkParameters("ASCII 3E"),
        deviceSteps: [
          "在 GX Works2/GX Works3 开放设置中选择 ASCII/文本帧，并记录端口；常见与 Binary 分开配置。",
          "确认上位机与 PLC 都选择 3E ASCII，不能连接到 Binary 通道。",
        ],
      }),
      "ascii-4e": networkGuide({
        label: "MC ASCII 4E",
        summary: "带序列号的 4E ASCII 帧，只有 PLC/模块明确支持并开放时才能使用。",
        parameters: mcNetworkParameters("ASCII 4E"),
        deviceSteps: [
          "在 PLC 开放设置中选择 4E ASCII，记录 TCP 端口并下载参数。",
          "若手册只列 3E，不要尝试用 4E 连接。",
        ],
      }),
      "mc-udp-3e": networkGuide({
        label: "MC over UDP 3E",
        summary: "通过 UDP 承载 MC Binary 3E；无 TCP 连接状态，PLC 侧必须开放 UDP 通道。",
        parameters: mcNetworkParameters("Binary 3E", "UDP"),
        deviceSteps: [
          "在 PLC Open settings 中把传输选为 UDP，帧型选 Binary 3E，并记录端口。",
          "确认交换机/防火墙允许 UDP；支持 TCP MC 不代表已经开放 UDP。",
        ],
      }),
      "mc-udp-4e": networkGuide({
        label: "MC over UDP 4E",
        summary: "通过 UDP 承载带序列号的 MC Binary 4E，用序列号进行响应配对。",
        parameters: mcNetworkParameters("Binary 4E", "UDP"),
        deviceSteps: [
          "PLC 开放设置选择 UDP + 4E Binary，并记录实际端口。",
          "确认 PLC/模块固件支持 4E UDP；不支持时改用已开放的 3E 通道。",
        ],
      }),
      "mc-1e": networkGuide({
        label: "A-1E / SLMP-1E",
        summary: "用于 A 系兼容 1E 设备及 FX3U-ENET/ENET-ADP 演示主线；Nexus 通过以太网读取真实软元件。",
        parameters: [
          ["PLC/ADP IP", "GX Works2 中的实际 IP", "演示计划为 192.168.1.20"],
          ["TCP 端口", "ADP 开放设置", "演示计划为 5000"],
          ["协议", "MC / A-1E", "FX3U ADP 真机不要选 3E"],
          ["电脑 IP", "同网段静态地址", "演示计划为 192.168.1.10/24"],
        ],
        deviceSteps: [
          "在 GX Works2 的 PLC/以太网适配器参数中设置 IP、TCP、端口和 MC 协议并下载。",
          "用 GX Works2 经 SC09 监视安全 D 寄存器，作为 Nexus 以太网读取的独立裁判。",
        ],
        checks: ["FX3U 首次以 D100 等确认安全的 D 区只读对账；实际 1E 响应才是 PASS。"],
      }),
      "mc-c24": serialGuide({
        label: "MC-C24 串口 3C帧",
        summary: "Q 系列 C24 通讯模块的串口 MC 通道；先在 Modbus 主站页打开同一个 COM，再回三菱页绑定。",
        parameters: [
          ["COM", "C24 模块连接的实际端口", "RS-232/422/485 取决于模块型号"],
          ["串口格式", "C24 参数中的实际值", "无统一默认，必须与模块参数一致"],
          ["站号", "模块设置值", "三菱页下方 FX 站号字段"],
          ["帧格式", "3C 格式1", "当前 Nexus 入口使用该格式"],
        ],
        deviceSteps: [
          "在 GX Works2/Q Parameter 的 C24 设置中确认接口、站号、协议格式和串口参数并下载。",
          "确认所用电缆电平与模块端口一致；RS-422 不能直接接普通 RS-232。",
        ],
        checks: ["本阶段只开放 C24 只读；写入按钮应禁用，不得把 C24 当成 FX 编程口写入。"],
      }),
      "fx-links": serialGuide({
        label: "FX Computer Link",
        summary: "FX 系列通过 232/485 扩展板或通讯适配器使用 Computer Link；先在 Modbus 主站页打开 COM。",
        parameters: [
          ["COM", "转换器实际端口", "选择正确 USB-SC09/USB-485 设备"],
          ["常用格式", "9600 7E1", "最终以 FX 通讯参数为准"],
          ["站号", "常见 0", "范围 0～31，同总线不可重复"],
          ["接口", "RS-232 或 RS-485", "按扩展板/适配器型号接线"],
        ],
        deviceSteps: [
          "在 GX Works2 中核对 FX 通讯口模式为 Computer Link，并记录站号与串口格式。",
          "多站 485 总线核对 A/B、终端电阻和每台 PLC 的唯一站号。",
        ],
      }),
      "fx-prog": serialGuide({
        label: "FX 编程口",
        summary: "通过 FX 编程口和 USB-SC09 类电缆读取 PLC；先在 Modbus 主站页按电缆参数打开 COM。",
        parameters: [
          ["COM", "USB-SC09 实际端口", "在设备管理器核对 VID/PID 和端口"],
          ["常用格式", "9600 7E1", "以电缆驱动和 PLC 编程口实际协商为准"],
          ["电缆", "FX 编程口专用电缆", "不能用普通 USB-RS485 直接替代全部 SC09 功能"],
        ],
        deviceSteps: [
          "确认 CPU 的圆形编程口/接口型号与电缆完全匹配，PLC 保持安全状态。",
          "关闭正在占用同一 SC09 COM 的 GX Works2 在线连接后，再让 Nexus 绑定。",
        ],
      }),
    },
  },

  siemens: {
    label: "西门子 PLC",
    variants: {
      s7comm: networkGuide({
        label: "S7comm / ISO-on-TCP",
        summary: "通过 TCP 102 与 S7-300/400/1200/1500 协商 COTP 和 S7 PDU，再读取 DB/M/I/Q 等变量。",
        parameters: [
          ["PLC IP", "TIA/STEP 7 中的实际 IP", "电脑需在同网段"],
          ["端口", "102", "ISO-on-TCP 标准端口"],
          ["Rack/Slot", "按 CPU 型号", "1200/1500 常见 0/1；300 常见 0/2；400 常见 0/3"],
          ["连接类型", "PG 常用", "被拒时按 CPU/工程切换 OP 或 Basic"],
          ["TSAP", "通常自动", "仅非标设备/网关需要自定义"],
        ],
        deviceSteps: [
          "S7-1200/1500 在 TIA Portal 允许远程 PUT/GET，并对目标 DB 取消“优化的块访问”。",
          "S7-300/400 核对机架槽位、CP 模块和保护等级；下载配置前保存原工程。",
        ],
      }),
      smart: networkGuide({
        label: "S7-200 SMART",
        summary: "SMART SR/ST 机型通过 TCP 102 读取 V 区；V 区在 Nexus 中兼容映射到 DB1。",
        parameters: [
          ["PLC IP", "Micro/WIN SMART 读取的实际 IP", "不要现场猜地址"],
          ["端口", "102", "COTP/S7comm"],
          ["Rack/Slot", "0 / 0", "连接失败时 Nexus 会再尝试 slot 1"],
          ["地址", "VW100 等安全 V 区", "VW100 = DB1.DBW100"],
        ],
        deviceSteps: [
          "记录 CPU 订货号和固件；V2.8 CPU 用 Micro/WIN SMART V2.8，V3 CPU 用 V3 软件。",
          "V3 在通讯设置中启用 Put/Get Server、保存并下载；建议限制通讯写入范围。",
          "CR20s/30s/40s/60s 没有以太网口，不能走本条 TCP 主线。",
        ],
        checks: ["用 Micro/WIN SMART 监视同一 VW 地址的 HEX 值，与 Nexus 只读结果对账。"],
      }),
      ppi: networkGuide({
        label: "PPI over TCP 网关",
        summary: "当前 Nexus PPI 在线入口连接的是 PPI-以太网网关，不是直接占用电脑 COM 口。",
        parameters: [
          ["网关 IP/端口", "串口服务器实际值", "页面 IP/端口填网关监听地址"],
          ["下游串口", "常见 9600 8E1", "PPI 波特率以 PLC/网关设置为准"],
          ["PLC 站号", "当前入口使用 2", "网关和 PLC 站号必须一致"],
          ["接口", "RS-485", "S7-200/SMART Port0"],
        ],
        deviceSteps: [
          "把 PPI 网关设为透明传输或明确的 PPI 网关模式，并与 PLC Port0 参数一致。",
          "确认 PLC 站号为 2；如果现场不是站 2，当前入口需要先扩展站号配置，不能盲连。",
        ],
        warnings: ["不要把页面端口保留为 102；这里应填写 PPI 网关自己的 TCP 监听端口。"],
      }),
      "ppi-serial": serialGuide({
        label: "PPI 原生 COM（只读）",
        summary: "通过主站页已经打开的 Windows COM 口执行 PPI 双拍只读：SD2 请求 → E5 → SA 确认 → SD2 数据。",
        parameters: [
          ["COM", "主站页当前打开的端口", "不能与其他串口事务并发占用"],
          ["串口格式", "PLC/适配器实际参数", "常见 9600 8E1；187.5k 需兼容的 USB-PPI 电缆"],
          ["PLC 站号", "当前默认 2", "首轮只读先固定目标站；多主站/令牌后续再验收"],
          ["安全范围", "V/M/I/Q 只读", "不要用该入口尝试写入或控制 CPU"],
        ],
        deviceSteps: [
          "确认 S7-200/SMART Port0 的电气接口、PPI 适配器驱动、A/B 线序和终端匹配，先保存 PLC 原参数。",
          "在 Micro/WIN SMART 监视同一安全 V/M/I/Q 地址，记录站号、波特率、校验和已知值。",
        ],
        warnings: [
          "此入口只证明软件双拍和串口链路；没有真实 PLC 回读证据时不能标记 L2/实机 PASS。",
          "PPI 串口由主站页独占；关闭其他串口轮询、调试或从站服务后再读取。",
        ],
      }),
      fw: networkGuide({
        label: "S5 Fetch/Write",
        summary: "用于配置了 Fetch/Write 兼容通道的 S5/CP343/CP443；端口由 NetPro 工程决定，不使用 102。",
        parameters: [
          ["PLC/CP IP", "工程实际 IP", "电脑与 CP 同网段"],
          ["Fetch/Write 端口", "NetPro 配置值", "常见示例 2000，但没有统一默认"],
          ["连接方向", "Nexus 主动连接", "CP 侧配置为被动监听"],
        ],
        deviceSteps: [
          "在 STEP 7/NetPro 为 CP343/443 创建 Fetch/Write 连接并记录本地端口。",
          "编译下载硬件与连接配置，确认 CP 诊断缓冲区无连接资源错误。",
        ],
        warnings: ["页面端口必须改成 NetPro 实际端口；102 是 S7comm，不是 Fetch/Write。"],
      }),
      uss: serialGuide({
        label: "USS 变频器",
        summary: "SINAMICS/MM4/G120 等变频器常用 RS-485 USS；当前接入参数只读事务，不开放控制字和参数写入。",
        parameters: [
          ["COM/接口", "USB-RS485 实际端口", "两线 A/B，必要时共地"],
          ["串口格式", "设备参数值", "常见 9600/19200，8E1"],
          ["USS 地址", "0～31", "每台变频器唯一"],
          ["PZD/PKW 长度", "变频器参数", "必须与报文配置一致"],
        ],
        deviceSteps: [
          "在变频器参数中设置 USS 地址、波特率、控制源和通讯超时，先保存原参数。",
          "首次测试只读取状态字/实际值，不发送启动、速度给定或控制字。",
        ],
        warnings: ["当前入口只做 USS 参数只读；控制字、速度给定和写参数必须等独立安全验收。", "软件/虚拟通过不能替代真实变频器、PZD 长度和站号的 L2 记录。"],
      }),
      rk512: serialGuide({
        label: "3964R / RK512",
        summary: "S5、CP341/441 等点对点模块使用 3964R 链路和 RK512 数据层；当前接入指定数据区只读，写入仍关闭。",
        parameters: [
          ["COM/接口", "模块实际接口", "RS-232/TTY/RS-485 取决于 CP 型号"],
          ["串口格式", "CP 参数值", "波特率、校验和停止位必须一致"],
          ["3964R 优先级", "高/低", "两端冲突处理参数需匹配"],
          ["DB/偏移", "PLC 工程地址", "先读已知安全数据区"],
        ],
        deviceSteps: [
          "在 STEP 7/COM 3964R 中核对 CP341/441 的接口、波特率、优先级和 RK512 参数并下载。",
          "使用匹配的电气转换器，尤其注意 TTY 电流环与普通 RS-232 不兼容。",
        ],
        warnings: ["当前入口只做指定数据区只读；RK512 写入、协调字策略和控制命令必须等独立安全验收。", "软件/虚拟通过不能替代真实 CP341/441 的 3964R 参数和 L2 记录。"],
      }),
      webapi: networkGuide({
        label: "S7-1500 Web API",
        summary: "S7-1500 固件支持时，通过 HTTPS 443 和 JSON-RPC 使用符号变量名访问 Web API。",
        parameters: [
          ["PLC IP", "TIA Portal 中的实际 IP", "建议固定地址"],
          ["端口", "443", "HTTPS"],
          ["Web 用户", "TIA 中创建的 Web API 用户", "不是 TIA 工程密码"],
          ["变量", "PLC 符号名", "可访问范围取决于用户权限"],
        ],
        deviceSteps: [
          "确认 S7-1500 固件版本支持 Web API，在 TIA Portal 启用 Web Server/API。",
          "创建最小权限用户，只开放演示需要的变量读取权限，编译下载并确认 HTTPS 可访问。",
        ],
        checks: ["核对证书/时间、用户名和权限；认证成功后再用符号变量做只读验证。"],
      }),
    },
  },

  omron: {
    label: "欧姆龙 PLC 通讯",
    variants: {
      tcp: networkGuide({
        label: "FINS/TCP",
        summary: "通过 TCP 连接 CJ/CS/CP 系列本体网口或 ETN21 模块，完成 FINS 节点协商和内存区读写。",
        parameters: [
          ["PLC/ETN IP", "CX-Programmer 中的实际 IP", "电脑与 PLC 同网段"],
          ["端口", "9600", "FINS/TCP 常用端口"],
          ["目标节点", "通常为 PLC IP 末段", "以 FINS 网络参数为准"],
          ["本机节点", "通常为电脑 IP 末段", "自动协商失败时显式填写"],
        ],
        deviceSteps: [
          "在 CX-Programmer/CX-Integrator 的 Ethernet Unit Setup 中设置 IP、FINS 网络号和节点号。",
          "下载参数并按模块要求重启；确认连接资源和 IP 地址转换表。",
        ],
      }),
      udp: networkGuide({
        label: "FINS/UDP",
        summary: "通过 UDP 9600 直接发送 FINS 命令；无 TCP 节点协商，源/目标节点设置更关键。",
        parameters: [
          ["PLC/ETN IP", "设备实际 IP", "电脑与 PLC 同网段"],
          ["端口", "9600", "FINS/UDP 常用端口"],
          ["目标节点", "PLC FINS 节点", "通常但不必然等于 IP 末段"],
          ["本机节点", "电脑 FINS 节点", "与局域网内其他节点不重复"],
        ],
        deviceSteps: [
          "在 Ethernet Unit Setup 中启用/确认 FINS UDP、网络号、节点号及 IP 转换方式。",
          "跨网段时配置 FINS 路由表；只改 Windows 路由不一定足够。",
        ],
      }),
      "hostlink-serial": serialGuide({
        label: "HostLink C-mode 串口（只读）",
        summary: "通过共享 COM 口执行欧姆龙 HostLink C-mode RR 读 DM 首轮事务；当前不代表 HostLink FINS、写入或全部 CPU 系列兼容。",
        parameters: [
          ["COM", "主站页已打开的实际 COM", "Windows 串口由主站页独占"],
          ["串口格式", "以 CPU/串口单元手册为准，常见 9600 7E2", "不能把 8N1 演示值当作设备默认"],
          ["站号", "0..31", "HostLink C-mode 站号，不等同 FINS 节点号"],
          ["地址", "D0..D65535", "首轮只读 DM 字，暂不支持 CIO/WR/HR/AR"],
          ["数量", "1..100 字", "受首轮事务长度和设备限制约束"],
        ],
        deviceSteps: [
          "在 CPU/串口单元参数中确认 HostLink/C-mode 已启用、站号和通信格式；记录是否为 RS-232、RS-422 或 RS-485。",
          "先用 CX-Programmer 或厂家工具只读确认 DM 地址，再把同一地址填入 Nexus，避免把 CIO/HR 地址误当 DM。",
        ],
        pcSteps: [
          "在主站页打开 COM 后再回到欧姆龙页面选择 HostLink C-mode；不要让两个程序同时占用同一 COM。",
          "首轮只执行读取；看到 TX/RX 后核对 @站号、RR、结束码 00、FCS 和 CRLF。",
        ],
        checks: [
          "响应站号必须与请求站号一致，FCS 正确且返回字数等于请求数量。",
          "串口打开成功不等于 HostLink 成功；必须收到合法 RR 响应帧。",
        ],
        warnings: [
          "当前实现只覆盖 C-mode RR 读 DM；不把它宣传为 HostLink FINS 或全系列兼容。",
          "写入、运行控制、参数下载和非 DM 区域保持禁用，待型号/手册/实机矩阵后再扩展。",
        ],
      }),
      "hostlink-fins-serial": serialGuide({
        label: "HostLink FINS 串口（只读）",
        summary: "通过共享 COM 口执行 HostLink FINS 0101 字读取首轮事务；当前不开放位读取、写入或完整 HostLink 服务集。",
        parameters: [
          ["COM", "主站页已打开的实际 COM", "Windows 串口由主站页独占"],
          ["串口格式", "以 CPU/串口单元手册为准", "常见值不能替代设备参数"],
          ["站号", "0..31", "HostLink 站号，不等同 FINS/TCP 节点协商"],
          ["地址", "D/CIO/W/H 字地址", "首轮只支持字读取，不支持位地址"],
          ["数量", "1..100 字", "受首轮事务长度和设备限制约束"],
        ],
        deviceSteps: [
          "确认 CPU/串口单元启用 HostLink FINS、站号和通信格式，并记录 HostLink 头代码/服务支持。",
          "先用 CX-Programmer 或厂家工具只读核对 D/CIO/W/H 字地址，再在 Nexus 中执行同一地址读取。",
        ],
        pcSteps: [
          "在主站页打开 COM 后选择 HostLink FINS；同一 COM 不能同时被其他软件占用。",
          "首轮只读，核对 @站号 FA、FINS 0101、结束码 0000、FCS 和 CRLF。",
        ],
        checks: [
          "响应站号、FA 头代码、FCS、FINS 结束码和返回字节数都必须匹配。",
          "HostLink FINS 串口成功不代表 FINS/TCP 或 FINS/UDP 节点参数已验证。",
        ],
        warnings: [
          "当前仅实现 HostLink FINS 0101 字读取；不把它宣传为完整 HostLink 兼容。",
          "写入、位读取、路由网络号和高级服务需按具体 CPU/串口单元另行验收。",
        ],
      }),
    },
  },

  "allen-bradley": {
    label: "Allen-Bradley PLC",
    variants: {
      cip: networkGuide({
        label: "EtherNet/IP · CIP Explicit（TCP 只读 + 编解码）",
        summary: "首轮新增的 Allen-Bradley 路径：TCP 44818 的 RegisterSession、SendRRData/CPF 和 CIP Read Tag 只读会话；软件独立 TCP 对端已验证，真实 CompactLogix/ControlLogix L2 仍未完成。",
        parameters: [
          ["PLC IP", "CompactLogix/ControlLogix 实际 IPv4", "同网段、无重复 IP；TCP 只读会话仍需型号/固件核对"],
          ["端口", "44818", "EtherNet/IP 默认 TCP 端口，设备侧可能被防火墙隔离"],
          ["Session Handle", "RegisterSession 响应返回值", "未注册时请求用 0；不能伪造现场会话"],
          ["Tag", "例如 MyTag、MyTag[3]、Program:MainProgram.Speed", "大小写、程序作用域和数组索引必须与工程一致"],
          ["Elements", "1..65535", "首轮只构建 Read Tag，不开放写入或控制服务"],
        ],
        deviceSteps: [
          "在 Studio 5000/设备工程中确认控制器型号、固件、EtherNet/IP 端口和目标 Tag 的外部访问权限。",
          "先用厂家工具只读确认 Tag 名称、类型和安全地址；不要把在线读到的值当成允许写入的授权。",
        ],
        pcSteps: [
          "先在本机接口页确认实际网卡、子网和防火墙策略；TCP 端口可达只算网络参考。",
          "先用独立 TCP 对端验证 RegisterSession/Read Tag 分片收帧，再把相同 Tag/Session/Context 与抓包或厂家工具逐字段比对。",
        ],
        checks: [
          "ENIP 24 字节头的 command、length、session、sender context、options 必须一致。",
          "SendRRData CPF 必须包含 Null Address + Unconnected Data，CIP 回复必须校验 service、general status、extended status 和数据边界。",
          "软件向量和独立 TCP 对端只代表 S2-S4a；真实设备 L2 需要独立记录型号、固件、Tag 类型、抓包和重复读结果。",
        ],
        warnings: [
          "当前只实现 RegisterSession + unconnected Read Tag TCP 只读会话，不实现 ForwardOpen、Connected I/O、Implicit I/O、CIP Safety、PCCC 或写 Tag。",
          "不要因为 UI 能生成报文就把 EtherNet/IP、CIP、PCCC 或具体 Allen-Bradley 型号标记为已实机通过。",
        ],
      }),
    },
  },
  beckhoff: {
    label: "Beckhoff PLC",
    variants: {
      ads: networkGuide({
        label: "ADS/AMS over TCP（TCP 只读 + 编解码）",
        summary: "首轮新增的 Beckhoff 路径：ADS/TCP 48898 只读会话、AMS endpoint/InvokeId 校验、Read/ReadDeviceInfo/ReadState；独立 TCP 对端已验证，不自动创建 AMS Route，也不开放写入。",
        parameters: [
          ["PLC/路由器 IP", "TwinCAT ADS Router 实际 IPv4", "IP 可达不等于已建立 AMS Route"],
          ["ADS/TCP 端口", "48898", "默认 ADS TCP；以 TwinCAT Router 配置为准"],
          ["目标 AMS NetId", "例如 5.72.144.1.1.1", "六个 0..255 十进制字节；必须与目标 TwinCAT 路由一致"],
          ["目标 AMS Port", "TwinCAT 3 PLC 常见 851", "TwinCAT 2 常见 801；IO/系统服务端口不能混用"],
          ["源 AMS NetId / Port", "本机路由身份与端口", "目标 Router 必须配置回程 Route；首轮 UI 只构建字段"],
          ["InvokeId", "每个请求唯一的 u32", "响应必须与请求匹配，不能接受陈旧响应"],
          ["IndexGroup/Offset", "原始 ADS 地址或符号句柄地址", "首轮支持原始 Read/Write/ReadWrite 帧，不宣称工程符号已解析"],
        ],
        deviceSteps: [
          "在 TwinCAT XAE/Router 中记录 Runtime 版本、PLC AMS NetId、ADS Port、路由状态和目标符号/IndexGroup。",
          "先用 TwinCAT/厂家工具只读执行 ReadDeviceInfo、ReadState 和一个已知地址；记录返回 ADS/AMS 错误码。",
          "若使用符号句柄，后续必须单独完成 Create/Use/Release 生命周期和断线清理，不把句柄缓存跨连接复用。",
        ],
        pcSteps: [
          "先在本机接口页确认实际网卡和 IPv4；暂不把 VPN/TUN/代理链路当作 ADS 路由证据。",
          "先用独立 TCP 对端验证 ADS Read/ReadDeviceInfo/ReadState 分片收帧，再把头部、NetId、端口、InvokeId 和 payload 与抓包逐字段比对。",
        ],
        checks: [
          "AMS/TCP 保留字段必须为 0，长度必须与 32 字节 AMS 头及 DataLength 精确一致。",
          "响应必须是 StateFlags=0x0005、CommandId/InvokeId 匹配且 AMS 路由错误为 0；ADS Result 单独显示并映射为可读错误。",
          "Read/ReadWrite 响应必须校验 Result + DataLength + 数据边界；Write 响应必须恰好为 4 字节 Result。",
          "软件向量和独立 TCP 对端只代表 S2-S4a；真实 Runtime、型号、AMS Route 和重复读仍需 L2 记录。",
        ],
        warnings: [
          "当前不实现 AMS Route 自动创建、符号句柄缓存/释放、Sum Command、通知、UDP AMS、IPv6 或安全传输；TCP 会话仅允许上述只读命令。",
          "页面可以生成 Write 报文只是编解码能力，不代表已经获得现场写入授权；真实 PLC 首轮仍只读。",
        ],
      }),
    },
  },
  keyence: {
    label: "Keyence PLC",
    variants: {
      "kv-host-link": networkGuide({
        label: "KV Host Link ASCII（TCP 只读 + 编解码）",
        summary: "首轮新增的 Keyence 路径：KV Host Link over TCP 8501、CR/CR NN 握手、RDS 字/位只读和 ASCII/CRLF 响应错误码。页面可建立软件 TCP 只读会话；WRS/ST/RS、MC Compatible、EtherNet/IP 和真实 KV L2 仍单独隔离。",
        parameters: [
          ["PLC IP", "KV 系列实际 IPv4", "KV-7000/8000、KV-Nano 等型号和固件需单独确认"],
          ["TCP 端口", "8501", "KV Host Link 常用默认端口，以设备设置为准"],
          ["站号", "0..31 或不使用站号", "CR / CR NN 握手模式必须与设备配置一致"],
          ["地址", "DM/EM/FM/ZF/TM/CM/VM/MR/LR/CR/R/B/VB/W/Z", "R/MR/LR/CR 支持 word.bit；W/B/VB 偏移为十六进制"],
          ["命令", "RDS/WRS/ST/RS", "字读写、位读写和后续监视命令分开，不混用 MC"],
          ["响应", "ASCII + CRLF", "CC/OK、十进制字值/0-1 位值和 E0/E1/E2/E4/E5/E6 错误码"],
        ],
        deviceSteps: [
          "在 KV STUDIO/PLC 工程中确认 CPU 型号、固件、Ethernet/Host Link 端口、站号模式和目标地址区。",
          "先用厂家工具只读核对一个 DM 字、一个 MR/LR/CR 位；记录返回值、响应结束符和设备错误码。",
          "确认目标设备实际支持 KV Host Link；MC Compatible、EtherNet/IP/CIP 和专用服务另按独立协议验收。",
        ],
        pcSteps: [
          "先在本机接口页确认实际网卡、子网和防火墙；TCP 8501 可达只算网络参考。",
          "在本页生成 CR、RDS/WRS/ST/RS 报文；软件 TCP 对端只允许 CR 握手和 RDS 只读，逐字段比对 ASCII 命令、地址归一化、数量、CR 和响应 CRLF。",
        ],
        checks: [
          "RDS/WRS 数量必须是 1..256；字设备不能带位后缀，位设备不能走字命令。",
          "响应必须严格是单行 CRLF；字响应数量和 0..65535 范围、位响应的 0/1、写响应 OK 都要匹配。",
          "E0/E1/E2/E4/E5/E6 等设备错误保留原始码并映射可读说明，不把错误文本当成有效数据。",
          "软件向量和独立 TCP 对端只代表 S2-S4a；真实 KV 型号、固件、Host Link 设置和 KV STUDIO 比对仍需 L2。",
        ],
        warnings: [
          "当前只实现软件 TCP 只读会话，不实现真实 KV 型号适配、串口 Host Link、MC Compatible、EtherNet/IP/CIP、PLC 运行控制、监视订阅或虚拟 KV 服务器。",
          "页面可以生成 WRS/ST/RS 只是编解码能力，不代表现场写入授权；真实 KV 首次验收仍只读。",
        ],
      }),
    },
  },
  "ls-electric": {
    label: "LS Electric PLC",
    variants: {
      "xgt-fenet": networkGuide({
        label: "XGT FEnet（TCP 只读 + 编解码）",
        summary: "首轮新增的 LS Electric 路径：XGT Dedicated/FEnet TCP 2004、20 字节官方头、XGK/XGI/XGR CPU 标识、X/B/W/D/L 显式变量、单变量和连续只读响应校验。页面可建立软件 TCP 只读会话；写入、PLC 控制和真实 XGT L2 仍单独隔离。",
        parameters: [
          ["PLC IP", "XGK/XGI/XGR 实际 IPv4", "IP 可达只算网络参考，CPU/固件和 FEnet 通道需独立确认"],
          ["TCP 端口", "2004", "XGT FEnet 常用默认端口，以设备参数为准"],
          ["Company ID", "LSIS-XGT / LGIS-GLOFA", "响应必须是官方 10 字节字段之一；不接受任意文本"],
          ["CPU Type", "0xA0 XGK / 0xA4 XGI / 0xA8 XGR", "请求头 byte 12；具体 CPU/通信模块以现场手册为准"],
          ["变量", "%DX100 / %DW100 / %DD100 / %MX10", "必须带 X/B/W/D/L 显式类型；不接受无类型 D100"],
          ["Base/Slot", "0..15", "头 byte 18 以高低半字节编码；默认 base=0、slot=3 仅作向量默认"],
        ],
        deviceSteps: [
          "在 XG5000/PLC 工程中确认 CPU 型号、固件、FEnet 通道、IP、端口、Base/Slot 和目标变量类型。",
          "先用厂家工具只读核对一个 %DW/%MW 变量和一个 %MX 位变量；记录响应 command、InvokeId、error status、block count 和数据长度。",
          "确认目标控制器确实启用 XGT Dedicated/FEnet；Modbus TCP、EtherNet/IP、OPC UA 和 IEC 变量服务另按独立协议验收。",
        ],
        pcSteps: [
          "先在本机接口页确认实际网卡、子网和防火墙；TCP 2004 可达不等于 XGT 会话已建立。",
          "在本页生成单变量读、连续读、单变量写、连续写黄金帧；软件 TCP 对端只允许单变量/连续只读，再把 20 字节头、InvokeId、校验和、变量名和 application length 逐字段比对。",
        ],
        checks: [
          "请求 source 必须是 0x33，响应 source 必须是 0x11；Company ID、InvokeId、header checksum 和 application length 都必须严格匹配。",
          "单变量 X/B/W/D/L 类型必须和显式地址一致；连续读写使用 0x14 并单独校验 byte count/data length。",
          "Read 响应必须 block count=1，数据长度与帧边界一致；Write 响应必须恰好包含 2 字节 error status。",
          "软件向量和独立 TCP 对端只代表 S2-S4a；真实 XGT 型号、固件、FEnet 模块和重复只读结果仍需 L2 记录。",
        ],
        warnings: [
          "当前只实现软件 TCP 只读会话，不实现真实 XGT 型号适配、变量批量优化、PLC RUN/STOP、程序/文件服务、事件订阅或虚拟 XGT 服务器。",
          "页面可以生成 Write 报文只是编解码能力，不代表现场写入授权；真实 LS Electric 首次验收仍只读。",
        ],
      }),
    },
  },
  delta: {
    label: "Delta PLC",
    variants: {
      "dvp-modbus": serialGuide({
        label: "DVP/ES Modbus 地址 profile",
        summary: "Delta DVP/ES/EX/SS 使用标准 Modbus RTU/ASCII/TCP 传输；本页把 DVP 软元件名翻译为已审计的 Modbus 区域、地址和功能码，并可复用已打开的 RTU/ASCII COM 做只读分段读回，不把 profile 冒充独立私有协议。",
        parameters: [
          ["系列", "DVP/ES/EX/SS", "具体 CPU/通信模块仍需按 Delta 手册确认"],
          ["软元件", "S/X/Y/T/C/M/D", "X/Y 使用八进制；D/M 存在不连续地址段"],
          ["底层传输", "Modbus RTU/ASCII/TCP", "实际串口/IP 参数仍在 Modbus 主站页设置"],
          ["功能码", "FC01/02/03/05/06", "由软元件区和读写权限决定"],
        ],
        deviceSteps: [
          "确认 DVP/ES/EX/SS 的具体型号、通信模块、Modbus 站号或 TCP 端口和固件。",
          "从 Delta 手册确认目标软元件区存在；系列上限不代表每台 CPU 都实现全部地址。",
        ],
        pcSteps: [
          "先在 Modbus 主站页打开相同 RTU/ASCII COM；本页只复用该句柄做只读分段读回，并保存每段 TX/RX。",
          "把 D4095/D4096、M1535/M1536 等不连续边界拆成独立请求，不跨段批量合并。",
        ],
        checks: [
          "DVP X/Y 数字必须按八进制解释，D/M 不连续段必须命中正确 Modbus 地址。",
          "X 为 FC02 只读，Y/M/T/C/S 为线圈类，D 为保持寄存器；未审计区必须拒绝而不是猜测。",
          "软件地址映射通过不等于 DVP/AS 实机 L2；需要型号、固件和抓包/厂家工具对照。",
        ],
        warnings: [
          "当前页不自动打开或关闭 COM，不发送任何写入；底层 Modbus 的写入仍需原值记录、回读和恢复。",
          "不要把 Delta DVP profile 当作独立 DVP 私有帧；私有协议若有需求另立协议卡。",
        ],
      }),
      "as-modbus": serialGuide({
        label: "AS/DVP-ES3 Modbus 地址 profile",
        summary: "Delta AS300/DVP-ES3 的 Modbus 地址表与 DVP 不同；本页锁定 M/SM/S、X/Y 位/字、D/SR/T/C/HC/E 映射和 D 寄存器取位拒绝边界，并可复用已打开的 RTU/ASCII COM 做只读分段读回。",
        parameters: [
          ["系列", "AS300/DVP-ES3", "型号/固件和通讯模块必须先记录"],
          ["软元件", "M/SM/S/X/Y/D/SR/T/C/HC/E", "X/Y 位地址使用 Xword.bit / Yword.bit"],
          ["底层传输", "Modbus RTU/ASCII/TCP", "实际传输仍从 Modbus 主站页选择"],
          ["安全边界", "D100.5 禁止", "寄存器取位需要独立读改写语义，当前不猜"],
        ],
        deviceSteps: [
          "确认 AS300 或 DVP-ES3 型号、通信模块、串口格式、站号/IP 和目标设备区权限。",
          "用厂家工具核对一个 X/Y 位、一个 X/Y 字和一个 D/SR 字，再保存返回值与功能码。",
        ],
        pcSteps: [
          "先在本页解析地址和规划分段，再在 Modbus 主站页打开相同 RTU/ASCII COM；只读读回会保留跨区拆分和每段 TX/RX。",
          "不要把 AS 的地址表套用到 DVP，也不要把 D100.5 自动转成掩码写入。",
        ],
        checks: [
          "AS X/Y 位和字地址使用不同基址，X 为只读、Y 允许标准线圈/寄存器读写。",
          "D/SR/E 等寄存器范围和 HC/T/C 位范围必须 fail-closed。",
          "地址 profile 通过只代表软件映射证据；真实 AS/ES3 L2 仍需独立记录。",
        ],
        warnings: [
          "当前页不自动打开或关闭 COM，不发送写入；D 寄存器取位和跨区批量写入另立安全门禁。",
          "AS 地址表不是通用 Modbus 地址表，具体 CPU 能力以厂家手册为准。",
        ],
      }),
    },
  },
  inovance: {
    label: "汇川 PLC",
    variants: {
      "h3u-modbus": networkGuide({
        label: "H3U Modbus 地址 profile",
        summary: "H3U 使用标准 Modbus RTU/TCP；本页只把 M/SM/S/T/C/X/Y 与 D/SD/R/T/C 映射为已审计的 Modbus 区域、地址和功能码，不能把 H3U profile 当作 EasyNet、ComputerLink 或 EtherNet/IP/CIP。",
        parameters: [
          ["系列", "H3U", "记录 CPU 型号、固件、工程版本和通信口"],
          ["底层传输", "Modbus RTU 或 Modbus TCP", "串口/IP 参数仍由 Modbus 主站配置"],
          ["位软元件", "M/SM/S/T/C/X/Y", "X/Y 按八进制，可选 .0..7 点号位；X 只读"],
          ["字软元件", "D/SD/R/T/C", "R 与 C200..C255 使用 FC16/双寄存器边界"],
          ["AM 系列", "未确认/不注册", "没有统一地址表时必须显式拒绝"],
        ],
        deviceSteps: [
          "确认 H3U CPU 型号、固件、串口/以太网端口、Modbus 站号或 IP/端口；不要把编程口或 EasyNet 名称当作 Modbus 证据。",
          "用汇川工具或工程文档只读核对一个 D、一个 M、一个 X/Y 和一个 C200+ 计数器地址，记录实际功能码与返回值。",
        ],
        pcSteps: [
          "先在本页选择 Bit/Word 或 Auto 并解析地址，再到 Modbus 主站页选择 RTU/TCP；页面不自动连接 H3U。",
          "保留软元件、Modbus 地址、功能码和型号/固件记录；X 写入、M7680..M7999 和未确认区必须拒绝。",
        ],
        checks: [
          "H3U X/Y 按八进制解析，M7680..M7999 是地址空洞；H3U C200..C255 是双寄存器计数器并使用 FC16。",
          "Bit 与 Word 访问必须显式分开，D100.5、ReadInt16(M0) 等类型错配 fail-closed。",
          "软件 profile 和 Modbus JSONL 通过只代表 S2/S3；真实 H3U L2 需要型号、固件、抓包和重复只读。",
        ],
        warnings: [
          "首轮不实现 EasyNet 私有帧、Connected CIP、ComputerLink、Modbus ASCII、AM/AC/Easy 统一表或任何 PLC 控制。",
          "当前只提供地址 profile；不要因为地址解析成功就执行写入或把 H3U 标记为现场通过。",
        ],
      }),
      "h5u-modbus": networkGuide({
        label: "H5U Modbus 地址 profile",
        summary: "H5U 使用标准 Modbus RTU/TCP；本页固定 M/B/S/X/Y 位区与 D/R 字区，X/Y 使用八进制并保留 0xFFFF 边界，不混入 EasyNet 或其他私有协议。",
        parameters: [
          ["系列", "H5U", "记录 CPU 型号、固件、工程版本和通信口"],
          ["底层传输", "Modbus RTU 或 Modbus TCP", "实际串口/IP 参数仍由 Modbus 主站配置"],
          ["位软元件", "M/B/S/X/Y", "X/Y 按八进制；X 只读，Y 可写"],
          ["字软元件", "D/R", "R 使用 FC16 写多寄存器语义"],
          ["AM 系列", "未确认/不注册", "没有统一地址表时必须显式拒绝"],
        ],
        deviceSteps: [
          "确认 H5U CPU 型号、固件、串口/以太网口、Modbus 站号或 IP/端口，并记录工程版本。",
          "先用汇川工具只读核对 D、R、M/B 和 Y1777 边界；确认 X/Y 八进制不是十进制输入。",
        ],
        pcSteps: [
          "先解析 H5U 软元件并核对 FC01/03/05/06/16，再在 Modbus 主站页执行通讯；本页首轮只做 profile。",
          "保存型号/固件、软元件、Modbus 地址和返回功能码；未确认 AM/AC/Easy 区域必须 fail-closed。",
        ],
        checks: [
          "H5U Y1777 必须落到 0xFFFF，X/Y 点号位只允许 .0..7；R32767 必须落到 0xAFFF。",
          "Bit 与 Word 访问不能互换，D100.5 和不支持前缀必须返回明确错误。",
          "软件 profile 通过不等于 H5U 实机 L2；需要型号、固件、抓包、厂家工具值和长稳记录。",
        ],
        warnings: [
          "首轮不实现 EasyNet 私有帧、Connected CIP、ComputerLink、Modbus ASCII、AM/AC/Easy 统一表或 PLC 控制。",
          "页面只证明标准 Modbus 地址 profile；任何写入仍需单独安全门禁和原值恢复。",
        ],
      }),
    },
  },
  xinje: {
    label: "信捷 PLC",
    variants: {
      "xc-modbus": networkGuide({
        label: "XC Modbus 地址 profile（仅 D 区确认）",
        summary: "信捷 XC 首轮只确认标准 Modbus RTU/TCP 的 D 数据寄存器：D{n} 为 0-based holding register，FC03 读取、FC06 写入。HD/SD/SM/M/X/Y/C/T/S 区域与具体 XC 型号表尚未确认，页面必须拒绝而不是套用旧偏移。",
        parameters: [
          ["系列", "XC", "记录 CPU 型号、固件和工程版本"],
          ["底层传输", "Modbus RTU 或 Modbus TCP", "串口/IP 参数仍由 Modbus 主站配置"],
          ["已确认地址", "D0..D65535", "十进制、0-based、FC03/FC06"],
          ["未确认地址", "HD/SD/SM/M/X/Y/C/T/S", "必须提供具体型号手册和抓包后再增加"],
        ],
        deviceSteps: [
          "确认 XC CPU 型号、固件、Modbus 端口/站号和厂家软件设置；不要把编程口、自由口或 HSL 偏移当作通用证据。",
          "用厂家工具只读核对 D0、D100 和一个批量 D 区，记录功能码、站号、响应长度和型号/固件。",
        ],
        pcSteps: [
          "先解析 D 地址，再到 Modbus 主站页执行标准 FC03；本页不自动打开 TCP/COM。",
          "保存软元件、Modbus 地址、功能码、型号/固件和原始 TX/RX；其他区域会明确 fail-closed。",
        ],
        checks: [
          "D100 必须映射到 Modbus 0x0064，读取 FC03，写入 FC06。",
          "X/Y/S 的八进制、HD/SD/SM/M/C/T 的偏移不能在没有型号手册时猜测。",
          "软件 profile 通过只代表 D 区 S2/S3；真实 XC L2 需要手册、抓包和重复只读记录。",
        ],
        warnings: [
          "首轮不实现信捷私有命令、自由口、编程口、批量位区或 PLC 控制。",
          "D 映射确认不等于其他软元件区域可用；写入仍需单独安全门禁。",
        ],
      }),
      "xd-modbus": networkGuide({
        label: "XD/XL Modbus 地址 profile（仅 D 区确认）",
        summary: "信捷 XD/XL 首轮只确认标准 Modbus RTU/TCP 的 D 数据寄存器：D{n} 为 0-based holding register，FC03 读取、FC06 写入。XD/XL 与 XC 的其他软元件偏移可能不同，未确认区保持拒绝。",
        parameters: [
          ["系列", "XD/XL", "记录具体 CPU 型号、固件和工程版本"],
          ["底层传输", "Modbus RTU 或 Modbus TCP", "串口/IP 参数仍由 Modbus 主站配置"],
          ["已确认地址", "D0..D65535", "十进制、0-based、FC03/FC06"],
          ["未确认地址", "HD/SD/SM/M/X/Y/C/T/S", "不同系列可能不同，不能共用 XC 偏移"],
        ],
        deviceSteps: [
          "确认 XD/XL 具体 CPU、固件、Modbus 端口/站号；记录工程软件和厂家手册版本。",
          "用厂家工具只读核对 D0、D100 和一个批量 D 区，保留 TX/RX、返回功能码和设备身份。",
        ],
        pcSteps: [
          "先解析 D 地址，再在 Modbus 主站页执行标准 FC03；本页不会自动连接设备。",
          "任何非 D 区都先停在资料门禁，不复制 XC、HSL 或旧 C# 的未经确认偏移。",
        ],
        checks: [
          "D100 必须映射到 Modbus 0x0064，读取 FC03，写入 FC06。",
          "XD/XL 的 X/Y/S 八进制和其余区域需具体型号手册确认后才能新增 profile。",
          "软件 profile 通过只代表 D 区 S2/S3；真实 XD/XL L2 仍需抓包、回读和长稳记录。",
        ],
        warnings: [
          "首轮不实现信捷私有命令、自由口、编程口、批量位区或 PLC 控制。",
          "页面只证明 D 寄存器映射，不宣称 XC/XD/XL 全地址兼容。",
        ],
      }),
    },
  },
  fatek: {
    label: "FATEK PLC",
    variants: {
      ascii: networkGuide({
        label: "FBs 原生 ASCII（TCP 只读 + 编解码）",
        summary: "FATEK FBs 原生 ASCII 编程协议 over TCP：STX + 两位十六进制站号 + 40/44/45/46/47 命令 + 加和校验 + ETX。用户显式连接 TCP 5000 后可执行 44/46 只读，写入和 RUN/STOP 仍不开放。",
        parameters: [
          ["传输", "TCP，典型端口 5000", "具体以 FATEK Ethernet 模块配置为准"],
          ["站号", "01H..FEH", "帧内两位大写十六进制；0 和 FF 拒绝"],
          ["位命令", "44/45，1..255 点", "X/Y/M/S/T/C，四位十进制地址"],
          ["字命令", "46/47，1..64 字", "R/D/RT/RC，或位区的 W 前缀字操作数"],
          ["校验", "ASCII 加和低 8 位", "从 STX 到校验字段前，不含 ETX"],
        ],
        deviceSteps: [
          "确认 FBs/B1 CPU、以太网模块型号、固件、TCP 端口、站号和 WinProladder 权限；不要把旧客户端的十进制站号/错误校验当作证据。",
          "用厂家工具只读抓取 40 状态、44 位读取和 46 字读取各一帧，记录 STX/ETX、校验、返回码和设备身份。",
        ],
        pcSteps: [
          "填写主机、TCP 端口和站号后显式点击连接；连接成功后仅执行 44/46 只读，不自动重连或写入。",
          "保存 requestHex、站号、命令、校验和响应原文；真实连接前先用独立对端/厂家工具逐字节比对。",
        ],
        checks: [
          "站号、命令、STX/ETX、校验码和响应状态必须严格校验。",
          "位数量最多 255，字数量最多 64，地址范围和 X/Y/M/S/T/C、R/D/RT/RC 类型必须匹配。",
          "软件黄金帧和独立 TCP 对端只代表 S2-S4a；真实 FBs 型号、模块、Gateway、权限和 L2 仍未完成。",
        ],
        warnings: [
          "只读 TCP 会话不代表真实 FBs/Gateway L2 通过；不实现 RUN/STOP、程序下载、自动重连、连接池或 PLC 控制。",
          "写帧只用于离线格式检查；现场写入必须另立安全门禁、原值记录和恢复步骤。",
        ],
      }),
    },
  },
  fuji: {
    label: "Fuji PLC",
    variants: {
      sph: networkGuide({
        label: "MICREX-SX SPH Loader Command（TCP 只读 + 编解码）",
        summary: "Fuji MICREX-SX SPH Loader Command 使用 20 字节二进制头、默认 TCP 18245、00H 读和 01H 写；用户显式连接后可执行同一 TCP 长连接上的只读字读取，写入仍仅离线构帧。",
        parameters: [
          ["传输", "TCP，默认 18245", "具体以 SPH CPU/以太网模块手册和工程参数为准"],
          ["连接 ID", "常见 FEH", "请求/响应必须一致；现场 CPU/模块配置优先"],
          ["存储区", "M1/M3/M10、I、Q", "类型码 M1=02H、M3=04H、M10=08H、I/Q=01H"],
          ["地址", "24 位小端字地址", "可选 .0..15 位后缀；首轮字构帧不接受位后缀"],
          ["单帧上限", "1..230 字", "更长读写需在真实会话层按响应回显分帧"],
        ],
        deviceSteps: [
          "确认 MICREX-SX SPH CPU、固件、以太网模块、Loader Command 服务、TCP 端口和连接 ID；不要把旧 ASCII 客户端当作 SPH 证据。",
          "用 Fuji Loader 或另一独立实现只读抓取 M1/M3/M10、I/Q 各一帧，记录 20 字节头、命令、24 位地址、字数、错误码和设备身份。",
        ],
        pcSteps: [
          "填写主机、TCP 端口和连接 ID 后显式点击连接；连接成功后仅执行 00H 只读，不自动重连或写入。",
          "保存 requestHex、响应长度、连接 ID、类型码、地址、字数和响应原文；真实连接前先逐字段比对手册/抓包。",
        ],
        checks: [
          "响应必须严格匹配 FB/80/80/00/7B/11 固定头、连接 ID、命令、payload length、CPU 错误码和数据长度。",
          "地址必须是 M1/M3/M10/I/Q 且在 24 位窗口内；位后缀只保留为解析信息，首轮不构造位写入。",
          "软件黄金帧和独立 TCP 对端只代表 S2-S4a；真实 SPH 型号、固件、Loader 服务和 L2 仍未完成。",
        ],
        warnings: [
          "只读 TCP 会话不代表真实 CPU/L2 通过；不实现 CPU 状态推断、位读改写、自动重连、连接池或 PLC 控制。",
          "写帧只用于离线格式检查；现场写入必须另立安全门禁、原值记录和恢复步骤。",
        ],
      }),
    },
  },
  ge: {
    label: "GE PLC",
    variants: {
      srtp: networkGuide({
        label: "Series 90 / PACSystems SRTP（TCP 只读 + 编解码）",
        summary: "GE SRTP 使用 TCP 默认 18245；用户显式连接后先发送 56 个零字节建立会话，随后在同一长连接上执行只读事务。页面仍提供离线帧生成/解析，不推断 PLC 状态、不提供程序/时钟服务。",
        parameters: [
          ["传输", "TCP，默认 18245", "Series 90/VersaMax/PACSystems 的具体 CPU、固件和 Ethernet 配置优先"],
          ["会话", "56 字节全零初始化", "初始化响应必须是 56 字节、类型 01H、标识 0FH、无负载"],
          ["字区域", "%R、%AI、%AQ", "16 位字元素；用户地址从 1 开始，R1 编码偏移 0"],
          ["字节/位区域", "%I、%Q、%T、%M、%SA、%SB、%SC、%S、%G", "字节/位数据码不同，位访问必须显式勾选"],
          ["响应", "短 0xD4 / 长 0x94", "短响应最多 6 字节并含 PLC 状态；长响应长度必须等于期望数据长度"],
        ],
        deviceSteps: [
          "确认 Series 90-30/90-70、VersaMax 或 PACSystems 的具体 CPU、固件、以太网模块、SRTP 服务和 TCP 端口；记录工程版本与设备身份。",
          "先用厂家工具或独立实现只读抓取会话初始化、R100、一个 I/Q/M 位区和一个错误响应，保存 TX/RX、事务号、长度、响应形态和 PLC 状态码。",
        ],
        pcSteps: [
          "先解析 R/AI/AQ 或 I/Q/T/M/SA/SB/SC/S/G 地址，再生成会话、读/写黄金帧；TCP 只读连接必须由用户显式点击并先完成会话初始化。",
          "连接后使用 TCP 只读读取，保存连接 ID、请求帧、响应形态、事务号和 dataHex；断开使用同一连接 ID。",
          "位访问需显式选择 bitAccess，字区域按 16 位字校验数据长度；保存 frameHex、数据码、偏移和元素数量。",
          "将响应 HEX 粘贴到解析器，严格检查事务号、声明长度、短/长响应形态和 PLC 原始状态码。",
        ],
        checks: [
          "R0、M0 等零地址必须拒绝；用户地址 1..65536 编码为零基 16 位偏移。",
          "字区域只能按 16 位字访问；I/Q/T/M/SA/SB/SC/S/G 的字节和位数据码不能混用。",
          "软件黄金帧和独立 TCP 对端测试代表软件 S2-S4a；具体 GE 系列、CPU、固件、最大引用表和真实长连接 L2 仍未完成。",
        ],
        warnings: [
          "当前 TCP 会话只读且不自动重连；不恢复旧自制 8 字节包络，不实现 PLC 状态推断、程序名、时钟、订阅或虚拟服务器。",
          "写帧只用于离线格式检查；现场写入必须另立安全门禁、原值/回读/恢复记录和相邻位影响验证。",
        ],
      }),
    },
  },
  panasonic: {
    label: "Panasonic PLC",
    variants: {
      "mewtocol-com": serialGuide({
        label: "MEWTOCOL-COM（共享 COM 只读）",
        summary: "Panasonic FP 系列 MEWTOCOL-COM ASCII 路径：1..32 站号、RD/WD 数据区、RCS/WCS 单触点、标准/扩展头、XOR BCC 和 CR 终止。当前允许只复用主站页已打开的 COM 做 RD/RCS 读取；不会自动开关 COM、不会通过该按钮发送 WD/WCS，也不宣称具体 FP 型号已通过。",
        parameters: [
          ["串口", "典型 9600 8O1", "具体 FP 系列、编程口和适配器手册优先；首轮页面只做离线帧"],
          ["站号", "1..32，两位十进制", "写入 `%01#...` 或 `<01#...` 头；不能使用 Modbus 0 站"],
          ["数据区", "DT/D、LD、FL/F，0..99999", "RD/WD 按起止字地址；单帧首轮最多 500 字"],
          ["触点", "X/Y/R/L/T/C", "RCS/WCS 单点；X/Y/R/L 末位可用 0..F，T/C 为十进制"],
          ["校验", "XOR BCC + CR", "BCC 覆盖从 `%`/`<` 到正文末尾，不包含 BCC 和 CR"],
        ],
        deviceSteps: [
          "在 FP 系列工程/手册中确认 CPU 型号、MEWTOCOL-COM 是否启用、串口电气层、波特率/校验、站号和数据区权限。",
          "先用厂家工具只读核对一个 DT/LD/FL 字和一个 X/Y/R/L/T/C 触点；记录正常 `$` 响应、错误 `!` 响应和 BCC。",
          "确认目标设备支持 MEWTOCOL-COM；MC、Modbus、EtherNet/IP 或其他扩展服务另按独立协议验收。",
        ],
        pcSteps: [
          "先在本机接口页确认实际 COM、RS-232/485 转换器和串口格式，并在主站页先打开共享 COM；本页不会自动打开或关闭端口。",
          "先使用本页的 COM 只读 RD/RCS，保存 TX/RX、响应 BCC、设备型号/序列号和串口参数记录；再生成 RD/WD/RCS/WCS 报文与手册/抓包逐字段比对。",
        ],
        checks: [
          "站号、数据地址、单帧字数、标准/扩展长度和 BCC 必须通过软件边界校验。",
          "正常响应必须匹配头、站号、期望命令、BCC 和 CR；错误响应保留原始两位十六进制错误码。",
          "写入向量只代表编解码能力；真实 FP PLC 首次验收仍只读，写入需原值记录、回读和恢复。",
          "软件 S2/S3 通过不等于具体 FP 型号、串口电气层或现场 L2 通过。",
        ],
        warnings: [
          "当前只实现共享 COM 的只读 RD/RCS 会话、回显剥离、有限超时重试和人工设备记录；不实现写入、批量订阅、FP 程序服务、TCP 封装或虚拟 Panasonic PLC。",
          "不要把 MEWTOCOL-COM 的 D/X/Y 地址映射为 Modbus 或三菱 MC 地址，也不要把同源 DLL 视为现场互操作证据。",
        ],
      }),
    },
  },
  mqtt: {
    label: "MQTT 工业上行",
    variants: {
      "mqtt-311": networkGuide({
        label: "MQTT 3.1.1（TCP 只读订阅 + 编解码）",
        summary: "MQTT 3.1.1 TCP 只读订阅已完成 Aedes/MQTT.js 独立实现互操作：CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH 解码、PINGREQ/PINGRESP 和 DISCONNECT。页面不发布任何值或命令，不实现 Sparkplug B、TLS、用户名密码或 QoS 1/2 确认。",
        parameters: [
          ["Broker IP/主机", "实际 MQTT Broker 地址", "127.0.0.1 只用于本机独立对端；IP 可达不等于鉴权成功"],
          ["TCP 端口", "1883", "TLS 常见 8883 另立安全边界，不能把明文 1883 当作安全通道"],
          ["Client ID", "唯一 UTF-8 字符串", "首轮默认 clean session；Broker 可能要求唯一且有长度限制"],
          ["Topic Filter", "例如 factory/line1/#", "首轮只订阅，不允许发布；通配符语义由 Broker 按 MQTT 规范解释"],
          ["Keep Alive", "例如 30 秒", "仅用于 PING 诊断；断线重连和离线队列另立门禁"],
        ],
        deviceSteps: [
          "在 Mosquitto、EMQX 或现场 Broker 中确认 MQTT 3.1.1 监听端口、匿名/用户名策略、ACL 和 Topic 只读权限。",
          "为 Nexus 创建只读 Client ID 与 Topic ACL；先用独立 mqtt 客户端发布测试状态，再由 Nexus 订阅回读。",
          "若使用 TLS、用户名密码或 Sparkplug B，必须单独记录证书、身份、命令/状态 Topic 和长稳证据。",
        ],
        pcSteps: [
          "先在本机接口页确认实际网卡、代理/TUN 分流和 TCP 1883 可达；不要把 Ping 或端口连通当成 Broker 鉴权通过。",
          "在本页先连接，再订阅一个明确的只读状态 Topic；收到 PUBLISH 后保存 Topic、QoS、retain、payloadHex 和原始帧。",
          "使用 PING 只验证 keep-alive 往返；断开由 MQTT DISCONNECT 完成，不向 Broker 发送 PUBLISH。",
        ],
        checks: [
          "CONNECT 必须是 MQTT 3.1.1、固定头 flags=0、Client ID 和剩余长度一致；CONNACK return code 必须为 0。",
          "SUBSCRIBE 固定 flags=0x2，packetId 非零，SUBACK packetId/授权 QoS 必须匹配；首轮 live path 只接受 QoS 0 PUBLISH。",
          "PUBLISH Topic UTF-8、QoS/packetId、payload 边界和 remaining length 必须严格解析；不自动 PUBACK/PUBREC。",
          "软件 TCP 对端与 Aedes/MQTT.js 互操作代表 MQTT S2-S4b；生产 Mosquitto/EMQX、正向认证、TLS、复杂 ACL、Topic 语义和 24/72 小时 L2 仍需独立记录。",
        ],
        warnings: [
          "当前不实现 PUBLISH、命令订阅、QoS 1/2 确认、TLS、用户名密码、遗嘱、离线队列、Sparkplug B、桥接或自动重连。",
          "不要因为收到状态 Topic 就把 MQTT 当成 PLC 原生协议；Topic 数据的来源、单位、时间戳和写入权限必须由上层工程确认。",
        ],
      }),
    },
  },
  iec: {
    label: "IEC 60870-5-104 电力规约",
    variants: {
      "iec-60870-5-104": networkGuide({
        label: "IEC 60870-5-104（TCP 只读主站 + 总召）",
        summary: "Nexus 作为只读 Client/Master 连接 TCP 2404 站端，完成 STARTDT、站/组总召、首批遥信遥测 ASDU 解码、逐 I 帧确认、TESTFR 和 STOPDT。当前不提供遥控、设点、校时、文件传输或站端模式。",
        parameters: [
          ["RTU/IED IP", "站端实际 IPv4", "127.0.0.1 只用于独立脚本对端；生产站端需单独 L2"],
          ["TCP 端口", "2404", "IEC 104 常用明文端口；TLS/IEC 62351 不是本轮能力"],
          ["公共地址 CA", "常见 1", "本轮为 2 字节 little-endian；必须与站端工程一致"],
          ["发起方地址 OA", "默认 0", "一字节；站端有明确要求时再修改"],
          ["总召组", "0=站总召，1..16=组总召", "QOI 20=站总召，QOI 21..36=组 1..16"],
          ["首批监视类型", "M_SP / M_DP / M_ME_NA / M_ME_NC / M_IT", "无时标首批类型；CP24/CP56 带时标类型后续实现"],
        ],
        deviceSteps: [
          "从站端配置或点表记录 IEC104 Server 是否启用、IP/端口、公共地址、OA 策略、总召组和每个 IOA 的类型/单位/倍率。",
          "在隔离测试环境确认站端允许 Client/Master 连接并会回复 STARTDT_CON、C_IC_NA_1 ACT_CON、监视数据和 ACT_TERM。",
          "现场首次验收只启用监视方向；遥控、设点、校时及其他反向命令必须保持关闭并另走安全评审。",
        ],
        pcSteps: [
          "先核对电脑调试网卡、站端路由和 TCP 2404；端口可达仅说明网络层，不等于 IEC104 链路与 ASDU 正确。",
          "连接后先执行 TESTFR，再执行站总召；保存 STARTDT、总召请求、ACT_CON、全部监视 ASDU、S 确认和 ACT_TERM 原始帧。",
          "将 CA/IOA/typeId/COT/Quality/值逐项与站端点表和独立客户端比对；Invalid、Blocked、Substituted、NotTopical、Overflow 不能被忽略。",
        ],
        checks: [
          "APDU 必须以 0x68 开始，长度为 4..253；I/S/U 控制域和 15 位 N(S)/N(R) 必须严格连续。",
          "总召必须按 ACT_CON → 监视 ASDUs → ACT_TERM 完成；CA、IOA=0、QOI 和否定确认标志必须匹配请求。",
          "每个收到的 I 帧都要推进 N(R) 并发送 S 确认；错序、未确认、未知类型、截断或尾随字节必须失败。",
          "当前脚本 Outstation 互操作属于软件 S4a；生产 RTU/IED、冗余链路、长稳、带时标数据、TLS 和一致性测试仍为 L2 pending。",
        ],
        warnings: [
          "当前没有 C_SC/C_DC/C_SE 遥控或设点入口，也没有 C_CS 校时入口；不要通过手工 I 帧把写方向命令绕过 UI 安全边界。",
          "页面显示值不等于点表语义已验收；短浮点/归一化/累计量的工程单位、倍率、死区和时标必须由站端点表确认。",
        ],
      }),
    },
  },
  dnp: {
    label: "DNP3 电力规约",
    variants: {
      "dnp3-tcp": networkGuide({
        label: "DNP3（TCP 只读 Master + Class 扫描）",
        summary: "Nexus 作为只读 Master 连接 TCP 20000 Outstation，支持严格链路 CRC、传输分段、完整性轮询、Class 0/1/2/3、静态/事件测点、IIN、Quality 和应用确认。控制、校时、串口、Secure Authentication 与 Outstation 模式均未开放。",
        parameters: [
          ["Outstation IP", "RTU/IED 实际 IPv4", "127.0.0.1 只用于独立脚本对端；真实站端必须单独 L2"],
          ["TCP 端口", "20000", "常见 DNP3/TCP 端口；TLS/证书和专网策略不在首轮范围"],
          ["Master Link Address", "默认 1", "本轮点对点地址必须小于 FFF0H，且不能与 Outstation 地址相同"],
          ["Outstation Link Address", "默认 1024", "必须与 RTU/IED 工程配置一致；不能用保留/广播地址代替"],
          ["扫描范围", "完整性或 Class 0/1/2/3", "完整性轮询发送 g60v1/v2/v3/v4；事件类可单独读取"],
          ["首批对象", "g1/g2/g3/g4/g10/g11/g20-g23/g30/g32/g40/g42", "覆盖静态值、变化事件、Flags 和部分绝对/相对时间变化；并非完整 DNP3 variation 集"],
        ],
        deviceSteps: [
          "记录 Outstation 型号、固件、TCP 监听端口、Link Address、Class 分配、默认静态/事件 variation 和 unsolicited 策略。",
          "在隔离环境先允许只读 READ 与应用 CONFIRM；Select、Operate、Direct Operate、Restart、Freeze、Time Write、File 和认证写流程全部保持关闭。",
          "准备相同点表的独立 DNP3 Master，对 Class 0 静态值、Class 1/2/3 事件、IIN 和时间戳逐点比对。",
        ],
        pcSteps: [
          "先确认电脑调试网卡、路由和 TCP 20000；端口可达只说明网络层，不能证明链路地址、CRC、Class 和对象 variation 正确。",
          "连接后先执行完整性轮询，再分别执行 Class 1/2/3；保存每个链路帧、CRC、transport/application sequence、IIN 和应用 CONFIRM。",
          "把 group/variation/index/value/flags/time 与 Outstation 点表和独立 Master 比对；Quality 不良点不能被当成普通有效值。",
        ],
        checks: [
          "数据链路必须以 05 64 开始，length 语义、little-endian Link Address、头 CRC 和每 16 user bytes 的 CRC 全部通过。",
          "Transport FIR/FIN 与 6 位序号、Application FIR/FIN/CON/UNS 与 4 位序号必须连续；错序或地址不符立即断开会话。",
          "完整性请求固定 READ g60v1/v2/v3/v4；IIN 的 function-not-supported、object-unknown、parameter-error 必须作为事务失败。",
          "当前只通过独立脚本 Outstation；生产第三方栈、真实 RTU/IED、长稳、time sync、serial 和 Secure Authentication 仍为 L2 pending。",
        ],
        warnings: [
          "当前没有 Select/Operate/Direct Operate、Analog Output、Time Write、Restart 或 Freeze 命令；不要通过新增手工应用 PDU 绕过只读边界。",
          "维护中的 Step Function Rust DNP3 使用非商业/非生产许可证，本项目未引入；当前 MIT 自有实现仍需第三方栈互操作和一致性测试后才能提升生产成熟度。",
        ],
      }),
    },
  },
  dlt: {
    label: "DL/T 645 电能表规约",
    variants: {
      "dlt645-2007": serialGuide({
        label: "DL/T 645-2007（共享 COM 只读，默认）",
        summary: "Nexus 复用主站页已打开的 RS-485 COM 口，以 DL/T 645-2007 主站身份读取电能量、日期/时间和运行状态字。实现严格 12 位 BCD 表地址、4 字节数据标识、低字节先传、+33H 数据变换、算术和与读响应回显校验；本轮不开放任何写入或控制命令。",
        parameters: [
          ["版本", "DL/T 645-2007", "当前默认；不得与 1997 的 2 字节 DI 和控制码混用"],
          ["COM / 接口", "Windows 实际 COM / RS-485", "复用主站页已打开串口；页面本身不自动开关 COM"],
          ["串口格式", "默认 2400 8E1", "设备若改过速率，以现场表计参数为准；无流控"],
          ["表地址", "12 位十进制 BCD", "线上按低位字节先传；999999999999 广播地址禁止用于本轮读取"],
          ["数据标识 DI", "8 位 HEX / 4 字节", "如 00010000=当前正向有功总电能；线上低字节先传并逐字节 +33H"],
          ["前导字节", "0..4 个 FE，默认 4", "用于唤醒表计；校验和从第一个 68H 开始，不包含 FE、CS、16H"],
        ],
        deviceSteps: [
          "从表计铭牌、资产台账或厂家软件记录完整 12 位通信地址、型号、序列号、当前波特率/校验和支持的 2007 数据标识表。",
          "在隔离的 RS-485 总线上确认 A/B 极性、公共地、总线偏置与终端；一次只接入已确认地址的表计，避免把广播或缩位地址当成普通读地址。",
          "首轮只读取 00000000/00010000/00020000、04000101、04000102 或 04000501..07；校时、改地址、密码、冻结、费率和拉合闸全部保持关闭。",
        ],
        pcSteps: [
          "先在主站页按表计实际参数打开 COM；本页面只复用该句柄，不会替用户修改或重开串口。",
          "选择 2007，填写完整表地址和 8 位 DI，先点“生成读请求”核对 FE、68、地址、11、L、+33H、CS、16，再执行“共享 COM 只读”。",
          "保存 TX/RX、型号/序列号记录和解码结果，与表计液晶读数、厂家软件及点表单位逐项比对。",
        ],
        checks: [
          "帧必须满足 68+A0..A5+68+C+L+DATA+CS+16，响应前只允许 0..4 个 FE，L 最大 200，不能用搜索首个 16H 的方式截帧。",
          "CS 必须是从第一个 68H 到数据域末字节的模 256 算术和；接收数据逐字节 -33H 后再解释。",
          "普通读取使用 11H，请求响应只接受 91H/B1H/D1H；响应方向、异常位、表地址和回显 DI 必须全部匹配。",
          "已知 BCD 数据严格拒绝 A..F 半字节；日期、星期、时分秒和数据长度必须各自在合法范围。",
          "当前独立脚本电表应答器与共享 COM 注入测试属于软件 S4a；真实表计、转换器、总线长稳与厂家互操作仍为 L2 pending。",
        ],
        warnings: [
          "当前没有写数据、广播校时、改通信地址、密码/操作员、冻结、费率编程、拉闸或合闸入口，也不允许手工把控制帧送到共享 COM。",
          "B1H 后续帧标志当前会明确失败，不返回不完整结果；多帧续读需要单独实现和验证。",
          "00010000 等标准数据标识的倍率可直接解码，但厂家扩展 DI、组合数据块、单位和小数位必须以该型号手册为准。",
        ],
      }),
      "dlt645-1997": serialGuide({
        label: "DL/T 645-1997（共享 COM 只读，旧版）",
        summary: "为存量旧表保留 DL/T 645-1997 只读边界。它仍使用 12 位 BCD 地址、低字节先传、+33H 和相同物理帧，但数据标识为 2 字节，读请求/普通响应/后续响应/异常响应分别为 01H/81H/A1H/C1H；不会借用 2007 的 DI 或控制码。",
        parameters: [
          ["版本", "DL/T 645-1997", "已被 2007 全部代替，仅在设备铭牌或手册明确要求时选用"],
          ["COM / 接口", "Windows 实际 COM / RS-485", "复用主站页已打开串口；电气层和转换器必须另行确认"],
          ["串口格式", "以旧表实际设置为准", "页面会提示与 2400 8E1 的偏差，但不能据此替代旧表手册"],
          ["表地址", "12 位十进制 BCD", "线上按低位字节先传；本轮读取禁止广播地址"],
          ["数据标识 DI", "4 位 HEX / 2 字节", "例如 9010 为当前正向有功总电能；低字节先传并逐字节 +33H"],
          ["控制码", "01 / 81 / A1 / C1", "D7 是传送方向，D6 才是异常标志；不能把所有响应误判为错误"],
        ],
        deviceSteps: [
          "确认设备确实只支持 1997，而不是把 2007 表误配成旧版；记录型号、固件、12 位地址、串口格式和该型号的 1997 DI 表。",
          "用厂家软件或独立表计工具先验证一个只读数据项；保存其 TX/RX 作为该型号基线，不从未经测试的旧代码猜测地址或倍率。",
          "现场首轮只读 9010 等已确认项目；写数据、校时、改地址、最大需量复位、广播和控制类命令均保持关闭。",
        ],
        pcSteps: [
          "在主站页打开与旧表完全一致的 COM 参数，再进入本页选择 1997；不要只改页面版本而继续使用 2007 的 8 位 DI。",
          "先生成 01H 请求并离线核对两字节 DI 的反序和 +33H；确认后再执行共享 COM 只读。",
          "把 81H 响应的解码值与液晶和独立工具比对；收到 A1H 后续帧时当前会停止并提示尚未支持。",
        ],
        checks: [
          "帧结构和 CS 范围与 2007 相同，校验和必须包含前后两个 68H，但不包含 FE、CS 和 16H。",
          "1997 DI 必须恰好 2 字节；普通、后续、异常读响应只接受 81H、A1H、C1H，不能接受 91H/B1H/D1H。",
          "响应地址与回显 DI 必须与请求完全一致；D7=1 仅说明从站向主站响应，D6=1 才进入异常字解析。",
          "9010 的 4 字节 BCD 电能量按低位先传并严格校验；其他 DI 暂只返回原始数据，不擅自推断单位或倍率。",
          "软件测试不代表旧表兼容性；真实 1997 表计、不同速率、RS-485 转换器和长稳仍为 L2 pending。",
        ],
        warnings: [
          "DL/T 645-1997 已被 2007 全部代替；新设备默认选 2007，只有明确旧表证据时才选 1997。",
          "当前没有任何 1997 写入或广播入口；不要把离线构造能力扩展成发送任意帧。",
          "旧项目代码曾出现十进制地址转字节、漏算 68H、遗漏 +33H 和误判 D7/D6 等问题，本实现不继承这些行为。",
        ],
      }),
    },
  },
  cjt: {
    label: "CJ/T 188 水气热表规约",
    variants: {
      "cjt188-2004": serialGuide({
        label: "CJ/T 188-2004（离线只读编解码）",
        summary: "Nexus 锁定 CJ/T 188-2004 主流帧型：FE(0..4) + 68H + T + A0..A6 + C + L + DATA + CS + 16H；CS 为从 68H 到数据域末字节的模 256 算术和，数据域不做 +33H 变换，读数据 DATA 为 DI0 DI1 + SER。当前页面只提供离线构帧/解帧，读取 901F 累计流量向量，尚不打开 COM、不做真实表计会话。",
        parameters: [
          ["表类型 T", "10H 冷水 / 11H 热水 / 20H 热量 / 30H 燃气", "0x40 电表不属于 CJ/T 188 首批确认范围；电表请使用 DL/T 645"],
          ["表地址", "14 位 BCD，低位对先传", "线上 A0 在最前；AAAAAAAAAAAAAA 为广播地址，仅点对点读地址场景使用，本轮读数据禁止"],
          ["数据标识 DI", "2 字节，高位在前", "例如 901F；线上按 DI0 DI1 原序传输，后接 1 字节 SER 序列号"],
          ["序列号 SER", "主站选择，响应回显", "首批示例使用 01H；响应 SER 不一致会拒绝"],
          ["控制码", "读数据 01H / 正常响应 81H / 异常响应 C1H", "A1H 后续帧未在本轮确认，会明确拒绝"],
          ["校验和 CS", "模 256 算术和", "范围从 68H 到 DATA 末字节；不含 FE 前导、CS 和 16H"],
        ],
        deviceSteps: [
          "记录水表/热量表/燃气表的厂家、型号、固件、表类型代码、14 位表地址、串口参数和厂家 DI 表；不要用电表资料套用 CJ/T 188。",
          "在隔离 RS-485 总线上确认 A/B 极性、公共地和终端电阻；一次只接一块已确认地址的表，广播 AA×7 只用于点对点读地址且本轮未开放。",
          "用厂家抄表软件抓取一次只读 901F 的 TX/RX 作为该型号基线；阀门控制、写参数、改地址和后续帧读取全部保持关闭。",
        ],
        pcSteps: [
          "当前页面不会打开或复用 COM；先在下方的构帧/解帧卡片离线核对表类型、地址反序、DI/SER、CS 和 16H。",
          "把 901F 的 4 字节 BCD 流量和 2 字节状态与厂家软件及表显读数比对；热量表 901F 保持原始数据，不猜测单位和倍率。",
          "真实 COM 会话、唤醒前导时序、后续帧和厂家异常码留待下一轮 L2 门禁，软件向量通过不代表现场通过。",
        ],
        checks: [
          "帧必须是单 68H 结构；68 T A0..A6 C L DATA CS 16，共 13+L 字节。第二个 68H、XOR 校验或 +33H 变换都属于旧 Nexus.Cjt 偏差，会被拒绝。",
          "CS 必须是模 256 算术和；示例 68 10 77 66 55 44 33 22 11 01 03 90 1F 01 08 16 中 CS=08H 可手算复核。",
          "901F 水表/燃气表响应 DATA 应为 DI(2)+SER(1)+流量 BCD(4)+S0+S1，流量按低位先传解成 XXXXXX.XX m³；S1 标准建议 FFH，当前仅报告不强制。",
          "S0 的 bit0/bit1/bit2 分别标记阀门关、阀门异常、电池欠压；bit3..bit7 为厂家自定义，只报告位号不猜语义。",
          "当前属于软件 S1-S3 离线编解码；真实水/气/热表、RS-485 电气层、唤醒时序、长稳和厂家互操作均为 L2 pending。",
        ],
        warnings: [
          "当前没有写数据、写地址、阀门控制、校时、清零或后续帧命令，也没有共享 COM 发送入口；不要把离线构帧能力扩展成发送任意帧。",
          "旧 Nexus.Cjt 使用双 68H、XOR 校验和 +33H 加密，与主流 CJ/T 188-2004 证据不符，本实现不继承；若现场表计要求该变体，需另行抓包立项。",
          "热量表 901F 的工程量格式、单位和厂家扩展 DI 未确认；C1H 异常响应的载荷保持原始字节，不擅自翻译错误码。",
        ],
      }),
    },
  },
  bacnet: {
    label: "BACnet/IP 楼宇自控",
    variants: {
      "bacnet-ip": networkGuide({
        label: "BACnet/IP（定向 Who-Is/I-Am + ReadProperty 只读会话）",
        summary: "Nexus 当前锁定 BACnet/IPv4 本地编解码和显式 UDP 对端只读会话：BVLC 81H + 0AH/0BH；在线 Who-Is 固定 0AH 定向单播并接收 I-Am；ReadProperty 请求使用 DER=1 的本地 NPDU、Confirmed 0CH，并核对 30H ComplexACK 回显。不做广播发现、RPM、写入、COV、BBMD/FDR 或 MS/TP。",
        parameters: [
          ["UDP 端口", "常用 47808（0xBAC0）", "页面连接一个显式对端；不绑定/发送 0BH 广播，也不注册 BBMD/FDR"],
          ["BVLC Function", "0AH Original-Unicast / 0BH Original-Broadcast", "长度字段包含 BVLC 4 字节；旧 Nexus.Bacnet 的 00H 已判定错误"],
          ["NPDU", "Who-Is/I-Am/ACK 为 01 00；ReadProperty 请求为 01 04", "DNET/SNET、路由和网络层消息不进入当前边界；DER 只在 ReadProperty 请求中出现"],
          ["Who-Is 范围", "无范围或 low/high 成对出现", "Device Instance 0..4194303；low≤high，不能只填一半"],
          ["I-Am 字段", "Device Object、Max-APDU、Segmentation、Vendor ID", "Object ID 必须是 tag C4；Segmentation 0..3 分别为 both/transmit/receive/none"],
          ["APDU", "10 08=Who-Is，10 00=I-Am，00..0C=ReadProperty 请求，30..0C=ComplexACK", "错误、拒绝、中止、分段、RPM 和 COV 当前明确拒绝"],
          ["ReadProperty 参数", "context [0] Object ID、[1] Property ID、可选 [2] Array Index", "对象类型 0..1023、实例/属性/索引 0..4194303；ACK 值必须由 [3] 3EH/3FH 包裹"],
        ],
        deviceSteps: [
          "记录楼宇控制器/BACnet 路由器的型号、固件、UDP 端口、Device Instance、Max-APDU、Segmentation、Vendor ID、BBMD/FDR 拓扑和 VLAN/组播策略。",
          "在隔离楼宇网络或实验室先抓取第三方工具的一次 Who-Is/I-Am；确认 0AH/0BH、长度、标签和 I-Am 四字段，不把旧 Nexus.Bacnet 报文当作基线。",
          "现场计划只允许显式对端 Who-Is 发现和 ReadProperty 读取；RPM 需另立证据，WriteProperty、COV 写订阅、时间同步、重启动和设备配置全部另立安全门禁。",
        ],
        pcSteps: [
          "先在“本机接口”页确认实际调试网卡和 VLAN；连接后仅对填写的对端发送 0AH Who-Is 或 ReadProperty，不发送广播。",
          "先用离线构帧核对 C4/22/91/21 和 context 0/1/2/3 标签，再对独立脚本对端执行 Who-Is/ReadProperty，保存 TX/RX 与解码结果。",
          "把控制器回复 I-Am 的源 IP:端口、Device Instance、Max-APDU 和 Segmentation 记录为后续实机验收证据；独立脚本对端通过不是 L2 PASS。",
        ],
        checks: [
          "全局 Who-Is 黄金帧必须是 81 0B 00 08 01 00 10 08；范围 1000..2000 必须是 81 0B 00 0E 01 00 10 08 0A 03 E8 1A 07 D0。",
          "I-Am 示例必须是 81 0A/0B 00 14 01 00 10 00 C4 02 00 03 E9 22 01 E0 91 03 21 2A；C4 是 Object Identifier 应用标签，22 是 Unsigned，91 是 Enumerated。",
          "ReadProperty AI1001/Present Value 请求必须是 81 0A 00 11 01 04 00 03 01 0C 0C 00 00 03 E9 19 55；ComplexACK 必须核对 Invoke ID、对象、属性、可选索引和 [3] 值包装。",
          "BVLC 长度必须等于 UDP 载荷长度；NPDU 控制位含 DNET/SNET/网络消息/DER、服务为非 Who-Is/I-Am、尾随数据和非法标签必须失败。",
          "旧 Nexus.Bacnet 的 BVLC 00H、上下文标签 class 位缺失、LVT 少 1、I-Am 缺 Object Identifier 标签均已判定不继承。",
          "在线 Who-Is 只发送 0AH 定向单播并按超时收集 I-Am；ReadProperty ACK 的 Invoke ID、对象、属性和可选数组索引必须与请求一致，否则断开会话。",
          "当前属于软件 S1-S4b：离线编解码、独立脚本 UDP 对端，且 bacstack 0.0.1-beta.14 独立栈已完成 Who-Is/I-Am 与 ReadProperty/ComplexACK 互操作 1/1。真实控制器、BBMD/FDT、分段、COV 和长稳均为 L2 pending。ReadProperty 只解释 Unsigned/Real，未知应用标签保留原始字节。",
        ],
        warnings: [
          "当前没有 bacnet_ip_read_property_multiple、bacnet_ip_write_property、bacnet_ip_subscribe_cov 或 BBMD/FDR 命令；在线路径仅限 0AH Who-Is 和 ReadProperty，不要把它扩展成发送任意 UDP。",
          "BACnet 写入可能改变楼宇设备设定、启停或报警策略；WriteProperty 必须先完成独立栈和真实/BTL 设备互操作并另立确认流程。",
          "跨子网 Who-Is/I-Am 依赖 BBMD/FDT 拓扑；当前不实现 Forwarded-NPDU、Register-Foreign-Device 或 Distribute-Broadcast-To-Network。",
        ],
      }),
    },
  },
  knx: {
    label: "KNXnet/IP 楼宇自控",
    variants: {
      "tunneling-v1": networkGuide({
        label: "KNXnet/IP Tunneling v1（离线只读编解码）",
        summary: "Nexus 当前锁定非安全 KNXnet/IP Tunneling v1 的离线编解码和显式 UDP 网关只读会话：公共头 06 10、Connect Request/Response、三层组地址、GroupValueRead 的 L_Data.req、Tunneling ACK、GroupValueResponse 的 L_Data.ind + 回 ACK、Connection State 手动/周期保活和 Disconnect；开发测试已用 knx 2.5.4 独立编解码器完成互通。页面不做 Routing、Discovery、Device Management、KNX IP Secure、写组值或场景控制。",
        parameters: [
          ["UDP 端口", "默认 3671", "页面连接一个显式网关；不使用 Routing 多播或设备发现广播"],
          ["公共头", "06 10 + service + total length", "旧 Nexus.Knx 的 10 00 头已判定错误；总长度必须等于 UDP 载荷长度"],
          ["Connect Request", "两段 UDP/IPv4 HPAI + CRI 04 04 02 00", "HPAI 为 08 01 + IPv4 + port；CRI 表示 Tunneling、link layer"],
          ["Tunneling 连接头", "structure-length 04 + channel + sequence + reserved 00", "请求/ACK 必须核对 channel 和 sequence；reserved 非 00 拒绝"],
          ["Connection State", "Request/Response + HPAI", "status=00 表示隧道仍有效；连续超时或非零状态会释放本地会话，需显式重连"],
          ["周期保活", "可启动/停止的 Connection State 定时探测", "失败后只释放本地会话，不自动改写连接状态；恢复必须显式重新 Connect"],
          ["组地址", "三层 main/middle/sub", "main 0..31、middle 0..7、sub 0..255，例如 1/2/3 线值为 0A03"],
          ["cEMI Group Read", "11H L_Data.req + BC E0 + destination + APDU 00 00", "仅生成 GroupValueRead；不生成 GroupValueWrite 或场景命令"],
          ["GroupValueResponse", "29H L_Data.ind + APCI Response", "短值取低 6 位，扩展值保留原始字节并按 APDU 长度校验；收到后建议回 ACK"],
        ],
        deviceSteps: [
          "记录 KNXnet/IP Interface/Router 的型号、固件、IP、UDP 3671、隧道通道容量、个体地址、组地址表和 DPT；区分 Tunneling 与 Routing/Secure。",
          "在隔离楼宇网络先用 ETS 或独立工具抓取一次 Connect、GroupValueRead、Tunneling ACK 和 GroupValueResponse，保存为该网关基线。",
          "现场首轮只允许读取组值；写组值、场景控制、设备管理、重启和地址编程全部另立安全评审。",
        ],
        pcSteps: [
          "先在“本机接口”页确认调试网卡和 VLAN；连接路径只面向显式网关，端口可达不代表 Connect/Sequence 语义正确。",
          "离线生成 Connect Request 和 1/2/3 GroupValueRead，逐项核对 06 10、HPAI、CRI、channel、sequence、cEMI 控制字段和 APDU。",
          "解析独立工具抓到的 GroupValueResponse，确认源/目的组地址、APCI、payload 和建议 ACK；软件向量不能替代真实网关验收。",
        ],
        checks: [
          "Connect Request 黄金帧使用 06 10 02 05 00 1A + 两段相同 HPAI + 04 04 02 00。",
          "GroupValueRead 示例必须是 06 10 04 20 00 15 04 15 00 00 11 00 BC E0 00 00 0A 03 01 00 00。",
          "Tunneling ACK 必须是 06 10 04 21 00 0A 04 channel sequence status；GroupValueResponse 后建议按 channel/sequence 回 00 状态 ACK。",
          "公共头长度、Tunneling structure-length、reserved 字节、cEMI 附加信息长度、APDU 长度、组地址范围和尾随数据必须 fail-closed。",
          "在线读取流程固定为 GroupValueRead → 网关 ACK → GroupValueResponse → 客户端 ACK；响应 Channel、Sequence、组地址不一致会断开本地会话。",
          "Connection State 失败会断开本地会话；重新执行 Connect 后使用新的 Channel/Sequence，不沿用旧隧道状态。独立网关已覆盖状态错误→释放→显式重连→状态成功。",
          "独立网关已覆盖周期保活丢包→本地会话释放→显式重连获得新 Channel，且没有自动重连副作用。",
          "当前属于软件审计 + 离线编解码 + 独立脚本 UDP 网关 + knx 2.5.4 独立栈互通 S1-S4b；真实 Interface/Router、Secure、Routing 和长稳均为 L2 pending。",
        ],
        warnings: [
          "当前没有 knx_group_write、knx_write_group_value、knx_group_write_live、knx_auto_reconnect 或场景命令；在线路径仅限 Connect、Connection State/周期保活、GroupValueRead 和 Disconnect，不要扩展成发送任意 UDP。",
          "KNX 写组值可能改变灯光、遮阳、暖通或安防逻辑；写值、场景和设备管理必须单独确认并显式安全门禁。",
          "KNX IP Secure 与普通 Tunneling 不能混用；缺少项目密钥、keyring 和 DPT 时不得猜测工程语义。",
        ],
      }),
    },
  },
};

export function listProtocolGuideVariants(source) {
  const group = PROTOCOL_GUIDE_SOURCES[source];
  if (!group) return [];
  const registered = listProtocolVariants(source);
  const entries = registered.length
    ? registered.map((entry) => [entry.variant, group.variants[entry.variant]]).filter(([, guide]) => guide)
    : Object.entries(group.variants);
  return entries.map(([value, guide]) => ({ value, label: guide.label }));
}

export function resolveProtocolGuide(source, variant) {
  const group = PROTOCOL_GUIDE_SOURCES[source];
  if (!group) return null;
  const entries = Object.entries(group.variants);
  const selected = group.variants[variant] || entries[0]?.[1];
  if (!selected) return null;
  const selectedKey = group.variants[variant] ? variant : entries[0][0];
  return { source, sourceLabel: group.label, variant: selectedKey, ...selected };
}
