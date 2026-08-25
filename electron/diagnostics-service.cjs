"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const DIAGNOSTICS_SCHEMA_VERSION = 1;
const MAX_RECENT_FRAMES = 100;
const MAX_FRAME_BYTES = 2_048;
const MAX_REPORT_BYTES = 2 * 1024 * 1024;
const SENSITIVE_KEY_RE = /(password|passwd|secret|token|authorization|cookie|credential|privatekey|api[_-]?key)/i;
const IPV4_RE = /^(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)$/;
const MAC_RE = /^[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}$/;
const IPV6_RE = /^[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){1,7}(?:%[A-Za-z0-9_.-]+)?$/;

function stableHash(value) {
  return crypto.createHash("sha256").update(String(value)).digest("hex").slice(0, 10);
}

function redactIPv4(value) {
  const parts = value.split(".");
  const first = Number(parts[0]);
  if (first === 127 || first === 10) return `${first}.x.x.x`;
  if (first === 172 && Number(parts[1]) >= 16 && Number(parts[1]) <= 31) return `${first}.${parts[1]}.x.x`;
  if (first === 192 && Number(parts[1]) === 168) return `${first}.${parts[1]}.x.x`;
  if (first === 169 && Number(parts[1]) === 254) return `${first}.${parts[1]}.x.x`;
  return `public-${stableHash(value)}`;
}

function redactIPv6(value) {
  if (value === "::1") return value;
  return `ipv6-${stableHash(value)}`;
}

function redactMac(value) {
  return value ? `mac-${stableHash(value)}` : "";
}

function redactPath(value) {
  return value ? `path-${stableHash(value)}` : value ?? null;
}

function redactString(value) {
  if (IPV4_RE.test(value)) return redactIPv4(value);
  if (IPV6_RE.test(value)) return redactIPv6(value);
  if (MAC_RE.test(value)) return redactMac(value);
  return value;
}

function sanitize(value, keyName = "") {
  if (SENSITIVE_KEY_RE.test(keyName)) return "[REDACTED]";
  if (value === null || value === undefined) return value;
  if (typeof value === "string") return redactString(value);
  if (typeof value === "number" || typeof value === "boolean") return value;
  if (Array.isArray(value)) return value.slice(0, 500).map((item) => sanitize(item, keyName));
  if (value instanceof Date) return value.toISOString();
  if (value && typeof value === "object") {
    const output = {};
    for (const [key, nested] of Object.entries(value)) output[key] = sanitize(nested, key);
    return output;
  }
  return String(value);
}

function normalizeFrame(raw, index) {
  const frame = raw && typeof raw === "object" && !Array.isArray(raw) ? raw : {};
  const bytes = Array.isArray(frame.bytes) ? frame.bytes.slice(0, MAX_FRAME_BYTES) : [];
  const hexFromBytes = bytes.map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
  return {
    index,
    timestamp: typeof frame.timestamp === "number" ? new Date(frame.timestamp).toISOString() : null,
    direction: frame.direction === "RX" || frame.direction === "TX" ? frame.direction : "unknown",
    byteCount: bytes.length,
    hex: typeof frame.hex === "string" && frame.hex.length <= MAX_FRAME_BYTES * 3
      ? frame.hex
      : hexFromBytes,
  };
}

function normalizeNetworkInterfaces(interfaces) {
  return Object.entries(interfaces ?? {}).map(([name, addresses]) => {
    const list = Array.isArray(addresses) ? addresses : [];
    const first = list[0] ?? {};
    return {
      name,
      internal: Boolean(first.internal),
      mac: redactMac(first.mac ?? ""),
      addressCount: list.length,
      ipv4: list
        .filter((address) => address.family === "IPv4" || address.family === 4)
        .map((address) => ({
          address: redactIPv4(address.address),
          netmask: address.netmask,
        })),
      ipv6Count: list.filter((address) => address.family === "IPv6" || address.family === 6).length,
    };
  });
}

function normalizeSerialPorts(ports) {
  return (ports ?? []).slice(0, 100).map((port) => ({
    path: typeof port?.path === "string" ? port.path : (typeof port?.name === "string" ? port.name : String(port ?? "")),
    manufacturer: port?.manufacturer ?? null,
    serialNumber: port?.serialNumber ? `serial-${stableHash(port.serialNumber)}` : null,
    vendorId: port?.vendorId ?? null,
    productId: port?.productId ?? null,
  }));
}

function normalizeProjectSnapshot(raw) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) return null;
  return {
    name: raw.projectName ? `project-${stableHash(raw.projectName)}` : null,
    hasPath: Boolean(raw.hasPath),
    activeView: raw.activeView ?? null,
    activeSession: raw.activeSession ? `session-${stableHash(raw.activeSession)}` : null,
    config: sanitize(raw.config ?? null),
    sessionCount: Number(raw.sessionCount ?? 0),
    pointCount: Number(raw.pointCount ?? 0),
  };
}

function renderText(report) {
  const lines = [
    "Nexus 2.0 Diagnostic Report",
    `Generated at: ${report.generatedAt}`,
    `App version: ${report.product.version}`,
    `Runtime: Electron ${report.runtime.electron}, Node ${report.runtime.node}, Chromium ${report.runtime.chromium}`,
    `Platform: ${report.runtime.platform} ${report.runtime.arch}, release ${report.runtime.release}`,
    "",
    "Rust Core:",
    JSON.stringify(report.runtime.rustCore, null, 2),
    "",
    "Serial transport:",
    JSON.stringify(report.serial.status, null, 2),
    "",
    `Serial ports: ${report.serial.ports.length}`,
    `Network interfaces: ${report.network.interfaces.length}`,
    `Recent frames: ${report.communication.recentFrames.length}`,
    "",
    "Sensitive values are redacted. JSON contains the full structured report.",
  ];
  return lines.join("\n");
}

class DiagnosticsService {
  constructor({
    app,
    osImpl = os,
    fsImpl = fs,
    pathImpl = path,
    processInfo = process.versions,
  } = {}) {
    this.app = app;
    this.os = osImpl;
    this.fs = fsImpl;
    this.path = pathImpl;
    this.processInfo = processInfo;
  }

  buildReport({
    backendStatus,
    serialStatus,
    serialPorts = [],
    recentFrames = [],
    projectSnapshot = null,
    rustCoreLastError = null,
  } = {}) {
    const generatedAt = new Date().toISOString();
    const frames = (Array.isArray(recentFrames) ? recentFrames : [])
      .slice(-MAX_RECENT_FRAMES)
      .map(normalizeFrame);
    const report = {
      schemaVersion: DIAGNOSTICS_SCHEMA_VERSION,
      product: { name: "Nexus 2.0", version: this.app?.getVersion?.() ?? "unknown" },
      generatedAt,
      runtime: {
        electron: this.processInfo.electron ?? null,
        node: this.processInfo.node ?? null,
        chromium: this.processInfo.chrome ?? null,
        platform: this.os.platform(),
        arch: this.os.arch(),
        release: this.os.release(),
        uptimeSeconds: Math.round(process.uptime()),
        memory: {
          freeBytes: this.os.freemem(),
          totalBytes: this.os.totalmem(),
        },
        rustCore: sanitize(backendStatus ?? { state: "unknown", lastError: rustCoreLastError }),
      },
      serial: {
        status: sanitize(serialStatus ?? { isOpen: false, config: null }),
        ports: normalizeSerialPorts(serialPorts),
      },
      project: normalizeProjectSnapshot(projectSnapshot),
      network: {
        interfaces: normalizeNetworkInterfaces(this.os.networkInterfaces()),
      },
      communication: {
        recentFrameCount: frames.length,
        recentFrames: frames,
      },
      redaction: {
        policy: "ipv4-public-hash,private-prefix-mask,ipv6-hash,mac-tail,sensitive-key-drop,path-hash",
      },
    };
    const bytes = Buffer.byteLength(JSON.stringify(report), "utf8");
    if (bytes > MAX_REPORT_BYTES) {
      const error = new Error("诊断包超过 2 MiB 限制");
      error.code = "DIAGNOSTICS_TOO_LARGE";
      throw error;
    }
    return { report, bytes };
  }

  export({
    outputDirectory = this.path.join(this.os.homedir(), "Desktop"),
    timestamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19),
    ...collectArgs
  } = {}) {
    const { report, bytes } = this.buildReport(collectArgs);
    const safeTimestamp = timestamp.replace(/[^0-9TZ-]/g, "");
    const jsonPath = this.path.join(outputDirectory, `Nexus诊断_${safeTimestamp}.json`);
    const textPath = this.path.join(outputDirectory, `Nexus诊断_${safeTimestamp}.txt`);
    this.fs.writeFileSync(jsonPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
    this.fs.writeFileSync(textPath, `${renderText(report)}\n`, "utf8");
    return {
      ok: true,
      path: jsonPath,
      paths: { json: jsonPath, text: textPath },
      bytes: { json: Buffer.byteLength(JSON.stringify(report), "utf8") + 1, text: Buffer.byteLength(renderText(report)) + 1 },
      reportBytes: bytes,
    };
  }
}

module.exports = {
  DIAGNOSTICS_SCHEMA_VERSION,
  DiagnosticsService,
  MAX_RECENT_FRAMES,
  redactIPv4,
  redactIPv6,
  redactMac,
  redactPath,
  sanitize,
};
