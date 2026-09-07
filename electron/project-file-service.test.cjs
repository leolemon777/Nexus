"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { pathToFileURL } = require("node:url");
const {
  LEGACY_PROJECT_SCHEMA_VERSION,
  SANITIZED_PROJECT_NAME,
  SANITIZED_TCP_HOST,
  ProjectFileService,
  migrateProjectDocument,
  normalizeProjectDocument,
} = require("./project-file-service.cjs");

function validProject(overrides = {}) {
  return {
    format: "nexus-project",
    schemaVersion: 2,
    projectName: "产线调试",
    activeView: "master",
    activeSession: "default",
    config: { transport: "tcp", tcp: { host: "192.168.1.20", port: "502" } },
    sessions: [{
      name: "default",
      pointTable: [{ name: "温度", unitId: 1, fc: 3, address: 0, quantity: 2, dataType: "Float32", scale: "0.1", unit: "℃" }],
      trendSelection: ["reg-HR-0", "reg-IR-4", "bad-key", "reg-HR-0"],
    }],
    workspace: {
      commandList: [{ fc: 3, unitId: 2, address: 100, quantity: 4, value: "" }],
      simulators: {
        modbus: { mode: "tcp", port: "1502", allowedStations: "1,3-5" },
        melsec: { port: "5100" },
        s7: { port: "1102" },
      },
      lastHelpReference: { source: "siemens", variant: "ppi-serial" },
    },
    ...overrides,
  };
}

test("normalizes a versioned project and preserves safe workspace data", () => {
  const project = normalizeProjectDocument(validProject());
  assert.equal(project.format, "nexus-project");
  assert.equal(project.schemaVersion, 2);
  assert.equal(project.config.tcp.host, "192.168.1.20");
  assert.equal(project.sessions[0].pointTable[0].unit, "℃");
  assert.deepEqual(project.sessions[0].trendSelection, ["reg-HR-0", "reg-IR-4"]);
  assert.deepEqual(project.workspace.commandList, [{ fc: 3, unitId: 2, address: 100, quantity: 4, value: "" }]);
  assert.deepEqual(project.workspace.simulators, {
    modbus: { mode: "tcp", port: "1502", allowedStations: "1,3-5" },
    melsec: { port: "5100" },
    s7: { port: "1102" },
  });
  assert.deepEqual(project.workspace.lastHelpReference, { source: "siemens", variant: "ppi-serial" });
});

test("preserves the GX3 analysis view in a saved Nexus project", () => {
  const project = normalizeProjectDocument(validProject({ activeView: "gx3" }));
  assert.equal(project.activeView, "gx3");
});

test("rejects unsupported versions, duplicate sessions, and unsafe point ranges", () => {
  assert.throws(() => normalizeProjectDocument(validProject({ schemaVersion: 99 })), (e) => e.code === "PROJECT_VERSION_UNSUPPORTED");
  assert.throws(() => normalizeProjectDocument(validProject({ sessions: [{ name: "A" }, { name: "A" }] })), (e) => e.code === "PROJECT_DUPLICATE_SESSION");
  assert.throws(() => normalizeProjectDocument(validProject({ sessions: [{ name: "A", pointTable: [{ fc: 3, address: 65_535, quantity: 2 }] }] })), (e) => e.code === "PROJECT_INVALID_POINT");
});

test("save and load round-trip through an injected filesystem", () => {
  const files = new Map();
  const fsImpl = {
    writeFileSync: (filePath, data) => files.set(filePath, data),
    readFileSync: (filePath) => files.get(filePath),
    statSync: (filePath) => ({ size: Buffer.byteLength(files.get(filePath), "utf8") }),
    renameSync: (from, to) => files.set(to, files.get(from)) && files.delete(from),
    unlinkSync: (filePath) => files.delete(filePath),
  };
  const service = new ProjectFileService({ fsImpl });
  const saved = service.save("C:\\tmp\\line.nexus.json", validProject());
  assert.ok(saved.bytes > 0);
  const loaded = service.load(saved.path);
  assert.equal(loaded.document.projectName, "产线调试");
  assert.equal(loaded.document.sessions[0].pointTable.length, 1);
  assert.deepEqual(loaded.document.sessions[0].trendSelection, ["reg-HR-0", "reg-IR-4"]);
  assert.equal(loaded.document.workspace.commandList.length, 1);
  assert.equal(loaded.document.workspace.simulators.modbus.port, "1502");
  assert.equal(loaded.document.workspace.lastHelpReference.source, "siemens");
});

test("save replaces the target only after a verified same-directory temporary write", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-project-atomic-"));
  const target = path.join(directory, "line.nexus.json");
  try {
    fs.writeFileSync(target, "original project\n", "utf8");
    const service = new ProjectFileService({ randomId: () => "fixed-id" });
    const saved = service.save(target, validProject());
    assert.equal(saved.path, target);
    assert.equal(JSON.parse(fs.readFileSync(target, "utf8")).projectName, "产线调试");
    const temporary = path.join(directory, ".line.nexus.json.tmp-fixed-id");
    assert.equal(fs.existsSync(temporary), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("a failed rename keeps the original file and removes the temporary file", () => {
  const files = new Map([["D:\\work\\line.nexus.json", "original project"]]);
  const removed = [];
  const fsImpl = {
    writeFileSync: (filePath, data) => files.set(filePath, data),
    readFileSync: (filePath) => files.get(filePath),
    renameSync: () => {
      throw new Error("target is locked");
    },
    unlinkSync: (filePath) => {
      removed.push(filePath);
      files.delete(filePath);
    },
  };
  const service = new ProjectFileService({ fsImpl, randomId: () => "failed-id" });
  assert.throws(() => service.save("D:\\work\\line.nexus.json", validProject()), /target is locked/);
  assert.equal(files.get("D:\\work\\line.nexus.json"), "original project");
  assert.deepEqual(removed, ["D:\\work\\.line.nexus.json.tmp-failed-id"]);
});

test("a failed temporary write keeps the original file and still attempts cleanup", () => {
  const files = new Map([["D:\\work\\line.nexus.json", "original project"]]);
  const removed = [];
  const fsImpl = {
    writeFileSync: (filePath) => {
      files.set(filePath, "partial write");
      throw new Error("disk full");
    },
    readFileSync: (filePath) => files.get(filePath),
    renameSync: (from, to) => files.set(to, files.get(from)) && files.delete(from),
    unlinkSync: (filePath) => {
      removed.push(filePath);
      files.delete(filePath);
    },
  };
  const service = new ProjectFileService({ fsImpl, randomId: () => "write-id" });
  assert.throws(() => service.save("D:\\work\\line.nexus.json", validProject()), /disk full/);
  assert.equal(files.get("D:\\work\\line.nexus.json"), "original project");
  assert.deepEqual(removed, ["D:\\work\\.line.nexus.json.tmp-write-id"]);
});

test("invalid JSON reports a path and line or column without changing the source file", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-project-invalid-"));
  const target = path.join(directory, "broken.nexus.json");
  const invalid = "{\n  \"format\": nexus-project\n";
  try {
    fs.writeFileSync(target, invalid, "utf8");
    const service = new ProjectFileService();
    assert.throws(() => service.load(target), (error) => {
      assert.equal(error.code, "PROJECT_JSON_INVALID");
      assert.equal(error.path, target);
      assert.equal(error.line, 2);
      assert.equal(error.column, 13);
      assert.equal(typeof error.characterOffset, "number");
      assert.match(error.message, /第 2 行第 13 列，字节 \d+/);
      return true;
    });
    assert.equal(fs.readFileSync(target, "utf8"), invalid);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("sanitizes project identity and free-text fields while preserving reproducible protocol structure", () => {
  const source = validProject({
    projectName: "Customer Secret Line",
    activeSession: "Secret Session",
    config: {
      transport: "tcp",
      serial: { portName: "COM-CUSTOMER", baudRate: "9600", parity: "even", dataBits: "8", stopBits: "1" },
      tcp: { host: "192.168.77.88", port: "1502" },
      command: {
        unitId: "9", functionCode: "3", startAddress: "100", quantity: "4",
        displayType: "SecretDisplayType", pollInterval: "1000",
      },
    },
    sessions: [{
      name: "Secret Session",
      pointTable: [{
        name: "Customer Temperature", unitId: 9, fc: 3, address: 100, quantity: 4,
        dataType: "SecretDataType", scale: "0.25", unit: "customer-unit",
      }],
      trendSelection: ["reg-HR-100"],
    }],
    workspace: {
      commandList: [{ fc: 6, unitId: 9, address: 200, quantity: 1, value: "customer-write-value" }],
      simulators: { modbus: { mode: "tcp", port: "1502", allowedStations: "9" } },
      lastHelpReference: { source: "siemens", variant: "customer-variant" },
    },
  });
  const service = new ProjectFileService();
  const sanitized = service.sanitizeForExport(source);
  const output = JSON.stringify(sanitized);

  assert.equal(sanitized.projectName, SANITIZED_PROJECT_NAME);
  assert.equal(sanitized.config.tcp.host, SANITIZED_TCP_HOST);
  assert.equal(sanitized.config.serial.portName, "");
  assert.equal(sanitized.sessions[0].name, "脱敏会话1");
  assert.equal(sanitized.sessions[0].pointTable[0].name, "脱敏点位1-1");
  assert.equal(sanitized.sessions[0].pointTable[0].dataType, "Unsigned16");
  assert.equal(sanitized.sessions[0].pointTable[0].scale, "0.25");
  assert.equal(sanitized.sessions[0].pointTable[0].unit, "");
  assert.equal(sanitized.workspace.commandList[0].value, "");
  assert.equal(sanitized.workspace.lastHelpReference, null);

  assert.equal(sanitized.config.transport, "tcp");
  assert.equal(sanitized.config.tcp.port, "1502");
  assert.equal(sanitized.sessions[0].pointTable[0].fc, 3);
  assert.equal(sanitized.sessions[0].pointTable[0].address, 100);
  assert.equal(sanitized.sessions[0].pointTable[0].quantity, 4);
  assert.deepEqual(sanitized.sessions[0].trendSelection, ["reg-HR-100"]);
  assert.equal(sanitized.workspace.simulators.modbus.allowedStations, "9");

  for (const forbidden of [
    "Customer Secret Line",
    "Secret Session",
    "COM-CUSTOMER",
    "192.168.77.88",
    "SecretDisplayType",
    "Customer Temperature",
    "SecretDataType",
    "customer-unit",
    "customer-write-value",
    "customer-variant",
  ]) {
    assert.ok(!output.includes(forbidden), `sanitized project leaked ${forbidden}`);
  }
});

test("exports a loadable sanitized project through the atomic save path", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-project-sanitized-"));
  const target = path.join(directory, "support.nexus.json");
  try {
    const service = new ProjectFileService({ randomId: () => "sanitized-id" });
    const source = validProject({
      projectName: "Customer Secret Line",
      config: { transport: "tcp", tcp: { host: "10.20.30.40", port: "502" } },
      sessions: [{ name: "Customer Session", pointTable: [], trendSelection: [] }],
    });
    const exported = service.exportSanitized(target, source);
    const loaded = service.load(target);
    assert.equal(exported.sanitized, undefined);
    assert.equal(loaded.document.projectName, SANITIZED_PROJECT_NAME);
    assert.equal(loaded.document.config.tcp.host, SANITIZED_TCP_HOST);
    assert.equal(loaded.document.sessions[0].name, "脱敏会话1");
    assert.equal(fs.readFileSync(target, "utf8").includes("10.20.30.40"), false);
    assert.equal(fs.existsSync(path.join(directory, ".support.nexus.json.tmp-sanitized-id")), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("sanitized project export stays wired without replacing the active project", () => {
  const main = fs.readFileSync(path.join(__dirname, "main.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(__dirname, "preload.cjs"), "utf8");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  const html = fs.readFileSync(path.join(__dirname, "..", "index.html"), "utf8");
  const functionMatch = /async function exportSanitizedProject\(\) \{[\s\S]*?\n\}/.exec(renderer);
  const handlerMatch = /ipcMain\.handle\("nexus:project_export_sanitized"[\s\S]*?\n  }\);/.exec(main);

  assert.ok(functionMatch, "exportSanitizedProject function is missing");
  assert.match(functionMatch[0], /project_export_sanitized/);
  assert.match(functionMatch[0], /当前工作区未切换/);
  assert.doesNotMatch(functionMatch[0], /setProjectIdentity\(/);
  assert.match(main, /nexus:project_export_sanitized/);
  assert.match(main, /projectFileService\.exportSanitized\(/);
  assert.match(main, /defaultSanitizedFilename\(\)/);
  assert.ok(handlerMatch, "project_export_sanitized IPC handler is missing");
  assert.doesNotMatch(handlerMatch[0], /recordSaved\(/);
  assert.doesNotMatch(handlerMatch[0], /currentProjectPath\s*=/);
  assert.match(preload, /project_export_sanitized/);
  assert.match(renderer, /elements\.projectExportSanitized/);
  assert.match(html, /id="project-export-sanitized"/);
});

test("legacy v0 project migrates to the v1 golden sample without modifying its source", () => {
  const fixtureRoot = path.join(__dirname, "fixtures");
  const sourcePath = path.join(fixtureRoot, "project-v0.nexus.json");
  const expectedPath = path.join(fixtureRoot, "project-v1.expected.json");
  const sourceBefore = fs.readFileSync(sourcePath, "utf8");
  const expected = JSON.parse(fs.readFileSync(expectedPath, "utf8"));
  const service = new ProjectFileService();

  const migrated = migrateProjectDocument(JSON.parse(sourceBefore));
  assert.equal(migrated.sourceSchemaVersion, LEGACY_PROJECT_SCHEMA_VERSION);
  assert.equal(migrated.targetSchemaVersion, 2);
  assert.deepEqual(migrated.steps, [{
    fromVersion: 0,
    toVersion: 1,
    summary: "补充 schemaVersion、workspace 默认结构和 v1 字段校验",
  }, {
    fromVersion: 1,
    toVersion: 2,
    summary: "workspace 增加 frameDefinitions 自定义帧定义数组(串口可视化批次 2)",
  }]);
  assert.deepEqual(migrated.document, expected);

  const preview = service.previewMigration(sourcePath);
  assert.equal(preview.previewOnly, true);
  assert.equal(preview.migration.sourceSchemaVersion, 0);
  assert.equal(preview.migration.targetSchemaVersion, 2);
  assert.equal(preview.migration.appliedInMemoryOnly, true);
  assert.equal(preview.migration.sourceModified, false);
  assert.deepEqual(preview.document, expected);
  assert.equal(fs.readFileSync(sourcePath, "utf8"), sourceBefore);

  const currentPath = path.join(fixtureRoot, "project-v1.expected.json");
  const currentBefore = fs.readFileSync(currentPath, "utf8");
  const currentPreview = service.previewMigration(currentPath);
  // 夹具已是当前 v2: 预览应识别为无迁移
  assert.equal(currentPreview.migration.sourceSchemaVersion, 2);
  assert.equal(currentPreview.migration.targetSchemaVersion, 2);
  assert.deepEqual(currentPreview.migration.steps, []);
  assert.equal(fs.readFileSync(currentPath, "utf8"), currentBefore);
});

test("project persistence stores protocol identifiers rather than display labels", async () => {
  const registry = await import(pathToFileURL(path.join(__dirname, "..", "src", "protocol-registry.js")).href);
  const project = normalizeProjectDocument(validProject({
    activeView: "melsec",
    config: { transport: "tcp", tcp: { host: "127.0.0.1", port: "502" } },
    workspace: {
      commandList: [],
      simulators: { modbus: { mode: "tcp", port: "1502", allowedStations: "1" } },
      lastHelpReference: { source: "melsec", variant: "3e" },
    },
  }));

  assert.equal(project.activeView, "melsec");
  assert.ok(registry.getProtocolDescriptor("master", project.config.transport));
  assert.ok(registry.getProtocolDescriptor(
    project.workspace.lastHelpReference.source,
    project.workspace.lastHelpReference.variant,
  ));
  assert.equal(Object.hasOwn(project, "label"), false);
  assert.equal(Object.hasOwn(project, "displayName"), false);
  assert.equal(Object.hasOwn(project.workspace.lastHelpReference, "label"), false);
  assert.equal(Object.hasOwn(project.workspace.lastHelpReference, "sourceLabel"), false);
});

test("rejects credential and private-key fields instead of silently persisting them", () => {
  for (const [fieldPath, project] of [
    ["$.config.tcp.password", validProject({ config: { transport: "tcp", tcp: { host: "127.0.0.1", port: "502", password: "secret" } } })],
    ["$.workspace.apiToken", validProject({ workspace: { commandList: [], apiToken: "token-value" } })],
    ["$.workspace.keyMaterial.privateKey", validProject({ workspace: { commandList: [], keyMaterial: { privateKey: "private-key-data" } } })],
    ["$.workspace.credentials", validProject({ workspace: { commandList: [], credentials: { username: "user" } } })],
  ]) {
    assert.throws(() => normalizeProjectDocument(project), (error) => {
      assert.equal(error.code, "PROJECT_CREDENTIALS_NOT_PERSISTED");
      assert.equal(error.field, fieldPath);
      return true;
    });
  }

  const service = new ProjectFileService();
  const normalized = normalizeProjectDocument(validProject());
  const output = JSON.stringify(service.sanitizeForExport(normalized));
  for (const forbidden of ["password", "passwd", "secret", "token", "authorization", "cookie", "credential", "privateKey", "apiKey"]) {
    assert.ok(!new RegExp(`"${forbidden}"`, "i").test(output), `project output contains credential key ${forbidden}`);
  }
});

test("treats project files as untrusted input before any renderer state is created", () => {
  const oversizedFs = {
    statSync: () => ({ size: 5 * 1024 * 1024 + 1 }),
    readFileSync: () => {
      throw new Error("oversized project must not be read");
    },
  };
  const boundedService = new ProjectFileService({ fsImpl: oversizedFs });
  assert.throws(() => boundedService.load("D:\\untrusted\\large.nexus.json"), (error) => {
    assert.equal(error.code, "PROJECT_TOO_LARGE");
    assert.equal(error.path, "D:\\untrusted\\large.nexus.json");
    assert.equal(error.bytes, 5 * 1024 * 1024 + 1);
    return true;
  });

  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-project-untrusted-"));
  const target = path.join(directory, "untrusted.nexus.json");
  const project = validProject();
  const raw = JSON.stringify(project).replace(
    /\}\s*$/,
    ',"__proto__":{"polluted":true},"constructor":{"prototype":{"polluted":true},"unexpected":"value"}}',
  );
  try {
    fs.writeFileSync(target, raw, "utf8");
    const parsed = JSON.parse(raw);
    assert.equal(Object.hasOwn(parsed, "__proto__"), true);
    assert.equal(Object.hasOwn(parsed, "constructor"), true);

    const loaded = new ProjectFileService().load(target);
    const output = JSON.stringify(loaded.document);
    assert.equal(Object.hasOwn(loaded.document, "__proto__"), false);
    assert.equal(Object.hasOwn(loaded.document, "constructor"), false);
    assert.equal(Object.hasOwn(loaded.document.workspace, "unexpected"), false);
    assert.equal("polluted" in {}, false);
    assert.equal(output.includes("polluted"), false);
    assert.equal(output.includes("unexpected"), false);
    assert.equal(fs.readFileSync(target, "utf8"), raw);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("explicit project migration writes a separate loadable v1 file and preserves the v0 source", () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-project-migration-"));
  const sourcePath = path.join(directory, "legacy.nexus.json");
  const outputPath = path.join(directory, "current.nexus.json");
  const legacy = JSON.parse(fs.readFileSync(path.join(__dirname, "fixtures", "project-v0.nexus.json"), "utf8"));
  try {
    fs.writeFileSync(sourcePath, `${JSON.stringify(legacy, null, 2)}\n`, "utf8");
    const sourceBefore = fs.readFileSync(sourcePath, "utf8");
    const service = new ProjectFileService({ randomId: () => "migration-id" });
    const migrated = service.migrateFile(sourcePath, outputPath);
    const loaded = service.load(outputPath);

    assert.equal(migrated.document.schemaVersion, 2);
    assert.equal(loaded.migration, undefined);
    assert.equal(loaded.document.schemaVersion, 2);
    assert.equal(loaded.document.projectName, legacy.projectName);
    assert.equal(fs.readFileSync(sourcePath, "utf8"), sourceBefore);
    assert.equal(fs.existsSync(path.join(directory, ".current.nexus.json.tmp-migration-id")), false);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

test("renderer makes legacy project migration visible and keeps the source read-only until explicit save", () => {
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  assert.match(renderer, /function projectMigrationNotice\(result\)/);
  assert.match(renderer, /只读兼容打开，原文件未修改，显式保存后才升级/);
  assert.match(renderer, /projectMigrationNotice\(result\)/);
});

test("migrates v1 projects to v2 with empty frameDefinitions and sanitizes definitions", () => {
  const v1 = validProject({ schemaVersion: 1, workspace: { commandList: [] } });
  const migrated = migrateProjectDocument(v1);
  assert.equal(migrated.sourceSchemaVersion, 1);
  assert.equal(migrated.targetSchemaVersion, 2);
  assert.equal(migrated.document.schemaVersion, 2);
  assert.deepEqual(migrated.document.workspace.frameDefinitions, []);

  const dirty = validProject({
    workspace: {
      commandList: [],
      frameDefinitions: [
        {
          name: "RS485 温度模块",
          mode: "binary",
          head: "01 03",
          length: 9,
          checksum: { type: "crc16-modbus" },
          fields: [{ name: "temp1", offset: 3, fieldType: "i16", byteOrder: "be", scale: 0.1, unit: "℃" }],
        },
        { name: "RS485 温度模块", mode: "binary", fields: [{ name: "dup", offset: 0 }] },
        { name: "", mode: "binary", fields: [{ name: "x", offset: 0 }] },
        { name: "无字段", mode: "binary", fields: [] },
        { name: "坏模式", mode: "websocket", fields: [{ name: "a", offset: 0 }], head: "", length: 999999 },
        {
          name: "ascii",
          mode: "ascii-delimited",
          separator: "",
          lineEnding: "\r",
          fields: [{ name: "w", index: 1, fieldType: "f64", scale: "abc" }],
        },
        {
          name: "动态长度模块",
          mode: "binary",
          head: "AA",
          lengthField: { offset: 1, fieldType: "u16", byteOrder: "le", adjust: -3 },
          tail: "0D 0A",
          fields: [{ name: "value", offset: 4, fieldType: "u16", byteOrder: "be", scale: 0.1 }],
        },
        {
          name: "坏长度字段",
          mode: "binary",
          lengthField: { offset: -5, fieldType: "u32", byteOrder: "weird", adjust: "abc" },
          tail: "ZZ",
          fields: [{ name: "x", offset: 3 }],
        },
      ],
    },
  });
  const normalized = normalizeProjectDocument(dirty);
  const defs = normalized.workspace.frameDefinitions;
  assert.equal(defs.length, 5);
  // 1) 合法 binary 定义原样保留
  assert.equal(defs[0].name, "RS485 温度模块");
  assert.equal(defs[0].checksum.type, "crc16-modbus");
  assert.equal(defs[0].fields[0].scale, 0.1);
  // 2) 重名被剔除
  // 3) 坏模式回退 binary,非法长度/校验被清空,fieldType 回退 u16
  assert.equal(defs[1].mode, "binary");
  assert.equal(defs[1].length, null);
  assert.equal(defs[1].checksum, null);
  assert.equal(defs[1].fields[0].fieldType, "u16");
  // 4) ascii:空分隔符回退逗号,非法行尾回退 \n,坏缩放回退 1
  assert.equal(defs[2].mode, "ascii-delimited");
  assert.equal(defs[2].separator, ",");
  assert.equal(defs[2].lineEnding, "\n");
  assert.equal(defs[2].fields[0].scale, 1);
  // 5) 动态长度 + 尾部定界原样保留
  assert.equal(defs[3].lengthField.offset, 1);
  assert.equal(defs[3].lengthField.fieldType, "u16");
  assert.equal(defs[3].lengthField.byteOrder, "le");
  assert.equal(defs[3].lengthField.adjust, -3);
  assert.equal(defs[3].tail, "0D 0A");
  // 6) 非法长度字段回退默认,坏 tail hex 原样交给 Rust validate 显式报错(不静默清空)
  const bad = defs[4];
  assert.equal(bad.lengthField.offset, 0);
  assert.equal(bad.lengthField.fieldType, "u8");
  assert.equal(bad.lengthField.byteOrder, "be");
  assert.equal(bad.lengthField.adjust, 0);
  assert.equal(bad.tail, "ZZ");
});

test("normalizes script frame definitions (expr/accept/verify) and strips binary keys", () => {
  const dirty = validProject({
    workspace: {
      commandList: [],
      frameDefinitions: [
        {
          name: "BCD 温湿度",
          mode: "script",
          accept: "  frame[0] == 0x55  ",
          verify: "(sum(0, len - 2) & 0xFF) == frame[len - 1]",
          // 脚本模式混入的 binary 字段应被剥掉
          head: "AA",
          length: 9,
          checksum: { type: "sum8" },
          tail: "0D 0A",
          fields: [
            { name: "temp", expr: "bcd(frame[1])", scale: 0.01, unit: "℃", offset: 3, fieldType: "i16" },
            { name: "flag", expr: "   ", scale: 1 }, // 空白表达式归 null(Rust validate 显式报缺)
          ],
        },
        {
          name: "脚本坏条件",
          mode: "script",
          accept: "x".repeat(300), // 超限被裁断(256)
          fields: [{ name: "a", expr: "frame[0] * 2" }],
        },
        {
          name: "binary 不带脚本键",
          mode: "binary",
          fields: [{ name: "a", offset: 0, expr: "frame[0]" }], // 非脚本模式 expr 应被清掉
        },
      ],
    },
  });
  const defs = normalizeProjectDocument(dirty).workspace.frameDefinitions;
  assert.equal(defs.length, 3);

  const script = defs[0];
  assert.equal(script.mode, "script");
  assert.equal(script.accept, "frame[0] == 0x55");
  assert.equal(script.verify, "(sum(0, len - 2) & 0xFF) == frame[len - 1]");
  assert.equal(script.head, "");
  assert.equal(script.length, null);
  assert.equal(script.checksum, null);
  assert.equal(script.tail, "");
  assert.equal(script.fields[0].expr, "bcd(frame[1])");
  assert.equal(script.fields[0].scale, 0.01);
  assert.equal(script.fields[0].offset, null); // 归一化输出以 null 表达"无此键"
  assert.equal(script.fields[1].expr, null);

  assert.equal(defs[1].accept.length, 256);

  const binary = defs[2];
  assert.equal(binary.mode, "binary");
  assert.equal(binary.fields[0].expr, null);
  assert.equal(binary.accept, null);
  assert.equal(binary.verify, null);
});

test("rejects excessive workspace collections and deeply nested project input", () => {
  const trendSelection = Array.from({ length: 33 }, (_, index) => `reg-HR-${index}`);
  assert.throws(() => normalizeProjectDocument(validProject({
    sessions: [{ name: "default", pointTable: [], trendSelection }],
  })), (error) => error.code === "PROJECT_TOO_MANY_TREND_SELECTIONS");

  const commandList = Array.from({ length: 201 }, () => ({ fc: 3, unitId: 1, address: 0, quantity: 1, value: "" }));
  assert.throws(() => normalizeProjectDocument(validProject({ workspace: { commandList } })), (error) => error.code === "PROJECT_TOO_MANY_COMMANDS");

  let deep = { unexpected: true };
  for (let index = 0; index < 17; index += 1) deep = { unexpected: deep };
  assert.throws(() => normalizeProjectDocument(validProject({ unexpected: deep })), (error) => error.code === "PROJECT_TOO_DEEP");
});

test("defaults workspace state for v2 projects and rejects unsafe command lists", () => {
  const legacy = normalizeProjectDocument(validProject({
    sessions: [{ name: "default", pointTable: [] }],
    workspace: { commandList: [] },
  }));
  assert.deepEqual(legacy.workspace.commandList, []);
  assert.deepEqual(legacy.workspace.frameDefinitions, []);
  assert.deepEqual(legacy.workspace.simulators, {
    modbus: { mode: "tcp", port: "502", allowedStations: "" },
    melsec: { port: "5000" },
    s7: { port: "102" },
  });
  assert.equal(legacy.workspace.lastHelpReference, null);
  assert.deepEqual(legacy.sessions[0].trendSelection, []);

  assert.throws(() => normalizeProjectDocument(validProject({
    workspace: { commandList: [{ fc: 99, unitId: 1, address: 0, quantity: 1 }] },
  })), (error) => error.code === "PROJECT_INVALID_COMMAND");
  assert.throws(() => normalizeProjectDocument(validProject({
    workspace: {
      simulators: { modbus: { mode: "tcp", port: "502", allowedStations: "bad" } },
    },
  })), (error) => error.code === "PROJECT_INVALID_SIMULATOR");
  assert.equal(normalizeProjectDocument(validProject({
    workspace: { lastHelpReference: { source: "unknown", variant: "x" } },
  })).workspace.lastHelpReference, null);
});

test("renderer persists and restores workspace state without execution", () => {
  const fs = require("node:fs");
  const path = require("node:path");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  assert.match(renderer, /tab\.trendSelection = \[\.\.\.trendSeries\.keys\(\)\]/);
  assert.match(renderer, /restoreTrendSelection\(tab\?\.trendSelection \?\? \[\]\)/);
  assert.match(renderer, /workspace: \{/);
  assert.match(renderer, /commandList: commandList\.map/);
  assert.match(renderer, /replaceCommandList\(project\.workspace\?\.commandList \?\? \[\]\)/);
  assert.match(renderer, /collectSimulatorWorkspace\(\)/);
  assert.match(renderer, /restoreSimulatorWorkspace\(/);
  assert.match(renderer, /savedProtocolGuideReference =/);
  assert.match(renderer, /restoreHelpReference\(/);
  assert.match(renderer, /modbus: \{ mode: "tcp", port: "502", allowedStations: "" \}/);
  assert.match(renderer, /模拟器配置和帮助引用/);
  assert.doesNotMatch(renderer, /restoreSimulatorWorkspace[\s\S]{0,900}(startSlave\(|mcStartSlave|s7StartSlave|start_tcp_slave|start_mc_tcp_slave|start_s7_slave)/);
});

test("uses the dedicated extension and sanitizes default filenames", () => {
  const service = new ProjectFileService();
  assert.equal(service.ensureExtension("D:\\work\\line"), "D:\\work\\line.nexus.json");
  assert.equal(service.ensureExtension("D:\\work\\line.nexus.json"), "D:\\work\\line.nexus.json");
  assert.equal(service.defaultFilename("A/B:产线"), "A_B_产线.nexus.json");
});
