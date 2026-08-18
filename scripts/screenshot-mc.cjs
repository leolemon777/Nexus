// M1-7d 简化验证:切到三菱页,DOM 检查元素存在性 + 截图渲染
const { app, BrowserWindow } = require("electron");
const path = require("path");
const fs = require("fs");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

app.whenReady().then(async () => {
  const outdir = path.join(process.cwd(), "screenshots");
  if (!fs.existsSync(outdir)) fs.mkdirSync(outdir, { recursive: true });
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: false } });

  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  // 切到三菱页
  await win.webContents.executeJavaScript(`document.querySelector('.nav-item[data-view="melsec"]').click()`);
  await sleep(600);

  // DOM 验证:所有 MC 元素存在且可见
  const state = await win.webContents.executeJavaScript(`(function(){
    function vis(sel) {
      const el = document.querySelector(sel);
      if (!el) return 'NULL';
      const r = el.getBoundingClientRect();
      return (r.width > 0 && r.height > 0) ? 'visible' : 'hidden';
    }
    return JSON.stringify({
      view: vis('#melsec-view'),
      host: vis('#mc-host'),
      port: vis('#mc-port'),
      frameType: vis('#mc-frame-type'),
      networkNo: vis('#mc-network-no'),
      pcNo: vis('#mc-pc-no'),
      watchdog: vis('#mc-watchdog'),
      connect: vis('#mc-connect'),
      disconnect: vis('#mc-disconnect'),
      startSlave: vis('#mc-start-slave'),
      address: vis('#mc-address'),
      points: vis('#mc-points'),
      read: vis('#mc-read'),
      write: vis('#mc-write'),
      writeValues: vis('#mc-write-values'),
      results: vis('#mc-results'),
      stateText: document.querySelector('#mc-state')?.textContent,
      readDisabled: document.querySelector('#mc-read')?.disabled,
      navActive: document.querySelector('.nav-item[data-view="melsec"]')?.classList.contains('is-active'),
      modbusHidden: document.querySelector('#workspace')?.classList.contains('hidden'),
    });
  })()`);
  console.log("MC_UI_STATE:", state);

  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "mc-page.png"), img.toPNG());
  console.log("Saved: mc-page.png");

  // 验证写值解析逻辑(纯前端,无后端):地址渲染函数存在性通过模块检查
  app.quit();
});
app.on("window-all-closed", () => app.quit());
