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

**复活条件 #1 已满足,PoC 通过。** 原计划 open62541 FFI(条件 #3)不再需要。

- 选型:[async-opcua](https://crates.io/crates/async-opcua) 0.19.0(FreeOpcUa 社区 fork,2026-07-18 发版)。
  其加密栈 `async-opcua-crypto` 全部基于 RustCrypto(aes/cbc/rsa/sha2/x509-cert 等),
  `cargo tree` 确认整棵依赖树 **0 个 openssl 系 crate**,Windows MSVC 编译链接零 C 工具链。
- PoC:`poc/opcua-server-poc/`(独立 crate,不触碰 rust-core 三依赖纪律)。
  内嵌 server(动态端口,SecurityPolicy::None + 匿名端点,3 变量点表)+
  同进程 client 匿名连接,直接 Read 验证静态值精确读回与动态计数器推进。
  两次运行均 `POC RESULT {...}` + 退出码 0,见 `poc/opcua-server-poc/README.md`。
- PoC 未覆盖(正式接入前仍需):加密端点(Sign/SignAndEncrypt)、订阅、浏览、
  真实 PLC 互操作(L2)。S7-1500 符号寻址当前仍由 Web API 覆盖。
- 正式接入属新功能批次,另行排期;本记录仅解除技术阻塞状态。
