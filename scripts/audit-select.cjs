// Select-specific truncation audit: measures selected option text width vs available space.
// This catches the DOM-invisible truncation that the subagent identified.
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const SELECT_AUDIT = `(function() {
  // Create a canvas for text measurement
  var canvas = document.createElement('canvas');
  var ctx = canvas.getContext('2d');
  function measureText(text, font) {
    ctx.font = font;
    return ctx.measureText(text).width;
  }
  function isHidden(el) {
    if (!el || el.offsetParent === null) return true;
    var p = el.parentElement;
    while (p) { if (p.classList && p.classList.contains('hidden')) return true; p = p.parentElement; }
    return false;
  }

  var issues = [];
  document.querySelectorAll('select').forEach(function(sel) {
    if (isHidden(sel)) return;
    var cs = getComputedStyle(sel);
    // Measure the selected option text
    var selectedOpt = sel.options[sel.selectedIndex];
    if (!selectedOpt) return;
    var text = selectedOpt.text;
    var font = cs.fontWeight + ' ' + cs.fontSize + ' ' + cs.fontFamily;
    var textW = measureText(text, font);

    // Available width = clientWidth - paddingLeft - paddingRight - arrow(~20px)
    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var arrow = 20; // native dropdown arrow
    var availW = sel.clientWidth - padL - padR - arrow;

    if (textW > availW + 2) {
      issues.push({
        id: sel.id || '',
        text: text.substring(0, 40),
        textW: Math.round(textW),
        availW: Math.round(availW),
        clientW: sel.clientWidth,
        overflow: Math.round(textW - availW),
        fontSize: cs.fontSize,
      });
    }

    // Also check ALL options (not just selected) for potential truncation when selected
    for (var i = 0; i < sel.options.length; i++) {
      var opt = sel.options[i];
      if (opt.disabled) continue;
      var optText = opt.text;
      var optW = measureText(optText, font);
      if (optW > availW + 5) {
        // Only report if not already reported as the selected option
        if (i !== sel.selectedIndex) {
          issues.push({
            id: sel.id || '',
            text: '[opt] ' + optText.substring(0, 35),
            textW: Math.round(optW),
            availW: Math.round(availW),
            overflow: Math.round(optW - availW),
            fontSize: cs.fontSize,
          });
        }
      }
    }
  });
  return JSON.stringify({count: issues.length, issues: issues});
})()`;

async function checkView(win, view) {
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="${view}"]');if(b)b.click();})()`);
  await sleep(800);
  const raw = await win.webContents.executeJavaScript(SELECT_AUDIT);
  const data = JSON.parse(raw);
  console.log(`\n=== ${view.toUpperCase()} @ 1440x900: ${data.count} select truncation issues ===`);
  for (const it of data.issues) {
    console.log(`  ${it.id}: "${it.text}" textW=${it.textW} avail=${it.availW} over=${it.overflow} font=${it.fontSize}`);
  }
  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  for (const v of ["master", "slave", "debug", "parser"]) {
    await checkView(win, v);
  }

  // TCP mode
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await sleep(800);
  const tcpRaw = await win.webContents.executeJavaScript(SELECT_AUDIT);
  const tcpData = JSON.parse(tcpRaw);
  console.log(`\n=== TCP @ 1440x900: ${tcpData.count} issues ===`);
  for (const it of tcpData.issues) console.log(`  ${it.id}: "${it.text}" over=${it.overflow}`);

  // Small window
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="rtu"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await win.setSize(1024, 700);
  await sleep(800);
  await checkView(win, "master");

  app.quit();
});
app.on("window-all-closed", () => app.quit());
