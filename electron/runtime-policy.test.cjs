const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const {
  fileEntryUrl,
  isAllowedNavigation,
  resolveDevelopmentUrl,
  resolveRustCoreBinaryPath,
} = require("./runtime-policy.cjs");

test("accepts only the fixed local Vite development endpoint", () => {
  assert.equal(resolveDevelopmentUrl("http://127.0.0.1:1420"), "http://127.0.0.1:1420/");
  assert.equal(resolveDevelopmentUrl("http://localhost:1420/anything"), "http://localhost:1420/");
  assert.throws(() => resolveDevelopmentUrl("https://127.0.0.1:1420"), /只允许本机/);
  assert.throws(() => resolveDevelopmentUrl("http://example.com:1420"), /只允许本机/);
  assert.throws(() => resolveDevelopmentUrl("http://127.0.0.1:5173"), /只允许本机/);
});

test("ignores the development URL in packaged builds", () => {
  assert.equal(resolveDevelopmentUrl("http://127.0.0.1:1420", { isPackaged: true }), null);
});

test("packaged Rust Core path cannot be replaced by an environment value", () => {
  const result = resolveRustCoreBinaryPath({
    isPackaged: true,
    resourcesPath: "C:\\Program Files\\Nexus\\resources",
    projectRoot: "C:\\source\\Nexus",
    envPath: "D:\\untrusted\\nexus-rust-core.exe",
    platform: "win32",
    existsSync: () => false,
  });
  assert.equal(result, path.resolve("C:\\Program Files\\Nexus\\resources", "bin", "nexus-rust-core.exe"));
});

test("development override must keep the expected executable name", () => {
  assert.throws(
    () =>
      resolveRustCoreBinaryPath({
        isPackaged: false,
        resourcesPath: "C:\\resources",
        projectRoot: "C:\\source\\Nexus",
        envPath: "D:\\tools\\other.exe",
        platform: "win32",
        existsSync: () => false,
      }),
    /nexus-rust-core\.exe/,
  );
});

test("navigation stays on the selected local origin or exact packaged file", () => {
  assert.equal(
    isAllowedNavigation("http://127.0.0.1:1420/settings", "http://127.0.0.1:1420/"),
    true,
  );
  assert.equal(
    isAllowedNavigation("http://localhost:1420/", "http://127.0.0.1:1420/"),
    false,
  );

  const entry = fileEntryUrl("C:\\Program Files\\Nexus\\resources\\app\\dist\\index.html");
  assert.equal(isAllowedNavigation(`${entry}#serial`, entry), true);
  assert.equal(isAllowedNavigation(fileEntryUrl("C:\\Windows\\win.ini"), entry), false);
});
