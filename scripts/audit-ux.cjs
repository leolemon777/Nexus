// Current UI pain points analysis: what makes this hard to use.
// Run: npx electron scripts/audit-ux.cjs
const { app, BrowserWindow } = require("electron");
const path = require("path");

app.commandLine.appendSwitch("disable-gpu");
app.commandLine.appendSwitch("no-sandbox");
const sleep = (ms) => new Promise(r => setTimeout(r, ms));

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1440, height: 900, show: false,
    webPreferences: { offscreen: true, contextIsolation: true } });
  await win.loadURL("file://" + path.join(process.cwd(), "dist", "index.html").replace(/\\/g, "/"));
  await sleep(2500);

  // Measure the entire UI hierarchy: what takes how much space
  const layout = await win.webContents.executeJavaScript(`(function() {
    function m(sel) {
      var el = document.querySelector(sel);
      if (!el || el.offsetParent === null) return null;
      var r = el.getBoundingClientRect();
      return {w: Math.round(r.width), h: Math.round(r.height), x: Math.round(r.x), y: Math.round(r.y)};
    }
    return JSON.stringify({
      window: {w: innerWidth, h: innerHeight},
      appShell: m('#app'),
      header: m('.app-header'),
      sidebar: m('.protocol-sidebar'),
      moduleShell: m('.module-shell'),
      moduleTabs: m('.module-tabs'),
      connectionPane: m('.connection-pane'),
      connectionForm: m('.connection-form'),
      connectionHeader: m('.connection-header'),
      masterWorkspace: m('.master-workspace'),
      commandPanel: m('.command-panel'),
      commandBody: m('.command-body'),
      commandFields: m('.command-fields'),
      commandActions: m('.command-actions'),
      registerPanel: m('.register-panel'),
      registerGrid: m('.register-grid'),
      panelToolbar: m('.register-panel .panel-toolbar'),
      panelStatus: m('.register-panel .panel-status'),
      consolePanel: m('.console-panel'),
      consoleTabs: m('.console-tabs'),
      consoleContent: m('.console-content.is-active'),
      statusbar: m('.statusbar'),
    });
  })()`);

  const L = JSON.parse(layout);
  console.log("=== UI SPACE ALLOCATION @ 1440x900 ===\n");
  const total = L.window.h;
  console.log(`Window:        ${L.window.w}x${L.window.h}`);
  console.log(`Header:        ${L.header.h}px (${Math.round(L.header.h/total*100)}%)`);
  console.log(`Statusbar:     ${L.statusbar.h}px (${Math.round(L.statusbar.h/total*100)}%)`);
  console.log(`Module tabs:   ${L.moduleTabs.h}px (${Math.round(L.moduleTabs.h/total*100)}%)`);
  console.log(`Connection:    ${L.connectionPane.h}px (${Math.round(L.connectionPane.h/total*100)}%)`);
  console.log(`  - form:      ${L.connectionForm.h}px`);
  console.log(`  - header:    ${L.connectionHeader.h}px`);
  console.log(`Workspace:     ${L.masterWorkspace.h}px (${Math.round(L.masterWorkspace.h/total*100)}%)`);
  console.log(`  - command:   ${L.commandPanel.h}px (${Math.round(L.commandPanel.h/L.masterWorkspace.h*100)}% of workspace)`);
  console.log(`    - fields:  ${L.commandFields.h}px`);
  console.log(`    - actions: ${L.commandActions.h}px`);
  console.log(`  - register:  ${L.registerPanel.h}px (${Math.round(L.registerPanel.h/L.masterWorkspace.h*100)}%)`);
  console.log(`    - toolbar: ${L.panelToolbar.h}px`);
  console.log(`    - grid:    ${L.registerGrid.h}px`);
  console.log(`    - status:  ${L.panelStatus.h}px`);
  console.log(`  - console:   ${L.consolePanel.h}px (${Math.round(L.consolePanel.h/L.masterWorkspace.h*100)}%)`);
  console.log(`    - tabs:    ${L.consoleTabs.h}px`);
  console.log(`    - content: ${L.consoleContent.h}px`);
  console.log(`Sidebar:       ${L.sidebar.w}px (${Math.round(L.sidebar.w/L.window.w*100)}% width)`);

  // Count total visible interactive elements
  const counts = await win.webContents.executeJavaScript(`(function() {
    function count(sel, parent) {
      var els = (parent || document).querySelectorAll(sel);
      var visible = 0;
      els.forEach(function(e) { if (e.offsetParent !== null) visible++; });
      return visible;
    }
    return JSON.stringify({
      inputs: count('input:not([type="radio"]):not([type="checkbox"])'),
      selects: count('select'),
      buttons: count('button'),
      radios: count('input[type="radio"]'),
      checkboxes: count('input[type="checkbox"]'),
      labels: count('label'),
      details: count('details'),
      tables: count('table'),
      totalInteractive: count('input, select, button, details'),
      totalText: count('label, strong, span, small, p, th, td'),
    });
  })()`);
  const C = JSON.parse(counts);
  console.log(`\n=== ELEMENT COUNTS (visible) ===`);
  console.log(`Inputs:     ${C.inputs}`);
  console.log(`Selects:    ${C.selects}`);
  console.log(`Buttons:    ${C.buttons}`);
  console.log(`Radios:     ${C.radios}`);
  console.log(`Labels:     ${C.labels}`);
  console.log(`Details:    ${C.details}`);
  console.log(`Tables:     ${C.tables}`);
  console.log(`Total interactive: ${C.totalInteractive}`);
  console.log(`Total text: ${C.totalText}`);

  // Count clicks needed for common tasks
  console.log(`\n=== CLICK PATHS (estimated) ===`);
  console.log("Open serial port:     1 click (select port) + 1 click (open) = 2 clicks");
  console.log("Read registers:       2 (open) + 0 (FC=03 default) + 0 (addr=0) + 0 (qty=1) + 1 (read) = 3 clicks");
  console.log("Write single register: 2 (open) + 1 (FC) + 1 (addr) + 1 (value) + 1 (write) = 6 clicks");
  console.log("Switch to slave view:  1 click (tab) + 1 click (start) = 2 clicks");
  console.log("Debug: send hex:      1 (tab) + 1 (bind) + 1 (input) + 1 (send) = 4 clicks");
  console.log("Parse frame:          1 (tab) + 1 (input) + 1 (parse) = 3 clicks");

  app.quit();
});
app.on("window-all-closed", () => app.quit());
