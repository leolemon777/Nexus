"use strict";

const fs = require("node:fs");
const path = require("node:path");

const RECOVERY_SCHEMA_VERSION = 1;
const MAX_PATH_BYTES = 1024;

function recoveryError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function normalizeMetadata(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    return { schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null };
  }
  if (raw.schemaVersion !== RECOVERY_SCHEMA_VERSION) {
    throw recoveryError("WORKSPACE_RECOVERY_VERSION_UNSUPPORTED", `不支持的工作区恢复格式 ${raw.schemaVersion}`);
  }
  if (!raw.lastProject || typeof raw.lastProject !== "object") {
    return { schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null };
  }
  const projectPath = String(raw.lastProject.path ?? "");
  if (!projectPath || Buffer.byteLength(projectPath, "utf8") > MAX_PATH_BYTES) {
    return { schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null };
  }
  return {
    schemaVersion: RECOVERY_SCHEMA_VERSION,
    lastProject: {
      path: path.resolve(projectPath),
      projectName: String(raw.lastProject.projectName ?? "").slice(0, 128),
      savedAt: typeof raw.lastProject.savedAt === "string" ? raw.lastProject.savedAt : null,
      lastRestoredAt: typeof raw.lastProject.lastRestoredAt === "string" ? raw.lastProject.lastRestoredAt : null,
    },
  };
}

class WorkspaceRecoveryService {
  constructor({ filePath, fsImpl = fs, pathImpl = path } = {}) {
    if (!filePath) throw recoveryError("WORKSPACE_RECOVERY_PATH_REQUIRED", "未提供工作区恢复文件");
    this.filePath = filePath;
    this.fs = fsImpl;
    this.path = pathImpl;
  }

  _read() {
    let raw;
    try {
      raw = JSON.parse(this.fs.readFileSync(this.filePath, "utf8"));
    } catch (error) {
      if (error?.code === "ENOENT") return { schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null };
      throw recoveryError("WORKSPACE_RECOVERY_READ_FAILED", error.message);
    }
    return normalizeMetadata(raw);
  }

  _write(metadata) {
    this.fs.mkdirSync(this.path.dirname(this.filePath), { recursive: true });
    this.fs.writeFileSync(this.filePath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
  }

  recordSaved({ path: projectPath, projectName }) {
    const savedAt = new Date().toISOString();
    const metadata = {
      schemaVersion: RECOVERY_SCHEMA_VERSION,
      lastProject: {
        path: this.path.resolve(String(projectPath)),
        projectName: String(projectName ?? "").slice(0, 128),
        savedAt,
        lastRestoredAt: null,
      },
    };
    this._write(metadata);
    return metadata;
  }

  markRestored() {
    const metadata = this._read();
    if (!metadata.lastProject) return metadata;
    metadata.lastProject.lastRestoredAt = metadata.lastProject.savedAt;
    this._write(metadata);
    return metadata;
  }

  clear() {
    this._write({ schemaVersion: RECOVERY_SCHEMA_VERSION, lastProject: null });
  }

  pending() {
    const metadata = this._read();
    return metadata.lastProject && metadata.lastProject.lastRestoredAt !== metadata.lastProject.savedAt
      ? metadata.lastProject
      : null;
  }
}

module.exports = {
  RECOVERY_SCHEMA_VERSION,
  WorkspaceRecoveryService,
  normalizeMetadata,
};
