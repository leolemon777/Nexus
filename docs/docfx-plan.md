# DocFX 文档站计划

> 状态: 计划中（P2）
> 里程碑: M5

## 目标

为 Nexus 建立专业的文档站，让用户能在 5 分钟内完成首次 PLC 读取。

## 方案选择

| 方案 | 优点 | 缺点 |
|------|------|------|
| **DocFX** | 原生 .NET、API 文档自动生成、Markdown 原生支持 | 主题定制有限 |
| **Docusaurus** | React 生态、丰富的插件 | 需要 Node.js |
| **纯 GitHub Pages + Jekyll** | 零配置 | 功能有限 |

**推荐：DocFX** — 与 .NET 生态最契合，可从 XML 注释自动生成 API 文档。

## 文档站结构

```
docs/
├── index.md                    # 首页：项目介绍 + 5 分钟 Quick Start
├── getting-started.md          # 安装、配置、第一个读取
├── ci.md                       # CI 集成指南
├── core/                       # 核心基础设施文档
│   ├── address-context.md
│   ├── connection-pool.md
│   ├── data-acquisition.md     # ← 新：数据采集引擎
│   ├── reconnect-heartbeat.md
│   └── struct-mapping.md
├── protocols/                  # 协议文档
│   ├── modbus/
│   ├── siemens/
│   ├── mitsubishi/
│   ├── omron/
│   └── allenbradley/
└── api/                        # ← DocFX 自动生成
    └── (从 XML 注释生成)
```

## DocFX 配置步骤

### 1. 添加 DocFX 工具

```bash
dotnet tool install -g docfx
```

### 2. 创建 `docfx.json`

```json
{
  "metadata": [{
    "src": [{ "files": ["**/*.csproj"], "src": "../src" }],
    "dest": "api"
  }],
  "build": {
    "content": [{
      "files": ["**/*.md", "**/*.yml"],
      "exclude": ["_site/**", "api/**"]
    }],
    "resource": [{ "files": ["images/**"] }],
    "output": "_site",
    "template": ["default", "modern"]
  }
}
```

### 3. GitHub Actions 自动部署

```yaml
# .github/workflows/docs.yml
name: Docs
on:
  push:
    branches: [main]
    paths: ['docs/**', 'src/**']
jobs:
  docs:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - run: dotnet tool install -g docfx
      - run: docfx docs/docfx.json
      - uses: peaceiris/actions-gh-pages@v3
        with:
          github_token: ${{ secrets.GITHUB_TOKEN }}
          publish_dir: docs/_site
```

## 待创建内容

| 优先级 | 文件 | 说明 |
|--------|------|------|
| P0 | `docfx.json` | DocFX 配置文件 |
| P0 | `getting-started.md` | 5 分钟上手指南 |
| P0 | `toc.yml` | 导航目录 |
| P1 | `core/data-acquisition.md` | 数据采集引擎文档 |
| P1 | 各协议 `toc.yml` | 协议文档导航 |
| P2 | 自定义首页模板 | 项目 Logo + 特性展示 |

## 当前状态

- ✅ 已有 38 个 Markdown 文档（protocols + core）
- ⬜ 需要创建 `docfx.json` + `toc.yml`
- ⬜ 需要添加 XML 文档注释启用 API 文档生成
- ⬜ 需要 GitHub Pages 部署配置
