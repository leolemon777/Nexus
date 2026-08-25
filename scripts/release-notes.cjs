"use strict";

const fs = require("node:fs");
const path = require("node:path");

const RELEASE_NOTES_SCHEMA_VERSION = 1;
const REQUIRED_SECTIONS = [
  "## 新增",
  "## 修复",
  "## 限制",
  "## 未验证项",
  "## 回滚",
];

function notesError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

function sha256Text(text) {
  return require("node:crypto").createHash("sha256").update(text).digest("hex");
}

function validateReleaseNotesMarkdown(markdown, manifest) {
  const text = String(markdown ?? "");
  const version = `# Nexus ${manifest.build.productName} ${manifest.build.applicationVersion}`;
  if (!text.startsWith(`${version}\n`)) {
    throw notesError("RELEASE_NOTES_VERSION_INVALID", `发布说明必须以「${version}」开头`);
  }
  if (!/^Release-Commit: `?[0-9a-f]{40}`?$/m.test(text)) {
    throw notesError("RELEASE_NOTES_COMMIT_MISSING", "发布说明缺少 40 位 Release-Commit");
  }
  const commit = text.match(/^Release-Commit: `?([0-9a-f]{40})`?$/m)?.[1];
  if (manifest.source.commit && commit !== manifest.source.commit) {
    throw notesError("RELEASE_NOTES_COMMIT_MISMATCH", "发布说明提交与 release manifest 不一致");
  }
  if (manifest.qualification !== "release-candidate" || manifest.source.dirty !== false) {
    throw notesError("RELEASE_NOTES_NOT_FORMAL", "formal 发布说明只能对应 clean release-candidate manifest");
  }
  for (const section of REQUIRED_SECTIONS) {
    const occurrences = text.split(section).length - 1;
    if (occurrences !== 1) {
      throw notesError("RELEASE_NOTES_SECTION_INVALID", `发布说明章节「${section}」必须恰好出现一次`);
    }
  }
  if (!/^回滚包：`.+`$/m.test(text) || !/^回滚验证：`.+`$/m.test(text)) {
    throw notesError("RELEASE_NOTES_ROLLBACK_MISSING", "回滚章节必须包含回滚包与回滚验证命令");
  }
  if (!text.includes("未完成 8 小时长稳") || !text.includes("真实设备 L2 未完成")) {
    throw notesError("RELEASE_NOTES_BOUNDARY_MISSING", "未验证项必须明确 8 小时长稳和真实设备 L2 未完成");
  }
  return {
    version: manifest.build.applicationVersion,
    sourceCommit: manifest.source.commit,
    sha256: sha256Text(text),
    bytes: Buffer.byteLength(text, "utf8"),
  };
}

function archiveReleaseNotes({ notesPath, manifestPath, outputPath }) {
  if (!notesPath || !manifestPath || !outputPath) {
    throw notesError("RELEASE_NOTES_PATH_REQUIRED", "notesPath、manifestPath 和 outputPath 都是必填项");
  }
  const markdown = fs.readFileSync(notesPath, "utf8");
  const manifest = readJson(manifestPath);
  const summary = validateReleaseNotesMarkdown(markdown, manifest);
  const archive = {
    schemaVersion: RELEASE_NOTES_SCHEMA_VERSION,
    kind: "nexus-release-notes",
    ...summary,
    notesPath: path.basename(notesPath),
    manifestPath: path.basename(manifestPath),
    manifestSha256: sha256Text(fs.readFileSync(manifestPath, "utf8")),
    markdown,
  };
  fs.mkdirSync(path.dirname(path.resolve(outputPath)), { recursive: true });
  fs.writeFileSync(outputPath, `${JSON.stringify(archive, null, 2)}\n`, "utf8");
  return archive;
}

function verifyReleaseNotesArchive({ archivePath, notesPath, manifestPath }) {
  if (!archivePath || !notesPath || !manifestPath) {
    throw notesError("RELEASE_NOTES_PATH_REQUIRED", "archivePath、notesPath 和 manifestPath 都是必填项");
  }
  const archive = readJson(archivePath);
  if (archive.schemaVersion !== RELEASE_NOTES_SCHEMA_VERSION) {
    throw notesError("RELEASE_NOTES_VERSION_UNSUPPORTED", `不支持发布说明归档版本 ${archive.schemaVersion}`);
  }
  const markdown = fs.readFileSync(notesPath, "utf8");
  const manifest = readJson(manifestPath);
  const current = validateReleaseNotesMarkdown(markdown, manifest);
  if (current.sha256 !== archive.sha256 || current.bytes !== archive.bytes) {
    throw notesError("RELEASE_NOTES_HASH_MISMATCH", "发布说明与归档哈希不一致");
  }
  const manifestHash = sha256Text(fs.readFileSync(manifestPath, "utf8"));
  if (manifestHash !== archive.manifestSha256) {
    throw notesError("RELEASE_NOTES_MANIFEST_MISMATCH", "release manifest 与归档哈希不一致");
  }
  return {
    verified: true,
    version: current.version,
    sourceCommit: current.sourceCommit,
  };
}

function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    const value = argv[index + 1];
    if (!value) throw notesError("RELEASE_NOTES_ARG_INVALID", `缺少参数值：${name}`);
    if (name === "--notes") options.notesPath = path.resolve(value);
    else if (name === "--manifest") options.manifestPath = path.resolve(value);
    else if (name === "--output") options.outputPath = path.resolve(value);
    else if (name === "--archive") options.archivePath = path.resolve(value);
    else throw notesError("RELEASE_NOTES_ARG_INVALID", `未知参数：${name}`);
  }
  return options;
}

function usage() {
  return [
    "Usage:",
    "  archive: node scripts/release-notes.cjs archive --notes NOTES --manifest MANIFEST --output OUTPUT",
    "  verify:  node scripts/release-notes.cjs verify --archive ARCHIVE --notes NOTES --manifest MANIFEST",
  ].join("\n");
}

function main() {
  const [command, ...args] = process.argv.slice(2);
  const options = parseArgs(args);
  if (command === "archive") {
    const archive = archiveReleaseNotes(options);
    process.stdout.write(`${JSON.stringify({ archived: true, ...archive, markdown: undefined }, null, 2)}\n`);
  } else if (command === "verify") {
    process.stdout.write(`${JSON.stringify(verifyReleaseNotesArchive(options), null, 2)}\n`);
  } else {
    throw notesError("RELEASE_NOTES_COMMAND_INVALID", `未知命令：${command ?? "(空)"}\n${usage()}`);
  }
}

if (require.main === module) {
  try {
    main();
  } catch (error) {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  }
}

module.exports = {
  REQUIRED_SECTIONS,
  RELEASE_NOTES_SCHEMA_VERSION,
  archiveReleaseNotes,
  validateReleaseNotesMarkdown,
  verifyReleaseNotesArchive,
};
