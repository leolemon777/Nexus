const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: false } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);
  const state = await win.webContents.executeJavaScript(`(function(){
    var sel = document.querySelector('#interface-type');
    if (!sel) return 'NULL';
    var rts = document.querySelector('#rts-mode');
    var hint = document.querySelector('#port-hint');
    var before = rts.value;
    sel.value = 'rs485';
    sel.dispatchEvent(new Event('change'));
    var after485 = rts.value;
    var hint485 = hint.textContent;
    sel.value = 'rs232';
    sel.dispatchEvent(new Event('change'));
    var back232 = rts.value;
    return JSON.stringify({ before: before, after485: after485, hint485: hint485, back232: back232 });
  })()`);
  console.log("IFACE_STATE:", state);
  app.quit();
});
app.on("window-all-closed", () => app.quit());
