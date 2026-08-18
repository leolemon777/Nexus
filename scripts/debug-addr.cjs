const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await new Promise(r => setTimeout(r, 2000));
  const result = await win.webContents.executeJavaScript(`(function(){
    var sel = document.querySelector('#address-base');
    if (!sel) return 'NOT FOUND';
    var cs = getComputedStyle(sel);
    var r = sel.getBoundingClientRect();
    var parent = sel.parentElement;
    var pcs = getComputedStyle(parent);
    return JSON.stringify({
      selW: r.width, selH: r.height,
      display: cs.display, flexBasis: cs.flexBasis, flexGrow: cs.flexGrow, flexShrink: cs.flexShrink,
      width: cs.width, minWidth: cs.minWidth, maxWidth: cs.maxWidth,
      parentCls: parent.className,
      parentDisplay: pcs.display, parentFlexDirection: pcs.flexDirection,
      htmlStyle: sel.getAttribute('style'),
    });
  })()`);
  console.log(JSON.parse(result));
  app.quit();
});
app.on("window-all-closed", () => app.quit());
