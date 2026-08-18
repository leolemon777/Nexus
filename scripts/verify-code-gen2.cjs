const { app, BrowserWindow } = require("electron");
const path = require("path");
app.commandLine.appendSwitch("headless");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 1200, show: false, webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").split(path.sep).join("/"));
  await new Promise((r) => setTimeout(r, 2500));
  const out = await win.webContents.executeJavaScript(`(() => {
    const set = (sel, v) => { const el = document.querySelector(sel); el.value = v; el.dispatchEvent(new Event("input", { bubbles: true })); el.dispatchEvent(new Event("change", { bubbles: true })); };
    const tab = (lang) => document.querySelector('.code-tab[data-lang="' + lang + '"]').click();
    const radio = (v) => document.querySelector('input[name="transport"][value="' + v + '"]').click();
    const code = () => document.querySelector("#code-sample").textContent;
    const out = {};
    radio("tcp"); set("#start-address", "100"); set("#function-code", "16"); set("#write-value", "10,20,30");
    tab("csharp"); out.csharp_fc16 = code();
    set("#function-code", "15"); set("#write-value", "1,0,1"); tab("python"); out.python_fc15 = code();
    radio("rtu-over-tcp"); tab("csharp"); set("#function-code", "1"); set("#quantity", "8");
    out.csharp_rtu_over_tcp_fc01 = code();
    // 1 基地址: 输入 40100 → 协议地址 99
    set("#address-base", "1"); set("#start-address", "40100"); set("#function-code", "3"); tab("rust");
    out.rust_1based = code().split("\\n").filter(l => l.includes("起始地址") || l.includes("build_read")).join("\\n");
    return out;
  })()`);
  for (const [k, v] of Object.entries(out)) console.log(`\n===== ${k} =====\n${v}`);
  app.quit();
});
app.on("window-all-closed", () => app.quit());
