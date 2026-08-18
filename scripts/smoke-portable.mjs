import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = path.join(root, "output", "portable", "Nexus 2.0");
const executable = path.join(packageRoot, "Nexus 2.0.exe");

if (!fs.existsSync(executable)) {
  throw new Error(`便携版 EXE 不存在：${executable}`);
}

const environment = {
  ...process.env,
  NEXUS_SMOKE_TEST: "1",
  ELECTRON_ENABLE_LOGGING: "1",
};
delete environment.NEXUS_RUST_CORE_PATH;

const child = spawn(executable, [], {
  cwd: packageRoot,
  env: environment,
  stdio: ["ignore", "pipe", "pipe"],
  windowsHide: true,
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
  console.error("NEXUS_PORTABLE_SMOKE_TIMEOUT");
}, 20_000);

child.on("error", (error) => {
  clearTimeout(timeout);
  console.error(`NEXUS_PORTABLE_LAUNCH_FAILED ${error.message}`);
  process.exitCode = 1;
});

child.on("exit", (code) => {
  clearTimeout(timeout);
  if (code !== 0 || !output.includes("NEXUS_UI_SMOKE_OK") || !output.includes("NEXUS_ELECTRON_SMOKE_OK")) {
    process.exitCode = code || 1;
    return;
  }
  console.log("NEXUS_PORTABLE_SMOKE_OK");
});
