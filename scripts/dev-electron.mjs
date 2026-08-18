import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const electronPath = require("electron");
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const viteEntry = path.join(root, "node_modules", "vite", "bin", "vite.js");
const developmentUrl = "http://127.0.0.1:1420";

const vite = spawn(process.execPath, [viteEntry, "--host", "127.0.0.1", "--port", "1420"], {
  cwd: root,
  stdio: "inherit",
});

async function waitForServer() {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    try {
      const response = await fetch(developmentUrl);
      if (response.ok) return;
    } catch {}
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error("Vite 开发服务器启动超时。");
}

let electron;

function stop() {
  if (electron && !electron.killed) electron.kill();
  if (!vite.killed) vite.kill();
}

process.on("SIGINT", stop);
process.on("SIGTERM", stop);

try {
  await waitForServer();
  electron = spawn(electronPath, ["."], {
    cwd: root,
    env: { ...process.env, NEXUS_DEV_SERVER_URL: developmentUrl },
    stdio: "inherit",
  });
  electron.on("exit", (code) => {
    if (!vite.killed) vite.kill();
    process.exitCode = code ?? 0;
  });
} catch (error) {
  console.error(error.message);
  stop();
  process.exitCode = 1;
}
