"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const {
  collectToolchain,
  normalizeSemanticVersion,
  parseRustToolchain,
  readPackageRequirements,
  verifyToolchainConfig,
} = require("./toolchain-preflight.cjs");

test("semantic versions are exact and normalized", () => {
  assert.deepEqual(normalizeSemanticVersion("v26.1.0"), { major: 26, minor: 1, patch: 0, text: "26.1.0" });
  assert.equal(normalizeSemanticVersion("26.x"), null);
  assert.deepEqual(parseRustToolchain("[toolchain]\nchannel = \"1.97.1\"\nprofile = \"minimal\""), {
    channel: "1.97.1",
    profile: "minimal",
  });
  assert.throws(() => parseRustToolchain("[toolchain]\nchannel = \"stable\""), error => error.code === "TOOLCHAIN_CONFIG_INVALID");
});

test("locked toolchain config is coherent across nvmrc, engines, lockfile, and Cargo", () => {
  const expected = readPackageRequirements();
  assert.equal(expected.node.text, "26.1.0");
  assert.equal(expected.npm.text, "11.13.0");
  assert.equal(expected.electron, "43.1.0");
  assert.equal(expected.rustMinimum.text, "1.85.0");
  const config = verifyToolchainConfig();
  assert.deepEqual(config, {
    ok: true,
    node: "26.1.0",
    npm: "11.13.0",
    electron: "43.1.0",
    rust: "1.97.1",
    rustMinimum: "1.85.0",
  });
});

test("preflight verifies the actual local toolchain and Windows x64 build host", async () => {
  const result = await collectToolchain({
    platform: process.platform === "win32" ? "win32" : "linux",
    arch: process.arch === "x64" ? "x64" : "arm64",
  }).catch(error => error);
  if (process.platform !== "win32" || process.arch !== "x64") {
    assert.equal(result.code, process.platform === "win32" ? "TOOLCHAIN_ARCH_UNSUPPORTED" : "TOOLCHAIN_PLATFORM_UNSUPPORTED");
    return;
  }
  assert.deepEqual(result, {
    ok: true,
    platform: "win32",
    arch: "x64",
    node: "26.1.0",
    npm: "11.13.0",
    electron: "43.1.0",
    rust: "1.97.1",
    rustMinimum: "1.85.0",
    rustToolchain: { channel: "1.97.1", profile: "minimal" },
  });
});
