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
    webPreferences: { offscreen: true, contextIsolation: true, nodeIntegration: false, sandbox: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);
  const state = await win.webContents.executeJavaScript(`(function(){
    var imgs = Array.from(document.querySelectorAll('.nav-item .nav-img')).map(function(img){
      return { src: img.getAttribute('src'), ok: img.complete && img.naturalWidth > 0 };
    });
    var labels = Array.from(document.querySelectorAll('.nav-item .nav-label')).map(function(s){ return s.textContent; });
    var s7Groups = Array.from(document.querySelectorAll('#s7-variant optgroup')).map(function(g){ return g.label; });
    var mcGroups = Array.from(document.querySelectorAll('#mc-frame-type optgroup')).map(function(g){ return g.label; });
    var s7Options = Array.from(document.querySelectorAll('#s7-variant option:not([disabled])')).length;
    var mcOptions = Array.from(document.querySelectorAll('#mc-frame-type option:not([disabled])')).length;
    return JSON.stringify({ imgs: imgs, labels: labels, s7Groups: s7Groups, mcGroups: mcGroups, s7Options: s7Options, mcOptions: mcOptions });
  })()`);
  console.log("NAV_CHECK:", state);
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "nav-brands.png"), img.toPNG());
  // 三菱页截图
  await win.webContents.executeJavaScript("document.querySelector('.nav-item[data-view=\"melsec\"]').click()");
  await sleep(600);
  img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "melsec-dropdown.png"), img.toPNG());
  console.log("Saved: nav-brands.png / melsec-dropdown.png");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
