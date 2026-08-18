// Comprehensive text truncation audit for 3-column layout
// Checks every visible text element across all 4 views + TCP mode + multiple sizes
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const AUDIT = `(function() {
  var issues = [];
  function measureText(text, fontRef) {
    var span = document.createElement('span');
    var cs = getComputedStyle(fontRef);
    span.style.font = cs.font;
    span.style.fontWeight = cs.fontWeight;
    span.style.fontFamily = cs.fontFamily;
    span.style.fontSize = cs.fontSize;
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
    if (!el) return true;
    if (el.offsetParent === null) {
      var cs = getComputedStyle(el);
      if (cs.display === 'none' || cs.visibility === 'hidden') return true;
    }
    var p = el;
    while (p) {
      if (p.classList && p.classList.contains('hidden')) return true;
      var pcs = getComputedStyle(p);
      if (pcs.display === 'none') return true;
      p = p.parentElement;
    }
    return false;
  }

  // 1. Check all text-bearing leaf elements
  document.querySelectorAll('label, button, strong, span, small, summary, .form-label, .nav-label, .card-header strong, .section-state').forEach(function(el) {
    if (isHidden(el)) return;
    var direct = '';
    el.childNodes.forEach(function(n) { if (n.nodeType === 3) direct += n.textContent; });
    direct = direct.trim();
    if (!direct || direct.length < 1) return;
    var cs = getComputedStyle(el);
    var r = el.getBoundingClientRect();
    if (r.width < 2) return;
    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var availW = r.width - padL - padR;
    if (availW < 1) return;
    var txtW = measureText(direct, el);
    if (txtW > availW + 3 && (cs.whiteSpace === 'nowrap' || cs.overflow === 'hidden' || cs.textOverflow === 'ellipsis')) {
      issues.push({type:'TEXT_CLIP', tag:el.tagName, id:el.id||'', cls:(typeof el.className==='string'?el.className:'').substring(0,30), text:direct.substring(0,40), txtW:Math.round(txtW), availW:Math.round(availW), over:Math.round(txtW-availW), font:cs.fontSize});
    }
  });

  // 2. Check select selected option text vs available width
  document.querySelectorAll('select').forEach(function(sel) {
    if (isHidden(sel)) return;
    var opt = sel.options[sel.selectedIndex];
    if (!opt) return;
    var txt = opt.text;
    var cs = getComputedStyle(sel);
    var r = sel.getBoundingClientRect();
    var txtW = measureText(txt, sel);
    var availW = r.width - (parseFloat(cs.paddingLeft)||0) - (parseFloat(cs.paddingRight)||0) - 22; // arrow+border
    if (txtW > availW + 2 && availW > 0) {
      issues.push({type:'SELECT_CLIP', id:sel.id||'', text:txt.substring(0,35), txtW:Math.round(txtW), availW:Math.round(availW), over:Math.round(txtW-availW), font:cs.fontSize});
    }
  });

  // 3. Check input value/placeholder
  document.querySelectorAll('input[type="text"], input[type="number"], input:not([type])').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.value || el.placeholder || '';
    if (!txt || txt.length < 2) return;
    var cs = getComputedStyle(el);
    var r = el.getBoundingClientRect();
    var txtW = measureText(txt, el);
    var availW = r.width - (parseFloat(cs.paddingLeft)||0) - (parseFloat(cs.paddingRight)||0) - 4;
    if (txtW > availW + 4 && availW > 10) {
      issues.push({type:'INPUT_CLIP', id:el.id||'', text:txt.substring(0,30), txtW:Math.round(txtW), availW:Math.round(availW), over:Math.round(txtW-availW), font:cs.fontSize});
    }
  });

  // 4. Check table cells (th/td) with nowrap + overflow hidden
  document.querySelectorAll('th, td').forEach(function(el) {
    if (isHidden(el)) return;
    var txt = el.textContent.trim();
    if (!txt || txt.length < 2) return;
    var cs = getComputedStyle(el);
    var r = el.getBoundingClientRect();
    if (r.width < 2) return;
    var padL = parseFloat(cs.paddingLeft) || 0;
    var padR = parseFloat(cs.paddingRight) || 0;
    var availW = r.width - padL - padR;
    var txtW = measureText(txt, el);
    if (txtW > availW + 3 && availW > 5) {
      var parent = el.closest('table').parentElement;
      issues.push({type:el.tagName+'_CLIP', text:txt.substring(0,30), txtW:Math.round(txtW), availW:Math.round(availW), over:Math.round(txtW-availW), parent:parent.className||parent.id||''});
    }
  });

  // 5. Check for card overlap (elements from different cards overlapping)
  var cards = document.querySelectorAll('.card');
  var cardRects = [];
  cards.forEach(function(c) {
    if (isHidden(c)) return;
    cardRects.push({el:c, r:c.getBoundingClientRect()});
  });
  for (var i = 0; i < cardRects.length; i++) {
    for (var j = i+1; j < cardRects.length; j++) {
      var a = cardRects[i].r, b = cardRects[j].r;
      var overlap = !(a.right < b.left || a.left > b.right || a.bottom < b.top || a.top > b.bottom);
      if (overlap) {
        issues.push({type:'CARD_OVERLAP', card1:cardRects[i].el.querySelector('.card-header strong')?.textContent||'?', card2:cardRects[j].el.querySelector('.card-header strong')?.textContent||'?'});
      }
    }
  }

  // 6. Check panel overflow (content extending beyond container)
  ['main-content', 'packet-list', 'sidebar-nav', 'data-grid'].forEach(function(sel) {
    var el = document.querySelector('.' + sel);
    if (!el || isHidden(el)) return;
    if (el.scrollHeight > el.clientHeight + 5 && getComputedStyle(el).overflowY !== 'auto' && getComputedStyle(el).overflowY !== 'scroll') {
      issues.push({type:'CONTAINER_OVERFLOW', container:sel, scrollH:el.scrollHeight, clientH:el.clientHeight});
    }
  });

  return JSON.stringify({count: issues.length, issues: issues});
})()`;

async function auditView(win, view, w, h) {
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('.nav-item[data-view="${view}"]');if(b)b.click();})()`);
  await sleep(800);
  const raw = await win.webContents.executeJavaScript(AUDIT);
  const data = JSON.parse(raw);
  const label = `${view.toUpperCase()} @ ${w}x${h}`;
  if (data.count === 0) {
    console.log(`✅ ${label}: 0 issues`);
  } else {
    console.log(`\n❌ ${label}: ${data.count} issues`);
    const byType = {};
    data.issues.forEach(it => { if (!byType[it.type]) byType[it.type] = []; byType[it.type].push(it); });
    for (const [type, items] of Object.entries(byType)) {
      console.log(`  [${type}] (${items.length})`);
      items.forEach(it => {
        const t = it.text ? `"${it.text}"` : '';
        const d = it.txtW ? `txt=${it.txtW} avail=${it.availW} over=${it.over}` : '';
        const f = it.font ? ` font=${it.font}` : '';
        const extra = it.card1 ? `${it.card1} ↔ ${it.card2}` : (it.container ? `${it.container} scroll=${it.scrollH}>${it.clientH}` : (it.parent ? `parent=${it.parent}` : ''));
        console.log(`    ${it.tag||''} ${it.id||it.cls||''} ${t} ${d}${f} ${extra}`);
      });
    }
  }
  return data;
}

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  for (const v of ["master", "slave", "debug", "parser"]) {
    await auditView(win, v, 1440, 900);
  }

  // TCP mode
  await win.webContents.executeJavaScript(`(function(){var b=document.querySelector('.nav-item[data-view="master"]');if(b)b.click();var r=document.querySelector('input[name="transport"][value="tcp"]');if(r){r.checked=true;r.dispatchEvent(new Event('change',{bubbles:true}));}})()`);
  await sleep(800);
  const tcpRaw = await win.webContents.executeJavaScript(AUDIT);
  const tcpData = JSON.parse(tcpRaw);
  console.log(`\n${tcpData.count === 0 ? '✅' : '❌'} TCP @ 1440x900: ${tcpData.count} issues`);
  tcpData.issues.forEach(it => console.log(`  [${it.type}] ${it.id||''} "${it.text||''}" over=${it.over||0}`));

  // Smaller sizes
  for (const [w, h] of [[1280, 800], [1024, 700]]) {
    await win.setSize(w, h);
    await sleep(600);
    await auditView(win, "master", w, h);
  }

  app.quit();
});
app.on("window-all-closed", () => app.quit());
