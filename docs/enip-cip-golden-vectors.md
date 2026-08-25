# Allen-Bradley EtherNet/IP / CIP 首轮黄金帧

日期：2026-08-22  
范围：EtherNet/IP encapsulation、RegisterSession/UnregisterSession、SendRRData/CPF、CIP Read Tag 显式只读编解码。  
证据等级：S2-S4a 软件 TCP 对端与 JSONL 回归；**不是** CompactLogix/ControlLogix、Micro800 或任何真实控制器的 L2 记录。

这份向量固定 Nexus 当前“新增协议”第一条边界。它证明的是：协议已登记、UI 可以生成/解析和发起只读会话、Electron preload/main 可以把命令送到 Rust core、Rust 能完成 RegisterSession、TCP 分片收帧、CIP 状态和长度 fail-closed 解析；它不证明真实控制器型号兼容、Tag 外部访问权限或现场值正确。

## 1. EtherNet/IP 封装头

固定 24 字节，小端序：

| 偏移 | 字段 | 说明 |
|---:|---|---|
| 0 | Command (u16) | `0x0065` RegisterSession、`0x0066` UnregisterSession、`0x006F` SendRRData |
| 2 | Length (u16) | 头后 payload 的精确字节数 |
| 4 | Session Handle (u32) | RegisterSession 请求通常为 0，成功响应后由设备分配 |
| 8 | Status (u32) | 封装层状态，成功为 0 |
| 12 | Sender Context (8B) | 请求/响应关联上下文，向量使用小端 `1` 或 `7` |
| 20 | Options (u32) | 首轮必须为 0 |

## 2. RegisterSession 请求

由 Rust `enip_build_register_session` 生成，Sender Context=1：

```text
65 00 04 00 00 00 00 00 00 00 00 00 01 00 00 00
00 00 00 00 00 00 00 00 01 00 00 00
```

payload 是协议版本 `1` 和选项 `0`：`01 00 00 00`。收到响应后，必须校验 command、status、sender context、options、协议版本和非零 Session Handle；Rust 会话层只把通过这些校验的连接放入只读 Session。

## 3. UnregisterSession 请求

由 `enip_build_unregister_session` 生成。Session Handle=0x11223344、Sender Context=7 时：

```text
66 00 00 00 44 33 22 11 00 00 00 00 07 00 00 00
00 00 00 00 00 00 00 00
```

## 4. CIP Read Tag 请求

场景：Session Handle=`0x11223344`、Sender Context=`7`、Tag=`MyTag[3]`、Elements=`2`。

Tag 路径由两个段组成：

```text
91 05 4D 79 54 61 67 00   # Symbolic Segment "MyTag"，奇数长度补 00
28 03                      # 8-bit array index 3
```

完整 EtherNet/IP SendRRData 请求：

```text
6F 00 1E 00 44 33 22 11 00 00 00 00 07 00 00 00
00 00 00 00 00 00 00 00
00 00 00 00 00 00 02 00 00 00 00 00 B2 00 0E 00
4C 05 91 05 4D 79 54 61 67 00 28 03 02 00
```

关键字段：

- ENIP Command=`0x006F`，payload Length=`0x001E`。
- CPF Interface Handle=0，Timeout=0，Item Count=2。
- Item 1 是 Null Address (`0x0000`, length 0)。
- Item 2 是 Unconnected Data (`0x00B2`)，长度 14，承载 CIP `Read Tag (0x4C)`。
- CIP Path Size=5 个 16-bit word，Elements=2。

首轮支持普通 Tag、成员和数组索引（例如 `MyTag`、`MyTag.Member`、`MyTag[3]`、`Program:MainProgram.Speed`）；当前不开放 Write Tag、Fragmented Read、Multiple Service Packet 或 PCCC。

## 5. CIP Read Tag 响应解析向量

以下是软件构造的成功响应：CIP Reply Service=`0xCC`、General Status=`0`、返回两个字节 `34 12`。它用于验证 CPF、状态和数据长度解析，不代表控制器真实类型。

```text
6F 00 16 00 05 00 00 00 00 00 00 00 09 00 00 00
00 00 00 00 00 00 00 00
00 00 00 00 00 00 02 00 00 00 00 00 B2 00 06 00
CC 00 00 00 34 12
```

解析结果应包含：

```json
{
  "service": 204,
  "generalStatus": 0,
  "cipOk": true,
  "dataHex": "3412"
}
```

General Status 非零时，解析器仍返回状态和 Extended Status，但 `cipOk=false`；封装层长度、CPF 项目边界、CIP Additional Status 长度或 Session Context 不合法时直接失败。

## 6. 当前代码/测试证据

- Registry/UI：`src/protocol-registry.js`、`src/protocol-guides.js`、`index.html`、`src/main.js` 已新增 `allen-bradley/cip`，页面可发起 TCP 只读会话和离线生成/解析。
- Electron bridge：`electron/preload.cjs`、`electron/main.cjs`、`electron/rust-core-client.cjs` 已登记 `open_enip_connection`、`enip_read_tag` 与编解码命令。
- Rust codec：`rust-core/src/enip.rs`，覆盖 ENIP 头、CPF、Tag symbolic/index path、CIP 状态和畸形长度。
- Rust Session：`rust-core/src/session.rs` 完成 TCP 44818 RegisterSession、Sender Context、Read Tag 收发和 UnregisterSession best-effort 关闭。
- JSONL E2E：`rust-core/tests/enip_jsonl_e2e.rs` 3/3 通过，含独立 TCP 对端；`npm run test:rust-jsonl` 同时执行 AB 3/3 与既有协议回归。
- UI/registry regression：`electron/protocol-registry.test.cjs` 与 `electron/protocol-guide-ui.test.cjs` 验证当前 46 个可选变体和帮助目录一致。

独立 TCP 对端已验证 RegisterSession、分片 Read Tag 响应和 UnregisterSession 关闭请求；该证据不替代真实控制器 L2。

## 7. 未完成门禁

- [x] TCP 44818 connect/read-only transaction、RegisterSession 响应会话生命周期（软件独立 TCP 对端）。
- [ ] ListIdentity 发现与设备身份指纹。
- [ ] Unconnected Send 路由（背板/槽号）和目标模块路径矩阵。
- [ ] Read Tag Fragmented、数组/UDT/STRING 数据类型矩阵。
- [ ] Connected CIP / ForwardOpen / Implicit I/O。
- [ ] PCCC、DF1、CIP Safety 和任何写入/运行控制。
- [ ] CompactLogix/ControlLogix/Micro800 真实型号、固件、Tag 权限和 L2 抓包记录。
