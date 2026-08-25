# MQTT 3.1.1 Golden Vectors（TCP 只读订阅）

本文件固定 Nexus 首轮 MQTT 3.1.1 边界。向量、内置独立 TCP 对端和 Aedes 1.1.1 独立 Broker 互操作只证明 Rust 编解码、CONNECT/CONNACK、SUBSCRIBE/SUBACK、QoS 0 PUBLISH、PING、DISCONNECT、匿名允许、需认证拒绝和 Topic ACL 允许/拒绝的软件会话；不证明生产 Mosquitto/EMQX、TLS、证书、Sparkplug B 或现场长稳。

## CONNECT

Client ID=`nexus-test`、Keep Alive=`30`、Clean Session=true：

```text
10 16 00 04 4D 51 54 54 04 02 00 1E 00 0A 6E 65 78 75 73 2D 74 65 73 74
```

固定字段：Protocol Name=`MQTT`、Level=`4`（3.1.1）、Connect Flags=`0x02`、Keep Alive 大端 `0x001E`。首轮不带 Will、Username 或 Password。

## CONNACK

成功响应：

```text
20 02 00 00
```

Acknowledge Flags 必须只有 Session Present bit；Return Code `0` 才能进入订阅会话，1..5 映射为版本、Client ID、Broker、认证或授权错误。

## SUBSCRIBE / SUBACK

Packet ID=`7`、Topic Filter=`factory/line1/#`、请求 QoS=`0`：

```text
82 14 00 07 00 0F 66 61 63 74 6F 72 79 2F 6C 69 6E 65 31 2F 23 00
90 03 00 07 00
```

SUBSCRIBE 固定 flags=`0x2`，Packet ID 不能为零；SUBACK 必须回显 Packet ID，授权 QoS 必须匹配。页面 live path 默认只请求 QoS 0。

## QoS 0 PUBLISH

Topic=`state`、Payload=`4F 4B 21 00 01 02`：

```text
30 0D 00 05 73 74 61 74 65 4F 4B 21 00 01 02
```

PUBLISH 解析严格校验 Topic UTF-8、QoS/保留位、Topic 长度和剩余长度。首轮 live path 只接收 QoS 0，不自动发送 PUBACK/PUBREC；Payload 原样返回，同时提供 HEX 和可选 UTF-8 展示。

## 心跳和关闭

```text
C0 00       # PINGREQ
D0 00       # PINGRESP
E0 00       # DISCONNECT
```

## 独立 Broker 互操作证据

`scripts/mqtt-aedes-integration.test.cjs` 启动 Aedes 1.1.1 的真实 TCP Broker，并使用 MQTT.js 5.15.2 作为独立发布客户端，固定执行：

1. Nexus Rust sidecar 以 MQTT 3.1.1 连接动态 loopback 端口并收到成功 CONNACK。
2. Nexus 订阅 `nexus/e2e/temperature`，Broker 返回 QoS 0 SUBACK。
3. MQTT.js 从另一条 TCP 连接发布 JSON Payload，Nexus 读取并核对 Topic、Payload、QoS 和 Retain。
4. Nexus 发送 PINGREQ 并核对 Broker 的 `D0 00` PINGRESP。
5. Broker 拒绝 `nexus/forbidden/#`，Nexus 必须返回 `MQTT_SUBACK_REJECTED`。
6. Broker 对指定 Client ID 要求凭据；由于首轮 Nexus 不发送凭据，CONNACK Return Code 4 必须映射为 `MQTT_CONNACK_REJECTED`。
7. Nexus 显式 DISCONNECT；测试关闭发布客户端、sidecar、TCP listener 和 Broker，不能遗留后台服务。

该测试属于独立实现交叉验证，不是生产 Broker、现场网络或 L2 通过证据。

## 代码、测试与未完成门禁

- `rust-core/src/mqtt.rs` 固定 MQTT 3.1.1 fixed header、remaining length、CONNECT/CONNACK、SUBSCRIBE/SUBACK、PUBLISH、PING 和 DISCONNECT。
- `rust-core/src/session.rs` 接入 TCP 1883 CONNECT/CONNACK、只读订阅、QoS 0 PUBLISH 收帧、PING 和 best-effort DISCONNECT；不提供 PUBLISH 写入。
- `rust-core/tests/mqtt_jsonl_e2e.rs` 3/3 通过，含独立 TCP Broker 对端和分片响应。
- `scripts/mqtt-aedes-integration.test.cjs` 1/1 通过，覆盖 Aedes/MQTT.js 独立实现的完整订阅读回、PING、认证拒绝和 Topic ACL 拒绝；由 `scripts/test-rust-jsonl.ps1` 构建普通 sidecar 后自动执行。
- `electron/mqtt-route.test.cjs` 验证 Rust client、preload/main allow-list、MQTT 页面和只读入口；当前 Electron 全量回归数字以 `spec-plan.markdown` 为准。
- 未完成：生产 Mosquitto/EMQX/现场 Broker、TLS 8883、用户名密码/证书正向认证、复杂 ACL、QoS 1/2 确认、遗嘱、离线队列、自动重连、Sparkplug B 和 24/72 小时 L2。
