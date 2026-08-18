// Quick DOM layout check for new 4-zone layout
const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  const result = await win.webContents.executeJavaScript(`(function() {
    function m(sel) {
      var el = document.querySelector(sel);
      if (!el || el.offsetParent === null) return null;
      var r = el.getBoundingClientRect();
      return {w: Math.round(r.width), h: Math.round(r.height), x: Math.round(r.x), y: Math.round(r.y),
              scrollH: el.scrollHeight, overflow: el.scrollHeight > el.clientHeight + 2 || el.scrollWidth > el.clientWidth + 2};
    }
    return JSON.stringify({
      win: {w: innerWidth, h: innerHeight},
      toolbar: m('.toolbar'),
      actionBar: m('.action-bar'),
      rail: m('.icon-rail'),
      setupPanel: m('.setup-panel'),
      setupBody: m('.setup-body'),
      mainArea: m('.main-area'),
      workspace: m('.master-workspace'),
      registerPanel: m('.register-panel'),
      registerGrid: m('.register-grid'),
      consolePanel: m('.console-panel'),
      statusbar: m('.statusbar'),
      zeroElements: (function(){
        var count = 0;
        document.querySelectorAll('button, input, select, label').forEach(function(el) {
          if (el.offsetParent === null) return;
          var r = el.getBoundingClientRect();
          if (r.width < 3 || r.height < 3) count++;
        });
        return count;
      })(),
    });
  })()`);
  const L = JSON.parse(result);
  console.log("=== NEW LAYOUT @ 1440x900 ===\n");
  for (const [k,v] of Object.entries(L)) {
    if (v === null) { console.log(`  ${k}: NULL`); continue; }
    if (typeof v === 'number') { console.log(`  ${k}: ${v}`); continue; }
    const flag = v.overflow ? " ⚠OVERFLOW" : "";
    console.log(`  ${k}: ${v.w}x${v.h} at (${v.x},${v.y})${flag}`);
  }
  app.quit();
});
app.on("window-all-closed", () => app.quit());
