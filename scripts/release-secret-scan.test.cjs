"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const {
  isPlaceholderValue,
  scanPackage,
  scanText,
} = require("./release-secret-scan.cjs");

test("detects private keys, URL credentials, and hardcoded credentials", () => {
  const findings = scanText("resources/app/electron/service.js", [
    "const password = 'hunter2';",
    "config.token='secret-token-value';",
    "https://operator:hunter2@plc.local/api",
    "-----BEGIN RSA PRIVATE KEY-----",
  ].join("\n"));
  assert.deepEqual(findings.map(({ kind }) => kind), [
    "hardcoded-credential",
    "hardcoded-credential",
    "url-credentials",
    "private-key-pem",
  ]);
  assert.deepEqual(scanText("service.js", "privateKey: 'AKIA1234567890ABCDEF'"), [{
    path: "service.js",
    line: 1,
    kind: "hardcoded-credential",
    key: "privateKey",
    excerpt: "privateKey=[REDACTED]",
  }]);
  assert.ok(findings.every((finding) => !JSON.stringify(finding).includes("hunter2")));
  assert.ok(findings.every((finding) => !JSON.stringify(finding).includes("secret-token-value")));
});

test("allows credential field names, empty values, and placeholders", () => {
  assert.equal(isPlaceholderValue(""), true);
  assert.equal(isPlaceholderValue("[REDACTED]"), true);
  assert.equal(isPlaceholderValue("${password}"), true);
  assert.equal(isPlaceholderValue("your-token"), true);
  assert.equal(isPlaceholderValue("s7_password"), true);
  assert.deepEqual(scanText("app.js", [
    "const password = '';",
    "const token = '[REDACTED]';",
    "const secret = 'your-password';",
    "request({ password });",
  ].join("\n")), []);
});

function createMemoryFs(files, directoryRoot) {
  return {
    statSync(filePath) {
      if (!files.has(filePath)) {
        const error = new Error("not found");
        error.code = "ENOENT";
        throw error;
      }
      return { size: files.get(filePath).length, isDirectory: () => filePath === directoryRoot };
    },
    readFileSync(filePath) {
      if (!files.has(filePath)) throw new Error("not found");
      return files.get(filePath);
    },
  };
}

test("scans production package text files and reports exact locations", () => {
  const root = path.resolve("D:\\package");
  const files = new Map([
    [root, ""],
    [path.join(root, "resources", "app", "electron", "main.cjs"), "password = 'factory-password'\n"],
    [path.join(root, "resources", "app", "dist", "index.html"), "<script src='/app.js'></script>\n"],
    [path.join(root, "resources", "app", "node_modules", "LICENSE"), "password = 'dependency-license-text'\n"],
    [path.join(root, "resources", "bin", "nexus-rust-core.exe"), "binary-password = 'not-scanned'"],
  ]);
  const result = scanPackage({
    packageRoot: root,
    fsImpl: createMemoryFs(files, root),
    walkImpl: () => [...files.keys()],
  });
  assert.equal(result.ok, false);
  assert.equal(result.scannedFileCount, 2);
  assert.deepEqual(result.findings, [{
    path: "resources/app/electron/main.cjs",
    line: 1,
    kind: "hardcoded-credential",
    key: "password",
    excerpt: "password=[REDACTED]",
  }]);
});

test("portable packaging invokes the release secret scan before metadata generation", () => {
  const fs = require("node:fs");
  const packageScript = fs.readFileSync(path.join(__dirname, "package-portable.ps1"), "utf8");
  const metadataIndex = packageScript.indexOf("generate-release-metadata.cjs");
  const scanIndex = packageScript.indexOf("release-secret-scan.cjs");
  assert.ok(metadataIndex >= 0, "release metadata command is missing");
  assert.ok(scanIndex >= 0 && scanIndex < metadataIndex, "secret scan must run before release metadata");
  assert.match(packageScript, /if \(\$LASTEXITCODE -ne 0\) \{\s*throw "生产包敏感内容扫描失败/);
  assert.match(packageScript, /-Filter "\*\.test\.cjs" \| Remove-Item -Force/);
  assert.match(packageScript, /Join-Path \$packagedElectron "fixtures"/);
});
