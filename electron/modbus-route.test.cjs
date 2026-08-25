const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const root = path.resolve(__dirname, "..");
const routeModuleUrl = pathToFileURL(path.join(root, "src", "modbus-route.js")).href;

function read(relPath) {
  return fs.readFileSync(path.join(root, relPath), "utf8");
}

function assertCommandPresent(haystack, command, where) {
  const quoted = new RegExp(`["'\`]${command}["'\`]`);
  const ipc = new RegExp(`nexus:${command}\\b`);
  assert.ok(quoted.test(haystack) || ipc.test(haystack), `${command} missing from ${where}`);
}

test("UDP never shares TCP read or write command names", async () => {
  const {
    resolveModbusReadCommand,
    resolveModbusWriteCommand,
    MODBUS_READ_FUNCTION_CODES,
    MODBUS_WRITE_FUNCTION_CODES,
  } = await import(routeModuleUrl);

  for (const fc of MODBUS_READ_FUNCTION_CODES) {
    const command = resolveModbusReadCommand("udp", fc);
    assert.match(command, /^udp_/);
    assert.doesNotMatch(command, /^tcp_/);
    assert.equal(resolveModbusReadCommand("tcp", fc).replace(/^tcp_/, "udp_"), command);
  }
  for (const fc of MODBUS_WRITE_FUNCTION_CODES) {
    const command = resolveModbusWriteCommand("udp", fc);
    assert.match(command, /^udp_/);
    assert.doesNotMatch(command, /^tcp_/);
  }
});

test("serial FC01/FC02 do not collapse to holding-register reads", async () => {
  const { resolveModbusReadCommand } = await import(routeModuleUrl);
  assert.equal(resolveModbusReadCommand("rtu", 1), "read_coils_once");
  assert.equal(resolveModbusReadCommand("ascii", 2), "read_discrete_inputs_once");
  assert.equal(resolveModbusReadCommand("rtu", 3), "read_holding_registers_once");
  assert.equal(resolveModbusReadCommand("ascii", 4), "read_input_registers_once");
});

test("TCP-like transports keep their framing at connect time", async () => {
  const { resolveModbusConnect } = await import(routeModuleUrl);
  assert.deepEqual(resolveModbusConnect("tcp"), { command: "open_tcp_connection", framing: "standard" });
  assert.deepEqual(resolveModbusConnect("udp"), { command: "open_udp_connection", framing: "standard" });
  assert.deepEqual(resolveModbusConnect("rtu-over-tcp"), { command: "open_tcp_connection", framing: "rtu-over-tcp" });
  assert.deepEqual(resolveModbusConnect("ascii-over-tcp"), { command: "open_tcp_connection", framing: "ascii-over-tcp" });
  assert.equal(resolveModbusConnect("rtu").command, null);
  assert.notEqual(resolveModbusConnect("udp").framing, "ascii-over-tcp");
});

test("unknown transport or function code fails closed", async () => {
  const { resolveModbusReadCommand, resolveModbusWriteCommand, resolveModbusConnect } = await import(routeModuleUrl);
  assert.equal(resolveModbusReadCommand("future", 3), null);
  assert.equal(resolveModbusReadCommand("tcp", 99), null);
  assert.equal(resolveModbusWriteCommand("rtu", 3), null);
  assert.equal(resolveModbusConnect("").command, null);
});

test("8x6 Modbus command matrix exists on the Electron/Rust bridge", async () => {
  const {
    MODBUS_TRANSPORTS,
    MODBUS_READ_FUNCTION_CODES,
    MODBUS_WRITE_FUNCTION_CODES,
    resolveModbusReadCommand,
    resolveModbusWriteCommand,
    resolveModbusConnect,
  } = await import(routeModuleUrl);
  const rustClient = read("electron/rust-core-client.cjs");
  const preload = read("electron/preload.cjs");
  const main = read("electron/main.cjs");
  const serialOnly = /_once$/;
  const commands = new Set();

  for (const transport of MODBUS_TRANSPORTS) {
    const connect = resolveModbusConnect(transport);
    if (connect.command) commands.add(connect.command);
    for (const fc of MODBUS_READ_FUNCTION_CODES) {
      const command = resolveModbusReadCommand(transport, fc);
      assert.ok(command, `${transport} FC${fc} read missing`);
      commands.add(command);
    }
    for (const fc of MODBUS_WRITE_FUNCTION_CODES) {
      const command = resolveModbusWriteCommand(transport, fc);
      assert.ok(command, `${transport} FC${fc} write missing`);
      commands.add(command);
    }
  }

  for (const command of commands) {
    assertCommandPresent(preload, command, "preload allow-list");
    assertCommandPresent(main, command, "main IPC");
    if (!serialOnly.test(command)) {
      assertCommandPresent(rustClient, command, "rust-core-client COMMANDS");
    }
  }
});

test("Modbus master UI uses the shared route helpers and does not hard-code UDP as TCP", () => {
  const renderer = read("src/main.js");
  const html = read("index.html");
  assert.match(renderer, /resolveModbusReadCommand/);
  assert.match(renderer, /resolveModbusWriteCommand/);
  assert.match(renderer, /resolveModbusConnect/);
  assert.doesNotMatch(renderer, /tcpCmdMap/);
  assert.doesNotMatch(renderer, /tcpWMap/);
  assert.match(html, /value="rtu"/);
  assert.match(html, /value="ascii"/);
  assert.match(html, /value="tcp"/);
  assert.match(html, /value="udp"/);
  assert.match(html, /value="rtu-over-tcp"/);
  assert.match(html, /value="ascii-over-tcp"/);

  const connectFn = renderer.match(/async function connectTcp\(\) \{[\s\S]*?\nasync function disconnectTcp\(/);
  assert.ok(connectFn, "connectTcp missing");
  assert.match(connectFn[0], /resolveModbusConnect/);
  assert.doesNotMatch(connectFn[0], /ascii-over-tcp"\s*;/);
});
