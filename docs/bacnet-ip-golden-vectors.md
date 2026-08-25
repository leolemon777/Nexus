# BACnet/IP Who-Is / I-Am 黄金帧与审计边界

## 1. 证据链与重新引入边界

| 来源 | 结论 | 本轮使用方式 |
|---|---|---|
| 旧 `Nexus.Bacnet` 移除审计 | BVLC Function 全部写 00H；上下文标签缺 class 位；LVT 少 1；I-Am 缺 Object Identifier 标签；响应/COV 事务不可信 | 作为反面清单，明确不继承任何旧实现 |
| ASHRAE Annex J 官方资料入口 | BACnet/IPv4 BVLL Type 为 81H；Original-Unicast-NPDU=0AH，Original-Broadcast-NPDU=0BH；长度包含 BVLC 全部字节 | 锁定 BVLC 4 字节头和 0AH/0BH 边界 |
| 公开 BACnet-stack 编码实现/测试 | NPDU v1 本地控制位、Unconfirmed PDU 10H；Who-Is=08H、I-Am=00H；Who-Is 范围是上下文 [0]/[1] Unsigned；I-Am 四字段分别是 Object Identifier、Unsigned、Enumerated、Unsigned | 只复核语义，未复制代码，未引入依赖 |

本轮修复的标签位宽是关键点：tag number 占高 4 位，class-specific 位是 bit3，LVT 是低 3 位。因此：

- Object Identifier 应用标签 12、LVT 4 → `C4`。
- Unsigned 应用标签 2、LVT 2 → `22`。
- Enumerated 应用标签 9、LVT 1 → `91`。
- Who-Is 上下文标签 0/1、LVT 2 → `0A`/`1A`。

## 2. 黄金帧向量

### 2.1 全局 Who-Is（Original-Broadcast）

```text
81 0B 00 08 01 00 10 08
```

字段：`81` BVLL BACnet/IPv4 | `0B` Original-Broadcast-NPDU | `0008` BVLC 总长 | `01 00` 本地 NPDU v1 / APDU、Normal 优先级 | `10` Unconfirmed-Request-PDU | `08` Who-Is。无服务参数表示全局范围。

### 2.2 范围 Who-Is（1000..2000）

```text
81 0B 00 0E 01 00 10 08 0A 03 E8 1A 07 D0
```

字段：`0A 03 E8` 是 context [0] Unsigned 1000；`1A 07 D0` 是 context [1] Unsigned 2000。范围必须成对出现、low≤high，且均在 0..4194303 内。

### 2.3 I-Am（Device 1001，Max-APDU 480，无分段，Vendor 42，Original-Unicast）

```text
81 0A 00 14 01 00 10 00 C4 02 00 03 E9 22 01 E0 91 03 21 2A
```

字段分解：

- `10 00`：Unconfirmed-Request-PDU，服务 I-Am。
- `C4 02 00 03 E9`：Object Identifier 应用标签，Device 类型 8 + instance 1001。
- `22 01 E0`：Unsigned Max-APDU 480。
- `91 03`：Enumerated Segmentation=3（无分段）。
- `21 2A`：Unsigned Vendor ID 42。

### 2.4 I-Am（同上字段，Original-Broadcast）

```text
81 0B 00 14 01 00 10 00 C4 02 00 03 E9 22 01 E0 91 03 21 2A
```

### 2.5 ReadProperty 请求（Analog Input 1001 / Present Value / Invoke 1）

```text
81 0A 00 11 01 04 00 03 01 0C 0C 00 00 03 E9 19 55
```

字段：`01 04` 为本地 NPDU 且 DER=1；`00 03` 为不分段、Max-APDU 480 的 Confirmed-Request 声明；`0C` 是 ReadProperty 服务；`0C 00 00 03 E9` 是 context [0] Object Identifier（type 0 Analog Input + instance 1001）；`19 55` 是 context [1] Property Identifier 85（Present Value）。

如需数组元素，在 `19 55` 后追加 context [2] Unsigned，例如 `1A 00` 表示 index 0。

### 2.6 ReadProperty ComplexACK（Real 123.45）

```text
81 0A 00 17 01 00 30 01 0C 0C 00 00 03 E9 19 55 3E 44 42 F6 E6 66 3F
```

字段：`30 01 0C` 为 ComplexACK / Invoke 1 / ReadProperty；`3E`、`3F` 分别是 context [3] opening/closing tag；`44 42 F6 E6 66` 是 Real 应用标签 4 + IEEE-754 值 123.45。ACK 必须与请求的 Invoke ID、对象、属性和可选数组索引完全一致。

## 3. 解析拒绝边界

以下均必须返回结构化错误，不能宽松搜索或截断：

```text
81 00 00 08 01 00 10 08
```

BVLC Function 00H 是 BVLC-Result，也是旧 `Nexus.Bacnet` 的错误之一；本轮解析返回 `BACNET_BVLC_FUNCTION_UNSUPPORTED`。

```text
81 0B 00 08 01 20 10 08
```

NPDU 控制位包含源/目的路由说明或其他不支持字段，返回 `BACNET_NPDU_CONTROL_UNSUPPORTED`。本轮只接受本地非路由、非网络消息、不期待回复的 NPDU。

```text
81 0B 00 09 01 00 10 08
```

BVLC Length 与 UDP 载荷长度不符，返回 `BACNET_BVLC_LENGTH_MISMATCH`。

其他强制失败：非 81H BVLC Type、非 01H NPDU 版本、非 10H APDU、非 08H/00H 服务、只出现一半 Who-Is 范围、low>high、I-Am 第一字段不是 `C4` Object Identifier、对象类型不是 Device、Segmentation>3、Vendor ID 超 16 位、服务尾随数据。

ReadProperty 额外强制失败：ReadProperty 请求/ACK 使用 0BH 广播、请求 NPDU 没有 DER、ACK NPDU 带 DER、APDU 声明不是 03H、service choice 不是 0CH、context [0]/[1] 缺失或类型错误、数组索引越界、ACK 缺少 `3E`/`3F` 值包装、[3] 内为空或多个应用值、ACK 与请求回显不一致、Real 长度不是 4、Real 为 NaN/Infinity。

## 4. 安全与成熟度边界

- 当前命令包括六个离线命令和三个 UDP 只读会话命令：`open_bacnet_ip_connection`、`bacnet_ip_whois`、`bacnet_ip_read_property_live`。
- 没有 `bacnet_ip_read_property`（在线泛用命令）、`bacnet_ip_read_property_multiple`、`bacnet_ip_write_property`、`bacnet_ip_subscribe_cov`、`bacnet_ip_register_foreign_device`。
- 在线 Who-Is 固定连接一个显式 UDP 对端并发送 0AH Original-Unicast；不发送 0BH 广播。ReadProperty 在线事务递增 Invoke ID，并核对 ACK 的对象、属性、可选索引和 Invoke ID。
- 独立脚本对端回归覆盖：Who-Is 请求 `81 0A 00 08 01 00 10 08` → I-Am；ReadProperty 请求 `81 0A 00 11 01 04 00 03 01 0C ...` → Real 123.45 ComplexACK；Invoke ID 回显不一致返回 `BACNET_READ_PROPERTY_MISMATCH` 并断开本地 UDP 会话。
- ReadProperty 仅解释 Unsigned/Real 并保留未知应用标签原始字节。ReadPropertyMultiple、WriteProperty、COV、分段、错误/拒绝/中止、BBMD/Foreign Device、Forwarded-NPDU、BACnet/SC 和 MS/TP 均未实现。
- 成熟度：软件审计 + 离线编解码 + 独立脚本 UDP 对端 + bacstack 0.0.1-beta.14 独立栈互操作 S1-S4b。`scripts/bacnet-bacstack-integration.test.cjs` 使用 MIT 许可的独立 JavaScript BACnet 栈，通过动态 loopback UDP 完成 Who-Is→I-Am 与 ReadProperty→Real ComplexACK 1/1；该栈的 ReadProperty 服务端处理路径官方标注 beta，仅作为开发/测试互操作证据。真实楼宇控制器、BBMD/FDT 行为、抓包比对和长稳均为 L2 pending。软件对端或独立栈通过不得记为现场 PASS。
