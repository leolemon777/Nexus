# OPC UA 集成技术阻塞记录(2026-08-18)

## 问题
`cargo add opcua` 引入 `openssl-sys` 依赖,Windows 无原生 OpenSSL 库,构建失败。

## 尝试的替代方案
1. `rustls` feature — opcua 0.11 的 TLS 后端仍然链接 openssl-sys
2. 预编译 OpenSSL(vcpkg / chocolatey) — 引入外部构建依赖,便携包无法自带

## 决策
OPC UA 延期,列为独立任务。当前 S7-1500 符号寻址需求由 **Web API(JSON-RPC)** 覆盖(已实现)。

## 复活条件(任一满足即可)
- opcua crate 支持 pure-Rust TLS(无 openssl)
- 项目引入 vcpkg/OpenSSL 构建流水线
- 改用 FFI 绑定 open62541(C 库,Windows 预编译)

## 复活记录(2026-09-07)

**复活条件 #1 已满足:构建阻塞解除。** 原计划 open62541 FFI(条件 #3)不再需要。

本条**只**解除"依赖装不上"这一个阻塞。已证明的范围仅限于:async-opcua 0.19 在
Windows MSVC 上零 C 工具链完成编译链接,且匿名 + 未加密的单次 Read 能跑通闭环。
OPC UA 的真实复杂度(加密端点、订阅、浏览、真机互操作)一项都未验证 ——
排期正式接入时**不能**按"风险已消除"估算,PoC 的可复用产出是"依赖能编译",
而不是"我们知道怎么接"。

- 选型:[async-opcua](https://crates.io/crates/async-opcua) 0.19.0(FreeOpcUa 社区 fork,2026-07-18 发版)。
  其加密栈 `async-opcua-crypto` 全部基于 RustCrypto(aes/cbc/rsa/sha2/x509-cert 等),
  `cargo tree` 确认整棵依赖树 **0 个 openssl 系 crate**,Windows MSVC 编译链接零 C 工具链。
  该结论的作用域是 PoC 的 feature 组合(`default-features = false` + `server` + `client`);
  正式接入若改用默认 feature 集,需要重新跑一次 `cargo tree` 确认。
- PoC:`poc/opcua-server-poc/`(独立 crate,不触碰 rust-core 三依赖纪律)。
  内嵌 server(动态端口,SecurityPolicy::None + 匿名端点,3 变量点表)+
  同进程 client 匿名连接,直接 Read 验证静态值精确读回与动态计数器推进。
  两次运行均 `POC RESULT {...}` + 退出码 0,见 `poc/opcua-server-poc/README.md`。
- PoC 未覆盖(正式接入前仍需):加密端点(Sign/SignAndEncrypt)、订阅、浏览、
  真实 PLC 互操作(L2)。S7-1500 符号寻址当前仍由 Web API 覆盖。
- 正式接入属新功能批次,另行排期;本记录仅解除技术阻塞状态。

## 正式接入的硬约束(2026-09-09 补)

PoC 的代码形态**不是**正式接入的模板。以下几条是审查后写死的约束,
`poc/opcua-server-poc/src/main.rs` 里之所以还是现在这样,是因为它只连自己进程内的
server、不接触现场设备;换成真设备后每一条都会变成问题。

1. **不得继承 PoC 的连接重试循环。** PoC 里那段 40 次 × 250ms 的循环等的是同进程
   server 启动,和现场设备无关。全仓协议文档(fatek-ascii / fuji-sph / ge-srtp /
   keyence-kv-host-link / dlt645 等 golden-vectors 文档)都明确写了**不自动重连**,
   `PRODUCT_COMPLETION_ROADMAP.md` 也要求打开项目不得自动连接。正式接入必须沿用
   `rust-core/src/session.rs` 里 `Session::open_*` 的单次有界超时 + 返回 `CoreError`,
   不能把这个循环带过去,否则等于给现场 PLC 悄悄加了后台自动重连。
2. **不得沿用手写 YAML 配置。** PoC 用 `format!` 拼 server.conf 再落盘让库回读,
   已经因为 `ServerConfig.user_tokens` 无 serde 默认值踩过一次坑,路径里出现 `#`
   或 `: ` 还会把整行截断(现已改成 YAML 双引号标量止血)。正式接入用
   `ServerBuilder` 的 typed setter(`host` / `port` / `add_endpoint` / `with_config`),
   与 `rust-core/src/serial_config.rs` 那套"结构体 + `deny_unknown_fields` +
   `validate_and_normalize()`"的既有约定对齐。
3. **错误类型要换。** PoC 全程 `Result<_, String>`;正式接入走 JSONL/IPC 边界,
   必须产出 `rust-core/src/error.rs` 的 `CoreError`(带机器可读 code),
   与其余 50+ 协议一致。
4. **安全配置不可照抄。** PoC 是 `SecurityPolicy::None` + 匿名 + `trust_client_certs(true)`
   / `trust_server_certs(true)`,只监听 127.0.0.1。正式接入必须走加密端点与真实的
   证书信任判定。

## 待定:正式接入的拓扑(2026-09-09 补)

仓库里目前有两种互不一致的"OPC UA 支持"说法,排期前必须先定下来:

- 本文件开头(2026-08-18)的动机是**做 client**:连 S7-1500 拿符号寻址。
- `docs/gap-analysis.md` 的路线图条目要的是**做 server/网关**:把 Nexus 采到的数据
  重新发布给 SCADA。

PoC 在同一个进程里各做了一个玩具版,没有回答该走哪条。两条路线的落地形态差别很大
(前者是对外连接 + 厂商实现差异,后者是点表映射 + 会话/订阅管理),定不下来的话
PoC 代码本身几乎没有可复用的部分。
