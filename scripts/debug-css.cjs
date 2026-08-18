// Debug: check if CSS is loaded and classes match
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
    var app = document.querySelector('#app');
    var topbar = document.querySelector('.topbar');
    var content = document.querySelector('.content-area');
    var sidebar = document.querySelector('.sidebar');
    var main = document.querySelector('.main-content');
    var packet = document.querySelector('.packet-panel');
    var stylesheets = document.styleSheets.length;
    var firstLink = document.querySelector('link[rel=stylesheet]');
    var cssHref = firstLink ? firstLink.href : 'NONE';
    // Check computed styles
    function cs(sel) {
      var el = document.querySelector(sel);
      if (!el) return 'NULL';
      var s = getComputedStyle(el);
      return s.display + ' | bg:' + s.backgroundColor + ' | w:' + el.offsetWidth;
    }
    return JSON.stringify({
      stylesheets: stylesheets,
      cssHref: cssHref,
      appShell: cs('#app'),
      topbar: cs('.topbar'),
      contentArea: cs('.content-area'),
      sidebar: cs('.sidebar'),
      mainContent: cs('.main-content'),
      packetPanel: cs('.packet-panel'),
      bodyBg: getComputedStyle(document.body).backgroundColor,
      bodyFont: getComputedStyle(document.body).font,
    });
  })()`);
  console.log(JSON.parse(result));
  app.quit();
});
app.on("window-all-closed", () => app.quit());
