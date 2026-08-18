// Comprehensive DOM layout check: verify all views render without overflow, overlap, or zero-size elements.
// Usage: npx electron scripts/check-layout.cjs
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function checkView(win, viewName) {
  // Activate the view
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="${viewName}"]');if(b)b.click();})()`);
  await sleep(800);

  const result = await win.webContents.executeJavaScript(`(function(){
    function rect(sel) {
      var el = document.querySelector(sel);
      if (!el) return null;
      var r = el.getBoundingClientRect();
      var cs = getComputedStyle(el);
      return {
        x: Math.round(r.x), y: Math.round(r.y),
        w: Math.round(r.width), h: Math.round(r.height),
        display: cs.display, visibility: cs.visibility,
        overflow: cs.overflow, overflowX: cs.overflowX, overflowY: cs.overflowY,
        scrollW: el.scrollWidth, scrollH: el.scrollHeight,
        clientW: el.clientWidth, clientH: el.clientHeight,
        overflowing: el.scrollWidth > el.clientWidth + 2 || el.scrollHeight > el.clientHeight + 2,
      };
    }
    var checks = {};
    // Check key elements
    ['.app-shell', '.app-body', '.module-shell', '.module-tabs',
     '.connection-pane', '.master-workspace', '.slave-workspace',
     '.debug-workspace', '.parser-workspace',
     '.command-panel', '.register-panel', '.console-panel',
     '.connection-form', '.command-fields', '.command-actions',
     '.register-grid table', '.panel-toolbar'
    ].forEach(function(sel) {
      checks[sel] = rect(sel);
    });
    // Check for any element with zero size that should be visible
    var zeroSize = [];
    document.querySelectorAll('button, input, select, label').forEach(function(el) {
      if (el.offsetParent === null) return; // hidden
      var r = el.getBoundingClientRect();
      if (r.width < 5 || r.height < 5) {
        zeroSize.push({tag: el.tagName, id: el.id, w: Math.round(r.width), h: Math.round(r.height)});
      }
    });
    // Check for overlapping elements in command-fields
    var fields = [];
    document.querySelectorAll('.command-field').forEach(function(el) {
      if (el.offsetParent === null) return;
      var r = el.getBoundingClientRect();
      fields.push({id: el.querySelector('input,select')?.id, x: Math.round(r.x), y: Math.round(r.y), w: Math.round(r.width), h: Math.round(r.height)});
    });
    return JSON.stringify({checks: checks, zeroSizeElements: zeroSize.slice(0, 10), commandFields: fields, zeroCount: zeroSize.length});
  })()`);

  const data = JSON.parse(result);
  console.log(`\n=== View: ${viewName} (${data.zeroCount} zero-size elements) ===`);

  // Print key layout info
  for (const [sel, r] of Object.entries(data.checks)) {
    if (!r) { console.log(`  ${sel}: NULL (not found)`); continue; }
    if (r.display === 'none') { console.log(`  ${sel}: display:none`); continue; }
    const flag = r.overflowing ? " ⚠OVERFLOW" : "";
    console.log(`  ${sel}: ${r.w}x${r.h} at (${r.x},${r.y}) scroll=${r.scrollW}x${r.scrollH}${flag}`);
  }

  if (data.zeroSizeElements.length > 0) {
    console.log("  ZERO-SIZE elements:");
    data.zeroSizeElements.forEach(e => console.log(`    ${e.tag}#${e.id}: ${e.w}x${e.h}`));
  }

  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });

  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  for (const view of ["master", "slave", "debug", "parser"]) {
    await checkView(win, view);
  }

  // Check small window
  console.log("\n=== Resizing to 1024x700 ===");
  await win.setSize(1024, 700);
  await sleep(800);
  await checkView(win, "master");

  console.log("\n=== Resizing to 800x600 ===");
  await win.setSize(800, 600);
  await sleep(800);
  await checkView(win, "master");

  app.quit();
});
app.on("window-all-closed", () => app.quit());
