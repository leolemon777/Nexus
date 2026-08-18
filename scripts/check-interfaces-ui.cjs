const { app, BrowserWindow, ipcMain } = require("electron");
const path = require("path");
const fs = require("fs");
const { RustCoreClient } = require(path.join(process.cwd(), "electron", "rust-core-client.cjs"));
const { resolveRustCoreBinaryPath } = require(path.join(process.cwd(), "electron", "runtime-policy.cjs"));
const { SerialService } = require(path.join(process.cwd(), "electron", "serial-service.cjs"));
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
app.whenReady().then(async () => {
  // 注册与真实 main.cjs 一致的接口体检 handler
  ipcMain.handle("nexus:list_network_interfaces", () => {
    const os = require("node:os");
    const raw = os.networkInterfaces();
    const list = [];
    for (const [name, addrs] of Object.entries(raw)) {
      const v4 = (addrs ?? []).filter((a) => a.family === "IPv4" || a.family === 4);
      const v6 = (addrs ?? []).filter((a) => a.family === "IPv6" || a.family === 6);
      list.push({
        name,
        internal: (addrs ?? [])[0]?.internal ?? false,
        mac: (addrs ?? [])[0]?.mac ?? "",
        ipv4: v4.map((a) => ({ address: a.address, netmask: a.netmask, cidr: a.cidr })),
        ipv6: v6.map((a) => a.address),
      });
    }
    return { ok: true, hostname: os.hostname(), interfaces: list };
  });
  // USB 枚举(与真实 main.cjs 一致,调 PowerShell)
  const { execFile } = require("node:child_process");
  ipcMain.handle("nexus:list_usb_devices", async () => {
    const run = (args) => new Promise((resolve) => {
      execFile("powershell.exe", ["-NoProfile", "-Command", args], { timeout: 15000 },
        (error, stdout) => resolve(error ? null : stdout));
    });
    const out = await run(
      "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB*' } | " +
      "Select-Object FriendlyName, Class, Status, InstanceId | ConvertTo-Json -Compress"
      "Select-Object FriendlyName, Class, Status, InstanceId | ConvertTo-Json -Compress"
    );
    if (out == null) return { ok: false, message: "PowerShell 不可用" };
    let items;
    try { const parsed = JSON.parse(out); items = Array.isArray(parsed) ? parsed : [parsed]; } catch { items = []; }
    return { ok: true, devices: items.map((d) => {
      const m = /VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})/i.exec(d.InstanceId ?? "");
      return { name: d.FriendlyName ?? "(未命名)", class: d.Class ?? "", status: d.Status ?? "",
               vid: m ? m[1].toLowerCase() : null, pid: m ? m[2].toLowerCase() : null };
    }) };
  });
  const serial = new SerialService();
  ipcMain.handle("nexus:list_serial_ports", () => serial.listPorts());
  ipcMain.handle("nexus:get_serial_status", () => serial.getStatus());

  const outdir = path.join(process.cwd(), "screenshots");
  if (!fs.existsSync(outdir)) fs.mkdirSync(outdir, { recursive: true });
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true, nodeIntegration: false, sandbox: true,
      preload: path.join(process.cwd(), "electron", "preload.cjs") } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2000);
  await win.webContents.executeJavaScript("document.querySelector('.nav-item[data-view=\"interfaces\"]').click()");
  await sleep(18000); // USB 枚举走 PowerShell,冷启动需数秒
  const state = await win.webContents.executeJavaScript(`(function(){
    var net = Array.from(document.querySelectorAll('#if-net-rows tr')).map(function(tr){
      return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | ');
    });
    var com = Array.from(document.querySelectorAll('#if-com-rows tr')).map(function(tr){
      return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | ');
    });
    var usb = Array.from(document.querySelectorAll('#if-usb-rows tr')).map(function(tr){
      return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | ');
    });
    var adapterOptions = Array.from(document.querySelectorAll('#if-ip-adapter option')).map(function(o){ return o.textContent; });
    return JSON.stringify({
      netState: (document.querySelector('#if-net-state')||{}).textContent,
      comState: (document.querySelector('#if-com-state')||{}).textContent,
      usbState: (document.querySelector('#if-usb-state')||{}).textContent,
      netRows: net.slice(0, 6),
      comRows: com.slice(0, 4),
      usbRows: usb.slice(0, 12),
      adapterOptions: adapterOptions,
      ipButtons: !!document.querySelector('#if-ip-apply') && !!document.querySelector('#if-ip-dhcp'),
    });
  })()`);
  console.log("IFACES:", state);
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "interfaces-page.png"), img.toPNG());
  console.log("Saved: interfaces-page.png");
  app.quit();
});
app.on("window-all-closed", () => app.quit());
