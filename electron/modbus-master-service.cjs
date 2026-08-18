function publicError(error, fallbackCode = "READ_REGISTERS_FAILED") {
  return {
    code: typeof error?.code === "string" ? error.code : fallbackCode,
    message: error instanceof Error ? error.message : String(error),
    details: error?.details ?? null,
  };
}

async function readHoldingRegistersOnce(
  { rustCore, serialService, ensureRustCore },
  args,
) {
  const ascii = args?.transport === "ascii";
  return readRegistersOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiReadHoldingRegisters(payload) : core.buildReadHoldingRegisters(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiReadHoldingRegisters(payload) : core.parseReadHoldingRegisters(payload),
      fallbackCode: "READ_HOLDING_REGISTERS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function readInputRegistersOnce(
  { rustCore, serialService, ensureRustCore },
  args,
) {
  const ascii = args?.transport === "ascii";
  return readRegistersOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiReadInputRegisters(payload) : core.buildReadInputRegisters(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiReadInputRegisters(payload) : core.parseReadInputRegisters(payload),
      fallbackCode: "READ_INPUT_REGISTERS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function readCoilsOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return readBitsOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiReadCoils(payload) : core.buildReadCoils(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiReadCoils(payload) : core.parseReadCoils(payload),
      fallbackCode: "READ_COILS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function readDiscreteInputsOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return readBitsOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiReadDiscreteInputs(payload) : core.buildReadDiscreteInputs(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiReadDiscreteInputs(payload) : core.parseReadDiscreteInputs(payload),
      fallbackCode: "READ_DISCRETE_INPUTS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function writeSingleCoilOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return writeOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiWriteSingleCoil(payload) : core.buildWriteSingleCoil(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiWriteSingleCoil(payload) : core.parseWriteSingleCoil(payload),
      fallbackCode: "WRITE_SINGLE_COIL_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function writeSingleRegisterOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return writeOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiWriteSingleRegister(payload) : core.buildWriteSingleRegister(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiWriteSingleRegister(payload) : core.parseWriteSingleRegister(payload),
      fallbackCode: "WRITE_SINGLE_REGISTER_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function writeMultipleCoilsOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return writeOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiWriteMultipleCoils(payload) : core.buildWriteMultipleCoils(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiWriteMultipleCoils(payload) : core.parseWriteMultipleCoils(payload),
      fallbackCode: "WRITE_MULTIPLE_COILS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

async function writeMultipleRegistersOnce({ rustCore, serialService, ensureRustCore }, args) {
  const ascii = args?.transport === "ascii";
  return writeOnce(
    { rustCore, serialService, ensureRustCore },
    args,
    {
      build: (core, payload) =>
        ascii ? core.buildAsciiWriteMultipleRegisters(payload) : core.buildWriteMultipleRegisters(payload),
      parse: (core, payload) =>
        ascii ? core.parseAsciiWriteMultipleRegisters(payload) : core.parseWriteMultipleRegisters(payload),
      fallbackCode: "WRITE_MULTIPLE_REGISTERS_FAILED",
      framing: ascii ? "ascii" : "rtu",
    },
  );
}

// --- TCP 端到端(Rust 持 socket,无需 serial transact) ---

async function openTcpConnection({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.openTcpConnection(args);
}

async function openUdpConnection({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.openUdpConnection(args);
}

async function closeConnection({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.closeConnection(args);
}

async function tcpReadCoils({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpReadCoils(args);
}

async function tcpReadDiscreteInputs({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpReadDiscreteInputs(args);
}

async function tcpReadHoldingRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpReadHoldingRegisters(args);
}

async function tcpReadInputRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpReadInputRegisters(args);
}

async function tcpWriteSingleCoil({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpWriteSingleCoil(args);
}

async function tcpWriteSingleRegister({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpWriteSingleRegister(args);
}

async function tcpWriteMultipleCoils({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpWriteMultipleCoils(args);
}

async function tcpWriteMultipleRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.tcpWriteMultipleRegisters(args);
}

// --- UDP 端到端(同 TCP 版本,底层用 transact_udp) ---

async function udpReadCoils({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpReadCoils(args);
}
async function udpReadDiscreteInputs({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpReadDiscreteInputs(args);
}
async function udpReadHoldingRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpReadHoldingRegisters(args);
}
async function udpReadInputRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpReadInputRegisters(args);
}
async function udpWriteSingleCoil({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpWriteSingleCoil(args);
}
async function udpWriteSingleRegister({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpWriteSingleRegister(args);
}
async function udpWriteMultipleCoils({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpWriteMultipleCoils(args);
}
async function udpWriteMultipleRegisters({ ensureRustCore }, args) {
  const core = await ensureRustCore();
  return core.udpWriteMultipleRegisters(args);
}

async function readRegistersOnce(
  { rustCore, serialService, ensureRustCore },
  args,
  { build, parse, fallbackCode, framing = "rtu" },
) {
  const startedAt = Date.now();
  let built = null;
  let transaction = null;
  try {
    const liveRustCore = (await ensureRustCore()) ?? rustCore;
    const unitId = args?.unitId;
    const startAddress = args?.startAddress;
    const quantity = args?.quantity;
    built = await build(liveRustCore, { unitId, startAddress, quantity });

    const serialStatus = serialService.getStatus();
    const timeoutMs = args?.timeoutMs ?? serialStatus.config?.readTimeoutMs;
    transaction = await serialService.transact({
      request: built.adu,
      expectedResponseLength: built.expectedResponseLength,
      exceptionResponseLength: built.exceptionResponseLength,
      timeoutMs,
      framing,
    });

    const parsed = await parse(liveRustCore, {
      response: transaction.rx,
      unitId,
      quantity,
    });
    if (parsed.status === "exception") {
      return {
        ok: false,
        tx: transaction.tx,
        rx: transaction.rx,
        elapsedMs: transaction.elapsedMs,
        crcValid: true,
        registers: [],
        error: {
          code: "MODBUS_EXCEPTION",
          message: `从站返回异常 0x${parsed.exceptionCode.toString(16).padStart(2, "0").toUpperCase()}：${parsed.exceptionName}`,
          details: {
            exceptionCode: parsed.exceptionCode,
            exceptionName: parsed.exceptionName,
          },
        },
      };
    }

    return {
      ok: true,
      tx: transaction.tx,
      rx: transaction.rx,
      elapsedMs: transaction.elapsedMs,
      crcValid: true,
      registers: parsed.registers,
      error: null,
    };
  } catch (error) {
    const details = error?.details ?? {};
    return {
      ok: false,
      tx: transaction?.tx ?? built?.adu ?? details.tx ?? [],
      rx: transaction?.rx ?? details.rx ?? [],
      elapsedMs: transaction?.elapsedMs ?? Date.now() - startedAt,
      crcValid: error?.code === "CRC_MISMATCH" ? false : null,
      registers: [],
      error: publicError(error, fallbackCode),
    };
  }
}

async function readBitsOnce(
  { rustCore, serialService, ensureRustCore },
  args,
  { build, parse, fallbackCode, framing = "rtu" },
) {
  const startedAt = Date.now();
  let built = null;
  let transaction = null;
  try {
    const liveRustCore = (await ensureRustCore()) ?? rustCore;
    const { unitId, startAddress, quantity } = args ?? {};
    built = await build(liveRustCore, { unitId, startAddress, quantity });
    const serialStatus = serialService.getStatus();
    const timeoutMs = args?.timeoutMs ?? serialStatus.config?.readTimeoutMs;
    transaction = await serialService.transact({
      request: built.adu,
      expectedResponseLength: built.expectedResponseLength,
      exceptionResponseLength: built.exceptionResponseLength,
      timeoutMs,
      framing,
    });
    const parsed = await parse(liveRustCore, { response: transaction.rx, unitId, quantity });
    if (parsed.status === "exception") {
      return {
        ok: false,
        tx: transaction.tx,
        rx: transaction.rx,
        elapsedMs: transaction.elapsedMs,
        crcValid: true,
        coils: [],
        error: {
          code: "MODBUS_EXCEPTION",
          message: `从站返回异常 0x${parsed.exceptionCode.toString(16).padStart(2, "0").toUpperCase()}:${parsed.exceptionName}`,
          details: { exceptionCode: parsed.exceptionCode, exceptionName: parsed.exceptionName },
        },
      };
    }
    return {
      ok: true,
      tx: transaction.tx,
      rx: transaction.rx,
      elapsedMs: transaction.elapsedMs,
      crcValid: true,
      coils: parsed.coils,
      error: null,
    };
  } catch (error) {
    const details = error?.details ?? {};
    return {
      ok: false,
      tx: transaction?.tx ?? built?.adu ?? details.tx ?? [],
      rx: transaction?.rx ?? details.rx ?? [],
      elapsedMs: transaction?.elapsedMs ?? Date.now() - startedAt,
      crcValid: error?.code === "CRC_MISMATCH" ? false : null,
      coils: [],
      error: publicError(error, fallbackCode),
    };
  }
}

async function writeOnce(
  { rustCore, serialService, ensureRustCore },
  args,
  { build, parse, fallbackCode, framing = "rtu" },
) {
  const startedAt = Date.now();
  let built = null;
  let transaction = null;
  try {
    const liveRustCore = (await ensureRustCore()) ?? rustCore;
    const { unitId, address, value, values } = args ?? {};
    const buildPayload = values !== undefined ? { unitId, address, values } : { unitId, address, value };
    built = await build(liveRustCore, buildPayload);

    // 广播写(unit 0)不期待响应,发完即返回
    if (built.expectResponse === false) {
      const serialStatus = serialService.getStatus();
      const timeoutMs = args?.timeoutMs ?? serialStatus.config?.writeTimeoutMs;
      // 发送但不等待响应(flush + write + drain);失败必须上抛——
      // 旧实现用 1 字节期望长度 + catch 吞错,帧根本没写入串口也报"广播成功"(静默数据丢失)
      await serialService.transact({
        request: built.adu,
        timeoutMs: Math.min(timeoutMs ?? 1000, 5000),
        framing,
        awaitResponse: false,
      });
      return {
        ok: true,
        tx: built.adu,
        rx: [],
        elapsedMs: Date.now() - startedAt,
        crcValid: true,
        broadcast: true,
        error: null,
      };
    }

    const serialStatus = serialService.getStatus();
    const timeoutMs = args?.timeoutMs ?? serialStatus.config?.writeTimeoutMs;
    transaction = await serialService.transact({
      request: built.adu,
      expectedResponseLength: built.expectedResponseLength,
      exceptionResponseLength: built.exceptionResponseLength,
      timeoutMs,
      framing,
    });

    const parsed = await parse(liveRustCore, { response: transaction.rx, unitId });
    if (parsed.status === "exception") {
      return {
        ok: false,
        tx: transaction.tx,
        rx: transaction.rx,
        elapsedMs: transaction.elapsedMs,
        crcValid: true,
        error: {
          code: "MODBUS_EXCEPTION",
          message: `从站返回异常 0x${parsed.exceptionCode.toString(16).padStart(2, "0").toUpperCase()}:${parsed.exceptionName}`,
          details: { exceptionCode: parsed.exceptionCode, exceptionName: parsed.exceptionName },
        },
      };
    }
    return {
      ok: true,
      tx: transaction.tx,
      rx: transaction.rx,
      elapsedMs: transaction.elapsedMs,
      crcValid: true,
      error: null,
    };
  } catch (error) {
    const details = error?.details ?? {};
    return {
      ok: false,
      tx: transaction?.tx ?? built?.adu ?? details.tx ?? [],
      rx: transaction?.rx ?? details.rx ?? [],
      elapsedMs: transaction?.elapsedMs ?? Date.now() - startedAt,
      crcValid: error?.code === "CRC_MISMATCH" ? false : null,
      error: publicError(error, fallbackCode),
    };
  }
}

module.exports = {
  publicError,
  readHoldingRegistersOnce,
  readInputRegistersOnce,
  readCoilsOnce,
  readDiscreteInputsOnce,
  writeSingleCoilOnce,
  writeSingleRegisterOnce,
  writeMultipleCoilsOnce,
  writeMultipleRegistersOnce,
  tcpReadCoils,
  tcpReadDiscreteInputs,
  tcpReadHoldingRegisters,
  tcpReadInputRegisters,
  tcpWriteSingleCoil,
  tcpWriteSingleRegister,
  tcpWriteMultipleCoils,
  tcpWriteMultipleRegisters,
  udpReadCoils,
  udpReadDiscreteInputs,
  udpReadHoldingRegisters,
  udpReadInputRegisters,
  udpWriteSingleCoil,
  udpWriteSingleRegister,
  udpWriteMultipleCoils,
  udpWriteMultipleRegisters,
  openTcpConnection,
  openUdpConnection,
  closeConnection,
};
