const test = require("node:test");
const assert = require("node:assert/strict");

const { recoverRustCoreClient } = require("./rust-core-lifecycle.cjs");

test("replaces failed and stopped Rust core clients but preserves live states", () => {
  for (const state of ["failed", "stopped"]) {
    const replacement = { state: "idle" };
    assert.equal(recoverRustCoreClient({ state }, () => replacement), replacement);
  }
  for (const state of ["idle", "starting", "ready", "stopping"]) {
    const current = { state };
    assert.equal(recoverRustCoreClient(current, () => ({ state: "idle" })), current);
  }
});
