# Nexus 对标 HSL 完整功能计划

## 总览

HSL 共 **59 项功能**（Buy 页面列出），Nexus 当前已完成约 **5%**。
本文档逐条列出每项功能及其子功能，标注当前状态和实现细节。

---

## 一、Modbus 协议族（HSL 共 6 项）

### 1.1 Modbus TCP 协议

#### 已实现 ✅
- [x] 基础 TCP 连接/断开
- [x] 短连接模式（自动重连）
- [x] 长连接模式（SetPersistentConnection）
- [x] 读保持寄存器（FC03）— Int16/UInt16/Int32/UInt64/Float/Double
- [x] 读线圈（FC01）— Bool
- [x] 写单线圈（FC05）— Bool
- [x] 写单寄存器（FC06）— Int16/UInt16
- [x] 写多寄存器（FC16）— Int32/Float/String/byte[]
- [x] 虚拟 Modbus TCP 服务器（支持 FC01-06, 15, 16）
- [x] OperateResult 错误处理模式
- [x] 超时设置
- [x] 站号设置

#### 未实现 ❌（必须补全）
- [ ] FC02 — 读离散输入（Read Discrete Inputs）
- [ ] FC04 — 读输入寄存器（Read Input Registers）
- [ ] FC15 — 写多个线圈（Write Multiple Coils）
- [ ] FC23 — 读写多个寄存器（Read/Write Multiple Registers，原子操作）
- [ ] 批量读取（多地址一次请求）
- [ ] 随机读取（不连续地址批量读取）
- [ ] 地址前缀支持（0xxxx=线圈, 1xxxx=离散输入, 3xxxx=输入寄存器, 4xxxx=保持寄存器）
- [ ] 字节序选项（ABCD/DCBA/BADC/CDAB，即大端/小端/中间大端/中间小端）
- [ ] 字符串读写编码选项（ASCII/UTF-8/Unicode）
- [ ] 连接自动重试机制（可配置重试次数和间隔）
- [ ] 数据变化订阅/监控（轮询地址，变化时触发事件）
- [ ] 自定义功能码发送（原始报文收发）
- [ ] 报文日志记录（收发原始字节十六进制显示）
- [ ] 连接状态变化事件（Connected/Disconnected）
- [ ] Async 方法的真正异步实现（当前是 Task.Run 包装同步）

### 1.2 Modbus UDP 协议 ❌
- [ ] ModbusUdpClient（UDP 模式的 Modbus 通讯）
- [ ] 广播模式支持

### 1.3 Modbus RTU 协议（串口） ❌
- [ ] ModbusRtuClient（串口 RS232/RS485 通讯）
- [ ] 串口参数配置（波特率/数据位/停止位/校验位）
- [ ] CRC16 校验
- [ ] FC01-06, 15, 16 全部功能码
- [ ] 虚拟 Modbus RTU 服务器

### 1.4 Modbus RTU Over TCP ❌
- [ ] ModbusRtuOverTcpClient（TCP 通道传输 RTU 格式报文）
- [ ] CRC16 校验透传

### 1.5 Modbus ASCII 协议 ❌
- [ ] ModbusAsciiClient（ASCII 模式串口通讯）
- [ ] LRC 校验
- [ ] FC01-06, 15, 16

### 1.6 Modbus 虚拟服务器增强 ❌
- [ ] 线圈和寄存器的独立存储管理
- [ ] 输入寄存器和离散输入的独立存储
- [ ] 多客户端并发处理优化
- [ ] 报文日志记录
- [ ] 从站地址过滤

---

## 二、西门子协议族（HSL 共 6 项）

### 2.1 S7 协议 ❌
- [ ] TPKT + COTP + S7 Data 三层报文结构
- [ ] 连接建立（COTP Connection Request/Confirm）
- [ ] 通讯建立（S7 Setup Communication）
- [ ] 读变量（S7 Read Var）— 支持多 Item 批量读取
- [ ] 写变量（S7 Write Var）— 支持多 Item 批量写入
- [ ] PLC 型号枚举：S7-200, S7-200Smart, S7-300, S7-400, S7-1200, S7-1500
- [ ] 地址解析：V区, I区, Q区, M区, DB块（DB1.DBW100, DB1.DBX0.0, DB1.DBD0）
- [ ] 数据类型：Bit, Byte, Word, Int, DInt, Real, String, Timer, Counter
- [ ] S7-1200/1500 优化的块访问（Optimized Block Access）
- [ ] 长连接 + 短连接模式
- [ ] 最大 PDU 大小协商
- [ ] 机架/槽位设置（Rack/Slot）

### 2.2 Fetch/Write 协议 ❌
- [ ] 西门子 Fetch/Write 协议（老型号 PLC）
- [ ] 读/写 DB 块数据

### 2.3 PPI 协议 ❌
- [ ] PPI 协议（串口，S7-200 系列）
- [ ] PPI Over TCP

### 2.4 MPI 协议 ❌
- [ ] MPI 协议（串口，S7-300/400 系列）

### 2.5 西门子虚拟 PLC ❌
- [ ] S7 虚拟服务器（模拟 S7-200/300/1200/1500）
- [ ] 支持 Read Var / Write Var
- [ ] 内存模型（DB块 + I/Q/M/T/C）

---

## 三、三菱协议族（HSL 共 10 项）

### 3.1 MC-3E Binary (TCP) ❌
- [ ] SLMP 报文格式（MC 3E Binary Frame）
- [ ] 批量读取（连续地址）
- [ ] 批量写入
- [ ] 随机读取（不连续地址）
- [ ] 随机写入
- [ ] 多块批量读取
- [ ] 地址解析：D寄存器, M线圈, X输入, Y输出, Z变址, R文件寄存器, B链接寄存器, W链接寄存器, L锁存, F状态, V边沿, S步进, TS/TC/CS/CC 定时器计数器
- [ ] 型号枚举：Qna_3E, Qna_2E, A_3E, A_1E, FX_3U, FX_5U
- [ ] 网络/站号/目标站号设置

### 3.2 MC-3E ASCII (TCP) ❌
- [ ] ASCII 编码的 MC 协议

### 3.3 MC-3E UDP ❌
- [ ] UDP 模式的 MC 协议

### 3.4 A-1E 协议 ❌
- [ ] 三菱 A 系列 1E 协议

### 3.5 编程口协议（串口） ❌
- [ ] RS232 编程口通讯
- [ ] 编程口 Over TCP

### 3.6 计算机链接协议 ❌
- [ ] 计算机链接协议（串口 + OverTCP）

### 3.7 A-3C 串口协议 ❌
- [ ] A-3C 串口 + OverTCP

### 3.8 三菱虚拟 PLC ❌
- [ ] MC 虚拟服务器
- [ ] 内存模型（D/M/X/Y/Z/R/B/W/L/F/S 等）

---

## 四、欧姆龙协议族（HSL 共 6 项）

### 4.1 FINS-TCP ❌
- [ ] FINS 帧结构（FINS Header + FINS Data）
- [ ] 客户端 FINS 地址自动分配
- [ ] 读/写数据区：CIO, WR, HR, AR, DM, EM, T/C
- [ ] 数据类型：Bit, Word, Int, UInt, DInt, Real, String
- [ ] 地址解析：D100, CIO100, W100, H100, A100 等

### 4.2 FINS-UDP ❌
- [ ] UDP 模式的 FINS 协议

### 4.3 CIP 协议 ❌
- [ ] 欧姆龙 CIP/EtherNet/IP 协议

### 4.4 HostLink ❌
- [ ] HostLink 协议（串口 + OverTCP）

### 4.5 欧姆龙虚拟 PLC ❌
- [ ] FINS-TCP 虚拟服务器

---

## 五、AB（罗克韦尔）协议族（HSL 共 4 项）

### 5.1 CIP 协议 ❌
- [ ] EtherNet/IP + CIP 协议
- [ ] Tag 读写（Named Tag）
- [ ] Read Tag, Write Tag, Read Tag Fragmented, Write Tag Fragmented
- [ ] 支持 ControlLogix, CompactLogix, MicroLogix

### 5.2 CIP-PCCC ❌
- [ ] PCCC 协议（老型号 SLC/PLC-5）

### 5.3 SLC ❌
- [ ] SLC 500 系列协议

### 5.4 AB 虚拟 PLC ❌
- [ ] CIP 虚拟服务器

---

## 六、其他 PLC 品牌（HSL 共 14 项）

### 6.1 松下 ❌
- [ ] MC-3E 协议（兼容三菱 MC）
- [ ] Mewtocol 协议（串口 + OverTCP）
- [ ] 虚拟 PLC

### 6.2 基恩士 ❌
- [ ] MC-3E 协议（兼容三菱 MC）
- [ ] Nano 协议（串口 + OverTCP）

### 6.3 LS产电 ❌
- [ ] Fast ENet 协议
- [ ] Cnet 协议（串口 + OverTCP）
- [ ] 虚拟 PLC

### 6.4 永宏 ❌
- [ ] 编程口协议（串口 + OverTCP）

### 6.5 富士 ❌
- [ ] 编程口协议（串口 + OverTCP）

### 6.6 GE ❌
- [ ] SRTP 协议

### 6.7 横河 ❌
- [ ] TCP 协议

### 6.8 丰田 ❌
- [ ] ToyoPuc 协议

---

## 七、机器人通讯（HSL 共 3 项）

- [ ] 埃夫特（EFT）机器人通讯
- [ ] ABB 机器人通讯
- [ ] Kuka 机器人通讯

---

## 八、IoT / 中间件（HSL 共 5 项）

### 8.1 Redis ❌
- [ ] Redis 连接和命令
- [ ] 字符串/Hash/List/Set 读写
- [ ] 订阅/发布

### 8.2 MQTT ❌
- [ ] MQTT 客户端（连接/发布/订阅）
- [ ] MQTT 服务器（内置 Broker）
- [ ] QoS 0/1/2

### 8.3 HSL 网络协议 ❌
- [ ] Simplify Net（跨语言 TCP 通讯，C#/Java/Python 互通）
- [ ] Push Net（推送网络协议）

---

## 九、基础设施功能（超越 HSL 的部分也列在这里）

### 已有 ✅
- [x] OperateResult 操作结果模式
- [x] IReadWriteDevice 统一读写接口
- [x] TcpDeviceBase TCP 基类（短/长连接）
- [x] DataConverter 数据转换（大端序）
- [x] WPF UI 框架 + AtelierThemeKit 主题系统
- [x] 虚拟 Modbus TCP Server

### 必须补全 ❌
- [ ] 串口通讯基类（SerialDeviceBase）
- [ ] UDP 通讯基类（UdpDeviceBase）
- [ ] 连接池管理（多设备多连接）
- [ ] 批量读写接口（IBatchReadWrite）
- [ ] 报文日志系统（ILogger 接口）
- [ ] 字节序配置（不是固定大端，可选 ABCD/DCBA/BADC/CDAB）
- [ ] 连接状态事件（OnConnected/OnDisconnected/OnError）
- [ ] 数据监控/轮询引擎（定时读取地址列表，变化触发事件）
- [ ] NuGet 包发布（各模块独立包 + Meta-package）
- [ ] CI/CD（GitHub Actions）
- [ ] API 文档生成
- [ ] 中英双语 README

---

## 十、Nexus 超越 HSL 的差异化功能

| 超越点 | HSL 现状 | Nexus 目标 |
|--------|---------|-----------|
| UI | WinForm 风格 | WPF + AtelierThemeKit 375种主题 |
| 架构 | 单体 DLL | 模块化 NuGet 包 |
| 开源 | V7.0.1 后闭源 | MIT 永久免费 |
| 测试 | 无公开测试 | 每协议虚拟PLC+集成测试 |
| 现代 C# | 部分 async | 全面 async-first + nullable |
| 文档 | 零散博客 | 结构化 API 文档 + 教程 |

---

## 开发顺序（分批推进）

### 批次1：核心协议对齐（覆盖 80% 使用场景）
1. **Modbus TCP 补全** — FC02/04/15/23, 批量读写, 地址前缀, 字节序, 报文日志, 事件
2. **西门子 S7** — 最常用协议，S7-200~1500 全系列
3. **三菱 MC-3E** — 最常用日系 PLC
4. **Modbus RTU/ASCII/UDP** — 补全 Modbus 全系列

### 批次2：第二梯队协议（覆盖 95%）
5. **欧姆龙 FINS-TCP**
6. **AB CIP**
7. **松下/基恩士/LS产电**

### 批次3：IoT + 机器人 + 超越
8. **MQTT 客户端/服务器**
9. **Redis 客户端**
10. **跨语言网络协议**
11. **机器人通讯**

### 批次4：打磨发布
12. 全协议虚拟 PLC
13. NuGet 发布
14. API 文档
15. Demo 工具完善
