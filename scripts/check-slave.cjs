// Quick check: does slave view still show connection-pane?
// Read the rendered HTML to verify .connection-pane has .hidden class
const { app, BrowserWindow } = require("electron");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });

  await win.loadURL("file://" + require("path").join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await new Promise(r => setTimeout(r, 2000));

  // Click slave view
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="slave"]');if(b)b.click();})()`);
  await new Promise(r => setTimeout(r, 800));

  // Check visibility of connection-pane and slave-view
  const result = await win.webContents.executeJavaScript(`JSON.stringify({
    connectionPaneHidden: document.querySelector('.connection-pane')?.classList.contains('hidden'),
    slaveViewHidden: document.querySelector('#slave-view')?.classList.contains('hidden'),
    masterViewHidden: document.querySelector('#workspace')?.classList.contains('hidden'),
    slaveViewRect: document.querySelector('#slave-view')?.getBoundingClientRect(),
    connectionPaneRect: document.querySelector('.connection-pane')?.getBoundingClientRect(),
  })`);

  console.log("DOM state:", result);
  app.quit();
});
app.on("window-all-closed", () => app.quit());
