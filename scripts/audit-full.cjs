// Deeper audit: check label/button/th text widths against their containers,
// select option text widths, and any text using text-overflow:ellipsis that IS clipped.
// Also measures "visible text" vs "full text" for nowrap elements.
const { app, BrowserWindow } = require("electron");
const path = require("path");
const fs = require("fs");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const SCAN_JS = `(function() {
  var issues = [];
  function measureText(text, fontRef) {
    var span = document.createElement('span');
    var cs = getComputedStyle(fontRef);
    span.style.font = cs.font;
    span.style.fontSize = cs.fontSize;
    span.style.fontWeight = cs.fontWeight;
    span.style.visibility = 'hidden';
    span.style.position = 'absolute';
    span.style.whiteSpace = 'nowrap';
    span.textContent = text;
    document.body.appendChild(span);
    var w = span.offsetWidth;
    document.body.removeChild(span);
    return w;
  }
  function isHidden(el) {
    if (el.offsetParent === null && getComputedStyle(el).display !== 'contents') return true;
    var p = el.parentElement;
    while (p) { if (p.classList && p.classList.contains('hidden')) return true; p = p.parentElement; }
    return false;
  }

  // 1. Check all <label> text width vs available width
  document.querySelectorAll('label').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.textContent.trim();
    if (!txt) return;
    var cs = getComputedStyle(el);
    var txtW = measureText(txt, el);
    // available = clientWidth minus padding
    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var availW = el.clientWidth - padL - padR;
    if (txtW > availW + 2 && availW > 0) {
      issues.push({type:'LABEL_CLIP', tag:'label', forId: el.getAttribute('for')||'', text: txt.substring(0,40), txtW: txtW, availW: availW, elW: el.offsetWidth, fontSize: cs.fontSize});
    }
  });

  // 2. Check all <button> text width
  document.querySelectorAll('button').forEach(function(el) {
    if (isHidden(el)) return;
    if (el.disabled) return;
    var txt = el.textContent.trim();
    if (!txt || txt.length < 2) return;
    var cs = getComputedStyle(el);
    var txtW = measureText(txt, el);
    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var availW = el.clientWidth - padL - padR;
    if (txtW > availW + 2 && availW > 0 && cs.whiteSpace === 'nowrap') {
      issues.push({type:'BUTTON_CLIP', id: el.id||'', text: txt.substring(0,30), txtW: txtW, availW: availW, elW: el.offsetWidth, fontSize: cs.fontSize});
    }
  });

  // 3. Check <select> selected option text width vs available
  document.querySelectorAll('select').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.value || (el.selectedIndex >= 0 ? el.options[el.selectedIndex].text : '');
    if (!txt) return;
    var cs = getComputedStyle(el);
    var txtW = measureText(txt, el);
    var availW = el.clientWidth - 24; // dropdown arrow ~20px + padding
    if (txtW > availW + 2 && availW > 0) {
      issues.push({type:'SELECT_CLIP', id: el.id||'', text: txt.substring(0,40), txtW: txtW, availW: availW, elW: el.offsetWidth, fontSize: cs.fontSize});
    }
  });

  // 4. Check <input> value/placeholder width
  document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.value || el.placeholder || '';
    if (!txt) return;
    var cs = getComputedStyle(el);
    var txtW = measureText(txt, el);
    var availW = el.clientWidth - 16;
    if (txtW > availW + 4 && availW > 10) {
      issues.push({type:'INPUT_VALUE_LONG', id: el.id||'', text: txt.substring(0,30), txtW: txtW, availW: availW, elW: el.offsetWidth, fontSize: cs.fontSize});
    }
    // Also check input element width itself is too small
    if (el.offsetWidth < 35) {
      issues.push({type:'INPUT_TOO_NARROW', id: el.id||'', elW: el.offsetWidth});
    }
  });

  // 5. Check <th>/<td> text width when white-space:nowrap and overflow:hidden
  document.querySelectorAll('th, td').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.textContent.trim();
    if (!txt || txt.length < 2) return;
    var cs = getComputedStyle(el);
    if (cs.overflow === 'hidden' && cs.whiteSpace === 'nowrap') {
      var padL = parseFloat(cs.paddingLeft) || 0;
      var padR = parseFloat(cs.paddingRight) || 0;
      var availW = el.clientWidth - padL - padR;
      var txtW = measureText(txt, el);
      if (txtW > availW + 2 && availW > 5) {
        issues.push({type:el.tagName+'_CLIP', text: txt.substring(0,35), txtW: txtW, availW: availW, fontSize: cs.fontSize, parent: el.parentElement.parentElement.parentElement.id||''});
      }
    }
  });

  // 6. Check <span> and <strong> with overflow:hidden that clip
  document.querySelectorAll('span, strong, small, summary').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.textContent.trim();
    if (!txt || txt.length < 2) return;
    var cs = getComputedStyle(el);
    if (cs.whiteSpace === 'nowrap') {
      var txtW = measureText(txt, el);
      var padL = parseFloat(cs.paddingLeft) || 0;
      var padR = parseFloat(cs.paddingRight) || 0;
      var availW = el.clientWidth - padL - padR;
      if (cs.overflow === 'hidden' && txtW > availW + 2 && availW > 5) {
        issues.push({type:'SPAN_CLIP', tag: el.tagName, id: el.id||'', cls:(el.className||'').substring(0,25), text: txt.substring(0,40), txtW: txtW, availW: availW, fontSize: cs.fontSize});
      } else if (cs.overflow !== 'hidden' && txtW > el.offsetWidth + 3 && el.offsetWidth > 0) {
        // text overflows visible area without hidden — may cause layout issues
        issues.push({type:'SPAN_OVERFLOW', tag: el.tagName, id: el.id||'', text: txt.substring(0,40), txtW: txtW, elW: el.offsetWidth, fontSize: cs.fontSize});
      }
    }
  });

  return JSON.stringify({count: issues.length, issues: issues});
})()`;

async function auditView(win, viewName, w, h) {
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('button[data-view="${viewName}"]');if(b)b.click();})()`);
  await sleep(1000);

  const result = await win.webContents.executeJavaScript(SCAN_JS);
  const data = JSON.parse(result);
  console.log(`\n=== ${viewName.toUpperCase()} @ ${w}x${h}: ${data.count} issues ===`);

  const byType = {};
  for (const it of data.issues) {
    if (!byType[it.type]) byType[it.type] = [];
    byType[it.type].push(it);
  }
  for (const [type, items] of Object.entries(byType)) {
    console.log(`\n  [${type}] (${items.length})`);
    for (const it of items) {
      const t = it.text ? `"${it.text}"` : '';
      const dims = it.txtW ? `textW=${it.txtW} avail=${it.availW}` : `elW=${it.elW}`;
      const id = it.id || it.forId || it.cls || '';
      console.log(`    ${it.tag||''} ${id} ${t} [${dims}] font=${it.fontSize||''}`);
    }
  }
  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  for (const view of ["master", "slave", "debug", "parser"]) {
    await auditView(win, view, 1440, 900);
  }

  // TCP mode
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await sleep(800);
  const tcpResult = await win.webContents.executeJavaScript(SCAN_JS);
  const tcpData = JSON.parse(tcpResult);
  console.log(`\n=== TCP MODE @ 1440x900: ${tcpData.count} issues ===`);
  const tcpByType = {};
  for (const it of tcpData.issues) { if (!tcpByType[it.type]) tcpByType[it.type] = []; tcpByType[it.type].push(it); }
  for (const [type, items] of Object.entries(tcpByType)) {
    console.log(`\n  [${type}] (${items.length})`);
    for (const it of items) { const t = it.text?`"${it.text}"`:''; const d = it.txtW?`textW=${it.txtW} avail=${it.availW}`:''; console.log(`    ${it.id||it.forId||''} ${t} [${d}]`); }
  }

  // Smaller sizes
  await win.webContents.executeJavaScript(`(function(){var r=document.querySelector('input[name="transport"][value="rtu"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  for (const [w, h] of [[1280, 800], [1024, 700]]) {
    await win.setSize(w, h);
    await sleep(800);
    await auditView(win, "master", w, h);
  }

  app.quit();
});
app.on("window-all-closed", () => app.quit());
