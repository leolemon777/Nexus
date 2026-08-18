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
  await win.webContents.executeJavaScript("document.querySelector('.nav-item[data-view=\"melsec\"]').click()");
  await sleep(400);
  // 切到 FX Computer Link
  const state = await win.webContents.executeJavaScript(`(function(){
    var sel = document.querySelector('#mc-frame-type');
    sel.value = 'fx-links';
    sel.dispatchEvent(new Event('change'));
    return new Promise(function(resolve){
      setTimeout(function(){
        resolve(JSON.stringify({
          netRowHidden: document.querySelector('#mc-net-row').classList.contains('hidden'),
          serialRowVisible: !document.querySelector('#mc-serial-row').classList.contains('hidden'),
          fxStationVisible: !!document.querySelector('#mc-fx-station'),
        }));
      }, 200);
    });
  })()`);
  console.log("FX_UI_STATE:", state);
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(process.cwd(), "screenshots", "mc-fx-serial.png"), img.toPNG());
  console.log("Saved: mc-fx-serial.png");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
