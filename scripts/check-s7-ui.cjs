const { app, BrowserWindow, ipcMain } = require("electron");
const path = require("path");
const fs = require("fs");
const { RustCoreClient } = require(path.join(process.cwd(), "electron", "rust-core-client.cjs"));
const { resolveRustCoreBinaryPath } = require(path.join(process.cwd(), "electron", "runtime-policy.cjs"));
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
// 最小 IPC 环境:注册 S7 在线流程所需 handler(与 main.cjs 语义一致)
let coreClient = null;
async function ensureCore() {
  if (!coreClient) {
    const binaryPath = resolveRustCoreBinaryPath({
      isPackaged: false,
      resourcesPath: process.resourcesPath,
      projectRoot: process.cwd(),
      envPath: process.env.NEXUS_RUST_CORE_PATH,
    });
    coreClient = new RustCoreClient({ binaryPath });
    await coreClient.start();
  }
  return coreClient;
}
app.whenReady().then(async () => {
  for (const cmd of ["s7_parse_address", "open_s7_connection", "s7_read", "s7_write",
                     "start_s7_slave", "stop_s7_slave", "close_connection", "get_backend_status"]) {
    ipcMain.handle(`nexus:${cmd}`, async (_e, args) => {
      const core = await ensureCore();
      return core.request(cmd, args ?? {});
    });
  }
  const outdir = path.join(process.cwd(), "screenshots");
  if (!fs.existsSync(outdir)) fs.mkdirSync(outdir, { recursive: true });
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true, nodeIntegration: false, sandbox: true,
      preload: path.join(process.cwd(), "electron", "preload.cjs") } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);

  // 1) 视图切换与元素可见性
  await win.webContents.executeJavaScript("document.querySelector('.nav-item[data-view=\"siemens\"]').click()");
  await sleep(500);
  const state = await win.webContents.executeJavaScript(`(function(){
    function vis(sel) { var el = document.querySelector(sel); if (!el) return 'NULL'; var r = el.getBoundingClientRect(); return (r.width > 0 && r.height > 0) ? 'visible' : 'hidden'; }
    var ids = ['#s7-variant','#s7-model','#s7-host','#s7-port','#s7-rack','#s7-slot','#s7-connect',
               '#s7-disconnect','#s7-start-slave','#s7-read','#s7-write','#s7-address','#s7-points',
               '#s7-results','#s7-checklist','#s7-checklist-body'];
    var out = {};
    ids.forEach(function(s){ out[s] = vis(s); });
    out.readDisabled = (document.querySelector('#s7-read')||{}).disabled;
    out.hint = (document.querySelector('#s7-hint')||{}).textContent;
    out.slotDefault = (document.querySelector('#s7-slot')||{}).value;
    return JSON.stringify(out);
  })()`);
  console.log("S7_UI_STATE:", state);

  // 2) 型号联动:切 S7-300 → slot 应为 2
  await win.webContents.executeJavaScript(`(async function(){
    document.querySelector('#s7-model').value = '300';
    document.querySelector('#s7-model').dispatchEvent(new Event('change'));
    return document.querySelector('#s7-slot').value;
  })()`).then(v => console.log("MODEL_LINK_SLOT:", v));

  // 3) 检查清单展开(SMART)
  await win.webContents.executeJavaScript(`(async function(){
    document.querySelector('#s7-variant').value = 'smart';
    document.querySelector('#s7-variant').dispatchEvent(new Event('change'));
    document.querySelector('#s7-checklist').click();
    var t = document.querySelector('#s7-checklist-content').textContent;
    return { hasV: t.includes('VW100'), slot: document.querySelector('#s7-slot').value };
  })()`).then(r => console.log("SMART_CHECKLIST:", JSON.stringify(r)));
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "s7-page-smart.png"), img.toPNG());

  // 4) 全流程实测(虚拟 CPU → 连接 → 读 → 写 → 读回),切回 1200 模型
  const flow = await win.webContents.executeJavaScript(`(async function(){
    function sleep(ms){ return new Promise(r=>setTimeout(r,ms)); }
    try {
      document.querySelector('#s7-variant').value = 's7comm';
      document.querySelector('#s7-variant').dispatchEvent(new Event('change'));
      document.querySelector('#s7-port').value = '11702';
      document.querySelector('#s7-start-slave').click();
      await sleep(600);
      document.querySelector('#s7-connect').click();
      await sleep(1200);
      var state = document.querySelector('#s7-state').textContent;
      document.querySelector('#s7-address').value = 'DB1.DBB0';
      document.querySelector('#s7-points').value = '4';
      document.querySelector('#s7-read').click();
      await sleep(900);
      var rows = Array.from(document.querySelectorAll('#s7-results tr')).map(function(tr){
        return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | ');
      });
      document.querySelector('#s7-write-values').value = 'hex: CA FE BA BE';
      document.querySelector('#s7-write').click();
      await sleep(700);
      document.querySelector('#s7-read').click();
      await sleep(700);
      var rows2 = Array.from(document.querySelectorAll('#s7-results tr')).map(function(tr){
        return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | ');
      });
      // SMART V 语法
      document.querySelector('#s7-address').value = 'VW100';
      document.querySelector('#s7-points').value = '1';
      document.querySelector('#s7-read').click();
      await sleep(700);
      var smartRow = (document.querySelector('#s7-results tr td:nth-child(4)')||{}).textContent;
      return { state: state, read1: rows, afterWrite: rows2, smartVw100: smartRow };
    } catch (e) { return { error: String(e) }; }
  })()`);
  console.log("S7_FLOW:", JSON.stringify(flow, null, 1));
  var img2 = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "s7-page-flow.png"), img2.toPNG());
  console.log("Saved: s7-page-smart.png / s7-page-flow.png");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
