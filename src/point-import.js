const MAX_IMPORT_BYTES = 2 * 1024 * 1024;
const MAX_POINTS = 10_000;
const MAX_JSON_NODES = 20_000;
const MAX_JSON_DEPTH = 4;
const MAX_CSV_LINE_BYTES = 16 * 1024;
const utf8Encoder = new TextEncoder();
const ALLOWED_DATA_TYPES = new Set([
  "Unsigned16", "Signed16", "Unsigned32", "Signed32", "Float32", "Float64",
  "BCD16", "BCD32", "ASCII", "Bit",
]);

function importError(code, message, details = {}) {
  const error = new Error(message);
  error.code = code;
  Object.assign(error, details);
  return error;
}

function boundedText(value, field, maxLength) {
  if (value === undefined || value === null) return "";
  if (typeof value !== "string") {
    throw importError("POINT_IMPORT_INVALID_FIELD", `${field} 必须是字符串`);
  }
  return value.trim().slice(0, maxLength);
}

function boundedInteger(value, field, min, max, fallback) {
  const number = typeof value === "number" ? value : Number(value);
  if (!Number.isInteger(number) || number < min || number > max) {
    if (arguments.length > 5) return fallback;
    throw importError("POINT_IMPORT_INVALID_FIELD", `${field} 必须是 ${min}-${max} 的整数`);
  }
  return number;
}

function validScale(value) {
  return /^[+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value) ? value : "1";
}

function normalizePoint(raw, index) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw importError("POINT_IMPORT_INVALID_POINT", `点位 #${index + 1} 必须是对象`);
  }
  const name = boundedText(raw.name, `点位 #${index + 1} name`, 128) || `导入点位${index + 1}`;
  const fc = boundedInteger(raw.fc, `点位 #${index + 1} fc`, 1, 16);
  if (![1, 2, 3, 4, 5, 6, 15, 16].includes(fc)) {
    throw importError("POINT_IMPORT_INVALID_FIELD", `点位 #${index + 1} 功能码不受支持`);
  }
  const unitId = boundedInteger(raw.unitId, `点位 #${index + 1} unitId`, 1, 247);
  const address = boundedInteger(raw.address, `点位 #${index + 1} address`, 0, 65_535);
  const maxQuantity = fc === 1 || fc === 2 ? 2000 : 125;
  const quantity = boundedInteger(raw.quantity, `点位 #${index + 1} quantity`, 1, maxQuantity);
  const dataType = ALLOWED_DATA_TYPES.has(raw.dataType) ? raw.dataType : "Unsigned16";
  return {
    name,
    unitId,
    fc,
    address,
    quantity,
    dataType,
    scale: validScale(boundedText(raw.scale, `点位 #${index + 1} scale`, 32) || "1"),
    unit: boundedText(raw.unit, `点位 #${index + 1} unit`, 32),
  };
}

function validateJsonTree(value, depth = 1, counter = { nodes: 0 }) {
  counter.nodes += 1;
  if (counter.nodes > MAX_JSON_NODES) {
    throw importError("POINT_IMPORT_TOO_COMPLEX", `导入 JSON 节点数超过 ${MAX_JSON_NODES}`);
  }
  if (depth > MAX_JSON_DEPTH) {
    throw importError("POINT_IMPORT_TOO_DEEP", `导入 JSON 嵌套深度超过 ${MAX_JSON_DEPTH}`);
  }
  if (Array.isArray(value)) {
    for (const item of value) validateJsonTree(item, depth + 1, counter);
    return;
  }
  if (value && typeof value === "object") {
    for (const key of Object.keys(value)) {
      if (key === "__proto__" || key === "constructor") {
        throw importError("POINT_IMPORT_UNSAFE_KEY", `导入 JSON 不允许字段 ${key}`);
      }
      validateJsonTree(value[key], depth + 1, counter);
    }
  }
}

function parseCsv(text) {
  const lines = text.split(/\r\n|\n|\r/).filter((line) => line.trim() && !/^name\s*,/i.test(line));
  if (lines.length > MAX_POINTS) {
    throw importError("POINT_IMPORT_TOO_MANY", `单次导入点位不能超过 ${MAX_POINTS}，当前 ${lines.length}`);
  }
  return lines.map((line, index) => {
    if (utf8EncodedLength(line) > MAX_CSV_LINE_BYTES) {
      throw importError("POINT_IMPORT_LINE_TOO_LONG", `第 ${index + 1} 行超过 ${MAX_CSV_LINE_BYTES} 字节`);
    }
    const cells = line.split(",").map((cell) => cell.trim());
    if (cells.length < 6 || cells.length > 8) {
      throw importError("POINT_IMPORT_INVALID_FIELD", `第 ${index + 1} 行应有 6-8 列`);
    }
    return normalizePoint({
      name: cells[0],
      unitId: cells[1],
      fc: cells[2],
      address: cells[3],
      quantity: cells[4],
      dataType: cells[5],
      ...(cells.length > 6 ? { scale: cells[6] } : {}),
      ...(cells.length > 7 ? { unit: cells[7] } : {}),
    }, index);
  });
}

export function parsePointImport({ fileName = "", byteSize = 0, text = "" } = {}) {
  if (byteSize > MAX_IMPORT_BYTES) {
    throw importError("POINT_IMPORT_TOO_LARGE", `导入文件超过 ${MAX_IMPORT_BYTES} 字节`);
  }
  if (utf8EncodedLength(text) > MAX_IMPORT_BYTES) {
    throw importError("POINT_IMPORT_TOO_LARGE", `导入内容超过 ${MAX_IMPORT_BYTES} 字节`);
  }
  if (fileName.toLowerCase().endsWith(".json")) {
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      throw importError("POINT_IMPORT_JSON_INVALID", `导入 JSON 解析失败：${error.message}`);
    }
    validateJsonTree(parsed);
    if (!Array.isArray(parsed)) {
      throw importError("POINT_IMPORT_INVALID_ROOT", "JSON 点表必须是数组");
    }
    if (parsed.length > MAX_POINTS) {
      throw importError("POINT_IMPORT_TOO_MANY", `单次导入点位不能超过 ${MAX_POINTS}，当前 ${parsed.length}`);
    }
    return parsed.map(normalizePoint);
  }
  return parseCsv(text);
}

function utf8EncodedLength(text) {
  return utf8Encoder.encode(text).length;
}

export function readPointImportFile(file) {
  if (!file) return Promise.resolve(null);
  if (file.size > MAX_IMPORT_BYTES) {
    return Promise.reject(importError("POINT_IMPORT_TOO_LARGE", `导入文件超过 ${MAX_IMPORT_BYTES} 字节`));
  }
  return file.text().then((text) => parsePointImport({
    fileName: file.name,
    byteSize: file.size,
    text,
  }));
}

export const POINT_IMPORT_LIMITS = Object.freeze({
  MAX_IMPORT_BYTES,
  MAX_POINTS,
  MAX_JSON_NODES,
  MAX_JSON_DEPTH,
  MAX_CSV_LINE_BYTES,
});
