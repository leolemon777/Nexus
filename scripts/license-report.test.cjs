"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {
  LICENSE_REPORT_SCHEMA_VERSION,
  buildReport,
  cargoInventory,
  npmInventory,
  renderMarkdown,
  generate,
} = require("./license-report.cjs");

const packageLockText = JSON.stringify({
  name: "nexus-rust",
  version: "0.1.0",
  lockfileVersion: 3,
  packages: {
    "": { name: "nexus-rust", version: "0.1.0" },
    "node_modules/serialport": { name: "serialport", version: "13.0.0", license: "MIT" },
    "node_modules/vite": { name: "vite", version: "7.1.0", dev: true, license: "MIT" },
  },
}, null, 2);

const cargoMetadataJson = JSON.stringify({
  packages: [
    { name: "nexus-rust-core", version: "0.1.0", license: "MIT" },
    { name: "serde", version: "1.0.229", license: "MIT OR Apache-2.0" },
    { name: "memchr", version: "2.8.3", license: "Unlicense OR MIT" },
  ],
});

const noticesText = [
  "serialport @tauri-apps/api Aedes MQTT.js bacstack knx",
  "serde serde_json thiserror",
].join("\n");

test("extracts only production npm licenses and validates SPDX expressions", () => {
  const npm = npmInventory(packageLockText);
  assert.deepEqual(npm, [{ name: "serialport", version: "13.0.0", license: "MIT", scope: "production" }]);
  assert.throws(() => npmInventory(JSON.stringify({
    lockfileVersion: 3,
    packages: { "node_modules/bad": { name: "bad", version: "1.0.0" } },
  })), (error) => error.code === "LICENSE_MISSING");
  assert.throws(() => npmInventory(JSON.stringify({
    lockfileVersion: 3,
    packages: { "node_modules/bad": { name: "bad", version: "1.0.0", license: "not spdx" } },
  })), (error) => error.code === "LICENSE_INVALID");
});

test("extracts Cargo licenses and rejects missing expressions", () => {
  const cargo = cargoInventory(cargoMetadataJson);
  assert.deepEqual(cargo, [
    { name: "serde", version: "1.0.229", license: "MIT OR Apache-2.0", source: "cargo" },
    { name: "memchr", version: "2.8.3", license: "Unlicense OR MIT", source: "cargo" },
  ]);
  assert.throws(() => cargoInventory(JSON.stringify({
    packages: [{ name: "bad", version: "1.0.0" }],
  })), (error) => error.code === "LICENSE_MISSING");
});

test("builds and renders the production license inventory with NOTICE hash", () => {
  const report = buildReport({
    packageLockText,
    cargoMetadataJson,
    noticesText,
    generatedAt: "2026-08-23T00:00:00.000Z",
  });
  assert.equal(report.schemaVersion, LICENSE_REPORT_SCHEMA_VERSION);
  assert.equal(report.npm.packageCount, 1);
  assert.equal(report.cargo.packageCount, 2);
  assert.deepEqual(report.npm.licenses, [{ license: "MIT", count: 1 }]);
  assert.match(report.noticeSha256, /^[0-9a-f]{64}$/);
  assert.match(renderMarkdown(report), /serialport@13\.0\.0 — MIT/);
  assert.match(renderMarkdown(report), /serde@1\.0\.229 — MIT OR Apache-2\.0/);

  assert.throws(() => buildReport({
    packageLockText,
    cargoMetadataJson,
    noticesText: "missing required names",
  }), (error) => error.code === "LICENSE_NOTICE_MISSING");
});

test("license report is wired and writes real npm/Cargo evidence", async () => {
  const root = path.join(__dirname, "..");
  const packageJson = JSON.parse(fs.readFileSync(path.join(root, "package.json"), "utf8"));
  assert.equal(packageJson.scripts["supply:licenses"], "node scripts/license-report.cjs");

  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-license-report-"));
  try {
    const result = await generate({ root, outputDirectory: directory });
    const persisted = JSON.parse(fs.readFileSync(result.paths.json, "utf8"));
    assert.ok(persisted.npm.packageCount > 0);
    assert.ok(persisted.cargo.packageCount > 0);
    assert.ok(persisted.npm.packages.some((entry) => entry.name === "serialport"));
    assert.ok(persisted.cargo.packages.some((entry) => entry.name === "serde"));
    assert.ok(fs.existsSync(result.paths.markdown));
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
