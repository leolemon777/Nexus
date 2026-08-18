const { app, BrowserWindow, ipcMain } = require("electron");
const path = require("path");
const fs = require("fs");
const { execFile } = require("node:child_process");
const { SerialService } = require(path.join(process.cwd(), "electron", "serial-service.cjs"));
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
app.whenReady().then(async () => {
  ipcMain.handle("nexus:list_network_interfaces", () => {
    const os = require("node:os");
    const raw = os.networkInterfaces();
    const list = [];
    for (const [name, addrs] of Object.entries(raw)) {
      const v4 = (addrs ?? []).filter((a) => a.family === "IPv4" || a.family === 4);
      list.push({
        name,
        internal: (addrs ?? [])[0]?.internal ?? false,
        mac: (addrs ?? [])[0]?.mac ?? "",
        ipv4: v4.map((a) => ({ address: a.address, netmask: a.netmask })),
        ipv6: [],
      });
    }
    return { ok: true, hostname: os.hostname(), interfaces: list };
  });
  ipcMain.handle("nexus:list_usb_devices", async () => {
    const PS_CMD = "Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB*' } | " +
      "Select-Object FriendlyName, Class, Status, InstanceId | ConvertTo-Json -Compress";
    return await new Promise((resolve) => {
      execFile("powershell.exe", ["-NoProfile", "-Command", PS_CMD], { timeout: 20000 },
        (error, stdout) => {
          if (error) { resolve({ ok: false, message: error.message }); return; }
          let items;
          try { const p = JSON.parse(stdout); items = Array.isArray(p) ? p : [p]; } catch { items = []; }
          resolve({ ok: true, devices: items.map((d) => {
            const m = /VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})/i.exec(d.InstanceId ?? "");
            return { name: d.FriendlyName ?? "(未命名)", class: d.Class ?? "", status: d.Status ?? "",
                     vid: m ? m[1].toLowerCase() : null, pid: m ? m[2].toLowerCase() : null };
          }) });
        });
    });
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
  await sleep(15000); // PowerShell 冷启动 + Get-PnpDevice
  const state = await win.webContents.executeJavaScript(`(function(){
    function rows(sel){ return Array.from(document.querySelectorAll(sel + ' tr')).map(function(tr){
      return Array.from(tr.cells).map(function(td){ return td.textContent; }).join(' | '); }); }
    return JSON.stringify({
      usbState: (document.querySelector('#if-usb-state')||{}).textContent,
      usbRows: rows('#if-usb-rows').slice(0, 12),
      adapterOptions: Array.from(document.querySelectorAll('#if-ip-adapter option')).map(function(o){ return o.textContent; }),
      ipButtons: !!document.querySelector('#if-ip-apply') && !!document.querySelector('#if-ip-dhcp'),
    });
  })()`);
  console.log("USB_CHECK:", state);
  var img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outdir, "interfaces-usb-final.png"), img.toPNG());
  app.quit();
});
app.on("window-all-closed", () => app.quit());
