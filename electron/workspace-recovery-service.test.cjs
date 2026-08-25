"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const {
  RECOVERY_SCHEMA_VERSION,
  WorkspaceRecoveryService,
  normalizeMetadata,
} = require("./workspace-recovery-service.cjs");

class MemoryFs {
  constructor() {
    this.files = new Map();
    this.directories = new Set();
  }
  mkdirSync(directory) { this.directories.add(directory); }
  readFileSync(filePath) {
    if (!this.files.has(filePath)) {
      const error = new Error("not found");
      error.code = "ENOENT";
      throw error;
    }
    return this.files.get(filePath);
  }
  writeFileSync(filePath, data) { this.files.set(filePath, data); }
}

test("workspace recovery normalizes and gates one-shot restore metadata", () => {
  const savedAt = "2026-08-23T12:00:00.000Z";
  const metadata = normalizeMetadata({
    schemaVersion: RECOVERY_SCHEMA_VERSION,
    lastProject: { path: "D:\\work\\line.nexus.json", projectName: "Line", savedAt },
  });
  assert.equal(metadata.lastProject.lastRestoredAt, null);
  metadata.lastProject.lastRestoredAt = savedAt;
  assert.equal(normalizeMetadata(metadata).lastProject.path, path.resolve("D:\\work\\line.nexus.json"));
  assert.equal(normalizeMetadata({ schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null }).lastProject, null);
  assert.throws(() => normalizeMetadata({ schemaVersion: 99 }), (error) => error.code === "WORKSPACE_RECOVERY_VERSION_UNSUPPORTED");
});

test("workspace recovery records save, marks one restore, and clears new projects", () => {
  const memoryFs = new MemoryFs();
  const service = new WorkspaceRecoveryService({
    filePath: path.join("C:\\Users\\test\\AppData", "Nexus 2.0", "workspace-recovery.json"),
    fsImpl: memoryFs,
    pathImpl: path,
  });
  assert.equal(service.pending(), null);
  service.recordSaved({ path: "D:\\work\\line.nexus.json", projectName: "产线" });
  assert.equal(service.pending().projectName, "产线");
  service.markRestored();
  assert.equal(service.pending(), null);
  service.recordSaved({ path: "D:\\work\\line2.nexus.json", projectName: "Second" });
  assert.equal(service.pending().projectName, "Second");
  service.clear();
  assert.equal(service.pending(), null);
  assert.equal(memoryFs.directories.has(path.join("C:\\Users\\test\\AppData", "Nexus 2.0")), true);
});

test("renderer and main keep startup restore read-only and explicit", () => {
  const fs = require("node:fs");
  const main = fs.readFileSync(path.join(__dirname, "main.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(__dirname, "preload.cjs"), "utf8");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  assert.match(main, /WorkspaceRecoveryService/);
  assert.match(main, /nexus:project_restore_last/);
  assert.match(preload, /project_restore_last/);
  assert.match(renderer, /restoreLastProject/);
  assert.match(renderer, /只读恢复/);
  assert.match(renderer, /不自动连接、启动模拟器或执行指令/);
  assert.doesNotMatch(renderer, /restoreLastProject[\s\S]{0,700}(start_poll|execute_commands|open_serial_port|open_tcp_connection)/);
});
