"use strict";

const fs = require("node:fs");
const path = require("node:path");
const { createHash } = require("node:crypto");
const { execFile } = require("node:child_process");

const MAX_GX3_BYTES = 512 * 1024 * 1024;
const DEFAULT_TIMEOUT_MS = 180_000;
const DEFAULT_MAX_BUFFER_BYTES = 8 * 1024 * 1024;
const DEVICE_RE = /^[A-Za-z][A-Za-z0-9._:-]{0,63}$/;

class Gx3AnalysisError extends Error {
  constructor(code, message, details = {}) {
    super(message);
    this.name = "Gx3AnalysisError";
    this.code = code;
    Object.assign(this, details);
  }
}

function gx3Error(code, message, details) {
  return new Gx3AnalysisError(code, message, details);
}

function normalizeText(value) {
  return String(value ?? "").replace(/^\uFEFF/, "").trim();
}

function parseJsonOutput(text, label) {
  try {
    return JSON.parse(normalizeText(text));
  } catch (error) {
    throw gx3Error("GX3_OUTPUT_INVALID", `${label} 返回了无法解析的 JSON：${error.message}`);
  }
}

function validateDevice(device) {
  const normalized = String(device ?? "").trim().toUpperCase();
  if (!DEVICE_RE.test(normalized)) {
    throw gx3Error("GX3_DEVICE_INVALID", "软元件格式无效，例如 M100、D200、X0、Y10、SM5628。");
  }
  return normalized;
}

function defaultCliCandidates(env = process.env) {
  const candidates = [];
  if (env.NEXUS_GX3_CLI_PATH) candidates.push(env.NEXUS_GX3_CLI_PATH);
  if (env.CONDA_PREFIX) candidates.push(path.join(env.CONDA_PREFIX, "Scripts", "gx3-cli.exe"));
  if (env.USERPROFILE) {
    candidates.push(path.join(env.USERPROFILE, "anaconda3", "Scripts", "gx3-cli.exe"));
    candidates.push(path.join(env.USERPROFILE, "miniconda3", "Scripts", "gx3-cli.exe"));
  }
  if (env.LOCALAPPDATA) {
    candidates.push(path.join(env.LOCALAPPDATA, "anaconda3", "Scripts", "gx3-cli.exe"));
    candidates.push(path.join(env.LOCALAPPDATA, "miniconda3", "Scripts", "gx3-cli.exe"));
  }
  candidates.push("D:\\Anaconda3\\Scripts\\gx3-cli.exe");
  candidates.push("C:\\ProgramData\\Anaconda3\\Scripts\\gx3-cli.exe");
  candidates.push("gx3-cli");
  return [...new Set(candidates.filter(Boolean))];
}

async function hashFile(filePath, fsImpl = fs) {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    const stream = fsImpl.createReadStream(filePath);
    stream.on("error", reject);
    stream.on("data", (chunk) => hash.update(chunk));
    stream.on("end", () => resolve(hash.digest("hex")));
  });
}

class Gx3AnalysisService {
  constructor({
    workRoot,
    cliPath,
    execFileImpl = execFile,
    fsImpl = fs,
    env = process.env,
    timeoutMs = DEFAULT_TIMEOUT_MS,
    maxBufferBytes = DEFAULT_MAX_BUFFER_BYTES,
  } = {}) {
    if (typeof workRoot !== "string" || workRoot.trim() === "") {
      throw new TypeError("workRoot is required");
    }
    this.workRoot = path.resolve(workRoot);
    this.cliPath = cliPath || null;
    this.execFileImpl = execFileImpl;
    this.fs = fsImpl;
    this.env = env;
    this.timeoutMs = timeoutMs;
    this.maxBufferBytes = maxBufferBytes;
    this.analyses = new Map();
  }

  _resolveCliPath() {
    if (this.cliPath) return this.cliPath;
    for (const candidate of defaultCliCandidates(this.env)) {
      if (candidate === "gx3-cli" || this.fs.existsSync(candidate)) {
        this.cliPath = candidate;
        return candidate;
      }
    }
    this.cliPath = "gx3-cli";
    return this.cliPath;
  }

  _validateSource(sourcePath) {
    if (typeof sourcePath !== "string" || sourcePath.trim() === "") {
      throw gx3Error("GX3_PATH_REQUIRED", "请选择一个 .gx3 项目文件。");
    }
    const resolved = path.resolve(sourcePath);
    if (path.extname(resolved).toLowerCase() !== ".gx3") {
      throw gx3Error("GX3_EXTENSION_INVALID", "只能选择扩展名为 .gx3 的 GX Works3 项目。");
    }
    let stat;
    try {
      stat = this.fs.statSync(resolved);
    } catch (error) {
      throw gx3Error("GX3_SOURCE_NOT_FOUND", `项目文件不存在或不可读取：${error.message}`, { path: resolved });
    }
    if (!stat.isFile()) throw gx3Error("GX3_SOURCE_NOT_FILE", "所选路径不是普通文件。", { path: resolved });
    if (stat.size <= 0) throw gx3Error("GX3_SOURCE_EMPTY", "所选 GX3 项目为空。", { path: resolved });
    if (stat.size > MAX_GX3_BYTES) {
      throw gx3Error("GX3_SOURCE_TOO_LARGE", "GX3 项目超过 512 MiB 安全上限。", { path: resolved, bytes: stat.size });
    }
    return { path: resolved, bytes: stat.size };
  }

  _runCli(args, { cwd = this.workRoot, timeoutMs = this.timeoutMs } = {}) {
    const executable = this._resolveCliPath();
    const startedAt = Date.now();
    return new Promise((resolve, reject) => {
      this.execFileImpl(executable, args, {
        cwd,
        windowsHide: true,
        shell: false,
        timeout: timeoutMs,
        maxBuffer: this.maxBufferBytes,
        encoding: "utf8",
        env: this.env,
      }, (error, stdout, stderr) => {
        const result = {
          command: args[0] ?? "",
          durationMs: Date.now() - startedAt,
          stdout: normalizeText(stdout),
          stderr: normalizeText(stderr),
        };
        if (!error) return resolve(result);
        const code = error.code === "ENOENT" ? "GX3_CLI_NOT_FOUND"
          : error.killed ? "GX3_COMMAND_TIMEOUT"
            : "GX3_COMMAND_FAILED";
        const detail = [result.stdout, result.stderr].filter(Boolean).join("\n");
        return reject(gx3Error(
          code,
          code === "GX3_CLI_NOT_FOUND"
            ? "未找到 gx3-cli。请先安装 gx3-cli-mcp，或设置 NEXUS_GX3_CLI_PATH。"
            : `GX3 命令 ${result.command} 执行失败${detail ? `：${detail}` : `：${error.message}`}`,
          { executable, args: [...args], exitCode: error.code, detail },
        ));
      });
    });
  }

  async checkAvailability() {
    try {
      this.fs.mkdirSync(this.workRoot, { recursive: true });
      const result = await this._runCli(["--version"], { timeoutMs: 15_000 });
      return {
        available: true,
        cliPath: this._resolveCliPath(),
        version: result.stdout || result.stderr || "unknown",
      };
    } catch (error) {
      return {
        available: false,
        cliPath: this._resolveCliPath(),
        error: { code: error.code ?? "GX3_STATUS_FAILED", message: error.message },
      };
    }
  }

  async _prepareWorkingCopy(source) {
    const sourceSha256 = await hashFile(source.path, this.fs);
    const analysisId = sourceSha256;
    // GX3 extraction adds another 64-character hash directory. Keep the parent
    // deliberately short so Windows installations without long-path support work.
    const analysisRoot = path.join(this.workRoot, analysisId.slice(0, 16));
    const reportRoot = path.join(analysisRoot, "reports");
    const workingCopyPath = path.join(analysisRoot, "p.gx3");
    this.fs.mkdirSync(analysisRoot, { recursive: true });
    this.fs.mkdirSync(reportRoot, { recursive: true });

    let reuseCopy = false;
    if (this.fs.existsSync(workingCopyPath)) {
      try {
        reuseCopy = (await hashFile(workingCopyPath, this.fs)) === sourceSha256;
      } catch {
        reuseCopy = false;
      }
    }
    if (!reuseCopy) {
      const temporaryPath = path.join(analysisRoot, `.p.copy-${process.pid}-${Date.now()}`);
      try {
        this.fs.copyFileSync(source.path, temporaryPath);
        const copiedHash = await hashFile(temporaryPath, this.fs);
        if (copiedHash !== sourceSha256) {
          throw gx3Error("GX3_COPY_VERIFY_FAILED", "GX3 工作副本校验失败，已停止解析。");
        }
        this.fs.copyFileSync(temporaryPath, workingCopyPath);
      } finally {
        try {
          if (this.fs.existsSync(temporaryPath)) this.fs.unlinkSync(temporaryPath);
        } catch {
          // A leftover private-cache temp file is safe; preserve the original failure.
        }
      }
    }

    return { sourceSha256, analysisId, analysisRoot, reportRoot, workingCopyPath };
  }

  async analyzeProject(sourcePath) {
    const source = this._validateSource(sourcePath);
    const availability = await this.checkAvailability();
    if (!availability.available) {
      throw gx3Error(availability.error.code, availability.error.message, { cliPath: availability.cliPath });
    }
    const prepared = await this._prepareWorkingCopy(source);
    const indexDir = path.join(prepared.analysisRoot, ".gx3_index");
    const linkDb = path.join(indexDir, "link_map.sqlite");
    this.fs.mkdirSync(indexDir, { recursive: true });

    const steps = [];
    const runStep = async (name, args) => {
      const result = await this._runCli(args, { cwd: prepared.analysisRoot });
      steps.push({ name, ...result });
      return result;
    };

    await runStep("预检查", [
      "doctor", "--root", prepared.workingCopyPath,
      "--index-dir", indexDir, "--link-db", linkDb,
    ]);
    await runStep("轻量索引", ["index-lite", "build", "--root", prepared.workingCopyPath]);
    await runStep("交叉引用", ["xref", "build", "--root", prepared.workingCopyPath]);
    const doctorAfter = await runStep("完成检查", [
      "doctor", "--root", prepared.workingCopyPath,
      "--index-dir", indexDir, "--link-db", linkDb,
    ]);
    const programMapResult = await runStep("程序映射", [
      "program-map", "--root", prepared.workingCopyPath, "--json",
    ]);
    const reliabilityPath = path.join(prepared.reportRoot, "reliability.json");
    await runStep("解析可靠性", [
      "reliability-report", "--root", prepared.workingCopyPath,
      "--output", reliabilityPath, "--json",
    ]);

    const sourceSha256After = await hashFile(source.path, this.fs);
    if (sourceSha256After !== prepared.sourceSha256) {
      throw gx3Error(
        "GX3_SOURCE_CHANGED_DURING_ANALYSIS",
        "分析期间原始 GX3 文件发生了变化，请保存完成后重新解析。",
        { before: prepared.sourceSha256, after: sourceSha256After },
      );
    }

    const programMap = parseJsonOutput(programMapResult.stdout, "program-map");
    let reliability = null;
    try {
      reliability = parseJsonOutput(this.fs.readFileSync(reliabilityPath, "utf8"), "reliability-report");
    } catch (error) {
      if (error.code === "GX3_OUTPUT_INVALID") throw error;
      throw gx3Error("GX3_REPORT_READ_FAILED", `读取解析可靠性报告失败：${error.message}`);
    }

    const analysis = {
      analysisId: prepared.analysisId,
      sourcePath: source.path,
      sourceBytes: source.bytes,
      sourceSha256: prepared.sourceSha256,
      sourceUnchanged: true,
      workingCopyPath: prepared.workingCopyPath,
      analysisRoot: prepared.analysisRoot,
      indexDir,
      cliPath: availability.cliPath,
      cliVersion: availability.version,
      programMap,
      reliability,
      doctor: doctorAfter.stdout || doctorAfter.stderr,
      steps,
    };
    this.analyses.set(prepared.analysisId, analysis);
    return analysis;
  }

  async queryDevice({ analysisId, device } = {}) {
    const analysis = this.analyses.get(String(analysisId ?? ""));
    if (!analysis) {
      throw gx3Error("GX3_ANALYSIS_REQUIRED", "请先完成一次 GX3 项目解析。");
    }
    const normalizedDevice = validateDevice(device);
    const indexResult = await this._runCli([
      "query-device", normalizedDevice, "--root", analysis.workingCopyPath,
    ], { cwd: analysis.analysisRoot });
    const xrefResult = await this._runCli([
      "xref", "where-used", normalizedDevice, "--root", analysis.workingCopyPath,
    ], { cwd: analysis.analysisRoot });
    return {
      ok: true,
      analysisId: analysis.analysisId,
      device: normalizedDevice,
      index: indexResult.stdout || indexResult.stderr,
      xref: xrefResult.stdout || xrefResult.stderr,
    };
  }
}

module.exports = {
  DEFAULT_MAX_BUFFER_BYTES,
  DEFAULT_TIMEOUT_MS,
  DEVICE_RE,
  Gx3AnalysisError,
  Gx3AnalysisService,
  MAX_GX3_BYTES,
  defaultCliCandidates,
  hashFile,
  validateDevice,
};
