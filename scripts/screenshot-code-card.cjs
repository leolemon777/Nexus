// Headless verification for the sample-code card: switches to TCP, dumps the
// three generated templates, scrolls the card into view and captures PNGs.
// Usage: node scripts/screenshot-code-card.cjs
const { app, BrowserWindow } = require("electron");
const fs = require("fs");
const path = require("path");

app.commandLine.appendSwitch("headless");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: 1440,
    height: 1500,
    show: false,
    webPreferences: { offscreen: true, contextIsolation: true },
  });

  const distPath = path.join(process.cwd(), "dist", "index.html");
  const url = "file://" + distPath.replace(/\\/g, "/");
  console.log("Loading:", url);
  await win.loadURL(url);
  await new Promise((r) => setTimeout(r, 2500));

  const dumps = await win.webContents.executeJavaScript(`(() => {
    const out = {};
    // 默认 FC03 / 地址 0 / 数量 1;切到 TCP 验证网络模板
    document.querySelector('input[name="transport"][value="tcp"]').click();
    out.rust_tcp = document.querySelector("#code-sample").textContent;
    document.querySelector('.code-tab[data-lang="csharp"]').click();
    out.csharp_tcp = document.querySelector("#code-sample").textContent;
    document.querySelector('.code-tab[data-lang="python"]').click();
    out.python_tcp = document.querySelector("#code-sample").textContent;
    // 切回 RTU 串口验证串口模板(仅取 Rust 验证结构)
    document.querySelector('.code-tab[data-lang="rust"]').click();
    document.querySelector('input[name="transport"][value="rtu"]').click();
    out.rust_rtu = document.querySelector("#code-sample").textContent;
    // 回到 TCP + 卡片滚动进可视区用于截图
    document.querySelector('input[name="transport"][value="tcp"]').click();
    const card = document.querySelector(".code-card");
    card.scrollIntoView({ block: "start" });
    return out;
  })()`);

  for (const [key, text] of Object.entries(dumps)) {
    console.log(`\n===== ${key} =====`);
    console.log(text);
  }

  await new Promise((r) => setTimeout(r, 600));
  const rect = await win.webContents.executeJavaScript(`(() => {
    const r = document.querySelector(".code-card").getBoundingClientRect();
    return { x: Math.max(0, Math.floor(r.x)), y: Math.max(0, Math.floor(r.y)),
             width: Math.ceil(r.width), height: Math.ceil(r.height) };
  })()`);
  const img = await win.webContents.capturePage(rect);
  const out1 = path.join(process.cwd(), "screenshot-code-card.png");
  fs.writeFileSync(out1, img.toPNG());
  console.log("\nSaved:", out1, `(${rect.width}x${rect.height})`);

  const full = await win.webContents.capturePage();
  const out2 = path.join(process.cwd(), "screenshot-code-full.png");
  fs.writeFileSync(out2, full.toPNG());
  console.log("Saved:", out2);

  app.quit();
});

app.on("window-all-closed", () => app.quit());
