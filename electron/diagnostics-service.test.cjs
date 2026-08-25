"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const {
  DIAGNOSTICS_SCHEMA_VERSION,
  MAX_RECENT_FRAMES,
  DiagnosticsService,
  redactIPv4,
  redactIPv6,
  redactMac,
  sanitize,
} = require("./diagnostics-service.cjs");

class MemoryFs {
  constructor() {
    this.files = new Map();
  }
  writeFileSync(filePath, contents) {
    this.files.set(filePath, contents);
  }
}

function createService() {
  const memoryFs = new MemoryFs();
  const osImpl = {
    platform: () => "win32",
    arch: () => "x64",
    release: () => "10.0.test",
    homedir: () => "C:\\Users\\test-user",
    freemem: () => 1024,
    totalmem: () => 4096,
    networkInterfaces: () => ({
      "Ethernet": [
        {
          family: "IPv4",
          address: "192.168.1.23",
          netmask: "255.255.255.0",
          mac: "AA:BB:CC:DD:EE:FF",
          internal: false,
        },
        { family: "IPv6", address: "fe80::1234", mac: "AA:BB:CC:DD:EE:FF", internal: false },
      ],
      "Loopback": [
        { family: "IPv4", address: "127.0.0.1", netmask: "255.0.0.0", mac: "00:00:00:00:00:00", internal: true },
      ],
    }),
  };
  const service = new DiagnosticsService({
    app: { getVersion: () => "9.9.9" },
    osImpl,
    fsImpl: memoryFs,
    pathImpl: path,
    processInfo: { electron: "43.1.0", node: "26.1.0", chrome: "140.0.0" },
  });
  return { service, memoryFs };
}

test("diagnostic service redacts topology and credentials", () => {
  assert.equal(redactIPv4("192.168.1.23"), "192.168.x.x");
  assert.equal(redactIPv4("10.1.2.3"), "10.x.x.x");
  assert.match(redactIPv4("8.8.8.8"), /^public-[0-9a-f]{10}$/);
  assert.match(redactIPv6("fe80::1234"), /^ipv6-[0-9a-f]{10}$/);
  assert.match(redactIPv6("fe80::1234%eth5"), /^ipv6-[0-9a-f]{10}$/);
  assert.match(redactMac("AA:BB:CC:DD:EE:FF"), /^mac-[0-9a-f]{10}$/);
  assert.deepEqual(sanitize({
    password: "secret-value",
    nested: { apiToken: "abc", host: "172.20.3.4" },
  }), {
    password: "[REDACTED]",
    nested: { apiToken: "[REDACTED]", host: "172.20.x.x" },
  });
});

test("diagnostic service exports structured JSON plus a sanitized TXT summary", () => {
  const { service, memoryFs } = createService();
  const recentFrames = Array.from({ length: MAX_RECENT_FRAMES + 25 }, (_, index) => ({
    timestamp: 1_700_000_000_000 + index,
    direction: index % 2 ? "TX" : "RX",
    bytes: [index & 0xff, 0x02],
    password: "must-not-appear",
  }));
  const result = service.export({
    outputDirectory: "D:\\diag-output",
    timestamp: "2026-08-23T12-00-00",
    backendStatus: {
      mode: "full",
      serialTransport: { state: "closed", config: { host: "10.20.30.40" } },
      rustCore: { state: "ready", binaryAvailable: true },
    },
    serialStatus: { isOpen: false, config: null },
    serialPorts: [{
      name: "COM3",
      manufacturer: "Test Adapter",
      serialNumber: "SERIAL-PRIVATE",
      vendorId: "0403",
      productId: "6001",
    }],
    recentFrames,
    projectSnapshot: {
      projectName: "Customer Secret Project",
      hasPath: true,
      activeView: "master",
      activeSession: "Line A",
      config: { tcp: { host: "192.168.20.30", port: "502" } },
      sessionCount: 2,
      pointCount: 25,
    },
    rustCoreLastError: null,
  });

  assert.equal(result.ok, true);
  assert.match(result.paths.json, /Nexus诊断_2026-08-23T12-00-00\.json$/);
  assert.match(result.paths.text, /Nexus诊断_2026-08-23T12-00-00\.txt$/);
  assert.equal(memoryFs.files.size, 2);
  const report = JSON.parse(memoryFs.files.get(result.paths.json));
  assert.equal(report.schemaVersion, DIAGNOSTICS_SCHEMA_VERSION);
  assert.equal(report.product.version, "9.9.9");
  assert.equal(report.communication.recentFrames.length, MAX_RECENT_FRAMES);
  assert.equal(report.communication.recentFrames[0].index, 0);
  assert.equal(report.serial.ports[0].path, "COM3");
  assert.notEqual(report.serial.ports[0].serialNumber, "SERIAL-PRIVATE");
  assert.equal(report.network.interfaces[0].ipv4[0].address, "192.168.x.x");
  assert.match(report.network.interfaces[0].mac, /^mac-[0-9a-f]{10}$/);
  assert.equal(report.network.interfaces[0].ipv6Count, 1);
  assert.match(report.project.name, /^project-[0-9a-f]{10}$/);
  assert.equal(report.project.config.tcp.host, "192.168.x.x");

  const allOutput = [...memoryFs.files.values()].join("\n");
  for (const forbidden of [
    "192.168.1.23",
    "fe80::1234",
    "AA:BB:CC:DD:EE:FF",
    "SERIAL-PRIVATE",
    "secret-value",
    "must-not-appear",
    "Customer Secret Project",
    "test-user",
  ]) {
    assert.ok(!allOutput.includes(forbidden), `diagnostic output leaked ${forbidden}`);
  }
});

test("diagnostic export stays wired through preload, main IPC, renderer data, and sidebar UI", () => {
  const main = fs.readFileSync(path.join(__dirname, "main.cjs"), "utf8");
  const preload = fs.readFileSync(path.join(__dirname, "preload.cjs"), "utf8");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  const html = fs.readFileSync(path.join(__dirname, "..", "index.html"), "utf8");

  assert.match(main, /DiagnosticsService/);
  assert.match(main, /nexus:export_diagnostics/);
  assert.match(main, /serialDebugService\.getLog\(\)/);
  assert.match(preload, /export_diagnostics/);
  assert.match(renderer, /recentFrames: traceHistory\.slice\(-100\)/);
  assert.match(renderer, /projectSnapshot:/);
  assert.match(html, /id="export-diagnostics"/);
});
