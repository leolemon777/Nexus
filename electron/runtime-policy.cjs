const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const DEVELOPMENT_HOSTS = new Set(["127.0.0.1", "localhost"]);
const DEVELOPMENT_PORT = "1420";

function resolveDevelopmentUrl(rawValue, { isPackaged = false } = {}) {
  if (isPackaged || rawValue == null || String(rawValue).trim() === "") return null;

  let url;
  try {
    url = new URL(String(rawValue));
  } catch {
    throw new Error("NEXUS_DEV_SERVER_URL 必须是有效 URL。");
  }

  if (
    url.protocol !== "http:" ||
    !DEVELOPMENT_HOSTS.has(url.hostname) ||
    url.port !== DEVELOPMENT_PORT ||
    url.username ||
    url.password
  ) {
    throw new Error("NEXUS_DEV_SERVER_URL 只允许本机 http://127.0.0.1:1420 或 http://localhost:1420。");
  }

  return `${url.origin}/`;
}

function resolveRustCoreBinaryPath({
  isPackaged,
  resourcesPath,
  projectRoot,
  envPath,
  platform = process.platform,
  existsSync = fs.existsSync,
  realpathSync = fs.realpathSync,
}) {
  const binaryName = platform === "win32" ? "nexus-rust-core.exe" : "nexus-rust-core";
  const candidate = isPackaged
    ? path.join(resourcesPath, "bin", binaryName)
    : envPath || path.join(projectRoot, "rust-core", "target", "debug", binaryName);
  const resolved = path.resolve(candidate);

  if (path.basename(resolved).toLowerCase() !== binaryName.toLowerCase()) {
    throw new Error(`Rust Core 可执行文件名必须是 ${binaryName}。`);
  }

  return existsSync(resolved) ? realpathSync(resolved) : resolved;
}

function isAllowedNavigation(targetUrl, allowedEntryUrl) {
  let target;
  let allowed;
  try {
    target = new URL(targetUrl);
    allowed = new URL(allowedEntryUrl);
  } catch {
    return false;
  }

  if (allowed.protocol === "http:") {
    return target.protocol === "http:" && target.origin === allowed.origin;
  }

  return (
    allowed.protocol === "file:" &&
    target.protocol === "file:" &&
    target.pathname === allowed.pathname
  );
}

function fileEntryUrl(filePath) {
  return pathToFileURL(filePath).href;
}

module.exports = {
  fileEntryUrl,
  isAllowedNavigation,
  resolveDevelopmentUrl,
  resolveRustCoreBinaryPath,
};
