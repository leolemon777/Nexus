# Nexus 2.0 — 工业通讯调试工作台

**一体化工业协议调试工具**：Modbus 全家 + 三菱 MC 12 变体 + 西门子 10 通道 + 欧姆龙 FINS + 国产品牌映射 + 变频器 USS + S5 兼容 RK512。

![License](https://img.shields.io/badge/license-MIT-blue)
![Tests](https://img.shields.io/badge/tests-492%20passing-brightgreen)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)

## 支持协议

| 厂商 | 协议 | 传输 | 状态 |
|------|------|------|------|
| **Modbus** | TCP / UDP / RTU / ASCII | 网口 / 232 / 485 | ✅ 主站+从站+轮询 |
| **三菱** | MC Binary 3E/4E / ASCII / UDP / A-1E / C24 串口 / FX Link / FX 编程口 | 网口 / 232 / 485 | ✅ 12 变体 + 虚拟从站 |
| **西门子** | S7comm (0x32) / Fetch-Write / PPI / Web API / Modbus TCP / USS / RK512 | 网口 / 232 / 485 | ✅ 10 通道 + CPU启停/SZL/密码 |
| **欧姆龙** | FINS TCP / UDP | 网口 | ✅ + 虚拟 PLC |
| **国产品牌** | 台达 DVP / 汇川(部分) | 走 Modbus | ✅ 地址映射层 |
| **变频器** | USS (SINAMICS / MicroMaster) | 232 / 485 | ✅ 组帧/解帧 |
| **S5 兼容** | 3964R + RK512 | 232 / 485 | ✅ 组帧/解帧 |

## 快速开始

### 方式一：便携版(推荐)

下载 `Nexus-2.0-portable.zip`，解压后双击 `Nexus 2.0.exe`。免安装、免依赖、免管理员权限(改 IP 功能除外)。

### 方式二：开发模式

```bash
# 安装依赖
npm install

# 构建 Rust 核心
npm run build:rust-core

# 构建 Web 界面
npm run build

# 启动 Electron
npm run electron

# 打包便携版
npm run package:portable
```

## 架构

```
┌─────────────────────────────────────────────────┐
│                Electron (渲染层)                  │
│  3 列布局:导航 | 工作区 | 报文面板                  │
├─────────────────────────────────────────────────┤
│                Electron (主进程)                   │
│  IPC 白名单 · 串口服务 · 轮询调度 · Web API         │
├─────────────────────────────────────────────────┤
│              Rust Sidecar (JSONL stdio)           │
│  协议核心: Modbus · MC · S7 · FINS · PPI          │
│  帧层: RTU/ASCII/TPKT/COTP/FX/C24/PPI/USS/RK512  │
│  虚拟从站: Modbus · MC · S7 · FINS · PPI · FW     │
└─────────────────────────────────────────────────┘
```

## 测试

```bash
# Rust 全量(单元 + E2E + 交叉验证)
cd rust-core && cargo test

# Electron 单元
node --test electron/*.test.cjs

# 交叉验证(需 Python)
pip install python-snap7 pymodbus
python tools/python_snap7_cross.py
```

**451 Rust + 41 Electron 测试**，含 golden 向量逐字节断言、python-snap7/pymodbus 第三方交叉验证。

## 安全审查

两轮全面对抗性审查（代码正确性 + 并发/资源 + 安全面 + 前端状态），报告见 `docs/audit-*.md`。

## 技术文档

| 文档 | 内容 |
|------|------|
| `docs/西门子全协议设计文档.md` | 西门子 20 章字节级规范 |
| `docs/三菱全协议设计文档.md` | 三菱 MC 家族字节级规范 |
| `docs/协议路线图-v2.md` | 多品牌扩展路线 |
| `docs/research/` | 调研报告(snap7 交叉/开源对比/VOC/协议深挖) |
| `docs/audit-*.md` | 代码+安全审查报告 |

## License

MIT — 见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party dependencies.
