"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");

const { runSoak } = require("./soak-r0.cjs");

test("R0 soak harness keeps a real poll stream alive and cleans it up", { timeout: 20_000 }, async () => {
  const result = await runSoak({
    durationMs: 2_000,
    intervalMs: 50,
    sampleIntervalMs: 400,
    progressIntervalMs: 2_000,
    maxRssGrowthBytes: 128 * 1024 * 1024,
    maxHandleGrowth: 100,
    maxTransportLatencyMs: 2_000,
    minSuccessRatio: 0.6,
  });

  assert.equal(result.passed, true);
  assert.equal(result.counters.dataErrors, 0);
  assert.equal(result.counters.streamErrors, 0);
  assert.ok(result.counters.successes >= 20, `unexpected success count: ${result.counters.successes}`);
  assert.equal(result.cleanup.streamStopped, true);
  assert.equal(result.cleanup.connectionClosed, true);
  assert.equal(result.cleanup.slaveStopped, true);
  assert.equal(result.cleanup.shutdown, true);
  assert.equal(result.exitCode, 0);
  assert.ok(result.resource.sampleCount >= 2);
  assert.equal(typeof result.resource.last.HandleCount, "number");
});
