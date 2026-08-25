# Nexus 2.0 发布元数据运行手册

## 目的

`scripts/generate-release-metadata.cjs` 为便携包生成三类相互校验的发布证据：

1. `release-manifest.json`：源提交、工作区状态、产品/Rust/Electron 版本、NOTICE 哈希、SBOM 哈希和全包文件 SHA-256/大小。
2. `sbom.spdx.json`：SPDX 2.3 格式 SBOM，包含 npm 生产闭包、Electron 运行时和 Rust Cargo 锁文件组件。
3. `SHA256SUMS.txt`：按相对路径排序的全包 SHA-256 清单。

生成后立即重新读取并校验文件集合、大小、哈希和 SBOM 哈希；任何不匹配都会失败。

## Formal 与 Candidate

### Formal

```powershell
npm run build
npm run build:rust-core
npm run package:portable
```

Formal 打包要求源码 Git 工作区 clean。生成器会记录当前提交，`qualification=release-candidate`。如果工作区 dirty，打包在删除旧输出目录并复制新内容之后、生成元数据阶段失败；此时不生成可用元数据，不得宣称正式发布。

### Candidate

```powershell
npm run package:portable:candidate
```

该模式向元数据生成器传 `--allow-dirty`，输出 `qualification=candidate`，用于开发验证和回滚候选。dirty 状态会写入 manifest；candidate 不得当作 formal release。

## 输出位置

以默认便携目录 `Nexus 2.0` 为例：

```text
output/portable/Nexus 2.0/
output/release-metadata/Nexus-2.0/release-manifest.json
output/release-metadata/Nexus-2.0/sbom.spdx.json
output/release-metadata/Nexus-2.0/SHA256SUMS.txt
```

使用 `-PortableFolderName` 时，元数据目录 slug 会把非 `A-Z a-z 0-9 . _ -` 字符替换为 `-`。

## 单独生成与校验

```powershell
# formal 要求源码 clean
npm run release:metadata

# 开发候选
npm run release:metadata:candidate
```

脚本会自动执行生成与校验。输出 `verified: true`、文件数、源码状态和 qualification 后才算通过。

## 判定

- `source.commit` 必须存在；
- formal 打包时 `source.dirty=false`；
- candidate 允许 dirty，但 qualification 必须是 `candidate`；
- `notices.sha256` 必须对应包内 `THIRD_PARTY_NOTICES.md`；
- `sbom.sha256` 必须对应生成的 SPDX 文件；
- manifest 文件数、路径、大小、SHA-256 必须与实际包完全一致；
- `SHA256SUMS.txt` 中的每行必须对应 manifest；
- `counts.files` 必须等于实际文件数。

## 范围与限制

- 已接入便携包打包脚本；installer 尚未接入。
- SBOM 来自 `package-lock.json` 与 `rust-core/Cargo.lock`，npm 部分包含生产闭包、显式运行时依赖和 Electron；dev-only 测试栈不进入 SBOM。
- Rust Cargo lock 不携带许可证字段，SBOM 中许可证为 `NOASSERTION`；应用级第三方说明仍以 `THIRD_PARTY_NOTICES.md` 为准。
- 尚未签名，尚未生成安装版，尚未执行 formal clean-source 打包和正式 8/24/72 小时长稳。
- 旧 `output/portable` 目录是历史包；本工具接入后必须重新打包才会生成对应元数据。
- 发布说明与回滚配对流程见 `docs/release-rollout-runbook.md`。
- 构建日志与测试摘要见 `docs/build-evidence-runbook.md`。
