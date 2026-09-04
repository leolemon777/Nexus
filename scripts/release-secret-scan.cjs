"use strict";

const fs = require("node:fs");
const path = require("node:path");

const MAX_FILE_BYTES = 5 * 1024 * 1024;
const MAX_FILES = 20_000;
const SCANNED_EXTENSIONS = new Set([
  ".js", ".cjs", ".mjs", ".json", ".html", ".css", ".txt", ".md", ".ps1",
]);
const SKIP_PATH_FRAGMENTS = [
  "resources/app/node_modules/",
  "/LICENSE",
  "/LICENCE",
  "/NOTICE",
  "LICENSES.chromium.html",
];
const PLACEHOLDER_VALUES = new Set([
  "",
  "[REDACTED]",
  "REDACTED",
  "${password}",
  "${token}",
  "${secret}",
  "PASSWORD",
  "TOKEN",
  "SECRET",
  "changeme",
  "example",
  "placeholder",
  "your-password",
  "your-token",
]);
const CREDENTIAL_KEY_RE = /(?:password|passwd|secret|token|api[_-]?key|private[_-]?key|client[_-]?secret|certificate[_-]?key|access[_-]?token|auth[_-]?token|bearer[_-]?token)/i;
const PEM_RE = /-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----/;
const URL_CREDENTIAL_RE = /\bhttps?:\/\/[^\s/:@"']+:[^\s/@"']+@/i;

function scanError(code, message, findings = []) {
  const error = new Error(message);
  error.code = code;
  error.findings = findings;
  return error;
}

function normalizeRelativePath(relativePath) {
  return String(relativePath ?? "").replace(/\\/g, "/");
}

function shouldSkip(relativePath) {
  const normalized = normalizeRelativePath(relativePath);
  return SKIP_PATH_FRAGMENTS.some((fragment) => normalized.includes(fragment));
}

function isPlaceholderValue(value) {
  const normalized = value.trim();
  if (PLACEHOLDER_VALUES.has(normalized)) return true;
  if (normalized === "s7_password") return true;
  if (/^(?:x+|\*+|\.+|#+|<[^>]+>|\$\{[^}]+\})$/i.test(normalized)) return true;
  return false;
}

function scanText(relativePath, text) {
  const findings = [];
  const normalizedPath = normalizeRelativePath(relativePath);
  const lines = text.split(/\r?\n/);
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    const pem = PEM_RE.test(line);
    if (pem) {
      findings.push({
        path: normalizedPath,
        line: index + 1,
        kind: "private-key-pem",
        excerpt: "-----BEGIN ... PRIVATE KEY-----",
      });
    }
    if (URL_CREDENTIAL_RE.test(line)) {
      findings.push({
        path: normalizedPath,
        line: index + 1,
        kind: "url-credentials",
        excerpt: "https://user:password@...",
      });
    }
    const assignment = new RegExp(
      `["']?(${CREDENTIAL_KEY_RE.source})["']?\\s*[:=]\\s*["']([^"\\\\\\r\\n]{4,})["']`,
      "i",
    ).exec(line);
    if (assignment && !isPlaceholderValue(assignment[2])) {
      findings.push({
        path: normalizedPath,
        line: index + 1,
        kind: "hardcoded-credential",
        key: assignment[1],
        excerpt: `${assignment[1]}=${"[REDACTED]"}`,
      });
    }
  }
  return findings;
}

function walkFiles(root, current = root, output = []) {
  for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
    if (entry.name === ".git" || entry.name === ".DS_Store") continue;
    const filePath = path.join(current, entry.name);
    if (entry.isDirectory()) {
      walkFiles(root, filePath, output);
    } else if (SCANNED_EXTENSIONS.has(path.extname(entry.name).toLowerCase())) {
      output.push(filePath);
    }
  }
  return output;
}

function scanPackage({ packageRoot, fsImpl = fs, walkImpl = walkFiles } = {}) {
  if (!packageRoot || typeof packageRoot !== "string") {
    throw scanError("RELEASE_SCAN_ROOT_REQUIRED", "packageRoot 是必填项");
  }
  const resolvedRoot = path.resolve(packageRoot);
  const stat = fsImpl.statSync(resolvedRoot);
  if (!stat.isDirectory()) {
    throw scanError("RELEASE_SCAN_ROOT_INVALID", "packageRoot 必须是目录");
  }
  const files = walkImpl(resolvedRoot)
    .filter((filePath) => filePath !== resolvedRoot)
    .filter((filePath) => SCANNED_EXTENSIONS.has(path.extname(filePath).toLowerCase()))
    .filter((filePath) => !shouldSkip(path.relative(resolvedRoot, filePath)));
  if (files.length > MAX_FILES) {
    throw scanError("RELEASE_SCAN_TOO_MANY_FILES", `扫描文件数超过 ${MAX_FILES}`);
  }
  const findings = [];
  const scanned = [];
  for (const filePath of files) {
    const fileStat = fsImpl.statSync(filePath);
    if (fileStat.size > MAX_FILE_BYTES) {
      throw scanError("RELEASE_SCAN_FILE_TOO_LARGE", `待扫描文件超过 5 MiB：${filePath}`);
    }
    const relative = normalizeRelativePath(path.relative(resolvedRoot, filePath));
    let text;
    try {
      text = fsImpl.readFileSync(filePath, "utf8");
    } catch (error) {
      throw scanError("RELEASE_SCAN_READ_FAILED", `读取扫描文件失败：${relative}：${error.message}`);
    }
    findings.push(...scanText(relative, text));
    scanned.push(relative);
  }
  return {
    ok: findings.length === 0,
    packageRoot: resolvedRoot,
    scannedFileCount: scanned.length,
    findings,
  };
}

module.exports = {
  MAX_FILE_BYTES,
  MAX_FILES,
  isPlaceholderValue,
  scanPackage,
  scanText,
};

if (require.main === module) {
  try {
    const packageRoot = process.argv[2];
    const result = scanPackage({ packageRoot });
    if (!result.ok) {
      console.error(`RELEASE_SECRET_SCAN_FAILED findings=${result.findings.length}`);
      for (const finding of result.findings) {
        console.error(`${finding.path}:${finding.line} ${finding.kind} ${finding.excerpt ?? finding.key}`);
      }
      process.exitCode = 1;
    } else {
      console.log(`RELEASE_SECRET_SCAN_OK files=${result.scannedFileCount}`);
    }
  } catch (error) {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  }
}
