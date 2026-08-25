# Nexus 2.0 [VERSION]

Release-Commit: `[40-character-git-commit]`

## 新增

- 列出本版新增能力和协议边界。
- 每个新增项必须能追溯到 release manifest、测试或文档证据。

## 修复

- 列出本版修复的问题。
- 写明修复验证方式，不使用“已优化”等不可验证描述。

## 限制

- 列出尚未支持的功能、协议范围和运行环境。
- 明确 portable/installer、实机 L2、长稳、签名等边界。

## 未验证项

- 未完成 8 小时长稳。
- 真实设备 L2 未完成。
- 列出本版仍需外部硬件、网络或现场条件的项目。

## 回滚

回滚包：`[previous-portable-or-archive-path]`
回滚验证：`node scripts/release-rollback.cjs verify --plan [rollback-plan.json]`

1. 确认当前 Nexus 进程已退出。
2. 如有文件锁，记录占用进程并请求用户关闭。
3. 保留当前失败包和元数据。
4. 按 rollback plan 恢复上一版。
5. 运行便携包冒烟并记录结果。
