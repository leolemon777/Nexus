# Nexus 2.0 发布说明与回滚运行手册

## 1. 前置条件

formal rollout 只允许 clean Git 工作区：

```powershell
git status --porcelain
```

输出必须为空。候选包可使用 `--allow-dirty`，但 qualification 会是 `candidate`，不能当 formal。

## 2. 生成发布说明归档

先按 [release-notes-template.md](./release-notes-template.md) 编写本版说明，再执行：

```powershell
node scripts/release-notes.cjs archive `
  --notes docs/release-notes/<VERSION>.md `
  --manifest output/release-metadata/<PORTABLE-SLUG>/release-manifest.json `
  --output output/release-metadata/<PORTABLE-SLUG>/release-notes.json
```

校验规则：

- 标题必须是 `# Nexus 2.0 <VERSION>`；
- `Release-Commit` 必须与 release manifest 一致；
- manifest 必须是 `release-candidate` 且 `source.dirty=false`；
- “新增 / 修复 / 限制 / 未验证项 / 回滚”五个章节各出现一次；
- 必须明确“未完成 8 小时长稳”和“真实设备 L2 未完成”；
- 回滚章节必须包含回滚包路径和验证命令。

验证：

```powershell
node scripts/release-notes.cjs verify `
  --archive output/release-metadata/<PORTABLE-SLUG>/release-notes.json `
  --notes docs/release-notes/<VERSION>.md `
  --manifest output/release-metadata/<PORTABLE-SLUG>/release-manifest.json
```

## 3. 生成回滚计划

准备当前包和上一版包各自的 release metadata，然后执行：

```powershell
node scripts/release-rollback.cjs create `
  --current-package output/portable/<CURRENT> `
  --current-metadata output/release-metadata/<CURRENT> `
  --previous-package output/portable/<PREVIOUS> `
  --previous-metadata output/release-metadata/<PREVIOUS> `
  --output output/release-metadata/<CURRENT>/rollback-plan.json `
  --reason "post-release rollback"
```

脚本会：

- 重新校验当前包文件集合、大小、SHA-256、SBOM 和 NOTICE；
- 重新校验上一版包；
- 要求双方均为 clean release-candidate；
- 要求源提交和版本号不同；
- 生成不覆盖旧目录的回滚步骤。

验证：

```powershell
node scripts/release-rollback.cjs verify `
  --plan output/release-metadata/<CURRENT>/rollback-plan.json
```

## 4. 正式目录切换原则

- 不直接在 previous 包目录上原地覆盖；
- 不强杀无关进程；
- 文件被占用时记录占用进程并停止；
- 替换前保留当前 formal 目录；
- 替换后必须运行 `smoke:portable --folder <FORMAL_FOLDER>`；
- 冒烟失败时恢复替换前目录并再次冒烟；
- 保留当前包、metadata、notes、rollback plan 和失败输出作为证据。

## 5. 当前边界

- 本手册提供发布说明和回滚配对验证，不自动执行目录替换；
- 尚无新的 formal clean-source 便携包；
- 旧 `output/portable` 历史目录没有 release metadata，不能直接作为回滚包；
- installer、签名、正式回滚演练和 8 小时长稳仍 pending。
- 构建日志与测试摘要使用 `docs/build-evidence-runbook.md` 的独立证据链。
