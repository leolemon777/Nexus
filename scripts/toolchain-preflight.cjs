"use strict";

const { execFile } = require("node:child_process");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { promisify } = require("node:util");

const execFileAsync = promisify(execFile);
const REPO_ROOT = path.resolve(__dirname, "..");
const SUPPORTED_PLATFORMS = new Set(["win32"]);
const SUPPORTED_ARCHITECTURES = new Set(["x64"]);

function preflightError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

async function commandOutput(program, args, options = {}) {
  try {
    const result = await execFileAsync(program, args, {
      cwd: options.cwd ?? REPO_ROOT,
      windowsHide: true,
      timeout: options.timeoutMs ?? 10_000,
    });
    return result.stdout.trim();
  } catch (error) {
    throw preflightError(options.code ?? "TOOLCHAIN_COMMAND_FAILED", error.message);
  }
}

function normalizeSemanticVersion(raw) {
  const match = /^(?:rustc\s+)?v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\s+\([^)]+\))?(?:([-+].+))?$/.exec(String(raw ?? "").trim());
  if (!match) return null;
  return {
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3] ?? 0),
    text: `${match[1]}.${match[2] ?? 0}.${match[3] ?? 0}`,
  };
}

function parseRustToolchain(raw) {
  const text = String(raw ?? "");
  const channel = text.match(/^\s*channel\s*=\s*"([^"]+)"/m)?.[1];
  const profile = text.match(/^\s*profile\s*=\s*"([^"]+)"/m)?.[1];
  if (!channel || !/^\d+\.\d+\.\d+$/.test(channel)) {
    throw preflightError("TOOLCHAIN_CONFIG_INVALID", "rust-core/rust-toolchain.toml 必须锁定精确 Rust channel");
  }
  if (profile !== "minimal") {
    throw preflightError("TOOLCHAIN_PROFILE_INVALID", "Rust toolchain profile 必须是 minimal");
  }
  return { channel, profile };
}

function readNodeLock() {
  const nodeVersion = fs.readFileSync(path.join(REPO_ROOT, ".nvmrc"), "utf8").trim();
  const normalized = normalizeSemanticVersion(nodeVersion);
  if (!normalized) throw preflightError("NODE_LOCK_INVALID", ".nvmrc 必须是精确 Node x.y.z 版本");
  return normalized;
}

function readPackageRequirements() {
  const packageJson = readJson(path.join(REPO_ROOT, "package.json"));
  const lock = readJson(path.join(REPO_ROOT, "package-lock.json"));
  const nodeEngine = packageJson.engines?.node;
  const npmEngine = packageJson.engines?.npm;
  if (nodeEngine !== readNodeLock().text) {
    throw preflightError("NODE_ENGINE_MISMATCH", "package.json engines.node 必须与 .nvmrc 一致");
  }
  if (!/^\d+\.\d+\.\d+$/.test(npmEngine ?? "")) {
    throw preflightError("NPM_ENGINE_INVALID", "package.json engines.npm 必须是精确 x.y.z 版本");
  }
  const electron = lock.packages?.["node_modules/electron"]?.version;
  if (electron !== packageJson.devDependencies?.electron) {
    throw preflightError("ELECTRON_LOCK_MISMATCH", "package.json 与 package-lock.json 的 Electron 版本必须一致");
  }
  return {
    node: normalizeSemanticVersion(nodeEngine),
    npm: normalizeSemanticVersion(npmEngine),
    electron,
    rustMinimum: (() => {
      const cargoToml = fs.readFileSync(path.join(REPO_ROOT, "rust-core", "Cargo.toml"), "utf8");
      const value = cargoToml.match(/^rust-version\s*=\s*"([^"]+)"/m)?.[1];
      if (!value) throw preflightError("RUST_MINIMUM_INVALID", "Cargo.toml 缺少 rust-version");
      return normalizeSemanticVersion(value);
    })(),
  };
}

function compareVersion(left, right) {
  if (left.major !== right.major) return left.major - right.major;
  if (left.minor !== right.minor) return left.minor - right.minor;
  return left.patch - right.patch;
}

async function collectToolchain({ platform = process.platform, arch = process.arch } = {}) {
  if (!SUPPORTED_PLATFORMS.has(platform)) {
    throw preflightError("TOOLCHAIN_PLATFORM_UNSUPPORTED", `构建平台不支持：${platform}`);
  }
  if (!SUPPORTED_ARCHITECTURES.has(arch)) {
    throw preflightError("TOOLCHAIN_ARCH_UNSUPPORTED", `构建架构不支持：${arch}`);
  }
  const expected = readPackageRequirements();
  const rustToolchain = parseRustToolchain(
    fs.readFileSync(path.join(REPO_ROOT, "rust-core", "rust-toolchain.toml"), "utf8"),
  );
  const actual = {
    node: normalizeSemanticVersion(await commandOutput(process.execPath, ["--version"], { code: "NODE_VERSION_FAILED" })),
    npm: normalizeSemanticVersion(await commandOutput("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "npm.cmd --version"], { code: "NPM_VERSION_FAILED" })),
    rust: normalizeSemanticVersion(await commandOutput("rustc", [`+${rustToolchain.channel}`, "--version"], { code: "RUST_VERSION_FAILED", cwd: path.join(REPO_ROOT, "rust-core") })),
    rustToolchain: await commandOutput("rustup", ["show", "active-toolchain"], {
      code: "RUST_TOOLCHAIN_FAILED",
      cwd: path.join(REPO_ROOT, "rust-core"),
    }),
  };
  if (!actual.node || actual.node.text !== expected.node.text) {
    throw preflightError("NODE_VERSION_MISMATCH", `Node 需要 ${expected.node.text}，实际 ${actual.node?.text ?? "unknown"}`);
  }
  if (!actual.npm || actual.npm.text !== expected.npm.text) {
    throw preflightError("NPM_VERSION_MISMATCH", `npm 需要 ${expected.npm.text}，实际 ${actual.npm?.text ?? "unknown"}`);
  }
  if (!actual.rust || actual.rust.text !== rustToolchain.channel) {
    throw preflightError("RUST_VERSION_MISMATCH", `Rust 需要 ${rustToolchain.channel}，实际 ${actual.rust?.text ?? "unknown"}`);
  }
  if (!actual.rustToolchain.startsWith(`${rustToolchain.channel}-`)) {
    throw preflightError("RUST_TOOLCHAIN_MISMATCH", `rustup active toolchain 需要 ${rustToolchain.channel}，实际 ${actual.rustToolchain}`);
  }
  if (compareVersion(actual.rust, expected.rustMinimum) < 0) {
    throw preflightError("RUST_MINIMUM_MISMATCH", `Rust 低于 Cargo.toml 最低版本 ${expected.rustMinimum.text}`);
  }
  return {
    ok: true,
    platform,
    arch,
    node: actual.node.text,
    npm: actual.npm.text,
    electron: expected.electron,
    rust: actual.rust.text,
    rustMinimum: expected.rustMinimum.text,
    rustToolchain: rustToolchain,
  };
}

function verifyToolchainConfig() {
  const expected = readPackageRequirements();
  const rustToolchain = parseRustToolchain(
    fs.readFileSync(path.join(REPO_ROOT, "rust-core", "rust-toolchain.toml"), "utf8"),
  );
  return {
    ok: true,
    node: expected.node.text,
    npm: expected.npm.text,
    electron: expected.electron,
    rust: rustToolchain.channel,
    rustMinimum: expected.rustMinimum.text,
  };
}

async function main() {
  const result = await collectToolchain();
  process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
}

if (require.main === module) {
  main().catch(error => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}

module.exports = {
  collectToolchain,
  normalizeSemanticVersion,
  parseRustToolchain,
  readPackageRequirements,
  verifyToolchainConfig,
};
