"use strict";

const crypto = require("node:crypto");
const { execFile } = require("node:child_process");
const fs = require("node:fs");
const net = require("node:net");
const path = require("node:path");
const { promisify } = require("node:util");

const { COMMANDS, RustCoreClient } = require("../electron/rust-core-client.cjs");

const execFileAsync = promisify(execFile);
const REPO_ROOT = path.resolve(__dirname, "..");

const DEFAULTS = Object.freeze({
  durationMs: 60 * 60 * 1000,
  intervalMs: 100,
  sampleIntervalMs: 5_000,
  progressIntervalMs: 30_000,
  maxRssGrowthBytes: 128 * 1024 * 1024,
  maxHandleGrowth: 100,
  maxTransportLatencyMs: 2_000,
  minSuccessRatio: 0.75,
});

function parseDuration(raw, fallback = DEFAULTS.durationMs) {
  if (raw === undefined || raw === null || raw === "") return fallback;
  if (typeof raw !== "string") throw new TypeError("duration must be a string");
  const match = /^(\d+(?:\.\d+)?)(ms|s|m|h)?$/i.exec(raw.trim());
  if (!match) throw new TypeError(`invalid duration: ${raw}`);
  const value = Number(match[1]);
  const unit = match[2]?.toLowerCase() ?? "ms";
  const multiplier = { ms: 1, s: 1000, m: 60_000, h: 3_600_000 }[unit];
  const result = Math.round(value * multiplier);
  if (!Number.isFinite(result) || result <= 0) {
    throw new TypeError(`invalid duration: ${raw}`);
  }
  return result;
}

function parseInteger(raw, fallback) {
  if (raw === undefined || raw === null || raw === "") return fallback;
  const value = Number(raw);
  if (!Number.isInteger(value) || value <= 0) {
    throw new TypeError(`invalid positive integer: ${raw}`);
  }
  return value;
}

function parseArgs(argv) {
  const options = { ...DEFAULTS };
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index];
    const raw = argv[index + 1];
    switch (name) {
      case "--duration":
        options.durationMs = parseDuration(raw);
        break;
      case "--interval-ms":
        options.intervalMs = parseDuration(raw, DEFAULTS.intervalMs);
        if (options.intervalMs < 20) throw new TypeError("--interval-ms must be at least 20ms");
        break;
      case "--sample-interval-ms":
        options.sampleIntervalMs = parseDuration(raw, DEFAULTS.sampleIntervalMs);
        break;
      case "--progress-interval-ms":
        options.progressIntervalMs = parseDuration(raw, DEFAULTS.progressIntervalMs);
        break;
      case "--max-rss-growth-mb":
        options.maxRssGrowthBytes = parseInteger(
          raw,
          DEFAULTS.maxRssGrowthBytes / (1024 * 1024),
        ) * 1024 * 1024;
        break;
      case "--max-handle-growth":
        options.maxHandleGrowth = parseInteger(raw, DEFAULTS.maxHandleGrowth);
        break;
      case "--max-transport-latency-ms":
        options.maxTransportLatencyMs = parseInteger(raw, DEFAULTS.maxTransportLatencyMs);
        break;
      case "--min-success-ratio": {
        const value = Number(raw);
        if (!Number.isFinite(value) || value < 0 || value > 1) {
          throw new TypeError("--min-success-ratio must be between 0 and 1");
        }
        options.minSuccessRatio = value;
        break;
      }
      case "--output":
        options.output = raw;
        break;
      default:
        throw new TypeError(`unknown option or missing value: ${name}`);
    }
  }
  options.sampleIntervalMs = Math.min(options.sampleIntervalMs, options.durationMs);
  options.progressIntervalMs = Math.min(options.progressIntervalMs, options.durationMs);
  return options;
}

function percentile(values, percentileValue) {
  if (values.length === 0) return null;
  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(
    sorted.length - 1,
    Math.max(0, Math.ceil((percentileValue / 100) * sorted.length) - 1),
  );
  return sorted[index];
}

function formatDuration(ms) {
  if (ms < 1000) return `${ms}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(seconds < 10 ? 1 : 0)}s`;
  const minutes = Math.floor(seconds / 60);
  const remainder = Math.round(seconds % 60);
  if (minutes < 60) return `${minutes}m${remainder.toString().padStart(2, "0")}s`;
  const hours = Math.floor(minutes / 60);
  return `${hours}h${(minutes % 60).toString().padStart(2, "0")}m`;
}

function defaultOutputPath(startedAt) {
  const stamp = startedAt.toISOString().replace(/[:.]/g, "-");
  return path.join(REPO_ROOT, "evidence", "r0", `soak-${stamp}.json`);
}

function getFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

async function sampleProcess(processId, timestampMs) {
  const args = [
    "-NoProfile",
    "-NonInteractive",
    "-Command",
    `$p = Get-Process -Id ${processId} -ErrorAction Stop; [pscustomobject]@{
      ProcessId = $p.Id
      ProcessName = $p.ProcessName
      CpuSeconds = [math]::Round($p.TotalProcessorTime.TotalSeconds, 3)
      WorkingSetBytes = $p.WorkingSet64
      PrivateBytes = $p.PrivateMemorySize64
      HandleCount = $p.HandleCount
      ThreadCount = $p.Threads.Count
    } | ConvertTo-Json -Compress`,
  ];
  const { stdout } = await execFileAsync("powershell.exe", args, { windowsHide: true });
  const sample = JSON.parse(stdout);
  for (const key of [
    "ProcessId",
    "CpuSeconds",
    "WorkingSetBytes",
    "PrivateBytes",
    "HandleCount",
    "ThreadCount",
  ]) {
    if (typeof sample[key] !== "number" || !Number.isFinite(sample[key])) {
      throw new TypeError(`PowerShell process sample field ${key} is not numeric: ${stdout}`);
    }
  }
  return { timestampMs, ...sample };
}

async function sourceMetadata(binaryPath) {
  const packageJson = JSON.parse(fs.readFileSync(path.join(REPO_ROOT, "package.json"), "utf8"));
  const binaryHash = crypto.createHash("sha256").update(fs.readFileSync(binaryPath)).digest("hex");
  let commit = null;
  let workingTreeDirty = null;
  try {
    const git = await execFileAsync("git", ["rev-parse", "HEAD"], {
      cwd: REPO_ROOT,
      windowsHide: true,
    });
    commit = git.stdout.trim();
    const status = await execFileAsync("git", ["status", "--porcelain"], {
      cwd: REPO_ROOT,
      windowsHide: true,
    });
    workingTreeDirty = status.stdout.trim().length > 0;
  } catch {
    // Git metadata is useful, but a source-archive run must still be able to produce evidence.
  }
  return {
    packageName: packageJson.name,
    packageVersion: packageJson.version,
    sourceCommit: commit,
    workingTreeDirty,
    binaryPath,
    binarySha256: binaryHash,
  };
}

function makeResultObject({
  options,
  source,
  startedAt,
  endedAt,
  port,
  stats,
  samples,
  cleanup,
  failure,
  exitCode,
}) {
  const actualDurationMs = endedAt - startedAt;
  const expectedMinimum = Math.max(
    1,
    Math.floor((options.durationMs / options.intervalMs) * options.minSuccessRatio),
  );
  const first = samples.at(0);
  const last = samples.at(-1);
  const maxTransportLatency = stats.transportLatencies.length
    ? Math.max(...stats.transportLatencies)
    : null;
  const checks = {
    noFailure: !failure,
    successCount: stats.successes >= expectedMinimum,
    noDataErrors: stats.dataErrors === 0,
    noStreamErrors: stats.streamErrors === 0,
    noSilence: !stats.silenceError,
    transportLatency: maxTransportLatency === null
      || maxTransportLatency <= options.maxTransportLatencyMs,
    rssGrowth: Boolean(first && last)
      && last.WorkingSetBytes - first.WorkingSetBytes <= options.maxRssGrowthBytes,
    handleGrowth: Boolean(first && last)
      && last.HandleCount - first.HandleCount <= options.maxHandleGrowth,
    cleanShutdown: cleanup.shutdown === true && exitCode === 0,
  };
  const passed = Object.values(checks).every(Boolean);
  const successRatio = actualDurationMs > 0
    ? Number((stats.successes / (actualDurationMs / options.intervalMs)).toFixed(6))
    : 0;
  return {
    schemaVersion: 1,
    kind: "R0-soak-A",
    passed,
    actualDurationMs,
    startedAt: startedAt.toISOString(),
    endedAt: endedAt.toISOString(),
    source,
    workload: {
      protocol: "Modbus TCP",
      peer: "built-in Rust virtual slave",
      endpoint: `127.0.0.1:${port}`,
      connectionId: "soak-connection",
      slaveId: "soak-slave",
      streamId: "soak-stream",
      fc: 3,
      startAddress: 100,
      quantity: 2,
      expectedRegisters: [0x1234, 0xabcd],
      intervalMs: options.intervalMs,
      faultInjection: "none",
    },
    configuration: options,
    counters: {
      ...stats,
      expectedMinimumSuccesses: expectedMinimum,
      successRatio,
    },
    latency: {
      transport: {
        samples: stats.transportLatencies.length,
        min: stats.transportLatencies.length ? Math.min(...stats.transportLatencies) : null,
        p50: percentile(stats.transportLatencies, 50),
        p95: percentile(stats.transportLatencies, 95),
        p99: percentile(stats.transportLatencies, 99),
        max: maxTransportLatency,
      },
      interArrivalJitterMs: {
        samples: stats.interArrivalJitters.length,
        p50: percentile(stats.interArrivalJitters, 50),
        p95: percentile(stats.interArrivalJitters, 95),
        p99: percentile(stats.interArrivalJitters, 99),
        max: stats.interArrivalJitters.length ? Math.max(...stats.interArrivalJitters) : null,
      },
    },
    resource: {
      sampleCount: samples.length,
      first,
      last,
      rssGrowthBytes: first && last ? last.WorkingSetBytes - first.WorkingSetBytes : null,
      handleGrowth: first && last ? last.HandleCount - first.HandleCount : null,
      cpuSecondsDelta: first && last ? Number((last.CpuSeconds - first.CpuSeconds).toFixed(3)) : null,
      samples,
    },
    cleanup,
    checks,
    failure: failure ? { message: failure.message, code: failure.code ?? null } : null,
    exitCode,
  };
}

async function runSoak(options) {
  const binaryPath = process.env.NEXUS_RUST_CORE_PATH
    ?? path.join(REPO_ROOT, "rust-core", "target", "debug", "nexus-rust-core.exe");
  if (!fs.existsSync(binaryPath)) {
    throw new Error(`Rust core binary not found: ${binaryPath}`);
  }

  const startedAt = new Date();
  const source = await sourceMetadata(binaryPath);
  const port = await getFreePort();
  const stats = {
    successes: 0,
    dataErrors: 0,
    streamErrors: 0,
    silenceError: false,
    transportLatencies: [],
    interArrivalJitters: [],
  };
  const samples = [];
  const cleanup = {
    streamStopped: false,
    connectionClosed: false,
    slaveStopped: false,
    shutdown: false,
  };
  let failure = null;
  let lastDataAt = 0;
  let previousDataAt = 0;
  let exitCode = null;
  const core = new RustCoreClient({
    binaryPath,
    requestTimeoutMs: Math.max(options.durationMs + 5_000, 10_000),
    shutdownGraceMs: 3_000,
    logger: Object.freeze({
      debug() {},
      info() {},
      warn() {},
      error() {},
    }),
  });

  const fail = (error) => {
    if (!failure) failure = error;
  };
  let samplingTimer = null;
  let progressTimer = null;
  let silenceTimer = null;

  try {
    await core.start();
    await core.request(COMMANDS.START_TCP_SLAVE, {
      slaveId: "soak-slave",
      port,
      allowedStationIds: [],
    });
    await core.request(COMMANDS.SLAVE_SET_VALUE, {
      slaveId: "soak-slave",
      area: "holding",
      address: 100,
      values: [0x1234, 0xabcd],
    });
    await core.request(COMMANDS.OPEN_TCP_CONNECTION, {
      connectionId: "soak-connection",
      host: "127.0.0.1",
      port,
      unitId: 1,
      framing: "standard",
    });

    await core.startPollStream({
      streamId: "soak-stream",
      connectionId: "soak-connection",
      fc: 3,
      startAddress: 100,
      quantity: 2,
      intervalMs: options.intervalMs,
    }, (data) => {
      const receivedAt = Date.now();
      lastDataAt = receivedAt;
      if (previousDataAt > 0) {
        stats.interArrivalJitters.push(Math.abs(receivedAt - previousDataAt - options.intervalMs));
      }
      previousDataAt = receivedAt;
      const latency = typeof data?.timestamp === "number" ? receivedAt - data.timestamp : null;
      if (latency !== null && Number.isFinite(latency) && latency >= 0) {
        stats.transportLatencies.push(latency);
      }
      const registers = data?.registers;
      if (
        Array.isArray(registers)
        && registers.length === 2
        && registers[0] === 0x1234
        && registers[1] === 0xabcd
      ) {
        stats.successes += 1;
      } else {
        stats.dataErrors += 1;
      }
    }, (error) => {
      stats.streamErrors += 1;
      fail(new Error(`poll stream error: ${error?.code ?? "unknown"} ${error?.message ?? ""}`.trim()));
    });

    samples.push(await sampleProcess(core.child.pid, Date.now()));
    const maxSilenceMs = Math.max(options.intervalMs * 3, 1_000);
    silenceTimer = setInterval(() => {
      const silence = Date.now() - (lastDataAt || startedAt.getTime());
      if (silence > maxSilenceMs) {
        stats.silenceError = true;
        fail(new Error(`no poll data for ${silence}ms`));
      }
    }, maxSilenceMs);
    silenceTimer.unref?.();

    samplingTimer = setInterval(async () => {
      try {
        if (core.child?.pid && !failure) samples.push(await sampleProcess(core.child.pid, Date.now()));
      } catch (error) {
        fail(error);
      }
    }, options.sampleIntervalMs);
    samplingTimer.unref?.();

    process.stdout.write(
      `soak started: ${formatDuration(options.durationMs)}, interval ${options.intervalMs}ms, pid ${core.child.pid}\n`,
    );
    progressTimer = setInterval(() => {
      const elapsedMs = Date.now() - startedAt.getTime();
      const lastSample = samples.at(-1);
      process.stdout.write(
        `soak progress ${formatDuration(Math.min(elapsedMs, options.durationMs))}/${formatDuration(options.durationMs)}`
        + ` successes=${stats.successes} errors=${stats.dataErrors + stats.streamErrors}`
        + ` rss=${lastSample ? `${Math.round(lastSample.WorkingSetBytes / 1024)}KiB` : "n/a"}`
        + ` handles=${lastSample?.HandleCount ?? "n/a"}\n`,
      );
    }, options.progressIntervalMs);
    progressTimer.unref?.();

    await new Promise((resolve) => {
      const deadline = startedAt.getTime() + options.durationMs;
      const wait = setInterval(() => {
        if (failure || Date.now() >= deadline) {
          clearInterval(wait);
          resolve();
        }
      }, Math.min(50, options.intervalMs));
      wait.unref?.();
    });
  } catch (error) {
    fail(error);
  } finally {
    if (samplingTimer) clearInterval(samplingTimer);
    if (progressTimer) clearInterval(progressTimer);
    if (silenceTimer) clearTimeout(silenceTimer);

    try {
      if (core.child?.pid) samples.push(await sampleProcess(core.child.pid, Date.now()));
    } catch {
      // Exit-code and shutdown checks report a child that disappeared before final sampling.
    }

    try {
      if (core.state === "ready") {
        await core.stopPollStream({ streamId: "soak-stream" });
        cleanup.streamStopped = true;
      }
    } catch (error) {
      fail(error);
    }
    try {
      if (core.state === "ready") {
        await core.request(COMMANDS.CLOSE_CONNECTION, { connectionId: "soak-connection" });
        cleanup.connectionClosed = true;
      }
    } catch (error) {
      fail(error);
    }
    try {
      if (core.state === "ready") {
        await core.request(COMMANDS.STOP_SLAVE, { slaveId: "soak-slave" });
        cleanup.slaveStopped = true;
      }
    } catch (error) {
      fail(error);
    }
    try {
      if (core.state === "ready" || core.state === "stopping") {
        await core.shutdown();
        cleanup.shutdown = true;
      }
    } catch (error) {
      fail(error);
    }
    if (core.child) exitCode = core.child.exitCode;
  }

  const endedAt = new Date();
  return makeResultObject({
    options,
    source,
    startedAt,
    endedAt,
    port,
    stats,
    samples,
    cleanup,
    failure,
    exitCode,
  });
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const result = await runSoak(options);
  const output = options.output ?? defaultOutputPath(new Date(result.startedAt));
  if (output && output !== "-") {
    const outputPath = path.resolve(output);
    result.evidenceFile = outputPath;
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    fs.writeFileSync(outputPath, `${JSON.stringify(result, null, 2)}\n`);
  }
  process.stdout.write(`${JSON.stringify({
    passed: result.passed,
    evidenceFile: result.evidenceFile ?? null,
    actualDurationMs: result.actualDurationMs,
    counters: result.counters,
    latency: result.latency,
    resource: {
      sampleCount: result.resource.sampleCount,
      first: result.resource.first,
      last: result.resource.last,
      rssGrowthBytes: result.resource.rssGrowthBytes,
      handleGrowth: result.resource.handleGrowth,
      cpuSecondsDelta: result.resource.cpuSecondsDelta,
    },
    cleanup: result.cleanup,
    checks: result.checks,
    failure: result.failure,
    exitCode: result.exitCode,
  }, null, 2)}\n`);
  if (!result.passed) process.exitCode = 1;
}

if (require.main === module) {
  main().catch((error) => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}

module.exports = {
  DEFAULTS,
  formatDuration,
  parseArgs,
  parseDuration,
  percentile,
  runSoak,
};
