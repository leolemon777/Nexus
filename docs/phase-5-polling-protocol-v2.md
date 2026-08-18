# 阶段 5:协议升级 v2 + 轮询订阅

> 把轮询逻辑从 Electron 侧 `setInterval` 下沉到 Rust 侧,引入流式 JSONL 协议(v1 → v2)。
> 这是架构变更阶段,依赖 [阶段 2](./phase-2-modbus-master-advanced.md) 的轮询需求明确后执行。
>
> 主索引:[spec-plan.md](./spec-plan.md)

---

## 目标

把当前的「1 请求 → 1 响应」模型升级为支持「1 订阅 → N 推送」的流式模型。完成后:
- 轮询在 Rust 侧执行(消除 Electron setInterval 的 IPC 往返开销)
- 多个订阅可并行(各自独立的 interval)
- 协议版本升级到 v2,但**向后兼容 v1**(v1 客户端仍能工作)
- 推送帧带 `streamId`,渲染层按 stream 路由

### 为什么需要这个阶段

阶段 2 的轮询用 Electron 侧 `setInterval` 实现,问题是:
1. **IPC 开销** —— 每个 tick 一次 JSONL 往返(Rust build → Electron transact → Rust parse),延迟累积
2. **精度差** —— `setInterval` 受事件循环影响,高频(>10Hz)时不稳定
3. **无法多路复用** —— 串口是半双工,多个轮询只能排队,Electron 侧调度低效
4. **无法推送** —— 从站模式收到请求时无法主动通知渲染层(只能轮询查询)

把轮询下沉到 Rust 侧:Rust 自己定时、自己收发、自己解析,只在有新数据时推送一帧 JSONL 给 Electron。

### 就绪标准
- [ ] JSONL 协议升级到 v2,支持 `streamId` 顶层字段
- [ ] `hello` 命令支持版本协商
- [ ] Rust core:轮询调度器(基于 `std::time::Instant`,单线程非阻塞)
- [ ] Rust core:推送式订阅(start → N 推送 → stop)
- [ ] Electron:`rust-core-client.cjs` 支持流式响应(不删 entry 的订阅 map)
- [ ] UI:轮询切换到 Rust 侧,渲染层只接收推送

---

## 协议升级:v1 → v2

### 破坏性变更

v1 的 `ResponseEnvelope`:
```rust
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResponseEnvelope {
    pub protocol_version: u16,
    pub request_id: Option<String>,
    pub ok: bool,
    pub result: Option<Value>,
    pub error: Option<ErrorBody>,
}
```

v2 新增可选字段 `streamId`:
```rust
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ResponseEnvelope {
    pub protocol_version: u16,
    pub request_id: Option<String>,
    pub stream_id: Option<String>, ,   // ← v2 新增,仅订阅推送帧有
    pub ok: bool,
    pub result: Option<Value>,
    pub error: Option<ErrorBody>,
    pub stream_end: Option<bool>,       // ← v2 新增,true = 流结束
}
```

> 注:`RequestEnvelope` 仍用 `deny_unknown_fields` —— v1 客户端发的请求 v2 服务端能解析(因为没加新请求字段)。只有响应多了字段,v1 客户端解析时会忽略未知字段(serde 默认行为)—— **所以实际向后兼容**。

### 版本协商

`hello` 命令扩展:
```json
// v1 hello
{"command":"hello","payload":{}}
→ {"result":{"protocolVersion":1, "supportedVersions":[1]}}

// v2 hello
{"command":"hello","payload":{"clientVersion":2}}
→ {"result":{"protocolVersion":2, "supportedVersions":[1,2], "features":["streaming"]}}
```

客户端根据 `supportedVersions` 选择最高公共版本。v1 客户端发 v1 请求,服务端以 v1 响应(无 `streamId`)。v2 客户端可用流式命令。

---

## Rust core 变更

### `serve()` 循环重构(支持非阻塞 + 定时推送)

当前 `serve()` 是阻塞读 stdin。v2 需要「同时监听 stdin + 定时器」:

**方案:stdin 监听线程 + 主循环**

```rust
pub fn serve<R: BufRead, W: Write + Send>(session: Arc<Mutex<Session>>, reader: R, writer: W) {
    // stdin 在主线程阻塞读
    // 定时器在单独线程,通过 channel 通知主循环
    let (timer_tx, timer_rx) = std::sync::mpsc::channel();

    std::thread::spawn(move || {
        loop {
            std::thread::sleep(Duration::from_mils(10));
            let now = Instant::now();
            let due = session.lock().poll_due_streams(now);
            for stream_id in due {
                timer_tx.send(stream_id).ok();
            }
        }
    });

    loop {
        // 非阻塞检查 timer channel
        while let Ok(stream_id) = timer_rx.try_recv() {
            let outcomes = session.lock().fire_poll(&stream_id);
            for o in outcomes { write_outcome(&writer, o)?; }
        }
        // 阻塞读 stdin(带超时,让定时器有机会跑)
        match read_bounded_line_timeout(&mut reader, Duration::from_millis(10))? {
            Some(line) => {
                let outcome = handle_line(&mut session, &line);
                write_outcome(&writer, outcome)?;
            }
            None => continue,   // 超时,回去检查定时器
        }
    }
}
```

> **Windows stdin 非阻塞的麻烦** —— `std::io::stdin()` 无原生超时。方案:用 `BufReader::fill_buf()` + 单独 stdin 线程 + channel,或用 `WaitForSingleObject` + stdin handle(Windows API)。阶段 5 实现时需验证。

### 流式订阅命令

| 命令 | payload | result(多帧推送) |
|---|---|---|
| `start_poll_stream` | `{streamId, connectionId\|transport, unitId, fc, startAddress, quantity, intervalMs, dataType?, byteOrder?}` | 第 1 帧:`{streamId, started:true}`;后续帧:`{streamId, values:[...], timestamp, elapsedMs}`;异常帧:`{streamId, ok:false, error}` |
| `stop_poll_stream` | `{streamId}` | `{streamId, streamEnd:true}` |

### Session 新增

```rust
impl Session {
    pub fn start_poll(&mut self, config: PollConfig) -> Result<String, CoreError>;
    pub fn stop_poll(&mut self, stream_id: &str) -> Result<(), CoreError>;
    pub fn poll_due_streams(&self, now: Instant) -> Vec<String>;
    pub fn fire_poll(&mut self, stream_id: &str) -> Vec<CommandOutcome>;
}

pub struct PollConfig {
    pub stream_id: String,
    pub connection_id: Option<String>,   // TCP/UDP
    pub transport: Option<Transport>,     // 串口
    pub unit_id: u8,
    pub fc: u8,
    pub start_address: u16,
    pub quantity: u16,
    pub interval_ms: u32,
    pub next_due: Instant,
}
```

---

## Electron 层变更

### `rust-core-client.cjs` — 流式支持

新增订阅 map(不删 entry):
```javascript
class RustCoreClient {
  constructor() {
    this.pending = new Map();           // 一次性请求(现有)
    this.subscriptions = new Map();     // 流式订阅(新增)
  }

  // 流式订阅方法
  async startPollStream(config, onData, onError) {
    const streamId = config.streamId || generateId();
    this.subscriptions.set(streamId, { onData, onError });
    await this._sendRequest("start_poll_stream", { ...config, streamId });
    return streamId;
  }

  async stopPollStream(streamId) {
    await this._sendRequest("stop_poll_stream", { streamId });
    this.subscriptions.delete(streamId);
  }

  // 修改 _handleResponse:先查 subscriptions,再查 pending
  _handleResponse(response) {
    if (response.streamId && this.subscriptions.has(response.streamId)) {
      const sub = this.subscriptions.get(response.streamId);
      if (response.streamEnd) {
        this.subscriptions.delete(response.streamId);
      } else if (response.ok) {
        sub.onData(response.result);
      } else {
        sub.onError(response.error);
      }
      return;   // 不走 pending 路径
    }
    // ... 现有 pending 逻辑 ...
  }
}
```

### 从站推送通知

从站模式收到客户端请求时,主动推送:
```json
{"streamId":"slave-1-traffic", "result":{"direction":"rx","unitId":1,"fc":3,"hex":"..."}}
```
渲染层订阅 `slave-1-traffic` 即可实时看从站流量。

---

## UI 变更

### 轮询切换到 Rust 侧(透明)

UI 层面无感知变化 —— `start_poll` / `stop_poll` 的 IPC handler 内部从 `setInterval` 切换到 `startPollStream` / `stopPollStream`。渲染层仍通过事件接收数据:

```javascript
// 渲染层(不变)
callBackend("start_poll", { addresses, intervalMs }).then(pollId => {
  window.nexusDesktop.on("poll_data", ({ pollId, data }) => {
    updateRegisterTable(data);
  });
});

// Electron 层(内部切换)
ipcMain.handle("nexus:start_poll", async (e, config) => {
  const streamId = await rustCore.startPollStream(
    { ...config, streamId: `poll-${Date.now()}` },
    (data) => mainWindow.webContents.send("nexus:poll_data", { pollId: streamId, data }),
    (err) => mainWindow.webContents.send("nexus:poll_error", { pollId: streamId, error: err }),
  );
  return { pollId: streamId };
});
```

---

## 测试要求

### Rust 单元测试
- `Session::start_poll` / `stop_poll` / `fire_poll` 逻辑
- 定时器精度(`Instant` 比较)
- 多 stream 并行

### JSONL 集成测试
- v2 版本协商(v1 客户端 + v2 服务端兼容)
- `start_poll_stream` → 收到 N 帧推送 → `stop_poll_stream`
- 流式帧的 `streamId` 一致性

### Electron 测试
- `rust-core-client.cjs`:subscriptions map 不删 entry、streamEnd 时清理
- 混合 one-shot + streaming 请求的正确路由

### 冒烟测试
- 协议版本协商无报错
- 轮询数据推送正常

---

## 风险与注意事项

1. **Windows stdin 非阻塞** —— 这是本阶段最大技术风险。`std::io::stdin()` 无超时 API。需要用 `std::thread` + channel,或 Windows API `PeekNamedPipe` / `WaitForSingleObject`。如果不解决,轮询推送会卡在 stdin 阻塞读上。
2. **协议版本协商的边界情况** —— v1 客户端发 `hello` 不带 `clientVersion`,v2 服务端应识别为 v1 并降级。测试必须覆盖。
3. **订阅泄漏** —— 如果渲染层 `stop_poll` 失败(如页面刷新),Rust 侧 stream 会持续推送。需要心跳/超时清理机制(无活动 N 秒后自动 stop)。
4. **推送频率 vs JSONL 带宽** —— 10 个订阅 × 10Hz = 100 帧/秒,每帧 JSONL ≈ 200 字节 = 20KB/s。1MiB 行限内,但 stdout 写入需要 flush 及时。
5. **向后兼容测试矩阵** —— v1 客户端 + v2 服务端、v2 客户端 + v1 服务端、v2 + v2,三种组合都要测。

---

## 对标参考

| 参考 | 文件 | 借鉴点 |
|---|---|---|
| .NET Nexus | `Nexus/src/Nexus.Core/ISubscribeDevice.cs` | 订阅接口设计 |
| .NET Nexus | `Nexus/src/Nexus.Modbus/ModbusTcpClient.cs:1103-1153` | Subscribe/Unsubscribe/StartSubscriptions 实现 |
| .NET Nexus | `Nexus/src/Nexus.App/Services/MonitorService.cs` | 监控调度服务 |
