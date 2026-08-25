"use strict";

const { execFile } = require("node:child_process");
const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { promisify } = require("node:util");

const execFileAsync = promisify(execFile);
const LICENSE_REPORT_SCHEMA_VERSION = 1;
const SPDX_LICENSE_EXPRESSION_RE = /^(?:UNLICENSED|SEE LICENSE IN [^\r\n]+|(?:\([^)]*\)|[A-Za-z0-9.+-]+)(?:\s+(?:AND|OR)\s+(?:\([^)]*\)|[A-Za-z0-9.+-]+))*)$/;

function reportError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function packageNameFromLockKey(key) {
  const normalized = key.replace(/\\/g, "/");
  const marker = "/node_modules/";
  let value = normalized;
  if (value.startsWith("node_modules/")) value = value.slice("node_modules/".length);
  const markerIndex = value.lastIndexOf(marker);
  if (markerIndex >= 0) value = value.slice(markerIndex + marker.length);
  return value || null;
}

function validLicenseExpression(value, name, ecosystem) {
  const expression = String(value ?? "").trim();
  if (!expression) {
    throw reportError("LICENSE_MISSING", `${ecosystem} dependency ${name} has no license expression`);
  }
  if (!SPDX_LICENSE_EXPRESSION_RE.test(expression)) {
    throw reportError("LICENSE_INVALID", `${ecosystem} dependency ${name} has invalid license expression: ${expression}`);
  }
  return expression;
}

function npmInventory(packageLockText) {
  let lock;
  try {
    lock = JSON.parse(packageLockText);
  } catch (error) {
    throw reportError("LICENSE_NPM_LOCK_INVALID", `package-lock.json 解析失败：${error.message}`);
  }
  if (lock.lockfileVersion !== 3 || !lock.packages || typeof lock.packages !== "object") {
    throw reportError("LICENSE_NPM_LOCK_UNSUPPORTED", "license report requires package-lock v3");
  }
  return Object.entries(lock.packages).map(([key, entry]) => {
    if (!entry || typeof entry !== "object" || typeof entry.version !== "string") return null;
    const name = entry.name || packageNameFromLockKey(key);
    if (!name) return null;
    const application = key === "";
    const dev = entry.dev === true;
    if (application || dev) return null;
    return {
      name,
      version: entry.version,
      license: validLicenseExpression(entry.license, name, "npm"),
      scope: entry.optional === true ? "optional" : "production",
    };
  }).filter(Boolean);
}

function cargoInventory(cargoMetadataJson) {
  let metadata;
  try {
    metadata = JSON.parse(cargoMetadataJson);
  } catch (error) {
    throw reportError("LICENSE_CARGO_METADATA_INVALID", `cargo metadata 解析失败：${error.message}`);
  }
  if (!Array.isArray(metadata.packages)) {
    throw reportError("LICENSE_CARGO_METADATA_INVALID", "cargo metadata packages 必须是数组");
  }
  return metadata.packages.map((entry) => {
    if (!entry || typeof entry.name !== "string" || typeof entry.version !== "string") return null;
    if (entry.name === "nexus-rust-core") return null;
    return {
      name: entry.name,
      version: entry.version,
      license: validLicenseExpression(entry.license, entry.name, "Cargo"),
      source: typeof entry.manifest_path === "string" ? "cargo" : "cargo",
    };
  }).filter(Boolean);
}

function countsByLicense(entries) {
  return Object.entries(entries.reduce((output, entry) => {
    output[entry.license] = (output[entry.license] ?? 0) + 1;
    return output;
  }, {})).sort(([a], [b]) => a.localeCompare(b)).map(([license, count]) => ({ license, count }));
}

function buildReport({ packageLockText, cargoMetadataJson, noticesText, generatedAt = new Date().toISOString() } = {}) {
  const npm = npmInventory(packageLockText ?? "");
  const cargo = cargoInventory(cargoMetadataJson ?? "{}");
  const requiredNoticeNames = [
    "serialport",
    "@tauri-apps/api",
    "Aedes",
    "MQTT.js",
    "bacstack",
    "knx",
    "serde",
    "serde_json",
    "thiserror",
  ];
  const missingNotices = requiredNoticeNames.filter((name) => !String(noticesText ?? "").includes(name));
  if (missingNotices.length > 0) {
    throw reportError("LICENSE_NOTICE_MISSING", `THIRD_PARTY_NOTICES.md 缺少依赖说明：${missingNotices.join(", ")}`);
  }
  return {
    schemaVersion: LICENSE_REPORT_SCHEMA_VERSION,
    generatedAt,
    noticeSha256: crypto.createHash("sha256").update(noticesText ?? "", "utf8").digest("hex"),
    npm: {
      packageCount: npm.length,
      licenses: countsByLicense(npm),
      packages: npm,
    },
    cargo: {
      packageCount: cargo.length,
      licenses: countsByLicense(cargo),
      packages: cargo,
    },
  };
}

function renderMarkdown(report) {
  return [
    "# Nexus 2.0 Dependency License Inventory",
    "",
    `Generated at: ${report.generatedAt}`,
    `NOTICE SHA-256: ${report.noticeSha256}`,
    "",
    "## npm production dependencies",
    "",
    ...report.npm.packages.map((entry) => `- ${entry.name}@${entry.version} — ${entry.license}`),
    "",
    "## Cargo dependencies",
    "",
    ...report.cargo.packages.map((entry) => `- ${entry.name}@${entry.version} — ${entry.license}`),
    "",
    "Complete SPDX expressions and lock identity are retained in dependency-report.json.",
    "",
  ].join("\n");
}

async function generate({
  root = process.cwd(),
  outputDirectory = path.join(root, "evidence", "supply-chain"),
  execImpl = execFileAsync,
} = {}) {
  const packageLockText = fs.readFileSync(path.join(root, "package-lock.json"), "utf8");
  const noticesText = fs.readFileSync(path.join(root, "THIRD_PARTY_NOTICES.md"), "utf8");
  const cargo = await execImpl("cargo", [
    "metadata",
    "--manifest-path",
    path.join(root, "rust-core", "Cargo.toml"),
    "--format-version",
    "1",
  ], { cwd: root, windowsHide: true, maxBuffer: 16 * 1024 * 1024 });
  const report = buildReport({ packageLockText, cargoMetadataJson: cargo.stdout, noticesText });
  fs.mkdirSync(outputDirectory, { recursive: true });
  const jsonPath = path.join(outputDirectory, "license-report.json");
  const markdownPath = path.join(outputDirectory, "license-report.md");
  fs.writeFileSync(jsonPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
  fs.writeFileSync(markdownPath, renderMarkdown(report), "utf8");
  return { report, paths: { json: jsonPath, markdown: markdownPath } };
}

module.exports = {
  LICENSE_REPORT_SCHEMA_VERSION,
  buildReport,
  cargoInventory,
  generate,
  npmInventory,
  renderMarkdown,
};

if (require.main === module) {
  generate().then((result) => {
    console.log(`LICENSE_REPORT_OK json=${result.paths.json} markdown=${result.paths.markdown}`);
  }).catch((error) => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}
