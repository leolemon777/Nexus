const { SerialPort } = require("serialport");

const ALLOWED_DATA_BITS = new Set([5, 6, 7, 8]);
const ALLOWED_PARITY = new Set(["none", "odd", "even"]);
const ALLOWED_STOP_BITS = new Set(["1", "2"]);
const ALLOWED_FLOW_CONTROL = new Set(["none", "rts-cts", "xon-xoff"]);
const ALLOWED_LINE_MODES = new Set(["preserve", "high", "low"]);
const ALLOWED_RTS_MODES = new Set(["preserve", "high", "low", "auto-toggle"]);

function assertIntegerInRange(value, minimum, maximum, label) {
  if (!Number.isInteger(value) || value < minimum || value > maximum) {
    throw new Error(`${label}必须是 ${minimum} 到 ${maximum} 之间的整数。`);
  }
}

function validateConfig(input) {
  const config = {
    portName: String(input?.portName ?? "").trim(),
    baudRate: Number(input?.baudRate),
    dataBits: Number(input?.dataBits),
    parity: String(input?.parity ?? ""),
    stopBits: String(input?.stopBits ?? ""),
    flowControl: String(input?.flowControl ?? ""),
    readTimeoutMs: Number(input?.readTimeoutMs),
    writeTimeoutMs: Number(input?.writeTimeoutMs),
    dtrMode: String(input?.dtrMode ?? ""),
    rtsMode: String(input?.rtsMode ?? ""),
  };

  if (!/^COM[1-9]\d*$/i.test(config.portName)) {
    throw new Error("串口名称必须是有效的 Windows COM 端口，例如 COM3。");
  }
  assertIntegerInRange(config.baudRate, 1, 12_000_000, "波特率");
  if (!ALLOWED_DATA_BITS.has(config.dataBits)) throw new Error("数据位只允许 5、6、7 或 8。");
  if (!ALLOWED_PARITY.has(config.parity)) throw new Error("当前传输层只支持无校验、奇校验和偶校验。");
  if (!ALLOWED_STOP_BITS.has(config.stopBits)) throw new Error("当前传输层只支持 1 或 2 个停止位。");
  if (!ALLOWED_FLOW_CONTROL.has(config.flowControl)) throw new Error("流控参数无效。");
  assertIntegerInRange(config.readTimeoutMs, 1, 600_000, "读取超时");
  assertIntegerInRange(config.writeTimeoutMs, 1, 600_000, "写入超时");
  if (!ALLOWED_LINE_MODES.has(config.dtrMode)) throw new Error("DTR 控制模式无效。");
  if (!ALLOWED_RTS_MODES.has(config.rtsMode)) throw new Error("RTS 控制模式无效。");
  if (config.flowControl === "rts-cts" && config.rtsMode !== "preserve") {
    throw new Error("启用 RTS/CTS 流控时，RTS 必须由驱动管理。");
  }

  return config;
}

function naturalPortKey(name) {
  const match = /^COM(\d+)$/i.exec(name);
  return match ? [0, Number(match[1]), name.toUpperCase()] : [1, Number.MAX_SAFE_INTEGER, name.toUpperCase()];
}

function comparePortNames(left, right) {
  const a = naturalPortKey(left);
  const b = naturalPortKey(right);
  return a[0] - b[0] || a[1] - b[1] || a[2].localeCompare(b[2]);
}

function callbackAsPromise(action) {
  return new Promise((resolve, reject) => {
    action((error) => (error ? reject(error) : resolve()));
  });
}

class SerialServiceError extends Error {
  constructor(message, { code = "SERIAL_SERVICE_ERROR", details, cause } = {}) {
    super(message, cause ? { cause } : undefined);
    this.name = "SerialServiceError";
    this.code = code;
    if (details !== undefined) this.details = details;
  }
}

function withTimeout(promise, timeoutMs, createError) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(createError()), timeoutMs);
    timer.unref?.();
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function createResponseCollector(port, { expectedResponseLength, exceptionResponseLength, timeoutMs, framing = "rtu" }) {
  let cancel;
  const promise = new Promise((resolve, reject) => {
    const chunks = [];
    let receivedLength = 0;
    let settled = false;

    const received = () => Buffer.concat(chunks, receivedLength);
    const finish = (action) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      port.removeListener("data", onData);
      port.removeListener("error", onError);
      action();
    };
    const fail = (error) => finish(() => reject(error));
    const onError = (error) => {
      fail(
        new SerialServiceError(`串口接收失败：${error.message}`, {
          code: "SERIAL_IO_ERROR",
          cause: error,
          details: { rx: [...received()] },
        }),
      );
    };
    const onData = (chunk) => {
      const bytes = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      if (!bytes.length) return;
      chunks.push(bytes);
      receivedLength += bytes.length;
      const frame = received();
      // ASCII 帧:以 ':'(0x3A) 起始,以 LF(0x0A) 结尾即视为完整帧
      if (framing === "ascii") {
        if (frame.length >= 1 && frame[0] === 0x3a && frame[frame.length - 1] === 0x0a) {
          finish(() => resolve(frame));
        }
        return;
      }
      // USS 变频器:STX(0x02) 起,BCC(XOR 1B) 结束;LGE 定长
      if (framing === "uss") {
        if ((frame[0] === 0x02 || frame[0] === 0x03) && frame.length >= 4) {
          const total = 2 + frame[1] + 1; // STX + LGE + LGE 字节 + BCC
          if (frame.length >= total) finish(() => resolve(frame.subarray(0, total)));
        }
        return;
      }
      // 3964R/RK512:STX 起,DLE(0x10)+ETX(0x03)+BCC 结束(注意 DLE 转义)
      if (framing === "rk512") {
        if (frame[0] === 0x10 && frame.length >= 1) finish(() => resolve(frame.subarray(0, 1)));
        if (frame[0] === 0x02 && frame.length >= 4) {
          for (let i = frame.length - 3; i >= 1; i--) {
            if (frame[i] === 0x10 && frame[i + 1] === 0x03) {
              if (frame.length >= i + 3) finish(() => resolve(frame.subarray(0, i + 3)));
              return;
            }
          }
        }
        return;
      }
      // PPI 帧三种结束:E5 单字节确认 / 10 .. 16 短帧 / 68 .. 16 SD2 长帧
      if (framing === "ppi") {
        if (frame.length === 1 && frame[0] === 0xE5) {
          finish(() => resolve(frame)); // SC 确认(调用方继续双拍)
        } else if (frame[0] === 0x68) {
          const etx = frame.indexOf(0x16, 5);
          if (etx >= 0) finish(() => resolve(frame.subarray(0, etx + 1)));
        } else if (frame[0] === 0x10 && frame.length >= 6) {
          finish(() => resolve(frame.subarray(0, 6))); // SA 短帧
        }
        return;
      }
      // MC C24 串口帧(格式1 ASCII):ETX+和校验(2)+CR LF 结尾
      if (framing === "mc-c24") {
        const etx = frame.indexOf(0x03, 1);
        if (etx >= 0 && frame.length >= etx + 5) {
          finish(() => resolve(frame.subarray(0, etx + 5)));
        }
        return;
      }
      // FX 三菱串口协议帧:按结尾控制字符判定
      // - ACK(0x06) / NAK(0x15) 单帧即完成(NAK 后再等 1 字节错误码)
      // - STX(0x02)...ETX(0x03) + 2 字节和校验 [+ 可选 CR LF]
      if (framing === "fx") {
        const first = frame[0];
        if (first === 0x06) {
          finish(() => resolve(frame)); // ACK
          return;
        }
        if (first === 0x15) {
          if (frame.length >= 2) {
            finish(() => resolve(frame)); // NAK + 错误码
          }
          return;
        }
        if (first === 0x02) {
          // 找 ETX(数据内不会有:FX 数据是 ASCII hex)
          const etx = frame.indexOf(0x03, 1);
          if (etx >= 0 && frame.length >= etx + 3) {
            finish(() => resolve(frame.subarray(0, etx + 3 + 2))); // STX..ETX+SUM(+CRLF 忽略)
          }
        }
        return;
      }
      const isException = frame.length >= 2 && (frame[1] & 0x80) !== 0;
      const targetLength = isException ? exceptionResponseLength : expectedResponseLength;
      if (frame.length < targetLength) return;
      if (frame.length > targetLength) {
        fail(
          new SerialServiceError(
            `串口响应超过预期长度：期望 ${targetLength} 字节，收到 ${frame.length} 字节。`,
            {
              code: "SERIAL_FRAME_TOO_LONG",
              details: { expectedLength: targetLength, rx: [...frame] },
            },
          ),
        );
        return;
      }
      finish(() => resolve(frame));
    };
    const timer = setTimeout(() => {
      const frame = received();
      fail(
        new SerialServiceError(
          frame.length
            ? `等待 Modbus 响应超时，已收到 ${frame.length} 字节。`
            : "等待 Modbus 响应超时，设备没有返回数据。",
          {
            code: "SERIAL_RESPONSE_TIMEOUT",
            details: { timeoutMs, rx: [...frame] },
          },
        ),
      );
    }, timeoutMs);
    timer.unref?.();

    port.on("data", onData);
    port.on("error", onError);
    cancel = (error) => fail(error);
  });
  return { promise, cancel };
}

class SerialService {
  constructor({ SerialPortImpl = SerialPort } = {}) {
    this.SerialPortImpl = SerialPortImpl;
    this.current = null;
    this.transactionActive = false;
  }

  async listPorts() {
    const ports = await this.SerialPortImpl.list();
    return ports
      .map((port) => {
        const details = [port.manufacturer, port.serialNumber].filter(Boolean).join(" · ");
        return {
          name: port.path,
          displayName: details ? `${port.path} · ${details}` : port.path,
          manufacturer: port.manufacturer ?? null,
          serialNumber: port.serialNumber ?? null,
          vendorId: port.vendorId ?? null,
          productId: port.productId ?? null,
        };
      })
      .sort((left, right) => comparePortNames(left.name, right.name));
  }

  getStatus() {
    const isOpen = Boolean(this.current?.port?.isOpen);
    return { isOpen, config: isOpen ? this.current.config : null };
  }

  async open(input) {
    if (this.transactionActive) {
      throw new SerialServiceError("串口事务正在执行，暂时不能重新打开端口。", {
        code: "SERIAL_BUSY",
      });
    }
    if (this.current?.port?.isOpen) {
      throw new Error(`串口 ${this.current.config.portName} 已经打开，请先关闭。`);
    }

    const config = validateConfig(input);
    const port = new this.SerialPortImpl({
      path: config.portName,
      baudRate: config.baudRate,
      dataBits: config.dataBits,
      parity: config.parity,
      stopBits: Number(config.stopBits),
      rtscts: config.flowControl === "rts-cts",
      xon: config.flowControl === "xon-xoff",
      xoff: config.flowControl === "xon-xoff",
      autoOpen: false,
    });

    port.on("error", (error) => {
      console.error(`[serial:${config.portName}] ${error.message}`);
    });
    // USB 转换器被拔出时 Windows 驱动通常只发 close 不发 error——必须监听,
    // 否则 current 残留:SERIAL_BUSY 拒绝一切后续操作,用户被锁死到超时。
    port.on("close", () => {
      if (this.current?.port === port) {
        console.error(`[serial:${config.portName}] 串口已断开(可能被拔出)`);
        this.current = null;
        this.transactionActive = false;
      }
    });

    await callbackAsPromise((done) => port.open(done));
    try {
      const lineState = {};
      if (config.dtrMode !== "preserve") lineState.dtr = config.dtrMode === "high";
      if (config.flowControl !== "rts-cts" && config.rtsMode !== "preserve") {
        lineState.rts = config.rtsMode === "high";
      }
      if (Object.keys(lineState).length) {
        await callbackAsPromise((done) => port.set(lineState, done));
      }
    } catch (error) {
      await callbackAsPromise((done) => port.close(done)).catch(() => {});
      throw error;
    }

    this.current = { port, config };
    return this.getStatus();
  }

  async close() {
    if (this.transactionActive) {
      throw new SerialServiceError("串口事务正在执行，请等待本次读取结束。", {
        code: "SERIAL_BUSY",
      });
    }
    const active = this.current;
    this.current = null;
    if (active?.port?.isOpen) {
      await callbackAsPromise((done) => active.port.close(done));
    }
    return this.getStatus();
  }

  async transact({ request, expectedResponseLength, exceptionResponseLength = 5, timeoutMs, framing = "rtu", awaitResponse = true }) {
    const active = this.current;
    if (!active?.port?.isOpen) {
      throw new SerialServiceError("串口尚未打开，无法执行 Modbus 事务。", {
        code: "SERIAL_NOT_OPEN",
      });
    }
    if (this.transactionActive) {
      throw new SerialServiceError("已有 Modbus 事务正在执行。", { code: "SERIAL_BUSY" });
    }

    const tx = Buffer.isBuffer(request) ? Buffer.from(request) : Buffer.from(request ?? []);
    if (framing === "fx") {
      // FX 帧:ENQ(1)+站号(2)+PC号(2)+命令(2)+延时(1)+数据+校验(2) 最短 10;STX 帧类似
      if (tx.length < 8 || tx.length > 512) {
        throw new SerialServiceError("FX 请求长度必须在 8 到 512 字节之间。", {
          code: "INVALID_TRANSACTION",
          details: { field: "request", length: tx.length },
        });
      }
    } else if (framing === "ascii") {
      if (tx.length < 3 || tx.length > 512) {
        throw new SerialServiceError("Modbus ASCII 请求长度必须在 3 到 512 字节之间。", {
          code: "INVALID_TRANSACTION",
          details: { field: "request", length: tx.length },
        });
      }
    } else if (tx.length < 4 || tx.length > 256) {
      throw new SerialServiceError("Modbus RTU 请求长度必须在 4 到 256 字节之间。", {
        code: "INVALID_TRANSACTION",
        details: { field: "request", length: tx.length },
      });
    }
    if (awaitResponse && framing !== "ascii" && framing !== "fx" && framing !== "mc-c24") {
      assertIntegerInRange(expectedResponseLength, 5, 256, "正常响应长度");
      assertIntegerInRange(exceptionResponseLength, 5, 5, "异常响应长度");
    }
    if (awaitResponse) {
      assertIntegerInRange(timeoutMs, 1, 600_000, "事务超时");
    }

    this.transactionActive = true;
    const startedAt = Date.now();
    let collector = null;
    try {
      if (typeof active.port.flush === "function") {
        await withTimeout(
          callbackAsPromise((done) => active.port.flush(done)),
          active.config.writeTimeoutMs,
          () =>
            new SerialServiceError("清理串口接收缓冲区超时。", {
              code: "SERIAL_FLUSH_TIMEOUT",
              details: { timeoutMs: active.config.writeTimeoutMs },
            }),
        );
      }

      if (awaitResponse) {
        collector = createResponseCollector(active.port, {
          expectedResponseLength,
          exceptionResponseLength,
          timeoutMs,
          framing,
        });
        collector.promise.catch(() => {});
      }

      // G7: RTS auto-toggle for RS-485(发送前拉高,发送后拉低)
      const needsRtsToggle = active.config.rtsMode === "auto-toggle";
      if (needsRtsToggle) {
        try { active.port.set({ rts: true }); } catch { /* 忽略 */ }
      }

      await withTimeout(
        callbackAsPromise((done) => active.port.write(tx, done)),
        active.config.writeTimeoutMs,
        () =>
          new SerialServiceError("写入 Modbus 请求超时。", {
            code: "SERIAL_WRITE_TIMEOUT",
            details: { timeoutMs: active.config.writeTimeoutMs, tx: [...tx] },
          }),
      );
      if (typeof active.port.drain === "function") {
        await withTimeout(
          callbackAsPromise((done) => active.port.drain(done)),
          active.config.writeTimeoutMs,
          () =>
            new SerialServiceError("等待串口发送完成超时。", {
              code: "SERIAL_DRAIN_TIMEOUT",
              details: { timeoutMs: active.config.writeTimeoutMs, tx: [...tx] },
            }),
        );
      }
      // G7: RTS auto-toggle — drain 后立即拉低 RTS(切换到接收模式)
      if (needsRtsToggle) {
        try { active.port.set({ rts: false }); } catch { /* 忽略 */ }
      }

      const rx = awaitResponse ? await collector.promise : [];
      return {
        tx: [...tx],
        rx: [...rx],
        elapsedMs: Date.now() - startedAt,
        broadcast: !awaitResponse,
      };
    } catch (error) {
      // RTS auto-toggle 异常路径复位:485 收发器保持发送态会持续占用总线
      if (active.config.rtsMode === "auto-toggle") {
        try { active.port.set({ rts: false }); } catch { /* 忽略 */ }
      }
      collector?.cancel(error);
      if (collector) await collector.promise.catch(() => {});
      if (error instanceof SerialServiceError) throw error;
      throw new SerialServiceError(`串口事务失败：${error.message}`, {
        code: "SERIAL_IO_ERROR",
        cause: error,
        details: { tx: [...tx] },
      });
    } finally {
      this.transactionActive = false;
    }
  }
}

module.exports = {
  SerialService,
  SerialServiceError,
  comparePortNames,
  naturalPortKey,
  validateConfig,
};
