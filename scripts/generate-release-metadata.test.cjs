"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  MANIFEST_SCHEMA_VERSION,
  buildReleaseMetadata,
  npmNameFromLockPath,
  parseCargoLock,
  parsePackageLock,
  verifyReleaseMetadata,
} = require("./generate-release-metadata.cjs");

function sha256(data) {
  return crypto.createHash("sha256").update(data).digest("hex");
}

test("lockfile parsers extract production, runtime, and Cargo components", () => {
  assert.equal(npmNameFromLockPath("node_modules/@scope/name"), "@scope/name");
  assert.equal(npmNameFromLockPath("node_modules/serialport"), "serialport");
  assert.equal(npmNameFromLockPath("node_modules/@scope/a/node_modules/b"), "b");

  const npm = parsePackageLock({
    name: "nexus-rust",
    packages: {
      "": { name: "nexus-rust", version: "0.1.0" },
      "node_modules/serialport": { version: "13.0.0", license: "MIT" },
      "node_modules/@tauri-apps/api": { version: "2.8.0", license: "MIT" },
      "node_modules/electron": { version: "43.1.0", license: "MIT", dev: true },
      "node_modules/aedes": { version: "1.1.1", license: "MIT", dev: true },
    },
  }, { runtimeDependencies: ["@tauri-apps/api", "serialport"] });
  const names = npm.map(entry => entry.name);
  assert.deepEqual(names, ["nexus-rust", "serialport", "@tauri-apps/api", "electron"]);
  assert.ok(!names.includes("aedes"));

  const cargo = parseCargoLock(`
[[package]]
name = "nexus-rust-core"
version = "0.1.0"
dependencies = [
 "serde",
]

[[package]]
name = "serde"
version = "1.0.229"
source = "registry+https://github.com/rust-lang/crates.io-index"
checksum = "abc"
`);
  assert.equal(cargo.length, 2);
  assert.deepEqual(cargo[0], {
    name: "nexus-rust-core",
    version: "0.1.0",
    source: null,
    checksum: null,
    dependencies: ["serde"],
  });
  assert.equal(cargo[1].checksum, "abc");
});

test("release metadata writes SPDX SBOM, manifest, and checksums then verifies them", async () => {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-release-"));
  const sourceRoot = path.join(temporaryRoot, "source");
  const packageRoot = path.join(temporaryRoot, "package");
  const metadataRoot = path.join(temporaryRoot, "metadata");
  fs.mkdirSync(path.join(sourceRoot, "rust-core"), { recursive: true });
  fs.mkdirSync(path.join(packageRoot, "resources", "app"), { recursive: true });
  fs.mkdirSync(path.join(packageRoot, "resources", "bin"), { recursive: true });

  fs.writeFileSync(path.join(sourceRoot, "package.json"), JSON.stringify({
    name: "nexus-rust",
    productName: "Nexus 2.0",
    version: "9.9.9",
    license: "MIT",
    dependencies: { serialport: "^13.0.0" },
    devDependencies: { electron: "43.1.0" },
  }));
  fs.writeFileSync(path.join(sourceRoot, "package-lock.json"), JSON.stringify({
    name: "nexus-rust",
    lockfileVersion: 3,
    packages: {
      "": { name: "nexus-rust", version: "9.9.9" },
      "node_modules/serialport": { version: "13.0.0", license: "MIT", resolved: "https://example.invalid/serialport" },
      "node_modules/electron": { version: "43.1.0", license: "MIT", dev: true },
      "node_modules/vite": { version: "7.1.0", license: "MIT", dev: true },
    },
  }));
  fs.writeFileSync(path.join(sourceRoot, "rust-core", "Cargo.lock"), `
[[package]]
name = "nexus-rust-core"
version = "9.9.9"

[[package]]
name = "serde"
version = "1.0.229"
source = "registry+https://github.com/rust-lang/crates.io-index"
checksum = "abc"
`);
  fs.writeFileSync(path.join(packageRoot, "resources", "app", "THIRD_PARTY_NOTICES.md"), "NOTICE\n");
  fs.writeFileSync(path.join(packageRoot, "resources", "bin", "nexus-rust-core.exe"), "rust-core\n");
  fs.writeFileSync(path.join(packageRoot, "resources", "app", "index.html"), "<html></html>\n");
  fs.writeFileSync(path.join(packageRoot, "Nexus 2.0.exe"), "app\n");

  const generated = await buildReleaseMetadata({
    packageRoot,
    metadataRoot,
    sourceRoot,
    packageType: "portable-test",
    allowDirty: true,
    timestamp: "2026-08-23T12:00:00.000Z",
  });
  assert.equal(generated.manifest.schemaVersion, MANIFEST_SCHEMA_VERSION);
  assert.equal(generated.manifest.qualification, "candidate");
  assert.equal(generated.manifest.source.dirty, null);
  assert.equal(generated.manifest.counts.files, 4);
  assert.ok(generated.manifest.components === undefined);
  assert.ok(fs.existsSync(path.join(metadataRoot, "sbom.spdx.json")));
  assert.ok(fs.existsSync(path.join(metadataRoot, "SHA256SUMS.txt")));

  const sbom = JSON.parse(fs.readFileSync(path.join(metadataRoot, "sbom.spdx.json"), "utf8"));
  assert.equal(sbom.spdxVersion, "SPDX-2.3");
  assert.equal(sbom.packages.length, 5);
  assert.ok(sbom.packages.some(entry => entry.name === "electron"));
  assert.ok(sbom.packages.some(entry => entry.name === "serde"));
  assert.ok(!sbom.packages.some(entry => entry.name === "vite"));

  const verification = await verifyReleaseMetadata({ packageRoot, metadataRoot });
  assert.equal(verification.verified, true);
  assert.equal(verification.files, 4);
  assert.equal(verification.qualification, "candidate");

  fs.appendFileSync(path.join(packageRoot, "resources", "app", "index.html"), "tampered");
  await assert.rejects(
    verifyReleaseMetadata({ packageRoot, metadataRoot }),
    (error) => error.code === "RELEASE_HASH_MISMATCH",
  );
  fs.writeFileSync(path.join(packageRoot, "resources", "app", "index.html"), "<html></html>\n");
  const checksumPath = path.join(metadataRoot, "SHA256SUMS.txt");
  fs.writeFileSync(checksumPath, `${fs.readFileSync(checksumPath, "utf8")}0  extra.txt\n`);
  await assert.rejects(
    verifyReleaseMetadata({ packageRoot, metadataRoot }),
    (error) => error.code === "RELEASE_CHECKSUM_MANIFEST_MISMATCH",
  );
  fs.writeFileSync(checksumPath, fs.readFileSync(checksumPath, "utf8").replace("0  extra.txt\n", ""));
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
});

test("formal metadata refuses a source tree without clean git evidence", async () => {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-release-source-"));
  try {
    await assert.rejects(
      buildReleaseMetadata({
        packageRoot: temporaryRoot,
        metadataRoot: path.join(temporaryRoot, "metadata"),
        sourceRoot: temporaryRoot,
      }),
      (error) => error.code === "RELEASE_SOURCE_DIRTY",
    );
  } finally {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
  }
});

test("portable packaging invokes release metadata and checks notice and core hashes", () => {
  const packaging = fs.readFileSync(path.join(__dirname, "package-portable.ps1"), "utf8");
  assert.match(packaging, /generate-release-metadata\.cjs/);
  assert.match(packaging, /--allow-dirty/);
  assert.match(packaging, /\[switch\]\$AllowDirty/);
  assert.match(packaging, /release-manifest\.json/);
  assert.match(packaging, /SHA256SUMS\.txt/);
});
