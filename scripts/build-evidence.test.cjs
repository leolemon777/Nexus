"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");

const {
  normalizePlan,
  redactSensitiveText,
  runBuildEvidence,
  verifyBuildEvidence,
} = require("./build-evidence.cjs");

function sha256(text) {
  return crypto.createHash("sha256").update(text).digest("hex");
}

test("build evidence redacts credentials in logs and query strings", () => {
  const redacted = redactSensitiveText([
    "password=hunter2 token=abc123",
    "Authorization: Bearer secret-value",
    "https://example.invalid/callback?token=secret-token&x=1",
    "user=operator private_key=-----BEGIN PRIVATE KEY-----secret-body-----END PRIVATE KEY-----",
    "C:\\Users\\operator\\AppData\\Local\\Nexus\\debug.log",
  ].join("\n"));
  assert.ok(!redacted.includes("hunter2"));
  assert.ok(!redacted.includes("abc123"));
  assert.ok(!redacted.includes("secret-value"));
  assert.ok(!redacted.includes("secret-token"));
  assert.ok(!redacted.includes("operator"));
  assert.ok(!redacted.includes("secret-body"));
  assert.match(redacted, /password=\[REDACTED\]/);
  assert.match(redacted, /token=\[REDACTED\]/);
  assert.match(redacted, /Authorization: Bearer \[REDACTED\]/);
  assert.match(redacted, /private_key=\[REDACTED(?:-PEM)?\]/);
  assert.match(redacted, /\[USER-PATH-REDACTED\]/);
});

test("build evidence runs bounded commands, records logs, and verifies hashes", async () => {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), "nexus-build-evidence-"));
  const sourceRoot = path.join(temporaryRoot, "source");
  const outputDirectory = path.join(temporaryRoot, "evidence");
  fs.mkdirSync(sourceRoot, { recursive: true });
  fs.writeFileSync(path.join(sourceRoot, "package.json"), "{}");
  fs.writeFileSync(path.join(sourceRoot, "script.js"), "console.log('ok');\n");

  const plan = {
    schemaVersion: 1,
    commands: [
      { id: "node-pass", kind: "node", script: "script.js", title: "Node pass" },
      { id: "npm-fail", kind: "npm", script: "missing-script", title: "Npm fail", timeoutMs: 5000 },
    ],
  };
  const planPath = path.join(temporaryRoot, "plan.json");
  fs.writeFileSync(planPath, JSON.stringify(plan));
  assert.equal(normalizePlan(plan, { projectRoot: sourceRoot }).length, 2);

  const calls = [];
  const summary = await runBuildEvidence({
    planPath,
    outputDirectory,
    sourceRoot,
    allowDirty: true,
    execImpl: async (program, args) => {
      calls.push([program, args]);
      if (args.join(" ").includes("missing-script")) {
        const error = new Error("command failed");
        error.code = 87;
        error.stdout = "token=secret-token\n";
        error.stderr = "npm ERR! password=hunter2\n";
        throw error;
      }
      return { stdout: "password=hunter2\n", stderr: "" };
    },
  });
  assert.equal(summary.overallOk, false);
  assert.deepEqual(summary.counts, { commands: 2, passed: 1, failed: 1 });
  assert.equal(summary.qualification, "candidate-dirty-source");
  assert.equal(calls.length, 2);
  const passLog = fs.readFileSync(path.join(outputDirectory, "node-pass.log"), "utf8");
  const failLog = fs.readFileSync(path.join(outputDirectory, "npm-fail.log"), "utf8");
  assert.ok(passLog.includes("password=[REDACTED]"));
  assert.ok(!failLog.includes("secret-token"));
  assert.ok(!failLog.includes("hunter2"));
  assert.ok(failLog.includes("exitCode=87"));
  assert.ok(fs.existsSync(path.join(outputDirectory, "BUILD-EVIDENCE.md")));

  const verification = verifyBuildEvidence({ outputDirectory });
  assert.equal(verification.verified, true);
  assert.equal(verification.overallOk, false);
  assert.deepEqual(verification.counts, summary.counts);

  fs.appendFileSync(path.join(outputDirectory, "node-pass.log"), "tampered");
  await assert.rejects(
    async () => verifyBuildEvidence({ outputDirectory }),
    error => error.code === "BUILD_EVIDENCE_SIZE_MISMATCH" || error.code === "BUILD_EVIDENCE_HASH_MISMATCH",
  );
  fs.rmSync(temporaryRoot, { recursive: true, force: true });
});

test("build evidence candidate plan and core build scripts enforce toolchain preflight", () => {
  const root = path.resolve(__dirname, "..");
  const candidate = fs.readFileSync(path.join(root, "scripts", "build-evidence.candidate.json"), "utf8");
  const packageScript = fs.readFileSync(path.join(root, "scripts", "package-portable.ps1"), "utf8");
  const rustScript = fs.readFileSync(path.join(root, "scripts", "test-rust-jsonl.ps1"), "utf8");
  assert.match(candidate, /"id": "toolchain-preflight"/);
  assert.match(packageScript, /toolchain-preflight\.cjs/);
  assert.match(rustScript, /toolchain-preflight\.cjs/);
});
