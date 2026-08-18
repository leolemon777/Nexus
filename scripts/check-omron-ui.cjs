// 欧姆龙页 UI 检查:元素可见 + 在线全流程(虚拟 PLC → 连接 → 读写)
const { app, BrowserWindow, ipcMain } = require("electron");
const path = require("path");
const fs = require("fs");
const { RustCoreClient } = require(path.join(process.cwd(), "electron", "rust-core-client.cjs"));
const { resolveRustCoreBinaryPath } = require(path.join(process.cwd(), "electron", "runtime-policy.cjs"));
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
let core = null;
async function ensureCore() {
  if (!core) {
    core = new RustCoreClient({ binaryPath: resolveRustCoreBinaryPath({
      isPackaged: false, resourcesPath: process.resourcesPath,
      projectRoot: process.cwd(), envPath: process.env.NEXUS_RUST_CORE_PATH }) });
    await core.start();
  }
  return core;
}
app.whenReady().then(async () => {
  for (const cmd of ["open_fins_tcp", "open_fins_udp", "fins_read", "fins_write",
                     "start_fins_slave", "stop_fins_slave", "close_connection"]) {
    ipcMain.handle(`nexus:${cmd}`, async (_e, args) => (await ensureCore()).request(cmd, args ?? {}));
  }
  const outdir = path.join(process.cwd(), "screenshots");
  if (!fs.existsSync(outdir)) fs.mkdirSync(outdir, { recursive: true });
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true, nodeIntegration: false, sandbox: true,
      preload: path.join(process.cwd(), "electron", "preload.cjs") } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);
  // 导航可见性
  const nav = await win.webContents.executeJavaScript(`(function(){
    document.querySelector('.nav-item[data-view="omron"]').click();
    var vis = ['#om-transport','#om-host','#om-port','#om-dest','#om-src','#om-connect',
               '#om-start-slave','#om-address','#om-points','#om-read','#om-write','#om-results']
      .every(function(s){ var el=document.querySelector(s); if(!el) return false;
        var r=el.getBoundingClientRect(); return r.width>0 && r.height>0; });
    return JSON.stringify({ allVisible: vis, readDisabled: document.querySelector('#om-read').disabled });
  })()`);
  console.log("OM_NAV:", nav);
  await sleep(400);
  // 在线全流程(走真实 preload 白名单)
  const flow = await win.webContents.executeJavaScript(`(async function(){
    function sleep(ms){ return new Promise(r=>setTimeout(r,ms)); }
    try {
      document.querySelector('#om-port').value = '19300';
      document.querySelector('#om-start-slave').click();
      await sleep(700);
      document.querySelector('#om-connect').click();
      await sleep(1200);
      var state = document.querySelector('#om-state').textContent;
      document.querySelector('#om-address').value = 'D100';
      document.querySelector('#om-points').value = '2';
      document.querySelector('#om-read').click();
      await sleep(800);
      var rows = Array.from(document.querySelectorAll('#om-results tr')).map(function(tr){
        return Array.from(tr.cells).map(function(td){ return td.textContent; }).join('|');
      });
      // UDP 模式
      document.querySelector('#om-transport').value = 'udp';
      document.querySelector('#om-transport').dispatchEvent(new Event('change'));
      document.querySelector('#om-disconnect').click(); await sleep(300);
      document.querySelector('#om-connect').click(); await sleep(1000);
      var udpState = document.querySelector('#om-state').textContent;
      document.querySelector('#om-read').click(); await sleep(800);
      var udpRows = Array.from(document.querySelectorAll('#om-results tr')).map(function(tr){
        return Array.from(tr.cells).map(function(td){ return td.textContent; }).join('|');
      });
      return { tcpState: state, tcpRows: rows.slice(0,3), udpState: udpState, udpRows: udpRows.slice(0,3) };
    } catch (e) { return { error: String(e) }; }
  })()`);
  console.log("OM_FLOW:", JSON.stringify(flow));
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "omron-page.png"), img.toPNG());
  app.quit();
});
app.on("window-all-closed", () => app.quit());
