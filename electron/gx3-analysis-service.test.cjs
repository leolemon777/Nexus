"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  Gx3AnalysisService,
  validateDevice,
} = require("./gx3-analysis-service.cjs");

function createFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-gx3-"));
  const sourcePath = path.join(root, "machine.gx3");
  const workRoot = path.join(root, "private-work");
  const sourceBytes = Buffer.from("synthetic gx3 fixture for service boundary tests\n", "utf8");
  fs.writeFileSync(sourcePath, sourceBytes);
  return { root, sourcePath, sourceBytes, workRoot };
}

function createFakeCli(calls) {
  return (_executable, args, options, callback) => {
    calls.push({ args: [...args], options: { ...options } });
    const command = args[0];
    if (command === "--version") return callback(null, "gx3-cli 0.1.0\n", "");
    if (command === "program-map") {
      return callback(null, JSON.stringify({
        program_files: ["MAIN"],
        pous: [{ name: "MainPou", program_file: "MAIN" }],
        warnings: [],
      }), "");
    }
    if (command === "reliability-report") {
      const outputIndex = args.indexOf("--output");
      fs.writeFileSync(args[outputIndex + 1], JSON.stringify({
        summary: { gap_rate: 0 },
        unsupported_instructions: [],
      }), "utf8");
      return callback(null, "reliability report written\n", "");
    }
    if (command === "query-device") return callback(null, "M100 启动允许\n", "");
    if (command === "xref") {
      const output = args[1] === "where-used" ? "writer MAIN step 10\n" : "xref rows=1\n";
      return callback(null, output, "");
    }
    return callback(null, `${command} ok\n`, "");
  };
}

test("GX3 analysis copies the source into private work storage and runs only fixed read-only commands", async () => {
  const fixture = createFixture();
  const calls = [];
  try {
    const service = new Gx3AnalysisService({
      workRoot: fixture.workRoot,
      cliPath: "C:\\tools\\gx3-cli.exe",
      execFileImpl: createFakeCli(calls),
    });
    const result = await service.analyzeProject(fixture.sourcePath);

    assert.equal(result.sourcePath, fixture.sourcePath);
    assert.equal(result.sourceUnchanged, true);
    assert.equal(fs.readFileSync(fixture.sourcePath).equals(fixture.sourceBytes), true);
    assert.equal(fs.readFileSync(result.workingCopyPath).equals(fixture.sourceBytes), true);
    assert.equal(result.workingCopyPath.startsWith(path.resolve(fixture.workRoot) + path.sep), true);
    assert.deepEqual(result.programMap.program_files, ["MAIN"]);
    assert.equal(result.reliability.summary.gap_rate, 0);
    assert.deepEqual(result.steps.map((step) => step.name), [
      "预检查", "轻量索引", "交叉引用", "完成检查", "程序映射", "解析可靠性",
    ]);

    const commands = calls.map((call) => call.args[0]);
    assert.deepEqual(commands, [
      "--version", "doctor", "index-lite", "xref", "doctor", "program-map", "reliability-report",
    ]);
    for (const call of calls) {
      assert.equal(call.options.shell, false);
      assert.equal(call.options.windowsHide, true);
      assert.equal(typeof call.options.timeout, "number");
    }
    const projectArguments = calls.flatMap((call) => call.args);
    assert.equal(projectArguments.includes(fixture.sourcePath), false, "CLI must receive the private copy, not the source path");
    assert.equal(projectArguments.includes(result.workingCopyPath), true);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("device query uses the completed analysis and rejects command injection text", async () => {
  const fixture = createFixture();
  const calls = [];
  try {
    const service = new Gx3AnalysisService({
      workRoot: fixture.workRoot,
      cliPath: "C:\\tools\\gx3-cli.exe",
      execFileImpl: createFakeCli(calls),
    });
    await assert.rejects(
      () => service.queryDevice({ analysisId: "missing", device: "M100" }),
      (error) => error.code === "GX3_ANALYSIS_REQUIRED",
    );
    const analysis = await service.analyzeProject(fixture.sourcePath);
    const query = await service.queryDevice({ analysisId: analysis.analysisId, device: "sm5628" });
    assert.equal(query.device, "SM5628");
    assert.match(query.index, /启动允许/);
    assert.match(query.xref, /writer/);
    await assert.rejects(
      () => service.queryDevice({ analysisId: analysis.analysisId, device: "M100; whoami" }),
      (error) => error.code === "GX3_DEVICE_INVALID",
    );
    assert.equal(calls.some((call) => call.args.includes("M100; whoami")), false);
  } finally {
    fs.rmSync(fixture.root, { recursive: true, force: true });
  }
});

test("source validation rejects non-GX3 files before starting the CLI", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-gx3-invalid-"));
  const sourcePath = path.join(root, "project.txt");
  const calls = [];
  try {
    fs.writeFileSync(sourcePath, "not gx3", "utf8");
    const service = new Gx3AnalysisService({
      workRoot: path.join(root, "work"),
      cliPath: "C:\\tools\\gx3-cli.exe",
      execFileImpl: createFakeCli(calls),
    });
    await assert.rejects(
      () => service.analyzeProject(sourcePath),
      (error) => error.code === "GX3_EXTENSION_INVALID",
    );
    assert.deepEqual(calls, []);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("availability reports a missing CLI without throwing", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-gx3-status-"));
  try {
    const service = new Gx3AnalysisService({
      workRoot: root,
      cliPath: "C:\\missing\\gx3-cli.exe",
      execFileImpl: (_exe, _args, _options, callback) => {
        const error = Object.assign(new Error("not found"), { code: "ENOENT" });
        callback(error, "", "");
      },
    });
    const status = await service.checkAvailability();
    assert.equal(status.available, false);
    assert.equal(status.error.code, "GX3_CLI_NOT_FOUND");
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("device normalization accepts common Mitsubishi formats", () => {
  assert.equal(validateDevice("m100"), "M100");
  assert.equal(validateDevice("SM5628"), "SM5628");
  assert.equal(validateDevice("D200.3"), "D200.3");
  assert.throws(() => validateDevice(""), (error) => error.code === "GX3_DEVICE_INVALID");
});

test("GX3 UI is wired through the isolated Electron command allow-list", () => {
  const projectRoot = path.join(__dirname, "..");
  const html = fs.readFileSync(path.join(projectRoot, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(projectRoot, "electron", "main.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(projectRoot, "electron", "preload.cjs"), "utf8");
  const renderer = fs.readFileSync(path.join(projectRoot, "src", "main.js"), "utf8");

  assert.match(html, /data-view="gx3"/);
  assert.match(html, /id="gx3-view"/);
  assert.match(html, /id="gx3-human-report"/);
  assert.match(html, /id="gx3-device-summary"/);
  assert.match(html, /查看技术原始结果/);
  assert.match(html, /不会连接 PLC，也不会写回 \.gx3/);
  for (const command of ["gx3_status", "gx3_select_project", "gx3_analyze_project", "gx3_query_device"]) {
    assert.match(preload, new RegExp(`"${command}"`));
    assert.match(main, new RegExp(`nexus:${command}`));
    assert.match(renderer, new RegExp(`"${command}"`));
  }
  assert.match(main, /app\.getPath\("temp"\), "NexusGX3"/);
  assert.match(renderer, /buildGx3Presentation/);
  assert.match(renderer, /parseGx3DevicePresentation/);
  assert.doesNotMatch(renderer, /child_process|execFile|spawn\(/);
});
