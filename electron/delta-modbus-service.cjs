"use strict";

/**
 * Delta profile -> ��� Modbus ���һ��ֻ���߼���ȡ��
 *
 * The transport remains the existing Modbus master.  This service only turns
 * a Delta soft-device range into safe, contiguous Modbus requests and keeps
 * D4095/D4096 and M1535/M1536 from being merged accidentally.
 */

function deltaModbusError(message, code = "DELTA_MODBUS_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function integer(value, minimum, maximum, label) {
  const number = Number(value);
  if (!Number.isInteger(number) || number < minimum || number > maximum) {
    throw deltaModbusError(`${label}必须是 ${minimum}..${maximum} 的整数`, "DELTA_PARAM_INVALID");
  }
  return number;
}

function parseSoftAddress(series, address) {
  const text = String(address ?? "").trim().toUpperCase();
  const match = /^([A-Z]+)([0-9]+(?:\.[0-9]+)?)$/.exec(text);
  if (!match) throw deltaModbusError(`Delta 地址格式无效：${address}`, "DELTA_PARAM_INVALID");
  const prefix = match[1];
  const suffix = match[2];
  const dotted = suffix.includes(".");
  if (dotted && !["X", "Y"].includes(prefix)) throw deltaModbusError(`Delta ${prefix} 不支持点号位地址`, "DELTA_PARAM_INVALID");
  const [wordText, bitText] = suffix.split(".");
  const isOctal = series === "dvp" && ["X", "Y"].includes(prefix) && !dotted;
  const word = Number.parseInt(wordText, isOctal ? 8 : 10);
  if (!Number.isInteger(word) || word < 0) throw deltaModbusError(`Delta 地址编号无效：${address}`, "DELTA_PARAM_INVALID");
  if (!dotted) return { prefix, word, bit: null, isOctal, format: (next) => `${prefix}${isOctal ? next.toString(8) : next}` };
  const bit = Number(bitText);
  if (!Number.isInteger(bit) || bit < 0 || bit > 15) throw deltaModbusError(`Delta 位号必须是 0..15：${address}`, "DELTA_PARAM_INVALID");
  return { prefix, word, bit, isOctal: false, format: (linear) => `${prefix}${Math.floor(linear / 16)}.${linear % 16}` };
}

function nextSoftAddress(info, current) {
  if (info.bit !== null) return info.format(current + 1);
  return info.format(current + 1);
}

function maxQuantityFor(parsed) {
  return parsed?.isBit ? 2000 : 120;
}

function createDeltaModbusService({ request, readers = {} }) {
  if (typeof request !== "function") throw new TypeError("request must be a function");

  async function planRange({ series = "dvp", address = "D0", quantity = 1 } = {}) {
    const normalizedSeries = String(series).trim().toLowerCase() === "as" ? "as" : "dvp";
    const count = integer(quantity, 1, 2000, "Delta 读取数量");
    const start = parseSoftAddress(normalizedSeries, address);
    const segments = [];
    let currentAddress = String(address).trim().toUpperCase();
    let previous = null;
    let segment = null;
    for (let index = 0; index < count; index++) {
      const parsed = await request("delta_parse_address", { series: normalizedSeries, address: currentAddress });
      if (parsed?.ok === false) throw deltaModbusError(parsed.error?.message || "Delta 地址解析失败", parsed.error?.code || "DELTA_PARAM_INVALID", parsed.error?.details);
      const current = {
        logicalAddress: currentAddress,
        modbusAddress: Number(parsed.modbusAddress),
        area: parsed.area,
        readFunction: Number(parsed.readFunction),
        writeFunction: parsed.writeFunction == null ? null : Number(parsed.writeFunction),
        isBit: Boolean(parsed.isBit),
        readOnly: Boolean(parsed.readOnly),
      };
      const contiguous = previous && current.area === previous.area && current.readFunction === previous.readFunction && current.isBit === previous.isBit && current.modbusAddress === previous.modbusAddress + 1;
      const segmentLimit = maxQuantityFor(current);
      if (!segment || !contiguous || segment.quantity >= segmentLimit) {
        segment = {
          logicalStart: current.logicalAddress,
          modbusStart: current.modbusAddress,
          area: current.area,
          readFunction: current.readFunction,
          isBit: current.isBit,
          readOnly: current.readOnly,
          quantity: 0,
        };
        segments.push(segment);
      }
      segment.quantity += 1;
      previous = current;
      if (index + 1 < count) {
        const nextInfo = parseSoftAddress(normalizedSeries, currentAddress);
        const logical = nextInfo.bit === null ? nextInfo.word : nextInfo.word * 16 + nextInfo.bit;
        currentAddress = nextSoftAddress(nextInfo, logical);
      }
    }
    return { ok: true, series: normalizedSeries.toUpperCase(), startAddress: String(address).trim().toUpperCase(), quantity: count, segments };
  }

  async function read({ series = "dvp", address = "D0", quantity = 1, unitId = 1, transport = "rtu", timeoutMs, model = "", firmware = "" } = {}) {
    const unit = integer(unitId, 0, 247, "Modbus 站号");
    if (!["rtu", "ascii"].includes(String(transport).toLowerCase())) throw deltaModbusError("Delta 首轮共享串口只读支持 RTU 或 ASCII", "DELTA_TRANSPORT_UNSUPPORTED");
    const plan = await planRange({ series, address, quantity });
    const values = [];
    const completed = [];
    for (const segment of plan.segments) {
      const args = { unitId: unit, startAddress: segment.modbusStart, quantity: segment.quantity, transport: String(transport).toLowerCase(), timeoutMs };
      const reader = segment.readFunction === 1 ? readers.readCoils : segment.readFunction === 2 ? readers.readDiscreteInputs : segment.readFunction === 4 ? readers.readInputRegisters : readers.readHoldingRegisters;
      if (typeof reader !== "function") throw deltaModbusError("Delta Modbus reader 未配置", "DELTA_READER_UNAVAILABLE");
      const result = await reader(args);
      if (!result?.ok) {
        return { ok: false, readOnly: true, plan, completedSegments: completed, failedSegment: segment, error: result?.error ?? { code: "DELTA_READ_FAILED", message: "Modbus 读取失败" } };
      }
      const data = segment.isBit ? (result.coils ?? []) : (result.registers ?? []);
      if (!Array.isArray(data) || data.length !== segment.quantity) {
        return { ok: false, readOnly: true, plan, completedSegments: completed, failedSegment: segment, error: { code: "DELTA_LENGTH_MISMATCH", message: "Delta 分段响应数量与请求不一致", details: { expected: segment.quantity, actual: data.length } } };
      }
      values.push(...data);
      completed.push({ ...segment, result: { tx: result.tx ?? [], rx: result.rx ?? [], elapsedMs: result.elapsedMs ?? null } });
    }
    return {
      ok: true,
      protocol: "delta-modbus-profile",
      readOnly: true,
      series: plan.series,
      address: plan.startAddress,
      quantity: plan.quantity,
      values,
      segments: completed,
      deviceRecord: {
        model: String(model ?? "").trim() || null,
        firmware: String(firmware ?? "").trim() || null,
        unitId: unit,
        transport: String(transport).toLowerCase(),
        recordedAt: new Date().toISOString(),
        source: "operator-input-and-modbus-profile",
      },
    };
  }

  return { planRange, read };
}

module.exports = { createDeltaModbusService, parseSoftAddress, deltaModbusError };
