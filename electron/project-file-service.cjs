"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { randomUUID } = require("node:crypto");

const PROJECT_FORMAT = "nexus-project";
const LEGACY_PROJECT_SCHEMA_VERSION = 0;
const PROJECT_SCHEMA_VERSION = 1;
const MAX_PROJECT_BYTES = 5 * 1024 * 1024;
const MAX_JSON_DEPTH = 16;
const MAX_JSON_NODES = 120_000;
const MAX_TOP_LEVEL_KEYS = 32;
const MAX_SESSIONS = 20;
const MAX_POINTS = 10_000;
const MAX_TREND_SELECTIONS = 32;
const MAX_COMMANDS = 200;
const SANITIZED_PROJECT_NAME = "Nexus脱敏项目";
const SANITIZED_TCP_HOST = "redacted.invalid";
const SAFE_PROJECT_DATA_TYPES = new Set([
  "Unsigned16", "Signed16", "Unsigned32", "Signed32", "Float32", "Float64",
  "BCD16", "BCD32", "ASCII", "Bit",
]);
const SENSITIVE_PROJECT_KEY_RE = /(password|passwd|secret|token|authorization|cookie|credential|private[_-]?key|api[_-]?key|client[_-]?secret|certificate[_-]?key)/i;
const PROJECT_MIGRATIONS = {
  [LEGACY_PROJECT_SCHEMA_VERSION]: {
    targetVersion: PROJECT_SCHEMA_VERSION,
    summary: "补充 schemaVersion、workspace 默认结构和 v1 字段校验",
    migrate(raw) {
      const output = { ...raw, schemaVersion: PROJECT_SCHEMA_VERSION };
      output.workspace = output.workspace && typeof output.workspace === "object"
        ? { ...output.workspace }
        : {};
      return output;
    },
  },
};
const ALLOWED_VIEWS = new Set(["master", "slave", "debug", "parser", "gx3", "melsec", "interfaces", "siemens", "omron"]);
const ALLOWED_TRANSPORTS = new Set(["rtu", "ascii", "tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"]);
const ALLOWED_FC = new Set([1, 2, 3, 4, 5, 6, 15, 16]);
const ALLOWED_HELP_SOURCES = new Set([
  "master", "slave", "debug", "melsec", "siemens", "omron",
  "allen-bradley", "beckhoff", "keyence", "ls-electric", "panasonic",
  "delta", "inovance", "xinje", "fatek", "fuji", "ge", "mqtt", "iec",
  "dnp", "dlt", "cjt", "bacnet", "knx",
]);

function projectError(code, message, details = {}) {
  const error = new Error(message);
  error.code = code;
  Object.assign(error, details);
  return error;
}

function validateJsonTree(value, depth = 0, counter = { nodes: 0 }) {
  counter.nodes += 1;
  if (counter.nodes > MAX_JSON_NODES) {
    throw projectError("PROJECT_TOO_COMPLEX", `项目 JSON 节点数超过 ${MAX_JSON_NODES} 限制`);
  }
  if (depth > MAX_JSON_DEPTH) {
    throw projectError("PROJECT_TOO_DEEP", `项目 JSON 嵌套深度超过 ${MAX_JSON_DEPTH} 层`);
  }
  if (Array.isArray(value)) {
    for (const item of value) validateJsonTree(item, depth + 1, counter);
    return;
  }
  if (value && typeof value === "object") {
    for (const item of Object.values(value)) validateJsonTree(item, depth + 1, counter);
  }
}

function rejectSensitiveProjectKeys(value, fieldPath = "$") {
  if (Array.isArray(value)) {
    for (const [index, item] of value.entries()) rejectSensitiveProjectKeys(item, `${fieldPath}[${index}]`);
    return;
  }
  if (!value || typeof value !== "object") return;
  for (const [key, nested] of Object.entries(value)) {
    const nestedPath = `${fieldPath}.${key}`;
    if (SENSITIVE_PROJECT_KEY_RE.test(key)) {
      throw projectError(
        "PROJECT_CREDENTIALS_NOT_PERSISTED",
        `项目文件不允许保存凭据类字段：${nestedPath}。密码、令牌、私钥和 API key 不会写入 .nexus.json。`,
        { path: null, field: nestedPath },
      );
    }
    rejectSensitiveProjectKeys(nested, nestedPath);
  }
}

function locateJsonSyntaxError(text) {
  let cursor = 0;
  const length = text.length;
  if (text.charCodeAt(0) === 0xfeff) cursor = 1;

  function skipWhitespace() {
    while (cursor < length && "\ \t\r\n".includes(text[cursor])) cursor += 1;
  }

  function readString() {
    const start = cursor;
    cursor += 1;
    while (cursor < length) {
      const character = text[cursor];
      if (character === "\"") {
        cursor += 1;
        return -1;
      }
      if (character === "\\") {
        cursor += 1;
        if (cursor >= length) return cursor;
        const escape = text[cursor];
        if (!"\"\\/bfnrt".includes(escape)) return cursor;
        if (escape === "u") {
          for (let digit = 1; digit <= 4; digit += 1) {
            cursor += 1;
            if (cursor >= length || !/[0-9a-fA-F]/.test(text[cursor])) return cursor;
          }
        }
        cursor += 1;
        continue;
      }
      if (character.charCodeAt(0) < 0x20) return cursor;
      cursor += 1;
    }
    return start;
  }

  function readNumber() {
    const start = cursor;
    if (text[cursor] === "-") cursor += 1;
    if (cursor >= length || !/[0-9]/.test(text[cursor])) return cursor;
    if (text[cursor] === "0") {
      cursor += 1;
    } else {
      while (cursor < length && /[0-9]/.test(text[cursor])) cursor += 1;
    }
    if (text[cursor] === ".") {
      cursor += 1;
      if (cursor >= length || !/[0-9]/.test(text[cursor])) return cursor;
      while (cursor < length && /[0-9]/.test(text[cursor])) cursor += 1;
    }
    if (text[cursor] === "e" || text[cursor] === "E") {
      cursor += 1;
      if (text[cursor] === "+" || text[cursor] === "-") cursor += 1;
      if (cursor >= length || !/[0-9]/.test(text[cursor])) return cursor;
      while (cursor < length && /[0-9]/.test(text[cursor])) cursor += 1;
    }
    return -1;
  }

  function readLiteral(word) {
    if (text.startsWith(word, cursor)) {
      cursor += word.length;
      return -1;
    }
    return cursor;
  }

  function readValue() {
    skipWhitespace();
    if (cursor >= length) return cursor;
    const character = text[cursor];
    if (character === "{") {
      cursor += 1;
      skipWhitespace();
      if (text[cursor] === "}") {
        cursor += 1;
        return -1;
      }
      for (;;) {
        skipWhitespace();
        if (cursor >= length || text[cursor] !== "\"") return cursor;
        const stringInvalid = readString();
        if (stringInvalid >= 0) return stringInvalid;
        skipWhitespace();
        if (cursor >= length || text[cursor] !== ":") return cursor;
        cursor += 1;
        const valueInvalid = readValue();
        if (valueInvalid >= 0) return valueInvalid;
        skipWhitespace();
        if (cursor >= length) return cursor;
        if (text[cursor] === ",") {
          cursor += 1;
          continue;
        }
        if (text[cursor] === "}") {
          cursor += 1;
          return -1;
        }
        return cursor;
      }
    }
    if (character === "[") {
      cursor += 1;
      skipWhitespace();
      if (text[cursor] === "]") {
        cursor += 1;
        return -1;
      }
      for (;;) {
        const valueInvalid = readValue();
        if (valueInvalid >= 0) return valueInvalid;
        skipWhitespace();
        if (cursor >= length) return cursor;
        if (text[cursor] === ",") {
          cursor += 1;
          continue;
        }
        if (text[cursor] === "]") {
          cursor += 1;
          return -1;
        }
        return cursor;
      }
    }
    if (character === "\"") return readString();
    if (character === "-" || /[0-9]/.test(character)) return readNumber();
    if (text.startsWith("true", cursor)) return readLiteral("true");
    if (text.startsWith("false", cursor)) return readLiteral("false");
    if (text.startsWith("null", cursor)) return readLiteral("null");
    return cursor;
  }

  const valueInvalid = readValue();
  if (valueInvalid >= 0) return valueInvalid;
  skipWhitespace();
  return cursor < length ? cursor : -1;
}

function jsonErrorLocation(text, error) {
  const scannedOffset = locateJsonSyntaxError(text);
  if (scannedOffset >= 0) {
    const before = text.slice(0, scannedOffset);
    const lines = before.split(/\r\n|\n|\r/);
    return {
      line: lines.length,
      column: lines[lines.length - 1].length + 1,
      characterOffset: scannedOffset,
      byteOffset: Buffer.byteLength(before, "utf8"),
    };
  }
  const locationMatch = /\(line (\d+) column (\d+)\)/.exec(error.message);
  if (locationMatch) {
    const line = Number(locationMatch[1]);
    const column = Number(locationMatch[2]);
    if (Number.isInteger(line) && line > 0 && Number.isInteger(column) && column > 0) {
      const lines = text.split(/\r\n|\n|\r/);
      if (line <= lines.length && column <= lines[line - 1].length + 1) {
        let characterOffset = column - 1;
        for (let index = 0; index < line - 1; index += 1) {
          characterOffset += lines[index].length + 1;
        }
        return {
          line,
          column,
          characterOffset,
          byteOffset: Buffer.byteLength(text.slice(0, characterOffset), "utf8"),
        };
      }
    }
  }
  const match = /position (\d+)/.exec(error.message);
  if (!match) return {};
  const characterOffset = Number(match[1]);
  if (!Number.isInteger(characterOffset) || characterOffset < 0 || characterOffset > text.length) return {};
  const before = text.slice(0, characterOffset);
  const lines = before.split(/\r\n|\n|\r/);
  return {
    line: lines.length,
    column: lines[lines.length - 1].length + 1,
    characterOffset,
    byteOffset: Buffer.byteLength(before, "utf8"),
  };
}

function boundedString(value, fallback, maxLength) {
  if (typeof value !== "string") return fallback;
  const text = value.trim();
  return text.slice(0, maxLength) || fallback;
}

function boundedInteger(value, fallback, min, max) {
  const number = Number(value);
  if (!Number.isInteger(number) || number < min || number > max) return fallback;
  return number;
}

function normalizePoint(raw, index) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw projectError("PROJECT_INVALID_POINT", `点位 #${index + 1} 格式无效`);
  }
  const fc = boundedInteger(raw.fc, 3, 1, 16);
  if (!ALLOWED_FC.has(fc)) throw projectError("PROJECT_INVALID_POINT", `点位 #${index + 1} 功能码不受支持`);
  const maxQuantity = fc === 1 || fc === 2 ? 2000 : 125;
  const address = boundedInteger(raw.address, -1, 0, 65_535);
  const quantity = boundedInteger(raw.quantity, -1, 1, maxQuantity);
  if (address < 0 || quantity < 1 || address + quantity - 1 > 65_535) {
    throw projectError("PROJECT_INVALID_POINT", `点位 #${index + 1} 地址或数量越界`);
  }
  return {
    name: boundedString(raw.name, `点位${index + 1}`, 128),
    unitId: boundedInteger(raw.unitId, 1, 1, 247),
    fc,
    address,
    quantity,
    dataType: boundedString(raw.dataType, "Unsigned16", 64),
    scale: boundedString(String(raw.scale ?? "1"), "1", 32),
    unit: boundedString(raw.unit, "", 32),
  };
}

function normalizeTrendSelection(raw) {
  const values = Array.isArray(raw) ? raw : [];
  const output = [];
  const seen = new Set();
  for (const value of values) {
    if (typeof value !== "string") continue;
    const key = value.trim();
    if (!/^reg-(?:HR|IR)-[0-9]{1,5}$/.test(key) || seen.has(key)) continue;
    seen.add(key);
    output.push(key);
    if (output.length > MAX_TREND_SELECTIONS) {
      throw projectError("PROJECT_TOO_MANY_TREND_SELECTIONS", `项目趋势选择不能超过 ${MAX_TREND_SELECTIONS} 个`);
    }
  }
  return output;
}

function normalizeCommandList(raw) {
  const values = Array.isArray(raw) ? raw : [];
  if (values.length > MAX_COMMANDS) {
    throw projectError("PROJECT_TOO_MANY_COMMANDS", `项目指令任务不能超过 ${MAX_COMMANDS} 条`);
  }
  return values.map((rawCommand, index) => {
    if (!rawCommand || typeof rawCommand !== "object" || Array.isArray(rawCommand)) {
      throw projectError("PROJECT_INVALID_COMMAND", `指令 #${index + 1} 格式无效`);
    }
    const fc = Number(rawCommand.fc);
    const unitId = Number(rawCommand.unitId);
    const address = Number(rawCommand.address);
    const quantity = Number(rawCommand.quantity);
    if (!Number.isInteger(fc) || !ALLOWED_FC.has(fc)) {
      throw projectError("PROJECT_INVALID_COMMAND", `指令 #${index + 1} 功能码不受支持`);
    }
    if (!Number.isInteger(unitId) || unitId < 1 || unitId > 247) {
      throw projectError("PROJECT_INVALID_COMMAND", `指令 #${index + 1} 站号无效`);
    }
    if (!Number.isInteger(address) || address < 0 || address > 65_535) {
      throw projectError("PROJECT_INVALID_COMMAND", `指令 #${index + 1} 地址无效`);
    }
    if (!Number.isInteger(quantity) || quantity < 0 || quantity > 2000) {
      throw projectError("PROJECT_INVALID_COMMAND", `指令 #${index + 1} 数量无效`);
    }
    return {
      fc,
      unitId,
      address,
      quantity,
      value: boundedString(String(rawCommand.value ?? ""), "", 256),
    };
  });
}

function normalizeSimulators(raw) {
  const value = raw && typeof raw === "object" && !Array.isArray(raw) ? raw : {};
  const modbus = value.modbus && typeof value.modbus === "object" ? value.modbus : {};
  const melsec = value.melsec && typeof value.melsec === "object" ? value.melsec : {};
  const s7 = value.s7 && typeof value.s7 === "object" ? value.s7 : {};
  const modbusMode = modbus.mode === "serial" ? "serial" : "tcp";
  const allowedStations = boundedString(String(modbus.allowedStations ?? ""), "", 128);
  if (allowedStations && !/^[0-9,\-\s]+$/.test(allowedStations)) {
    throw projectError("PROJECT_INVALID_SIMULATOR", "Modbus 模拟器允许站号只能包含数字、逗号、连字符和空格");
  }
  return {
    modbus: {
      mode: modbusMode,
      port: String(boundedInteger(modbus.port, 502, 1, 65_535)),
      allowedStations,
    },
    melsec: {
      port: String(boundedInteger(melsec.port, 5000, 1, 65_535)),
    },
    s7: {
      port: String(boundedInteger(s7.port, 102, 1, 65_535)),
    },
  };
}

function normalizeHelpReference(raw) {
  const value = raw && typeof raw === "object" && !Array.isArray(raw) ? raw : null;
  if (!value || !ALLOWED_HELP_SOURCES.has(value.source)) return null;
  const variant = boundedString(value.variant, "", 64);
  return variant ? { source: value.source, variant } : null;
}

function safeProjectDataType(value) {
  return SAFE_PROJECT_DATA_TYPES.has(value) ? value : "Unsigned16";
}

function safeProjectScale(value) {
  return /^[+-]?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value) ? value : "1";
}

function sanitizeProjectDocumentForExport(raw) {
  const document = normalizeProjectDocument(raw);
  document.projectName = SANITIZED_PROJECT_NAME;
  document.savedAt = null;
  document.config.serial.portName = "";
  document.config.tcp.host = SANITIZED_TCP_HOST;
  document.config.command.displayType = safeProjectDataType(document.config.command.displayType);

  document.sessions = document.sessions.map((session, sessionIndex) => ({
    ...session,
    name: `脱敏会话${sessionIndex + 1}`,
    pointTable: session.pointTable.map((point, pointIndex) => ({
      ...point,
      name: `脱敏点位${sessionIndex + 1}-${pointIndex + 1}`,
      dataType: safeProjectDataType(point.dataType),
      scale: safeProjectScale(point.scale),
      unit: "",
    })),
  }));
  document.activeSession = document.sessions[0].name;

  document.workspace.commandList = document.workspace.commandList.map((command) => ({
    ...command,
    value: "",
  }));
  document.workspace.lastHelpReference = null;
  return document;
}

function normalizeConfig(raw) {
  const config = raw && typeof raw === "object" && !Array.isArray(raw) ? raw : {};
  const serial = config.serial && typeof config.serial === "object" ? config.serial : {};
  const tcp = config.tcp && typeof config.tcp === "object" ? config.tcp : {};
  const command = config.command && typeof config.command === "object" ? config.command : {};
  const transport = ALLOWED_TRANSPORTS.has(config.transport) ? config.transport : "rtu";
  return {
    transport,
    serial: {
      portName: boundedString(serial.portName, "", 64),
      baudRate: String(boundedInteger(serial.baudRate, 9600, 50, 4_000_000)),
      parity: ["none", "even", "odd"].includes(serial.parity) ? serial.parity : "none",
      dataBits: ["7", "8"].includes(String(serial.dataBits)) ? String(serial.dataBits) : "8",
      stopBits: ["1", "2"].includes(String(serial.stopBits)) ? String(serial.stopBits) : "1",
    },
    tcp: {
      host: boundedString(tcp.host, "127.0.0.1", 253),
      port: String(boundedInteger(tcp.port, 502, 1, 65_535)),
    },
    command: {
      unitId: String(boundedInteger(command.unitId, 1, 1, 247)),
      functionCode: String(ALLOWED_FC.has(Number(command.functionCode)) ? Number(command.functionCode) : 3),
      startAddress: String(boundedInteger(command.startAddress, 0, 0, 65_535)),
      quantity: String(boundedInteger(command.quantity, 1, 1, 2000)),
      displayType: boundedString(command.displayType, "Unsigned16", 64),
      pollInterval: String(boundedInteger(command.pollInterval, 1000, 50, 600_000)),
    },
  };
}

function normalizeCurrentProjectDocument(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw projectError("PROJECT_INVALID", "项目文件根节点必须是对象");
  }
  validateJsonTree(raw);
  rejectSensitiveProjectKeys(raw);
  if (Object.keys(raw).length > MAX_TOP_LEVEL_KEYS) {
    throw projectError("PROJECT_TOO_COMPLEX", `项目根节点字段数不能超过 ${MAX_TOP_LEVEL_KEYS} 个`);
  }
  if (raw.format !== PROJECT_FORMAT) {
    throw projectError("PROJECT_FORMAT_UNSUPPORTED", "不是 Nexus 项目文件");
  }
  if (raw.schemaVersion !== PROJECT_SCHEMA_VERSION) {
    throw projectError("PROJECT_VERSION_UNSUPPORTED", `不支持项目格式版本 ${raw.schemaVersion}`);
  }
  const sessionsRaw = Array.isArray(raw.sessions) ? raw.sessions : [];
  if (sessionsRaw.length < 1 || sessionsRaw.length > MAX_SESSIONS) {
    throw projectError("PROJECT_INVALID_SESSIONS", `项目会话数量应为 1-${MAX_SESSIONS}`);
  }
  let pointCount = 0;
  const names = new Set();
  const sessions = sessionsRaw.map((session, sessionIndex) => {
    if (!session || typeof session !== "object" || Array.isArray(session)) {
      throw projectError("PROJECT_INVALID_SESSIONS", `会话 #${sessionIndex + 1} 格式无效`);
    }
    const name = boundedString(session.name, `会话${sessionIndex + 1}`, 50);
    if (names.has(name)) throw projectError("PROJECT_DUPLICATE_SESSION", `会话名称重复：${name}`);
    names.add(name);
    const pointsRaw = Array.isArray(session.pointTable) ? session.pointTable : [];
    pointCount += pointsRaw.length;
    if (pointCount > MAX_POINTS) throw projectError("PROJECT_TOO_MANY_POINTS", `项目点位总数不能超过 ${MAX_POINTS}`);
    return {
      name,
      pointTable: pointsRaw.map(normalizePoint),
      trendSelection: normalizeTrendSelection(session.trendSelection),
    };
  });
  const activeSession = names.has(raw.activeSession) ? raw.activeSession : sessions[0].name;
  return {
    format: PROJECT_FORMAT,
    schemaVersion: PROJECT_SCHEMA_VERSION,
    product: "Nexus 2.0",
    projectName: boundedString(raw.projectName, "未命名项目", 128),
    savedAt: typeof raw.savedAt === "string" ? raw.savedAt : null,
    activeView: ALLOWED_VIEWS.has(raw.activeView) ? raw.activeView : "master",
    activeSession,
    config: normalizeConfig(raw.config),
    sessions,
    workspace: {
      commandList: normalizeCommandList(raw.workspace?.commandList),
      simulators: normalizeSimulators(raw.workspace?.simulators),
      lastHelpReference: normalizeHelpReference(raw.workspace?.lastHelpReference),
    },
  };
}

function projectSchemaVersion(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw projectError("PROJECT_INVALID", "项目文件根节点必须是对象");
  }
  if (raw.format !== PROJECT_FORMAT) {
    throw projectError("PROJECT_FORMAT_UNSUPPORTED", "不是 Nexus 项目文件");
  }
  return raw.schemaVersion ?? LEGACY_PROJECT_SCHEMA_VERSION;
}

function migrateProjectDocument(raw) {
  const sourceSchemaVersion = projectSchemaVersion(raw);
  if (!Number.isInteger(sourceSchemaVersion) || sourceSchemaVersion < 0) {
    throw projectError("PROJECT_VERSION_UNSUPPORTED", `不支持项目格式版本 ${raw.schemaVersion}`);
  }
  if (sourceSchemaVersion > PROJECT_SCHEMA_VERSION) {
    throw projectError("PROJECT_VERSION_UNSUPPORTED", `项目格式版本 ${sourceSchemaVersion} 高于当前支持的 ${PROJECT_SCHEMA_VERSION}`);
  }

  let working = JSON.parse(JSON.stringify(raw));
  const steps = [];
  for (let version = sourceSchemaVersion; version < PROJECT_SCHEMA_VERSION; version += 1) {
    const migration = PROJECT_MIGRATIONS[version];
    if (!migration) {
      throw projectError("PROJECT_MIGRATION_MISSING", `缺少项目格式 v${version} 到 v${version + 1} 的迁移器`);
    }
    working = migration.migrate(working);
    working.schemaVersion = migration.targetVersion;
    steps.push({
      fromVersion: version,
      toVersion: migration.targetVersion,
      summary: migration.summary,
    });
  }

  return {
    document: normalizeCurrentProjectDocument(working),
    sourceSchemaVersion,
    targetSchemaVersion: PROJECT_SCHEMA_VERSION,
    steps,
  };
}

function normalizeProjectDocument(raw) {
  const version = projectSchemaVersion(raw);
  if (version === LEGACY_PROJECT_SCHEMA_VERSION) {
    return migrateProjectDocument(raw).document;
  }
  if (version !== PROJECT_SCHEMA_VERSION) {
    throw projectError("PROJECT_VERSION_UNSUPPORTED", `不支持项目格式版本 ${version}`);
  }
  return normalizeCurrentProjectDocument(raw);
}

class ProjectFileService {
  constructor({ fsImpl = fs, randomId = randomUUID } = {}) {
    this.fs = fsImpl;
    this.randomId = randomId;
  }

  save(filePath, document) {
    const normalized = normalizeProjectDocument(document);
    normalized.savedAt = new Date().toISOString();
    const json = JSON.stringify(normalized, null, 2) + "\n";
    const bytes = Buffer.byteLength(json, "utf8");
    if (bytes > MAX_PROJECT_BYTES) throw projectError("PROJECT_TOO_LARGE", "项目文件超过 5 MiB 限制");
    const temporaryPath = path.join(
      path.dirname(filePath),
      `.${path.basename(filePath)}.tmp-${this.randomId()}`,
    );
    let temporaryCreated = false;
    try {
      temporaryCreated = true;
      this.fs.writeFileSync(temporaryPath, json, "utf8");
      if (this.fs.readFileSync(temporaryPath, "utf8") !== json) {
        throw projectError("PROJECT_WRITE_VERIFY_FAILED", "项目临时文件写入后校验失败");
      }
      this.fs.renameSync(temporaryPath, filePath);
      temporaryCreated = false;
    } finally {
      if (temporaryCreated) {
        try {
          this.fs.unlinkSync(temporaryPath);
        } catch {
          // Keep the original failure; never fall back to directly overwriting the target.
        }
      }
    }
    return { path: filePath, bytes, document: normalized };
  }

  load(filePath) {
    let stat;
    let text;
    try {
      stat = this.fs.statSync(filePath);
      if (stat.size > MAX_PROJECT_BYTES) {
        throw projectError("PROJECT_TOO_LARGE", "项目文件超过 5 MiB 限制", { path: filePath, bytes: stat.size });
      }
      text = this.fs.readFileSync(filePath, "utf8");
    } catch (error) {
      if (error.code?.startsWith("PROJECT_")) throw error;
      throw projectError("PROJECT_READ_FAILED", `项目文件读取失败：${error.message}`, { path: filePath });
    }
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch (error) {
      const location = jsonErrorLocation(text, error);
      const locationText = Number.isInteger(location.line)
        ? `（第 ${location.line} 行第 ${location.column} 列，字节 ${location.byteOffset}）`
        : "";
      throw projectError(
        "PROJECT_JSON_INVALID",
        `项目 JSON 解析失败：${error.message}${locationText}`,
        {
          path: filePath,
          byteLength: Buffer.byteLength(text, "utf8"),
          ...location,
        },
      );
    }
    const version = projectSchemaVersion(parsed);
    if (version === LEGACY_PROJECT_SCHEMA_VERSION) {
      const migrated = migrateProjectDocument(parsed);
      return {
        path: filePath,
        document: migrated.document,
        migration: {
          sourceSchemaVersion: migrated.sourceSchemaVersion,
          targetSchemaVersion: migrated.targetSchemaVersion,
          steps: migrated.steps,
          appliedInMemoryOnly: true,
          sourceModified: false,
        },
      };
    }
    if (version !== PROJECT_SCHEMA_VERSION) {
      throw projectError("PROJECT_VERSION_UNSUPPORTED", `不支持项目格式版本 ${version}`);
    }
    return { path: filePath, document: normalizeCurrentProjectDocument(parsed) };
  }

  ensureExtension(filePath) {
    return filePath.toLowerCase().endsWith(".nexus.json") ? filePath : `${filePath}.nexus.json`;
  }

  defaultFilename(projectName) {
    const safe = boundedString(projectName, "Nexus项目", 128).replace(/[<>:"/\\|?*\x00-\x1F]/g, "_");
    return path.basename(`${safe}.nexus.json`);
  }

  sanitizeForExport(document) {
    return sanitizeProjectDocumentForExport(document);
  }

  exportSanitized(filePath, document) {
    return this.save(filePath, sanitizeProjectDocumentForExport(document));
  }

  previewMigration(filePath) {
    const loaded = this.load(filePath);
    return {
      path: filePath,
      previewOnly: true,
      document: loaded.document,
      migration: loaded.migration ?? {
        sourceSchemaVersion: PROJECT_SCHEMA_VERSION,
        targetSchemaVersion: PROJECT_SCHEMA_VERSION,
        steps: [],
        appliedInMemoryOnly: true,
        sourceModified: false,
      },
    };
  }

  migrateFile(filePath, outputFilePath) {
    const preview = this.previewMigration(filePath);
    return this.save(outputFilePath, preview.document);
  }

  defaultSanitizedFilename() {
    return "Nexus脱敏项目.nexus.json";
  }
}

module.exports = {
  MAX_JSON_DEPTH,
  MAX_JSON_NODES,
  MAX_PROJECT_BYTES,
  PROJECT_FORMAT,
  PROJECT_SCHEMA_VERSION,
  LEGACY_PROJECT_SCHEMA_VERSION,
  ProjectFileService,
  SANITIZED_PROJECT_NAME,
  SANITIZED_TCP_HOST,
  normalizeProjectDocument,
  migrateProjectDocument,
  sanitizeProjectDocumentForExport,
};
