// Adversarial truncation audit: scans every visible text-bearing element for overflow.
// Checks: labels, buttons, inputs, select options, table cells, strong/span/small/p.
// Reports element type, id, text, computed size vs scroll size.
// Usage: npx electron scripts/audit-truncation.cjs
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Inject this function into the renderer to scan all elements
const SCAN_JS = `(function() {
  var results = [];
  var selectors = 'label, button, strong, span, small, p, th, td, summary, .section-title, .section-state, .panel-name, .field-meta, .nav-heading, .brand-copy';
  var all = document.querySelectorAll(selectors);
  var visible = 0, truncated = 0;

  all.forEach(function(el) {
    if (el.offsetParent === null && getComputedStyle(el).display !== 'contents') return;
    // Skip if inside hidden parent
    var parent = el.parentElement;
    var hidden = false;
    while (parent) {
      if (parent.classList && parent.classList.contains('hidden')) { hidden = true; break; }
      parent = parent.parentElement;
    }
    if (hidden) return;

    visible++;
    var r = el.getBoundingClientRect();
    if (r.width < 2 || r.height < 2) return;

    var cs = getComputedStyle(el);
    // Check horizontal truncation (text clipping)
    var hOverflow = el.scrollWidth - el.clientWidth;
    // Check vertical truncation
    var vOverflow = el.scrollHeight - el.clientHeight;

    // For elements with overflow:hidden, check if text is clipped
    var hasText = el.textContent && el.textContent.trim().length > 0;
    var singleLine = cs.whiteSpace === 'nowrap' || cs.whiteSpace === 'pre';
    var canClip = cs.overflow === 'hidden' || cs.textOverflow === 'ellipsis' || singleLine;

    if (hasText && ((hOverflow > 1 && canClip) || (vOverflow > 1 && cs.overflow === 'hidden'))) {
      truncated++;
      var text = el.textContent.trim().substring(0, 50);
      var id = el.id || el.className || el.tagName;
      results.push({
        tag: el.tagName,
        id: el.id || '',
        cls: (typeof el.className === 'string' ? el.className : '').substring(0, 40),
        text: text,
        w: Math.round(r.width),
        h: Math.round(r.height),
        scrollW: el.scrollWidth,
        clientW: el.clientWidth,
        scrollH: el.scrollHeight,
        clientH: el.clientHeight,
        hOver: hOverflow,
        vOver: vOverflow,
        fontSize: cs.fontSize,
        whiteSpace: cs.whiteSpace,
        overflow: cs.overflow,
      });
    }
  });

  // Also check input/select placeholder truncation
  document.querySelectorAll('input, select').forEach(function(el) {
    if (el.offsetParent === null) return;
    var parent = el.parentElement;
    var hidden = false;
    while (parent) {
      if (parent.classList && parent.classList.contains('hidden')) { hidden = true; break; }
      parent = parent.parentElement;
    }
    if (hidden) return;

    var r = el.getBoundingClientRect();
    if (r.width < 10) return;
    var cs = getComputedStyle(el);
    // Check if the select's selected option text is wider than the select
    if (el.tagName === 'SELECT') {
      var text = el.value || (el.options[el.selectedIndex] && el.options[el.selectedIndex].text) || '';
      // Create a temporary span to measure text width
      var span = document.createElement('span');
      span.style.font = cs.font;
      span.style.fontSize = cs.fontSize;
      span.style.visibility = 'hidden';
      span.style.position = 'absolute';
      span.textContent = text;
      document.body.appendChild(span);
      var textW = span.offsetWidth;
      document.body.removeChild(span);
      var availW = el.clientWidth - 20; // account for dropdown arrow
      if (textW > availW + 2) {
        truncated++;
        results.push({
          tag: 'SELECT',
          id: el.id || '',
          text: text.substring(0, 40),
          w: Math.round(r.width),
          textW: textW,
          availW: availW,
          fontSize: cs.fontSize,
          issue: 'OPTION_TEXT_TRUNCATED',
        });
      }
    }
    // Check input width too small
    if (r.width < 30 && el.type !== 'radio' && el.type !== 'checkbox') {
      truncated++;
      results.push({
        tag: 'INPUT',
        id: el.id || '',
        text: (el.placeholder || el.value || '').substring(0, 30),
        w: Math.round(r.width),
        issue: 'INPUT_TOO_NARROW',
      });
    }
  });

  return JSON.stringify({ visibleScanned: visible, truncatedCount: truncated, items: results });
})()`;

async function auditView(win, viewName, w, h) {
  // Click the view tab
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="${viewName}"]');if(b)b.click();})()`);
  await sleep(1000);

  const result = await win.webContents.executeJavaScript(SCAN_JS);
  const data = JSON.parse(result);
  console.log(`\n=== ${viewName.toUpperCase()} @ ${w}x${h}: ${data.truncatedCount} truncation issues (scanned ${data.visibleScanned} elements) ===`);

  // Group by issue type
  const byType = {};
  for (const item of data.items) {
    const key = item.issue || (item.hOver > 1 ? 'H_CLIP' : 'V_CLIP');
    if (!byType[key]) byType[key] = [];
    byType[key].push(item);
  }

  for (const [type, items] of Object.entries(byType)) {
    console.log(`\n  [${type}] (${items.length} cases)`);
    for (const it of items) {
      const txt = it.text ? `"${it.text}"` : '';
      const dim = it.issue ? `${it.w}px` : `${it.w}x${it.h} scroll=${it.scrollW}x${it.scrollH}`;
      const over = it.hOver > 1 ? ` hOverflow=${it.hOver}` : (it.vOver > 1 ? ` vOverflow=${it.vOver}` : '');
      const fs = it.fontSize ? ` font=${it.fontSize}` : '';
      const ws = it.whiteSpace ? ` ws=${it.whiteSpace}` : '';
      console.log(`    ${it.tag}#${it.id} ${it.cls || ''} ${txt} [${dim}]${over}${fs}${ws}${it.textW ? ' textW='+it.textW : ''}${it.availW ? ' availW='+it.availW : ''}`);
    }
  }
  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });

  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  // Audit all 4 views at 1440x900
  for (const view of ["master", "slave", "debug", "parser"]) {
    await auditView(win, view, 1440, 900);
  }

  // Audit master at smaller sizes
  console.log("\n\n========== RESIZING TO 1280x800 ==========");
  await win.setSize(1280, 800);
  await sleep(800);
  await auditView(win, "master", 1280, 800);

  console.log("\n\n========== RESIZING TO 1024x700 ==========");
  await win.setSize(1024, 700);
  await sleep(800);
  await auditView(win, "master", 1024, 700);

  // Also audit TCP mode at 1440
  console.log("\n\n========== TCP MODE @ 1440x900 ==========");
  await win.setSize(1440, 900);
  await sleep(500);
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="master"]');if(b)b.click();})()`);
  await sleep(500);
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await sleep(800);
  const tcpResult = await win.webContents.executeJavaScript(SCAN_JS);
  const tcpData = JSON.parse(tcpResult);
  console.log(`\n=== TCP MODE @ 1440x900: ${tcpData.truncatedCount} truncation issues ===`);
  for (const it of tcpData.items) {
    const txt = it.text ? `"${it.text}"` : '';
    const dim = it.issue ? `${it.w}px` : `${it.w}x${it.h}`;
    const over = it.hOver > 1 ? ` hOver=${it.hOver}` : '';
    console.log(`  ${it.tag}#${it.id} ${it.cls||''} ${txt} [${dim}]${over} font=${it.fontSize||''}`);
  }

  app.quit();
});
app.on("window-all-closed", () => app.quit());
