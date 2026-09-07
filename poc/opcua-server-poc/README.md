# OPC UA 复活 PoC（async-opcua 0.19）

验证 [docs/opcua-blocked.md](../../docs/opcua-blocked.md) 的复活条件 #1：
OPC UA 协议栈能否以纯 Rust 依赖在 Windows MSVC 上编译并完成最小自闭环。

## 选型

- crate：[async-opcua](https://crates.io/crates/async-opcua) 0.19.0（FreeOpcUa 社区维护 fork，2026-07-18 发布）。
- 加密栈 `async-opcua-crypto` 全部基于 RustCrypto（aes/cbc/rsa/sha2/x509-cert 等），
  **不含 openssl-sys**，也不需要 C 工具链——这改变了当年 opcua crate 被 blocked 的前提。
- 相比原计划 open62541 FFI：无 C 依赖、无 DLL 分发问题、与 rust-core 的链接器环境一致。

## PoC 覆盖

1. 内嵌 server：`opc.tcp://127.0.0.1:<动态端口>/`，SecurityPolicy::None + 匿名端点；
   地址空间挂 3 个变量（temp1/temp2 静态 f64，counter 每 300ms 推进的 i32）。
2. 同进程 client：匿名连接，直接 Read 三个节点验证静态值与 Good 状态码；
   间隔 700ms 二次读取 counter 验证动态值链路。
3. 判定：退出码 0 且 stdout 打印 `POC RESULT {...}`；任一步失败退出码 1。

不覆盖（留给后续正式接入）：加密端点（Sign/SignAndEncrypt）、订阅（MonitoredItems）、
浏览、与真实 PLC（如 S7-1500 的 OPC UA server）互操作。

## 运行

```powershell
cargo run --manifest-path poc/opcua-server-poc/Cargo.toml --release
```

工作目录使用系统临时目录（`%TEMP%\nexus-opcua-poc-<pid>`），pki 与 `server.conf`
不会落在仓库内。本 crate 独立于 rust-core，不受其三依赖纪律约束；`Cargo.lock`
随仓库提交以保证 PoC 可复现。
