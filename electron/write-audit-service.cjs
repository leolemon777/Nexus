"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { redactLogMessage } = require("./log-redaction-service.cjs");

const MAX_AUDIT_BYTES = 5 * 1024 * 1024;
const MAX_VALUE_ITEMS = 2000;
const ALLOWED_RESULTS = new Set([
  "confirmed",
  "cancelled",
  "pre-read-failed",
  "write-failed",
  "readback-mismatch",
  "readback-failed",
  "write-succeeded",
]);

function auditError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function boundedValue(value) {
  if (Array.isArray(value)) {
    return value.slice(0, MAX_VALUE_ITEMS).map((item) => (typeof item === "boolean" ? item : Number(item)));
  }
  if (typeof value === "boolean") return value;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function normalizeEntry(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw auditError("WRITE_AUDIT_INVALID", "审计记录必须是对象");
  }
  const result = ALLOWED_RESULTS.has(raw.result) ? raw.result : "write-succeeded";
  const entry = {
    schemaVersion: 1,
    timestamp: typeof raw.timestamp === "string" ? raw.timestamp : new Date().toISOString(),
    protocol: String(raw.protocol ?? "modbus").slice(0, 64),
    transport: String(raw.transport ?? "unknown").slice(0, 64),
    connectionId: String(raw.connectionId ?? "").slice(0, 128),
    unitId: Number.isInteger(Number(raw.unitId)) ? Number(raw.unitId) : null,
    functionCode: Number.isInteger(Number(raw.functionCode)) ? Number(raw.functionCode) : null,
    address: Number.isInteger(Number(raw.address)) ? Number(raw.address) : null,
    quantity: Number.isInteger(Number(raw.quantity)) ? Number(raw.quantity) : null,
    oldValue: boundedValue(raw.oldValue),
    newValue: boundedValue(raw.newValue),
    readbackValue: boundedValue(raw.readbackValue),
    result,
    errorCode: raw.errorCode ? String(raw.errorCode).slice(0, 128) : null,
    message: raw.message ? redactLogMessage(raw.message, 512) : null,
  };
  return entry;
}

class WriteAuditService {
  constructor({ directory, fsImpl = fs, pathImpl = path } = {}) {
    this.directory = directory;
    this.fs = fsImpl;
    this.path = pathImpl;
  }

  _filePath() {
    if (!this.directory) throw auditError("WRITE_AUDIT_DIRECTORY_REQUIRED", "未提供写入审计目录");
    return this.path.join(this.directory, "write-audit.jsonl");
  }

  append(rawEntry) {
    const entry = normalizeEntry(rawEntry);
    const filePath = this._filePath();
    this.fs.mkdirSync(this.directory, { recursive: true });
    let rotate = false;
    try {
      const stat = this.fs.statSync(filePath);
      rotate = stat.size >= MAX_AUDIT_BYTES;
    } catch (error) {
      if (error?.code !== "ENOENT") throw error;
    }
    if (rotate) {
      const stamp = new Date().toISOString().replace(/[:.]/g, "-");
      this.fs.renameSync(filePath, this.path.join(this.directory, `write-audit-${stamp}.jsonl`));
    }
    const line = `${JSON.stringify(entry)}\n`;
    this.fs.appendFileSync(filePath, line, "utf8");
    return { path: filePath, bytes: Buffer.byteLength(line, "utf8"), rotated: rotate, entry };
  }
}

module.exports = {
  MAX_AUDIT_BYTES,
  WriteAuditService,
  normalizeEntry,
};
