import "./app.css";
import { buildPollPlan, splitBatchResult } from "./poll-planner.js";
import { filterTrace } from "./trace-filter.js";

const elements = {
  form: document.querySelector("#serial-form"),
  portName: document.querySelector("#port-name"),
  portCount: document.querySelector("#port-count"),
  portHint: document.querySelector("#port-hint"),
  refresh: document.querySelector("#refresh-ports"),
  open: document.querySelector("#open-port"),
  close: document.querySelector("#close-port"),
  restore: document.querySelector("#restore-defaults"),
  connectionPill: document.querySelector("#connection-pill"),
  connectionLabel: document.querySelector("#connection-label"),
  detailState: document.querySelector("#detail-state"),
  detailPort: document.querySelector("#detail-port"),
  detailFormat: document.querySelector("#detail-format"),
  detailTimeout: document.querySelector("#detail-timeout"),
  notice: document.querySelector("#notice"),
  noticeTitle: document.querySelector("#notice-title"),
  noticeMessage: document.querySelector("#notice-message"),
  flowControl: document.querySelector("#flow-control"),
  rtsMode: document.querySelector("#rts-mode"),
  workspace: document.querySelector("#workspace"),
  consoleToggle: document.querySelector("#toggle-console"),
  consoleTabs: [...document.querySelectorAll(".console-tab")],
  consolePanels: [...document.querySelectorAll("[data-console-panel]")],
  commandFields: document.querySelector("#command-fields"),
  commandState: document.querySelector("#command-state"),
  functionCode: document.querySelector("#function-code"),
  unitId: document.querySelector("#unit-id"),
  startAddress: document.querySelector("#start-address"),
  addressBase: document.querySelector("#address-base"),
  scaleFactor: document.querySelector("#scale-factor"),
  unitLabel: document.querySelector("#unit-label"),
  addPoint: document.querySelector("#add-point"),
  importPoints: document.querySelector("#import-points"),
  savePoints: document.querySelector("#save-points"),
  quantity: document.querySelector("#quantity"),
  commandTimeout: document.querySelector("#command-timeout"),
  readOnce: document.querySelector("#read-once"),
  writeOnce: document.querySelector("#write-once"),
  writeValue: document.querySelector("#write-value"),
  writeValueField: document.querySelector("#write-value-field"),
  scanStations: document.querySelector("#scan-stations"),
  scanBaud: document.querySelector("#scan-baud"),
  pollInterval: document.querySelector("#poll-interval"),
  startPoll: document.querySelector("#start-poll"),
  stopPoll: document.querySelector("#stop-poll"),
  addCmd: document.querySelector("#add-cmd"),
  clearCmd: document.querySelector("#clear-cmd"),
  executeCmds: document.querySelector("#execute-cmds"),
  cmdRows: document.querySelector("#cmd-rows"),
  cmdCount: document.querySelector("#cmd-count"),
  viewTabs: [...document.querySelectorAll('.nav-item[data-view]')],
  connectionPane: document.querySelector(".connection-pane"),
  slaveView: document.querySelector("#slave-view"),
  masterView: document.querySelector("#workspace"),
  slavePort: document.querySelector("#slave-port"),
  slaveStations: document.querySelector("#slave-stations"),
  slaveStart: document.querySelector("#slave-start"),
  slaveStop: document.querySelector("#slave-stop"),
  slaveState: document.querySelector("#slave-state"),
  slaveArea: document.querySelector("#slave-area"),
  slaveMode: document.querySelector("#slave-mode"),
  slaveFillRandom: document.querySelector("#slave-fill-random"),
  slaveReadMem: document.querySelector("#slave-read-mem"),
  slaveClearMem: document.querySelector("#slave-clear-mem"),
  slaveMemRows: document.querySelector("#slave-mem-rows"),
  slaveSetAddr: document.querySelector("#slave-set-addr"),
  slaveSetVals: document.querySelector("#slave-set-vals"),
  slaveSetBtn: document.querySelector("#slave-set-btn"),
  debugView: document.querySelector("#debug-view"),
  dbgAllowRx: document.querySelector("#dbg-allow-rx"),
  dbgAllowTx: document.querySelector("#dbg-allow-tx"),
  dbgAppendCrc: document.querySelector("#dbg-append-crc"),
  dbgFrameTimeout: document.querySelector("#dbg-frame-timeout"),
  dbgAttach: document.querySelector("#dbg-attach"),
  dbgSendMode: document.querySelector("#dbg-send-mode"),
  dbgInput: document.querySelector("#dbg-input"),
  dbgSend: document.querySelector("#dbg-send"),
  dbgClearInput: document.querySelector("#dbg-clear-input"),
  dbgLogRows: document.querySelector("#dbg-log-rows"),
  dbgClearLog: document.querySelector("#dbg-clear-log"),
  dbgCrcInput: document.querySelector("#dbg-crc-input"),
  dbgCalcCrc: document.querySelector("#dbg-calc-crc"),
  dbgCalcLrc: document.querySelector("#dbg-calc-lrc"),
  dbgChecksumResult: document.querySelector("#dbg-checksum-result"),
  parserView: document.querySelector("#parser-view"),
  parserTransport: document.querySelector("#parser-transport"),
  parserInput: document.querySelector("#parser-input"),
  parserParse: document.querySelector("#parser-parse"),
  parserClear: document.querySelector("#parser-clear"),
  parserResult: document.querySelector("#parser-result"),
  sessionTabsBar: document.querySelector("#session-tabs-bar"),
  addSessionTab: document.querySelector("#add-session-tab"),
  scanRows: document.querySelector("#scan-rows"),
  transportRadios: [...document.querySelectorAll('input[name="transport"]')],
  tcpHost: document.querySelector("#tcp-host"),
  tcpPort: document.querySelector("#tcp-port"),
  tcpUnitId: null, // 已废弃:站号统一取 #unit-id(协议层参数)
  connectTcp: document.querySelector("#connect-tcp"),
  disconnectTcp: document.querySelector("#disconnect-tcp"),
  registerResults: document.querySelector("#register-results"),
  pointCount: document.querySelector("#point-count"),
  errorCount: document.querySelector("#error-count"),
  txCount: document.querySelector("#tx-count"),
  rxCount: document.querySelector("#rx-count"),
  timeoutCount: document.querySelector("#timeout-count"),
  crcCount: document.querySelector("#crc-count"),
  traceRows: document.querySelector("#trace-rows"),
  traceCount: document.querySelector("#trace-count"),
  alarmRows: document.querySelector("#alarm-rows"),
  alarmCount: document.querySelector("#alarm-count"),
  trendPointSelect: document.querySelector("#trend-point-select"),
  trendAdd: document.querySelector("#trend-add"),
  trendClear: document.querySelector("#trend-clear"),
  trendCanvas: document.querySelector("#trend-canvas"),
  trendLegend: document.querySelector("#trend-legend"),
};

const defaults = {
  baudRate: 9600,
  dataBits: 8,
  parity: "none",
  stopBits: "1",
  flowControl: "none",
  readTimeoutMs: 1000,
  writeTimeoutMs: 1000,
  dtrMode: "preserve",
  rtsMode: "preserve",
};

let busy = false;
let tcpConnected = false;
let activePollId = null;
let activeView = "master";
const stats = {
  tx: 0,
  rx: 0,
  timeout: 0,
  crc: 0,
  errors: 0,
  traces: 0,
  alarms: 0,
};
const traceHistory = []; // 报文历史(用于导出)
let traceQuery = ""; // 报文搜索框当前关键词(空=无过滤)
const isDesktop = () => Boolean(window.nexusDesktop || window.__TAURI_INTERNALS__);
const isConnected = () =>
  elements.connectionPill.dataset.state === "open" || tcpConnected;

function updateDependentControls() {
  const handleOpen = elements.connectionPill.dataset.state === "open";
  const flowOwnsRts = elements.flowControl.value === "rts-cts";
  if (flowOwnsRts) elements.rtsMode.value = "preserve";
  elements.rtsMode.disabled = handleOpen || flowOwnsRts;
}

function setNotice(kind, title, message) {
  elements.notice.dataset.kind = kind;
  elements.notice.setAttribute("role", kind === "error" ? "alert" : "status");
  elements.notice.setAttribute("aria-live", kind === "error" ? "assertive" : "polite");
  elements.noticeTitle.textContent = title;
  elements.noticeMessage.textContent = message;
}

function syncActionState() {
  const connected = isConnected();
  elements.refresh.disabled = busy || connected;
  elements.open.disabled = busy || connected;
  elements.close.disabled = busy || !connected;
  elements.restore.disabled = busy || connected;
  elements.commandFields.disabled = busy || !connected;
  const fc = Number(elements.functionCode.value);
  const isWrite = [5, 6, 15, 16].includes(fc);
  elements.readOnce.disabled = busy || !connected || isWrite || !!activePollId;
  elements.writeOnce.disabled = busy || !connected || !isWrite || !!activePollId;
  if (elements.scanStations) elements.scanStations.disabled = busy || !connected;
  if (elements.scanBaud) elements.scanBaud.disabled = busy || !connected;
  if (elements.startPoll) elements.startPoll.disabled = busy || !connected || !!activePollId;
  if (elements.stopPoll) elements.stopPoll.disabled = !activePollId;
  if (elements.addCmd) elements.addCmd.disabled = busy || !connected;
  if (elements.clearCmd) elements.clearCmd.disabled = commandList.length === 0;
  if (elements.executeCmds) elements.executeCmds.disabled = busy || !connected || commandList.length === 0;
  updateWriteValueVisibility();
}

function updateWriteValueVisibility() {
  const fc = Number(elements.functionCode.value);
  const isWrite = [5, 6, 15, 16].includes(fc);
  if (elements.writeValueField) {
    elements.writeValueField.classList.toggle("hidden", !isWrite);
  }
  if (elements.quantity) {
    elements.quantity.parentElement.classList.toggle("hidden", isWrite && [5, 6].includes(fc));
  }
}

function currentTransport() {
  const checked = elements.transportRadios.find((r) => r.checked);
  return checked ? checked.value : "rtu";
}

function isTcpTransport(transport) {
  return ["tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"].includes(transport);
}

function updateTransportVisibility() {
  const transport = currentTransport();
  const tcpMode = isTcpTransport(transport);
  const connectionPane = document.querySelector(".connection-pane");
  if (connectionPane) connectionPane.dataset.transport = tcpMode ? "tcp" : "serial";
  // 卡片标题按模式切换:TCP 没有串口,标题不应叫"串口配置"
  const connTitle = document.querySelector("#connection-title");
  if (connTitle) connTitle.textContent = tcpMode ? "TCP 配置" : "串口配置";
  // 扫描站号:TCP 和串口都可用(TCP 走 Rust 扫描,串口走 Electron 逐站探测)
  if (elements.scanStations) elements.scanStations.disabled = busy;
  if (elements.scanBaud) elements.scanBaud.disabled = busy || tcpMode; // 波特率扫描仅串口
  // TCP 连接按钮
  if (elements.connectTcp) elements.connectTcp.disabled = busy || !tcpMode || tcpConnected;
  if (elements.disconnectTcp) elements.disconnectTcp.disabled = busy || !tcpMode || !tcpConnected;
  // 串口打开/关闭按钮:TCP 模式禁用
  if (elements.open) elements.open.disabled = busy || tcpMode || isConnected();
  if (elements.close) elements.close.disabled = busy || tcpMode || !isConnected();
}

async function connectTcp() {
  if (busy) return; // 在途时禁止重入:双开连接会撕裂状态
  const transport = currentTransport();
  const host = elements.tcpHost?.value?.trim() || "127.0.0.1";
  const port = Number(elements.tcpPort?.value) || 502;
  // 站号是协议层参数,统一取读写定义区的 #unit-id(不再有独立 TCP 站号字段)
  const unitId = Number(elements.unitId?.value) || 1;
  if (!host) {
    setNotice("error", "参数无效", "请输入主机地址。");
    return;
  }
  setBusy(true);
  try {
    const framing = transport === "tcp" ? "standard" : transport === "rtu-over-tcp" ? "rtu-over-tcp" : "ascii-over-tcp";
    const cmd = transport === "udp" ? "open_udp_connection" : "open_tcp_connection";
    await callBackend(cmd, {
      connectionId: "default",
      host,
      port,
      unitId,
      framing,
    });
    tcpConnected = true;
    elements.connectionPill.dataset.state = "open";
    elements.connectionLabel.textContent = `${transport.toUpperCase()} ${host}:${port}`;
    elements.commandState.textContent = commandReadyText();
    setNotice("success", "已连接", `${transport.toUpperCase()} ${host}:${port} 站号 ${unitId}`);
    renderAdvFcParams(); // 解锁高级 FC 执行按钮
    persistConfig(); // 保存连接配置
  } catch (error) {
    setNotice("error", "连接失败", error.message || String(error));
  } finally {
    setBusy(false);
    updateTransportVisibility();
    syncActionState();
  }
}

async function disconnectTcp() {
  if (busy) return;
  // 断开前必须停轮询,否则调度器继续对已关闭连接 tick → 错误风暴
  await stopPoll().catch(() => {});
  setBusy(true);
  try {
    await callBackend("close_connection", { connectionId: "default" });
    tcpConnected = false;
    elements.connectionPill.dataset.state = "closed";
    elements.connectionLabel.textContent = "未连接";
    elements.commandState.textContent = "请先连接";
    setNotice("info", "已断开", "TCP 连接已关闭。");
  } catch (error) {
    setNotice("error", "断开失败", error.message || String(error));
  } finally {
    setBusy(false);
    updateTransportVisibility();
    syncActionState();
  }
}

// === 扫描站号 ===

async function scanStations() {
  if (busy || !isConnected()) return;
  const transport = currentTransport();
  const isTcp = isTcpTransport(transport);
  setBusy(true);
  elements.commandState.textContent = "正在扫描站号 1-247";
  setNotice("info", "扫描中", "正在扫描站号 1-247,请等待...");
  try {
    let result;
    if (isTcp) {
      result = await callBackend("scan_station_ids", {
        connectionId: "default",
        rangeStart: 1,
        rangeEnd: 247,
        timeoutMs: 500,
      });
    } else {
      // 串口模式:Electron 侧逐站号发 FC03 探测
      result = await callBackend("scan_serial_stations", {
        rangeStart: 1,
        rangeEnd: 247,
        timeoutMs: 300,
      });
    }
    const found = result?.found ?? [];
    if (found.length > 0) {
      setNotice("success", `发现 ${found.length} 个在线从站`, found.map((s) => `#${s.stationId}`).join(", "));
      renderScanResults(found);
    } else {
      setNotice("info", "未发现在线从站", `扫描了 ${result?.scanned ?? 247} 个站号,均无响应。`);
    }
  } catch (error) {
    setNotice("error", "扫描失败", error.message || String(error));
  } finally {
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
    syncActionState();
  }
}

function renderScanResults(found) {
  if (!elements.scanRows) return;
  elements.scanRows.replaceChildren();
  for (const station of found) {
    const row = document.createElement("tr");
    appendCells(row, [
      station.stationId,
      "—",
      "—",
      `${station.firstResponseMs ?? 0} ms`,
      "FC03",
      "在线",
      `<button onclick="document.querySelector('#unit-id').value=${station.stationId}">选用</button>`,
    ]);
    elements.scanRows.append(row);
  }
}

// === 轮询 ===

async function startPoll() {
  if (busy || !isConnected() || activePollId) return;
  let command;
  try {
    command = readCommand();
  } catch (error) {
    setNotice("error", "参数无效", error.message);
    return;
  }
  const intervalMs = Number(elements.pollInterval?.value) || 1000;
  if (intervalMs < 50 || intervalMs > 60000) {
    setNotice("error", "参数无效", "轮询间隔必须在 50 到 60000 毫秒之间。");
    return;
  }
  const transport = currentTransport();
  setBusy(true);
  try {
    const result = await callBackend("start_poll", {
      transport,
      connectionId: "default",
      unitId: command.unitId,
      fc: command.functionCode,
      startAddress: command.startAddress,
      quantity: command.quantity,
      intervalMs,
      dataType: elements.displayType?.value || "Unsigned16",
    });
    activePollId = result?.pollId ?? null;
    if (activePollId) {
      setBusy(false); // 轮询期间不占用 busy 锁,允许其他操作
      elements.commandState.textContent = `轮询中 (${intervalMs}ms)`;
      setNotice("success", "轮询已启动", `pollId: ${activePollId},间隔 ${intervalMs}ms`);
      // 点表非空时同时启动点表轮询
      if (pointTable.length > 0) {
        startPointPoll(intervalMs);
        setNotice("info", "点表采集已启动", `${pointTable.length} 个点位进入周期采集`);
      }
      syncPollState();
    }
  } catch (error) {
    setNotice("error", "启动轮询失败", error.message || String(error));
    setBusy(false);
    syncPollState();
  }
}

async function stopPoll() {
  // 停止点表轮询
  if (pointPollTimer) {
    clearInterval(pointPollTimer);
    pointPollTimer = null;
  }
  if (!activePollId) {
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
    syncPollState();
    return;
  }
  try {
    await callBackend("stop_poll", { pollId: activePollId });
    setNotice("info", "轮询已停止", `pollId: ${activePollId}`);
  } catch (error) {
    // 忽略错误
  }
  activePollId = null;
  elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
  syncPollState();
}

/** 点表轮询:把点表合并为批量读取计划,每批发一次事务并更新表格 */
let pointPollInFlight = false;
async function pollPointTableTick() {
  if (pointPollInFlight) return; // #14: 重入保护(上一轮未完成时跳过)
  pointPollInFlight = true;
  try {
  if (!isConnected() || pointTable.length === 0) return;
  const updatedAt = clockTime();
  // 1. 合并同站号/同FC/地址连续(或间隙≤0)的点位为批量读取批次
  const batches = buildPollPlan(pointTable);
  for (const batch of batches) {
    const fc = batch.fc;
    const isCoils = fc === 1 || fc === 2;
    const prefix = fc === 1 ? "C" : fc === 2 ? "DI" : fc === 4 ? "IR" : "HR";
    const args = {
      unitId: batch.unitId,
      startAddress: batch.startAddress,
      address: batch.startAddress,
      quantity: batch.quantity,
      timeoutMs: 1000,
      transport: currentTransport(),
    };
    try {
      let result;
      if (fc === 1) result = await callBackend("read_coils_once", args);
      else if (fc === 2) result = await callBackend("read_discrete_inputs_once", args);
      else if (fc === 4) result = await callBackend("read_input_registers_once", args);
      else result = await callBackend("read_holding_registers_once", args);

      const values = isCoils ? result?.coils : result?.registers;
      if (!Array.isArray(values)) continue;

      // 2. 把批量结果按地址偏移拆回各点位的值切片
      const slices = splitBatchResult(batch, pointTable, values);
      for (const [pointIdx, pointValues] of slices) {
        const point = pointTable[pointIdx];
        if (!point) continue;
        for (let i = 0; i < pointValues.length; i++) {
          const address = point.address + i;
          const rowKey = `reg-${prefix}-${address}`;
          const row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
          if (!row) continue;
          const cells = row.querySelectorAll("td");
          if (cells.length < 11) continue;
          let displayValue;
          if (isCoils) {
            displayValue = pointValues[i] ? "ON" : "OFF";
          } else {
            displayValue = String(pointValues[i]);
            // 应用倍率
            const scale = Number(point.scale);
            if (scale && scale !== 1) {
              const num = Number(pointValues[i]);
              if (!Number.isNaN(num)) displayValue = (num * scale).toFixed(3).replace(/\.?0+$/, "");
            }
            if (point.unit) displayValue += ` ${point.unit}`;
          }
          cells[8].textContent = displayValue;
          cells[9].textContent = "Good";
          cells[9].className = "quality-good";
          cells[10].textContent = updatedAt;
          if (!isCoils) {
            // 趋势采集: 喂入应用倍率后的数值(trendFeed 内部跳过 NaN)
            let numeric = Number(pointValues[i]);
            const scale = Number(point.scale);
            if (scale && scale !== 1) numeric *= scale;
            trendFeed(rowKey, numeric);
          }
        }
      }
    } catch {
      // 批次读取失败:仅将该批次覆盖的地址区间标记为 Bad(跨界点位不波及其他批次)
      for (let addr = batch.startAddress; addr < batch.startAddress + batch.quantity; addr++) {
        const rowKey = `reg-${prefix}-${addr}`;
        const row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
        if (!row) continue;
        const cells = row.querySelectorAll("td");
        if (cells.length >= 11) {
          cells[9].textContent = "Bad";
          cells[9].className = "";
          cells[10].textContent = updatedAt;
        }
      }
    }
  }
  } finally {
    pointPollInFlight = false;
  }
}

/** 启动点表轮询 */
function startPointPoll(intervalMs) {
  if (pointPollTimer) clearInterval(pointPollTimer);
  pointPollTimer = setInterval(pollPointTableTick, intervalMs);
  // 立即执行一次
  pollPointTableTick();
}

// === 实时趋势图 ===

const TREND_WINDOW_MS = 60000; // X 轴时间窗口: 最近 60 秒
const TREND_COLORS = ["#111111", "#1A7F45", "#9A6B00", "#D52B1E", "#555555", "#3366AA"]; // mono 曲线颜色池
/** trendId(表格行 key, 如 "reg-HR-0") -> { name, color, dataPoints: [{t, v}], maxPoints } */
const trendSeries = new Map();
let trendRafId = null;
let trendLastDrawAt = 0;
let trendCanvasW = 0; // 上次绘制时的 CSS 尺寸(用于空状态下检测 resize)
let trendCanvasH = 0;

/** 轮询更新表格值后调用: 地址在追踪集合中且数值可解析时推入数据点 */
function trendFeed(rowKey, value) {
  if (trendSeries.size === 0) return;
  const series = trendSeries.get(rowKey);
  if (!series) return;
  const numeric = Number(value);
  if (Number.isNaN(numeric)) return;
  const now = Date.now();
  series.dataPoints.push({ t: now, v: numeric });
  // 裁剪: 超出时间窗的旧点 + 超出点数上限
  const cutoff = now - TREND_WINDOW_MS - 2000;
  while (series.dataPoints.length > 0 && series.dataPoints[0].t < cutoff) series.dataPoints.shift();
  if (series.dataPoints.length > series.maxPoints) {
    series.dataPoints.splice(0, series.dataPoints.length - series.maxPoints);
  }
}

/** 刷新点位下拉框: 列出寄存器表格中的数值型点位(保持/输入寄存器) */
function trendRefreshPointOptions() {
  const select = elements.trendPointSelect;
  if (!select) return;
  const previous = select.value;
  select.replaceChildren(new Option("选择点位…", ""));
  const rows = elements.registerResults.querySelectorAll('tr[data-key^="reg-HR-"], tr[data-key^="reg-IR-"]');
  for (const row of rows) {
    const cells = row.querySelectorAll("td");
    if (cells.length < 11) continue;
    const rowKey = row.dataset.key;
    const name = cells[1].textContent.trim() || rowKey;
    const areaAddr = rowKey.replace(/^reg-/, "").replace("-", " ");
    const option = new Option(name === areaAddr ? name : `${name}（${areaAddr}）`, rowKey);
    option.disabled = trendSeries.has(rowKey); // 已添加的点位置灰, 防重复
    select.append(option);
  }
  if ([...select.options].some((o) => o.value === previous)) select.value = previous;
}

/** 添加选中的点位为一条趋势曲线 */
function trendAddSelected() {
  const rowKey = elements.trendPointSelect?.value;
  if (!rowKey) {
    setNotice("error", "未选择点位", "请先从下拉框选择一个数值型点位。");
    return;
  }
  if (trendSeries.has(rowKey)) {
    setNotice("info", "已存在", "该点位已在趋势图中。");
    return;
  }
  const row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
  const name = row?.querySelectorAll("td")[1]?.textContent.trim() || rowKey;
  const usedColors = new Set([...trendSeries.values()].map((s) => s.color));
  const color = TREND_COLORS.find((c) => !usedColors.has(c)) ?? TREND_COLORS[trendSeries.size % TREND_COLORS.length];
  trendSeries.set(rowKey, { name, color, dataPoints: [], maxPoints: 300 });
  renderTrendLegend();
  trendRefreshPointOptions();
  elements.trendPointSelect.value = ""; // 复位到占位项, 避免停在已添加的 disabled 选项上
  setNotice("success", "已添加曲线", `${name}（等待轮询数据…）`);
}

function trendRemoveSeries(rowKey) {
  if (!trendSeries.delete(rowKey)) return;
  renderTrendLegend();
  trendRefreshPointOptions();
  if (trendSeries.size === 0) drawTrendChart(); // 回到空状态
}

function trendClearAll() {
  if (trendSeries.size === 0) return;
  trendSeries.clear();
  renderTrendLegend();
  trendRefreshPointOptions();
  drawTrendChart();
  setNotice("info", "已清空", "所有趋势曲线已移除。");
}

/** HTML 图例(画布左上角覆盖层): 色块 + 名称 + 最新值 + × 删除按钮 */
function renderTrendLegend() {
  const legend = elements.trendLegend;
  if (!legend) return;
  legend.replaceChildren();
  for (const [rowKey, series] of trendSeries) {
    const chip = document.createElement("span");
    chip.className = "trend-chip";
    chip.dataset.key = rowKey;
    const swatch = document.createElement("span");
    swatch.className = "trend-swatch";
    swatch.style.background = series.color;
    const label = document.createElement("span");
    label.className = "trend-chip-name";
    label.textContent = series.name;
    const value = document.createElement("span");
    value.className = "trend-chip-val";
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "trend-remove";
    remove.textContent = "×";
    remove.setAttribute("aria-label", `移除曲线 ${series.name}`);
    remove.addEventListener("click", () => trendRemoveSeries(rowKey));
    chip.append(swatch, label, value, remove);
    legend.append(chip);
  }
}

function trendFormatTick(v) {
  const abs = Math.abs(v);
  if (abs >= 10000) return v.toExponential(1);
  if (abs >= 100) return v.toFixed(0);
  if (abs >= 1) return String(Math.round(v * 10) / 10);
  return Number(v.toPrecision(2)).toString();
}

function trendFormatTime(t) {
  const d = new Date(t);
  const mm = String(d.getMinutes()).padStart(2, "0");
  const ss = String(d.getSeconds()).padStart(2, "0");
  return `${mm}:${ss}`;
}

/** 绘制趋势图: 统一 Y 轴 / 最近 60s 滚动窗口 / HiDPI 适配 */
function drawTrendChart() {
  const canvas = elements.trendCanvas;
  if (!canvas) return;
  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth;
  const cssH = canvas.clientHeight;
  if (cssW === 0 || cssH === 0) return;
  // HiDPI: 按 devicePixelRatio 放大 backing store, 避免模糊
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }
  trendCanvasW = cssW;
  trendCanvasH = cssH;
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);
  // 先设字体再 measureText, 保证右侧 gutter 宽度正确
  ctx.font = '10px "Cascadia Mono", "Consolas", monospace';
  ctx.textBaseline = "middle";

  // 空状态
  if (trendSeries.size === 0) {
    ctx.font = '12px "Manrope", "Microsoft YaHei UI", sans-serif';
    ctx.fillStyle = "#999999";
    ctx.textAlign = "center";
    ctx.fillText("添加曲线后开始绘制", cssW / 2, cssH / 2);
    ctx.textAlign = "left";
    return;
  }

  const now = Date.now();
  const tStart = now - TREND_WINDOW_MS;

  // 统一 Y 轴: 窗口内所有曲线共享 min/max
  let min = Infinity;
  let max = -Infinity;
  for (const series of trendSeries.values()) {
    for (const p of series.dataPoints) {
      if (p.t < tStart) continue;
      if (p.v < min) min = p.v;
      if (p.v > max) max = p.v;
    }
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) { min = 0; max = 1; }
  if (min === max) { min -= 1; max += 1; }
  const padY = (max - min) * 0.08;
  min -= padY;
  max += padY;

  // 布局: 右侧 Y 轴数值 gutter + 底部时间刻度
  const labelTexts = [];
  for (let i = 0; i <= 5; i++) labelTexts.push(trendFormatTick(max - ((max - min) * i) / 5));
  let gutterR = 0;
  for (const text of labelTexts) gutterR = Math.max(gutterR, ctx.measureText(text).width);
  gutterR += 12;
  const plot = { x: 8, y: 8, w: cssW - 8 - gutterR, h: cssH - 8 - 16 };
  if (plot.w < 40 || plot.h < 30) return;

  const xOf = (t) => plot.x + ((t - tStart) / TREND_WINDOW_MS) * plot.w;
  const yOf = (v) => plot.y + (1 - (v - min) / (max - min)) * plot.h;

  // 网格: 每 20% 一条浅色横线, 数值标在右侧
  ctx.strokeStyle = "rgba(0, 0, 0, 0.07)";
  ctx.fillStyle = "#999999";
  ctx.lineWidth = 1;
  for (let i = 0; i <= 5; i++) {
    const y = plot.y + (plot.h * i) / 5;
    ctx.beginPath();
    ctx.moveTo(plot.x, y + 0.5);
    ctx.lineTo(plot.x + plot.w, y + 0.5);
    ctx.stroke();
    ctx.fillText(labelTexts[i], plot.x + plot.w + 6, y);
  }
  // X 轴时间刻度(底部 4 个; 两端贴边对齐, 避免居中绘制时文本裁出画布)
  for (let i = 0; i <= 3; i++) {
    const t = tStart + (TREND_WINDOW_MS * i) / 3;
    const label = i === 3 ? "现在" : trendFormatTime(t);
    if (i === 0) ctx.textAlign = "left";
    else if (i === 3) ctx.textAlign = "right";
    else ctx.textAlign = "center";
    ctx.fillText(label, plot.x + (plot.w * i) / 3, plot.y + plot.h + 8);
  }
  ctx.textAlign = "left";
  // 绘图区边框
  ctx.strokeStyle = "rgba(0, 0, 0, 0.16)";
  ctx.strokeRect(plot.x + 0.5, plot.y + 0.5, plot.w - 1, plot.h - 1);

  // 曲线(裁剪到绘图区内)
  ctx.save();
  ctx.beginPath();
  ctx.rect(plot.x, plot.y, plot.w, plot.h);
  ctx.clip();
  for (const series of trendSeries.values()) {
    ctx.strokeStyle = series.color;
    ctx.fillStyle = series.color;
    ctx.lineWidth = 1.5;
    ctx.lineJoin = "round";
    ctx.beginPath();
    let visible = 0;
    let lastX = 0;
    let lastY = 0;
    for (const p of series.dataPoints) {
      if (p.t < tStart) continue;
      const x = xOf(p.t);
      const y = yOf(p.v);
      if (visible === 0) ctx.moveTo(x, y);
      else ctx.lineTo(x, y);
      visible += 1;
      lastX = x;
      lastY = y;
    }
    if (visible > 1) {
      ctx.stroke();
    } else if (visible === 1) {
      // 单点曲线画圆点, 否则不可见
      ctx.beginPath();
      ctx.arc(lastX, lastY, 2.5, 0, Math.PI * 2);
      ctx.fill();
    }
  }
  ctx.restore();

  // 图例上的最新值
  for (const [rowKey, series] of trendSeries) {
    const chipValue = elements.trendLegend?.querySelector(`.trend-chip[data-key="${rowKey}"] .trend-chip-val`);
    if (!chipValue) continue;
    const last = series.dataPoints[series.dataPoints.length - 1];
    chipValue.textContent = last ? trendFormatTick(last.v) : "—";
  }
}

/** 绘制循环: rAF + 100ms 节流, 仅主站视图可见且页面在前台时绘制 */
function trendLoop(timestamp) {
  trendRafId = requestAnimationFrame(trendLoop);
  if (timestamp - trendLastDrawAt < 100) return;
  trendLastDrawAt = timestamp;
  if (activeView !== "master" || document.hidden) return;
  const canvas = elements.trendCanvas;
  if (!canvas || !canvas.isConnected) return;
  // 无曲线时只在画布尺寸变化时重绘空状态
  if (trendSeries.size === 0 && canvas.clientWidth === trendCanvasW && canvas.clientHeight === trendCanvasH) return;
  drawTrendChart();
}

function startTrendLoop() {
  if (trendRafId !== null) return;
  trendRafId = requestAnimationFrame(trendLoop);
}

// === 示例代码生成 ===

let codeLang = "rust";

const CODE_LANG_LABEL = { rust: "Rust", csharp: "C#", python: "Python" };

const CODE_TRANSPORT_LABEL = {
  rtu: "RTU 串口",
  ascii: "ASCII 串口",
  tcp: "TCP",
  udp: "UDP",
  "rtu-over-tcp": "RTU/TCP",
  "ascii-over-tcp": "ASCII/TCP",
};

const CODE_FC = {
  1: { read: true, bits: true, label: "读线圈", rustBuild: "build_read_coils_pdu", rustParse: "parse_read_coils_response", csharp: "ReadCoils", python: "read_coils" },
  2: { read: true, bits: true, label: "读离散输入", rustBuild: "build_read_discrete_inputs_pdu", rustParse: "parse_read_discrete_inputs_response", csharp: "ReadDiscreteInputs", python: "read_discrete_inputs" },
  3: { read: true, bits: false, label: "读保持寄存器", rustBuild: "build_read_holding_registers_pdu", rustParse: "parse_read_holding_registers_response", csharp: "ReadHoldingRegisters", python: "read_holding_registers" },
  4: { read: true, bits: false, label: "读输入寄存器", rustBuild: "build_read_input_registers_pdu", rustParse: "parse_read_input_registers_response", csharp: "ReadInputRegisters", python: "read_input_registers" },
  5: { read: false, label: "写单线圈", rustBuild: "build_write_single_coil_pdu", rustParse: "parse_write_single_coil_response", csharp: "WriteSingleCoil", python: "write_coil" },
  6: { read: false, label: "写单寄存器", rustBuild: "build_write_single_register_pdu", rustParse: "parse_write_single_register_response", csharp: "WriteSingleRegister", python: "write_register" },
  15: { read: false, label: "写多线圈", rustBuild: "build_write_multiple_coils_pdu", rustParse: "parse_write_multiple_coils_response", csharp: "WriteMultipleCoils", python: "write_coils" },
  16: { read: false, label: "写多寄存器", rustBuild: "build_write_multiple_registers_pdu", rustParse: "parse_write_multiple_registers_response", csharp: "WriteMultipleRegisters", python: "write_registers" },
};

/** 读取当前 UI 配置(供代码模板使用;字段引用与 readCommand 保持一致,1 基地址减 1) */
function codeSampleConfig() {
  const el = (id) => document.querySelector(id);
  const transport = currentTransport();
  const tcp = isTcpTransport(transport);
  const fc = CODE_FC[Number(elements.functionCode?.value)] ? Number(elements.functionCode.value) : 3;
  const rawAddr = String(elements.startAddress?.value ?? "0").trim();
  let startAddress = /^0x[\da-f]+$/i.test(rawAddr) ? parseInt(rawAddr, 16) : Number(rawAddr) || 0;
  if (startAddress < 0 || startAddress > 65535) startAddress = 0;
  const addressBase = Number(el("#address-base")?.value ?? 0);
  if (addressBase === 1 && startAddress > 0) startAddress -= 1;
  return {
    transport,
    isTcp: tcp,
    fc,
    startAddress,
    quantity: Math.max(1, Number(elements.quantity?.value) || 1),
    displayType: el("#display-type")?.value || "Unsigned16",
    writeRaw: elements.writeValue?.value?.trim() || "",
    unitId: Number(el("#unit-id")?.value) || 1,
    host: el("#tcp-host")?.value?.trim() || "127.0.0.1",
    port: Number(el("#tcp-port")?.value) || 502,
    portName: el("#port-name")?.value || "COM3",
    baudRate: Number(el("#baud-rate")?.value) || 9600,
    parity: el("#parity")?.value || "none",
    dataBits: Number(el("#data-bits")?.value) || 8,
    stopBits: el("#stop-bits")?.value || "1",
  };
}

/** 写入值解析:界面"写入值"为空时给占位示例值 */
function codeWriteValues(cfg) {
  const raw = cfg.writeRaw;
  if (cfg.fc === 5) {
    return { placeholder: !raw, bool: raw ? /^(true|1|on)$/i.test(raw) : true };
  }
  if (cfg.fc === 6) {
    const n = Number(raw);
    return { placeholder: !raw, int: raw && Number.isInteger(n) ? n : 100 };
  }
  const parts = raw ? raw.split(/[,\s]+/).filter(Boolean) : [];
  if (cfg.fc === 15) {
    return {
      placeholder: parts.length === 0,
      bools: parts.length ? parts.map((p) => /^(true|1|on)$/i.test(p)) : [true, false, true, true],
    };
  }
  return {
    placeholder: parts.length === 0,
    ints: parts.length ? parts.map((p) => Number(p) || 0) : [100, 200, 300, 400],
  };
}

function generateSampleCode(lang) {
  const cfg = codeSampleConfig();
  if (lang === "csharp") return csharpCodeSample(cfg);
  if (lang === "python") return pythonCodeSample(cfg);
  return rustCodeSample(cfg);
}

// ── Rust 模板(nexus-rust-core) ──

const RUST_DATA_BITS = { 8: "Eight", 7: "Seven", 6: "Six", 5: "Five" };
const RUST_PARITY = { none: "None", even: "Even", odd: "Odd" };
const RUST_STOP_BITS = { 1: "One", 2: "Two" };
const RUST_TCP_NOTE = {
  tcp: "标准 Modbus TCP(MBAP 帧)",
  udp: "标准 Modbus UDP(MBAP 帧,无连接;open_udp 仅绑定本地并设定对端)",
  "rtu-over-tcp": "RTU over TCP(TCP 通道传输完整 RTU 帧,含 CRC16,无 MBAP 头)",
  "ascii-over-tcp": "ASCII over TCP(TCP 通道传输 ASCII 帧,LRC 校验,无 MBAP 头)",
};
const RUST_FRAMING = { tcp: "Standard", udp: "Standard", "rtu-over-tcp": "RtuOverTcp", "ascii-over-tcp": "AsciiOverTcp" };

function rustCodeSample(cfg) {
  const meta = CODE_FC[cfg.fc];
  const fcTag = `FC${String(cfg.fc).padStart(2, "0")}`;
  const end = cfg.startAddress + cfg.quantity - 1;
  const asciiSerial = cfg.transport === "ascii";
  const esc = (s) => String(s).replace(/\\/g, "\\\\").replace(/"/g, '\\"');

  // PDU 构建 / 解析(各传输路径共用,响应统一收进 resp)
  const pdu = [];
  const parse = [];
  if (meta.read) {
    const typeNote = meta.bits ? "" : `,解码类型 ${cfg.displayType}`;
    pdu.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},数量 ${cfg.quantity}${typeNote}`);
    pdu.push(`let pdu = modbus_pdu::${meta.rustBuild}(${cfg.startAddress}, ${cfg.quantity})?;`);
    parse.push(`let values = modbus_pdu::${meta.rustParse}(&resp, ${cfg.quantity})?;`);
    parse.push(`println!("地址 ${cfg.startAddress}..${end} = {values:?}");`);
  } else {
    const w = codeWriteValues(cfg);
    const note = w.placeholder ? "(界面未填写入值,以下为占位示例值)" : "";
    if (cfg.fc === 5) {
      pdu.push(`// ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${w.bool}${note}`);
      pdu.push(`let pdu = modbus_pdu::${meta.rustBuild}(${cfg.startAddress}, ${w.bool})?;`);
      parse.push(`let (addr, on) = modbus_pdu::${meta.rustParse}(&resp)?;`);
      parse.push(`println!("已写入: 地址 {addr} = {on}");`);
    } else if (cfg.fc === 6) {
      pdu.push(`// ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${w.int}${note}`);
      pdu.push(`let pdu = modbus_pdu::${meta.rustBuild}(${cfg.startAddress}, ${w.int})?;`);
      parse.push(`let (addr, val) = modbus_pdu::${meta.rustParse}(&resp)?;`);
      parse.push(`println!("已写入: 地址 {addr} = {val}");`);
    } else if (cfg.fc === 15) {
      pdu.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.bools.length} 个线圈${note}`);
      pdu.push(`let values = [${w.bools.join(", ")}];`);
      pdu.push(`let pdu = modbus_pdu::${meta.rustBuild}(${cfg.startAddress}, &values)?;`);
      parse.push(`let (addr, qty) = modbus_pdu::${meta.rustParse}(&resp)?;`);
      parse.push(`println!("已写入 {qty} 个线圈,起始地址 {addr}");`);
    } else {
      pdu.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.ints.length} 个寄存器${note}`);
      pdu.push(`let values: [u16; ${w.ints.length}] = [${w.ints.join(", ")}];`);
      pdu.push(`let pdu = modbus_pdu::${meta.rustBuild}(${cfg.startAddress}, &values)?;`);
      parse.push(`let (addr, qty) = modbus_pdu::${meta.rustParse}(&resp)?;`);
      parse.push(`println!("已写入 {qty} 个寄存器,起始地址 {addr}");`);
    }
  }

  const L = [];
  if (cfg.isTcp) {
    const openFn = cfg.transport === "udp" ? "open_udp" : "open_tcp";
    const transactFn = cfg.transport === "udp" ? "transact_udp" : "transact_tcp";
    L.push(`// Nexus Modbus — ${fcTag} ${meta.label} (${CODE_TRANSPORT_LABEL[cfg.transport]})`);
    L.push(`// 依赖: nexus-rust-core = { path = "../rust-core" }`);
    L.push(`use nexus_rust_core::modbus_pdu;`);
    L.push(`use nexus_rust_core::session::{Session, TcpFraming};`);
    L.push(``);
    L.push(`fn main() -> Result<(), Box<dyn std::error::Error>> {`);
    L.push(`    let mut session = Session::new();`);
    L.push(`    // 站号 ${cfg.unitId},${RUST_TCP_NOTE[cfg.transport]}`);
    L.push(`    session.${openFn}("plc", "${esc(cfg.host)}", ${cfg.port}, ${cfg.unitId}, TcpFraming::${RUST_FRAMING[cfg.transport]})?;`);
    L.push(``);
    for (const line of pdu) L.push(`    ${line}`);
    L.push(`    let resp = session.${transactFn}("plc", &pdu)?;`);
    for (const line of parse) L.push(`    ${line}`);
    L.push(``);
    L.push(`    session.close_connection("plc")?;`);
    L.push(`    Ok(())`);
    L.push(`}`);
    return L.join("\n");
  }

  const serialFormat = `${cfg.baudRate} ${cfg.dataBits}${parityLetter(cfg.parity)}${cfg.stopBits}`;
  const formatNote = asciiSerial ? "(ASCII 从站常见 7E1 格式,请按设备规格调整)" : "";
  L.push(`// Nexus Modbus — ${fcTag} ${meta.label} (${CODE_TRANSPORT_LABEL[cfg.transport]})`);
  L.push(`// 依赖: nexus-rust-core = { path = "../rust-core" }, serialport = "4"`);
  L.push(`use nexus_rust_core::modbus_pdu;`);
  L.push(asciiSerial
    ? `use nexus_rust_core::modbus_ascii;`
    : `use nexus_rust_core::modbus_rtu::{RtuFrame, RtuFrameRole};`);
  L.push(`use std::io::{Read, Write};`);
  L.push(`use std::time::Duration;`);
  L.push(``);
  L.push(`fn main() -> Result<(), Box<dyn std::error::Error>> {`);
  L.push(`    // 串口参数: ${esc(cfg.portName)} ${serialFormat}${formatNote}`);
  L.push(`    let mut port = serialport::new("${esc(cfg.portName)}", ${cfg.baudRate})`);
  L.push(`        .data_bits(serialport::DataBits::${RUST_DATA_BITS[cfg.dataBits] ?? "Eight"})`);
  L.push(`        .parity(serialport::Parity::${RUST_PARITY[cfg.parity] ?? "None"})`);
  L.push(`        .stop_bits(serialport::StopBits::${RUST_STOP_BITS[cfg.stopBits] ?? "One"})`);
  L.push(`        .timeout(Duration::from_millis(1000))`);
  L.push(`        .open()?;`);
  L.push(``);
  if (asciiSerial) {
    L.push(`    // ASCII 帧(':' 起始 + LRC + CRLF 结尾)由 modbus_ascii 封装,经串口层收发`);
    for (const line of pdu) L.push(`    ${line}`);
    L.push(`    let frame = modbus_ascii::build_ascii_frame(${cfg.unitId}, &pdu);`);
    L.push(`    port.write_all(&frame)?;`);
    L.push(`    port.flush()?;`);
    L.push(``);
    L.push(`    let mut buf = [0u8; 512];`);
    L.push(`    let n = port.read(&mut buf)?;`);
    L.push(`    let (_unit, resp) = modbus_ascii::parse_ascii_frame(&buf[..n])?;`);
    for (const line of parse) L.push(`    ${line}`);
  } else {
    L.push(`    // build_*_pdu 生成含功能码的 PDU;站号与 CRC16 由 RtuFrame 封装,经串口层收发`);
    for (const line of pdu) L.push(`    ${line}`);
    L.push(`    let frame = RtuFrame::request(${cfg.unitId}, pdu[0], &pdu[1..])?;`);
    L.push(`    port.write_all(&frame.encode())?;`);
    L.push(`    port.flush()?;`);
    L.push(``);
    L.push(`    // RTU 以 3.5 字符静默分帧;此处简化为单次 read,生产代码应循环拼帧`);
    L.push(`    let mut buf = [0u8; 256];`);
    L.push(`    let n = port.read(&mut buf)?;`);
    L.push(`    let resp_frame = RtuFrame::decode(&buf[..n], RtuFrameRole::Response)?;`);
    L.push(`    // 重组含功能码的响应 PDU,交给解析器`);
    L.push(`    let mut resp = vec![resp_frame.function_code()];`);
    L.push(`    resp.extend_from_slice(resp_frame.data());`);
    for (const line of parse) L.push(`    ${line}`);
  }
  L.push(`    Ok(())`);
  L.push(`}`);
  return L.join("\n");
}

// ── C# 模板(Nexus.Modbus,对标 WPF 版风格) ──

const CSHARP_PARITY = { none: "None", even: "Even", odd: "Odd" };
const CSHARP_STOP_BITS = { 1: "One", 2: "Two" };
const CSHARP_FRAMING = { "rtu-over-tcp": "RtuOverTcp", "ascii-over-tcp": "AsciiOverTcp" };

function csharpCodeSample(cfg) {
  const meta = CODE_FC[cfg.fc];
  const fcTag = `FC${String(cfg.fc).padStart(2, "0")}`;
  const end = cfg.startAddress + cfg.quantity - 1;
  const esc = (s) => String(s).replace(/\\/g, "\\\\").replace(/"/g, '\\"');
  const L = [];
  L.push(`// Nexus.Modbus — ${fcTag} ${meta.label} (${CODE_TRANSPORT_LABEL[cfg.transport]})`);
  L.push(`// Install-Package Nexus.Modbus`);
  if (!cfg.isTcp) L.push(`using System.IO.Ports;`);
  L.push(`using Nexus.Modbus;`);
  L.push(``);
  if (cfg.isTcp) {
    if (cfg.transport === "tcp") {
      L.push(`// 站号 ${cfg.unitId},标准 Modbus TCP(MBAP 帧)`);
      L.push(`using var client = new ModbusTcpClient("${esc(cfg.host)}", ${cfg.port}, unitId: ${cfg.unitId});`);
    } else if (cfg.transport === "udp") {
      L.push(`// 站号 ${cfg.unitId},Modbus UDP(MBAP 帧,无连接语义)`);
      L.push(`using var client = new ModbusUdpClient("${esc(cfg.host)}", ${cfg.port}, unitId: ${cfg.unitId});`);
    } else {
      const framing = CSHARP_FRAMING[cfg.transport];
      L.push(`// 站号 ${cfg.unitId},${CODE_TRANSPORT_LABEL[cfg.transport]}: TCP 通道传输串口风格帧,无 MBAP 头`);
      L.push(`using var client = new ModbusTcpClient("${esc(cfg.host)}", ${cfg.port}, unitId: ${cfg.unitId})`);
      L.push(`{`);
      L.push(`    Framing = ModbusFraming.${framing},`);
      L.push(`};`);
    }
  } else {
    const cls = cfg.transport === "ascii" ? "ModbusAsciiClient" : "ModbusRtuClient";
    const serialFormat = `${cfg.baudRate} ${cfg.dataBits}${parityLetter(cfg.parity)}${cfg.stopBits}`;
    L.push(`// 串口参数: ${esc(cfg.portName)} ${serialFormat},站号 ${cfg.unitId}`);
    L.push(`using var client = new ${cls}("${esc(cfg.portName)}", ${cfg.baudRate}, Parity.${CSHARP_PARITY[cfg.parity] ?? "None"}, ${cfg.dataBits}, StopBits.${CSHARP_STOP_BITS[cfg.stopBits] ?? "One"}, unitId: ${cfg.unitId});`);
  }
  L.push(`client.Connect();`);
  L.push(``);
  if (meta.read) {
    const typeNote = meta.bits ? "" : `,解码类型 ${cfg.displayType}`;
    L.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},数量 ${cfg.quantity}${typeNote}`);
    const valueType = meta.bits ? "bool[]" : "ushort[]";
    L.push(`${valueType} values = client.${meta.csharp}(${cfg.startAddress}, ${cfg.quantity});`);
    L.push(`Console.WriteLine($"地址 ${cfg.startAddress}..${end} = {string.Join(", ", values)}");`);
  } else {
    const w = codeWriteValues(cfg);
    const note = w.placeholder ? "(界面未填写入值,以下为占位示例值)" : "";
    if (cfg.fc === 5) {
      L.push(`// ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${w.bool}${note}`);
      L.push(`client.${meta.csharp}(${cfg.startAddress}, ${w.bool});`);
    } else if (cfg.fc === 6) {
      L.push(`// ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${w.int}${note}`);
      L.push(`client.${meta.csharp}(${cfg.startAddress}, ${w.int});`);
    } else if (cfg.fc === 15) {
      L.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.bools.length} 个线圈${note}`);
      L.push(`client.${meta.csharp}(${cfg.startAddress}, new bool[] { ${w.bools.join(", ")} });`);
    } else {
      L.push(`// ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.ints.length} 个寄存器${note}`);
      L.push(`client.${meta.csharp}(${cfg.startAddress}, new ushort[] { ${w.ints.join(", ")} });`);
    }
    L.push(`Console.WriteLine("${fcTag} 写入完成: 地址 ${cfg.startAddress}");`);
  }
  return L.join("\n");
}

// ── Python 模板(pymodbus) ──

function pythonCodeSample(cfg) {
  const meta = CODE_FC[cfg.fc];
  const fcTag = `FC${String(cfg.fc).padStart(2, "0")}`;
  const end = cfg.startAddress + cfg.quantity - 1;
  const pyBool = (b) => (b ? "True" : "False");
  const L = [];
  L.push(`# pymodbus — ${fcTag} ${meta.label} (${CODE_TRANSPORT_LABEL[cfg.transport]})`);
  L.push(`# pip install "pymodbus>=3.6"`);
  const needsFramer = ["rtu-over-tcp", "ascii-over-tcp", "ascii"].includes(cfg.transport);
  if (cfg.isTcp) {
    L.push(`from pymodbus.client import ${cfg.transport === "udp" ? "ModbusUdpClient" : "ModbusTcpClient"}`);
  } else {
    L.push(`from pymodbus.client import ModbusSerialClient`);
  }
  if (needsFramer) L.push(`from pymodbus.framer import FramerType`);
  L.push(``);
  if (cfg.isTcp) {
    if (cfg.transport === "tcp") {
      L.push(`client = ModbusTcpClient("${cfg.host}", port=${cfg.port})  # 站号 ${cfg.unitId}`);
    } else if (cfg.transport === "udp") {
      L.push(`client = ModbusUdpClient("${cfg.host}", port=${cfg.port})  # 站号 ${cfg.unitId}`);
    } else {
      const framer = cfg.transport === "rtu-over-tcp" ? "RTU" : "ASCII";
      L.push(`# ${CODE_TRANSPORT_LABEL[cfg.transport]}: TCP 通道传输串口风格帧,无 MBAP 头`);
      L.push(`client = ModbusTcpClient("${cfg.host}", port=${cfg.port}, framer=FramerType.${framer})  # 站号 ${cfg.unitId}`);
    }
  } else {
    const serialFormat = `${cfg.baudRate} ${cfg.dataBits}${parityLetter(cfg.parity)}${cfg.stopBits}`;
    L.push(`# 串口参数: ${cfg.portName} ${serialFormat},站号 ${cfg.unitId}`);
    L.push(`client = ModbusSerialClient(`);
    if (cfg.transport === "ascii") L.push(`    framer=FramerType.ASCII,`);
    L.push(`    port="${cfg.portName}",`);
    L.push(`    baudrate=${cfg.baudRate},`);
    L.push(`    bytesize=${cfg.dataBits},`);
    L.push(`    parity="${parityLetter(cfg.parity)}",`);
    L.push(`    stopbits=${cfg.stopBits},`);
    L.push(`    timeout=1.0,`);
    L.push(`)`);
  }
  L.push(`client.connect()`);
  L.push(``);
  if (meta.read) {
    const typeNote = meta.bits ? "" : `,解码类型 ${cfg.displayType}`;
    L.push(`# ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},数量 ${cfg.quantity}${typeNote}`);
    L.push(`rr = client.${meta.python}(address=${cfg.startAddress}, count=${cfg.quantity}, slave=${cfg.unitId})`);
    L.push(`if rr.isError():`);
    L.push(`    raise RuntimeError(f"Modbus 错误: {rr}")`);
    const attr = meta.bits ? `rr.bits[:${cfg.quantity}]` : `rr.registers`;
    L.push(`print(f"地址 ${cfg.startAddress}..${end} = {${attr}}")`);
  } else {
    const w = codeWriteValues(cfg);
    const note = w.placeholder ? "(界面未填写入值,以下为占位示例值)" : "";
    let call;
    if (cfg.fc === 5) {
      L.push(`# ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${pyBool(w.bool)}${note}`);
      call = `client.${meta.python}(address=${cfg.startAddress}, value=${pyBool(w.bool)}, slave=${cfg.unitId})`;
    } else if (cfg.fc === 6) {
      L.push(`# ${fcTag} ${meta.label}: 地址 ${cfg.startAddress},值 ${w.int}${note}`);
      call = `client.${meta.python}(address=${cfg.startAddress}, value=${w.int}, slave=${cfg.unitId})`;
    } else if (cfg.fc === 15) {
      L.push(`# ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.bools.length} 个线圈${note}`);
      call = `client.${meta.python}(address=${cfg.startAddress}, values=[${w.bools.map(pyBool).join(", ")}], slave=${cfg.unitId})`;
    } else {
      L.push(`# ${fcTag} ${meta.label}: 起始地址 ${cfg.startAddress},共 ${w.ints.length} 个寄存器${note}`);
      call = `client.${meta.python}(address=${cfg.startAddress}, values=[${w.ints.join(", ")}], slave=${cfg.unitId})`;
    }
    L.push(`rr = ${call}`);
    L.push(`if rr.isError():`);
    L.push(`    raise RuntimeError(f"Modbus 错误: {rr}")`);
    L.push(`print("${fcTag} 写入完成: 地址 ${cfg.startAddress}")`);
  }
  L.push(``);
  L.push(`client.close()`);
  return L.join("\n");
}

function renderCodeSample() {
  const target = document.querySelector("#code-sample");
  if (!target) return;
  try {
    target.textContent = generateSampleCode(codeLang);
  } catch (error) {
    target.textContent = `// 代码生成失败: ${error?.message || error}`;
  }
}

function activateCodeTab(lang) {
  codeLang = lang;
  for (const tab of document.querySelectorAll(".code-tab")) {
    const active = tab.dataset.lang === lang;
    tab.classList.toggle("is-active", active);
    tab.setAttribute("aria-selected", String(active));
  }
  renderCodeSample();
}

async function copyCodeSample() {
  const text = document.querySelector("#code-sample")?.textContent || "";
  if (!text.trim()) return;
  try {
    await navigator.clipboard.writeText(text);
    setNotice("success", "已复制", `${CODE_LANG_LABEL[codeLang]} 示例代码已复制到剪贴板。`);
  } catch {
    setNotice("error", "复制失败", "剪贴板不可用,请手动全选代码复制。");
  }
}

// === 扫描波特率 ===

async function scanBaudRate() {
  if (busy || !isConnected()) return;
  const transport = currentTransport();
  if (isTcpTransport(transport)) {
    setNotice("error", "不支持", "扫描波特率仅适用于串口模式。");
    return;
  }
  const comPort = elements.portName?.value;
  if (!comPort) {
    setNotice("error", "参数无效", "请先选择串口。");
    return;
  }
  const stationId = Number(elements.unitId?.value) || 1;
  setBusy(true);
  elements.commandState.textContent = "正在扫描波特率";
  setNotice("info", "扫描中", "正在逐个波特率探测,请等待...");
  try {
    const result = await callBackend("scan_baud_rate", { comPort, stationId, timeoutMs: 500 });
    if (result.ok && result.foundBaudRate) {
      setNotice("success", "发现波特率", `${result.foundBaudRate} bps(站号 ${result.stationId})`);
      // 自动设置波特率下拉
      const baudSelect = elements.form?.elements?.namedItem("baudRate");
      if (baudSelect) baudSelect.value = String(result.foundBaudRate);
    } else {
      setNotice("info", "未找到", result?.error?.message ?? "所有波特率均无响应");
    }
  } catch (error) {
    setNotice("error", "扫描失败", error.message || String(error));
  } finally {
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
    syncActionState();
  }
}

// === 指令列表 ===

let commandList = [];

function addCurrentCommand() {
  let command;
  try {
    command = readCommand();
  } catch (error) {
    setNotice("error", "参数无效", error.message);
    return;
  }
  const entry = {
    fc: command.functionCode,
    unitId: command.unitId,
    address: command.startAddress,
    quantity: command.quantity,
    value: elements.writeValue?.value || "",
  };
  commandList.push(entry);
  renderCommandList();
  setNotice("info", "已添加", `指令 #${commandList.length}: FC${String(entry.fc).padStart(2, "0")}`);
}

function removeCommand(index) {
  commandList.splice(index, 1);
  renderCommandList();
}

function clearCommands() {
  commandList = [];
  renderCommandList();
}

function renderCommandList() {
  if (!elements.cmdRows) return;
  elements.cmdRows.replaceChildren();
  commandList.forEach((cmd, i) => {
    const row = document.createElement("tr");
    const delBtn = document.createElement("button");
    delBtn.textContent = "删除";
    delBtn.onclick = () => removeCommand(i);
    appendCells(row, [
      String(i + 1),
      `FC${String(cmd.fc).padStart(2, "0")}`,
      String(cmd.unitId),
      String(cmd.address),
      cmd.quantity ? String(cmd.quantity) : "—",
      cmd.value || "—",
    ]);
    const delCell = document.createElement("td");
    delCell.append(delBtn);
    row.append(delCell);
    elements.cmdRows.append(row);
  });
  if (elements.cmdCount) elements.cmdCount.textContent = String(commandList.length);
}

async function executeCommands() {
  if (busy || commandList.length === 0) return;
  setBusy(true);
  elements.commandState.textContent = `正在执行 ${commandList.length} 条指令`;
  setNotice("info", "执行中", `共 ${commandList.length} 条指令...`);
  try {
    const result = await callBackend("execute_commands", { commands: commandList });
    const okCount = result.results?.filter((r) => r.ok).length ?? 0;
    const failCount = (result.results?.length ?? 0) - okCount;
    if (failCount === 0) {
      setNotice("success", "全部成功", `${okCount} 条指令全部执行成功。`);
    } else {
      setNotice("error", "部分失败", `成功 ${okCount},失败 ${failCount}。`);
    }
    // 把每条失败追加到告警
    result.results?.forEach((r) => {
      if (!r.ok) {
        appendAlarm({ code: r.error?.code ?? "CMD_FAIL", message: `指令 #${r.index + 1}: ${r.error?.message ?? "失败"}` });
      }
    });
    refreshStats();
  } catch (error) {
    setNotice("error", "执行失败", error.message || String(error));
  } finally {
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
    syncActionState();
  }
}

// === View 切换(主站/从站) ===

function activateView(viewName) {
  activeView = viewName;
  for (const tab of elements.viewTabs) {
    const selected = tab.dataset.view === viewName;
    tab.classList.toggle("is-active", selected);
    tab.setAttribute("aria-selected", String(selected));
  }
  const isMaster = viewName === "master";
  const isSlave = viewName === "slave";
  const isDebug = viewName === "debug";
  const isParser = viewName === "parser";
  const isMelsec = viewName === "melsec";
  const isInterfaces = viewName === "interfaces";
  const isSiemens = viewName === "siemens";
  const isOmron = viewName === "omron";
  if (elements.masterView) elements.masterView.classList.toggle("hidden", !isMaster);
  if (elements.slaveView) elements.slaveView.classList.toggle("hidden", !isSlave);
  if (elements.debugView) elements.debugView.classList.toggle("hidden", !isDebug);
  if (elements.parserView) elements.parserView.classList.toggle("hidden", !isParser);
  const melsecView = document.querySelector("#melsec-view");
  if (melsecView) melsecView.classList.toggle("hidden", !isMelsec);
  const siemensView = document.querySelector("#siemens-view");
  if (siemensView) siemensView.classList.toggle("hidden", !isSiemens);
  const omronView = document.querySelector("#omron-view");
  if (omronView) omronView.classList.toggle("hidden", !isOmron);
  const interfacesView = document.querySelector("#interfaces-view");
  if (interfacesView) interfacesView.classList.toggle("hidden", !isInterfaces);
  // 打开本页即自动体检(一键看到)
  if (isInterfaces) refreshInterfaces().catch(() => {});
  // 右侧报文面板仅在主站视图显示
  const packetPanel = document.querySelector(".packet-panel");
  if (packetPanel) packetPanel.classList.toggle("hidden", !isMaster);
  // 切 view 时刷新 transport 状态
  updateTransportVisibility();
}

// === 从站模拟 ===

let slaveRunning = false;
const SLAVE_ID = "default";

async function startSlave() {
  const mode = elements.slaveMode?.value || "tcp";
  const stationsRaw = elements.slaveStations?.value?.trim() || "";
  const allowedStations = parseStationList(stationsRaw);
  setBusy(true);
  try {
    if (mode === "serial") {
      await callBackend("start_serial_slave", { slaveId: SLAVE_ID });
      slaveRunning = true;
      if (elements.slaveState) elements.slaveState.textContent = "运行中(RTU 串口)";
    } else {
      const port = Number(elements.slavePort?.value) || 5020;
      await callBackend("start_tcp_slave", {
        slaveId: SLAVE_ID,
        port,
        allowedStationIds: allowedStations,
      });
      slaveRunning = true;
      if (elements.slaveState) elements.slaveState.textContent = `运行中(端口 ${port})`;
    }
    if (elements.slaveStart) elements.slaveStart.disabled = true;
    if (elements.slaveStop) elements.slaveStop.disabled = false;
    setNotice("success", "从站已启动", mode === "serial" ? "RTU 串口从站已启动" : `监听 127.0.0.1:${port}`);
  } catch (error) {
    setNotice("error", "启动失败", error.message || String(error));
  } finally {
    setBusy(false);
    syncActionState();
  }
}

function updateSlaveModeVisibility() {
  const mode = elements.slaveMode?.value || "tcp";
  const tcpConfig = document.querySelector("#slave-tcp-config");
  if (tcpConfig) tcpConfig.style.display = mode === "serial" ? "none" : "";
}

async function stopSlave() {
  setBusy(true);
  try {
    await callBackend("stop_slave", { slaveId: SLAVE_ID });
    slaveRunning = false;
    if (elements.slaveState) elements.slaveState.textContent = "已停止";
    if (elements.slaveStart) elements.slaveStart.disabled = false;
    if (elements.slaveStop) elements.slaveStop.disabled = true;
    setNotice("info", "从站已停止", "");
  } catch (error) {
    setNotice("error", "停止失败", error.message || String(error));
  } finally {
    setBusy(false);
    syncActionState();
  }
}

async function fillSlaveRandom() {
  const area = elements.slaveArea?.value || "holding";
  // 生成 20 个随机值
  const values = Array.from({ length: 20 }, () => Math.floor(Math.random() * 65536));
  try {
    await callBackend("slave_set_value", { slaveId: SLAVE_ID, area, address: 0, values });
    setNotice("success", "已填充", `${area} 地址 0-19 填充随机值`);
    await readSlaveMemory();
  } catch (error) {
    // 如果 TCP 从站没开,尝试串口从站
    try {
      await callBackend("serial_slave_set_value", { slaveId: SLAVE_ID, area, address: 0, values });
      setNotice("success", "已填充", `${area} 填充随机值(串口从站)`);
    } catch {
      setNotice("error", "填充失败", "请先启动从站");
    }
  }
}

async function readSlaveMemory() {
  const area = elements.slaveArea?.value || "holding";
  try {
    const result = await callBackend("slave_get_memory", {
      slaveId: SLAVE_ID,
      area,
      address: 0,
      count: 20,
    });
    const values = result?.values ?? [];
    if (elements.slaveMemRows) {
      elements.slaveMemRows.replaceChildren();
      values.forEach((v, i) => {
        const row = document.createElement("tr");
        appendCells(row, [String(i), `0x${v.toString(16).padStart(4, "0").toUpperCase()}`, String(v)]);
        elements.slaveMemRows.append(row);
      });
    }
  } catch (error) {
    setNotice("error", "读取失败", error.message || String(error));
  }
}

async function clearSlaveMemory() {
  const area = elements.slaveArea?.value || "holding";
  try {
    await callBackend("slave_clear", { slaveId: SLAVE_ID, area });
    setNotice("info", "已清零", `区域 ${area} 已清零`);
    await readSlaveMemory();
  } catch (error) {
    setNotice("error", "清零失败", error.message || String(error));
  }
}

async function setSlaveValue() {
  const area = elements.slaveArea?.value || "holding";
  const address = Number(elements.slaveSetAddr?.value) || 0;
  const valsRaw = elements.slaveSetVals?.value || "";
  // 解析置于 try 内:throw 变为用户可见错误而非 unhandledrejection
  try {
    const values = valsRaw.split(/[,\s]+/).filter(Boolean).map((v) => {
      const n = Number(v);
      if (!Number.isInteger(n) || n < 0 || n > 65535) throw new Error(`值 ${v} 无效`);
      return n;
    });
    if (values.length === 0) {
      setNotice("error", "参数无效", "请输入至少一个值");
      return;
    }
    await callBackend("slave_set_value", { slaveId: SLAVE_ID, area, address, values });
    setNotice("success", "已写入", `${area} 地址 ${address} 写入 ${values.length} 个值`);
    await readSlaveMemory();
  } catch (error) {
    setNotice("error", "写入失败", error.message || String(error));
  }
}

function parseStationList(raw) {
  if (!raw) return [];
  const result = [];
  for (const part of raw.split(/[,\s]+/).filter(Boolean)) {
    if (part.includes("-")) {
      const [start, end] = part.split("-").map(Number);
      if (Number.isInteger(start) && Number.isInteger(end)) {
        for (let i = start; i <= end; i++) result.push(i);
      }
    } else {
      const n = Number(part);
      if (Number.isInteger(n)) result.push(n);
    }
  }
  return result;
}

// === 串口调试 ===

function parseHexInput(text) {
  const cleaned = text.replace(/0x/gi, "").trim();
  const parts = cleaned.split(/[\s,]+/).filter(Boolean);
  return parts.map((p) => {
    const n = parseInt(p, 16);
    if (isNaN(n) || n < 0 || n > 255) throw new Error(`非法字节值: ${p}`);
    return n;
  });
}

function parseAsciiInput(text) {
  return [...text].map((c) => c.charCodeAt(0));
}

function formatTime(ms) {
  const d = new Date(ms);
  return `${String(d.getHours()).padStart(2, "0")}:${String(d.getMinutes()).padStart(2, "0")}:${String(d.getSeconds()).padStart(2, "0")}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

function appendDebugLog(record) {
  if (!elements.dbgLogRows) return;
  // 移除空行
  const empty = elements.dbgLogRows.querySelector(".console-empty");
  if (empty) empty.remove();
  const row = document.createElement("tr");
  row.dataset.direction = record.direction.toLowerCase();
  appendCells(row, [
    formatTime(record.timestamp),
    record.direction,
    record.hex,
    "",
  ]);
  // 点击行时解析
  row.style.cursor = "pointer";
  row.onclick = async () => {
    try {
      const result = await callBackend("parse_frame_online", { bytes: record.bytes, transport: "rtu" });
      row.cells[3].textContent = result?.summary || "—";
    } catch {
      row.cells[3].textContent = "解析失败";
    }
  };
  elements.dbgLogRows.prepend(row);
  // 限制行数
  const rows = elements.dbgLogRows.querySelectorAll("tr");
  if (rows.length > 200) rows[rows.length - 1].remove();
}

async function debugSend() {
  const mode = elements.dbgSendMode?.value || "hex";
  const input = elements.dbgInput?.value || "";
  if (!input.trim()) return;
  try {
    let bytes;
    if (mode === "ascii") {
      bytes = parseAsciiInput(input);
    } else {
      bytes = parseHexInput(input);
    }
    await callBackend("debug_send", { bytes, mode });
    // TX 记录由 onDebugFrame 回调处理
  } catch (error) {
    setNotice("error", "发送失败", error.message || String(error));
  }
}

async function debugAttach() {
  try {
    const result = await callBackend("debug_attach", {});
    if (result.attached) {
      setNotice("success", "已绑定", "串口调试已绑定到当前串口");
    } else {
      setNotice("error", "绑定失败", result.error || "请先打开串口");
    }
  } catch (error) {
    setNotice("error", "绑定失败", error.message || String(error));
  }
}

async function debugClearLog() {
  if (elements.dbgLogRows) {
    elements.dbgLogRows.replaceChildren();
    elements.dbgLogRows.innerHTML = '<tr><td colspan="4" class="console-empty">暂无收发记录</td></tr>';
  }
  await callBackend("debug_clear_log", {});
}

async function calcChecksum(type) {
  const input = elements.dbgCrcInput?.value || "";
  if (!input.trim()) return;
  try {
    const bytes = parseHexInput(input);
    const cmd = type === "crc" ? "compute_crc16" : "compute_lrc";
    const result = await callBackend(cmd, { bytes });
    if (type === "crc") {
      elements.dbgChecksumResult.textContent = `CRC-16 = ${result.crcHex} (低字节 ${result.crcHexLo}, 高字节 ${result.crcHexHi})`;
    } else {
      elements.dbgChecksumResult.textContent = `LRC = ${result.lrcHex}`;
    }
  } catch (error) {
    elements.dbgChecksumResult.textContent = `计算失败: ${error.message}`;
  }
}

// === 配置持久化(localStorage) ===

const STORAGE_KEY_CONFIG = "nexus.config.v1";
const STORAGE_KEY_POINTS = "nexus.pointTable.v1";

/** 保存当前配置到 localStorage */
function persistConfig() {
  try {
    const config = {
      transport: currentTransport(),
      serial: {
        portName: elements.portName?.value || "",
        baudRate: elements.baudRate?.value || "9600",
        parity: elements.parity?.value || "none",
        dataBits: elements.dataBits?.value || "8",
        stopBits: elements.stopBits?.value || "1",
      },
      tcp: {
        host: elements.tcpHost?.value || "127.0.0.1",
        port: elements.tcpPort?.value || "502",
      },
      command: {
        unitId: elements.unitId?.value || "1",
        functionCode: elements.functionCode?.value || "3",
        startAddress: elements.startAddress?.value || "0",
        quantity: elements.quantity?.value || "1",
        displayType: elements.displayType?.value || "Unsigned16",
        pollInterval: elements.pollInterval?.value || "1000",
      },
    };
    localStorage.setItem(STORAGE_KEY_CONFIG, JSON.stringify(config));
  } catch {
    // localStorage 不可用时静默失败
  }
}

/** 从 localStorage 恢复配置 */
function restoreConfig() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY_CONFIG);
    if (!raw) return;
    const config = JSON.parse(raw);
    // 恢复传输方式
    if (config.transport) {
      const radio = document.querySelector(`input[name="transport"][value="${config.transport}"]`);
      if (radio) { radio.checked = true; }
    }
    // 恢复串口参数
    if (config.serial) {
      if (elements.baudRate && config.serial.baudRate) elements.baudRate.value = config.serial.baudRate;
      if (elements.parity && config.serial.parity) elements.parity.value = config.serial.parity;
      if (elements.dataBits && config.serial.dataBits) elements.dataBits.value = config.serial.dataBits;
      if (elements.stopBits && config.serial.stopBits) elements.stopBits.value = config.serial.stopBits;
      // portName 等 refreshPorts 后匹配
      if (config.serial.portName) {
        const tryRestore = () => {
          const sel = elements.portName;
          if (sel && [...sel.options].some((o) => o.value === config.serial.portName)) {
            sel.value = config.serial.portName;
          }
        };
        setTimeout(tryRestore, 1500); // 等待串口列表加载
      }
    }
    // 恢复 TCP 参数
    if (config.tcp) {
      if (elements.tcpHost && config.tcp.host) elements.tcpHost.value = config.tcp.host;
      if (elements.tcpPort && config.tcp.port) elements.tcpPort.value = config.tcp.port;
    }
    // 恢复命令参数
    if (config.command) {
      if (elements.unitId && config.command.unitId) elements.unitId.value = config.command.unitId;
      if (elements.functionCode && config.command.functionCode) elements.functionCode.value = config.command.functionCode;
      if (elements.startAddress && config.command.startAddress) elements.startAddress.value = config.command.startAddress;
      if (elements.quantity && config.command.quantity) elements.quantity.value = config.command.quantity;
      if (elements.displayType && config.command.displayType) elements.displayType.value = config.command.displayType;
      if (elements.pollInterval && config.command.pollInterval) elements.pollInterval.value = config.command.pollInterval;
    }
    updateTransportVisibility();
  } catch {
    // 解析失败静默
  }
}

/** 保存点表到 localStorage */
function persistPointTable() {
  try {
    localStorage.setItem(STORAGE_KEY_POINTS, JSON.stringify(pointTable));
  } catch {
    // 静默
  }
}

/** 恢复点表 */
function restorePointTable() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY_POINTS);
    if (!raw) return;
    const data = JSON.parse(raw);
    if (Array.isArray(data) && data.length > 0) {
      pointTable.push(...data);
      renderPointRows();
    }
  } catch {
    // 静默
  }
}

// === G5: 点表管理 ===

const pointTable = [];
let pointPollTimer = null; // 点表轮询定时器

/** 把点表所有点位渲染到寄存器表格(值为"—"待采集) */
function renderPointRows() {
  for (const point of pointTable) {
    const prefix = point.fc === 1 ? "C" : point.fc === 2 ? "DI" : point.fc === 4 ? "IR" : "HR";
    const area = point.fc === 1 ? "线圈" : point.fc === 2 ? "离散输入" : point.fc === 4 ? "输入寄存器" : "保持寄存器";
    for (let i = 0; i < point.quantity; i++) {
      const address = point.address + i;
      const rowKey = `reg-${prefix}-${address}`;
      let row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
      if (!row) {
        row = document.createElement("tr");
        row.dataset.key = rowKey;
        appendCells(row, [
          "●",
          point.name || `${prefix} ${address}`,
          area,
          address,
          point.dataType || "UInt16",
          "—",
          point.scale ?? "1",
          point.unit || "—",
          "—",
          "待采集",
          "—",
        ]);
        elements.registerResults.append(row);
      }
    }
  }
  // 更新点位计数
  const totalPoints = pointTable.reduce((sum, p) => sum + p.quantity, 0);
  if (totalPoints > 0) elements.pointCount.textContent = `点位 ${totalPoints}`;
  // 清除空状态行
  const emptyRow = elements.registerResults.querySelector(".empty-row");
  if (emptyRow && pointTable.length > 0) emptyRow.remove();
}

function addPoint() {
  let cmd;
  try { cmd = readCommand(); } catch (e) { setNotice("error", "参数无效", e.message); return; }
  const point = {
    name: `${cmd.unitId}/${cmd.functionCode}/${cmd.startAddress}`,
    unitId: cmd.unitId,
    fc: cmd.functionCode,
    address: cmd.startAddress,
    quantity: cmd.quantity,
    dataType: elements.displayType?.value || "Unsigned16",
    scale: elements.scaleFactor?.value || "1",
    unit: elements.unitLabel?.value || "",
  };
  pointTable.push(point);
  renderPointRows();
  persistPointTable();
  setNotice("success", "已添加点位", `${point.name}（共 ${pointTable.length} 个点位）`);
}

function importPoints() {
  const input = document.createElement("input");
  input.type = "file";
  input.accept = ".csv,.json";
  input.onchange = (e) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      try {
        const text = reader.result;
        if (file.name.endsWith(".json")) {
          const data = JSON.parse(text);
          if (Array.isArray(data)) {
            pointTable.push(...data);
          }
        } else {
          // CSV: name,unitId,fc,address,quantity,dataType
          const lines = text.split("\n").filter((l) => l.trim() && !l.startsWith("name,"));
          for (const line of lines) {
            const [name, unitId, fc, address, quantity, dataType] = line.split(",").map((s) => s?.trim());
            pointTable.push({ name, unitId: Number(unitId) || 1, fc: Number(fc) || 3, address: Number(address) || 0, quantity: Number(quantity) || 1, dataType: dataType || "Unsigned16" });
          }
        }
        setNotice("success", "导入成功", `共 ${pointTable.length} 个点位`);
        renderPointRows();
        persistPointTable();
      } catch (err) {
        setNotice("error", "导入失败", err.message);
      }
    };
    reader.readAsText(file);
  };
  input.click();
}

async function savePoints() {
  if (pointTable.length === 0) { setNotice("error", "无点位", "请先添加点位"); return; }
  try {
    const result = await callBackend("export_json", { data: pointTable, filename: "nexus_point_table" });
    setNotice("success", "已保存", `点表已导出到 ${result.path}`);
  } catch (err) {
    setNotice("error", "保存失败", err.message);
  }
}

/** 导出寄存器表格当前数据为 CSV */
async function exportRegisterData() {
  const rows = [];
  elements.registerResults.querySelectorAll("tr:not(.empty-row)").forEach((tr) => {
    const cells = tr.querySelectorAll("td");
    if (cells.length >= 11) {
      rows.push({
        enabled: cells[0].textContent,
        name: cells[1].textContent,
        area: cells[2].textContent,
        address: cells[3].textContent,
        dataType: cells[4].textContent,
        byteOrder: cells[5].textContent,
        scale: cells[6].textContent,
        unit: cells[7].textContent,
        value: cells[8].textContent,
        quality: cells[9].textContent,
        updatedAt: cells[10].textContent,
      });
    }
  });
  if (rows.length === 0) { setNotice("error", "无数据", "寄存器表格为空"); return; }
  try {
    const result = await callBackend("export_csv", { rows, filename: "nexus_registers" });
    setNotice("success", "已导出", `${rows.length} 行导出到 ${result.path}`);
  } catch (err) {
    setNotice("error", "导出失败", err.message);
  }
}

/** 导出报文记录为 CSV */
async function exportTraceData() {
  if (traceHistory.length === 0) { setNotice("error", "无记录", "暂无通信报文"); return; }
  try {
    const result = await callBackend("export_trace", { frames: traceHistory, filename: "nexus_trace" });
    setNotice("success", "已导出", `${traceHistory.length} 条报文导出到 ${result.path}`);
  } catch (err) {
    setNotice("error", "导出失败", err.message);
  }
}

// === 三菱 MC 协议 ===

let mcConnected = false;
let mcIsAscii = false; // 当前连接编码模式(ASCII/Binary),读写命令据此分流
let mcIsUdp = false; // MC over UDP
let mcFxProtocol = null; // "links" | "prog" | null(FX 串口模式)
let mcSlaveRunning = false;
const MC_CONN_ID = "melsec";

function mcSetState(text, ok = false) {
  const el = document.querySelector("#mc-state");
  if (el) { el.textContent = text; el.style.color = ok ? "var(--ok)" : ""; }
}

async function mcConnect() {
  if (mcConnected) return;
  const variant = document.querySelector("#mc-frame-type")?.value || "3e";
  const isFxSerial = variant === "fx-links" || variant === "fx-prog";
  const isC24Serial = variant === "mc-c24";
  const is1e = variant === "mc-1e";
  if (isC24Serial) {
    // MC-C24:复用主站页串口(Q 系列 C24 模块,3C 帧格式1)
    try {
      const status = await callBackend("get_serial_status", {});
      if (!status?.isOpen) {
        mcSetState("串口未打开");
        setNotice("error", "串口未打开", "请先在 Modbus 主站页打开串口(Q 系列 C24 模块),再回到本页连接");
        return;
      }
      mcConnected = true;
      mcIsAscii = false; mcIsUdp = false;
      mcFxProtocol = "c24";
      mcSetState("MC-C24 串口已就绪", true);
      setNotice("success", "MC-C24 已绑定", "走主站页串口(3C 帧·格式1,站号在下方)");
    } catch (error) {
      mcSetState("串口检查失败");
      setNotice("error", "串口检查失败", error.message || String(error));
    }
    mcSyncButtons();
    return;
  }
  if (is1e) {
    const host = document.querySelector("#mc-host")?.value?.trim() || "127.0.0.1";
    const port = Number(document.querySelector("#mc-port")?.value) || 5000;
    try {
      await callBackend("open_mc_1e_tcp", { connectionId: MC_CONN_ID, host, port });
      mcConnected = true;
      mcIsAscii = false; mcIsUdp = false;
      mcFxProtocol = "1e";
      mcSetState(`1E 已连接 ${host}:${port}`, true);
      setNotice("success", "A-1E 已连接", `${host}:${port} (FX3U-ENET/FX5U/A 系兼容)`);
    } catch (error) {
      mcSetState("连接失败");
      setNotice("error", "1E 连接失败", error.message || String(error));
    }
    mcSyncButtons();
    return;
  }
  if (isFxSerial) {
    // FX 串口:复用主站页已打开的串口;此处只检查状态
    try {
      const status = await callBackend("get_serial_status", {});
      if (!status?.isOpen) {
        mcSetState("串口未打开");
        setNotice("error", "串口未打开", "请先在 Modbus 主站页打开串口(FX 默认 9600 7E1),再回到本页连接");
        return;
      }
      mcConnected = true;
      mcIsAscii = false;
      mcFxProtocol = variant === "fx-links" ? "links" : "prog";
      mcSetState(`FX ${mcFxProtocol === "links" ? "Computer Link" : "编程口"} 已就绪`, true);
      setNotice("success", "FX 串口已绑定", `走主站页串口,站号 ${document.querySelector("#mc-fx-station")?.value ?? 0}`);
    } catch (error) {
      mcSetState("串口检查失败");
      setNotice("error", "串口检查失败", error.message || String(error));
    }
    mcSyncButtons();
    return;
  }
  const host = document.querySelector("#mc-host")?.value?.trim() || "127.0.0.1";
  let port = Number(document.querySelector("#mc-port")?.value) || 5000;
  const isUdp = variant.startsWith("mc-udp-");
  const isAscii = variant.startsWith("ascii");
  const frameType = variant.replace("ascii-", "").replace("mc-udp-", "");
  const networkNo = Number(document.querySelector("#mc-network-no")?.value) || 0;
  const pcNo = Number(document.querySelector("#mc-pc-no")?.value) || 255;
  const watchdog = Number(document.querySelector("#mc-watchdog")?.value) || 16;
  setNotice("info", "连接中", `${host}:${port} (${variant.toUpperCase()})`);
  try {
    const cmd = isUdp ? "open_mc_udp_connection" : (isAscii ? "open_mc_ascii_connection" : "open_mc_tcp_connection");
    await callBackend(cmd, {
      connectionId: MC_CONN_ID,
      host, port, frameType,
      networkNo, pcNo, watchdog,
    });
    mcConnected = true;
    mcIsAscii = isAscii;
    mcIsUdp = isUdp;
    mcFxProtocol = null;
    mcSetState(`已连接 ${host}:${port} ${isUdp ? "UDP" : (isAscii ? "ASCII" : "Binary")}`, true);
    setNotice("success", "MC 已连接", `${host}:${port} (${variant.toUpperCase()})`);
  } catch (error) {
    mcSetState("连接失败");
    setNotice("error", "MC 连接失败", error.message || String(error));
  } finally {
    mcSyncButtons();
  }
}

async function mcDisconnect() {
  if (!mcConnected) return;
  try {
    await callBackend("close_connection", { connectionId: MC_CONN_ID });
  } catch { /* 忽略 */ }
  mcConnected = false;
  mcSetState("未连接");
  setNotice("info", "MC 已断开", "");
  mcSyncButtons();
}

async function mcStartSlave() {
  if (mcSlaveRunning) return;
  const port = Number(document.querySelector("#mc-port")?.value) || 5000;
  try {
    await callBackend("start_mc_tcp_slave", { slaveId: "mc-ui", port, seed: true });
    mcSlaveRunning = true;
    setNotice("success", "MC 虚拟从站已启动", `127.0.0.1:${port}(预置 D100=0x1234 等)`);
  } catch (error) {
    setNotice("error", "启动失败", error.message || String(error));
  }
  mcSyncButtons();
}

async function mcStopSlave() {
  if (!mcSlaveRunning) return;
  try {
    await callBackend("stop_mc_slave", { slaveId: "mc-ui" });
  } catch { /* 忽略 */ }
  mcSlaveRunning = false;
  setNotice("info", "MC 虚拟从站已停止", "");
  mcSyncButtons();
}

function mcSyncButtons() {
  const q = (id) => document.querySelector(id);
  if (q("#mc-connect")) q("#mc-connect").disabled = mcConnected;
  if (q("#mc-disconnect")) q("#mc-disconnect").disabled = !mcConnected;
  if (q("#mc-read")) q("#mc-read").disabled = !mcConnected;
  if (q("#mc-write")) q("#mc-write").disabled = !mcConnected;
  if (q("#mc-start-slave")) q("#mc-start-slave").disabled = mcSlaveRunning;
  if (q("#mc-stop-slave")) q("#mc-stop-slave").disabled = !mcSlaveRunning;
  // M2 诊断/控制按钮随连接状态启用
  for (const id of ["#mc-read-type", "#mc-read-status", "#mc-read-clock", "#mc-echo",
                    "#mc-random-read", "#mc-remote-run", "#mc-remote-stop", "#mc-remote-reset"]) {
    if (q(id)) q(id).disabled = !mcConnected;
  }
  const diagState = document.querySelector("#mc-diag-state");
  if (diagState) diagState.textContent = mcConnected ? "已连接" : "需要连接";
}

function mcRenderRows(address, values, isBit) {
  const tbody = document.querySelector("#mc-results");
  if (!tbody) return;
  tbody.replaceChildren();
  if (!values || values.length === 0) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="5">无数据</td></tr>';
    return;
  }
  // 解析地址前缀和起始号用于显示"软元件名"
  const m = address.match(/^([A-Za-z]+)(\d+)(?:\.(\d+))?/);
  const prefix = m ? m[1].toUpperCase() : "";
  const startNo = m ? Number(m[2]) : 0;
  const step = isBit ? 1 : 1; // 位/字软元件编号都按 1 递增显示
  for (let i = 0; i < values.length; i++) {
    const row = document.createElement("tr");
    const v = values[i];
    const name = prefix ? `${prefix}${startNo + i * step}` : String(i);
    const cells = [
      String(i + 1),
      name,
      isBit ? "位" : "字",
      isBit ? (v ? "01" : "00") : `0x${v.toString(16).padStart(4, "0").toUpperCase()}`,
      isBit ? (v ? "ON" : "OFF") : String(v),
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      row.append(td);
    }
    tbody.append(row);
  }
}

async function mcRead() {
  if (!mcConnected) return;
  const address = document.querySelector("#mc-address")?.value?.trim();
  const points = Number(document.querySelector("#mc-points")?.value) || 1;
  if (!address) { setNotice("error", "地址无效", "请输入软元件地址(如 D100)"); return; }
  try {
    let result;
    if (mcFxProtocol === "c24") {
      // MC-C24 串口读(3C 帧格式1)
      const station = Number(document.querySelector("#mc-fx-station")?.value) || 0;
      const r24 = await callBackend("mc_c24_serial_read", { address, points, station, format: "1" });
      if (r24.ok === false) {
        setNotice("error", "MC-C24 失败", r24.error?.message || r24.errorMessage || "");
        return;
      }
      if (r24.endCode !== 0) {
        setNotice("error", `MC 错误 ${r24.endCode?.toString(16).toUpperCase()}`, r24.endCodeMessage || "");
        return;
      }
      const isBitC24 = /^[XYMSTCB]/i.test(address);
      mcRenderRows(address, r24.values, isBitC24);
      setNotice("success", "MC-C24 读取成功", `${address} × ${points}`);
      return;
    }
    if (mcFxProtocol === "1e") {
      const r1e = await callBackend("mc_1e_read", { connectionId: MC_CONN_ID, address, points });
      if (r1e.endCode !== 0) {
        setNotice("error", `1E 错误 ${r1e.endCode?.toString(16).toUpperCase()}`, r1e.message || "");
        return;
      }
      mcRenderRows(address, r1e.values, r1e.isBit);
      setNotice("success", "1E 读取成功", `${address} × ${points}`);
      return;
    }
    if (mcFxProtocol) {
      // FX 串口在线事务
      const station = Number(document.querySelector("#mc-fx-station")?.value) || 0;
      const m = address.match(/^([A-Za-z]+)(\d+)$/);
      if (!m) { setNotice("error", "地址无效", "FX 地址形如 D100/M100/X0"); return; }
      result = await callBackend("fx_serial_transact", {
        op: "read", protocol: mcFxProtocol,
        params: mcFxProtocol === "links"
          ? { station, device: m[1], head: Number(m[2]), points }
          : { device: m[1], address: m[2], words: points },
      });
      if (!result.ok) {
        setNotice("error", `FX 错误 ${result.errorCode ?? ""}`, result.errorMessage || result.error?.message || "");
        return;
      }
      mcRenderRows(address, result.values, /^[XYMSTC]/i.test(m[1]));
      setNotice("success", "FX 读取成功", `${address} × ${points}`);
      return;
    }
    result = mcIsUdp
      ? await callBackend("mc_udp_read", { connectionId: MC_CONN_ID, address, points })
      : mcIsAscii
        ? await callBackend("mc_ascii_read", { connectionId: MC_CONN_ID, address, points })
        : await callBackend("mc_tcp_read", { connectionId: MC_CONN_ID, address, points });
    if (result.endCode !== 0) {
      setNotice("error", `MC 错误 ${result.endCode?.toString(16).toUpperCase()}`, result.endCodeMessage || "");
      return;
    }
    mcRenderRows(address, result.values, result.isBit);
    setNotice("success", "读取成功", `${address} × ${points} 点 (${mcIsAscii ? "ASCII" : "Binary"})`);
  } catch (error) {
    setNotice("error", "读取失败", error.message || String(error));
  }
}

async function mcWrite() {
  if (!mcConnected) return;
  const address = document.querySelector("#mc-address")?.value?.trim();
  const raw = document.querySelector("#mc-write-values")?.value?.trim();
  if (!address) { setNotice("error", "地址无效", "请输入软元件地址"); return; }
  if (!raw) { setNotice("error", "值无效", "请输入写入值(逗号分隔)"); return; }
  const values = raw.split(/[,，\s]+/).map((s) => {
    const t = s.trim();
    if (/^0x/i.test(t)) return parseInt(t, 16);
    if (/^(on|true)$/i.test(t)) return 1;
    if (/^(off|false)$/i.test(t)) return 0;
    return Number(t);
  });
  if (values.some((v) => Number.isNaN(v))) {
    setNotice("error", "值无效", "包含无法解析的值");
    return;
  }
  try {
    let result;
    if (mcFxProtocol === "1e") {
      const w1e = await callBackend("mc_1e_write", { connectionId: MC_CONN_ID, address, values });
      if (w1e.endCode !== 0) {
        setNotice("error", `1E 错误 ${w1e.endCode?.toString(16).toUpperCase()}`, w1e.message || "");
        return;
      }
      setNotice("success", "1E 写入成功", `${address} ← [${values.join(", ")}]`);
      await mcRead();
      return;
    }
    if (mcFxProtocol) {
      const station = Number(document.querySelector("#mc-fx-station")?.value) || 0;
      const m = address.match(/^([A-Za-z]+)(\d+)$/);
      if (!m) { setNotice("error", "地址无效", "FX 地址形如 D100/M100"); return; }
      result = await callBackend("fx_serial_transact", {
        op: "write", protocol: mcFxProtocol,
        params: mcFxProtocol === "links"
          ? { station, device: m[1], head: Number(m[2]), values }
          : { device: m[1], address: m[2], values },
      });
      if (!result.ok) {
        setNotice("error", `FX 错误 ${result.errorCode ?? ""}`, result.errorMessage || result.error?.message || "");
        return;
      }
      setNotice("success", "FX 写入成功", `${address} ← [${values.join(", ")}]`);
      await mcRead();
      return;
    }
    result = mcIsUdp
      ? await callBackend("mc_udp_write", { connectionId: MC_CONN_ID, address, values })
      : mcIsAscii
        ? await callBackend("mc_ascii_write", { connectionId: MC_CONN_ID, address, values })
        : await callBackend("mc_tcp_write", { connectionId: MC_CONN_ID, address, values });
    if (result.endCode !== 0) {
      setNotice("error", `MC 错误 ${result.endCode?.toString(16).toUpperCase()}`, result.endCodeMessage || "");
      return;
    }
    setNotice("success", "写入成功", `${address} ← [${values.join(", ")}]`);
    // 写入后自动读回验证
    await mcRead();
  } catch (error) {
    setNotice("error", "写入失败", error.message || String(error));
  }
}

// === 欧姆龙 FINS ===

let omConnected = false, omSlaveRunning = false, omIsUdp = false;
const OM_CONN_ID = "omron";

function omSetState(text, ok = false) {
  const el = document.querySelector("#om-state");
  if (el) { el.textContent = text; el.style.color = ok ? "var(--ok)" : ""; }
}
function omSyncButtons() {
  const q = (s) => document.querySelector(s);
  if (q("#om-connect")) q("#om-connect").disabled = omConnected;
  if (q("#om-disconnect")) q("#om-disconnect").disabled = !omConnected;
  if (q("#om-read")) q("#om-read").disabled = !omConnected;
  if (q("#om-write")) q("#om-write").disabled = !omConnected;
  if (q("#om-start-slave")) q("#om-start-slave").disabled = omSlaveRunning;
  if (q("#om-stop-slave")) q("#om-stop-slave").disabled = !omSlaveRunning;
}
async function omConnect() {
  if (omConnected) return;
  const host = document.querySelector("#om-host")?.value?.trim() || "127.0.0.1";
  const port = Number(document.querySelector("#om-port")?.value) || 9600;
  const destNode = Number(document.querySelector("#om-dest")?.value) || 0;
  const sourceNode = Number(document.querySelector("#om-src")?.value) || 0;
  omIsUdp = document.querySelector("#om-transport")?.value === "udp";
  const cmd = omIsUdp ? "open_fins_udp" : "open_fins_tcp";
  setNotice("info", "连接中", `${host}:${port} (FINS/${omIsUdp ? "UDP" : "TCP"},节点 ${sourceNode}→${destNode})`);
  try {
    await callBackend(cmd, { connectionId: OM_CONN_ID, host, port, destNode, sourceNode });
    omConnected = true;
    omSetState(`已连接 ${host}:${port} ${omIsUdp ? "UDP" : "TCP"}`, true);
    setNotice("success", "FINS 已连接", "");
  } catch (error) {
    omSetState("连接失败");
    setNotice("error", "FINS 连接失败", error.message || String(error));
  } finally { omSyncButtons(); }
}
async function omDisconnect() {
  if (!omConnected) return;
  try { await callBackend("close_connection", { connectionId: OM_CONN_ID }); } catch { }
  omConnected = false;
  omSetState("未连接"); omSyncButtons();
}
async function omStartSlave() {
  if (omSlaveRunning) return;
  const port = Number(document.querySelector("#om-port")?.value) || 9600;
  try {
    await callBackend("start_fins_slave", { slaveId: "om-ui", port, seed: true });
    omSlaveRunning = true;
    setNotice("success", "FINS 虚拟 PLC 已启动", `127.0.0.1:${port}(TCP+UDP,预置 D100=0x1234 等)`);
  } catch (error) { setNotice("error", "启动失败", error.message || String(error)); }
  omSyncButtons();
}
async function omStopSlave() {
  if (!omSlaveRunning) return;
  try { await callBackend("stop_fins_slave", { slaveId: "om-ui" }); } catch { }
  omSlaveRunning = false;
  setNotice("info", "FINS 虚拟 PLC 已停止", ""); omSyncButtons();
}
function omRender(address, values, isBit) {
  const tbody = document.querySelector("#om-results");
  if (!tbody) return;
  tbody.replaceChildren();
  const m = address.match(/^([A-Za-z]+)(\d+)/);
  const prefix = m ? m[1].toUpperCase() : "";
  const startNo = m ? Number(m[2]) : 0;
  for (let i = 0; i < values.length; i++) {
    const row = document.createElement("tr");
    const v = values[i];
    const cells = [
      String(i + 1),
      `${prefix}${startNo + i}`,
      isBit ? "位" : "字",
      isBit ? (v ? "01" : "00") : `0x${v.toString(16).padStart(4, "0").toUpperCase()}`,
      isBit ? (v ? "ON" : "OFF") : String(v),
    ];
    for (const c of cells) { const td = document.createElement("td"); td.textContent = c; row.append(td); }
    tbody.append(row);
  }
}
async function omRead() {
  if (!omConnected) return;
  const address = document.querySelector("#om-address")?.value?.trim();
  const count = Number(document.querySelector("#om-points")?.value) || 1;
  if (!address) { setNotice("error", "地址无效", "如 D100 / CIO0.00 / W0 / T0"); return; }
  try {
    const r = await callBackend("fins_read", { connectionId: OM_CONN_ID, address, count });
    if (r.endCode !== 0) { setNotice("error", `FINS 0x${r.endCode.toString(16).toUpperCase().padStart(4, "0")}`, ""); return; }
    omRender(address, r.values, r.isBit);
    setNotice("success", "读取成功", `${address} × ${count}`);
  } catch (error) { setNotice("error", "读取失败", error.message || String(error)); }
}
async function omWrite() {
  if (!omConnected) return;
  const address = document.querySelector("#om-address")?.value?.trim();
  const raw = document.querySelector("#om-write-values")?.value?.trim() || "";
  if (!address) return;
  const values = raw.split(",").map((v) => Number(v.trim()));
  if (!values.length || values.some((v) => !Number.isInteger(v) || v < 0 || v > 65535)) {
    setNotice("error", "写入值无效", "逗号分隔的整数(0-65535)"); return;
  }
  try {
    const r = await callBackend("fins_write", { connectionId: OM_CONN_ID, address, values });
    if (r.endCode !== 0) { setNotice("error", `FINS 0x${r.endCode.toString(16).toUpperCase().padStart(4, "0")}`, ""); return; }
    setNotice("success", "写入成功", `${address} ← ${values.length} 个值`);
  } catch (error) { setNotice("error", "写入失败", error.message || String(error)); }
}
function initOmronUi() {
  const q = (id, fn, ev = "click") => { const el = document.querySelector(id); if (el) el.addEventListener(ev, fn); };
  q("#om-connect", omConnect); q("#om-disconnect", omDisconnect);
  q("#om-start-slave", omStartSlave); q("#om-stop-slave", omStopSlave);
  q("#om-read", omRead); q("#om-write", omWrite);
  const addr = document.querySelector("#om-address");
  if (addr) addr.addEventListener("keydown", (e) => { if (e.key === "Enter" && omConnected) omRead(); });
}

// === 本机接口体检 ===

/// 常见 USB 转串口适配器芯片识别表(VID:PID → 芯片/说明)
const USB_SERIAL_CHIPS = {
  "1a86:7523": { chip: "CH340", note: "USB 转 RS232/485(国产适配器最常见)" },
  "1a86:5523": { chip: "CH341", note: "USB 转 RS232/485" },
  "10c4:ea60": { chip: "CP2102", note: "USB 转 UART(西门子/部分 PLC 电缆)" },
  "10c4:ea70": { chip: "CP2105", note: "USB 转 双 UART" },
  "0403:6001": { chip: "FT232", note: "USB 转 RS232/485(质量较好)" },
  "067b:2303": { chip: "PL2303", note: "USB 转 RS232(老款,Win11 需特定驱动)" },
  "0483:5740": { chip: "STM32 VCP", note: "STM32 虚拟串口(自研设备常见)" },
  "1915:ca01": { chip: "Nordic", note: "蓝牙/无线串口桥" },
};

function ifSetNetState(text) {
  const el = document.querySelector("#if-net-state");
  if (el) el.textContent = text;
}
function ifSetComState(text, ok = false) {
  const el = document.querySelector("#if-com-state");
  if (el) { el.textContent = text; el.style.color = ok ? "var(--ok)" : ""; }
}

function ifRenderNetInterfaces(data) {
  const tbody = document.querySelector("#if-net-rows");
  if (!tbody) return;
  tbody.replaceChildren();
  const rows = [];
  // 物理网卡(有 IPv4 的)排前面,回环/虚拟排后面
  const sorted = [...(data.interfaces ?? [])].sort((a, b) => {
    const score = (x) => (x.internal ? 2 : (x.ipv4?.length ? 0 : 1));
    return score(a) - score(b);
  });
  for (const it of sorted) {
    const tr = document.createElement("tr");
    const v4 = it.ipv4?.[0];
    const cells = [
      it.name + (it.internal ? "(回环)" : ""),
      v4 ? v4.address : "—",
      v4 ? v4.netmask : "—",
      [it.ipv6?.[0], it.mac].filter(Boolean).join(" · ") || "—",
      it.internal ? "虚拟" : v4 ? "物理/活动" : "未连接",
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      tr.append(td);
    }
    rows.push(tr);
  }
  if (!rows.length) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="5">未枚举到网卡</td></tr>';
  } else {
    tbody.append(...rows);
  }
  ifSetNetState(`${data.hostname ?? ""} · ${rows.length} 个接口`);
}

async function ifRenderComPorts() {
  const tbody = document.querySelector("#if-com-rows");
  if (!tbody) return;
  let ports = [];
  try {
    ports = await callBackend("list_serial_ports", {});
  } catch (error) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="5">COM 口枚举失败:${error.message || error}</td></tr>`;
    ifSetComState("枚举失败");
    return;
  }
  tbody.replaceChildren();
  let openedName = null;
  try {
    const status = await callBackend("get_serial_status", {});
    if (status?.isOpen && status?.config?.portName) openedName = status.config.portName;
  } catch { /* 忽略 */ }
  if (!ports.length) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="5">未发现 COM 口(USB 转 232/485 适配器插上后会出现在这里;若无,请检查驱动)</td></tr>';
    ifSetComState("无 COM 口");
    return;
  }
  for (const p of ports) {
    const tr = document.createElement("tr");
    const vidpid = (p.vendorId && p.productId)
      ? `${String(p.vendorId).toLowerCase().padStart(4, "0")}:${String(p.productId).toLowerCase().padStart(4, "0")}`
      : "—";
    const chipInfo = USB_SERIAL_CHIPS[vidpid];
    const isOpened = openedName === p.name;
    const cells = [
      p.name,
      [p.manufacturer, chipInfo?.chip, p.serialNumber].filter(Boolean).join(" · ") || "标准串口",
      vidpid,
      isOpened ? "已被本软件打开" : "空闲",
      chipInfo?.note ?? (vidpid !== "—" ? "USB 转串口设备" : "主板/PCI 串口"),
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      if (isOpened) td.style.color = "var(--ok)";
      tr.append(td);
    }
    tbody.append(tr);
  }
  ifSetComState(`${ports.length} 个端口 · ${ports.filter((p) => p.vendorId).length} 个 USB 适配器`, true);
}

async function refreshInterfaces() {
  ifSetNetState("刷新中…");
  ifSetComState("刷新中…");
  try {
    const data = await callBackend("list_network_interfaces", {});
    ifRenderNetInterfaces(data);
    ifFillAdapterSelect(data);
  } catch (error) {
    ifSetNetState("刷新失败");
    setNotice("error", "网卡枚举失败", error.message || String(error));
  }
  await ifRenderComPorts();
  await ifRenderUsbDevices();
}

async function ifRenderUsbDevices() {
  const tbody = document.querySelector("#if-usb-rows");
  const stateEl = document.querySelector("#if-usb-state");
  if (!tbody) return;
  let data;
  try {
    data = await callBackend("list_usb_devices", {});
  } catch (error) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="4">USB 枚举失败:${error.message || error}</td></tr>`;
    if (stateEl) stateEl.textContent = "枚举失败";
    return;
  }
  if (!data.ok) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="4">${data.message || "USB 枚举失败"}</td></tr>`;
    if (stateEl) stateEl.textContent = "枚举失败";
    return;
  }
  tbody.replaceChildren();
  const devices = data.devices ?? [];
  if (!devices.length) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="4">未发现 USB 设备</td></tr>';
    if (stateEl) stateEl.textContent = "无设备";
    return;
  }
  const CLASS_NAMES = {
    Mouse: "鼠标", Keyboard: "键盘", "USB": "USB 设备", DiskDrive: "磁盘",
    "Class for Drivers": "驱动接口", Net: "网卡", Ports: "串口(COM)",
    HIDClass: "HID 设备", "Media": "媒体", Printer: "打印机", Bluetooth: "蓝牙",
    Sensors: "传感器", WPD: "便携设备", System: "系统", "SoftwareDevice": "软件设备",
    Unknown: "未知", "": "未分类",
  };
  for (const d of devices) {
    const tr = document.createElement("tr");
    const cells = [
      d.name,
      CLASS_NAMES[d.class] ?? d.class,
      d.vid ? `${d.vid}:${d.pid}` : "—",
      d.status === "OK" ? "正常" : d.status || "—",
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      if (d.class === "Ports") td.style.color = "var(--ok)"; // 串口类高亮(与上表联动)
      tr.append(td);
    }
    tbody.append(tr);
  }
  if (stateEl) stateEl.textContent = `${devices.length} 个设备`;
}

function ifFillAdapterSelect(data) {
  const sel = document.querySelector("#if-ip-adapter");
  if (!sel) return;
  const current = sel.value;
  sel.replaceChildren();
  // 只列非回环网卡(物理/虚拟均列,名称即 netsh 接口名)
  const list = (data.interfaces ?? []).filter((it) => !it.internal);
  for (const it of list) {
    const opt = document.createElement("option");
    opt.value = it.name;
    opt.textContent = `${it.name}${it.ipv4?.[0] ? `(${it.ipv4[0].address})` : "(未连接)"}`;
    sel.append(opt);
  }
  if (current && list.some((it) => it.name === current)) sel.value = current;
}

async function ifApplyStaticIp() {
  const name = document.querySelector("#if-ip-adapter")?.value;
  const ip = document.querySelector("#if-ip-address")?.value?.trim();
  const mask = document.querySelector("#if-ip-mask")?.value?.trim();
  const gateway = document.querySelector("#if-ip-gateway")?.value?.trim();
  const dns = document.querySelector("#if-ip-dns")?.value?.trim();
  const resultEl = document.querySelector("#if-ip-result");
  if (!name) { setNotice("error", "请选择网卡", ""); return; }
  if (!confirm(`确认把网卡「${name}」改为静态 IP ${ip} / ${mask}?
改错网络会断开,可用「恢复自动获取」还原。`)) return;
  const r = await callBackend("set_interface_ip", { name, mode: "static", ip, mask, gateway, dns });
  const msg = r.ok ? `已设为静态 IP ${r.ip}。若远程连接请确认新网段可达。` : r.message;
  if (resultEl) resultEl.textContent = msg;
  setNotice(r.ok ? "success" : "error", r.ok ? "IP 已修改" : "修改失败", msg);
  if (r.ok) {
    // 网卡信息延迟刷新(系统应用需要 1-2 秒)
    setTimeout(() => refreshInterfaces().catch(() => {}), 2000);
  }
}

async function ifApplyDhcp() {
  const name = document.querySelector("#if-ip-adapter")?.value;
  const resultEl = document.querySelector("#if-ip-result");
  if (!name) { setNotice("error", "请选择网卡", ""); return; }
  if (!confirm(`确认把网卡「${name}」恢复为自动获取 IP(DHCP)?`)) return;
  const r = await callBackend("set_interface_ip", { name, mode: "dhcp" });
  const msg = r.ok ? "已恢复自动获取(DHCP)。" : r.message;
  if (resultEl) resultEl.textContent = msg;
  setNotice(r.ok ? "success" : "error", r.ok ? "已恢复 DHCP" : "恢复失败", msg);
  if (r.ok) {
    setTimeout(() => refreshInterfaces().catch(() => {}), 2000);
  }
}

function initInterfacesUi() {
  const btn = document.querySelector("#if-refresh");
  if (btn) btn.addEventListener("click", () => refreshInterfaces().catch(() => {}));
  const apply = document.querySelector("#if-ip-apply");
  if (apply) apply.addEventListener("click", () => ifApplyStaticIp().catch((e) =>
    setNotice("error", "修改失败", e.message || String(e))));
  const dhcp = document.querySelector("#if-ip-dhcp");
  if (dhcp) dhcp.addEventListener("click", () => ifApplyDhcp().catch((e) =>
    setNotice("error", "恢复失败", e.message || String(e))));
}

// === 西门子 S7comm ===

let s7Connected = false;
let s7SlaveRunning = false;
let s7FxWebApi = false; // Web API 模式分流
const S7_CONN_ID = "siemens";

/// 型号默认 rack/slot(调研 §6.3 表)
const S7_MODEL_DEFAULTS = {
  "1200": { rack: 0, slot: 1 },
  "1500": { rack: 0, slot: 1 },
  "300": { rack: 0, slot: 2 },
  "400": { rack: 0, slot: 3 },
  "smart": { rack: 0, slot: 0 },
};

/// 连接前置检查清单(VOC 报告 ③,按型号)
function s7ChecklistHtml(model) {
  if (model === "smart") {
    return `<strong>S7-200 SMART —— 无需任何 PLC 侧设置,本体网口直连:</strong><br/>
      · 默认 IP <code>192.168.2.1</code>(与 PC 同网段) · rack=0 / slot=0(1 亦可) · 端口 102<br/>
      · V 区自动映射 DB1:VW100 = DB1.DBW100 · 单次读上限约 200 字节(自动分片)<br/>
      · 紧凑型 CR20s/CR30s/CR40s/CR60s 无网口,只能 PPI(本工具暂不支持)`
  }
  if (model === "300" || model === "400") {
    return `<strong>S7-${model} —— 经典机型,默认开放外部访问:</strong><br/>
      · rack=0 / slot=${model === "300" ? 2 : 3}(${model === "300" ? "CP343-1 在 2 号槽" : "CP443-1,多机架会变"}) · 端口 102<br/>
      · 若设了保护密码,读写在 S7 里暂不支持(规划中)<br/>
      · 个别 CPU 只接受特定连接类型:被拒时换 rack/slot 或告知(默认 PG 连接)`
  }
  return `<strong>S7-${model} —— 出厂默认禁止 PUT/GET,需在 TIA Portal 做 4 件事:</strong><br/>
      1. CPU 属性 → 防护与安全 → <strong>连接机制</strong> → 勾选「允许来自远程对象的 PUT/GET 通信访问」<br/>
      2. 防护与安全 → <strong>保护</strong> → 保护等级设为「完全访问(无保护)」<br/>
      3. 目标 DB 右键属性 → 属性 → <strong>取消勾选「优化的块访问」</strong> → 重新编译并下载(否则报 0x05/0x0A)<br/>
      4. 确认 PC 与 PLC 同网段(ping 通);rack=0 / slot=1`
}

function s7SetState(text, ok = false) {
  const el = document.querySelector("#s7-state");
  if (el) { el.textContent = text; el.style.color = ok ? "var(--ok)" : ""; }
}

function s7CurrentModel() {
  const variant = document.querySelector("#s7-variant")?.value;
  return variant === "smart" ? "smart" : (document.querySelector("#s7-model")?.value || "1200");
}

function s7ApplyModel() {
  const model = s7CurrentModel();
  const def = S7_MODEL_DEFAULTS[model] || S7_MODEL_DEFAULTS["1200"];
  const rack = document.querySelector("#s7-rack");
  const slot = document.querySelector("#s7-slot");
  if (rack) rack.value = def.rack;
  if (slot) slot.value = def.slot;
  const hint = document.querySelector("#s7-hint");
  if (hint) {
    hint.textContent = model === "smart"
      ? "SMART V 区语法:VW100(=DB1.DBW100) / VB100 / V100.3 · 或直接用 DB1.DBW100"
      : "地址语法:DB1.DBW20 / M10.3 / IW0 / T5 / C3";
  }
  const addr = document.querySelector("#s7-address");
  if (addr) addr.placeholder = model === "smart" ? "VW100 / VB100 / V100.3 / DB1.DBW100" : "DB1.DBW20 / M10.3 / IW0";
  // 已展开的清单同步刷新
  const body = document.querySelector("#s7-checklist-body");
  if (body && !body.classList.contains("hidden")) {
    document.querySelector("#s7-checklist-content").innerHTML = s7ChecklistHtml(model);
  }
}

function s7SyncButtons() {
  const q = (id) => document.querySelector(id);
  if (q("#s7-connect")) q("#s7-connect").disabled = s7Connected;
  if (q("#s7-disconnect")) q("#s7-disconnect").disabled = !s7Connected;
  if (q("#s7-read")) q("#s7-read").disabled = !s7Connected;
  if (q("#s7-write")) q("#s7-write").disabled = !s7Connected;
  for (const id of ["#s7-read-status", "#s7-pwd-btn", "#s7-hot-start", "#s7-cold-start", "#s7-stop-cpu"]) {
    if (q(id)) q(id).disabled = !s7Connected;
  }
  const diagState = document.querySelector("#s7-diag-state");
  if (diagState) diagState.textContent = s7Connected ? "已连接" : "需要连接";
  if (q("#s7-start-slave")) q("#s7-start-slave").disabled = s7SlaveRunning;
  if (q("#s7-stop-slave")) q("#s7-stop-slave").disabled = !s7SlaveRunning;
}

async function s7Connect() {
  if (s7Connected) return;
  const variant = document.querySelector("#s7-variant")?.value || "s7comm";
  const host = document.querySelector("#s7-host")?.value?.trim() || "127.0.0.1";
  const port = Number(document.querySelector("#s7-port")?.value) || 102;
  const rack = Number(document.querySelector("#s7-rack")?.value) || 0;
  const slot = Number(document.querySelector("#s7-slot")?.value) || 0;
  const model = s7CurrentModel();
  s7FxWebApi = false;

  // === 变体分流(PPI / Fetch-Write / Web API) ===
  if (variant === "ppi") {
    setNotice("info", "连接中", `${host}:${port} (PPI,站 2)`);
    try {
      await callBackend("open_ppi_tcp", { connectionId: S7_CONN_ID, host, port, station: 2 });
      s7Connected = true;
      s7SetState(`PPI 已连接 ${host}:${port}(站 2)`, true);
      setNotice("success", "PPI 已连接", "双拍确认;V 区=DB1");
    } catch (error) {
      s7SetState("连接失败");
      setNotice("error", "PPI 连接失败", error.message || String(error));
    } finally { s7SyncButtons(); }
    return;
  }
  if (variant === "fw") {
    setNotice("info", "连接中", `${host}:${port} (Fetch/Write)`);
    try {
      await callBackend("open_fw_tcp", { connectionId: S7_CONN_ID, host, port: port || 2000 });
      s7Connected = true;
      s7SetState(`FW 已连接 ${host}:${port}`, true);
      setNotice("success", "Fetch/Write 已连接", "S5 兼容通道(DB/M/I/Q 直读)");
    } catch (error) {
      s7SetState("连接失败");
      setNotice("error", "FW 连接失败", error.message || String(error));
    } finally { s7SyncButtons(); }
    return;
  }
  if (variant === "webapi") {
    const user = document.querySelector("#s7-webapi-user")?.value?.trim() || "";
    const password = document.querySelector("#s7-webapi-pass")?.value || "";
    if (!user) { setNotice("error", "Web 用户名为空", "CPU 属性 → 防护与安全 → 用户与权限里设置的 Web 账户"); return; }
    setNotice("info", "连接中", `${host}:443 (Web API)`);
    try {
      await callBackend("s7web_connect", { host, port: 443, user, password });
      s7Connected = true; s7FxWebApi = true;
      s7SetState(`Web API 已登录 ${host}`, true);
      setNotice("success", "Web API 已连接", "JSON-RPC 符号寻址(可读优化块)");
    } catch (error) {
      s7SetState("Web API 登录失败");
      setNotice("error", "Web API 登录失败", error.message || String(error));
    } finally { s7SyncButtons(); }
    return;
  }

  // === 默认:S7comm ===
  setNotice("info", "连接中", `${host}:${port} (rack ${rack}/slot ${slot}, ${model.toUpperCase()})`);
  try {
    const def = S7_MODEL_DEFAULTS[model] || {};
    const r = await callBackend("open_s7_connection", {
      connectionId: S7_CONN_ID, host, port, rack, slot,
      connType: Number(document.querySelector("#s7-conn-type")?.value) || 1,
      localTsap: document.querySelector("#s7-custom-localtsap")?.value?.trim() || def.localTsap || null,
      remoteTsap: document.querySelector("#s7-custom-remotetsap")?.value?.trim() || def.remoteTsap || null,
    });
    s7Connected = true;
    s7SetState(`已连接 · PDU ${r.pduSize}B`, true);
    setNotice("success", "S7 已连接", `协商 PDU ${r.pduSize} 字节(单次最多读 ${r.maxReadBytes}B/写 ${r.maxWriteBytes}B)`);
  } catch (error) {
    s7SetState("连接失败");
    const msg = error.message || String(error);
    if (msg.includes("rack") || msg.includes("拒绝")) {
      setNotice("error", "CPU 拒绝连接", msg + " · 点「连接检查清单」核对型号参数");
    } else {
      setNotice("error", "S7 连接失败", msg);
    }
  } finally {
    s7SyncButtons();
  }
}

async function s7Disconnect() {
  if (!s7Connected) return;
  try {
    await callBackend("close_connection", { connectionId: S7_CONN_ID });
  } catch { /* 忽略 */ }
  s7Connected = false;
  s7SetState("未连接");
  setNotice("info", "S7 已断开", "");
  s7SyncButtons();
}

async function s7StartSlave() {
  if (s7SlaveRunning) return;
  const port = Number(document.querySelector("#s7-port")?.value) || 102;
  try {
    await callBackend("start_s7_slave", { slaveId: "s7-ui", port, seed: true });
    s7SlaveRunning = true;
    setNotice("success", "S7 虚拟 CPU 已启动", `127.0.0.1:${port}(预置 DB1.DBD0=0x12345678 / MW0=0x1234 / T0=0x2510)`);
  } catch (error) {
    setNotice("error", "启动失败", error.message || String(error));
  }
  s7SyncButtons();
}

async function s7StopSlave() {
  if (!s7SlaveRunning) return;
  try {
    await callBackend("stop_s7_slave", { slaveId: "s7-ui" });
  } catch { /* 忽略 */ }
  s7SlaveRunning = false;
  setNotice("info", "S7 虚拟 CPU 已停止", "");
  s7SyncButtons();
}

/// 地址 → 元素宽度(字节):用于结果按宽度组合显示
function s7ElemBytes(address) {
  const a = address.trim().toUpperCase();
  if (/^T\d+$/.test(a) || /^C\d+$/.test(a)) return 2; // S5TIME/计数 16 位
  if (/\.\d+$/.test(a) && !/DBX/.test(a)) return 1;   // 位(M10.3 / V100.3)
  if (/DBX/.test(a)) return 1;
  if (/(DBB|^VB|^IB|^QB|^MB)/.test(a.replace(/DB\d+\./, "DB"))) return 1;
  if (/(DBW|^VW|^IW|^QW|^MW)/.test(a.replace(/DB\d+\./, "DB"))) return 2;
  if (/(DBD|^VD|^ID|^QD|^MD)/.test(a.replace(/DB\d+\./, "DB"))) return 4;
  return 1;
}

/// 字节按宽度大端组合
function s7GroupBytes(bytes, width) {
  const groups = [];
  for (let i = 0; i + width <= bytes.length; i += width) {
    let v = 0n;
    for (let j = 0; j < width; j++) v = (v << 8n) | BigInt(bytes[i + j]);
    groups.push(v);
  }
  return groups;
}

function s7RenderRows(address, data, rc, rcMsg) {
  const tbody = document.querySelector("#s7-results");
  if (!tbody) return;
  tbody.replaceChildren();
  if (!data || data.length === 0) {
    tbody.innerHTML = `<tr class="empty-row"><td colspan="5">${rcMsg || "无数据"}</td></tr>`;
    return;
  }
  const width = s7ElemBytes(address);
  const groups = s7GroupBytes(data, width);
  const isBit = /\.\d+$/.test(address.trim()) && !/DB[BWDX]/.test(address.trim().toUpperCase());
  for (let i = 0; i < groups.length; i++) {
    const row = document.createElement("tr");
    const hexBytes = Array.from(data.slice(i * width, (i + 1) * width))
      .map((b) => b.toString(16).padStart(2, "0").toUpperCase()).join(" ");
    const cells = [
      String(i + 1),
      `${address}#${i}`,
      rc === 0xFF ? "0xFF ✓" : `0x${rc.toString(16).toUpperCase().padStart(2, "0")}`,
      isBit ? (groups[i] ? "01" : "00") : hexBytes,
      isBit ? (groups[i] ? "ON" : "OFF") : groups[i].toString(),
    ];
    for (const c of cells) {
      const td = document.createElement("td");
      td.textContent = c;
      row.append(td);
    }
    tbody.append(row);
  }
}

async function s7Read() {
  if (!s7Connected) return;
  const address = document.querySelector("#s7-address")?.value?.trim();
  const count = Number(document.querySelector("#s7-points")?.value) || 1;
  if (!address) { setNotice("error", "地址无效", "请输入 S7 地址(如 DB1.DBW20 / M10.3 / VW100)"); return; }
  try {
    const r = await callBackend("s7_read", { connectionId: S7_CONN_ID, items: [{ address, count }] });
    const item = r.items?.[0];
    if (!item) { setNotice("error", "读取失败", "无返回项"); return; }
    if (item.returnCode !== 0xFF) {
      s7RenderRows(address, item.data || [], item.returnCode, item.returnCodeMessage);
      setNotice("error", `返回码 0x${item.returnCode.toString(16).toUpperCase().padStart(2, "0")}`, item.returnCodeMessage || "");
      return;
    }
    s7RenderRows(address, item.data || [], 0xFF, "");
    setNotice("success", "读取成功", `${address} × ${count}(${item.data.length} 字节,大端)`);
  } catch (error) {
    setNotice("error", "读取失败", error.message || String(error));
  }
}

async function s7Write() {
  if (!s7Connected) return;
  const address = document.querySelector("#s7-address")?.value?.trim();
  const raw = document.querySelector("#s7-write-values")?.value?.trim() || "";
  if (!address) { setNotice("error", "地址无效", ""); return; }
  let values = null;
  if (/^hex:/i.test(raw)) {
    values = raw.slice(4).trim().split(/[\s,]+/).filter(Boolean).map((h) => parseInt(h, 16));
  } else if (raw) {
    values = raw.split(",").map((v) => Number(v.trim()));
  }
  if (!values || values.length === 0 || values.some((v) => !Number.isInteger(v) || v < 0 || v > 255)) {
    setNotice("error", "写入值无效", "格式:十进制字节列表(1,2,3,4)或 hex: 12 34 56 78");
    return;
  }
  if (values.length > 32 && !confirm(`将写入 ${values.length} 字节到 ${address},确认?`)) return;
  try {
    const r = await callBackend("s7_write", { connectionId: S7_CONN_ID, items: [{ address, values }] });
    const codes = r.returnCodes || [];
    const msgs = r.returnCodeMessages || [];
    if (codes.length && codes[0] !== 0xFF) {
      setNotice("error", `写入失败 0x${codes[0].toString(16).toUpperCase().padStart(2, "0")}`, msgs[0] || "");
      return;
    }
    setNotice("success", "写入成功", `${address} ← ${values.length} 字节`);
  } catch (error) {
    setNotice("error", "写入失败", error.message || String(error));
  }
}

async function s7Diag(cmd, args, fmt) {
  try {
    const r = await callBackend(cmd, Object.assign({ connectionId: S7_CONN_ID }, args || {}));
    const el = document.querySelector("#s7-diag-result");
    if (el) el.textContent = fmt(r);
    setNotice("success", "诊断完成", fmt(r));
  } catch (error) {
    setNotice("error", "诊断失败", error.message || String(error));
  }
}
function s7Control(action, label) {
  if (!confirm(`确认对 CPU 执行「${label}」?
远程控制属高危操作,请确认设备安全。`)) return;
  s7Diag("s7_cpu_control", { action }, (r) => `控制结果:${r.message}`);
}
function initSiemensUi() {
  const q = (id, fn, ev = "click") => {
    const el = document.querySelector(id);
    if (el) el.addEventListener(ev, fn);
  };
  q("#s7-connect", s7Connect);
  q("#s7-disconnect", s7Disconnect);
  q("#s7-start-slave", s7StartSlave);
  q("#s7-stop-slave", s7StopSlave);
  q("#s7-read", s7Read);
  q("#s7-write", s7Write);
  const addrInput = document.querySelector("#s7-address");
  if (addrInput) addrInput.addEventListener("keydown", (e) => { if (e.key === "Enter" && s7Connected) s7Read(); });
  const modelSel = document.querySelector("#s7-model");
  if (modelSel) modelSel.addEventListener("change", s7ApplyModel);
  const variantSel = document.querySelector("#s7-variant");
  if (variantSel) variantSel.addEventListener("change", () => {
    // SMART 变体强制型号联动(共用 rack/slot 逻辑)
    if (variantSel.value === "smart") {
      const m = document.querySelector("#s7-model");
      if (m) m.value = "smart";
    }
    s7ApplyModel();
  });
  q("#s7-checklist", () => {
    const body = document.querySelector("#s7-checklist-body");
    if (!body) return;
    body.classList.toggle("hidden");
    if (!body.classList.contains("hidden")) {
      document.querySelector("#s7-checklist-content").innerHTML = s7ChecklistHtml(s7CurrentModel());
    }
  });
  q("#s7-read-status", () => s7Diag("s7_read_status", {}, (r) => `CPU 模式:${r.mode}`));
  q("#s7-pwd-btn", () => {
    const pwd = document.querySelector("#s7-pwd-input")?.value;
    if (!pwd) { setNotice("error", "密码为空", ""); return; }
    s7Diag("s7_password", { password: pwd }, () => "密码已提交(300/400 有效;1200/1500 无此机制)");
  });
  q("#s7-hot-start", () => s7Control("hot", "暖启动"));
  q("#s7-cold-start", () => s7Control("cold", "冷启动"));
  q("#s7-stop-cpu", () => s7Control("stop", "停止 CPU"));
  const advBtn = document.querySelector("#s7-advanced-toggle");
  if (advBtn) advBtn.addEventListener("click", () => {
    document.querySelector("#s7-advanced-row")?.classList.toggle("hidden");
  });
  s7ApplyModel();
}

function initMelsecUi() {
  const q = (id, fn, ev = "click") => {
    const el = document.querySelector(id);
    if (el) el.addEventListener(ev, fn);
  };
  q("#mc-connect", mcConnect);
  q("#mc-disconnect", mcDisconnect);
  q("#mc-start-slave", mcStartSlave);
  q("#mc-stop-slave", mcStopSlave);
  q("#mc-read", mcRead);
  q("#mc-write", mcWrite);
  const addrInput = document.querySelector("#mc-address");
  if (addrInput) addrInput.addEventListener("keydown", (e) => { if (e.key === "Enter" && mcConnected) mcRead(); });
  // 协议变体切换:网口字段 ↔ FX 串口字段
  const variantSel = document.querySelector("#mc-frame-type");
  if (variantSel) {
    variantSel.addEventListener("change", () => {
      const isFx = variantSel.value === "fx-links" || variantSel.value === "fx-prog";
      document.querySelector("#mc-net-row")?.classList.toggle("hidden", isFx);
      document.querySelector("#mc-serial-row")?.classList.toggle("hidden", !isFx);
    });
  }
  // M2:诊断与控制
  q("#mc-read-type", () => mcDiag("mc_read_cpu_type", (r) => `CPU 型号: ${r.cpuType}`));
  q("#mc-read-status", () => mcDiag("mc_read_cpu_status", (r) => `CPU 状态: ${r.cpuStatus}`));
  q("#mc-read-clock", () => mcDiag("mc_read_clock", (r) => {
    const c = r.clock;
    return `PLC 时钟: 20${c.year}-${String(c.month).padStart(2, "0")}-${String(c.day).padStart(2, "0")} ${String(c.hour).padStart(2, "0")}:${String(c.minute).padStart(2, "0")}:${String(c.second).padStart(2, "0")} 周${"日一二三四五六"[c.weekday]}`;
  }));
  q("#mc-echo", () => mcDiag("mc_echo_test", (r) => `链路自检: ${r.matched ? "✓ 回送一致(链路正常)" : "✗ 回送不一致!"}`));
  q("#mc-random-read", mcRandomRead);
  q("#mc-remote-run", () => mcRemoteConfirm("mc_remote_run", "远程 RUN", "让 PLC 进入运行状态?"));
  q("#mc-remote-stop", () => mcRemoteConfirm("mc_remote_stop", "远程 STOP", "让 PLC 停止运行?生产设备可能中断输出!"));
  q("#mc-remote-reset", () => mcRemoteConfirm("mc_remote_reset", "远程 RESET", "复位 CPU?这是高危操作!"));
  mcSyncButtons();
}

/** 诊断类命令(读型号/状态/时钟/回送) */
async function mcDiag(cmd, format) {
  if (!mcConnected) return;
  const out = document.querySelector("#mc-diag-result");
  try {
    const r = await callBackend(cmd, { connectionId: MC_CONN_ID });
    const text = r.endCode === 0 ? format(r) : `错误 ${r.endCode?.toString(16).toUpperCase()}: ${r.endCodeMessage}`;
    if (out) out.textContent = text;
    setNotice(r.endCode === 0 ? "success" : "error", "MC 诊断", text);
  } catch (error) {
    if (out) out.textContent = `失败: ${error.message}`;
    setNotice("error", "诊断失败", error.message || String(error));
  }
}

/** 随机读(0403):逗号分隔地址 */
async function mcRandomRead() {
  if (!mcConnected) return;
  const raw = document.querySelector("#mc-random-addrs")?.value?.trim();
  const out = document.querySelector("#mc-diag-result");
  if (!raw) { setNotice("error", "地址为空", "输入逗号分隔的软元件地址"); return; }
  const addresses = raw.split(/[,，\s]+/).filter(Boolean);
  try {
    const r = await callBackend("mc_tcp_read_random", { connectionId: MC_CONN_ID, addresses });
    if (r.endCode !== 0) {
      if (out) out.textContent = `错误 ${r.endCode?.toString(16).toUpperCase()}: ${r.endCodeMessage}`;
      return;
    }
    const lines = addresses.map((a, i) => `${a} = 0x${(r.values[i] ?? 0).toString(16).padStart(4, "0").toUpperCase()} (${r.values[i] ?? 0})${r.isBit ? (r.values[i] ? " ON" : " OFF") : ""}`);
    if (out) out.textContent = lines.join("\n");
    setNotice("success", "随机读成功", `${addresses.length} 个软元件`);
  } catch (error) {
    if (out) out.textContent = `失败: ${error.message}`;
    setNotice("error", "随机读失败", error.message || String(error));
  }
}

/** 远程控制(高危,二次确认) */
async function mcRemoteConfirm(cmd, name, warning) {
  if (!mcConnected) return;
  if (!window.confirm(`${name}\n\n${warning}`)) return;
  try {
    const r = await callBackend(cmd, { connectionId: MC_CONN_ID });
    const ok = r.endCode === 0;
    setNotice(ok ? "success" : "error", name, ok ? "已执行" : `错误 ${r.endCode?.toString(16).toUpperCase()}: ${r.endCodeMessage}`);
  } catch (error) {
    setNotice("error", `${name} 失败`, error.message || String(error));
  }
}

// === SSE 实时推送(供外部客户端订阅) ===

let sseRunning = false;

async function toggleSse() {
  const btn = document.querySelector("#toggle-sse");
  try {
    if (!sseRunning) {
      const result = await callBackend("start_realtime_push", { port: 8080 });
      if (result?.started) {
        sseRunning = true;
        if (btn) { btn.textContent = "推送中"; btn.style.color = "var(--ok)"; }
        setNotice("success", "SSE 推送已启动", `外部客户端可订阅 ${result.url}`);
      } else {
        setNotice("error", "启动失败", result?.error || "未知错误");
      }
    } else {
      await callBackend("stop_realtime_push");
      sseRunning = false;
      if (btn) { btn.textContent = "推送"; btn.style.color = ""; }
      setNotice("info", "SSE 推送已停止", "");
    }
  } catch (error) {
    setNotice("error", "推送操作失败", error.message || String(error));
  }
}

// === 高级功能码 (TCP 专属) ===

const ADV_FC_CONFIG = {
  "07": { name: "读异常状态", cmd: "tcp_read_exception_status", params: [] },
  "08": { name: "诊断", cmd: "tcp_diagnostics", params: [
    { key: "subFunction", label: "子功能码", default: "0", hint: "0=回环 1=重启 2=诊断寄存器" },
    { key: "data", label: "数据", default: "0", hint: "十六进制或十进制" },
  ] },
  "11": { name: "通信事件计数", cmd: "tcp_get_comm_event_counter", params: [] },
  "12": { name: "通信事件日志", cmd: "tcp_get_comm_event_log", params: [] },
  "17": { name: "报告从站标识", cmd: "tcp_report_slave_id", params: [] },
  "22": { name: "屏蔽写寄存器", cmd: "tcp_mask_write_register", params: [
    { key: "address", label: "寄存器地址", default: "0" },
    { key: "andMask", label: "AND 掩码", default: "0xFFFF", hint: "如 0xFFFF" },
    { key: "orMask", label: "OR 掩码", default: "0x0000", hint: "如 0x0000" },
  ] },
  "23": { name: "读写多寄存器", cmd: "tcp_read_write_multiple", params: [
    { key: "readAddress", label: "读地址", default: "0" },
    { key: "readQuantity", label: "读数量", default: "1" },
    { key: "writeAddress", label: "写地址", default: "0" },
    { key: "writeValues", label: "写入值", default: "0", wide: true, hint: "逗号分隔,如 100,200" },
  ] },
  "43": { name: "读设备标识", cmd: "tcp_read_device_id", params: [
    { key: "readDeviceIdCode", label: "读取码", default: "1", hint: "1=基本 2=常规 3=扩展 4=单个" },
    { key: "objectId", label: "对象 ID", default: "0" },
  ] },
};

/** 解析数字输入(支持 0x 十六进制) */
function parseNum(str) {
  const s = String(str).trim();
  if (/^0x/i.test(s)) return parseInt(s, 16);
  return Number(s) || 0;
}

/** 渲染高级 FC 参数输入行 */
function renderAdvFcParams() {
  const container = document.querySelector("#adv-fc-params");
  const fc = document.querySelector("#adv-fc-select")?.value;
  if (!container || !fc || !ADV_FC_CONFIG[fc]) return;
  container.replaceChildren();
  for (const p of ADV_FC_CONFIG[fc].params) {
    const field = document.createElement("div");
    field.className = "param-field";
    const label = document.createElement("label");
    label.textContent = p.label;
    const input = document.createElement("input");
    input.className = `input${p.wide ? " wide" : ""}`;
    input.id = `adv-param-${p.key}`;
    input.value = p.default;
    if (p.hint) input.title = p.hint;
    field.append(label, input);
    container.append(field);
  }
  const execBtn = document.querySelector("#adv-fc-exec");
  if (execBtn) execBtn.disabled = !tcpConnected;
}

/** 执行高级 FC */
async function executeAdvFc() {
  const fc = document.querySelector("#adv-fc-select")?.value;
  const resultEl = document.querySelector("#adv-fc-result");
  if (!fc || !ADV_FC_CONFIG[fc]) return;
  const cfg = ADV_FC_CONFIG[fc];
  if (!tcpConnected) {
    setNotice("error", "仅 TCP 模式", "高级功能码需要 TCP/UDP 连接。");
    return;
  }
  // 收集参数
  const args = { connectionId: "default" };
  for (const p of cfg.params) {
    const input = document.querySelector(`#adv-param-${p.key}`);
    if (!input) continue;
    if (p.key === "writeValues") {
      args[p.key] = input.value.split(",").map((s) => parseNum(s)).filter((n) => !Number.isNaN(n));
    } else {
      args[p.key] = parseNum(input.value);
    }
  }
  setBusy(true);
  try {
    const result = await callBackend(cfg.cmd, args);
    if (resultEl) {
      resultEl.classList.add("has-content");
      resultEl.textContent = `✓ FC${fc.padStart(2, "0")} ${cfg.name} 执行成功\n${JSON.stringify(result, null, 2)}`;
    }
    setNotice("success", `FC${fc.padStart(2, "0")} ${cfg.name}`, "执行成功,结果已显示。");
  } catch (error) {
    if (resultEl) {
      resultEl.classList.add("has-content");
      resultEl.textContent = `✗ FC${fc.padStart(2, "0")} ${cfg.name} 执行失败\n${error.message || String(error)}`;
    }
    setNotice("error", "执行失败", error.message || String(error));
  } finally {
    setBusy(false);
  }
}



const sessionTabs = new Map(); // sessionName → { rowsHtml: string, pointTable: Array, btn: Element|null }
let activeSession = "default";
sessionTabs.set("default", { rowsHtml: "", pointTable: [], btn: null });

/** 保存当前标签的表格内容和点表 */
function saveCurrentSessionData() {
  const tab = sessionTabs.get(activeSession);
  if (tab) {
    tab.rowsHtml = elements.registerResults.innerHTML;
    tab.pointTable = pointTable.map((p) => ({ ...p }));
  }
}

/** 恢复目标标签的表格内容和点表 */
function restoreSessionData(name) {
  const tab = sessionTabs.get(name);
  const emptyHtml = '<tr class="empty-row"><td colspan="11"><div class="empty-guide">暂无监控点<br/><small>连接后点击"读取"开始监控</small></div></td></tr>';
  elements.registerResults.innerHTML = tab?.rowsHtml || emptyHtml;
  pointTable.length = 0;
  pointTable.push(...(tab?.pointTable ?? []).map((p) => ({ ...p })));
  activeSession = name;
  // 刷新计数显示
  const rowCount = elements.registerResults.querySelectorAll("tr:not(.empty-row)").length;
  elements.pointCount.textContent = `点位 ${rowCount}`;
}

function addSessionTab() {
  const name = `会话${sessionTabs.size}`;
  if (sessionTabs.has(name)) return;
  // 先保存当前标签数据
  saveCurrentSessionData();
  // 创建标签按钮
  const btn = document.createElement("button");
  btn.className = "session-tab-btn";
  btn.dataset.session = name;
  btn.textContent = `${name} ×`;
  btn.title = `切换到 ${name}（点击 × 关闭）`;
  btn.addEventListener("click", (e) => {
    if (e.offsetX > btn.offsetWidth - 20) {
      closeSessionTab(name, btn);
    } else {
      activateSessionTab(name);
    }
  });
  elements.sessionTabsBar?.append(btn);
  // 新标签:空表格 + 空点表
  sessionTabs.set(name, { rowsHtml: "", pointTable: [], btn });
  activateSessionTab(name);
  setNotice("info", "新标签", `已创建 ${name}（独立数据区）`);
}

function closeSessionTab(name, btn) {
  if (name === "default") {
    setNotice("error", "不允许", "默认标签不可关闭。");
    return;
  }
  const switchingAway = activeSession === name;
  sessionTabs.delete(name);
  btn.remove();
  if (switchingAway) activateSessionTab("default");
}

function activateSessionTab(name) {
  if (name === activeSession) {
    for (const tab of elements.sessionTabsBar?.querySelectorAll(".session-tab-btn") ?? []) {
      tab.classList.toggle("is-active", tab.dataset.session === name);
    }
    return;
  }
  saveCurrentSessionData();
  restoreSessionData(name);
  for (const tab of elements.sessionTabsBar?.querySelectorAll(".session-tab-btn") ?? []) {
    tab.classList.toggle("is-active", tab.dataset.session === name);
  }
  setNotice("info", "切换标签", `当前: ${name}（${pointTable.length} 个点位）`);
}



async function parseFrame() {
  const hex = elements.parserInput?.value || "";
  const transport = elements.parserTransport?.value || "auto";
  if (!hex.trim()) return;
  try {
    const result = await callBackend("parse_frame_offline", { hex, transport });
    renderParseResult(result);
  } catch (error) {
    if (elements.parserResult) {
      elements.parserResult.innerHTML = `<div style="color:var(--danger)">解析失败: ${error.message || error}</div>`;
    }
  }
}

function renderParseResult(info) {
  if (!elements.parserResult) return;
  if (!info || !info.isValid) {
    elements.parserResult.innerHTML = `<div style="color:var(--danger)">无效报文: ${info?.error || "解析失败"}</div>`;
    return;
  }
  const fields = [
    ["传输方式", info.transport?.toUpperCase()],
    ["有效性", info.isValid ? "✓ 有效" : "✗ 无效"],
    ["方向", info.direction],
    ["站号", info.unitId],
    ["功能码", `0x${(info.functionCode ?? 0).toString(16).padStart(2, "0").toUpperCase()}`],
    ["功能名称", info.functionName],
    ["是否异常", info.isException ? `是 (0x${(info.exceptionCode ?? 0).toString(16).padStart(2, "0")} ${info.exceptionName ?? ""})` : "否"],
    ["地址", info.address ?? "—"],
    ["数量", info.quantity ?? "—"],
    ["字节计数", info.byteCount ?? "—"],
    ["寄存器数据", info.registers?.length ? info.registers.map((r) => `0x${r.toString(16).padStart(4, "0").toUpperCase()}`).join(" ") : "—"],
    ["线圈数据", info.coils?.length ? info.coils.map((b) => (b ? "1" : "0")).join("") : "—"],
    ["校验状态", info.checksumStatus],
    ["校验码", info.checksum ?? "—"],
    ["摘要", info.summary],
  ];
  elements.parserResult.innerHTML = fields
    .map(
      ([label, value]) =>
        `<div class="field"><span class="field-label">${label}</span><span class="field-value">${value ?? "—"}</span></div>`,
    )
    .join("");
}

function syncPollState() {
  const polling = !!activePollId || !!pointPollTimer;
  if (elements.startPoll) elements.startPoll.disabled = polling || !isConnected();
  if (elements.stopPoll) elements.stopPoll.disabled = !polling;
  if (elements.readOnce) elements.readOnce.disabled = polling || !isConnected();
  if (elements.writeOnce) elements.writeOnce.disabled = polling || !isConnected();
}

async function handlePollData(data) {
  if (!data) return;
  // FC01/02 返回 coils(布尔数组), FC03/04 返回 registers(u16 数组)
  const isCoils = Array.isArray(data.coils);
  const values = isCoils ? data.coils : data.registers;
  if (!Array.isArray(values)) return;

  // 增量更新寄存器表格(keyed-row)
  const updatedAt = clockTime();
  const fc = data.fc || 3;
  let prefix, area, dataType;
  if (fc === 1) { prefix = "C"; area = "线圈"; dataType = "Bool"; }
  else if (fc === 2) { prefix = "DI"; area = "离散输入"; dataType = "Bool"; }
  else if (fc === 4) { prefix = "IR"; area = "输入寄存器"; dataType = "UInt16"; }
  else { prefix = "HR"; area = "保持寄存器"; dataType = "UInt16"; }

  // 按 keyed-row 模式更新(如果行已存在就改值,不存在就新增)
  for (let i = 0; i < values.length; i++) {
    const address = (data.startAddress ?? data.address ?? 0) + i;
    const rawValue = values[i];
    // 线圈/离散输入显示 ON/OFF(工业惯例),寄存器显示数值
    const displayValue = isCoils ? (rawValue ? "ON" : "OFF") : rawValue;
    const rowKey = `reg-${prefix}-${address}`;
    let row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
    if (!row) {
      row = document.createElement("tr");
      row.dataset.key = rowKey;
      appendCells(row, [
        "●",
        `${prefix} ${address}`,
        area,
        address,
        dataType,
        "—",
        "1",
        "—",
        displayValue,
        "Good",
        updatedAt,
      ]);
      elements.registerResults.append(row);
    } else {
      // 更新值和时间的单元格(倒数第 3 列是值,倒数第 2 是质量,最后是时间)
      const cells = row.querySelectorAll("td");
      if (cells.length >= 11) {
        const oldVal = cells[8].textContent;
        cells[8].textContent = displayValue;
        cells[10].textContent = updatedAt;
        // 值变化时闪烁高亮
        if (oldVal !== String(displayValue)) {
          cells[8].style.transition = "background 0.5s";
          cells[8].style.background = "var(--ok)";
          setTimeout(() => {
            cells[8].style.background = "";
          }, 500);
        }
      }
    }
    // 趋势采集: 寄存器值直接喂数(线圈/离散为 ON/OFF, 非数值型, 跳过)
    if (!isCoils) trendFeed(rowKey, rawValue);
  }
  elements.pointCount.textContent = `点位 ${values.length}`;
}

function setBusy(nextBusy) {
  busy = nextBusy;
  syncActionState();
}

function readConfig() {
  const data = new FormData(elements.form);
  return {
    portName: String(data.get("portName") ?? "").trim(),
    baudRate: Number(data.get("baudRate")),
    dataBits: Number(data.get("dataBits")),
    parity: String(data.get("parity")),
    stopBits: String(data.get("stopBits")),
    flowControl: String(data.get("flowControl")),
    readTimeoutMs: Number(data.get("readTimeoutMs")),
    writeTimeoutMs: Number(data.get("writeTimeoutMs")),
    dtrMode: String(data.get("dtrMode")),
    rtsMode: String(data.get("rtsMode")),
  };
}

function parityLetter(parity) {
  return parity === "none" ? "N" : parity === "even" ? "E" : parity === "odd" ? "O" : parity;
}

function renderStatus(status) {
  const connected = Boolean(status?.isOpen);
  const config = status?.config ?? null;
  elements.connectionPill.dataset.state = connected ? "open" : "closed";
  elements.connectionLabel.textContent = connected ? `${config?.portName ?? "未知端口"} 已打开` : "未连接";
  elements.detailState.textContent = connected ? "已建立" : "未打开";
  elements.detailPort.textContent = config?.portName ?? "—";
  elements.detailFormat.textContent = config ? `${config.baudRate} · ${config.dataBits}${parityLetter(config.parity)}${config.stopBits}` : "—";
  elements.detailTimeout.textContent = config ? `${config.readTimeoutMs}/${config.writeTimeoutMs} ms` : "—";
  elements.commandState.textContent = connected ? commandReadyText() : "请先打开串口";

  for (const control of elements.form.querySelectorAll("input, select")) control.disabled = connected;
  updateDependentControls();
  syncActionState();
}

function parseStartAddress(value) {
  const text = String(value ?? "").trim();
  const decimal = /^\d+$/.test(text);
  const hexadecimal = /^0x[\da-f]+$/i.test(text);
  if (!decimal && !hexadecimal) {
    throw new Error("地址必须是 0 到 65535 的十进制数，或以 0x 开头的十六进制数。");
  }
  const address = Number.parseInt(text, hexadecimal ? 16 : 10);
  if (!Number.isInteger(address) || address < 0 || address > 65_535) {
    throw new Error("地址必须在 0 到 65535 之间。");
  }
  return address;
}

function readCommand() {
  const unitId = Number(elements.unitId.value);
  const functionCode = Number(elements.functionCode.value);
  let startAddress = parseStartAddress(elements.startAddress.value);
  // G3: 地址基调整 — 1 基模式下用户输入 40001 对应协议地址 0(减 1)
  const addressBase = Number(elements.addressBase?.value ?? 0);
  if (addressBase === 1 && startAddress > 0) startAddress -= 1;
  const quantity = Number(elements.quantity.value);
  const timeoutMs = Number(elements.commandTimeout.value);
  const isWrite = [5, 6, 15, 16].includes(functionCode);
  const isReadBits = [1, 2].includes(functionCode);
  const isReadRegisters = [3, 4].includes(functionCode);

  if (!Number.isInteger(unitId) || unitId < 0 || unitId > 247) {
    throw new Error("站号必须在 0 到 247 之间(0 为广播,仅写操作可用)。");
  }
  if (![1, 2, 3, 4, 5, 6, 15, 16].includes(functionCode)) {
    throw new Error("不支持的功能码。");
  }
  // 读操作不允许广播
  if (!isWrite && unitId === 0) {
    throw new Error("读取操作不允许使用广播站号 0。");
  }
  // 数量校验(读操作和写多条)
  const maxQty = isReadBits ? 2000 : isReadRegisters ? 125 : 0;
  if ((isReadBits || isReadRegisters) && (!Number.isInteger(quantity) || quantity < 1 || quantity > maxQty)) {
    throw new Error(`数量必须在 1 到 ${maxQty} 之间。`);
  }
  if (startAddress + quantity - 1 > 65_535) {
    throw new Error("起始地址加数量超过了寄存器地址上限 65535。");
  }
  if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 600_000) {
    throw new Error("指令超时必须在 1 到 600000 毫秒之间。");
  }
  return { unitId, functionCode, startAddress, quantity, timeoutMs, transport: currentTransport() };
}

function parseWriteValue(functionCode, raw) {
  const text = String(raw ?? "").trim();
  if (functionCode === 5) {
    return { value: /^(true|1|on|true)$/i.test(text) || text === "1" };
  }
  if (functionCode === 6) {
    const value = Number(text);
    if (!Number.isInteger(value) || value < 0 || value > 65535) {
      throw new Error("FC06 写入值必须是 0 到 65535 之间的整数。");
    }
    return { value };
  }
  if (functionCode === 15) {
    const parts = text.split(/[,\s]+/).filter(Boolean);
    const values = parts.map((p) => /^(true|1|on)$/i.test(p) || p === "1");
    if (values.length < 1 || values.length > 1968) {
      throw new Error("FC15 写入值必须是 1 到 1968 个布尔值(逗号分隔)。");
    }
    return { values };
  }
  if (functionCode === 16) {
    const parts = text.split(/[,\s]+/).filter(Boolean);
    const values = parts.map((p) => {
      const n = Number(p);
      if (!Number.isInteger(n) || n < 0 || n > 65535) throw new Error("FC16 写入值必须都是 0 到 65535 的整数。");
      return n;
    });
    if (values.length < 1 || values.length > 123) {
      throw new Error("FC16 写入值必须是 1 到 123 个整数(逗号分隔)。");
    }
    return { values };
  }
  return {};
}

function commandReadyText() {
  const functionCode = Number(elements.functionCode.value);
  return `FC${String(functionCode).padStart(2, "0")} 单次读取就绪`;
}

function formatHex(bytes) {
  return [...(bytes ?? [])].map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
}

function clockTime() {
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(new Date());
}

function clearEmptyRow(body) {
  if (body.querySelector(".console-empty, .empty-row")) body.replaceChildren();
}

function appendCells(row, values) {
  for (const value of values) {
    const cell = document.createElement("td");
    cell.textContent = String(value ?? "—");
    row.append(cell);
  }
}

function refreshStats() {
  elements.txCount.textContent = `TX ${stats.tx}`;
  elements.rxCount.textContent = `RX ${stats.rx}`;
  elements.timeoutCount.textContent = `超时 ${stats.timeout}`;
  elements.crcCount.textContent = `CRC 错误 ${stats.crc}`;
  elements.errorCount.textContent = `异常 ${stats.errors}`;
  elements.traceCount.textContent = String(stats.traces);
  elements.alarmCount.textContent = String(stats.alarms);
}

function appendTrace({ direction, unitId, functionCode, bytes, crc, elapsedMs, result }) {
  const timestamp = Date.now();
  const hex = bytes?.length ? formatHex(bytes) : "";
  // 记录到历史(用于导出,最多保留 5000 条)
  const record = { timestamp, direction, unitId, functionCode, hex, bytes: bytes ?? [], crc, elapsedMs, result };
  traceHistory.push(record);
  if (traceHistory.length > 5000) traceHistory.shift();
  stats.traces += 1;
  if (direction === "TX" && bytes?.length) stats.tx += 1;
  if (direction === "RX" && bytes?.length) stats.rx += 1;
  // 无查询时保持原有 prepend 行为(只追加一行,高效);有查询时整体重渲染过滤视图
  if (traceQuery.trim() === "") {
    clearEmptyRow(elements.traceRows);
    elements.traceRows.prepend(buildTraceRowFromRecord(record));
    while (elements.traceRows.children.length > 5000) {
      elements.traceRows.removeChild(elements.traceRows.lastChild);
    }
  } else {
    renderTraceRows(filterTrace(traceHistory, traceQuery));
  }
}

/** 把一条 traceHistory 记录渲染成 <tr>(与 appendTrace 原行结构一致) */
function buildTraceRowFromRecord(rec) {
  const row = document.createElement("tr");
  row.dataset.direction = String(rec.direction ?? "").toLowerCase();
  appendCells(row, [
    formatTraceTimestamp(rec.timestamp),
    rec.direction ?? "—",
    rec.unitId,
    String(rec.functionCode ?? 0).padStart(2, "0"),
    rec.hex || "—",
    rec.crc ?? "—",
    rec.elapsedMs == null ? "—" : `${rec.elapsedMs} ms`,
    rec.result ?? "—",
  ]);
  return row;
}

/** 把时间戳格式化为 HH:mm:ss(与 clockTime 同格式,但基于记录里的 timestamp) */
function formatTraceTimestamp(ts) {
  if (ts == null) return clockTime();
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(new Date(ts));
}

/** 整体重渲染报文行(用于搜索过滤/清空搜索时)。records 按时间升序,展示时倒序(最新在顶) */
function renderTraceRows(records) {
  elements.traceRows.replaceChildren();
  if (records.length === 0) {
    const empty = document.createElement("tr");
    empty.className = "empty-row";
    const td = document.createElement("td");
    td.colSpan = 8;
    td.textContent = traceQuery.trim() === "" ? "暂无通信记录" : "无匹配报文";
    empty.append(td);
    elements.traceRows.append(empty);
    return;
  }
  const frag = document.createDocumentFragment();
  for (let i = records.length - 1; i >= 0; i--) frag.append(buildTraceRowFromRecord(records[i]));
  elements.traceRows.append(frag);
}

function errorSuggestion(code) {
  if (code === "SERIAL_RESPONSE_TIMEOUT") return "检查站号、波特率、校验位、接线和从站电源。";
  if (code === "CRC_MISMATCH") return "检查串口格式、线路干扰、终端电阻和接地。";
  if (code === "MODBUS_EXCEPTION") return "检查寄存器地址、读取数量及设备说明书。";
  if (code === "UNIT_ID_MISMATCH") return "检查总线上是否存在重复站号或迟到响应。";
  return "确认串口仍在线，然后核对通信参数与设备状态。";
}

function appendAlarm(error) {
  clearEmptyRow(elements.alarmRows);
  const row = document.createElement("tr");
  appendCells(row, [
    clockTime(),
    "Modbus RTU",
    "错误",
    error.code,
    error.message,
    errorSuggestion(error.code),
  ]);
  elements.alarmRows.prepend(row);
  // 上限裁剪:掉线时 500ms 一条,不裁剪内存只增不减
  while (elements.alarmRows.children.length > 500) {
    elements.alarmRows.removeChild(elements.alarmRows.lastChild);
  }
  stats.alarms += 1;
}

async function renderRegisters(command, registers) {
  elements.registerResults.replaceChildren();
  const updatedAt = clockTime();
  const inputRegisters = command.functionCode === 4;
  const prefix = inputRegisters ? "IR" : "HR";
  const area = inputRegisters ? "输入寄存器" : "保持寄存器";
  const dataType = elements.displayType?.value || "Unsigned16";
  const regPerElem = getRegPerElem(dataType);

  // 尝试用 decode_values 解码(含缩放/偏移)
  const scaleFactor = Number(elements.scaleFactor?.value) || 1;
  const unit = elements.unitLabel?.value || "";
  let decodedValues = null;
  if (window.nexusDesktop) {
    try {
      const result = await callBackend("decode_values", {
        registers,
        dataType,
        offset: 0,
        count: Math.floor(registers.length / regPerElem) || 1,
        scale: scaleFactor !== 1 ? scaleFactor : undefined,
      });
      decodedValues = result?.values ?? null;
    } catch {
      decodedValues = null;
    }
  }

  const elemCount = decodedValues
    ? decodedValues.length
    : Math.ceil(registers.length / regPerElem);
  for (let i = 0; i < elemCount; i++) {
    const address = command.startAddress + i * regPerElem;
    let displayValue;
    if (decodedValues && decodedValues[i] !== undefined) {
      const v = decodedValues[i];
      displayValue = typeof v === "object" ? JSON.stringify(v) : String(v);
      if (unit) displayValue += ` ${unit}`;
    } else {
      displayValue = registers[i] ?? "—";
      if (unit) displayValue += ` ${unit}`;
    }
    const row = document.createElement("tr");
    appendCells(row, [
      "●",
      `${prefix} ${address}`,
      area,
      address,
      dataType,
      "ABCD",
      String(elements.scaleFactor?.value ?? "1"),
      unit || "—",
      displayValue,
      "Good",
      updatedAt,
    ]);
    row.lastElementChild.previousElementSibling.classList.add("quality-good");
    elements.registerResults.append(row);
  }
  elements.pointCount.textContent = `点位 ${elemCount}`;
}

function getRegPerElem(dataType) {
  if (!dataType) return 1;
  const dt = dataType.toLowerCase();
  if (dt.includes("double") || dt.includes("64")) return 4;
  if (dt.includes("32") || dt.includes("float")) return 2;
  return 1;
}

async function readRegistersOnce() {
  if (busy || !isConnected()) return;
  let command;
  try {
    command = readCommand();
  } catch (error) {
    setNotice("error", "参数无效", error.message);
    return;
  }

  setBusy(true);
  const functionLabel = `FC${String(command.functionCode).padStart(2, "0")}`;
  const registerLabel = command.functionCode === 4 ? "输入寄存器" : "保持寄存器";
  elements.commandState.textContent = `正在执行 ${functionLabel}`;
  setNotice("info", "正在读取", `站号 ${command.unitId}，地址 ${command.startAddress}，数量 ${command.quantity}。`);
  try {
    const backendCommand = command.functionCode === 4
      ? "read_input_registers_once"
      : "read_holding_registers_once";
    const response = await callBackend(backendCommand, command);
    appendTrace({
      direction: "TX",
      unitId: command.unitId,
      functionCode: command.functionCode,
      bytes: response.tx,
      crc: response.tx?.length ? "已生成" : "—",
      elapsedMs: null,
      result: `读取 ${command.startAddress}..${command.startAddress + command.quantity - 1}`,
    });

    const error = response.error ?? null;
    const rxCrc = response.crcValid === true ? "通过" : response.crcValid === false ? "失败" : "未校验";
    appendTrace({
      direction: "RX",
      unitId: command.unitId,
      functionCode: command.functionCode,
      bytes: response.rx,
      crc: rxCrc,
      elapsedMs: response.elapsedMs,
      result: response.ok ? `${response.registers.length} 个寄存器` : error?.message ?? "读取失败",
    });

    if (response.ok) {
      await renderRegisters(command, response.registers);
      setNotice("success", "读取完成", `收到 ${response.registers.length} 个${registerLabel}，CRC 校验通过。`);
    } else {
      const code = error?.code ?? "READ_FAILED";
      stats.errors += 1;
      if (code.includes("TIMEOUT")) stats.timeout += 1;
      if (code === "CRC_MISMATCH") stats.crc += 1;
      appendAlarm({ code, message: error?.message ?? "读取失败" });
      setNotice("error", "读取失败", error?.message ?? "Modbus 事务未完成。");
    }
    refreshStats();
  } catch (error) {
    stats.errors += 1;
    appendAlarm({ code: error.code ?? "IPC_ERROR", message: error.message ?? String(error) });
    refreshStats();
    setNotice("error", "读取失败", error.message ?? String(error));
  } finally {
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先打开串口";
  }
}

async function writeRegistersOnce() {
  if (busy || !isConnected()) return;
  let command;
  try {
    command = readCommand();
  } catch (error) {
    setNotice("error", "参数无效", error.message);
    return;
  }
  const { unitId, functionCode, startAddress: address, timeoutMs, transport } = command;
  let writePayload;
  try {
    writePayload = parseWriteValue(functionCode, elements.writeValue?.value);
  } catch (error) {
    setNotice("error", "写入值无效", error.message);
    return;
  }

  setBusy(true);
  const functionLabel = `FC${String(functionCode).padStart(2, "0")}`;
  elements.commandState.textContent = `正在执行 ${functionLabel} 写入`;
  setNotice("info", "正在写入", `站号 ${unitId}，地址 ${address}。`);
  try {
    const fcMap = {
      5: "write_single_coil_once",
      6: "write_single_register_once",
      15: "write_multiple_coils_once",
      16: "write_multiple_registers_once",
    };
    const backendCommand = fcMap[functionCode];
    const response = await callBackend(backendCommand, { unitId, address, timeoutMs, transport, ...writePayload });

    appendTrace({
      direction: "TX",
      unitId,
      functionCode,
      bytes: response.tx,
      crc: response.tx?.length ? "已生成" : "—",
      elapsedMs: null,
      result: `${functionLabel} 写 ${address}`,
    });
    const error = response.error ?? null;
    const rxCrc = response.crcValid === true ? "通过" : response.crcValid === false ? "失败" : "未校验";
    appendTrace({
      direction: "RX",
      unitId,
      functionCode,
      bytes: response.rx,
      crc: rxCrc,
      elapsedMs: response.elapsedMs,
      result: response.ok ? "写入成功" : error?.message ?? "写入失败",
    });

    if (response.ok) {
      if (response.broadcast) {
        setNotice("success", "广播发送", `已向站号 0 广播 ${functionLabel} 命令(无响应)。`);
      } else {
        setNotice("success", "写入完成", `${functionLabel} 地址 ${address} CRC 校验通过。`);
      }
    } else {
      const code = error?.code ?? "WRITE_FAILED";
      stats.errors += 1;
      if (code.includes("TIMEOUT")) stats.timeout += 1;
      appendAlarm({ code, message: error?.message ?? "写入失败" });
      setNotice("error", "写入失败", error?.message ?? "Modbus 写入未完成。");
    }
    refreshStats();
  } catch (error) {
    stats.errors += 1;
    appendAlarm({ code: error.code ?? "IPC_ERROR", message: error.message ?? String(error) });
    refreshStats();
    setNotice("error", "写入失败", error.message ?? String(error));
  } finally {
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先打开串口";
  }
}

function renderConnectionFault(message) {
  elements.connectionPill.dataset.state = "error";
  elements.connectionLabel.textContent = "串口异常";
  setNotice("error", "链路异常", message);
  syncActionState();
}

async function callBackend(command, args = {}) {
  if (window.nexusDesktop) return window.nexusDesktop.invoke(command, args);
  if (window.__TAURI_INTERNALS__) {
    const { invoke } = await import("@tauri-apps/api/core");
    return invoke(command, args);
  }
  if (!isDesktop()) {
    if (command === "list_serial_ports") return [];
    if (command === "get_serial_status") return { isOpen: false, config: null };
    throw new Error("浏览器预览只能检查界面；真实串口操作需要在 Electron 桌面窗口中运行。");
  }
  throw new Error("桌面通信桥未初始化。");
}

async function refreshPorts({ quiet = false } = {}) {
  if (busy) return;
  setBusy(true);
  if (!quiet) setNotice("info", "正在刷新", "正在向 Windows 查询可用串口。");
  const previous = elements.portName.value;
  try {
    const ports = await callBackend("list_serial_ports");
    elements.portName.replaceChildren();
    if (!ports.length) {
      elements.portName.add(new Option("未发现串口", ""));
      elements.portHint.textContent = isDesktop() ? "请检查设备、电源、驱动和 USB 连接。" : "浏览器预览模式（需桌面版读取真实端口）";
    } else {
      for (const port of ports) elements.portName.add(new Option(port.displayName || port.name, port.name));
      if (ports.some((port) => port.name === previous)) elements.portName.value = previous;
      elements.portHint.textContent = "端口列表来自 Electron 串口传输层。";
    }
    elements.portCount.textContent = `${ports.length} 个端口`;
    if (!quiet) setNotice("success", "刷新完成", ports.length ? `发现 ${ports.length} 个串口。` : "没有发现可用串口。");
  } catch (error) {
    setNotice("error", "刷新失败", String(error));
  } finally {
    setBusy(false);
  }
}

async function openPort(event) {
  event.preventDefault();
  if (busy) return;
  if (!elements.form.reportValidity()) return;
  const config = readConfig();
  if (!config.portName) {
    setNotice("error", "缺少串口", "请选择一个真实串口后再打开。");
    return;
  }
  setBusy(true);
  setNotice("info", "正在打开", `正在应用 ${config.portName} 的通信参数。`);
  try {
    const status = await callBackend("open_serial_port", { config });
    renderStatus(status);
    setNotice("success", "串口句柄已打开", `${config.portName} 已建立独占句柄；尚未验证从站通信。`);
    persistConfig(); // 保存连接配置
  } catch (error) {
    renderConnectionFault(`打开失败：${String(error)}`);
  } finally {
    setBusy(false);
  }
}

async function closePort() {
  await stopPoll().catch(() => {});
  if (busy) return;
  setBusy(true);
  try {
    const status = await callBackend("close_serial_port");
    renderStatus(status);
    setNotice("success", "串口已关闭", "系统已释放串口句柄。");
  } catch (error) {
    setNotice("error", "关闭失败", String(error));
  } finally {
    setBusy(false);
  }
}

function restoreDefaults() {
  for (const [key, value] of Object.entries(defaults)) {
    const control = elements.form.elements.namedItem(key);
    if (control) control.value = String(value);
  }
  updateDependentControls();
  setNotice("info", "已恢复默认值", `${defaults.baudRate}、${defaults.dataBits}${parityLetter(defaults.parity)}${defaults.stopBits}、无流控。`);
}

function activateConsole(panelName) {
  elements.workspace.dataset.console = "open";
  elements.consoleToggle.textContent = "收起";
  for (const tab of elements.consoleTabs) {
    const selected = tab.dataset.panel === panelName;
    tab.classList.toggle("is-active", selected);
    tab.setAttribute("aria-selected", String(selected));
  }
  for (const panel of elements.consolePanels) {
    const selected = panel.dataset.consolePanel === panelName;
    panel.classList.toggle("is-active", selected);
    panel.hidden = !selected;
  }
}

function toggleConsole() {
  const collapsed = elements.workspace.dataset.console === "collapsed";
  elements.workspace.dataset.console = collapsed ? "open" : "collapsed";
  elements.consoleToggle.textContent = collapsed ? "收起" : "展开";
}

async function initialise() {
  elements.refresh.addEventListener("click", () => refreshPorts());
  elements.form.addEventListener("submit", openPort);
  elements.close.addEventListener("click", closePort);
  elements.restore.addEventListener("click", restoreDefaults);
  elements.flowControl.addEventListener("change", updateDependentControls);
  // 电气接口类型联动:RS-485 总线自动启用 RTS 收发切换(半双工方向控制)
  const interfaceSel = document.querySelector("#interface-type");
  if (interfaceSel) {
    interfaceSel.addEventListener("change", () => {
      if (interfaceSel.value === "rs485") {
        elements.rtsMode.value = "auto-toggle";
        if (elements.portHint) elements.portHint.textContent = "RS-485:RTS 自动切换已启用;总线两端设备请接 120Ω 终端电阻";
      } else if (elements.rtsMode.value === "auto-toggle") {
        elements.rtsMode.value = "preserve";
        if (elements.portHint) elements.portHint.textContent = "";
      }
    });
  }
  elements.readOnce.addEventListener("click", readRegistersOnce);
  elements.writeOnce.addEventListener("click", writeRegistersOnce);
  elements.functionCode.addEventListener("change", () => {
    if (isConnected() && !busy) elements.commandState.textContent = commandReadyText();
    syncActionState();
  });
  for (const radio of elements.transportRadios) {
    radio.addEventListener("change", () => { updateTransportVisibility(); persistConfig(); });
  }
  if (elements.connectTcp) elements.connectTcp.addEventListener("click", connectTcp);
  if (elements.disconnectTcp) elements.disconnectTcp.addEventListener("click", disconnectTcp);
  if (elements.scanStations) elements.scanStations.addEventListener("click", scanStations);
  if (elements.scanBaud) elements.scanBaud.addEventListener("click", scanBaudRate);
  if (elements.startPoll) elements.startPoll.addEventListener("click", startPoll);
  if (elements.stopPoll) elements.stopPoll.addEventListener("click", stopPoll);
  if (elements.addCmd) elements.addCmd.addEventListener("click", addCurrentCommand);
  if (elements.clearCmd) elements.clearCmd.addEventListener("click", clearCommands);
  if (elements.executeCmds) elements.executeCmds.addEventListener("click", executeCommands);
  // View 切换 + 从站
  for (const tab of elements.viewTabs) {
    tab.addEventListener("click", () => activateView(tab.dataset.view));
  }
  if (elements.slaveStart) elements.slaveStart.addEventListener("click", startSlave);
  if (elements.slaveStop) elements.slaveStop.addEventListener("click", stopSlave);
  if (elements.slaveReadMem) elements.slaveReadMem.addEventListener("click", readSlaveMemory);
  if (elements.slaveClearMem) elements.slaveClearMem.addEventListener("click", clearSlaveMemory);
  if (elements.slaveSetBtn) elements.slaveSetBtn.addEventListener("click", setSlaveValue);
  if (elements.slaveFillRandom) elements.slaveFillRandom.addEventListener("click", fillSlaveRandom);
  if (elements.slaveMode) elements.slaveMode.addEventListener("change", updateSlaveModeVisibility);
  // 串口调试
  if (elements.dbgSend) elements.dbgSend.addEventListener("click", debugSend);
  if (elements.dbgClearInput) elements.dbgClearInput.addEventListener("click", () => { if (elements.dbgInput) elements.dbgInput.value = ""; });
  if (elements.dbgAttach) elements.dbgAttach.addEventListener("click", debugAttach);
  if (elements.dbgClearLog) elements.dbgClearLog.addEventListener("click", debugClearLog);
  if (elements.dbgCalcCrc) elements.dbgCalcCrc.addEventListener("click", () => calcChecksum("crc"));
  if (elements.dbgCalcLrc) elements.dbgCalcLrc.addEventListener("click", () => calcChecksum("lrc"));
  if (elements.dbgAllowRx) elements.dbgAllowRx.addEventListener("change", (e) => callBackend("debug_set_receive", { enabled: e.target.checked }));
  if (elements.dbgAllowTx) elements.dbgAllowTx.addEventListener("change", (e) => callBackend("debug_set_send", { enabled: e.target.checked }));
  if (elements.dbgAppendCrc) elements.dbgAppendCrc.addEventListener("change", (e) => callBackend("debug_set_crc", { enabled: e.target.checked }));
  // 接收 debug_frame 推送
  if (window.nexusDesktop?.onDebugFrame) {
    window.nexusDesktop.onDebugFrame((record) => appendDebugLog(record));
  }
  // 报文解析
  if (elements.parserParse) elements.parserParse.addEventListener("click", parseFrame);
  if (elements.parserClear) elements.parserClear.addEventListener("click", () => { if (elements.parserInput) elements.parserInput.value = ""; if (elements.parserResult) elements.parserResult.innerHTML = '<div class="console-empty">输入 HEX 后点"解析报文"</div>'; });
  // G8: 多会话标签页
  if (elements.addSessionTab) elements.addSessionTab.addEventListener("click", addSessionTab);
  if (elements.addPoint) elements.addPoint.addEventListener("click", addPoint);
  if (elements.importPoints) elements.importPoints.addEventListener("click", importPoints);
  if (elements.savePoints) elements.savePoints.addEventListener("click", savePoints);
  // 导出按钮
  const exportDataBtn = document.querySelector("#export-data");
  if (exportDataBtn) exportDataBtn.addEventListener("click", exportRegisterData);
  const exportTraceBtn = document.querySelector("#export-trace");
  if (exportTraceBtn) exportTraceBtn.addEventListener("click", exportTraceData);
  // 报文搜索过滤
  const packetSearch = document.querySelector("#packet-search");
  if (packetSearch) {
    packetSearch.addEventListener("input", () => {
      traceQuery = packetSearch.value;
      renderTraceRows(filterTrace(traceHistory, traceQuery));
    });
  }
  // SSE 推送开关
  const sseBtn = document.querySelector("#toggle-sse");
  if (sseBtn) sseBtn.addEventListener("click", toggleSse);
  // 高级功能码
  const advFcSelect = document.querySelector("#adv-fc-select");
  if (advFcSelect) advFcSelect.addEventListener("change", renderAdvFcParams);
  const advFcExec = document.querySelector("#adv-fc-exec");
  if (advFcExec) advFcExec.addEventListener("click", executeAdvFc);
  renderAdvFcParams(); // 初始化参数行

  // 实时趋势图
  if (elements.trendAdd) elements.trendAdd.addEventListener("click", trendAddSelected);
  if (elements.trendClear) elements.trendClear.addEventListener("click", trendClearAll);
  if (elements.trendPointSelect) {
    // 打开下拉前刷新点位列表(表格内容随轮询/会话切换动态变化)
    elements.trendPointSelect.addEventListener("focus", trendRefreshPointOptions);
    elements.trendPointSelect.addEventListener("pointerdown", trendRefreshPointOptions);
  }
  startTrendLoop();

  // 接收轮询数据推送(从 Electron 主进程)
  if (window.nexusDesktop?.onPollData) {
    window.nexusDesktop.onPollData((data) => {
      handlePollData(data);
    });
    window.nexusDesktop.onPollError((error) => {
      stats.errors += 1;
      appendAlarm({ code: error.code ?? "POLL_ERROR", message: error.message ?? "轮询错误" });
      refreshStats();
    });
  }
  for (const tab of elements.consoleTabs) tab.addEventListener("click", () => activateConsole(tab.dataset.panel));
  elements.consoleToggle.addEventListener("click", toggleConsole);

  // 右侧报文面板折叠/展开
  const toggleBtn = document.querySelector("#toggle-console");
  if (toggleBtn) {
    toggleBtn.addEventListener("click", () => {
      const packetPanel = document.querySelector(".packet-panel");
      if (packetPanel) packetPanel.classList.toggle("collapsed");
      toggleBtn.textContent = packetPanel?.classList.contains("collapsed") ? "◀" : "▶";
    });
  }

  try {
    renderStatus(await callBackend("get_serial_status"));
  } catch (error) {
    setNotice("error", "状态读取失败", String(error));
  }
  await refreshPorts({ quiet: true });
  // 恢复上次配置和点表(localStorage 持久化)
  restoreConfig();
  restorePointTable();
  // 三菱 MC 页面初始化
  initMelsecUi();
  initSiemensUi();
  initOmronUi();
  initInterfacesUi();
  const diagBtn = document.querySelector("#export-diagnostics");
  if (diagBtn) diagBtn.addEventListener("click", async () => {
    try {
      const r = await callBackend("export_diagnostics");
      if (r.ok) setNotice("success", "诊断报告已导出", "桌面:" + r.path);
      else setNotice("error", "导出失败", r.message || "");
    } catch (e) { setNotice("error", "导出失败", e.message || String(e)); }
  });

  // 示例代码生成:监听传输/串口/TCP/命令字段,变化即重新生成当前页签代码
  const codeWatchSelectors = [
    "#port-name", "#baud-rate", "#parity", "#data-bits", "#stop-bits", "#unit-id",
    "#tcp-host", "#tcp-port",
    "#function-code", "#start-address", "#address-base", "#quantity", "#display-type", "#write-value",
  ];
  for (const selector of codeWatchSelectors) {
    const control = document.querySelector(selector);
    if (!control) continue;
    control.addEventListener("input", renderCodeSample);
    control.addEventListener("change", renderCodeSample);
  }
  for (const radio of elements.transportRadios) {
    radio.addEventListener("change", renderCodeSample);
  }
  for (const tab of document.querySelectorAll(".code-tab")) {
    tab.addEventListener("click", () => activateCodeTab(tab.dataset.lang));
  }
  const codeCopyBtn = document.querySelector("#code-copy");
  if (codeCopyBtn) codeCopyBtn.addEventListener("click", copyCodeSample);
  renderCodeSample(); // 首次生成
}

void initialise().finally(() => {
  document.documentElement.dataset.nexusUiReady = "true";
});
