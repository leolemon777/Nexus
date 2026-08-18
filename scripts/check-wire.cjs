const { app, BrowserWindow } = require("electron");
const path = require("path");
const fs = require("fs");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: false } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);
  const state = await win.webContents.executeJavaScript(`(function(){
    var hints = [...document.querySelectorAll('.wire-hint')].map(function(e){return e.textContent;});
    var mcOpts = [...document.querySelectorAll('#mc-frame-type option')].map(function(o){return o.textContent;});
    var masterTitle = document.querySelector('.nav-item[data-view="master"]').title;
    return JSON.stringify({ transportHints: hints, mcVariants: mcOpts.slice(0,6), masterTitle: masterTitle });
  })()`);
  console.log("WIRE_STATE:", state);
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(process.cwd(), "screenshots", "wire-hints.png"), img.toPNG());
  console.log("Saved");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
