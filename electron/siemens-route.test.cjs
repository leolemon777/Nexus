const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const root = path.resolve(__dirname, "..");
const routeModuleUrl = pathToFileURL(path.join(root, "src", "siemens-route.js")).href;
const SELECTABLE_VARIANTS = ["s7comm", "smart", "ppi", "ppi-serial", "fw", "uss", "rk512", "webapi"];
const ELECTRON_ONLY = new Set([
  "get_serial_status",
  "ppi_serial_read",
  "uss_serial_read",
  "rk512_serial_read",
  "s7web_connect",
  "s7web_read",
  "s7web_write",
  "s7web_disconnect",
]);

function read(relPath) {
  return fs.readFileSync(path.join(root, relPath), "utf8");
}

function assertCommandPresent(haystack, command, where) {
  const quoted = new RegExp(`["'\`]${command}["'\`]`);
  const ipc = new RegExp(`nexus:${command}\\b`);
  assert.ok(quoted.test(haystack) || ipc.test(haystack), `${command} missing from ${where}`);
}

function extractFunction(source, name, nextName) {
  const match = source.match(new RegExp(`async function ${name}\\(\\) \\{[\\s\\S]*?\\nasync function ${nextName}\\(`));
  assert.ok(match, `${name} not found before ${nextName}`);
  return match[0];
}

function selectableSiemensVariants(html) {
  const select = html.match(/<select id="s7-variant"[\s\S]*?<\/select>/);
  assert.ok(select, "s7-variant select missing");
  const values = [];
  const optionRe = /<option\s+([^>]*?)>/g;
  let match;
  while ((match = optionRe.exec(select[0]))) {
    const attrs = match[1];
    if (/\bdisabled\b/.test(attrs)) continue;
    const value = /value="([^"]+)"/.exec(attrs);
    if (value) values.push(value[1]);
  }
  return values;
}

test("maps each Siemens UI variant to its own online command family", async () => {
  const { resolveSiemensRoute } = await import(routeModuleUrl);

  assert.deepEqual(
    SELECTABLE_VARIANTS.map((variant) => {
      const route = resolveSiemensRoute(variant);
      return {
        variant: route.variant,
        kind: route.kind,
        online: route.online,
        connect: route.connectCommand,
        read: route.readCommand,
        write: route.writeCommand,
        disconnect: route.disconnectCommand,
      };
    }),
    [
      { variant: "s7comm", kind: "s7comm", online: true, connect: "open_s7_connection", read: "s7_read", write: "s7_write", disconnect: "close_connection" },
      { variant: "smart", kind: "s7comm", online: true, connect: "open_s7_connection", read: "s7_read", write: "s7_write", disconnect: "close_connection" },
      { variant: "ppi", kind: "ppi", online: true, connect: "open_ppi_tcp", read: "ppi_read", write: "ppi_write", disconnect: "close_connection" },
      { variant: "ppi-serial", kind: "ppi-serial", online: true, connect: "get_serial_status", read: "ppi_serial_read", write: null, disconnect: null },
      { variant: "fw", kind: "fetchwrite", online: true, connect: "open_fw_tcp", read: "fw_read", write: "fw_write", disconnect: "close_connection" },
      { variant: "uss", kind: "uss-serial", online: true, connect: "get_serial_status", read: "uss_serial_read", write: null, disconnect: null },
      { variant: "rk512", kind: "rk512-serial", online: true, connect: "get_serial_status", read: "rk512_serial_read", write: null, disconnect: null },
      { variant: "webapi", kind: "webapi", online: true, connect: "s7web_connect", read: "s7web_read", write: "s7web_write", disconnect: "s7web_disconnect" },
    ],
  );
});

test("serial read-only variants do not silently turn into S7comm", async () => {
  const { resolveSiemensRoute } = await import(routeModuleUrl);
  for (const variant of ["uss", "rk512"]) {
    const route = resolveSiemensRoute(variant);
    assert.equal(route.online, true);
    assert.match(route.kind, /serial/);
    assert.ok(route.readCommand);
    assert.equal(route.writeCommand, null);
    assert.equal(route.connectCommand, "get_serial_status");
    assert.match(route.reason, /只读/);
  }
});

test("unknown variants fail closed instead of falling back to S7comm", async () => {
  const { resolveSiemensRoute } = await import(routeModuleUrl);
  for (const variant of ["future-variant", "", "   ", "S7COMM-PLUS", "profinet"]) {
    const route = resolveSiemensRoute(variant);
    assert.equal(route.kind, "unknown");
    assert.equal(route.online, false);
    assert.equal(route.connectCommand, null);
    assert.equal(route.readCommand, null);
    assert.equal(route.writeCommand, null);
    assert.match(route.reason, /不得回退到 S7comm/);
  }
});

test("every selectable Siemens UI option has a named route", async () => {
  const { resolveSiemensRoute } = await import(routeModuleUrl);
  const html = read("index.html");
  const registry = read("src/protocol-registry.js");
  const uiVariants = selectableSiemensVariants(html);
  assert.deepEqual([...uiVariants].sort(), [...SELECTABLE_VARIANTS].sort());

  const registryMatch = registry.match(/siemens:\s*\[([^\]]+)\]/);
  assert.ok(registryMatch, "protocol-registry siemens variants missing");
  const registryVariants = [...registryMatch[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
  assert.deepEqual(registryVariants, SELECTABLE_VARIANTS);

  for (const variant of uiVariants) {
    const route = resolveSiemensRoute(variant);
    assert.notEqual(route.kind, "unknown", `${variant} resolved as unknown`);
    assert.equal(route.variant, variant);
    assert.equal(route.online, true);
  }
});

test("Siemens route commands exist on the Electron/Rust bridge", async () => {
  const { listSiemensRoutes } = await import(routeModuleUrl);
  const rustClient = read("electron/rust-core-client.cjs");
  const preload = read("electron/preload.cjs");
  const main = read("electron/main.cjs");

  for (const route of listSiemensRoutes()) {
    const commands = [route.connectCommand, route.readCommand, route.writeCommand, route.disconnectCommand]
      .filter(Boolean);
    for (const command of commands) {
      assertCommandPresent(preload, command, "preload allow-list");
      assertCommandPresent(main, command, "main IPC");
      if (!ELECTRON_ONLY.has(command)) {
        assertCommandPresent(rustClient, command, "rust-core-client COMMANDS");
      }
    }
  }
});

test("s7Connect dispatches on route.kind and never defaults unknown variants to S7comm", () => {
  const renderer = read("src/main.js");
  const connectFn = extractFunction(renderer, "s7Connect", "s7Disconnect");
  assert.match(connectFn, /switch \(route\.kind\)/);
  for (const kind of ["ppi-serial", "ppi", "fetchwrite", "webapi", "uss-serial", "rk512-serial", "s7comm"]) {
    assert.match(connectFn, new RegExp(`case "${kind}"`));
  }
  assert.match(connectFn, /default:/);
  assert.doesNotMatch(connectFn, /默认:S7comm/);

  const ussCase = connectFn.match(/case "uss-serial": \{[\s\S]*?return;\s*\}/);
  const rkCase = connectFn.match(/case "rk512-serial": \{[\s\S]*?return;\s*\}/);
  const ppiSerialCase = connectFn.match(/case "ppi-serial": \{[\s\S]*?return;\s*\}/);
  const webapiCase = connectFn.match(/case "webapi": \{[\s\S]*?return;\s*\}/);
  const fwCase = connectFn.match(/case "fetchwrite": \{[\s\S]*?return;\s*\}/);
  assert.ok(ussCase && rkCase && ppiSerialCase && webapiCase && fwCase, "expected dedicated connect cases");
  for (const [name, block] of [
    ["uss", ussCase[0]],
    ["rk512", rkCase[0]],
    ["ppi-serial", ppiSerialCase[0]],
    ["webapi", webapiCase[0]],
    ["fw", fwCase[0]],
  ]) {
    assert.doesNotMatch(block, /open_s7_connection/, `${name} connect still calls open_s7_connection`);
  }
});

test("s7Read and s7Write do not fall back to S7comm for other kinds", () => {
  const renderer = read("src/main.js");
  const readFn = extractFunction(renderer, "s7Read", "s7Write");
  const writeFn = extractFunction(renderer, "s7Write", "s7Diag");
  assert.doesNotMatch(readFn, /\bvariant === "uss"/);
  assert.doesNotMatch(readFn, /\bvariant === "rk512"/);
  assert.match(readFn, /route\.kind !== "s7comm"/);
  assert.match(writeFn, /route\.kind !== "s7comm"/);
});
