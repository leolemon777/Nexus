"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const {
  MAX_AUDIT_BYTES,
  WriteAuditService,
  normalizeEntry,
} = require("./write-audit-service.cjs");

class MemoryFs {
  constructor({ initialSize = 0 } = {}) {
    this.files = new Map();
    this.directories = new Set();
    if (initialSize > 0) {
      this.files.set(path.sep, Buffer.alloc(initialSize));
    }
  }
  mkdirSync(directory) { this.directories.add(directory); }
  statSync(filePath) {
    if (!this.files.has(filePath)) {
      const error = new Error("not found");
      error.code = "ENOENT";
      throw error;
    }
    return { size: this.files.get(filePath).length };
  }
  renameSync(from, to) {
    assert.ok(this.files.has(from));
    this.files.set(to, this.files.get(from));
    this.files.delete(from);
  }
  appendFileSync(filePath, data) { this.files.set(filePath, Buffer.from(data)); }
}

test("write audit normalizes bounded, structured entries", () => {
  const entry = normalizeEntry({
    protocol: "modbus",
    transport: "rtu",
    unitId: 2,
    functionCode: 16,
    address: 100,
    quantity: 2,
    oldValue: [1, 2],
    newValue: [3, 4],
    readbackValue: [3, 4],
    result: "write-succeeded",
  });
  assert.equal(entry.schemaVersion, 1);
  assert.deepEqual(entry.newValue, [3, 4]);
  assert.deepEqual(normalizeEntry({ result: "unknown" }).result, "write-succeeded");
  const sensitive = normalizeEntry({
    result: "write-failed",
    message: "RPC failed password=hunter2 token=secret-token user=operator",
  });
  assert.equal(sensitive.message, "RPC failed password=[REDACTED] token=[REDACTED] user=[REDACTED]");
  assert.throws(() => normalizeEntry(null), (error) => error.code === "WRITE_AUDIT_INVALID");
});

test("write audit appends JSONL and rotates at the size boundary", () => {
  const memoryFs = new MemoryFs();
  const service = new WriteAuditService({
    directory: "D:\\userData\\logs",
    fsImpl: memoryFs,
    pathImpl: path,
  });
  const first = service.append({ protocol: "modbus", transport: "tcp", result: "confirmed", newValue: [1] });
  assert.equal(first.rotated, false);
  assert.match(first.path, /write-audit\.jsonl$/);
  const parsed = JSON.parse(memoryFs.files.get(first.path).toString("utf8"));
  assert.equal(parsed.result, "confirmed");

  memoryFs.files.set(first.path, Buffer.alloc(MAX_AUDIT_BYTES));
  const second = service.append({ protocol: "modbus", transport: "tcp", result: "cancelled" });
  assert.equal(second.rotated, true);
  assert.match(path.basename(second.path), /^write-audit\.jsonl$/);
  assert.equal([...memoryFs.files.keys()].filter((file) => file.includes("write-audit-")).length, 1);
  assert.equal(memoryFs.directories.has("D:\\userData\\logs"), true);
});

test("renderer write flow keeps confirmation, readback, and audit wiring explicit", () => {
  const fs = require("node:fs");
  const main = fs.readFileSync(path.join(__dirname, "main.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(__dirname, "preload.cjs"), "utf8");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");

  assert.match(main, /WriteAuditService/);
  assert.match(main, /nexus:record_write_audit/);
  assert.match(preload, /record_write_audit/);
  assert.match(renderer, /readCurrentValueForWrite/);
  assert.match(renderer, /pre-read-failed/);
  assert.match(renderer, /confirmModbusWrite/);
  assert.match(renderer, /readbackAfterWrite/);
  assert.match(renderer, /recordWriteAudit/);
  assert.match(renderer, /confirmHighRiskControl/);
  assert.match(renderer, /HOT-START/);
  assert.match(renderer, /mc_remote_stop/);
  assert.match(renderer, /旧值/);
  assert.match(renderer, /新值/);
});
