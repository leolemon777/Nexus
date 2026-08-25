"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { redactLogMessage, redactLogText } = require("./log-redaction-service.cjs");

test("log redaction removes credentials, private keys, and user identity", () => {
  const privateKey = [
    "-----BEGIN RSA PRIVATE KEY-----",
    "MIIEowIBAAKCAQEArandombody",
    "-----END RSA PRIVATE KEY-----",
  ].join("\n");
  const input = [
    "password=hunter2 token=abc123",
    "Authorization: Bearer secret-value",
    `private_key=${privateKey}`,
    "user=operator username=plant-admin email=operator@factory.example",
    "C:\\Users\\plant-admin\\AppData\\Local\\Nexus\\logs",
    "/home/operator/.nexus",
  ].join("\n");
  const output = redactLogText(input);

  for (const forbidden of [
    "hunter2",
    "abc123",
    "secret-value",
    "MIIEowIBAAKCAQEArandombody",
    "operator@factory.example",
    "plant-admin",
    "/.nexus",
  ]) {
    assert.ok(!output.includes(forbidden), `log redaction leaked ${forbidden}`);
  }
  assert.match(output, /password=\[REDACTED\]/);
  assert.match(output, /token=\[REDACTED\]/);
  assert.match(output, /Authorization: Bearer \[REDACTED\]/);
  assert.match(output, /private_key=\[REDACTED(?:-PEM)?\]/);
  assert.match(output, /user=\[REDACTED\]/);
  assert.match(output, /username=\[REDACTED\]/);
  assert.match(output, /email=\[REDACTED\]/);
  assert.match(output, /\[USER-PATH-REDACTED\]/);
});

test("log messages remain bounded", () => {
  assert.equal(redactLogMessage("x".repeat(20), 10), "x".repeat(10));
  assert.equal(redactLogMessage(null), "");
});

test("runtime and persisted log outlets use the shared redaction service", () => {
  const fs = require("node:fs");
  const path = require("node:path");
  const root = path.join(__dirname, "..");
  const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
  const rustClient = fs.readFileSync(path.join(root, "electron", "rust-core-client.cjs"), "utf8");
  const writeAudit = fs.readFileSync(path.join(root, "electron", "write-audit-service.cjs"), "utf8");
  const buildEvidence = fs.readFileSync(path.join(root, "scripts", "build-evidence.cjs"), "utf8");

  assert.match(main, /redactLogText\(error\.message\)/);
  assert.match(rustClient, /redactLogText\(message\)/);
  assert.match(writeAudit, /redactLogMessage\(raw\.message,\s*512\)/);
  assert.match(buildEvidence, /redactLogText\(text\)/);
});
