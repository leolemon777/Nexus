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
