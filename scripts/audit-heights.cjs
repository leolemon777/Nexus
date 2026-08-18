const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true, sandbox: false } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);
  const out = await win.webContents.executeJavaScript(`(function(){
    function h(sel){ var els = document.querySelectorAll(sel); return Array.from(els).slice(0,3).map(function(el){
      return Math.round(el.getBoundingClientRect().height * 10) / 10; }); }
    return JSON.stringify({
      inputText: h('input.input[type=text]'),
      inputNumber: h('input.input[type=number]'),
      select: h('select.input'),
      label: h('.form-label'),
      btnPrimary: h('.btn-primary'),
      btnGhost: h('.btn-ghost'),
      btnText: h('.btn-text'),
    });
  })()`);
  console.log("HEIGHTS:", out);
  app.quit();
});
app.on("window-all-closed", () => app.quit());
