"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const root = path.join(__dirname, "..");
const main = fs.readFileSync(path.join(root, "electron", "main.cjs"), "utf8");
const preload = fs.readFileSync(path.join(root, "electron", "preload.cjs"), "utf8");
const renderer = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
const html = fs.readFileSync(path.join(root, "index.html"), "utf8");

test("Electron renderer stays isolated without direct Node access", () => {
  const preferencesMatch = main.match(/webPreferences:\s*\{[\s\S]*?\n\s*\}/);
  assert.ok(preferencesMatch, "BrowserWindow webPreferences block is missing");
  const preferences = preferencesMatch[0];

  assert.match(preferences, /contextIsolation:\s*true/);
  assert.match(preferences, /nodeIntegration:\s*false/);
  assert.match(preferences, /sandbox:\s*true/);
  assert.doesNotMatch(main, /\bcontextIsolation:\s*false\b/);
  assert.doesNotMatch(main, /\bnodeIntegration:\s*true\b/);
  assert.doesNotMatch(main, /\bsandbox:\s*false\b/);
  assert.doesNotMatch(main, /\bwebSecurity:\s*false\b/);
  assert.match(main, /setWindowOpenHandler\(\(\)\s*=>\s*\(\{\s*action:\s*"deny"\s*\}\)\)/);
});

test("preload exposes one bridge with an action-level command allow-list", () => {
  assert.match(preload, /const allowedCommands = new Set\(\[/);
  assert.match(preload, /contextBridge\.exposeInMainWorld\("nexusDesktop"/);
  assert.equal(preload.match(/contextBridge\.exposeInMainWorld\(/g)?.length, 1);

  const invokeStart = preload.indexOf("invoke(command, args = {})");
  const invokeEnd = preload.indexOf("onPollData(callback)", invokeStart);
  assert.ok(invokeStart >= 0 && invokeEnd > invokeStart, "preload invoke function is missing");
  const invokeFunction = preload.slice(invokeStart, invokeEnd);
  assert.match(invokeFunction, /if \(!allowedCommands\.has\(command\)\)/);
  assert.match(invokeFunction, /return Promise\.reject\(/);
  assert.match(invokeFunction, /ipcRenderer\.invoke\(`nexus:\$\{command\}`,\s*args\)/);
  assert.doesNotMatch(preload, /require\(["']node:/);
});

test("CSP disallows arbitrary scripts and inline event handlers", () => {
  const csp = html.match(/http-equiv=["']Content-Security-Policy["'][^>]*content="([^"]+)"/i)?.[1];
  assert.ok(csp, "index.html CSP is missing");

  assert.match(csp, /(?:^|;)\s*default-src 'self'(?:;|$)/);
  assert.match(csp, /(?:^|;)\s*script-src 'self'(?:;|$)/);
  assert.match(csp, /(?:^|;)\s*object-src 'none'(?:;|$)/);
  assert.equal(csp.includes("unsafe-eval"), false);
  assert.equal(csp.includes("http://"), false);
  assert.equal(csp.includes("https://"), false);

  for (const scriptTag of html.matchAll(/<script\b([^>]*)>/gi)) {
    assert.match(scriptTag[1], /\bsrc=["'][^"']+["']/i);
  }
  assert.doesNotMatch(html, /\son[a-z]+\s*=/i);
  assert.doesNotMatch(renderer, /\son[a-z]+\s*=\s*["']/i);
  assert.doesNotMatch(`${html}\n${renderer}`, /javascript:/i);

});
