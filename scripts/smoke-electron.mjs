import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const electronPath = require("electron");
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const child = spawn(electronPath, ["."], {
  cwd: root,
  env: {
    ...process.env,
    NEXUS_SMOKE_TEST: "1",
    ELECTRON_ENABLE_LOGGING: "1",
  },
  stdio: ["ignore", "pipe", "pipe"],
});

let output = "";
child.stdout.on("data", (chunk) => {
  output += chunk;
  process.stdout.write(chunk);
});
child.stderr.on("data", (chunk) => {
  output += chunk;
  process.stderr.write(chunk);
});

const timeout = setTimeout(() => {
  child.kill();
  console.error("NEXUS_ELECTRON_SMOKE_TIMEOUT");
}, 20_000);

child.on("error", (error) => {
  clearTimeout(timeout);
  console.error(`NEXUS_ELECTRON_LAUNCH_FAILED ${error.message}`);
  process.exitCode = 1;
});

child.on("exit", (code) => {
  clearTimeout(timeout);
  if (code !== 0 || !output.includes("NEXUS_UI_SMOKE_OK") || !output.includes("NEXUS_ELECTRON_SMOKE_OK")) {
    process.exitCode = code || 1;
  }
});
