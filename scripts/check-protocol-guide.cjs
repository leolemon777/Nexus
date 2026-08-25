const { app, BrowserWindow } = require("electron");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

app.commandLine.appendSwitch("headless");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

const root = path.resolve(__dirname, "..");
const outDir = path.join(root, "screenshots");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function page(win, source) {
  return win.webContents.executeJavaScript(source, true);
}

async function capture(win, name) {
  await sleep(250);
  fs.mkdirSync(outDir, { recursive: true });
  const image = await win.webContents.capturePage();
  fs.writeFileSync(path.join(outDir, name), image.toPNG());
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({
    width: 1440,
    height: 900,
    show: false,
    webPreferences: { offscreen: true, contextIsolation: true },
  });
  await win.loadFile(path.join(root, "dist", "index.html"));
  await sleep(1800);

  try {
    const master = await page(win, `(() => {
      const triggers = [...document.querySelectorAll('[data-protocol-help]')];
      triggers.find((button) => button.dataset.protocolHelp === 'master').click();
      const dialog = document.querySelector('#protocol-guide-dialog');
      const selector = document.querySelector('#protocol-guide-variant');
      return { triggerCount: triggers.length, open: dialog.open, options: selector.options.length, title: document.querySelector('#protocol-guide-title').textContent };
    })()`);
    assert.deepEqual(master, { triggerCount: 6, open: true, options: 6, title: "Modbus 主站 · Modbus RTU 串口" });

    const switched = await page(win, `(() => {
      const selector = document.querySelector('#protocol-guide-variant');
      selector.value = 'tcp';
      selector.dispatchEvent(new Event('change', { bubbles: true }));
      return { title: document.querySelector('#protocol-guide-title').textContent, current: document.querySelector('#protocol-guide-current span').textContent };
    })()`);
    assert.match(switched.title, /Modbus TCP/);
    assert.match(switched.current, /RTU/);
    await capture(win, "protocol-guide-master.png");

    await page(win, `(() => {
      document.querySelector('#protocol-guide-close').click();
      document.querySelector('[data-view="melsec"]').click();
      const variant = document.querySelector('#mc-frame-type');
      variant.value = 'mc-1e';
      variant.dispatchEvent(new Event('change', { bubbles: true }));
      document.querySelector('[data-protocol-help="melsec"]').click();
    })()`);
    const a1e = await page(win, `(() => ({
      open: document.querySelector('#protocol-guide-dialog').open,
      selected: document.querySelector('#protocol-guide-variant').value,
      title: document.querySelector('#protocol-guide-title').textContent,
      warning: document.querySelector('#protocol-guide-warnings').textContent,
    }))()`);
    assert.equal(a1e.open, true);
    assert.equal(a1e.selected, "mc-1e");
    assert.match(a1e.title, /A-1E/);
    assert.match(a1e.warning, /首次连接真实 PLC/);
    await capture(win, "protocol-guide-melsec-a1e.png");

    await win.setSize(900, 700);
    await sleep(350);
    const compact = await page(win, `(() => {
      const dialog = document.querySelector('#protocol-guide-dialog').getBoundingClientRect();
      const shell = document.querySelector('.protocol-guide-shell');
      return { left: dialog.left, right: dialog.right, top: dialog.top, bottom: dialog.bottom, scrollable: shell.scrollHeight > shell.clientHeight };
    })()`);
    assert.ok(compact.left >= 0 && compact.right <= 900, JSON.stringify(compact));
    assert.ok(compact.top >= 0 && compact.bottom <= 700, JSON.stringify(compact));
    assert.equal(compact.scrollable, true);
    await capture(win, "protocol-guide-melsec-a1e-compact.png");

    console.log("PROTOCOL_GUIDE_UI_OK", JSON.stringify({ master, switched, a1e, compact }));
  } finally {
    win.destroy();
    app.quit();
  }
}).catch((error) => {
  console.error("PROTOCOL_GUIDE_UI_FAIL", error);
  app.exit(1);
});

app.on("window-all-closed", () => app.quit());
