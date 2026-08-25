"use strict";

const crypto = require("node:crypto");
const { createHash } = crypto;
const fs = require("node:fs");
const path = require("node:path");
const { execFile } = require("node:child_process");
const { promisify } = require("node:util");

const execFileAsync = promisify(execFile);
const MANIFEST_SCHEMA_VERSION = 1;
const SPDX_VERSION = "SPDX-2.3";
const TOOL_NAME = "nexus-release-manifest";
const TOOL_VERSION = "1";

function metadataError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

function safeRelativePath(value) {
  const normalized = String(value ?? "").replace(/\\/g, "/");
  if (!normalized || normalized === "." || normalized.startsWith("/") || /^[A-Za-z]:/.test(normalized)) {
    throw metadataError("RELEASE_PATH_INVALID", `发布文件路径无效：${normalized || "(空)"}`);
  }
  const parts = normalized.split("/");
  if (parts.includes("..") || parts.includes("")) {
    throw metadataError("RELEASE_PATH_INVALID", `发布文件路径无效：${normalized}`);
  }
  return normalized;
}

function purlFor(ecosystem, name, version) {
  if (ecosystem === "npm") {
    const encodedName = name.startsWith("@")
      ? name.replace(/^@/, "%40")
      : name;
    return `pkg:npm/${encodedName}@${encodeURIComponent(version)}`;
  }
  return `pkg:${ecosystem}/${encodeURIComponent(name)}@${encodeURIComponent(version)}`;
}

function parseCargoLock(text) {
  const lines = String(text ?? "").split(/\r?\n/);
  const packages = [];
  let current = null;
  let inDependencies = false;
  for (const line of lines) {
    if (line.trim() === "[[package]]") {
      if (current) packages.push(current);
      current = { dependencies: [] };
      inDependencies = false;
      continue;
    }
    if (!current) continue;
    if (line.trim() === "[[patch.unused]]" || line.trim() === "[[patch]]") {
      if (current) packages.push(current);
      current = null;
      continue;
    }
    if (line.trim() === "[dependencies]") {
      inDependencies = true;
      continue;
    }
    if (/^dependencies\s*=\s*\[$/.test(line.trim())) {
      inDependencies = true;
      continue;
    }
    if (line.startsWith("[") && line.trim() !== "[") {
      inDependencies = false;
      continue;
    }
    const match = /^([A-Za-z0-9_-]+)\s*=\s*"(.*)",?$/.exec(line.trim());
    if (!match && inDependencies) {
      const dependency = /^"(.+)",?$/.exec(line.trim());
      if (dependency) current.dependencies.push(dependency[1]);
      continue;
    }
    if (!match) continue;
    const [, key, rawValue] = match;
    const value = rawValue.replace(/\\"/g, '"').replace(/,$/, "");
    if (inDependencies) current.dependencies.push(value);
    else current[key] = value;
  }
  if (current) packages.push(current);
  return packages.map((entry) => ({
    name: entry.name,
    version: entry.version,
    source: entry.source ?? null,
    checksum: entry.checksum ?? null,
    dependencies: [...entry.dependencies],
  })).filter((entry) => entry.name && entry.version);
}

function npmNameFromLockPath(lockPath) {
  const normalized = String(lockPath ?? "").replace(/\\/g, "/");
  const match = /(?:^|\/)node_modules\/(?:@[^/]+\/[^/]+|[^@/][^/]*)$/.exec(normalized);
  return match ? normalized.slice(match.index + match[0].indexOf("node_modules/") + "node_modules/".length) : null;
}

function parsePackageLock(lock, { runtimeDependencies = [] } = {}) {
  const root = lock && typeof lock === "object" ? lock : {};
  const packages = root.packages && typeof root.packages === "object" ? root.packages : {};
  const output = new Map();
  const runtimeNames = new Set(runtimeDependencies);
  for (const [lockPath, entry] of Object.entries(packages)) {
    if (!entry || typeof entry !== "object") continue;
    const name = lockPath === "" ? root.name ?? "nexus-rust" : npmNameFromLockPath(lockPath);
    if (!name) continue;
    const isProduction = entry.dev !== true && entry.devOptional !== true;
    const isRuntime = runtimeNames.has(name) || name === "electron";
    if (lockPath !== "" && !isProduction && !isRuntime) continue;
    output.set(name, {
      name,
      version: entry.version ?? "NOASSERTION",
      license: entry.license ?? null,
      resolved: entry.resolved ?? null,
      integrity: entry.integrity ?? null,
      scope: lockPath === "" ? "application" : (isRuntime || isProduction ? "required" : "excluded"),
    });
  }
  for (const name of runtimeNames) {
    if (!output.has(name)) {
      throw metadataError("RELEASE_RUNTIME_DEPENDENCY_MISSING", `package-lock 中缺少运行时依赖：${name}`);
    }
  }
  return [...output.values()];
}

function sha256File(filePath) {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256");
    const stream = fs.createReadStream(filePath, { autoClose: true });
    stream.on("data", chunk => hash.update(chunk));
    stream.on("error", reject);
    stream.on("end", () => resolve(hash.digest("hex")));
  });
}

function walkFiles(root, { limit = 100_000 } = {}) {
  const files = [];
  const stack = [root];
  while (stack.length > 0) {
    const current = stack.pop();
    const entries = fs.readdirSync(current, { withFileTypes: true });
    for (const entry of entries) {
      const fullPath = path.join(current, entry.name);
      if (entry.isDirectory()) stack.push(fullPath);
      else if (entry.isFile()) files.push(fullPath);
      if (files.length + stack.length > limit) {
        throw metadataError("RELEASE_PACKAGE_TOO_LARGE", `发布包文件数量超过 ${limit}`);
      }
    }
  }
  return files.sort((left, right) => left.localeCompare(right, "en"));
}

function spdxExpression(value) {
  if (!value) return "NOASSERTION";
  return String(value);
}

function createSpdxDocument({ app, components, timestamp, namespaceId }) {
  const packages = components.map((component, index) => ({
    name: component.name,
    SPDXID: `SPDXRef-Package-${index}`,
    versionInfo: component.version,
    downloadLocation: component.resolved ?? "NOASSERTION",
    filesAnalyzed: false,
    licenseConcluded: spdxExpression(component.license),
    licenseDeclared: spdxExpression(component.license),
    copyrightText: "NOASSERTION",
    externalRefs: [{
      referenceCategory: "PACKAGE-MANAGER",
      referenceType: "purl",
      referenceLocator: purlFor(component.ecosystem, component.name, component.version),
    }],
  }));
  return {
    spdxVersion: SPDX_VERSION,
    dataLicense: "CC0-1.0",
    SPDXID: "SPDXRef-DOCUMENT",
    name: `${app.name}-${app.version}-sbom`,
    documentNamespace: `https://spdx.org/spdxdocs/${app.name}-${app.version}-${namespaceId}`,
    creationInfo: {
      created: timestamp,
      creators: [`Tool:${TOOL_NAME}-${TOOL_VERSION}`],
    },
    packages,
    relationships: [{
      spdxElementId: "SPDXRef-DOCUMENT",
      relationshipType: "DESCRIBES",
      relatedSpdxElement: "SPDXRef-Package-0",
    }],
  };
}

async function sourceMetadata(sourceRoot) {
  let commit = null;
  let dirty = null;
  try {
    const rev = await execFileAsync("git", ["rev-parse", "HEAD"], { cwd: sourceRoot, windowsHide: true });
    commit = rev.stdout.trim();
    const status = await execFileAsync("git", ["status", "--porcelain"], { cwd: sourceRoot, windowsHide: true });
    dirty = status.stdout.trim().length > 0;
  } catch {
    dirty = null;
  }
  return { commit, dirty };
}

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8"));
}

async function buildReleaseMetadata({
  packageRoot,
  metadataRoot,
  sourceRoot = path.resolve(__dirname, ".."),
  packageType = "portable",
  allowDirty = false,
  timestamp = new Date().toISOString(),
} = {}) {
  if (!packageRoot || !metadataRoot) throw metadataError("RELEASE_PATH_REQUIRED", "packageRoot 和 metadataRoot 都是必填项");
  const resolvedPackageRoot = path.resolve(packageRoot);
  const resolvedMetadataRoot = path.resolve(metadataRoot);
  const packageStat = fs.statSync(resolvedPackageRoot);
  if (!packageStat.isDirectory()) throw metadataError("RELEASE_PACKAGE_INVALID", "packageRoot 必须是目录");

  const source = await sourceMetadata(sourceRoot);
  if (source.dirty !== false && !allowDirty) {
    throw metadataError("RELEASE_SOURCE_DIRTY", "源码工作区不是 clean；候选包需显式 --allow-dirty");
  }

  const packageJson = readJson(path.join(sourceRoot, "package.json"));
  const packageLock = readJson(path.join(sourceRoot, "package-lock.json"));
  const cargoLockText = fs.readFileSync(path.join(sourceRoot, "rust-core", "Cargo.lock"), "utf8");
  const cargoPackages = parseCargoLock(cargoLockText);
  const rootCargo = cargoPackages.find(entry => entry.name === "nexus-rust-core");
  const npmPackages = parsePackageLock(packageLock, {
    runtimeDependencies: Object.keys(packageJson.dependencies ?? {}),
  });

  const app = {
    name: packageJson.name,
    version: packageJson.version,
    ecosystem: "npm",
    license: "MIT",
  };
  const components = [
    app,
    ...npmPackages.filter(entry => entry.name !== app.name).map(entry => ({ ...entry, ecosystem: "npm" })),
    ...cargoPackages.map(entry => ({
      ...entry,
      ecosystem: "cargo",
      license: null,
    })),
  ];

  const artifacts = [];
  for (const filePath of walkFiles(resolvedPackageRoot)) {
    const relative = safeRelativePath(path.relative(resolvedPackageRoot, filePath));
    const stat = fs.statSync(filePath);
    artifacts.push({
      path: relative,
      bytes: stat.size,
      sha256: await sha256File(filePath),
    });
  }
  if (artifacts.length === 0) throw metadataError("RELEASE_PACKAGE_EMPTY", "发布包没有任何文件");

  const noticesArtifact = artifacts.find(entry => entry.path === "resources/app/THIRD_PARTY_NOTICES.md")
    ?? artifacts.find(entry => entry.path === "THIRD_PARTY_NOTICES.md");
  if (!noticesArtifact) {
    throw metadataError("RELEASE_NOTICE_MISSING", "发布包缺少 THIRD_PARTY_NOTICES.md");
  }
  const rustCoreArtifact = artifacts.find(entry => entry.path === "resources/bin/nexus-rust-core.exe");
  if (!rustCoreArtifact) {
    throw metadataError("RELEASE_RUST_CORE_MISSING", "发布包缺少 resources/bin/nexus-rust-core.exe");
  }

  const namespaceId = crypto.randomUUID();
  const spdx = createSpdxDocument({ app, components, timestamp, namespaceId });
  fs.mkdirSync(resolvedMetadataRoot, { recursive: true });
  const sbomPath = path.join(resolvedMetadataRoot, "sbom.spdx.json");
  const sbomJson = `${JSON.stringify(spdx, null, 2)}\n`;
  fs.writeFileSync(sbomPath, sbomJson, "utf8");
  const sbomSha256 = crypto.createHash("sha256").update(sbomJson).digest("hex");

  const manifest = {
    schemaVersion: MANIFEST_SCHEMA_VERSION,
    kind: "nexus-release-metadata",
    packageType,
    generatedAt: timestamp,
    qualification: source.commit && source.dirty === false ? "release-candidate" : "candidate",
    source,
    build: {
      applicationName: packageJson.name,
      applicationVersion: packageJson.version,
      productName: packageJson.productName,
      rustCoreVersion: rootCargo?.version ?? null,
      electronVersion: packageJson.devDependencies?.electron ?? null,
    },
    notices: {
      path: noticesArtifact.path,
      sha256: noticesArtifact.sha256,
      bytes: noticesArtifact.bytes,
    },
    sbom: {
      path: path.basename(sbomPath),
      sha256: sbomSha256,
      bytes: Buffer.byteLength(sbomJson, "utf8"),
    },
    artifacts,
    counts: {
      files: artifacts.length,
      components: components.length,
    },
  };
  const manifestPath = path.join(resolvedMetadataRoot, "release-manifest.json");
  fs.writeFileSync(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
  const checksums = artifacts
    .map(entry => `${entry.sha256}  ${entry.path}`)
    .join("\n");
  const checksumPath = path.join(resolvedMetadataRoot, "SHA256SUMS.txt");
  fs.writeFileSync(checksumPath, `${checksums}\n`, "utf8");
  return {
    manifestPath,
    sbomPath,
    checksumPath,
    manifest,
  };
}

async function verifyReleaseMetadata({ packageRoot, metadataRoot }) {
  if (!packageRoot || !metadataRoot) throw metadataError("RELEASE_PATH_REQUIRED", "packageRoot 和 metadataRoot 都是必填项");
  const resolvedPackageRoot = path.resolve(packageRoot);
  const resolvedMetadataRoot = path.resolve(metadataRoot);
  const manifestPath = path.join(resolvedMetadataRoot, "release-manifest.json");
  const manifest = readJson(manifestPath);
  if (manifest.schemaVersion !== MANIFEST_SCHEMA_VERSION) {
    throw metadataError("RELEASE_MANIFEST_VERSION_UNSUPPORTED", `不支持 release manifest 版本 ${manifest.schemaVersion}`);
  }
  const expected = new Map(manifest.artifacts.map(entry => [entry.path, entry]));
  const actualFiles = walkFiles(resolvedPackageRoot).map(filePath => safeRelativePath(path.relative(resolvedPackageRoot, filePath)));
  const actualSet = new Set(actualFiles);
  if (actualSet.size !== expected.size) {
    throw metadataError("RELEASE_FILE_SET_MISMATCH", `文件数不匹配：manifest=${expected.size} actual=${actualSet.size}`);
  }
  for (const relative of actualFiles) {
    if (!expected.has(relative)) throw metadataError("RELEASE_FILE_SET_MISMATCH", `发布包新增文件：${relative}`);
  }
  for (const entry of expected.values()) {
    const filePath = path.join(resolvedPackageRoot, ...entry.path.split("/"));
    const actualHash = await sha256File(filePath);
    if (actualHash !== entry.sha256) throw metadataError("RELEASE_HASH_MISMATCH", `文件哈希不匹配：${entry.path}`);
    const stat = fs.statSync(filePath);
    if (stat.size !== entry.bytes) throw metadataError("RELEASE_SIZE_MISMATCH", `文件大小不匹配：${entry.path}`);
  }
  const sbomJson = fs.readFileSync(path.join(resolvedMetadataRoot, manifest.sbom.path), "utf8");
  const sbomHash = crypto.createHash("sha256").update(sbomJson).digest("hex");
  if (sbomHash !== manifest.sbom.sha256) {
    throw metadataError("RELEASE_SBOM_HASH_MISMATCH", "SBOM 哈希不匹配");
  }
  let sbom;
  try {
    sbom = JSON.parse(sbomJson);
  } catch (error) {
    throw metadataError("RELEASE_SBOM_INVALID", `SBOM JSON 解析失败：${error.message}`);
  }
  if (sbom.spdxVersion !== SPDX_VERSION || !Array.isArray(sbom.packages) || sbom.packages.length === 0) {
    throw metadataError("RELEASE_SBOM_INVALID", "SBOM 格式或组件列表无效");
  }
  const checksumLines = fs.readFileSync(path.join(resolvedMetadataRoot, "SHA256SUMS.txt"), "utf8")
    .split(/\r?\n/)
    .filter(Boolean);
  const expectedLines = manifest.artifacts.map(entry => `${entry.sha256}  ${entry.path}`);
  if (checksumLines.length !== expectedLines.length || checksumLines.some((line, index) => line !== expectedLines[index])) {
    throw metadataError("RELEASE_CHECKSUM_MANIFEST_MISMATCH", "SHA256SUMS.txt 与 release manifest 不一致");
  }
  const requiredPaths = [
    manifest.notices.path,
    "resources/bin/nexus-rust-core.exe",
  ];
  for (const requiredPath of requiredPaths) {
    if (!expected.has(requiredPath)) throw metadataError("RELEASE_REQUIRED_FILE_MISSING", `发布包缺少必需文件：${requiredPath}`);
  }
  if (manifest.qualification === "release-candidate" && (!manifest.source.commit || manifest.source.dirty !== false)) {
    throw metadataError("RELEASE_SOURCE_NOT_CLEAN", "formal release metadata 必须对应 clean Git 提交");
  }
  return {
    verified: true,
    files: expected.size,
    source: manifest.source,
    qualification: manifest.qualification,
  };
}

function parseArgs(argv) {
  const options = {
    packageRoot: path.resolve(process.cwd(), "output", "portable", "Nexus 2.0"),
    metadataRoot: path.resolve(process.cwd(), "output", "release-metadata", "Nexus 2.0"),
    packageType: "portable",
    allowDirty: false,
  };
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--package-root") options.packageRoot = path.resolve(argv[++index]);
    else if (arg === "--metadata-root") options.metadataRoot = path.resolve(argv[++index]);
    else if (arg === "--package-type") options.packageType = String(argv[++index]);
    else if (arg === "--allow-dirty") options.allowDirty = true;
    else throw metadataError("RELEASE_ARG_INVALID", `未知参数：${arg}`);
  }
  return options;
}

async function main() {
  const options = parseArgs(process.argv.slice(2));
  const result = await buildReleaseMetadata(options);
  const verification = await verifyReleaseMetadata(options);
  process.stdout.write(`${JSON.stringify({
    ...verification,
    manifest: result.manifestPath,
    sbom: result.sbomPath,
    checksums: result.checksumPath,
  }, null, 2)}\n`);
}

if (require.main === module) {
  main().catch(error => {
    console.error(error?.stack ?? error);
    process.exitCode = 1;
  });
}

module.exports = {
  MANIFEST_SCHEMA_VERSION,
  SPDX_VERSION,
  buildReleaseMetadata,
  createSpdxDocument,
  npmNameFromLockPath,
  parseCargoLock,
  parsePackageLock,
  safeRelativePath,
  verifyReleaseMetadata,
};
