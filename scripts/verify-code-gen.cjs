// Headless edge-case checks for the sample-code generator (write FCs, UDP, ascii-over-tcp, serial unit id).
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("headless");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: 1440,
    height: 1200,
    show: false,
    webPreferences: { offscreen: true, contextIsolation: true },
  });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await new Promise((r) => setTimeout(r, 2500));

  const out = await win.webContents.executeJavaScript(`(() => {
    const set = (sel, v) => { const el = document.querySelector(sel); el.value = v; el.dispatchEvent(new Event("input", { bubbles: true })); el.dispatchEvent(new Event("change", { bubbles: true })); };
    const fc = (v) => set("#function-code", v);
    const tab = (lang) => document.querySelector('.code-tab[data-lang="' + lang + '"]').click();
    const radio = (v) => document.querySelector('input[name="transport"][value="' + v + '"]').click();
    const code = () => document.querySelector("#code-sample").textContent;
    const out = {};

    // 1) FC16 TCP + 自定义写入值
    radio("tcp"); set("#tcp-host", "192.168.1.10"); set("#tcp-port", "5020"); set("#tcp-unit-id", "7");
    set("#start-address", "100"); fc("16"); set("#write-value", "10,20,30");
    out.csharp_fc16 = code();
    tab("python"); out.python_fc16 = code();
    tab("rust"); out.rust_fc16 = code();

    // 2) FC05 空写入值 → 占位 true
    fc("5"); set("#write-value", "");
    out.rust_fc05 = code();

    // 3) UDP
    radio("udp"); fc("3");
    out.rust_udp_head = code().split("\\n").slice(0, 12).join("\\n");

    // 4) ascii-over-tcp + C#
    radio("ascii-over-tcp"); tab("csharp");
    out.csharp_ascii_over_tcp_head = code().split("\\n").slice(0, 10).join("\\n");

    // 5) RTU 串口站号联动 #unit-id-nav
    radio("rtu"); set("#unit-id-nav", "42"); fc("6"); set("#write-value", "1234");
    tab("python"); out.python_rtu_fc06 = code();

    return out;
  })()`);

  for (const [k, v] of Object.entries(out)) {
    console.log(`\n===== ${k} =====\n${v}`);
  }
  app.quit();
});
app.on("window-all-closed", () => app.quit());
