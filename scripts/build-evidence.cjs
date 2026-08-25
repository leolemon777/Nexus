"use strict";

const crypto = require("node:crypto");
const { execFile } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const { promisify } = require("node:util");
const { redactLogText } = require("../electron/log-redaction-service.cjs");

const execFileAsync = promisify(execFile);
const BUILD_EVIDENCE_SCHEMA_VERSION = 1;
const MAX_COMMANDS = 100;
const MAX_LOG_BYTES = 20 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 30 * 60 * 1000;
const SAFE_COMMAND_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/;
const SENSITIVE_KEY_RE = /(password|passwd|secret|token|authorization|cookie|credential|api[_-]?key)/i;

function evidenceError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function sha256Text(text) {
  return crypto.createHash("sha256").update(text).digest("hex");
}

function redactSensitiveText(text) {
  return redactLogText(text);
}

function normalizePlan(raw, { projectRoot }) {
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw evidenceError("BUILD_PLAN_INVALID", "构建证据计划必须是 JSON 对象");
  }
  if (raw.schemaVersion !== BUILD_EVIDENCE_SCHEMA_VERSION) {
    throw evidenceError("BUILD_PLAN_VERSION_UNSUPPORTED", `不支持构建证据计划版本 ${raw.schemaVersion}`);
  }
  const commands = Array.isArray(raw.commands) ? raw.commands : [];
  if (commands.length < 1 || commands.length > MAX_COMMANDS) {
    throw evidenceError("BUILD_PLAN_COMMAND_COUNT", `构建命令数量必须是 1-${MAX_COMMANDS}`);
  }
  const ids = new Set();
  return commands.map((entry, index) => {
    if (!entry || typeof entry !== "object" || Array.isArray(entry)) {
      throw evidenceError("BUILD_PLAN_COMMAND_INVALID", `命令 #${index + 1} 格式无效`);
    }
    const id = String(entry.id ?? "");
    if (!SAFE_COMMAND_ID.test(id) || ids.has(id)) {
      throw evidenceError("BUILD_PLAN_COMMAND_ID_INVALID", `命令 ID 无效或重复：${id || "(空)"}`);
    }
    ids.add(id);
    const timeoutMs = Number(entry.timeoutMs ?? DEFAULT_TIMEOUT_MS);
    if (!Number.isInteger(timeoutMs) || timeoutMs < 1000 || timeoutMs > 3 * 60 * 60 * 1000) {
      throw evidenceError("BUILD_PLAN_TIMEOUT_INVALID", `命令 ${id} 超时必须在 1 秒到 3 小时之间`);
    }
    const normalized = {
      id,
      title: String(entry.title ?? id).slice(0, 200),
      timeoutMs,
      program: null,
      args: [],
    };
    if (entry.kind === "npm") {
      const script = String(entry.script ?? "");
      if (!/^[A-Za-z0-9:_-]+$/.test(script)) throw evidenceError("BUILD_PLAN_COMMAND_INVALID", `npm 命令 ${id} script 无效`);
      // execFile deliberately avoids a shell. npm.cmd cannot be executed directly
      // by Node on Windows, so use PowerShell with a validated script name.
      normalized.program = "powershell.exe";
      normalized.args = ["-NoProfile", "-NonInteractive", "-Command", `npm.cmd run ${script}`];
    } else if (entry.kind === "powershell") {
      const script = String(entry.script ?? "");
      const scriptParts = script.replace(/\\/g, "/").split("/");
      if (!script || scriptParts.includes("..") || scriptParts.includes(".") || /^[A-Za-z]:/.test(script) || script.startsWith("/") || !script.endsWith(".ps1")) {
        throw evidenceError("BUILD_PLAN_COMMAND_INVALID", `PowerShell 命令 ${id} 只能执行仓库内相对 .ps1 脚本`);
      }
      const scriptPath = path.resolve(projectRoot, script);
      if (!scriptPath.startsWith(`${projectRoot}${path.sep}`) || !fs.existsSync(scriptPath)) {
        throw evidenceError("BUILD_PLAN_COMMAND_INVALID", `PowerShell 命令 ${id} 脚本越界或不存在`);
      }
      normalized.program = "powershell.exe";
      normalized.args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath];
    } else if (entry.kind === "node") {
      const script = String(entry.script ?? "");
      const scriptPath = path.resolve(projectRoot, script);
      if (!script || !scriptPath.startsWith(`${projectRoot}${path.sep}`) || !fs.existsSync(scriptPath)) {
        throw evidenceError("BUILD_PLAN_COMMAND_INVALID", `Node 命令 ${id} 脚本越界或不存在`);
      }
      normalized.program = process.execPath;
      normalized.args = [scriptPath, ...Array.isArray(entry.args) ? entry.args.map(String) : []];
    } else {
      throw evidenceError("BUILD_PLAN_COMMAND_KIND_INVALID", `命令 ${id} kind 必须是 npm/powershell/node`);
    }
    return normalized;
  });
}

async function sourceMetadata(sourceRoot) {
  let commit = null;
  let dirty = null;
  try {
    const rev = await execFileAsync("git", ["rev-parse", "HEAD"], { cwd: sourceRoot, windowsHide: true });
    commit = rev.stdout.trim();
    const status = await execFileAsync("git", ["status", "--porcelain"], { cwd: sourceRoot, windowsHide: true });
    dirty = status.stdout.trim().length > 0;
  } catch {
    dirty = null;
  }
  return { commit, dirty };
}

function loadManifest(manifestPath) {
  if (!manifestPath) return null;
  const raw = fs.readFileSync(manifestPath, "utf8");
  const manifest = JSON.parse(raw);
  if (manifest.schemaVersion !== 1 || !Array.isArray(manifest.artifacts)) {
    throw evidenceError("BUILD_EVIDENCE_MANIFEST_INVALID", "release manifest 格式无效");
  }
  return {
    path: path.resolve(manifestPath),
    sha256: sha256Text(raw),
    sourceCommit: manifest.source?.commit ?? null,
    sourceDirty: manifest.source?.dirty ?? null,
    applicationVersion: manifest.build?.applicationVersion ?? null,
    qualification: manifest.qualification ?? null,
  };
}

function renderCommand(execution) {
  return [
    `## ${execution.title}`,
    "",
    "- Command:",
    "  ```text",
    `${execution.program} ${execution.args.join(" ")}`,
    "  ```",
    `- Exit: ${execution.exitCode}`,
    `- Duration: ${execution.durationMs} ms`,
    `- Log: ${execution.log.path}`,
    `- Log SHA-256: ${execution.log.sha256}`,
    "",
  ].join("\n");
}

function renderMarkdown(summary) {
  return [
    `# Nexus Build Evidence`,
    "",
    `- Overall: ${summary.overallOk ? "PASS" : "FAIL"}`,
    `- Generated at: ${summary.generatedAt}`,
    `- Qualification: ${summary.qualification}`,
    `- Source commit: ${summary.source.commit ?? "unknown"}`,
    `- Source dirty: ${String(summary.source.dirty)}`,
    `- Release manifest: ${summary.releaseManifest ? path.basename(summary.releaseManifest.path) : "not supplied"}`,
    "",
    ...summary.commands.map(renderCommand),
  ].join("\n");
}

async function runBuildEvidence({
  planPath,
  outputDirectory,
  manifestPath = null,
  sourceRoot = path.resolve(__dirname, ".."),
  allowDirty = false,
  execImpl = execFileAsync,
  now = () => new Date(),
} = {}) {
  if (!planPath || !outputDirectory) throw evidenceError("BUILD_EVIDENCE_PATH_REQUIRED", "planPath 和 outputDirectory 都是必填项");
  const projectRoot = path.resolve(sourceRoot);
  const resolvedOutput = path.resolve(outputDirectory);
  const rawPlan = JSON.parse(fs.readFileSync(path.resolve(planPath), "utf8"));
  const commands = normalizePlan(rawPlan, { projectRoot });
  const source = await sourceMetadata(projectRoot);
  if (source.dirty !== false && !allowDirty) {
    throw evidenceError("BUILD_EVIDENCE_SOURCE_DIRTY", "源码工作区不是 clean；candidate 证据需显式 allowDirty");
  }
  const releaseManifest = loadManifest(manifestPath);
  fs.mkdirSync(resolvedOutput, { recursive: true });

  const executions = [];
  for (const command of commands) {
    const startedAt = now().toISOString();
    const started = Date.now();
    let stdout = "";
    let stderr = "";
    let exitCode = 0;
    try {
      const result = await execImpl(command.program, command.args, {
        cwd: projectRoot,
        windowsHide: true,
        timeout: command.timeoutMs,
        maxBuffer: MAX_LOG_BYTES,
      });
      stdout = result.stdout ?? "";
      stderr = result.stderr ?? "";
    } catch (error) {
      stdout = error.stdout ?? "";
      stderr = error.stderr ?? "";
      exitCode = Number.isInteger(error.code) ? error.code : 1;
    }
    const ended = Date.now();
    const redacted = redactSensitiveText([
      `$ ${command.program} ${command.args.join(" ")}`,
      "[stdout]",
      stdout,
      "",
      "[stderr]",
      stderr,
      "",
      `exitCode=${exitCode}`,
      `durationMs=${ended - started}`,
      "",
    ].join("\n"));
    if (Buffer.byteLength(redacted, "utf8") > MAX_LOG_BYTES) {
      throw evidenceError("BUILD_EVIDENCE_LOG_TOO_LARGE", `命令 ${command.id} 脱敏后日志超过 20 MiB`);
    }
    const logName = `${command.id}.log`;
    const logPath = path.join(resolvedOutput, logName);
    fs.writeFileSync(logPath, Buffer.from(redacted, "utf8"));
    executions.push({
      id: command.id,
      title: command.title,
      program: command.program,
      args: command.args,
      exitCode,
      ok: exitCode === 0,
      startedAt,
      endedAt: now().toISOString(),
      durationMs: ended - started,
      log: {
        path: logName,
        bytes: Buffer.byteLength(redacted, "utf8"),
        sha256: sha256Text(redacted),
      },
    });
  }

  const summary = {
    schemaVersion: BUILD_EVIDENCE_SCHEMA_VERSION,
    kind: "nexus-build-evidence",
    generatedAt: now().toISOString(),
    qualification: source.commit && source.dirty === false ? "candidate-clean-source" : "candidate-dirty-source",
    source,
    releaseManifest,
    overallOk: executions.every(entry => entry.ok),
    counts: {
      commands: executions.length,
      passed: executions.filter(entry => entry.ok).length,
      failed: executions.filter(entry => !entry.ok).length,
    },
    commands: executions,
  };
  const summaryJson = `${JSON.stringify(summary, null, 2)}\n`;
  fs.writeFileSync(path.join(resolvedOutput, "build-evidence.json"), summaryJson, "utf8");
  fs.writeFileSync(path.join(resolvedOutput, "BUILD-EVIDENCE.md"), `${renderMarkdown(summary)}\n`, "utf8");
  return summary;
}

function verifyBuildEvidence({ outputDirectory }) {
  if (!outputDirectory) throw evidenceError("BUILD_EVIDENCE_PATH_REQUIRED", "outputDirectory 是必填项");
  const root = path.resolve(outputDirectory);
  const summary = JSON.parse(fs.readFileSync(path.join(root, "build-evidence.json"), "utf8"));
  if (summary.schemaVersion !== BUILD_EVIDENCE_SCHEMA_VERSION) {
    throw evidenceError("BUILD_EVIDENCE_VERSION_UNSUPPORTED", `不支持构建证据版本 ${summary.schemaVersion}`);
  }
  if (!Array.isArray(summary.commands) || summary.commands.length === 0) {
    throw evidenceError("BUILD_EVIDENCE_COMMANDS_INVALID", "构建证据没有命令记录");
  }
  let passed = 0;
  for (const execution of summary.commands) {
    const logPath = path.join(root, execution.log.path);
    const raw = fs.readFileSync(logPath, "utf8");
    if (Buffer.byteLength(raw, "utf8") !== execution.log.bytes) {
      throw evidenceError("BUILD_EVIDENCE_SIZE_MISMATCH", `构建日志大小不匹配：${execution.id}`);
    }
    if (sha256Text(raw) !== execution.log.sha256) {
      throw evidenceError("BUILD_EVIDENCE_HASH_MISMATCH", `构建日志哈希不匹配：${execution.id}`);
    }
    if (!raw.includes(`exitCode=${execution.exitCode}`) || !raw.includes(`durationMs=${execution.durationMs}`)) {
      throw evidenceError("BUILD_EVIDENCE_LOG_INVALID", `构建日志缺少退出码或耗时：${execution.id}`);
    }
    if (execution.ok !== (execution.exitCode === 0)) {
      throw evidenceError("BUILD_EVIDENCE_STATUS_MISMATCH", `构建命令状态不一致：${execution.id}`);
    }
    if (execution.ok) passed += 1;
  }
  if (summary.counts.commands !== summary.commands.length || summary.counts.passed !== passed) {
    throw evidenceError("BUILD_EVIDENCE_COUNT_MISMATCH", "构建证据统计数量不一致");
  }
  if (summary.overallOk !== (passed === summary.commands.length)) {
    throw evidenceError("BUILD_EVIDENCE_OVERALL_MISMATCH", "构建证据总状态不一致");
  }
  if (summary.releaseManifest) {
    const raw = fs.readFileSync(summary.releaseManifest.path, "utf8");
    if (sha256Text(raw) !== summary.releaseManifest.sha256) {
      throw evidenceError("BUILD_EVIDENCE_MANIFEST_MISMATCH", "release manifest 哈希与构建证据不一致");
    }
  }
  if (!fs.existsSync(path.join(root, "BUILD-EVIDENCE.md"))) {
    throw evidenceError("BUILD_EVIDENCE_MARKDOWN_MISSING", "缺少 BUILD-EVIDENCE.md");
  }
  return {
    verified: true,
    overallOk: summary.overallOk,
    counts: summary.counts,
    qualification: summary.qualification,
    source: summary.source,
  };
}

function parseArgs(argv) {
  const options = { planPath: null, outputDirectory: null, manifestPath: null, allowDirty: false };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--plan") options.planPath = path.resolve(argv[++index]);
    else if (arg === "--output") options.outputDirectory = path.resolve(argv[++index]);
    else if (arg === "--manifest") options.manifestPath = path.resolve(argv[++index]);
    else if (arg === "--allow-dirty") options.allowDirty = true;
    else throw evidenceError("BUILD_EVIDENCE_ARG_INVALID", `未知参数：${arg}`);
  }
  return options;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const summary = await runBuildEvidence(options);
  const verification = verifyBuildEvidence(options);
  process.stdout.write(`${JSON.stringify(verification, null, 2)}\n`);
  if (!summary.overallOk) process.exitCode = 1;
}

if (require.main === module) {
  main().catch(error => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}

module.exports = {
  BUILD_EVIDENCE_SCHEMA_VERSION,
  normalizePlan,
  redactSensitiveText,
  runBuildEvidence,
  verifyBuildEvidence,
};
