// Multi-view screenshot: captures all 4 views + transport modes.
// Robust version: each click is fire-and-forget (no await on Promise).
// Usage: npx electron scripts/screenshot-views.cjs
const { app, BrowserWindow } = require("electron");
const fs = require("fs");
const path = require("path");

const W = 1440, H = 900;
const OUTDIR = path.join(process.cwd(), "screenshots");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function shot(win, name) {
  await sleep(1500);
  const img = await win.webContents.capturePage();
  fs.writeFileSync(path.join(OUTDIR, name + ".png"), img.toPNG());
  console.log("Saved:", name);
}

// fire-and-forget click — don't await the returned value (renderer may return a Promise)
async function clickInPage(win, jsExpr) {
  await win.webContents.executeJavaScript(jsExpr, true);
  await sleep(800);
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
    // 1. Master view - serial RTU mode (default)
    await shot(win, "01-master-rtu");

    // 2. Master view - TCP mode
    await clickInPage(win, `(function(){var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
    await shot(win, "02-master-tcp");

    // back to rtu
    await clickInPage(win, `(function(){var r=document.querySelector('input[name="transport"][value="rtu"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);

    // 3. Slave view
    await clickInPage(win, `(function(){var b=document.querySelector('button[data-view="slave"]');if(b)b.click();})()`);
    await shot(win, "03-slave");

    // 4. Debug view
    await clickInPage(win, `(function(){var b=document.querySelector('button[data-view="debug"]');if(b)b.click();})()`);
    await shot(win, "04-debug");

    // 5. Parser view
    await clickInPage(win, `(function(){var b=document.querySelector('button[data-view="parser"]');if(b)b.click();})()`);
    await shot(win, "05-parser");

    // 6. Back to master
    await clickInPage(win, `(function(){var b=document.querySelector('button[data-view="master"]');if(b)b.click();})()`);
    await shot(win, "06-master-back");

    // 7. Smaller window test
    await win.setSize(1024, 700);
    await sleep(800);
    await shot(win, "07-master-1024x700");

    // 8. Very small window
    await win.setSize(800, 600);
    await sleep(800);
    await shot(win, "08-master-800x600");
  } catch (e) {
    console.error("Error during capture:", e.message);
  }

  app.quit();
});

app.on("window-all-closed", () => app.quit());
