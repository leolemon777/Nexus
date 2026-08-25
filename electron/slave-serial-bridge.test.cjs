"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { SlaveSerialBridge } = require("./slave-serial-bridge.cjs");

test("rejects starting a serial slave before the COM port is open", async () => {
  const bridge = new SlaveSerialBridge();
  const calls = [];
  await assert.rejects(
    bridge.start({ current: null }, async (...args) => calls.push(args)),
    (error) => error.code === "SERIAL_NOT_OPEN",
  );
  assert.deepEqual(calls, []);
  assert.equal(bridge.active, false);
});

test("starts and stops a serial slave only on an open COM port", async () => {
  const port = new EventEmitter();
  port.isOpen = true;
  const serialService = { current: { port, config: { baudRate: 9600 } } };
  const calls = [];
  const bridge = new SlaveSerialBridge();
  await bridge.start(serialService, async (command, payload) => {
    calls.push({ command, payload });
    return {};
  }, "slave-a");

  assert.equal(bridge.active, true);
  assert.equal(port.listenerCount("data"), 1);
  await bridge.stop();
  assert.equal(bridge.active, false);
  assert.equal(port.listenerCount("data"), 0);
  assert.deepEqual(calls.map((call) => call.command), ["start_serial_slave", "stop_serial_slave"]);
});
