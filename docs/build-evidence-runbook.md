# Nexus 2.0 构建证据运行手册

## 目的

`scripts/build-evidence.cjs` 按一个显式 JSON 计划顺序执行构建/测试命令，并归档：

- `build-evidence.json`：命令、退出码、开始/结束时间、耗时、日志大小和 SHA-256；
- `<command-id>.log`：脱敏后的 stdout/stderr、退出码和耗时；
- `BUILD-EVIDENCE.md`：人可读摘要。

生成后立即复核日志大小、哈希、退出状态、统计数量和 Markdown 文件存在性。

## Candidate 运行

当前仓库允许 dirty 源码时生成开发候选证据：

```powershell
npm run evidence:candidate
```

计划文件是 `scripts/build-evidence.candidate.json`，当前包含：

1. Rust JSONL / MQTT / bacstack / knx / R0 soak harness；
2. Electron 与发布工具回归；
3. Vite production build；
4. Electron 桌面冒烟；
5. npm audit。

输出位置：

```text
evidence/build/candidate/
```

当前结果是 `candidate-dirty-source`，不能当 formal 发布证据。

## Formal 运行

```powershell
git status --porcelain
# 必须无输出
npm run build
npm run build:rust-core
npm run package:portable
node scripts/build-evidence.cjs `
  --plan scripts/build-evidence.candidate.json `
  --output evidence/build/formal `
  --manifest output/release-metadata/Nexus-2.0/release-manifest.json
```

Formal 要求：

- 源码 clean；
- release manifest 存在；
- 所有计划命令 exitCode=0；
- `overallOk=true`；
- qualification 为 `candidate-clean-source`。

## 计划格式

只允许三种命令类型，避免自由 shell 拼接：

```json
{
  "schemaVersion": 1,
  "commands": [
    { "id": "rust-jsonl", "kind": "powershell", "script": "scripts/test-rust-jsonl.ps1" },
    { "id": "electron", "kind": "npm", "script": "test:electron" },
    { "id": "custom", "kind": "node", "script": "scripts/example.cjs", "args": ["--flag"] }
  ]
}
```

限制：

- command id 唯一且只能使用安全字符；
- PowerShell/Node 脚本必须是仓库内相对路径，不能 `..` 或绝对路径；
- npm script 名称只能使用安全字符；
- 每条命令超时 1 秒到 3 小时；
- 单个脱敏日志最大 20 MiB；
- 最多 100 条命令。

## 日志脱敏

日志会替换常见敏感键值：

- password / passwd / secret / token
- authorization / cookie / credential / api key
- URL query 中的 token / password / secret / api_key

示例：

```text
password=hunter2      -> password=[REDACTED]
Authorization: Bearer x -> Authorization: [REDACTED]
```

如果命令本身会输出业务数据、设备名或生产拓扑，不应将该命令加入公开证据计划；先调整测试命令或输出。

## 验证

```powershell
node -e "require('./scripts/build-evidence.cjs').verifyBuildEvidence({ outputDirectory: 'evidence/build/candidate' })"
```

校验失败会返回：

- `BUILD_EVIDENCE_HASH_MISMATCH`
- `BUILD_EVIDENCE_SIZE_MISMATCH`
- `BUILD_EVIDENCE_STATUS_MISMATCH`
- `BUILD_EVIDENCE_COUNT_MISMATCH`
- `BUILD_EVIDENCE_MANIFEST_MISMATCH`

## 边界

- 这是构建/测试证据，不是 8 小时长稳；
- 不证明真实设备 L2；
- candidate-dirty-source 不能用于 formal release；
- formal 仍需 release metadata、release notes、回滚计划和发布门禁全部通过。
