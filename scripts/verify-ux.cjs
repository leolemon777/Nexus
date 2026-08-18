// UX upgrade verification: collapse states + remaining views + popover.
// Usage: npm run build && npx electron scripts/verify-ux.cjs
const { app, BrowserWindow } = require("electron");
const fs = require("fs");
const path = require("path");

const W = 1440, H = 900;
const OUTDIR = path.join(process.cwd(), "screenshots");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function shot(win, name) {
  await sleep(1200);
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(OUTDIR, name + ".png"), img.toPNG());
  console.log("Saved:", name);
}

async function evalInPage(win, jsExpr) {
  await win.webContents.executeJavaScript(jsExpr, true);
  await sleep(700);
}

app.whenReady().then(async () => {
  if (!fs.existsSync(OUTDIR)) fs.mkdirSync(OUTDIR, { recursive: true });
  const win = new BrowserWindow({
    width: W, height: H, show: false,
    webPreferences: { offscreen: true, contextIsolation: true },
  });
  const distPath = path.join(process.cwd(), "dist", "index.html");
  await win.loadURL("file://" + distPath.replace(/\\/g, "/"));
  await sleep(2500);

  try {
    // clear persisted layout state from previous runs
    await evalInPage(win, `localStorage.removeItem("nexus-layout-v1");`);

    // 1. sidebar collapsed
    await evalInPage(win, `document.querySelector("#toggle-sidebar")?.click()`);
    await shot(win, "ux-01-sidebar-collapsed");
    // restore
    await evalInPage(win, `document.querySelector("#toggle-sidebar")?.click()`);

    // 2. packet panel collapsed (via topbar button)
    await evalInPage(win, `document.querySelector("#toggle-packet-panel")?.click()`);
    await shot(win, "ux-02-packet-collapsed");
    // restore
    await evalInPage(win, `document.querySelector("#toggle-packet-panel")?.click()`);

    // 3. both collapsed
    await evalInPage(win, `document.querySelector("#toggle-sidebar")?.click(); document.querySelector("#toggle-packet-panel")?.click()`);
    await shot(win, "ux-03-both-collapsed");
    await evalInPage(win, `document.querySelector("#toggle-sidebar")?.click(); document.querySelector("#toggle-packet-panel")?.click()`);

    // 4. melsec view
    await evalInPage(win, `document.querySelector('button[data-view="melsec"]')?.click()`);
    await shot(win, "ux-04-melsec");

    // 5. siemens view
    await evalInPage(win, `document.querySelector('button[data-view="siemens"]')?.click()`);
    await shot(win, "ux-05-siemens");

    // 6. omron view
    await evalInPage(win, `document.querySelector('button[data-view="omron"]')?.click()`);
    await shot(win, "ux-06-omron");

    // 7. interfaces view
    await evalInPage(win, `document.querySelector('button[data-view="interfaces"]')?.click()`);
    await shot(win, "ux-07-interfaces");

    // 8. back to master + open advanced popover
    await evalInPage(win, `document.querySelector('button[data-view="master"]')?.click()`);
    await evalInPage(win, `var d=document.querySelector(".serial-advanced"); if(d) d.open = true;`);
    await shot(win, "ux-08-advanced-popover");

    // 9. narrow 1024x700 (auto-collapse breakpoint)
    win.setSize(1024, 700);
    await shot(win, "ux-09-1024");
  } catch (err) {
    console.error("VERIFY_FAILED", err);
  } finally {
    await sleep(300);
    app.exit(0);
  }
});
