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
不会落在仓库内。**跑通了就整个删掉**（里面是自签私钥，没有留存价值）；**失败则保留现场**
并在 stderr 打出路径——生成的 `server.conf` 和 pki 往往就是排查起点，排查完请自行删除。
本 crate 独立于 rust-core，不受其三依赖纪律约束；`Cargo.lock` 随仓库提交以保证 PoC 可复现。

端口不再是"绑 0 拿号再释放、等 server 自己重绑"：现在 listener 绑好后直接交给
`Server::run_with()`，中间没有可被别的进程抢走的窗口。

## 门禁

本 crate 已挂进候选构建证据清单（`scripts/build-evidence.candidate.json` 的
`opcua-poc-selfcheck`），避免它在没人看的时候烂掉。门禁跑的是**完整自闭环**而不是
只编译——PoC 的判定标准本来就是退出码，只 build 挡不住行为层面的腐烂：

```powershell
npm run run:opcua-poc
```

只想确认能编译（不占端口）时用：

```powershell
npm run build:opcua-poc
```

格式检查是独立的一条，没有进门禁：

```powershell
npm run fmt:opcua-poc
```

工具链沿用仓库根的活动工具链（由 `npm run preflight:toolchain` 锁定为
`rust-core/rust-toolchain.toml` 里的版本），本 crate 不单独放 `rust-toolchain.toml`
——rustup 按当前目录取 pin，而这里的构建命令都是从仓库根用 `--manifest-path` 发起的，
放了也不生效。

## 这份代码不是正式接入的模板

重试循环、手写 YAML 配置、`Result<_, String>` 错误、以及 None + 匿名 + 全信任的安全
配置，都是只在"连自己进程内的 server"这个前提下才成立的写法。正式接入的硬约束见
[docs/opcua-blocked.md](../../docs/opcua-blocked.md) 的「正式接入的硬约束」一节。
