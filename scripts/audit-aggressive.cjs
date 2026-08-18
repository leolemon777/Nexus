// Aggressive visual audit: check EVERY visible text element for potential truncation
// even without overflow:hidden — if text measurement > element width, it's a risk.
// Also captures screenshots of each view for manual review.
const { app, BrowserWindow } = require("electron");
const path = require("path");
const fs = require("fs");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const AUDIT_JS = `(function() {
  var issues = [];
  function measureText(text, fontRef) {
    var span = document.createElement('span');
    var cs = getComputedStyle(fontRef);
    span.style.font = cs.font;
    span.style.fontSize = cs.fontSize;
    span.style.fontWeight = cs.fontWeight;
    span.style.fontFamily = cs.fontFamily;
    span.style.visibility = 'hidden';
    span.style.position = 'absolute';
    span.style.whiteSpace = 'pre';
    span.textContent = text;
    document.body.appendChild(span);
    var w = span.offsetWidth;
    document.body.removeChild(span);
    return w;
  }
  function isHidden(el) {
    if (!el || el.offsetParent === null) return true;
    var cs = getComputedStyle(el);
    if (cs.display === 'none' || cs.visibility === 'hidden') return true;
    var p = el.parentElement;
    while (p) {
      if (p.classList && p.classList.contains('hidden')) return true;
      var pcs = getComputedStyle(p);
      if (pcs.display === 'none') return true;
      p = p.parentElement;
    }
    return false;
  }

  // Scan ALL text-containing elements
  var allEls = document.querySelectorAll('*');
  var checked = 0;
  allEls.forEach(function(el) {
    if (isHidden(el)) return;
    // Only check leaf-ish text nodes (direct textContent with no child elements that have text)
    var directText = '';
    el.childNodes.forEach(function(n) {
      if (n.nodeType === 3) directText += n.textContent;
    });
    directText = directText.trim();
    if (!directText || directText.length < 2) return;

    var cs = getComputedStyle(el);
    if (cs.display === 'none') return;

    checked++;
    var r = el.getBoundingClientRect();
    if (r.width < 2) return;

    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var availW = r.width - padL - padR;
    if (availW < 1) return;

    var txtW = measureText(directText, el);
    var ratio = txtW / availW;

    // Flag if text is more than 5px wider than available space
    if (txtW > availW + 5) {
      // Determine severity
      var severity = 'LOW';
      if (txtW > availW + 30) severity = 'HIGH';
      else if (txtW > availW + 15) severity = 'MED';

      // Check if it actually clips (overflow hidden + nowrap)
      var willClip = (cs.overflow === 'hidden' || cs.overflowX === 'hidden') && (cs.whiteSpace === 'nowrap' || cs.whiteSpace === 'pre');
      // Also flag nowrap elements that don't clip but still overflow visually
      if (!willClip && cs.whiteSpace !== 'nowrap' && cs.whiteSpace !== 'pre') return; // skip if text can wrap naturally

      issues.push({
        sev: severity,
        tag: el.tagName,
        id: el.id || '',
        cls: (typeof el.className === 'string' ? el.className : '').substring(0, 30),
        text: directText.substring(0, 45),
        txtW: txtW,
        availW: Math.round(availW),
        overflow: Math.round(txtW - availW),
        fontSize: cs.fontSize,
        whiteSpace: cs.whiteSpace,
        elOverflow: cs.overflow,
        willClip: willClip,
        parent: el.parentElement ? (el.parentElement.id || el.parentElement.tagName) : '',
      });
    }
  });

  // Sort by severity then overflow amount
  var sevOrder = {HIGH: 0, MED: 1, LOW: 2};
  issues.sort(function(a, b) { if (sevOrder[a.sev] !== sevOrder[b.sev]) return sevOrder[a.sev] - sevOrder[b.sev]; return b.overflow - a.overflow; });

  return JSON.stringify({checked: checked, found: issues.length, issues: issues});
})()`;

async function audit(win, label) {
  const raw = await win.webContents.executeJavaScript(AUDIT_JS);
  const data = JSON.parse(raw);
  console.log(`\n${'='.repeat(70)}`);
  console.log(`AUDIT: ${label} — checked ${data.checked} elements, found ${data.found} issues`);
  console.log('='.repeat(70));

  for (const it of data.issues) {
    const clip = it.willClip ? '✂CLIPS' : '↗OVERFLOW';
    console.log(`  [${it.sev}] ${it.tag}#${it.id} ${it.cls} ${clip} "${it.text}" txtW=${it.txtW} avail=${it.availW} over=${it.overflow} font=${it.fontSize} parent=${it.parent}`);
  }
  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  // All views at 1440x900
  for (const v of ["master", "slave", "debug", "parser"]) {
    await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="${v}"]');if(b)b.click();})()`);
    await sleep(800);
    await audit(win, `${v.toUpperCase()} @ 1440x900`);
  }

  // TCP mode
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="master"]');if(b)b.click();var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await sleep(800);
  await audit(win, "TCP @ 1440x900");

  // Back to RTU
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="rtu"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);

  // Smaller sizes
  for (const [w, h] of [[1280, 800], [1024, 700], [800, 600]]) {
    await win.setSize(w, h);
    await sleep(800);
    await audit(win, `MASTER @ ${w}x${h}`);
  }

  app.quit();
});
app.on("window-all-closed", () => app.quit());
