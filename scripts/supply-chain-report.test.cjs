"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {
  SUPPLY_REPORT_SCHEMA_VERSION,
  buildReport,
  generate,
  parseCargoLock,
  parseNpmLock,
  renderMarkdown,
} = require("./supply-chain-report.cjs");

const npmLockText = JSON.stringify({
  name: "nexus-rust",
  version: "0.1.0",
  lockfileVersion: 3,
  requires: true,
  packages: {
    "": { name: "nexus-rust", version: "0.1.0", dependencies: { serialport: "^13.0.0" } },
    "node_modules/serialport": {
      name: "serialport",
      version: "13.0.0",
      resolved: "https://registry.npmjs.org/serialport/-/serialport-13.0.0.tgz",
      integrity: "sha512-production-integrity",
    },
    "node_modules/vite": {
      name: "vite",
      version: "7.1.0",
      dev: true,
      resolved: "https://registry.npmjs.org/vite/-/vite-7.1.0.tgz",
      integrity: "sha512-dev-integrity",
    },
  },
}, null, 2);

const cargoLockText = [
  "# test lock",
  "version = 4",
  "",
  "[[package]]",
  'name = "nexus-rust-core"',
  'version = "0.1.0"',
  "",
  "[[package]]",
  'name = "serde"',
  'version = "1.0.0"',
  'source = "registry+https://github.com/rust-lang/crates.io-index"',
  'checksum = "cargo-checksum"',
  "",
].join("\n");

const packageJson = {
  name: "nexus-rust",
  version: "0.1.0",
  productName: "Nexus 2.0",
  engines: { node: "26.1.0", npm: "11.13.0" },
};

test("parses complete npm and Cargo lock inventories", () => {
  const npm = parseNpmLock(npmLockText);
  assert.deepEqual(npm.map(({ name, scope }) => ({ name, scope })), [
    { name: "nexus-rust", scope: "application" },
    { name: "serialport", scope: "production" },
    { name: "vite", scope: "dev" },
  ]);
  assert.equal(npm[1].integrity, "sha512-production-integrity");

  const cargo = parseCargoLock(cargoLockText);
  assert.deepEqual(cargo, [
    { name: "nexus-rust-core", version: "0.1.0", source: null, checksum: null },
    {
      name: "serde", version: "1.0.0",
      source: "registry+https://github.com/rust-lang/crates.io-index",
      checksum: "cargo-checksum",
    },
  ]);
});

test("builds a deterministic locked toolchain and dependency report", () => {
  const report = buildReport({
    packageJson,
    npmLockText,
    cargoLockText,
    nvmrcText: "26.1.0\n",
    rustToolchainText: '[toolchain]\nchannel = "1.97.1"\nprofile = "minimal"\n',
    generatedAt: "2026-08-23T00:00:00.000Z",
  });

  assert.equal(report.schemaVersion, SUPPLY_REPORT_SCHEMA_VERSION);
  assert.equal(report.toolchain.node, "26.1.0");
  assert.equal(report.toolchain.npm, "11.13.0");
  assert.deepEqual(report.toolchain.rust, { channel: "1.97.1", profile: "minimal" });
  assert.equal(report.locks.npm.packageCount, 3);
  assert.equal(report.locks.npm.productionCount, 1);
  assert.equal(report.locks.npm.devCount, 1);
  assert.equal(report.locks.cargo.packageCount, 2);
  assert.equal(report.locks.cargo.registryCount, 1);
  assert.equal(report.locks.cargo.localCount, 1);
  assert.match(report.locks.npm.sha256, /^[0-9a-f]{64}$/);
  assert.match(report.locks.cargo.sha256, /^[0-9a-f]{64}$/);
  assert.match(renderMarkdown(report), /## npm lock\n\n- SHA-256: [0-9a-f]{64}\n- Packages: 3/);
  assert.match(renderMarkdown(report), /## Cargo lock\n\n- SHA-256: [0-9a-f]{64}\n- Packages: 2/);
});

test("supply report is wired and writes JSON plus Markdown evidence", () => {
  const root = path.join(__dirname, "..");
  const packageJsonText = fs.readFileSync(path.join(root, "package.json"), "utf8");
  const original = JSON.parse(packageJsonText);
  assert.equal(original.scripts["supply:report"], "node scripts/supply-chain-report.cjs");

  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-supply-report-"));
  try {
    const result = generate({ root, outputDirectory: directory });
    assert.equal(fs.existsSync(result.paths.json), true);
    assert.equal(fs.existsSync(result.paths.markdown), true);
    const persisted = JSON.parse(fs.readFileSync(result.paths.json, "utf8"));
    assert.equal(persisted.product.name, "Nexus 2.0");
    assert.ok(persisted.locks.npm.packageCount > 0);
    assert.ok(persisted.locks.cargo.packageCount > 0);
    assert.ok(persisted.locks.npm.packages.some((entry) => entry.name === "serialport"));
    assert.ok(persisted.locks.cargo.packages.some((entry) => entry.name === "serde"));
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
