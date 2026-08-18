// UI 对齐/截断对抗性审查:逐页截图 + DOM 度量(溢出/基线偏差/数字框宽度)
const { app, BrowserWindow } = require("electron");
const path = require("path");
const fs = require("fs");
app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

const VIEWS = ["master", "interfaces", "slave", "debug", "parser", "melsec", "siemens"];

app.whenReady().then(async () => {
  const outdir = path.join(process.cwd(), "audit-ui");
  if (!fs.existsSync(outdir)) fs.mkdirSync(outdir, { recursive: true });
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true, nodeIntegration: false, sandbox: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  for (const view of VIEWS) {
    await win.webContents.executeJavaScript(`document.querySelector('.nav-item[data-view="${view}"]').click()`);
    await sleep(900);
    const report = await win.webContents.executeJavaScript(`(function(){
      const issues = [];
      // 1) 输入框/下拉内容溢出(scrollWidth > clientWidth → 文字显示不全)
      document.querySelectorAll('input, select').forEach(function(el){
        const r = el.getBoundingClientRect();
        if (r.width === 0) return;
        if (el.tagName === 'INPUT' && el.type !== 'number' && el.scrollWidth > el.clientWidth + 1) {
          issues.push({ type: 'input-overflow', sel: describe(el), scroll: el.scrollWidth, client: el.clientWidth });
        }
        // number 输入:值长度估算 vs 可视宽(扣除 spinner ~17px)
        if (el.type === 'number') {
          const val = String(el.value ?? '');
          const usable = el.clientWidth - 22; // spinner + padding
          const est = val.length * 8.2;
          if (est > usable) {
            issues.push({ type: 'number-truncated', sel: describe(el), value: val, usable: Math.round(usable), est: Math.round(est) });
          }
          if (el.clientWidth < 46) {
            issues.push({ type: 'number-too-narrow', sel: describe(el), width: Math.round(el.clientWidth) });
          }
        }
      });
      // 2) 任意元素文本溢出且被裁剪(ellipsis 之外的溢出)
      document.querySelectorAll('.form-row, .card-header, .section-state, .field-meta').forEach(function(el){
        if (el.scrollWidth > el.clientWidth + 2) {
          issues.push({ type: 'container-overflow', sel: describe(el), scroll: el.scrollWidth, client: el.clientWidth });
        }
      });
      // 3) form-row 内基线/底边不齐(bottom 差 > 3px)
      document.querySelectorAll('.form-row').forEach(function(row){
        const kids = Array.from(row.children).filter(function(k){ return k.getBoundingClientRect().height > 0; });
        const bottoms = kids.map(function(k){ return Math.round(k.getBoundingClientRect().bottom); });
        const tops = kids.map(function(k){ return Math.round(k.getBoundingClientRect().top); });
        const bSpread = Math.max.apply(null, bottoms) - Math.min.apply(null, bottoms);
        const tSpread = Math.max.apply(null, tops) - Math.min.apply(null, tops);
        if (bSpread > 3 || tSpread > 3) {
          issues.push({ type: 'row-misaligned', sel: describe(row), topSpread: tSpread, bottomSpread: bSpread,
            kids: kids.map(function(k){ return k.tagName + '.' + (k.className||'').toString().split(' ')[0]; }) });
        }
      });
      // 4) 元素重叠(同 form-row 内兄弟控件 rect 相交 > 2px)
      document.querySelectorAll('.form-row').forEach(function(row){
        const kids = Array.from(row.children).filter(function(k){ return k.getBoundingClientRect().width > 0; });
        for (let i = 0; i < kids.length; i++) for (let j = i+1; j < kids.length; j++) {
          const a = kids[i].getBoundingClientRect(), b = kids[j].getBoundingClientRect();
          const ox = Math.min(a.right, b.right) - Math.max(a.left, b.left);
          if (ox > 2 && a.top < b.bottom - 2 && b.top < a.bottom - 2 &&
              getComputedStyle(kids[i]).position !== 'absolute' && getComputedStyle(kids[j]).position !== 'absolute') {
            issues.push({ type: 'overlap', sel: describe(row), a: kids[i].id || kids[i].tagName, b: kids[j].id || kids[j].tagName, ox: Math.round(ox) });
          }
        }
      });
      function describe(el) {
        if (el.id) return '#' + el.id;
        const p = el.closest('[id]');
        return (p ? '#' + p.id + ' ' : '') + el.tagName.toLowerCase() + '.' + String(el.className||'').split(' ')[0];
      }
      return JSON.stringify(issues);
    })()`);
    const issues = JSON.parse(report);
    const img = await win.webContents.capturePage();
    fs.writeFileSync(path.join(outdir, `ui-${view}.png`), img.toPNG());
    console.log(`\n=== ${view}: ${issues.length} issues ===`);
    for (const it of issues.slice(0, 25)) console.log(JSON.stringify(it));
  }
  app.quit();
});
app.on("window-all-closed", () => app.quit());
