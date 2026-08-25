const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const root = path.resolve(__dirname, "..");
const routeModuleUrl = pathToFileURL(path.join(root, "src", "melsec-route.js")).href;
const SELECTABLE_VARIANTS = [
  "3e", "4e", "ascii-3e", "ascii-4e", "mc-udp-3e", "mc-udp-4e", "mc-1e", "mc-c24", "fx-links", "fx-prog",
];
const ELECTRON_ONLY = new Set(["get_serial_status", "mc_c24_serial_read", "fx_serial_transact"]);

function read(relPath) {
  return fs.readFileSync(path.join(root, relPath), "utf8");
}

function assertCommandPresent(haystack, command, where) {
  const quoted = new RegExp(`["'\`]${command}["'\`]`);
  const ipc = new RegExp(`nexus:${command}\\b`);
  assert.ok(quoted.test(haystack) || ipc.test(haystack), `${command} missing from ${where}`);
}

function selectableMelsecVariants(html) {
  const select = html.match(/<select id="mc-frame-type"[\s\S]*?<\/select>/);
  assert.ok(select, "mc-frame-type select missing");
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

test("maps each MELSEC UI variant to its own command family", async () => {
  const { resolveMelsecRoute } = await import(routeModuleUrl);
  const mapped = SELECTABLE_VARIANTS.map((variant) => {
    const route = resolveMelsecRoute(variant);
    return {
      variant: route.variant,
      kind: route.kind,
      connect: route.connectCommand,
      read: route.readCommand,
      write: route.writeCommand,
    };
  });
  assert.deepEqual(mapped, [
    { variant: "3e", kind: "mc-tcp", connect: "open_mc_tcp_connection", read: "mc_tcp_read", write: "mc_tcp_write" },
    { variant: "4e", kind: "mc-tcp", connect: "open_mc_tcp_connection", read: "mc_tcp_read", write: "mc_tcp_write" },
    { variant: "ascii-3e", kind: "mc-ascii", connect: "open_mc_ascii_connection", read: "mc_ascii_read", write: "mc_ascii_write" },
    { variant: "ascii-4e", kind: "mc-ascii", connect: "open_mc_ascii_connection", read: "mc_ascii_read", write: "mc_ascii_write" },
    { variant: "mc-udp-3e", kind: "mc-udp", connect: "open_mc_udp_connection", read: "mc_udp_read", write: "mc_udp_write" },
    { variant: "mc-udp-4e", kind: "mc-udp", connect: "open_mc_udp_connection", read: "mc_udp_read", write: "mc_udp_write" },
    { variant: "mc-1e", kind: "mc-1e", connect: "open_mc_1e_tcp", read: "mc_1e_read", write: "mc_1e_write" },
    { variant: "mc-c24", kind: "mc-c24-serial", connect: "get_serial_status", read: "mc_c24_serial_read", write: null },
    { variant: "fx-links", kind: "fx-links", connect: "get_serial_status", read: "fx_serial_transact", write: "fx_serial_transact" },
    { variant: "fx-prog", kind: "fx-prog", connect: "get_serial_status", read: "fx_serial_transact", write: "fx_serial_transact" },
  ]);
});

test("C24 write is disabled and must not use the FX serial path", async () => {
  const { resolveMelsecRoute } = await import(routeModuleUrl);
  const route = resolveMelsecRoute("mc-c24");
  assert.equal(route.writeCommand, null);
  assert.equal(route.readCommand, "mc_c24_serial_read");
  assert.match(route.reason, /只读|未实现/);

  const renderer = read("src/main.js");
  const writeFn = renderer.match(/async function mcWrite\(\) \{[\s\S]*?\n\/\/ === 欧姆龙/);
  assert.ok(writeFn, "mcWrite missing");
  assert.match(writeFn[0], /mcFxProtocol === "c24"/);
  assert.match(writeFn[0], /写入未开放/);
  assert.match(writeFn[0], /mcFxProtocol === "links" \|\| mcFxProtocol === "prog"/);

  const syncFn = renderer.match(/function mcSyncButtons\(\) \{[\s\S]*?\nfunction mcRenderRows/);
  assert.ok(syncFn, "mcSyncButtons missing");
  assert.match(syncFn[0], /mcFxProtocol === "c24"/);
  assert.match(syncFn[0], /#mc-write/);

  const fxHandler = read("electron/main.cjs");
  assert.match(fxHandler, /FX_BAD_PROTOCOL/);
  assert.match(fxHandler, /nexus:mc_c24_serial_read/);
});

test("unknown MELSEC variants fail closed instead of falling back to 3E", async () => {
  const { resolveMelsecRoute } = await import(routeModuleUrl);
  for (const variant of ["", "future", "3E-binary", "slmp"]) {
    const route = resolveMelsecRoute(variant);
    assert.equal(route.kind, "unknown");
    assert.equal(route.online, false);
    assert.equal(route.readCommand, null);
    assert.match(route.reason, /不得回退到 MC 3E/);
  }
});

test("every selectable MELSEC UI option has a named route", async () => {
  const { resolveMelsecRoute } = await import(routeModuleUrl);
  const uiVariants = selectableMelsecVariants(read("index.html"));
  assert.deepEqual([...uiVariants].sort(), [...SELECTABLE_VARIANTS].sort());
  const registry = read("src/protocol-registry.js");
  const registryMatch = registry.match(/melsec:\s*\[([^\]]+)\]/);
  assert.ok(registryMatch);
  const registryVariants = [...registryMatch[1].matchAll(/"([^"]+)"/g)].map((item) => item[1]);
  assert.deepEqual(registryVariants, SELECTABLE_VARIANTS);
  for (const variant of uiVariants) {
    const route = resolveMelsecRoute(variant);
    assert.notEqual(route.kind, "unknown", `${variant} resolved as unknown`);
    assert.equal(route.variant, variant);
  }
});

test("MELSEC route commands exist on the Electron/Rust bridge", async () => {
  const { listMelsecRoutes } = await import(routeModuleUrl);
  const rustClient = read("electron/rust-core-client.cjs");
  const preload = read("electron/preload.cjs");
  const main = read("electron/main.cjs");
  for (const route of listMelsecRoutes()) {
    for (const command of [route.connectCommand, route.readCommand, route.writeCommand, route.disconnectCommand].filter(Boolean)) {
      assertCommandPresent(preload, command, "preload allow-list");
      assertCommandPresent(main, command, "main IPC");
      if (!ELECTRON_ONLY.has(command)) {
        assertCommandPresent(rustClient, command, "rust-core-client COMMANDS");
      }
    }
  }
});
