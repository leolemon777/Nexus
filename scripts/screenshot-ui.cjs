// Headless screenshot: launches Electron, loads the built UI, captures full-page PNG.
// Usage: node scripts/screenshot-ui.cjs [width] [height]
const { app, BrowserWindow } = require("electron");
const fs = require("fs");
const path = require("path");

const W = parseInt(process.argv[2] || "1440", 10);
const H = parseInt(process.argv[3] || "900", 10);
const OUT = process.argv[4] || path.join(process.cwd(), "screenshot.png");

// Run as headless
app.commandLine.appendSwitch("headless");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: W,
    height: H,
    show: false,
    webPreferences: { offscreen: true, contextIsolation: true },
  });

  // Load built dist
  const distPath = path.join(process.cwd(), "dist", "index.html");
  const url = "file://" + distPath.replace(/\\/g, "/");
  console.log("Loading:", url);
  await win.loadURL(url);
  // Wait for JS to render
  await new Promise((r) => setTimeout(r, 2500));

  // Capture the visible area
  const img = await win.webContents.capturePage();
  const buf = img.toPNG();
  fs.writeFileSync(OUT, buf);
  console.log("Saved screenshot:", OUT, `(${buf.length} bytes, ${W}x${H})`);

  // Also capture at a smaller size to test responsive
  await win.setSize(1024, 700);
  await new Promise((r) => setTimeout(r, 1500));
  const img2 = await win.webContents.capturePage();
  const out2 = OUT.replace(".png", "-small.png");
  fs.writeFileSync(out2, img2.toPNG());
  console.log("Saved small screenshot:", out2);

  app.quit();
});

app.on("window-all-closed", () => app.quit());
