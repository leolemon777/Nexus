import fs from "node:fs/promises";
import path from "node:path";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const electronPackagePath = require.resolve("electron/package.json");
const electronRoot = path.dirname(electronPackagePath);
const distPath = path.resolve(electronRoot, "dist");
const expectedRoot = `${path.resolve(electronRoot)}${path.sep}`;

if (!`${distPath}${path.sep}`.startsWith(expectedRoot)) {
  throw new Error(`拒绝处理 Electron 包目录之外的路径：${distPath}`);
}

const electronPackage = require(electronPackagePath);
const executableName = process.platform === "win32" ? "electron.exe" : "electron";
const executablePath = path.join(distPath, executableName);
const versionFile = path.join(distPath, "version");

async function isReady() {
  try {
    const installedVersion = (await fs.readFile(versionFile, "utf8")).trim().replace(/^v/, "");
    await fs.access(executablePath);
    return installedVersion === electronPackage.version;
  } catch {
    return false;
  }
}

if (await isReady()) {
  console.log(`Electron ${electronPackage.version} binary is ready.`);
  process.exit(0);
}

const { downloadArtifact } = require("@electron/get");
const { extract } = require("@electron-internal/extract-zip");
const zipPath = await downloadArtifact({
  version: electronPackage.version,
  artifactName: "electron",
  platform: process.platform,
  arch: process.arch,
  checksums: require(path.join(electronRoot, "checksums.json")),
});

await fs.rm(distPath, { recursive: true, force: true });
await fs.mkdir(distPath, { recursive: true });
await extract(zipPath, { dir: distPath });
await fs.writeFile(path.join(electronRoot, "path.txt"), executableName, "utf8");

if (!(await isReady())) {
  throw new Error(`Electron ${electronPackage.version} binary validation failed.`);
}

console.log(`Electron ${electronPackage.version} binary installed and validated.`);
