"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { verifyReleaseMetadata } = require("./generate-release-metadata.cjs");

const ROLLBACK_SCHEMA_VERSION = 1;

function rollbackError(code, message) {
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

async function createRollbackPlan({
  currentPackageRoot,
  currentMetadataRoot,
  previousPackageRoot,
  previousMetadataRoot,
  outputPath,
  reason = "post-release rollback",
}) {
  if (!currentPackageRoot || !currentMetadataRoot || !previousPackageRoot || !previousMetadataRoot || !outputPath) {
    throw rollbackError("ROLLBACK_PATH_REQUIRED", "当前/上一版包、元数据目录和输出路径都是必填项");
  }
  const currentVerification = await verifyReleaseMetadata({
    packageRoot: currentPackageRoot,
    metadataRoot: currentMetadataRoot,
  });
  const previousVerification = await verifyReleaseMetadata({
    packageRoot: previousPackageRoot,
    metadataRoot: previousMetadataRoot,
  });
  const currentManifest = readJson(path.join(path.resolve(currentMetadataRoot), "release-manifest.json"));
  const previousManifest = readJson(path.join(path.resolve(previousMetadataRoot), "release-manifest.json"));
  if (currentVerification.qualification !== "release-candidate" || previousVerification.qualification !== "release-candidate") {
    throw rollbackError("ROLLBACK_QUALIFICATION_INVALID", "回滚配对双方必须是 clean release-candidate");
  }
  if (currentManifest.source.commit === previousManifest.source.commit) {
    throw rollbackError("ROLLBACK_SAME_SOURCE", "当前包与上一版包不能对应同一个源提交");
  }
  if (currentManifest.build.applicationVersion === previousManifest.build.applicationVersion) {
    throw rollbackError("ROLLBACK_SAME_VERSION", "当前包与上一版包版本必须不同");
  }
  const plan = {
    schemaVersion: ROLLBACK_SCHEMA_VERSION,
    kind: "nexus-portable-rollback-plan",
    createdAt: new Date().toISOString(),
    reason: String(reason).slice(0, 512),
    current: {
      packageRoot: path.resolve(currentPackageRoot),
      metadataRoot: path.resolve(currentMetadataRoot),
      sourceCommit: currentManifest.source.commit,
      applicationVersion: currentManifest.build.applicationVersion,
      manifestSha256: sha256Text(fs.readFileSync(path.join(path.resolve(currentMetadataRoot), "release-manifest.json"), "utf8")),
      verifiedFiles: currentVerification.files,
    },
    previous: {
      packageRoot: path.resolve(previousPackageRoot),
      metadataRoot: path.resolve(previousMetadataRoot),
      sourceCommit: previousManifest.source.commit,
      applicationVersion: previousManifest.build.applicationVersion,
      manifestSha256: sha256Text(fs.readFileSync(path.join(path.resolve(previousMetadataRoot), "release-manifest.json"), "utf8")),
      verifiedFiles: previousVerification.files,
    },
    procedure: [
      "确认当前 Nexus 进程已退出；不强制结束无关进程。",
      "如存在文件锁，记录占用进程并要求用户关闭后重试。",
      "保留当前包和当前元数据目录作为失败证据。",
      "将 previous.packageRoot 复制到独立正式目录，不覆盖 previous 原目录。",
      "复制 previous.metadataRoot 到对应正式元数据目录。",
      "运行 smoke:portable --folder <formal-folder-name>。",
      "失败时删除本次 formal 目录并恢复替换前备份，再重复 smoke。",
    ],
  };
  fs.mkdirSync(path.dirname(path.resolve(outputPath)), { recursive: true });
  fs.writeFileSync(outputPath, `${JSON.stringify(plan, null, 2)}\n`, "utf8");
  return plan;
}

async function verifyRollbackPlan({ planPath, currentPackageRoot, currentMetadataRoot, previousPackageRoot, previousMetadataRoot }) {
  if (!planPath) throw rollbackError("ROLLBACK_PATH_REQUIRED", "planPath 是必填项");
  const plan = readJson(planPath);
  if (plan.schemaVersion !== ROLLBACK_SCHEMA_VERSION) {
    throw rollbackError("ROLLBACK_VERSION_UNSUPPORTED", `不支持回滚计划版本 ${plan.schemaVersion}`);
  }
  const current = currentPackageRoot
    ? { packageRoot: path.resolve(currentPackageRoot), metadataRoot: path.resolve(currentMetadataRoot) }
    : plan.current;
  const previous = previousPackageRoot
    ? { packageRoot: path.resolve(previousPackageRoot), metadataRoot: path.resolve(previousMetadataRoot) }
    : plan.previous;
  const currentVerification = await verifyReleaseMetadata(current);
  const previousVerification = await verifyReleaseMetadata(previous);
  const currentManifest = readJson(path.join(current.metadataRoot, "release-manifest.json"));
  const previousManifest = readJson(path.join(previous.metadataRoot, "release-manifest.json"));
  if (currentManifest.source.commit !== plan.current.sourceCommit || previousManifest.source.commit !== plan.previous.sourceCommit) {
    throw rollbackError("ROLLBACK_PLAN_SOURCE_MISMATCH", "回滚计划源提交与实际 manifest 不一致");
  }
  if (currentManifest.build.applicationVersion !== plan.current.applicationVersion
    || previousManifest.build.applicationVersion !== plan.previous.applicationVersion) {
    throw rollbackError("ROLLBACK_PLAN_VERSION_MISMATCH", "回滚计划版本与实际 manifest 不一致");
  }
  return {
    verified: true,
    current: currentVerification,
    previous: previousVerification,
  };
}

function parseArgs(argv) {
  const options = {};
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--reason") {
      options.reason = argv[++index];
      continue;
    }
    const mapping = {
      "--current-package": "currentPackageRoot",
      "--current-metadata": "currentMetadataRoot",
      "--previous-package": "previousPackageRoot",
      "--previous-metadata": "previousMetadataRoot",
      "--output": "outputPath",
      "--plan": "planPath",
    };
    if (!mapping[arg]) throw rollbackError("ROLLBACK_ARG_INVALID", `未知参数：${arg}`);
    options[mapping[arg]] = path.resolve(argv[++index]);
  }
  return options;
}

async function main() {
  const [command, ...args] = process.argv.slice(2);
  const options = parseArgs(args);
  if (command === "create") {
    const plan = await createRollbackPlan(options);
    process.stdout.write(`${JSON.stringify({ created: true, planPath: options.outputPath, current: plan.current, previous: plan.previous }, null, 2)}\n`);
  } else if (command === "verify") {
    process.stdout.write(`${JSON.stringify(await verifyRollbackPlan(options), null, 2)}\n`);
  } else {
    throw rollbackError("ROLLBACK_COMMAND_INVALID", "命令必须是 create 或 verify");
  }
}

if (require.main === module) {
  main().catch(error => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}

module.exports = {
  ROLLBACK_SCHEMA_VERSION,
  createRollbackPlan,
  verifyRollbackPlan,
};
