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
  await sleep(2000);
  await win.webContents.executeJavaScript("document.querySelector('.nav-item[data-view=\"melsec\"]').click()");
  await sleep(500);
  const state = await win.webContents.executeJavaScript(`(function(){
    function vis(sel) { var el = document.querySelector(sel); if (!el) return 'NULL'; var r = el.getBoundingClientRect(); return (r.width > 0 && r.height > 0) ? 'visible' : 'hidden'; }
    var ids = ['#mc-read-type','#mc-read-status','#mc-read-clock','#mc-echo','#mc-random-read','#mc-random-addrs','#mc-remote-run','#mc-remote-stop','#mc-remote-reset','#mc-diag-result'];
    var out = {};
    ids.forEach(function(s){ out[s] = vis(s); });
    out.allDisabled = ['#mc-read-type','#mc-remote-run','#mc-echo'].every(function(s){ var el = document.querySelector(s); return el && el.disabled === true; });
    out.diagState = (document.querySelector('#mc-diag-state')||{}).textContent;
    return JSON.stringify(out);
  })()`);
  console.log("M2_UI_STATE:", state);
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "mc-m2-page.png"), img.toPNG());
  console.log("Saved: mc-m2-page.png");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
