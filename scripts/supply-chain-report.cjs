"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");

const SUPPLY_REPORT_SCHEMA_VERSION = 1;

function reportError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function sha256Text(text) {
  return crypto.createHash("sha256").update(text).digest("hex");
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

function parseNpmLock(raw) {
  let lock;
  try {
    lock = JSON.parse(raw);
  } catch (error) {
    throw reportError("SUPPLY_NPM_LOCK_INVALID", `package-lock.json 解析失败：${error.message}`);
  }
  if (lock.lockfileVersion !== 3) {
    throw reportError("SUPPLY_NPM_LOCK_VERSION_UNSUPPORTED", `仅支持 package-lock v3，当前 ${lock.lockfileVersion}`);
  }
  const packages = lock.packages && typeof lock.packages === "object" ? lock.packages : {};
  return Object.entries(packages).map(([key, entry]) => {
    if (!entry || typeof entry !== "object") return null;
    const name = entry.name || packageNameFromLockKey(key);
    if (!name || typeof entry.version !== "string") return null;
    return {
      name,
      version: entry.version,
      scope: key === "" ? "application" : (entry.dev === true ? "dev" : "production"),
      resolved: typeof entry.resolved === "string" ? entry.resolved : null,
      integrity: typeof entry.integrity === "string" ? entry.integrity : null,
      optional: entry.optional === true,
    };
  }).filter(Boolean);
}

function parseCargoLock(raw) {
  const lines = raw.split(/\r?\n/);
  const packages = [];
  let current = null;
  let section = null;
  for (const line of lines) {
    if (line === "[[package]]") {
      if (current?.name && current?.version) packages.push(current);
      current = {};
      section = "package";
      continue;
    }
    if (line.startsWith("[") && line.endsWith("]")) {
      if (current?.name && current?.version) packages.push(current);
      current = null;
      section = line;
      continue;
    }
    if (section !== "package" || !current) continue;
    const match = /^([A-Za-z0-9_-]+)\s*=\s*"([^"]*)"$/.exec(line);
    if (!match) continue;
    const [, field, value] = match;
    if (field === "name" || field === "version" || field === "source" || field === "checksum") {
      current[field] = value;
    }
  }
  if (current?.name && current?.version) packages.push(current);
  if (packages.length === 0) {
    throw reportError("SUPPLY_CARGO_LOCK_INVALID", "Cargo.lock 中没有可解析的 package 条目");
  }
  return packages.map((entry) => ({
    name: entry.name,
    version: entry.version,
    source: entry.source ?? null,
    checksum: entry.checksum ?? null,
  }));
}

function parseRustToolchain(raw) {
  const channel = /\bchannel\s*=\s*"([^"]+)"/.exec(raw)?.[1];
  const profile = /\bprofile\s*=\s*"([^"]+)"/.exec(raw)?.[1];
  if (!channel) {
    throw reportError("SUPPLY_RUST_TOOLCHAIN_INVALID", "rust-toolchain.toml 缺少 channel");
  }
  return { channel, profile: profile ?? null };
}

function buildReport({
  packageJson,
  npmLockText,
  cargoLockText,
  nvmrcText,
  rustToolchainText,
  generatedAt = new Date().toISOString(),
} = {}) {
  if (!packageJson || typeof packageJson !== "object") {
    throw reportError("SUPPLY_PACKAGE_JSON_INVALID", "package.json 必须是对象");
  }
  const npmPackages = parseNpmLock(npmLockText ?? "");
  const cargoPackages = parseCargoLock(cargoLockText ?? "");
  const rustToolchain = parseRustToolchain(rustToolchainText ?? "");
  const nodeVersion = String(nvmrcText ?? "").trim();
  if (!/^\d+\.\d+\.\d+$/.test(nodeVersion)) {
    throw reportError("SUPPLY_NODE_VERSION_INVALID", ".nvmrc 必须是精确 Node 版本");
  }
  if (packageJson.engines?.node !== nodeVersion) {
    throw reportError("SUPPLY_NODE_VERSION_MISMATCH", "package engines.node 与 .nvmrc 不一致");
  }

  return {
    schemaVersion: SUPPLY_REPORT_SCHEMA_VERSION,
    generatedAt,
    product: {
      name: packageJson.productName ?? packageJson.name,
      version: packageJson.version,
    },
    toolchain: {
      node: nodeVersion,
      npm: packageJson.engines?.npm ?? null,
      rust: rustToolchain,
    },
    locks: {
      npm: {
        lockfileVersion: 3,
        sha256: sha256Text(npmLockText ?? ""),
        packageCount: npmPackages.length,
        productionCount: npmPackages.filter((entry) => entry.scope === "production").length,
        devCount: npmPackages.filter((entry) => entry.scope === "dev").length,
        applicationCount: npmPackages.filter((entry) => entry.scope === "application").length,
        packages: npmPackages,
      },
      cargo: {
        formatVersion: 4,
        sha256: sha256Text(cargoLockText ?? ""),
        packageCount: cargoPackages.length,
        registryCount: cargoPackages.filter((entry) => entry.source?.startsWith("registry+")).length,
        localCount: cargoPackages.filter((entry) => !entry.source).length,
        packages: cargoPackages,
      },
    },
  };
}

function renderMarkdown(report) {
  const npm = report.locks.npm;
  const cargo = report.locks.cargo;
  return [
    "# Nexus 2.0 Dependency Inventory",
    "",
    `Generated at: ${report.generatedAt}`,
    `Product: ${report.product.name} ${report.product.version}`,
    "",
    "## Locked toolchain",
    "",
    `- Node: ${report.toolchain.node}`,
    `- npm: ${report.toolchain.npm}`,
    `- Rust: ${report.toolchain.rust.channel} (${report.toolchain.rust.profile ?? "default profile"})`,
    "",
    "## npm lock",
    "",
    `- SHA-256: ${npm.sha256}`,
    `- Packages: ${npm.packageCount}`,
    `- Production entries: ${npm.productionCount}`,
    `- Dev entries: ${npm.devCount}`,
    "",
    "## Cargo lock",
    "",
    `- SHA-256: ${cargo.sha256}`,
    `- Packages: ${cargo.packageCount}`,
    `- Registry crates: ${cargo.registryCount}`,
    `- Local workspace packages: ${cargo.localCount}`,
    "",
    "The JSON report contains the complete name/version/integrity/checksum inventory.",
    "",
  ].join("\n");
}

function generate({ root = process.cwd(), outputDirectory = path.join(root, "evidence", "supply-chain") } = {}) {
  const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  const npmLockText = fs.readFileSync(path.join(root, "package-lock.json"), "utf8");
  const cargoLockText = fs.readFileSync(path.join(root, "rust-core", "Cargo.lock"), "utf8");
  const nvmrcText = fs.readFileSync(path.join(root, ".nvmrc"), "utf8");
  const rustToolchainText = fs.readFileSync(path.join(root, "rust-core", "rust-toolchain.toml"), "utf8");
  const report = buildReport({ packageJson, npmLockText, cargoLockText, nvmrcText, rustToolchainText });
  fs.mkdirSync(outputDirectory, { recursive: true });
  const jsonPath = path.join(outputDirectory, "dependency-report.json");
  const markdownPath = path.join(outputDirectory, "dependency-report.md");
  fs.writeFileSync(jsonPath, `${JSON.stringify(report, null, 2)}\n`, "utf8");
  fs.writeFileSync(markdownPath, renderMarkdown(report), "utf8");
  return { report, paths: { json: jsonPath, markdown: markdownPath } };
}

module.exports = {
  SUPPLY_REPORT_SCHEMA_VERSION,
  buildReport,
  generate,
  parseCargoLock,
  parseNpmLock,
  renderMarkdown,
};

if (require.main === module) {
  try {
    const result = generate();
    console.log(`SUPPLY_REPORT_OK json=${result.paths.json} markdown=${result.paths.markdown}`);
  } catch (error) {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  }
}
