"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  archiveReleaseNotes,
  validateReleaseNotesMarkdown,
  verifyReleaseNotesArchive,
} = require("./release-notes.cjs");
const {
  createRollbackPlan,
  verifyRollbackPlan,
} = require("./release-rollback.cjs");

function writePackage(root, { commit, version, core = "core\n" }) {
  fs.mkdirSync(path.join(root, "resources", "app"), { recursive: true });
  fs.mkdirSync(path.join(root, "resources", "bin"), { recursive: true });
  fs.writeFileSync(path.join(root, "resources", "app", "THIRD_PARTY_NOTICES.md"), "NOTICE\n");
  fs.writeFileSync(path.join(root, "resources", "app", "index.html"), `<html>${version}</html>\n`);
  fs.writeFileSync(path.join(root, "resources", "bin", "nexus-rust-core.exe"), core);
  fs.writeFileSync(path.join(root, "Nexus 2.0.exe"), `app-${version}\n`);
  return root;
}

function manifestFor(commit, version) {
  return {
    schemaVersion: 1,
    build: { productName: "2.0", applicationVersion: version },
    source: { commit, dirty: false },
    qualification: "release-candidate",
    sbom: { path: "sbom.spdx.json" },
  };
}

test("release notes require formal manifest, fixed sections, and explicit boundaries", () => {
  const commit = "a".repeat(40);
  const markdown = [
    "# Nexus 2.0 9.9.9",
    "",
    `Release-Commit: ${commit}`,
    "",
    "## 新增",
    "- Release metadata.",
    "",
    "## 修复",
    "- Soak harness.",
    "",
    "## 限制",
    "- Portable only.",
    "",
    "## 未验证项",
    "- 未完成 8 小时长稳。",
    "- 真实设备 L2 未完成。",
    "",
    "## 回滚",
    "回滚包：`previous.zip`",
    "回滚验证：`node scripts/release-rollback.cjs verify --plan rollback.json`",
  ].join("\n");
  const summary = validateReleaseNotesMarkdown(markdown, manifestFor(commit, "9.9.9"));
  assert.equal(summary.version, "9.9.9");
  assert.equal(summary.sourceCommit, commit);

  assert.throws(() => validateReleaseNotesMarkdown(markdown, manifestFor("b".repeat(40), "9.9.9")),
    error => error.code === "RELEASE_NOTES_COMMIT_MISMATCH");
  const dirty = manifestFor(commit, "9.9.9");
  dirty.source.dirty = true;
  assert.throws(() => validateReleaseNotesMarkdown(markdown, dirty),
    error => error.code === "RELEASE_NOTES_NOT_FORMAL");
});

test("release notes archive and rollback plan verify against package metadata", async () => {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-rollout-"));
  const currentPackage = writePackage(path.join(temporaryRoot, "current-package"), { version: "9.9.9", core: "current\n" });
  const previousPackage = writePackage(path.join(temporaryRoot, "previous-package"), { version: "9.9.8", core: "previous\n" });
  const currentMetadata = path.join(temporaryRoot, "current-metadata");
  const previousMetadata = path.join(temporaryRoot, "previous-metadata");
  for (const [metadataRoot, manifest] of [
    [currentMetadata, manifestFor("1".repeat(40), "9.9.9")],
    [previousMetadata, manifestFor("2".repeat(40), "9.9.8")],
  ]) {
    fs.mkdirSync(metadataRoot, { recursive: true });
    fs.writeFileSync(path.join(metadataRoot, "release-manifest.json"), JSON.stringify(manifest, null, 2));
  }

  // Copy real metadata generation/verification behavior by using artifact-compatible manifests.
  // The rollback verifier delegates to release metadata verification, so create SHA256SUMS and SBOM.
  for (const metadataRoot of [currentMetadata, previousMetadata]) {
    const manifestPath = path.join(metadataRoot, "release-manifest.json");
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    manifest.artifacts = [];
    manifest.notices = { path: "resources/app/THIRD_PARTY_NOTICES.md" };
    const sbom = `${JSON.stringify({
      spdxVersion: "SPDX-2.3",
      packages: [{ name: "nexus-rust", SPDXID: "SPDXRef-Package-0" }],
    }, null, 2)}\n`;
    manifest.sbom = { path: "sbom.spdx.json", sha256: crypto.createHash("sha256").update(sbom).digest("hex") };
    fs.writeFileSync(path.join(metadataRoot, "sbom.spdx.json"), sbom);
    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
  }

  const notesPath = path.join(temporaryRoot, "release-notes.md");
  const notes = [
    "# Nexus 2.0 9.9.9",
    "",
    `Release-Commit: ${"1".repeat(40)}`,
    "",
    "## 新增",
    "- Release metadata.",
    "",
    "## 修复",
    "- Soak harness.",
    "",
    "## 限制",
    "- Portable only.",
    "",
    "## 未验证项",
    "- 未完成 8 小时长稳。",
    "- 真实设备 L2 未完成。",
    "",
    "## 回滚",
    "回滚包：`previous.zip`",
    "回滚验证：`node scripts/release-rollback.cjs verify --plan rollback.json`",
  ].join("\n");
  fs.writeFileSync(notesPath, notes);
  const archivePath = path.join(temporaryRoot, "release-notes.json");
  const archive = archiveReleaseNotes({
    notesPath,
    manifestPath: path.join(currentMetadata, "release-manifest.json"),
    outputPath: archivePath,
  });
  assert.equal(archive.sourceCommit, "1".repeat(40));
  assert.deepEqual(verifyReleaseNotesArchive({
    archivePath,
    notesPath,
    manifestPath: path.join(currentMetadata, "release-manifest.json"),
  }), { verified: true, version: "9.9.9", sourceCommit: "1".repeat(40) });

  // Populate artifacts in each copied manifest after note validation, then verify rollback pairing.
  for (const metadataRoot of [currentMetadata, previousMetadata]) {
    const manifestPath = path.join(metadataRoot, "release-manifest.json");
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
    const packageRoot = metadataRoot === currentMetadata ? currentPackage : previousPackage;
    const files = [
      "Nexus 2.0.exe",
      "resources/app/index.html",
      "resources/app/THIRD_PARTY_NOTICES.md",
      "resources/bin/nexus-rust-core.exe",
    ].map(relative => {
      const bytes = fs.statSync(path.join(packageRoot, relative)).size;
      const sha256 = crypto.createHash("sha256").update(fs.readFileSync(path.join(packageRoot, relative))).digest("hex");
      return { path: relative, bytes, sha256 };
    });
    manifest.artifacts = files;
    manifest.counts = { files: files.length, components: 1 };
    fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
    fs.writeFileSync(path.join(metadataRoot, "SHA256SUMS.txt"), files.map(entry => `${entry.sha256}  ${entry.path}`).join("\n") + "\n");
  }
  const rollbackPath = path.join(temporaryRoot, "rollback-plan.json");
  const plan = await createRollbackPlan({
    currentPackageRoot: currentPackage,
    currentMetadataRoot: currentMetadata,
    previousPackageRoot: previousPackage,
    previousMetadataRoot: previousMetadata,
    outputPath: rollbackPath,
  });
  assert.equal(plan.current.applicationVersion, "9.9.9");
  assert.equal(plan.previous.applicationVersion, "9.9.8");
  const verification = await verifyRollbackPlan({ planPath: rollbackPath });
  assert.equal(verification.verified, true);
  assert.equal(verification.current.files, 4);
  assert.equal(verification.previous.files, 4);
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
});
