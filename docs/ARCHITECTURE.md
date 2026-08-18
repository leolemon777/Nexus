# Nexus 2.0 architecture

## Current phase boundary

Phase 1 established the desktop and serial transport boundary. Phase 2.1 added strict generic RTU framing and CRC. Phase 2.2 exposes one-shot function 03 (Read Holding Registers), and Phase 2.3 adds one-shot function 04 (Read Input Registers).

> **完整产品规划见 [spec-plan.md](./spec-plan.md)** — 六阶段全覆盖计划(主站核心 / 主站高级 / 从站模拟 / 串口调试 / 协议升级 v2 / 打磨扩展),对标 Modbus Poll + Modbus Slave + HSL 串口调试助手。各阶段详细规格见 `phase-1` 至 `phase-6` 文档;三大产品形态总览见 [产品架构.md](./产品架构.md)。

```text
Reverie HTML/CSS/JavaScript renderer
                |
        allowlisted preload IPC
                |
       Electron main process
                |
     serial transport adapter

Electron main process <-> Rust protocol core
                          |- Phase 1: hello/version handshake
                          |- Phase 1: serial configuration validation
                          |- Phase 2.1: strict RTU framing and CRC
                          |- Phase 2.2: FC03 request build and response parse
                          |- Phase 2.3: FC04 request build and response parse
                          |- spec-plan Phase 1: full FC (01-06,15,16) + 6 transports
                          |- spec-plan Phase 2: scanning + polling + value codecs
                          |- spec-plan Phase 3: Modbus Slave simulator
                          |- spec-plan Phase 4: Serial Debug terminal
                          |- spec-plan Phase 5: protocol v2 + streaming subscriptions
                          |- spec-plan Phase 6: frame parser + export + bridges + advanced FC
```

Electron owns the desktop lifecycle and the minimum Windows serial handle adapter. Rust remains the authority for protocol behavior, binary codecs, validation rules shared by Modbus modules, and high-value tests. The boundary will use versioned messages so the serial adapter can later move behind a Rust sidecar without changing the renderer.

## Rust Core protocol v1

The Electron main process starts one Rust sidecar and exchanges newline-delimited JSON over stdin/stdout. Logs are restricted to stderr. Each line is limited to 1 MiB.

```json
{"protocolVersion":1,"requestId":"1","command":"hello","payload":{}}
{"protocolVersion":1,"requestId":"1","ok":true,"result":{},"error":null}
```

The sidecar exposes `hello`, `validate_serial_config`, typed FC03/FC04 build and parse commands, and `shutdown`. A failed response always contains `result: null` and a stable error object. Unknown versions and commands fail explicitly; they are never silently reinterpreted.

## Modbus RTU transport boundary

`rust-core/src/modbus_rtu.rs` is a pure library module and is intentionally not an IPC command. It implements CRC16/MODBUS, low-byte-first CRC wire order, owned raw request/response frames, strict 4..=256-byte ADU and 1..=253-byte PDU limits, request-only broadcast address 0, unicast addresses 1..=247, reserved-address rejection, and exception response shape validation.

The caller must supply whether incoming bytes are a request or response because that role is not encoded on the RTU wire. The FC03/FC04 read-register layer additionally enforces unicast addresses 1..=247, quantity 1..=125, non-wrapping 16-bit register ranges, expected station/function matching, exact byte counts, big-endian UInt16 decoding, and five-byte exception responses. FC03 and FC04 keep separate external commands so capability control and diagnostics remain explicit.

Electron owns the open Windows serial handle. `SerialService.transact` permits only one in-flight transaction, flushes stale input before TX, installs the RX listener before writing, bounds write/drain/read time, assembles fragmented responses by the Rust-provided length contract, short-circuits five-byte exception frames, and always removes listeners and timers. Electron then returns the received bytes to Rust for CRC and protocol validation; JavaScript does not duplicate the CRC implementation.

## Security boundary

- The renderer has no Node.js access.
- `contextIsolation` and Chromium sandboxing are enabled.
- The preload bridge uses a fixed command allowlist. Phase 2.2/2.3 add only the high-level `read_holding_registers_once` and `read_input_registers_once` commands; arbitrary serial bytes are not exposed to the renderer.
- A development renderer URL is accepted only from the fixed loopback Vite endpoint on port 1420; packaged builds ignore that override.
- New windows and navigation away from the selected local entry are denied.
- Packaged builds use the fixed Rust Core path under application resources and cannot replace it through an environment variable.
- Rust Core stdout and stderr buffers are bounded; protocol output must remain valid JSONL.
- DTR and RTS default to `preserve`; opening a port does not imply successful device communication.
