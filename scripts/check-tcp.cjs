const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await new Promise(r => setTimeout(r, 2000));
  // 切到 TCP
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await new Promise(r => setTimeout(r, 800));
  const result = await win.webContents.executeJavaScript(`(function(){
    var pane = document.querySelector('.connection-pane');
    var form = document.querySelector('.connection-form');
    var tcp = document.querySelector('.tcp-pane');
    var title = document.querySelector('#connection-title');
    var unitId = document.querySelector('#unit-id');
    return JSON.stringify({
      transport: pane?.dataset.transport,
      title: title?.textContent,
      formDisplay: form ? getComputedStyle(form).display : 'NULL',
      tcpDisplay: tcp ? getComputedStyle(tcp).display : 'NULL',
      unitIdLocation: unitId?.closest('.card')?.querySelector('.card-header strong')?.textContent,
      unitIdValue: unitId?.value,
    });
  })()`);
  console.log(JSON.parse(result));
  app.quit();
});
app.on("window-all-closed", () => app.quit());
