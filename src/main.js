import "./app.css";
import { buildPollPlan, splitBatchResult } from "./poll-planner.js";
import { filterTrace } from "./trace-filter.js";
import { listProtocolGuideVariants, resolveProtocolGuide } from "./protocol-guides.js";
import { formatDeviceSeries } from "./device-labels.js";
import { resolveSiemensRoute } from "./siemens-route.js";
import { resolveMelsecRoute } from "./melsec-route.js";
import {
  isModbusBitFunction,
  modbusNetworkPrefix,
  resolveModbusConnect,
  resolveModbusReadCommand,
  resolveModbusWriteCommand,
} from "./modbus-route.js";
import { readPointImportFile } from "./point-import.js";
import { initCardCollapse } from "./card-collapse.js";
import {
  buildGx3Presentation,
  formatGx3TechnicalReport,
  parseGx3DevicePresentation,
} from "./gx3-presenter.js";
import {
  SeriesStore,
  evalManualRule,
  pairRegisterChannels,
  buildCsvRows,
  drawSerialPlot,
  renderPlotLegend,
  parseHeadHex,
  PLOT_MAX_DISCOVERED,
} from "./serial-plot.js";
import { ReplayScheduler, normalizeReplayRecords } from "./replay-scheduler.js";

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
  scanAll: document.querySelector("#scan-all"),
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
  plotCanvas: document.querySelector("#plot-canvas"),
  plotLegend: document.querySelector("#plot-legend"),
  plotChannelSelect: document.querySelector("#plot-channel-select"),
  plotAdd: document.querySelector("#plot-add"),
  plotPause: document.querySelector("#plot-pause"),
  plotClear: document.querySelector("#plot-clear"),
  plotExport: document.querySelector("#plot-export"),
  plotAutoParse: document.querySelector("#plot-auto-parse"),
  plotParseTransport: document.querySelector("#plot-parse-transport"),
  plotManName: document.querySelector("#plot-man-name"),
  plotManHead: document.querySelector("#plot-man-head"),
  plotManOffset: document.querySelector("#plot-man-offset"),
  plotManType: document.querySelector("#plot-man-type"),
  plotManOrder: document.querySelector("#plot-man-order"),
  plotManScale: document.querySelector("#plot-man-scale"),
  plotManUnit: document.querySelector("#plot-man-unit"),
  plotManAdd: document.querySelector("#plot-man-add"),
  recToggle: document.querySelector("#rec-toggle"),
  recState: document.querySelector("#rec-state"),
  replayOpen: document.querySelector("#replay-open"),
  replayToggle: document.querySelector("#replay-toggle"),
  replaySpeed: document.querySelector("#replay-speed"),
  replayStep: document.querySelector("#replay-step"),
  replayExport: document.querySelector("#replay-export"),
  replayState: document.querySelector("#replay-state"),
  fdList: document.querySelector("#fd-list"),
  fdLoad: document.querySelector("#fd-load"),
  fdSave: document.querySelector("#fd-save"),
  fdDelete: document.querySelector("#fd-delete"),
  fdApply: document.querySelector("#fd-apply"),
  fdName: document.querySelector("#fd-name"),
  fdMode: document.querySelector("#fd-mode"),
  fdBinaryOpts: document.querySelector("#fd-binary-opts"),
  fdBinaryRow2: document.querySelector("#fd-binary-row2"),
  fdLfOpts: document.querySelector("#fd-lf-opts"),
  fdAsciiOpts: document.querySelector("#fd-ascii-opts"),
  fdHead: document.querySelector("#fd-head"),
  fdLenSrc: document.querySelector("#fd-len-src"),
  fdLength: document.querySelector("#fd-length"),
  fdLengthLabel: document.querySelector("#fd-length-label"),
  fdLfOffset: document.querySelector("#fd-lf-offset"),
  fdLfType: document.querySelector("#fd-lf-type"),
  fdLfOrder: document.querySelector("#fd-lf-order"),
  fdLfAdjust: document.querySelector("#fd-lf-adjust"),
  fdTail: document.querySelector("#fd-tail"),
  fdChecksum: document.querySelector("#fd-checksum"),
  fdLineEnding: document.querySelector("#fd-line-ending"),
  fdSeparator: document.querySelector("#fd-separator"),
  fdFieldsRows: document.querySelector("#fd-fields-rows"),
  fdAddField: document.querySelector("#fd-add-field"),
  fdTry: document.querySelector("#fd-try"),
  fdPreview: document.querySelector("#fd-preview"),
  parserView: document.querySelector("#parser-view"),
  parserTransport: document.querySelector("#parser-transport"),
  parserInput: document.querySelector("#parser-input"),
  parserParse: document.querySelector("#parser-parse"),
  parserClear: document.querySelector("#parser-clear"),
  parserResult: document.querySelector("#parser-result"),
  gx3View: document.querySelector("#gx3-view"),
  gx3ToolState: document.querySelector("#gx3-tool-state"),
  gx3ProjectPath: document.querySelector("#gx3-project-path"),
  gx3SelectProject: document.querySelector("#gx3-select-project"),
  gx3AnalyzeProject: document.querySelector("#gx3-analyze-project"),
  gx3AnalysisSummary: document.querySelector("#gx3-analysis-summary"),
  gx3AnalysisState: document.querySelector("#gx3-analysis-state"),
  gx3HumanReport: document.querySelector("#gx3-human-report"),
  gx3TechnicalDetails: document.querySelector("#gx3-technical-details"),
  gx3AnalysisOutput: document.querySelector("#gx3-analysis-output"),
  gx3Device: document.querySelector("#gx3-device"),
  gx3QueryDevice: document.querySelector("#gx3-query-device"),
  gx3DeviceSummary: document.querySelector("#gx3-device-summary"),
  gx3DeviceTechnical: document.querySelector("#gx3-device-technical"),
  gx3DeviceOutput: document.querySelector("#gx3-device-output"),
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
  projectName: document.querySelector("#project-name"),
  projectNew: document.querySelector("#project-new"),
  projectOpen: document.querySelector("#project-open"),
  projectSave: document.querySelector("#project-save"),
  projectSaveAs: document.querySelector("#project-save-as"),
  projectExportSanitized: document.querySelector("#project-export-sanitized"),
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
let activeGx3Analysis = null;
let gx3Busy = false;
let gx3Available = false;
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
  if (elements.scanAll) elements.scanAll.disabled = busy || !connected;
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
  if (elements.scanAll) elements.scanAll.disabled = busy || tcpMode; // 一键扫描仅串口
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
    const route = resolveModbusConnect(transport);
    if (!route.command) {
      throw new Error("当前传输不是 TCP/UDP，请先选择网口方式再连接。");
    }
    const t0 = performance.now();
    await callBackend(route.command, {
      connectionId: "default",
      host,
      port,
      unitId,
      framing: route.framing,
    });
    const ms = performance.now() - t0;
    tcpConnected = true;
    elements.connectionPill.dataset.state = "open";
    elements.connectionLabel.textContent = `${transport.toUpperCase()} ${host}:${port} · ${ms.toFixed(0)} ms`;
    elements.commandState.textContent = commandReadyText();
    setNotice("success", "已连接", `${transport.toUpperCase()} ${host}:${port} 站号 ${unitId} · 连接耗时 ${ms.toFixed(0)} ms`);
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
    const selectButton = document.createElement("button");
    selectButton.type = "button";
    selectButton.className = "btn-ghost btn-sm";
    selectButton.textContent = "选用";
    selectButton.addEventListener("click", () => {
      if (elements.unitId) elements.unitId.value = String(station.stationId);
    });
    appendCells(row, [
      station.stationId,
      "—",
      "—",
      `${station.firstResponseMs ?? 0} ms`,
      "FC03",
      "在线",
      selectButton,
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
      const readCommandName = resolveModbusReadCommand(currentTransport(), fc);
      if (!readCommandName) continue;
      let result;
      if (modbusNetworkPrefix(currentTransport())) {
        const network = await callBackend(readCommandName, {
          connectionId: "default",
          startAddress: batch.startAddress,
          quantity: batch.quantity,
        });
        result = {
          ok: !network.exceptionCode,
          coils: network.coils,
          registers: network.registers,
        };
      } else {
        result = await callBackend(readCommandName, args);
      }

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

/** 从项目/会话恢复趋势选择:只恢复 key,不伪造历史数据。 */
function restoreTrendSelection(rowKeys) {
  trendSeries.clear();
  renderTrendLegend();
  for (const rowKey of Array.isArray(rowKeys) ? rowKeys : []) {
    if (typeof rowKey !== "string" || !/^reg-(?:HR|IR)-[0-9]{1,5}$/.test(rowKey)) continue;
    const row = elements.registerResults.querySelector(`tr[data-key="${rowKey}"]`);
    if (!row) continue;
    const name = row.querySelectorAll("td")[1]?.textContent.trim() || rowKey;
    const usedColors = new Set([...trendSeries.values()].map((series) => series.color));
    const color = TREND_COLORS.find((candidate) => !usedColors.has(candidate))
      ?? TREND_COLORS[trendSeries.size % TREND_COLORS.length];
    trendSeries.set(rowKey, { name, color, dataPoints: [], maxPoints: 300 });
  }
  renderTrendLegend();
  trendRefreshPointOptions();
  drawTrendChart();
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

// === 串口实时曲线(调试页) ===

const plotStore = new SeriesStore();
const plotDiscovered = new Map(); // 自动解析发现的通道 key -> { key, name }
let plotLastTxInfo = null; // 最近一次成功解析的 TX 请求(为 RX 响应提供寄存器起始地址)
let plotFrameBatch = []; // 待解析收发帧(50ms 合批, 降低逐帧 IPC 频率)
let plotFlushTimer = null;
let plotParseBusy = false;
let plotRafId = null;
let plotLastDrawAt = 0;
let plotCanvasW = 0;
let plotCanvasH = 0;

/** onDebugFrame 入口: 手动规则同步求值(仅收包);自动解析按 50ms 合批走 parse_frame_online */
function plotFeedRecord(record) {
  if (!record || !Array.isArray(record.bytes)) return;
  if (record.direction === "RX") {
    for (const series of plotStore.entries()) {
      if (!series.rule) continue;
      const value = evalManualRule(series.rule, record.bytes);
      if (value !== null) plotStore.feed(series.key, record.timestamp, value);
    }
  }
  if (!elements.plotAutoParse?.checked) return;
  if (record.direction !== "RX" && record.direction !== "TX") return;
  plotFrameBatch.push(record);
  if (plotFlushTimer === null) plotFlushTimer = setTimeout(flushPlotBatch, 50);
}

async function flushPlotBatch() {
  plotFlushTimer = null;
  if (plotParseBusy) {
    if (plotFrameBatch.length > 0) plotFlushTimer = setTimeout(flushPlotBatch, 50);
    return;
  }
  const batch = plotFrameBatch;
  plotFrameBatch = [];
  plotParseBusy = true;
  try {
    for (const record of batch) {
      let info = null;
      try {
        info = await callBackend("parse_frame_online", {
          bytes: record.bytes,
          transport: elements.plotParseTransport?.value || "rtu",
        });
      } catch {
        info = null; // 非目标协议的帧解析失败,静默跳过
      }
      if (!info) continue;
      // 自定义帧定义(批次 2): 独立于 Modbus 解析,非标协议帧也能出通道
      if (activeFrameDef && record.direction === "RX") {
        try {
          const custom = await callBackend("custom_frame_parse", {
            definition: activeFrameDef,
            bytes: record.bytes,
          });
          if (custom?.status === "ok" && Array.isArray(custom.fields)) {
            for (const field of custom.fields) {
              const key = `fd:${activeFrameDef.name}.${field.name}`;
              if (!plotDiscovered.has(key) && plotDiscovered.size < PLOT_MAX_DISCOVERED) {
                plotDiscovered.set(key, {
                  key,
                  name: `${activeFrameDef.name}.${field.name}`,
                  unit: field.unit || "",
                });
              }
              plotStore.feed(key, record.timestamp, field.value);
            }
          }
        } catch { /* 单帧解析失败静默跳过 */ }
      }
      if (record.direction === "TX") {
        plotLastTxInfo = info.isValid ? info : null;
        continue;
      }
      if (!info.isValid || info.isException) continue;
      for (const channel of pairRegisterChannels(plotLastTxInfo, info)) {
        if (!plotDiscovered.has(channel.key) && plotDiscovered.size < PLOT_MAX_DISCOVERED) {
          plotDiscovered.set(channel.key, { key: channel.key, name: channel.name });
        }
        plotStore.feed(channel.key, record.timestamp, channel.value);
      }
    }
  } finally {
    plotParseBusy = false;
    if (plotFrameBatch.length > 0 && plotFlushTimer === null) {
      plotFlushTimer = setTimeout(flushPlotBatch, 50);
    }
  }
}

function plotRefreshChannelOptions() {
  const select = elements.plotChannelSelect;
  if (!select) return;
  const previous = select.value;
  select.replaceChildren(new Option("选择通道…", ""));
  const channels = [...plotDiscovered.values()].sort((a, b) => a.key.localeCompare(b.key));
  for (const channel of channels) {
    const option = new Option(channel.name, channel.key);
    option.disabled = plotStore.has(channel.key); // 已添加的置灰,防重复
    select.append(option);
  }
  if ([...select.options].some((o) => o.value === previous)) select.value = previous;
}

function plotRenderLegend() {
  renderPlotLegend(elements.plotLegend, plotStore, plotRemoveSeries);
}

function plotAddSelected() {
  const key = elements.plotChannelSelect?.value;
  if (!key) {
    setNotice("error", "未选择通道", "请先从下拉框选择一个自动解析发现的通道。");
    return;
  }
  const channel = plotDiscovered.get(key);
  if (!channel) return;
  if (plotStore.has(key)) {
    setNotice("info", "已存在", "该通道已在曲线图中。");
    return;
  }
  plotStore.add(key, { name: channel.name });
  plotRenderLegend();
  plotRefreshChannelOptions();
  elements.plotChannelSelect.value = ""; // 复位占位项,避免停在已添加的 disabled 选项上
  setNotice("success", "已添加曲线", `${channel.name}（等待收包数据…）`);
}

function plotAddManual() {
  const name = (elements.plotManName?.value || "").trim();
  if (!name) {
    setNotice("error", "缺少名称", "请填写手动通道名称。");
    return;
  }
  const key = `man:${name}`;
  if (plotStore.has(key)) {
    setNotice("info", "已存在", `手动通道「${name}」已在曲线图中。`);
    return;
  }
  let head = null;
  try {
    head = parseHeadHex(elements.plotManHead?.value);
  } catch (error) {
    setNotice("error", "帧头不合法", error.message || String(error));
    return;
  }
  const type = elements.plotManType?.value || "u16";
  const order = elements.plotManOrder?.value === "le" ? "le" : "be";
  const offset = Number(elements.plotManOffset?.value);
  const scale = Number(elements.plotManScale?.value);
  if (!Number.isInteger(offset) || offset < 0) {
    setNotice("error", "偏移不合法", "偏移必须是不小于 0 的整数（帧首字节为 0）。");
    return;
  }
  if (!Number.isFinite(scale) || scale === 0) {
    setNotice("error", "缩放不合法", "缩放必须是有效数字且不为 0（不缩放填 1）。");
    return;
  }
  plotStore.add(key, {
    name,
    unit: (elements.plotManUnit?.value || "").trim(),
    rule: { head, offset, type, order, scale },
  });
  plotRenderLegend();
  drawSerialPlot(elements.plotCanvas, plotStore, { legendHost: elements.plotLegend });
  setNotice("success", "已添加通道", `手动通道「${name}」已生效（等待匹配收包…）`);
}

function plotRemoveSeries(key) {
  if (!plotStore.remove(key)) return;
  plotRenderLegend();
  plotRefreshChannelOptions();
  if (plotStore.size === 0) drawSerialPlot(elements.plotCanvas, plotStore, { legendHost: elements.plotLegend });
}

function plotTogglePause() {
  plotStore.paused = !plotStore.paused;
  if (elements.plotPause) elements.plotPause.textContent = plotStore.paused ? "继续" : "暂停";
}

function plotClearAll() {
  if (plotStore.size === 0) return;
  plotStore.clear();
  plotRenderLegend();
  plotRefreshChannelOptions();
  drawSerialPlot(elements.plotCanvas, plotStore, { legendHost: elements.plotLegend });
  setNotice("info", "已清空", "所有曲线通道已移除（已发现的通道列表保留）。");
}

async function plotExportCsv() {
  const rows = buildCsvRows(plotStore);
  if (rows.length === 0) {
    setNotice("error", "无数据", "曲线还没有数据点，无法导出。");
    return;
  }
  const stamp = new Date().toISOString().replace(/[:T]/g, "-").slice(0, 19);
  try {
    const result = await callBackend("export_csv", { rows, filename: `nexus_serial_plot_${stamp}` });
    setNotice("success", "已导出 CSV", result?.path || "桌面 nexus_serial_plot_*.csv 已生成。");
  } catch (error) {
    setNotice("error", "导出失败", error.message || String(error));
  }
}

/** 绘制循环: rAF + 100ms 节流, 仅调试视图可见且未暂停时绘制(与主站 trendLoop 由 activeView 天然互斥) */
function plotLoop(timestamp) {
  plotRafId = requestAnimationFrame(plotLoop);
  if (timestamp - plotLastDrawAt < 100) return;
  plotLastDrawAt = timestamp;
  if (activeView !== "debug" || document.hidden) return;
  const canvas = elements.plotCanvas;
  if (!canvas || !canvas.isConnected) return;
  if (plotStore.paused) return;
  // 无曲线时只在画布尺寸变化时重绘空状态
  if (plotStore.size === 0 && canvas.clientWidth === plotCanvasW && canvas.clientHeight === plotCanvasH) return;
  drawSerialPlot(canvas, plotStore, { legendHost: elements.plotLegend });
  plotCanvasW = canvas.clientWidth;
  plotCanvasH = canvas.clientHeight;
}

function startPlotLoop() {
  if (plotRafId !== null) return;
  plotRafId = requestAnimationFrame(plotLoop);
}

// === 帧解析(批次 2)与会话录制/回放(批次 3) ===

let frameDefs = []; // 已保存的帧定义(随 .nexus.json workspace 持久化)
let activeFrameDef = null; // 已"应用到曲线"的定义;null=未启用
let lastDebugRxBytes = null; // 最近一个收包(帧解析试算用)
let recRecording = false;
let replayScheduler = null;
let replayPath = null; // 最近加载的录制文件(导出 CSV 用)

const FD_LINE_ENDING = { lf: "\n", crlf: "\r\n" };
const FD_LINE_ENDING_REV = { "\n": "lf", "\r\n": "crlf" };
const FD_SEPARATOR = { ",": ",", " ": " ", ";": ";", tab: "\t" };
const FD_SEPARATOR_REV = { ",": ",", " ": " ", ";": ";", "\t": "tab" };

function fdUpdateModeVisibility() {
  const binary = elements.fdMode?.value !== "ascii-delimited";
  if (elements.fdBinaryOpts) elements.fdBinaryOpts.style.display = binary ? "" : "none";
  if (elements.fdBinaryRow2) elements.fdBinaryRow2.style.display = binary ? "" : "none";
  if (elements.fdAsciiOpts) elements.fdAsciiOpts.style.display = binary ? "none" : "";
  const dynamic = binary && elements.fdLenSrc?.value === "field";
  if (elements.fdLfOpts) elements.fdLfOpts.style.display = dynamic ? "" : "none";
  if (elements.fdLengthLabel) elements.fdLengthLabel.style.visibility = dynamic ? "hidden" : "";
  if (elements.fdLength) elements.fdLength.style.visibility = dynamic ? "hidden" : "";
}

function fdFieldRow(field = {}) {
  const row = document.createElement("tr");
  const cell = () => row.insertCell(-1);
  const input = (props, width, titleText) => {
    const el = document.createElement("input");
    el.className = "input";
    Object.assign(el, props);
    el.style.width = width;
    if (titleText) el.title = titleText;
    return el;
  };
  const select = (options, value, width) => {
    const el = document.createElement("select");
    el.className = "input";
    el.style.width = width;
    for (const [val, label] of options) {
      const option = new Option(label, val);
      option.selected = val === value;
      el.append(option);
    }
    return el;
  };
  const name = input({ placeholder: "temp1", value: field.name ?? "" }, "100px");
  const offset = input({ type: "number", min: 0, max: 65535, value: field.offset ?? "" }, "52px", "binary:数据起始字节,帧首字节为 0");
  const index = input({ type: "number", min: 0, max: 65535, value: field.index ?? "" }, "52px", "ascii:分隔后第几段,从 0 起");
  const type = select([["u16", "u16"], ["i16", "i16"], ["u8", "u8"], ["u32", "u32"], ["i32", "i32"], ["f32", "f32"]], field.fieldType ?? "u16", "64px");
  const order = select([["be", "大端"], ["le", "小端"]], field.byteOrder ?? "be", "68px");
  const scale = input({ value: field.scale ?? 1 }, "56px", "原始值 × 缩放 = 曲线值");
  const unit = input({ placeholder: "℃", value: field.unit ?? "" }, "50px");
  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "btn-text";
  remove.textContent = "×";
  remove.setAttribute("aria-label", "删除字段");
  remove.addEventListener("click", () => { row.remove(); });
  cell().append(name);
  cell().append(offset);
  cell().append(index);
  cell().append(type);
  cell().append(order);
  cell().append(scale);
  cell().append(unit);
  cell().append(remove);
  return row;
}

function fdRenderFields(fields = []) {
  const body = elements.fdFieldsRows;
  if (!body) return;
  body.replaceChildren();
  if (fields.length === 0) {
    const empty = document.createElement("tr");
    empty.className = "empty-row";
    const cellNode = empty.insertCell(-1);
    cellNode.colSpan = 8;
    cellNode.className = "console-empty";
    cellNode.textContent = "点「+ 字段」添加要提取的数值";
    body.append(empty);
    return;
  }
  for (const field of fields) body.append(fdFieldRow(field));
}

function fdCollectFields() {
  const fields = [];
  const rows = elements.fdFieldsRows?.querySelectorAll("tr:not(.empty-row)") ?? [];
  for (const row of rows) {
    const [name, offset, index, type, order, scale, unit] = row.querySelectorAll("input,select");
    const nameValue = (name?.value ?? "").trim();
    if (!nameValue) continue;
    const offsetValue = offset?.value === "" ? null : Number(offset.value);
    const indexValue = index?.value === "" ? null : Number(index.value);
    const scaleValue = Number(scale?.value);
    fields.push({
      name: nameValue,
      offset: Number.isInteger(offsetValue) && offsetValue >= 0 ? offsetValue : null,
      index: Number.isInteger(indexValue) && indexValue >= 0 ? indexValue : null,
      fieldType: type?.value || "u16",
      byteOrder: order?.value === "le" ? "le" : "be",
      scale: Number.isFinite(scaleValue) && scaleValue !== 0 ? scaleValue : 1,
      unit: (unit?.value ?? "").trim(),
    });
  }
  return fields;
}

function fdCollectDefinition() {
  const mode = elements.fdMode?.value === "ascii-delimited" ? "ascii-delimited" : "binary";
  const lengthValue = Number(elements.fdLength?.value);
  const def = {
    schemaVersion: 1,
    name: (elements.fdName?.value || "").trim(),
    mode,
    fields: fdCollectFields(),
  };
  if (mode === "binary") {
    def.head = (elements.fdHead?.value || "").trim();
    const dynamic = elements.fdLenSrc?.value === "field";
    def.length = !dynamic && Number.isInteger(lengthValue) && lengthValue >= 1 ? lengthValue : null;
    if (dynamic) {
      const lfOffset = Number(elements.fdLfOffset?.value);
      const lfAdjust = Number(elements.fdLfAdjust?.value);
      def.lengthField = {
        offset: Number.isInteger(lfOffset) && lfOffset >= 0 ? lfOffset : 0,
        fieldType: elements.fdLfType?.value === "u16" ? "u16" : "u8",
        byteOrder: elements.fdLfOrder?.value === "le" ? "le" : "be",
        adjust: Number.isInteger(lfAdjust) ? lfAdjust : 0,
      };
    }
    def.tail = (elements.fdTail?.value || "").trim();
    const checksum = elements.fdChecksum?.value || "none";
    def.checksum = checksum === "none" ? null : { type: checksum };
  } else {
    def.lineEnding = FD_LINE_ENDING[elements.fdLineEnding?.value] ?? "\n";
    def.separator = FD_SEPARATOR[elements.fdSeparator?.value] ?? ",";
  }
  return def;
}

function fdFillForm(def) {
  if (elements.fdName) elements.fdName.value = def.name ?? "";
  if (elements.fdMode) elements.fdMode.value = def.mode === "ascii-delimited" ? "ascii-delimited" : "binary";
  if (elements.fdHead) elements.fdHead.value = def.head ?? "";
  if (elements.fdLenSrc) elements.fdLenSrc.value = def.lengthField ? "field" : "fixed";
  if (elements.fdLength) elements.fdLength.value = def.length ?? "";
  if (elements.fdLfOffset) elements.fdLfOffset.value = def.lengthField?.offset ?? "";
  if (elements.fdLfType) elements.fdLfType.value = def.lengthField?.fieldType ?? "u8";
  if (elements.fdLfOrder) elements.fdLfOrder.value = def.lengthField?.byteOrder ?? "be";
  if (elements.fdLfAdjust) elements.fdLfAdjust.value = def.lengthField?.adjust ?? 0;
  if (elements.fdTail) elements.fdTail.value = def.tail ?? "";
  if (elements.fdChecksum) elements.fdChecksum.value = def.checksum?.type ?? "none";
  if (elements.fdLineEnding) elements.fdLineEnding.value = FD_LINE_ENDING_REV[def.lineEnding] ?? "lf";
  if (elements.fdSeparator) elements.fdSeparator.value = FD_SEPARATOR_REV[def.separator] ?? ",";
  fdRenderFields(def.fields ?? []);
  fdUpdateModeVisibility();
}

/** 调 Rust 校验定义;返回问题数组,后端不可用返回 null。 */
async function fdValidate(def) {
  try {
    const result = await callBackend("custom_frame_validate", { definition: def });
    if (result?.status === "ok") return result.issues ?? [];
  } catch { /* fallthrough */ }
  return null;
}

function fdRefreshList(selectedName = "") {
  const select = elements.fdList;
  if (!select) return;
  const previous = selectedName || select.value;
  select.replaceChildren(new Option("已存定义…", ""));
  for (const def of frameDefs) {
    const option = new Option(def.name, def.name);
    option.selected = def.name === previous;
    select.append(option);
  }
}

async function fdSaveDefinition() {
  const def = fdCollectDefinition();
  if (!def.name) {
    setNotice("error", "缺少名称", "请填写帧定义名称。");
    return;
  }
  const issues = await fdValidate(def);
  if (issues === null) {
    setNotice("error", "无法校验", "Rust 核心不可用，稍后再试。");
    return;
  }
  if (issues.length > 0) {
    setNotice("error", "定义不合法", issues.join("；"));
    return;
  }
  const index = frameDefs.findIndex((d) => d.name === def.name);
  if (index >= 0) frameDefs[index] = def;
  else frameDefs.push(def);
  fdRefreshList(def.name);
  setNotice("success", "已保存定义", `「${def.name}」(${def.fields.length} 个字段) 已随项目保存。`);
}

function fdLoadSelected() {
  const name = elements.fdList?.value;
  const def = frameDefs.find((d) => d.name === name);
  if (!def) {
    setNotice("error", "未选择定义", "请先从下拉框选择一个已保存的帧定义。");
    return;
  }
  fdFillForm(def);
  setNotice("success", "已载入", `「${def.name}」已载入表单。`);
}

function fdDeleteSelected() {
  const name = elements.fdList?.value;
  if (!name) {
    setNotice("error", "未选择定义", "请先从下拉框选择要删除的帧定义。");
    return;
  }
  frameDefs = frameDefs.filter((d) => d.name !== name);
  if (activeFrameDef?.name === name) activeFrameDef = null;
  fdRefreshList("");
  setNotice("info", "已删除", `帧定义「${name}」已删除。`);
}

async function fdApplyToCurve() {
  const def = fdCollectDefinition();
  if (!def.name) {
    setNotice("error", "缺少名称", "请先填写并保存帧定义。");
    return;
  }
  const issues = await fdValidate(def);
  if (issues === null) {
    setNotice("error", "无法校验", "Rust 核心不可用，稍后再试。");
    return;
  }
  if (issues.length > 0) {
    setNotice("error", "定义不合法", issues.join("；"));
    return;
  }
  activeFrameDef = def;
  for (const field of def.fields) {
    const key = `fd:${def.name}.${field.name}`;
    if (!plotDiscovered.has(key) && plotDiscovered.size < PLOT_MAX_DISCOVERED) {
      plotDiscovered.set(key, { key, name: `${def.name}.${field.name}`, unit: field.unit || "" });
    }
  }
  plotRefreshChannelOptions();
  setNotice("success", "已应用到曲线", `收包将按「${def.name}」解析 ${def.fields.length} 个字段；去「实时曲线」卡下拉选择通道。`);
}

async function fdTryParse() {
  if (!lastDebugRxBytes) {
    setNotice("error", "没有收包", "尚未收到任何 RX 帧，先绑定串口收包或回放一段录制。");
    return;
  }
  const def = fdCollectDefinition();
  try {
    const result = await callBackend("custom_frame_parse", { definition: def, bytes: lastDebugRxBytes });
    if (result?.status === "ok") {
      elements.fdPreview.textContent = (result.fields ?? [])
        .map((field) => `${field.name}=${field.value}${field.unit || ""}`)
        .join("  ") || "(无字段)";
      elements.fdPreview.title = elements.fdPreview.textContent;
    } else {
      const message = `${result?.error?.code ?? "ERROR"} ${result?.error?.message ?? ""}`;
      elements.fdPreview.textContent = `✗ ${message}`;
      elements.fdPreview.title = message;
    }
  } catch (error) {
    elements.fdPreview.textContent = `✗ ${error.message || String(error)}`;
  }
}

// --- 会话录制(批次 3) ---

async function recToggle() {
  if (recRecording) {
    const result = await callBackend("record_stop", {});
    recRecording = false;
    if (elements.recToggle) elements.recToggle.textContent = "开始录制";
    if (elements.recState) elements.recState.textContent = `已停 · ${result?.frameCount ?? 0} 帧`;
    setNotice("info", "录制完成", `${result?.frameCount ?? 0} 帧已落盘${result?.files?.[0] ? `：${result.files[0]}` : ""}`);
    return;
  }
  const result = await callBackend("record_start", {});
  if (result?.ok) {
    recRecording = true;
    if (elements.recToggle) elements.recToggle.textContent = "停止录制";
    if (elements.recState) elements.recState.textContent = "录制中…";
    setNotice("success", "录制开始", `文件：${result.file}`);
  } else {
    setNotice("error", "录制失败", result?.error || "无法开始录制。");
  }
}

// --- 回放(批次 3): 重放进渲染层现有管线,不伪造 IPC 事件 ---

function replayUpdateState() {
  const state = elements.replayState;
  if (!replayScheduler) {
    if (state) state.textContent = "未加载";
    if (elements.replayToggle) { elements.replayToggle.disabled = true; elements.replayToggle.textContent = "播放"; }
    if (elements.replayStep) elements.replayStep.disabled = true;
    if (elements.replayExport) elements.replayExport.disabled = !replayPath;
    return;
  }
  const progress = replayScheduler.progress;
  const stateText = progress.state === "playing" ? "播放中" : progress.state === "paused" ? "已暂停" : progress.state === "done" ? "已播完" : progress.state;
  if (state) state.textContent = `${progress.index}/${progress.total} · ${stateText}`;
  if (elements.replayToggle) {
    elements.replayToggle.disabled = progress.state === "done" && progress.index >= progress.total;
    elements.replayToggle.textContent = progress.state === "playing" ? "暂停" : progress.state === "paused" ? "继续" : "播放";
  }
  if (elements.replayStep) elements.replayStep.disabled = progress.index >= progress.total;
  if (elements.replayExport) elements.replayExport.disabled = !replayPath;
}

async function replayOpenFile() {
  try {
    if (replayScheduler) replayScheduler.stop();
    replayScheduler = null;
    const picked = await callBackend("record_pick", {});
    if (!picked?.ok) return;
    replayPath = picked.path;
    const loaded = await callBackend("record_read", { path: picked.path });
    if (!loaded?.ok) {
      setNotice("error", "读取失败", loaded?.error || "无法读取录制文件。");
      replayUpdateState();
      return;
    }
    const records = normalizeReplayRecords(loaded.records);
    if (records.length === 0) {
      setNotice("error", "空会话", "录制文件里没有可回放的帧。");
      replayUpdateState();
      return;
    }
    replayScheduler = new ReplayScheduler({
      records,
      speed: Number(elements.replaySpeed?.value) || 1,
      onRecord: (record) => {
        appendDebugLog(record);
        plotFeedRecord(record);
      },
      onDone: replayUpdateState,
    });
    replayUpdateState();
    setNotice("success", "已加载录制", `${records.length} 帧（跳过 ${loaded.skipped ?? 0} 行）就绪，点「播放」按原始时间轴回放。`);
  } catch (error) {
    setNotice("error", "回放加载失败", error.message || String(error));
  }
}

function replayTogglePlay() {
  if (!replayScheduler) return;
  const state = replayScheduler.progress.state;
  if (state === "playing") replayScheduler.pause();
  else replayScheduler.start(); // idle/paused/done 都可(重新)开始;已播完则从头
  replayUpdateState();
}

function replaySpeedChange() {
  replayScheduler?.setSpeed(Number(elements.replaySpeed?.value) || 1);
}

function replayStepOnce() {
  replayScheduler?.step();
  replayUpdateState();
}

async function replayExportCsv() {
  if (!replayPath) {
    setNotice("error", "未加载录制", "先「打开录制文件」再导出。");
    return;
  }
  try {
    const result = await callBackend("record_export_csv", {
      path: replayPath,
      definition: activeFrameDef ?? undefined,
    });
    if (result?.ok) {
      setNotice("success", "已导出录制 CSV", result.path || "桌面 CSV 已生成。");
    } else {
      setNotice("error", "导出失败", result?.error || "无法导出。");
    }
  } catch (error) {
    setNotice("error", "导出失败", error.message || String(error));
  }
}

/** 项目打开/新建时恢复帧定义(只恢复列表,不自动应用到曲线)。 */
function restoreFrameDefinitions(defs) {
  frameDefs = (Array.isArray(defs) ? defs : []).map((def) => ({
    ...def,
    lengthField: def.lengthField ? { ...def.lengthField } : (def.lengthField ?? null),
    checksum: def.checksum ? { ...def.checksum } : (def.checksum ?? null),
    fields: (def.fields ?? []).map((field) => ({ ...field })),
  }));
  activeFrameDef = null;
  fdRefreshList("");
}

// === 示例代码生成 ===

let codeLang = "csharp";

const CODE_LANG_LABEL = { csharp: "C#", python: "Python" };

const CODE_TRANSPORT_LABEL = {
  rtu: "RTU 串口",
  ascii: "ASCII 串口",
  tcp: "TCP",
  udp: "UDP",
  "rtu-over-tcp": "RTU/TCP",
  "ascii-over-tcp": "ASCII/TCP",
};

const CODE_FC = {
  1: { read: true, bits: true, label: "读线圈", csharp: "ReadCoils", python: "read_coils" },
  2: { read: true, bits: true, label: "读离散输入", csharp: "ReadDiscreteInputs", python: "read_discrete_inputs" },
  3: { read: true, bits: false, label: "读保持寄存器", csharp: "ReadHoldingRegisters", python: "read_holding_registers" },
  4: { read: true, bits: false, label: "读输入寄存器", csharp: "ReadInputRegisters", python: "read_input_registers" },
  5: { read: false, label: "写单线圈", csharp: "WriteSingleCoil", python: "write_coil" },
  6: { read: false, label: "写单寄存器", csharp: "WriteSingleRegister", python: "write_register" },
  15: { read: false, label: "写多线圈", csharp: "WriteMultipleCoils", python: "write_coils" },
  16: { read: false, label: "写多寄存器", csharp: "WriteMultipleRegisters", python: "write_registers" },
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
  if (lang === "python") return pythonCodeSample(cfg);
  return csharpCodeSample(cfg);
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

// === 一键扫描(站号 × 波特率 × 奇偶校验) ===

let scanAllActive = false;
let scanProgressUnsubscribe = null;

const SCAN_PARITY_LABELS = { none: "8N1", even: "8E1", odd: "8O1" };

function scanAllOptions() {
  const bauds = [...document.querySelectorAll(".scan-baud-opt:checked")].map((el) => Number(el.value));
  const parities = [...document.querySelectorAll(".scan-parity-opt:checked")].map((el) => el.value);
  return {
    stationStart: Number(document.querySelector("#scan-station-start")?.value) || 1,
    stationEnd: Number(document.querySelector("#scan-station-end")?.value) || 16,
    bauds,
    parities,
    timeoutMs: Number(document.querySelector("#scan-timeout")?.value) || 200,
    mode: document.querySelector("#scan-mode")?.value === "full" ? "full" : "firstHit",
    autoQuantity: Number(document.querySelector("#scan-auto-quantity")?.value) || 8,
  };
}

async function scanAll() {
  if (scanAllActive) {
    // 扫描中再次点击 = 请求取消
    try { await callBackend("scan_all_cancel"); } catch { /* 忽略 */ }
    return;
  }
  if (busy || !elements.scanAll || elements.scanAll.disabled) return;
  const comPort = elements.portName?.value;
  if (!comPort) {
    setNotice("error", "参数无效", "请先选择串口。");
    return;
  }
  const opts = scanAllOptions();
  if (!opts.bauds.length || !opts.parities.length) {
    setNotice("error", "参数无效", "至少勾选一个波特率与一种校验格式。");
    return;
  }
  const form = elements.form;
  const lineConfig = {
    flowControl: form?.elements?.namedItem("flowControl")?.value ?? "none",
    dtrMode: form?.elements?.namedItem("dtrMode")?.value ?? "preserve",
    rtsMode: form?.elements?.namedItem("rtsMode")?.value ?? "preserve",
  };
  scanAllActive = true;
  setBusy(true);
  elements.scanAll.textContent = "停止扫描";
  elements.commandState.textContent = "一键扫描中…";
  setNotice("info", "一键扫描", `${opts.bauds.length} 档波特率 × ${opts.parities.length} 种校验 × 站号 ${opts.stationStart}~${opts.stationEnd}`);
  if (window.nexusDesktop?.onScanProgress) {
    scanProgressUnsubscribe = window.nexusDesktop.onScanProgress((p) => {
      const label = SCAN_PARITY_LABELS[p.parity] ?? p.parity;
      elements.commandState.textContent =
        `扫描 ${p.baud}·${label} 档 ${p.comboIndex + 1}/${p.totalCombos} · 站 ${p.stationId} · ${(p.elapsedMs / 1000).toFixed(1)}s`;
    });
  }
  try {
    const result = await callBackend("scan_all", {
      comPort,
      stationStart: opts.stationStart,
      stationEnd: opts.stationEnd,
      bauds: opts.bauds,
      parities: opts.parities,
      timeoutMs: opts.timeoutMs,
      mode: opts.mode,
      lineConfig,
    });
    if (result?.found) {
      renderScanAllResults(result.hits ?? []);
      const first = result.hits?.[0];
      setNotice("success", `发现 ${result.hits.length} 个命中`,
        first ? `${first.baudRate}·${first.parityLabel ?? SCAN_PARITY_LABELS[first.parity]} 站号 ${first.stationId} · 耗时 ${(result.elapsedMs / 1000).toFixed(1)}s。点击结果表"选用并连接"一键接入。` : "");
    } else if (result?.cancelled) {
      setNotice("info", "已取消", `扫描已停止(尝试 ${result.triedCombos}/${result.totalCombos} 档,耗时 ${(result.elapsedMs / 1000).toFixed(1)}s)。`);
    } else {
      setNotice("info", "未发现从站", `${result?.totalCombos ?? "?"} 档参数全部无响应,耗时 ${((result?.elapsedMs ?? 0) / 1000).toFixed(1)}s。请检查 485 接线与供电。`);
    }
  } catch (error) {
    setNotice("error", "扫描失败", error.message || String(error));
  } finally {
    if (scanProgressUnsubscribe) { scanProgressUnsubscribe(); scanProgressUnsubscribe = null; }
    scanAllActive = false;
    if (elements.scanAll) elements.scanAll.textContent = "一键扫描";
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先连接";
    syncActionState();
  }
}

function renderScanAllResults(hits) {
  if (!elements.scanRows) return;
  elements.scanRows.replaceChildren();
  for (const hit of hits) {
    const row = document.createElement("tr");
    const label = hit.parityLabel ?? SCAN_PARITY_LABELS[hit.parity] ?? hit.parity;
    appendCells(row, [
      hit.stationId,
      `${hit.baudRate}·${label}`,
      hit.format ?? "RTU",
      hit.firstResponseMs != null ? `${hit.firstResponseMs} ms` : "—",
      `FC${hit.functionCode ?? 3}`,
      hit.status ?? "在线",
    ]);
    const actionCell = document.createElement("td");
    const actionButton = document.createElement("button");
    actionButton.type = "button";
    actionButton.textContent = "选用并连接";
    actionButton.addEventListener("click", () => nexusApplyScanHit(hit.stationId, hit.baudRate, hit.parity));
    actionCell.append(actionButton);
    row.append(actionCell);
    elements.scanRows.append(row);
  }
}

/** 扫描结果"选用并连接":回填参数 → 打开串口 → 自动读保持寄存器。 */
async function nexusApplyScanHit(station, baud, parity) {
  if (busy) return;
  const form = elements.form;
  if (form) {
    const baudField = form.elements.namedItem("baudRate");
    if (baudField) baudField.value = String(baud);
    const parityField = form.elements.namedItem("parity");
    if (parityField) parityField.value = String(parity);
  }
  if (elements.unitId) elements.unitId.value = String(station);
  setBusy(true);
  const t0 = performance.now();
  try {
    const config = readConfig();
    const status = await callBackend("open_serial_port", { config });
    const ms = performance.now() - t0;
    renderStatus(status);
    const label = SCAN_PARITY_LABELS[parity] ?? parity;
    setNotice("success", "已按扫描结果连接", `${config.portName} ${config.baudRate}·${label} · 打开耗时 ${ms.toFixed(0)} ms`);
    persistConfig();
    // 自动读保持寄存器(FC03),地址 0 起
    if (elements.functionCode) elements.functionCode.value = "3";
    if (elements.startAddress) elements.startAddress.value = "0";
    if (elements.addressBase) elements.addressBase.value = "0";
    if (elements.quantity) {
      const qty = Number(document.querySelector("#scan-auto-quantity")?.value) || 8;
      elements.quantity.value = String(Math.min(Math.max(qty, 1), 125));
    }
    await readRegistersOnce();
    setNotice("success", "自动读取完成", "数据已入表。可设置倍率/单位(如 0.1 / ℃)并点击「连续轮询」实时刷新。");
  } catch (error) {
    renderConnectionFault(`按扫描结果连接失败：${String(error)}`);
  } finally {
    setBusy(false);
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

function replaceCommandList(commands) {
  commandList = (Array.isArray(commands) ? commands : []).map((command) => ({
    fc: Number(command?.fc) || 3,
    unitId: Number(command?.unitId) || 1,
    address: Number(command?.address) || 0,
    quantity: Number(command?.quantity) || 0,
    value: String(command?.value ?? ""),
  }));
  renderCommandList();
  syncActionState();
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
  const isGx3 = viewName === "gx3";
  const isMelsec = viewName === "melsec";
  const isInterfaces = viewName === "interfaces";
  const isSiemens = viewName === "siemens";
  const isOmron = viewName === "omron";
  const isAllenBradley = viewName === "allen-bradley";
  const isBeckhoff = viewName === "beckhoff";
  const isKeyence = viewName === "keyence";
  const isLsElectric = viewName === "ls-electric";
  const isDelta = viewName === "delta";
  const isInovance = viewName === "inovance";
  const isXinje = viewName === "xinje";
  const isFatek = viewName === "fatek";
  const isFuji = viewName === "fuji";
  const isGe = viewName === "ge";
  const isPanasonic = viewName === "panasonic";
  const isMqtt = viewName === "mqtt";
  const isIec104 = viewName === "iec104";
  const isDnp3 = viewName === "dnp3";
  const isDlt645 = viewName === "dlt645";
  const isCjt188 = viewName === "cjt188";
  const isBacnet = viewName === "bacnet";
  const isKnx = viewName === "knx";
  if (elements.masterView) elements.masterView.classList.toggle("hidden", !isMaster);
  if (elements.slaveView) elements.slaveView.classList.toggle("hidden", !isSlave);
  if (elements.debugView) elements.debugView.classList.toggle("hidden", !isDebug);
  if (elements.parserView) elements.parserView.classList.toggle("hidden", !isParser);
  if (elements.gx3View) elements.gx3View.classList.toggle("hidden", !isGx3);
  const melsecView = document.querySelector("#melsec-view");
  if (melsecView) melsecView.classList.toggle("hidden", !isMelsec);
  const siemensView = document.querySelector("#siemens-view");
  if (siemensView) siemensView.classList.toggle("hidden", !isSiemens);
  const omronView = document.querySelector("#omron-view");
  if (omronView) omronView.classList.toggle("hidden", !isOmron);
  const allenBradleyView = document.querySelector("#allen-bradley-view");
  if (allenBradleyView) allenBradleyView.classList.toggle("hidden", !isAllenBradley);
  const beckhoffView = document.querySelector("#beckhoff-view");
  if (beckhoffView) beckhoffView.classList.toggle("hidden", !isBeckhoff);
  const keyenceView = document.querySelector("#keyence-view");
  if (keyenceView) keyenceView.classList.toggle("hidden", !isKeyence);
  const lsElectricView = document.querySelector("#ls-electric-view");
  if (lsElectricView) lsElectricView.classList.toggle("hidden", !isLsElectric);
  const deltaView = document.querySelector("#delta-view");
  if (deltaView) deltaView.classList.toggle("hidden", !isDelta);
  const inovanceView = document.querySelector("#inovance-view");
  if (inovanceView) inovanceView.classList.toggle("hidden", !isInovance);
  const xinjeView = document.querySelector("#xinje-view");
  if (xinjeView) xinjeView.classList.toggle("hidden", !isXinje);
  const fatekView = document.querySelector("#fatek-view");
  if (fatekView) fatekView.classList.toggle("hidden", !isFatek);
  const fujiView = document.querySelector("#fuji-view");
  if (fujiView) fujiView.classList.toggle("hidden", !isFuji);
  const geView = document.querySelector("#ge-view");
  if (geView) geView.classList.toggle("hidden", !isGe);
  const panasonicView = document.querySelector("#panasonic-view");
  if (panasonicView) panasonicView.classList.toggle("hidden", !isPanasonic);
  const mqttView = document.querySelector("#mqtt-view");
  if (mqttView) mqttView.classList.toggle("hidden", !isMqtt);
  const iec104View = document.querySelector("#iec104-view");
  if (iec104View) iec104View.classList.toggle("hidden", !isIec104);
  const dnp3View = document.querySelector("#dnp3-view");
  if (dnp3View) dnp3View.classList.toggle("hidden", !isDnp3);
  const dlt645View = document.querySelector("#dlt645-view");
  if (dlt645View) dlt645View.classList.toggle("hidden", !isDlt645);
  const cjt188View = document.querySelector("#cjt188-view");
  if (cjt188View) cjt188View.classList.toggle("hidden", !isCjt188);
  const bacnetView = document.querySelector("#bacnet-view");
  if (bacnetView) bacnetView.classList.toggle("hidden", !isBacnet);
  const knxView = document.querySelector("#knx-view");
  if (knxView) knxView.classList.toggle("hidden", !isKnx);
  const interfacesView = document.querySelector("#interfaces-view");
  if (interfacesView) interfacesView.classList.toggle("hidden", !isInterfaces);
  if (isGx3 && !gx3Available) void refreshGx3Status();
  // 打开本页即自动体检(一键看到)
  if (isInterfaces) refreshInterfaces().catch(() => {});
  // 右侧报文面板仅在主站视图显示
  const packetPanel = document.querySelector(".packet-panel");
  if (packetPanel) packetPanel.classList.toggle("hidden", !isMaster);
  // 切 view 时刷新 transport 状态
  updateTransportVisibility();
}

// === 协议通讯设置帮助窗 ===

let activeProtocolGuideSource = null;
let protocolGuideReturnFocus = null;
let savedProtocolGuideReference = null;

function guideValue(selector, fallback = "—") {
  const value = document.querySelector(selector)?.value;
  return value == null || String(value).trim() === "" ? fallback : String(value).trim();
}

function currentProtocolGuideVariant(source) {
  if (source === "master") return currentTransport();
  if (source === "slave") return guideValue("#slave-mode", "tcp");
  if (source === "debug") return "serial";
  if (source === "melsec") return guideValue("#mc-frame-type", "3e");
  if (source === "siemens") return guideValue("#s7-variant", "s7comm");
  if (source === "omron") return guideValue("#om-transport", "tcp");
  if (source === "allen-bradley") return "cip";
  if (source === "beckhoff") return "ads";
  if (source === "keyence") return "kv-host-link";
  if (source === "ls-electric") return "xgt-fenet";
  if (source === "delta") return guideValue("#delta-series", "dvp-modbus");
  if (source === "inovance") return guideValue("#inovance-series", "h3u-modbus");
  if (source === "xinje") return guideValue("#xinje-series", "xc-modbus");
  if (source === "fatek") return "ascii";
  if (source === "fuji") return "sph";
  if (source === "ge") return "srtp";
  if (source === "panasonic") return "mewtocol-com";
  if (source === "mqtt") return "mqtt-311";
  if (source === "iec") return "iec-60870-5-104";
  if (source === "dnp") return "dnp3-tcp";
  if (source === "dlt") return `dlt645-${guideValue("#dlt645-version", "2007")}`;
  if (source === "cjt") return "cjt188-2004";
  if (source === "bacnet") return "bacnet-ip";
  if (source === "knx") return "tunneling-v1";
  return "";
}

function serialGuideSnapshot() {
  const parityLabels = { none: "N", even: "E", odd: "O", mark: "M", space: "S" };
  const parity = parityLabels[guideValue("#parity", "none")] || guideValue("#parity", "none");
  return `${guideValue("#port-name", "未选择 COM")} · ${guideValue("#baud-rate", "9600")} ${guideValue("#data-bits", "8")}${parity}${guideValue("#stop-bits", "1")} · ${guideValue("#interface-type", "rs232").toUpperCase()}`;
}

function protocolGuideCurrentSnapshot(source) {
  const actualVariant = currentProtocolGuideVariant(source);
  if (source === "master") {
    if (["rtu", "ascii"].includes(actualVariant)) {
      return `${actualVariant.toUpperCase()} · ${serialGuideSnapshot()} · 站号 ${guideValue("#unit-id", "1")}`;
    }
    return `${actualVariant.toUpperCase()} · ${guideValue("#tcp-host", "127.0.0.1")}:${guideValue("#tcp-port", "502")} · 站号 ${guideValue("#unit-id", "1")}`;
  }
  if (source === "slave") {
    return actualVariant === "serial"
      ? `RTU 从站 · ${serialGuideSnapshot()} · 允许站号 ${guideValue("#slave-stations", "全部")}`
      : `TCP 从站 · 127.0.0.1:${guideValue("#slave-port", "502")} · 允许站号 ${guideValue("#slave-stations", "全部")}`;
  }
  if (source === "debug") return `串口调试 · ${serialGuideSnapshot()}`;
  if (source === "melsec") {
    if (["mc-c24", "fx-links", "fx-prog"].includes(actualVariant)) {
      return `${actualVariant} · ${serialGuideSnapshot()} · 站号 ${guideValue("#mc-fx-station", "0")}`;
    }
    return `${actualVariant} · ${guideValue("#mc-host", "127.0.0.1")}:${guideValue("#mc-port", "5000")} · 网络号 ${guideValue("#mc-network-no", "0")} / PC号 ${guideValue("#mc-pc-no", "255")}`;
  }
  if (source === "siemens") {
    if (["uss", "rk512"].includes(actualVariant)) return `${actualVariant.toUpperCase()} 参数准备 · ${serialGuideSnapshot()}`;
    if (actualVariant === "webapi") return `Web API · https://${guideValue("#s7-host", "127.0.0.1")}:443`;
    return `${actualVariant} · ${guideValue("#s7-host", "127.0.0.1")}:${guideValue("#s7-port", "102")} · rack ${guideValue("#s7-rack", "0")} / slot ${guideValue("#s7-slot", "1")}`;
  }
  if (source === "omron") {
    if (actualVariant === "hostlink-fins-serial") {
      return `HostLink FINS · ${serialGuideSnapshot()} · 站号 ${guideValue("#om-serial-station", "0")}`;
    }
    if (actualVariant === "hostlink-serial") {
      return `HostLink C-mode · ${serialGuideSnapshot()} · 站号 ${guideValue("#om-serial-station", "0")}`;
    }
    return `FINS/${actualVariant.toUpperCase()} · ${guideValue("#om-host", "127.0.0.1")}:${guideValue("#om-port", "9600")} · 节点 ${guideValue("#om-src", "0")} → ${guideValue("#om-dest", "0")}`;
  }
  if (source === "allen-bradley") {
    return `CIP Explicit · TCP ${guideValue("#ab-host", "127.0.0.1")}:${guideValue("#ab-port", "44818")} · Tag ${guideValue("#ab-tag", "MyTag")} · TCP 只读`;
  }
  if (source === "beckhoff") {
    return `ADS/AMS · TCP ${guideValue("#ads-host", "127.0.0.1")}:${guideValue("#ads-port", "48898")} · NetId ${guideValue("#ads-target-netid", "未设置")} · TCP 只读`;
  }
  if (source === "keyence") {
    return `KV Host Link · TCP ${guideValue("#keyence-port", "8501")} · ${guideValue("#keyence-address", "DM0")} · TCP 只读`;
  }
  if (source === "ls-electric") {
    return `XGT FEnet · TCP ${guideValue("#xgt-port", "2004")} · ${guideValue("#xgt-variable", "%DW100")} · TCP 只读`;
  }
  if (source === "delta") {
    return `Delta ${actualVariant === "as-modbus" ? "AS" : "DVP"} · ${guideValue("#delta-address", "D100")} · Modbus profile`;
  }
  if (source === "inovance") {
    return `汇川 ${actualVariant === "h5u-modbus" ? "H5U" : "H3U"} · ${guideValue("#inovance-address", "D100")} · Modbus profile`;
  }
  if (source === "xinje") {
    return `信捷 ${actualVariant === "xd-modbus" ? "XD/XL" : "XC"} · ${guideValue("#xinje-address", "D100")} · Modbus profile`;
  }
  if (source === "fatek") {
    return `FATEK FBs ASCII · ${guideValue("#fatek-host", "127.0.0.1")}:${guideValue("#fatek-port", "5000")} · ${guideValue("#fatek-address", "R12")} · TCP 只读 + 编解码`;
  }
  if (source === "fuji") {
    return `Fuji SPH · ${guideValue("#fuji-host", "127.0.0.1")}:${guideValue("#fuji-port", "18245")} · ${guideValue("#fuji-address", "M1.0")} · TCP 只读 + 编解码`;
  }
  if (source === "ge") {
    return `GE SRTP · ${guideValue("#ge-host", "127.0.0.1")}:${guideValue("#ge-port", "18245")} · ${guideValue("#ge-address", "R1")} · TCP 只读 + 编解码`;
  }
  if (source === "panasonic") {
    return `MEWTOCOL-COM · ${guideValue("#panasonic-port", "COM1")} · 站号 ${guideValue("#panasonic-station", "1")} · 离线编解码`;
  }
  if (source === "mqtt") {
    return `MQTT 3.1.1 · TCP ${guideValue("#mqtt-host", "127.0.0.1")}:${guideValue("#mqtt-port", "1883")} · ${guideValue("#mqtt-topic-filter", "factory/line1/#")} · TCP 只读订阅`;
  }
  if (source === "iec") {
    return `IEC104 · TCP ${guideValue("#iec104-host", "127.0.0.1")}:${guideValue("#iec104-port", "2404")} · CA ${guideValue("#iec104-common-address", "1")} · 只读主站`;
  }
  if (source === "dnp") {
    return `DNP3 · TCP ${guideValue("#dnp3-host", "127.0.0.1")}:${guideValue("#dnp3-port", "20000")} · Link ${guideValue("#dnp3-master-address", "1")} → ${guideValue("#dnp3-outstation-address", "1024")} · 只读 Master`;
  }
  if (source === "dlt") {
    return `DL/T 645-${guideValue("#dlt645-version", "2007")} · ${serialGuideSnapshot()} · 表地址 ${guideValue("#dlt645-address", "未设置")} · DI ${guideValue("#dlt645-data-id", "未设置")} · 共享 COM 只读`;
  }
  if (source === "cjt") {
    return `CJ/T 188-2004 · ${guideValue("#cjt188-meter-type", "cold-water")} · 表地址 ${guideValue("#cjt188-address", "未设置")} · DI ${guideValue("#cjt188-data-id", "未设置")} · 离线编解码`;
  }
  if (source === "bacnet") {
    const global = document.querySelector("#bacnet-whois-global")?.checked ?? true;
    return `BACnet/IP · UDP ${guideValue("#bacnet-port", "47808")} · ${guideValue("#bacnet-host", "127.0.0.1")} · ${global ? "全局 Who-Is" : `Who-Is ${guideValue("#bacnet-whois-low", "0")}..${guideValue("#bacnet-whois-high", "0")}`} · 只读会话/离线编解码`;
  }
  if (source === "knx") {
    return `KNXnet/IP Tunneling v1 · UDP 3671（仅现场记录） · ${guideValue("#knx-group", "1/2/3")} · 离线只读编解码`;
  }
  return "—";
}

function renderProtocolGuideList(selector, items) {
  const list = document.querySelector(selector);
  if (!list) return;
  list.replaceChildren();
  for (const item of items || []) {
    const li = document.createElement("li");
    li.textContent = item;
    list.append(li);
  }
}

function renderProtocolGuide(source, variant) {
  const guide = resolveProtocolGuide(source, variant);
  if (!guide) return;
  const title = document.querySelector("#protocol-guide-title");
  const medium = document.querySelector("#protocol-guide-medium");
  const current = document.querySelector("#protocol-guide-current span");
  const summary = document.querySelector("#protocol-guide-summary");
  const rows = document.querySelector("#protocol-guide-parameter-rows");
  if (title) title.textContent = `${guide.sourceLabel} · ${guide.label}`;
  if (medium) medium.textContent = guide.medium === "serial" ? "串口 / COM" : "网口 / IP";
  if (current) current.textContent = protocolGuideCurrentSnapshot(source);
  if (summary) summary.textContent = guide.summary;
  if (rows) {
    rows.replaceChildren();
    for (const parameter of guide.parameters || []) {
      const tr = document.createElement("tr");
      for (const value of parameter) {
        const td = document.createElement("td");
        td.textContent = value;
        tr.append(td);
      }
      rows.append(tr);
    }
  }
  renderProtocolGuideList("#protocol-guide-device-steps", guide.deviceSteps);
  renderProtocolGuideList("#protocol-guide-pc-steps", guide.pcSteps);
  renderProtocolGuideList("#protocol-guide-checks", guide.checks);
  renderProtocolGuideList("#protocol-guide-warnings", guide.warnings);
}

function openProtocolGuide(source, trigger) {
  const dialog = document.querySelector("#protocol-guide-dialog");
  const selector = document.querySelector("#protocol-guide-variant");
  if (!dialog || !selector) return;
  const variants = listProtocolGuideVariants(source);
  if (variants.length === 0) return;
  activeProtocolGuideSource = source;
  protocolGuideReturnFocus = trigger || document.activeElement;
  selector.replaceChildren();
  for (const variant of variants) {
    const option = document.createElement("option");
    option.value = variant.value;
    option.textContent = variant.label;
    selector.append(option);
  }
  const current = savedProtocolGuideReference?.source === source
    ? savedProtocolGuideReference.variant
    : currentProtocolGuideVariant(source);
  selector.value = variants.some((entry) => entry.value === current) ? current : variants[0].value;
  savedProtocolGuideReference = { source, variant: selector.value };
  renderProtocolGuide(source, selector.value);
  if (typeof dialog.showModal === "function") dialog.showModal();
  else dialog.setAttribute("open", "");
}

function closeProtocolGuide() {
  const dialog = document.querySelector("#protocol-guide-dialog");
  if (!dialog?.open) return;
  if (typeof dialog.close === "function") dialog.close();
  else dialog.removeAttribute("open");
}

function initProtocolGuides() {
  const dialog = document.querySelector("#protocol-guide-dialog");
  const selector = document.querySelector("#protocol-guide-variant");
  for (const button of document.querySelectorAll("[data-protocol-help]")) {
    button.addEventListener("click", () => openProtocolGuide(button.dataset.protocolHelp, button));
  }
  document.querySelector("#protocol-guide-close")?.addEventListener("click", closeProtocolGuide);
  selector?.addEventListener("change", () => {
    if (activeProtocolGuideSource) renderProtocolGuide(activeProtocolGuideSource, selector.value);
    if (activeProtocolGuideSource) {
      savedProtocolGuideReference = { source: activeProtocolGuideSource, variant: selector.value };
    }
  });
  dialog?.addEventListener("click", (event) => {
    if (event.target === dialog) closeProtocolGuide();
  });
  dialog?.addEventListener("close", () => {
    if (protocolGuideReturnFocus instanceof HTMLElement && protocolGuideReturnFocus.isConnected) protocolGuideReturnFocus.focus();
    protocolGuideReturnFocus = null;
  });
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
    if (area === "coil" || area === "discrete") {
      // 线圈/离散输入走 slave_set_coil(布尔值)
      const bools = values.map((v) => v !== 0);
      await callBackend("slave_set_coil", { slaveId: SLAVE_ID, area, address, values: bools });
    } else {
      await callBackend("slave_set_value", { slaveId: SLAVE_ID, area, address, values });
    }
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
  if (record.direction === "RX") lastDebugRxBytes = record.bytes; // 帧解析"试算"用
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

function collectPersistentConfig() {
  return {
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
}

function applyPersistentConfig(config) {
  if (!config || typeof config !== "object") return;
  const allowedTransports = new Set(["rtu", "ascii", "tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"]);
  if (allowedTransports.has(config.transport)) {
    for (const radio of elements.transportRadios) radio.checked = radio.value === config.transport;
  }
  if (config.serial) {
    if (elements.baudRate && config.serial.baudRate) elements.baudRate.value = config.serial.baudRate;
    if (elements.parity && config.serial.parity) elements.parity.value = config.serial.parity;
    if (elements.dataBits && config.serial.dataBits) elements.dataBits.value = config.serial.dataBits;
    if (elements.stopBits && config.serial.stopBits) elements.stopBits.value = config.serial.stopBits;
    if (config.serial.portName) {
      const tryRestore = () => {
        const sel = elements.portName;
        if (sel && [...sel.options].some((option) => option.value === config.serial.portName)) {
          sel.value = config.serial.portName;
        }
      };
      tryRestore();
      setTimeout(tryRestore, 1500);
    }
  }
  if (config.tcp) {
    if (elements.tcpHost && config.tcp.host) elements.tcpHost.value = config.tcp.host;
    if (elements.tcpPort && config.tcp.port) elements.tcpPort.value = config.tcp.port;
  }
  if (config.command) {
    if (elements.unitId && config.command.unitId) elements.unitId.value = config.command.unitId;
    if (elements.functionCode && config.command.functionCode) elements.functionCode.value = config.command.functionCode;
    if (elements.startAddress && config.command.startAddress) elements.startAddress.value = config.command.startAddress;
    if (elements.quantity && config.command.quantity) elements.quantity.value = config.command.quantity;
    if (elements.displayType && config.command.displayType) elements.displayType.value = config.command.displayType;
    if (elements.pollInterval && config.command.pollInterval) elements.pollInterval.value = config.command.pollInterval;
  }
  updateTransportVisibility();
  syncActionState();
  renderCodeSample();
}

/** 保存当前配置到 localStorage */
function persistConfig() {
  try {
    localStorage.setItem(STORAGE_KEY_CONFIG, JSON.stringify(collectPersistentConfig()));
  } catch {
    // localStorage 不可用时静默失败
  }
}

/** 从 localStorage 恢复配置 */
function restoreConfig() {
  try {
    const raw = localStorage.getItem(STORAGE_KEY_CONFIG);
    if (!raw) return;
    applyPersistentConfig(JSON.parse(raw));
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
  input.addEventListener("change", async () => {
    const file = input.files?.[0];
    if (!file) return;
    try {
      const imported = await readPointImportFile(file);
      if (!imported?.length) {
        setNotice("error", "导入失败", "文件中没有可导入的点位。");
        return;
      }
      pointTable.push(...imported);
      setNotice("success", "导入成功", `已导入 ${imported.length} 个点位，当前共 ${pointTable.length} 个`);
      renderPointRows();
      persistPointTable();
    } catch (error) {
      setNotice("error", "导入失败", error.message || String(error));
    }
  });
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
      const t0 = performance.now();
      await callBackend("open_mc_1e_tcp", { connectionId: MC_CONN_ID, host, port });
      const ms = performance.now() - t0;
      mcConnected = true;
      mcIsAscii = false; mcIsUdp = false;
      mcFxProtocol = "1e";
      mcSetState(`1E 已连接 ${host}:${port} · ${ms.toFixed(0)} ms`, true);
      setNotice("success", "A-1E 已连接", `${host}:${port} (FX3U-ENET/FX5U/A 系兼容) · 连接耗时 ${ms.toFixed(0)} ms`);
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
      const cfg = status.config ?? {};
      const mismatch = [];
      if (Number(cfg.dataBits) !== 7) mismatch.push(`数据位 ${cfg.dataBits ?? "?"}(FX 默认 7)`);
      if (String(cfg.parity ?? "").toLowerCase() !== "even") mismatch.push(`校验 ${cfg.parity ?? "?"}(FX 默认 偶E)`);
      mcConnected = true;
      mcIsAscii = false;
      mcFxProtocol = variant === "fx-links" ? "links" : "prog";
      if (mismatch.length > 0) {
        mcSetState("已绑定但参数存疑");
        setNotice("error", "串口参数与 FX 不匹配", `当前 ${cfg.baudRate ?? "?"}·${cfg.dataBits ?? "?"}${(cfg.parity ?? "?").charAt(0).toUpperCase()}${cfg.stopBits ?? "?"},${mismatch.join(";")}——PLC 大概率无响应。请到主站页断开串口改参数后重连`);
      } else {
        mcSetState(`FX ${mcFxProtocol === "links" ? "Computer Link" : "编程口"} 已就绪`, true);
        setNotice("success", "FX 串口已绑定", `走主站页串口 ${cfg.baudRate ?? ""}·${cfg.dataBits ?? ""}${(cfg.parity ?? "e").charAt(0).toUpperCase()}${cfg.stopBits ?? ""},站号 ${document.querySelector("#mc-fx-station")?.value ?? 0}`);
      }
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
    const t0 = performance.now();
    await callBackend(cmd, {
      connectionId: MC_CONN_ID,
      host, port, frameType,
      networkNo, pcNo, watchdog,
    });
    const ms = performance.now() - t0;
    mcConnected = true;
    mcIsAscii = isAscii;
    mcIsUdp = isUdp;
    mcFxProtocol = null;
    mcSetState(`已连接 ${host}:${port} ${isUdp ? "UDP" : (isAscii ? "ASCII" : "Binary")} · ${ms.toFixed(0)} ms`, true);
    setNotice("success", "MC 已连接", `${host}:${port} (${variant.toUpperCase()}) · 连接耗时 ${ms.toFixed(0)} ms`);
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
  const c24ReadOnly = mcFxProtocol === "c24";
  if (q("#mc-connect")) q("#mc-connect").disabled = mcConnected;
  if (q("#mc-disconnect")) q("#mc-disconnect").disabled = !mcConnected;
  if (q("#mc-read")) q("#mc-read").disabled = !mcConnected;
  if (q("#mc-write")) q("#mc-write").disabled = !mcConnected || c24ReadOnly;
  if (q("#mc-start-slave")) q("#mc-start-slave").disabled = mcSlaveRunning;
  if (q("#mc-stop-slave")) q("#mc-stop-slave").disabled = !mcSlaveRunning;
  // M2 诊断/控制按钮随连接状态启用
  for (const id of ["#mc-read-type", "#mc-read-status", "#mc-read-clock", "#mc-echo",
                    "#mc-random-read", "#mc-remote-run", "#mc-remote-stop", "#mc-remote-reset"]) {
    if (q(id)) q(id).disabled = !mcConnected || c24ReadOnly;
  }
  const diagState = document.querySelector("#mc-diag-state");
  if (diagState) diagState.textContent = mcConnected ? (c24ReadOnly ? "C24 只读" : "已连接") : "需要连接";
}

function mcRenderRows(address, values, isBit) {
  const tbody = document.querySelector("#mc-results");
  if (!tbody) return;
  tbody.replaceChildren();
  if (!values || values.length === 0) {
    tbody.innerHTML = '<tr class="empty-row"><td colspan="5">无数据</td></tr>';
    return;
  }
  // 解析地址前缀和起始号用于显示"软元件名";X/Y 为八进制编号(Y7 之后是 Y10),其余十进制
  const m = address.match(/^([A-Za-z]+)(\d+)(?:\.(\d+))?/);
  const labels = m ? formatDeviceSeries(m[1], m[2], values.length) : null;
  for (let i = 0; i < values.length; i++) {
    const row = document.createElement("tr");
    const v = values[i];
    const name = labels ? labels[i] : String(i);
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
    if (mcFxProtocol === "links" || mcFxProtocol === "prog") {
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
    if (mcFxProtocol === "c24") {
      const route = resolveMelsecRoute("mc-c24");
      setNotice("error", route.label + " 写入未开放", route.reason);
      return;
    }
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
    if (mcFxProtocol === "links" || mcFxProtocol === "prog") {
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

let omConnected = false, omSlaveRunning = false, omIsUdp = false, omActiveVariant = null;
const OM_CONN_ID = "omron";
const OM_FINS_MAX_POINTS = 512;
const OM_HOSTLINK_MAX_POINTS = 100;

function omSetState(text, ok = false) {
  const el = document.querySelector("#om-state");
  if (el) { el.textContent = text; el.style.color = ok ? "var(--ok)" : ""; }
}
function omSyncButtons() {
  const q = (s) => document.querySelector(s);
  if (q("#om-connect")) q("#om-connect").disabled = omConnected;
  if (q("#om-disconnect")) q("#om-disconnect").disabled = !omConnected;
  if (q("#om-read")) q("#om-read").disabled = !omConnected;
  const hostLinkSerial = ["hostlink-serial", "hostlink-fins-serial"].includes(omActiveVariant);
  if (q("#om-write")) q("#om-write").disabled = !omConnected || hostLinkSerial;
  if (q("#om-transport")) q("#om-transport").disabled = omConnected;
  if (q("#om-start-slave")) q("#om-start-slave").disabled = omSlaveRunning || hostLinkSerial;
  if (q("#om-stop-slave")) q("#om-stop-slave").disabled = !omSlaveRunning;
}
function omApplyVariant() {
  const variant = document.querySelector("#om-transport")?.value || "tcp";
  const isHostLink = ["hostlink-serial", "hostlink-fins-serial"].includes(variant);
  document.querySelector("#om-network-fields")?.classList.toggle("hidden", isHostLink);
  document.querySelector("#om-serial-station-wrap")?.classList.toggle("hidden", !isHostLink);
  const hint = document.querySelector("#om-hint");
  if (hint) hint.textContent = variant === "hostlink-fins-serial"
    ? "HostLink FINS 首轮只读字：D100/CIO0/W0/H0 · 站号 0..31 · 共享 COM"
    : isHostLink
    ? "HostLink C-mode 首轮只读 DM：D100 · 站号 0..31 · 共享 COM"
    : "地址:D100 / CIO10.00 / W0 / H50 / T0(TS/CS 查手册)";
  const title = document.querySelector("#om-connection-title");
  if (title) title.textContent = variant === "hostlink-fins-serial"
    ? "HostLink FINS 串口只读配置"
    : isHostLink ? "HostLink C-mode 串口只读配置" : "FINS 连接配置";
  omSyncButtons();
}
async function omConnect() {
  if (omConnected) return;
  const variant = document.querySelector("#om-transport")?.value || "tcp";
  omActiveVariant = variant;
  if (["hostlink-serial", "hostlink-fins-serial"].includes(variant)) {
    const station = Number(document.querySelector("#om-serial-station")?.value);
    if (!Number.isInteger(station) || station < 0 || station > 31) {
      omSetState("站号无效");
      setNotice("error", "HostLink 站号无效", "请输入 0 到 31 的整数");
      omActiveVariant = null;
      omSyncButtons();
      return;
    }
    try {
      const status = await callBackend("get_serial_status", {});
      if (!status?.isOpen) throw new Error("请先在主站页打开 HostLink 使用的 COM 串口");
      omConnected = true;
      omSetState(`HostLink 只读就绪 · 站 ${station}`, true);
      setNotice("success", "HostLink 串口已就绪", variant === "hostlink-fins-serial" ? "当前只开放 FINS 0101 字读取" : "当前只开放 C-mode RR 读 DM");
    } catch (error) {
      omActiveVariant = null;
      omSetState("连接失败");
      setNotice("error", "HostLink 串口不可用", error.message || String(error));
    } finally { omSyncButtons(); }
    return;
  }
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
    omActiveVariant = null;
    omSetState("连接失败");
    setNotice("error", "FINS 连接失败", error.message || String(error));
  } finally { omSyncButtons(); }
}
async function omDisconnect() {
  if (!omConnected) return;
  if (!["hostlink-serial", "hostlink-fins-serial"].includes(omActiveVariant)) {
    try { await callBackend("close_connection", { connectionId: OM_CONN_ID }); } catch { }
  }
  omConnected = false;
  omActiveVariant = null;
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
  const count = Number(document.querySelector("#om-points")?.value);
  if (!address) { setNotice("error", "地址无效", "如 D100 / CIO0.00 / W0 / T0"); return; }
  const maxPoints = ["hostlink-serial", "hostlink-fins-serial"].includes(omActiveVariant)
    ? OM_HOSTLINK_MAX_POINTS
    : OM_FINS_MAX_POINTS;
  if (!Number.isInteger(count) || count < 1 || count > maxPoints) {
    setNotice("error", "读取数量无效", `当前变体允许 1..${maxPoints} 点`);
    return;
  }
  try {
    if (["hostlink-serial", "hostlink-fins-serial"].includes(omActiveVariant)) {
      const r = await callBackend("omron_hostlink_serial_read", {
        station: Number(document.querySelector("#om-serial-station")?.value) || 0,
        address,
        count,
        timeoutMs: 1500,
        mode: omActiveVariant === "hostlink-fins-serial" ? "fins" : "cmode",
      });
      if (r?.ok === false) throw new Error(r.error?.message || "HostLink 读取失败");
      omRender(address, omActiveVariant === "hostlink-fins-serial" ? (r.values || []) : (r.words || []), false);
      setNotice("success", "HostLink 读取成功", `${r.address || address} × ${r.count || count} · 只读`);
      return;
    }
    const r = await callBackend("fins_read", { connectionId: OM_CONN_ID, address, count });
    if (r.endCode !== 0) { setNotice("error", `FINS 0x${r.endCode.toString(16).toUpperCase().padStart(4, "0")}`, ""); return; }
    omRender(address, r.values, r.isBit);
    setNotice("success", "读取成功", `${address} × ${count}`);
  } catch (error) { setNotice("error", "读取失败", error.message || String(error)); }
}
async function omWrite() {
  if (!omConnected) return;
  if (["hostlink-serial", "hostlink-fins-serial"].includes(omActiveVariant)) {
    setNotice("info", "HostLink 当前只读", "C-mode RR 写入未开放");
    return;
  }
  const address = document.querySelector("#om-address")?.value?.trim();
  const raw = document.querySelector("#om-write-values")?.value?.trim() || "";
  if (!address) return;
  const values = raw.split(",").map((v) => Number(v.trim()));
  if (!values.length || values.some((v) => !Number.isInteger(v) || v < 0 || v > 65535)) {
    setNotice("error", "写入值无效", "逗号分隔的整数(0-65535)"); return;
  }
  if (values.length > OM_FINS_MAX_POINTS) {
    setNotice("error", "写入数量超限", `FINS 网络写入当前软件安全上限 ${OM_FINS_MAX_POINTS} 点`);
    return;
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
  q("#om-transport", omApplyVariant, "change");
  omApplyVariant();
  const addr = document.querySelector("#om-address");
  if (addr) addr.addEventListener("keydown", (e) => { if (e.key === "Enter" && omConnected) omRead(); });
}

// === Allen-Bradley EtherNet/IP / CIP（TCP 只读会话 + 编解码）===

function abUnsigned(value, max, label) {
  const text = String(value ?? "").trim();
  const number = /^0x/i.test(text) ? Number.parseInt(text, 16) : Number(text);
  if (!Number.isSafeInteger(number) || number < 0 || number > max) {
    throw new Error(`${label} 必须是 0..${max} 的整数`);
  }
  return number;
}

function abFrameText(bytes) {
  return (bytes || []).map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
}

function showAbResult(result) {
  const output = document.querySelector("#ab-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (key === "frame" || key === "payload" || key === "data" || key === "tagPath") return Array.isArray(value) ? abFrameText(value) : value;
    return value;
  }, 2);
}

function abCurrentPayload() {
  return {
    sessionHandle: abUnsigned(document.querySelector("#ab-session")?.value || "0", 0xFFFF_FFFF, "Session Handle"),
    senderContext: abUnsigned(document.querySelector("#ab-context")?.value || "0", Number.MAX_SAFE_INTEGER, "Sender Context"),
  };
}

function abHost() {
  const value = guideValue("#ab-host", "127.0.0.1").trim();
  if (!value) throw new Error("EtherNet/IP TCP 主机不能为空");
  return value;
}

function abPort() {
  return abUnsigned(document.querySelector("#ab-port")?.value || "44818", 65535, "EtherNet/IP TCP 端口") || 44818;
}

function abSessionId() {
  return "enip-live";
}

async function abBuild(command, payload) {
  try {
    const result = await callBackend(command, payload);
    showAbResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#ab-frame-input");
      if (input) input.value = abFrameText(result.frame);
    }
    setNotice("success", "CIP 报文已生成", `${command} · 软件编解码，不代表实机响应`);
  } catch (error) {
    showAbResult({ error: error.message || String(error) });
    setNotice("error", "CIP 编解码失败", error.message || String(error));
  }
}

async function abParse(command) {
  try {
    const frame = parseHexInput(document.querySelector("#ab-frame-input")?.value || "");
    if (!frame.length) throw new Error("请先粘贴或生成 ENIP HEX 报文");
    const result = await callBackend(command, { frame });
    showAbResult(result);
    setNotice("success", "CIP 报文已解析", `${command} · 仅验证软件边界`);
  } catch (error) {
    showAbResult({ error: error.message || String(error) });
    setNotice("error", "CIP 解析失败", error.message || String(error));
  }
}

function initAllenBradleyUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#ab-open-connection", async () => {
    try {
      const result = await callBackend("open_enip_connection", { connectionId: abSessionId(), host: abHost(), port: abPort() });
      showAbResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "EtherNet/IP RegisterSession 失败");
      const handle = result?.sessionHandle;
      if (handle !== undefined) {
        const input = document.querySelector("#ab-session");
        if (input) input.value = String(handle);
      }
      setNotice("success", "EtherNet/IP TCP 只读会话已建立", `${abHost()}:${abPort()} · RegisterSession`);
    } catch (error) {
      showAbResult({ error: error.message || String(error) });
      setNotice("error", "EtherNet/IP 连接失败", error.message || String(error));
    }
  });
  q("#ab-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: abSessionId() });
      showAbResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "EtherNet/IP 断开失败");
      setNotice("success", "EtherNet/IP TCP 会话已断开", "");
    } catch (error) {
      showAbResult({ error: error.message || String(error) });
      setNotice("error", "EtherNet/IP 断开失败", error.message || String(error));
    }
  });
  q("#ab-register", () => {
    try {
      abBuild("enip_build_register_session", {
        sessionHandle: 0,
        senderContext: abUnsigned(document.querySelector("#ab-context")?.value || "0", Number.MAX_SAFE_INTEGER, "Sender Context"),
      });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ab-unregister", () => {
    try { abBuild("enip_build_unregister_session", abCurrentPayload()); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ab-read-tag", () => {
    try {
      const tag = document.querySelector("#ab-tag")?.value?.trim();
      if (!tag) throw new Error("CIP Tag 不能为空");
      abBuild("enip_build_read_tag", {
        ...abCurrentPayload(),
        tag,
        elements: abUnsigned(document.querySelector("#ab-elements")?.value || "1", 65535, "Elements"),
      });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ab-live-read", async () => {
    try {
      const tag = document.querySelector("#ab-tag")?.value?.trim();
      if (!tag) throw new Error("CIP Tag 不能为空");
      const result = await callBackend("enip_read_tag", {
        connectionId: abSessionId(),
        tag,
        elements: abUnsigned(document.querySelector("#ab-elements")?.value || "1", 65535, "Elements"),
      });
      showAbResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "CIP TCP 只读失败");
      setNotice("success", "CIP TCP 只读完成", `${tag} · ${result?.dataHex || ""}`);
    } catch (error) {
      showAbResult({ error: error.message || String(error) });
      setNotice("error", "CIP TCP 只读失败", error.message || String(error));
    }
  });
  q("#ab-parse-frame", () => abParse("enip_parse_frame"));
  q("#ab-parse-cip", () => abParse("enip_parse_cip_response"));
  q("#ab-clear", () => {
    const input = document.querySelector("#ab-frame-input");
    const output = document.querySelector("#ab-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成或粘贴报文。";
  });
}

// === Beckhoff ADS / AMS（TCP 只读会话 + 编解码）===

function adsFrameText(bytes) {
  return (bytes || []).map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
}

function showAdsResult(result) {
  const output = document.querySelector("#ads-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "payload", "data", "writeData"].includes(key) && Array.isArray(value)) return adsFrameText(value);
    return value;
  }, 2);
}

function adsUnsigned(value, max, label) {
  return abUnsigned(value, max, label);
}

function adsContextPayload() {
  return {
    targetNetId: document.querySelector("#ads-target-netid")?.value?.trim() || "",
    targetPort: adsUnsigned(document.querySelector("#ads-target-port")?.value || "851", 65535, "目标 AMS Port"),
    sourceNetId: document.querySelector("#ads-source-netid")?.value?.trim() || "",
    sourcePort: adsUnsigned(document.querySelector("#ads-source-port")?.value || "32905", 65535, "源 AMS Port"),
    invokeId: adsUnsigned(document.querySelector("#ads-invoke")?.value || "0", 0xFFFF_FFFF, "InvokeId"),
  };
}

function adsAddressPayload() {
  return {
    indexGroup: adsUnsigned(document.querySelector("#ads-group")?.value || "0", 0xFFFF_FFFF, "IndexGroup"),
    indexOffset: adsUnsigned(document.querySelector("#ads-offset")?.value || "0", 0xFFFF_FFFF, "IndexOffset"),
    readLength: adsUnsigned(document.querySelector("#ads-read-length")?.value || "0", 0xFFFF_FFFF, "Read bytes"),
  };
}

function adsHost() {
  const value = guideValue("#ads-host", "127.0.0.1").trim();
  if (!value) throw new Error("ADS/TCP 主机不能为空");
  return value;
}

function adsPort() {
  return adsUnsigned(document.querySelector("#ads-port")?.value || "48898", 65535, "ADS/TCP 端口") || 48898;
}

function adsSessionId() {
  return "ads-live";
}

async function adsBuild(command, payload) {
  try {
    const result = await callBackend(command, payload);
    showAdsResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#ads-frame-input");
      if (input) input.value = adsFrameText(result.frame);
    }
    setNotice("success", "ADS 报文已生成", `${command} · 软件编解码，不代表 TwinCAT/PLC 实机响应`);
  } catch (error) {
    showAdsResult({ error: error.message || String(error) });
    setNotice("error", "ADS 编解码失败", error.message || String(error));
  }
}

async function adsParse(command) {
  try {
    const frame = parseHexInput(document.querySelector("#ads-frame-input")?.value || "");
    if (!frame.length) throw new Error("请先粘贴或生成 ADS HEX 报文");
    const payload = { frame };
    if (command === "ads_parse_response") {
      payload.expectedInvokeId = adsUnsigned(document.querySelector("#ads-invoke")?.value || "0", 0xFFFF_FFFF, "InvokeId");
    }
    const result = await callBackend(command, payload);
    showAdsResult(result);
    setNotice("success", "ADS 报文已解析", `${command} · 仅验证软件边界`);
  } catch (error) {
    showAdsResult({ error: error.message || String(error) });
    setNotice("error", "ADS 解析失败", error.message || String(error));
  }
}

function initBeckhoffUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#ads-open-connection", async () => {
    try {
      const context = adsContextPayload();
      const result = await callBackend("open_ads_connection", {
        connectionId: adsSessionId(),
        host: adsHost(),
        port: adsPort(),
        ...context,
      });
      showAdsResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "ADS/TCP 连接失败");
      setNotice("success", "ADS/TCP 只读会话已建立", `${adsHost()}:${adsPort()} · ${context.targetNetId}:${context.targetPort}`);
    } catch (error) {
      showAdsResult({ error: error.message || String(error) });
      setNotice("error", "ADS/TCP 连接失败", error.message || String(error));
    }
  });
  q("#ads-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: adsSessionId() });
      showAdsResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "ADS/TCP 断开失败");
      setNotice("success", "ADS/TCP 会话已断开", "");
    } catch (error) {
      showAdsResult({ error: error.message || String(error) });
      setNotice("error", "ADS/TCP 断开失败", error.message || String(error));
    }
  });
  q("#ads-build-read", () => {
    try { adsBuild("ads_build_read", { ...adsContextPayload(), ...adsAddressPayload() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ads-build-write", () => {
    try {
      const data = parseHexInput(document.querySelector("#ads-write-data")?.value || "");
      adsBuild("ads_build_write", { ...adsContextPayload(), ...adsAddressPayload(), data });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ads-build-readwrite", () => {
    try {
      const data = parseHexInput(document.querySelector("#ads-write-data")?.value || "");
      adsBuild("ads_build_readwrite", { ...adsContextPayload(), ...adsAddressPayload(), writeData: data });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ads-build-info", () => {
    try { adsBuild("ads_build_read_device_info", adsContextPayload()); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ads-build-state", () => {
    try { adsBuild("ads_build_read_state", adsContextPayload()); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#ads-live-read", async () => {
    try {
      const result = await callBackend("ads_read", { connectionId: adsSessionId(), ...adsAddressPayload() });
      showAdsResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "ADS Read 失败");
      setNotice("success", "ADS TCP 只读完成", `${result?.dataHex || ""}`);
    } catch (error) {
      showAdsResult({ error: error.message || String(error) });
      setNotice("error", "ADS TCP 只读失败", error.message || String(error));
    }
  });
  q("#ads-live-info", async () => {
    try {
      const result = await callBackend("ads_read_device_info", { connectionId: adsSessionId() });
      showAdsResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "ADS ReadDeviceInfo 失败");
      setNotice("success", "ADS ReadDeviceInfo 完成", "只读");
    } catch (error) {
      showAdsResult({ error: error.message || String(error) });
      setNotice("error", "ADS ReadDeviceInfo 失败", error.message || String(error));
    }
  });
  q("#ads-live-state", async () => {
    try {
      const result = await callBackend("ads_read_state", { connectionId: adsSessionId() });
      showAdsResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "ADS ReadState 失败");
      setNotice("success", "ADS ReadState 完成", "只读");
    } catch (error) {
      showAdsResult({ error: error.message || String(error) });
      setNotice("error", "ADS ReadState 失败", error.message || String(error));
    }
  });
  q("#ads-parse-frame", () => adsParse("ads_parse_frame"));
  q("#ads-parse-response", () => adsParse("ads_parse_response"));
  q("#ads-clear", () => {
    const input = document.querySelector("#ads-frame-input");
    const output = document.querySelector("#ads-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成或粘贴报文。";
  });
}

// === MQTT 3.1.1（TCP 只读订阅 + 编解码）===

function showMqttResult(result) {
  const output = document.querySelector("#mqtt-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "request", "response", "payload"].includes(key) && Array.isArray(value)) return value.map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
    return value;
  }, 2);
}

function mqttHost() {
  const value = guideValue("#mqtt-host", "127.0.0.1").trim();
  if (!value) throw new Error("MQTT Broker 主机不能为空");
  return value;
}

function mqttPort() {
  return abUnsigned(document.querySelector("#mqtt-port")?.value || "1883", 65535, "MQTT TCP 端口") || 1883;
}

function mqttClientId() {
  const value = guideValue("#mqtt-client-id", "nexus-readonly").trim();
  if (!value) throw new Error("MQTT Client ID 不能为空");
  return value;
}

function mqttKeepAlive() {
  return abUnsigned(document.querySelector("#mqtt-keep-alive")?.value || "30", 65535, "Keep Alive");
}

function mqttTopicFilter() {
  const value = guideValue("#mqtt-topic-filter", "factory/line1/#").trim();
  if (!value) throw new Error("MQTT Topic Filter 不能为空");
  return value;
}

function mqttQos() {
  return abUnsigned(document.querySelector("#mqtt-qos")?.value || "0", 2, "QoS");
}

function mqttSessionId() {
  return "mqtt-live";
}

async function mqttBuild(command, payload) {
  try {
    const result = await callBackend(command, payload);
    showMqttResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#mqtt-frame-input");
      if (input) input.value = result.frameHex || result.frame.map((byte) => Number(byte).toString(16).padStart(2, "0").toUpperCase()).join(" ");
    }
    setNotice("success", "MQTT 报文已生成", `${command} · 只读软件编解码，不代表 Broker 实机权限`);
  } catch (error) {
    showMqttResult({ error: error.message || String(error) });
    setNotice("error", "MQTT 编解码失败", error.message || String(error));
  }
}

async function mqttParse(command) {
  try {
    const frame = parseHexInput(document.querySelector("#mqtt-frame-input")?.value || "");
    if (!frame.length) throw new Error("请先粘贴或生成 MQTT HEX 报文");
    const result = await callBackend(command, { frame });
    showMqttResult(result);
    setNotice("success", "MQTT 报文已解析", `${command} · 仅验证软件边界`);
  } catch (error) {
    showMqttResult({ error: error.message || String(error) });
    setNotice("error", "MQTT 解析失败", error.message || String(error));
  }
}

function initMqttUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#mqtt-open-connection", async () => {
    try {
      const result = await callBackend("open_mqtt_connection", {
        connectionId: mqttSessionId(),
        host: mqttHost(),
        port: mqttPort(),
        clientId: mqttClientId(),
        keepAlive: mqttKeepAlive(),
        cleanSession: Boolean(document.querySelector("#mqtt-clean-session")?.checked),
      });
      showMqttResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "MQTT CONNECT 失败");
      setNotice("success", "MQTT TCP 只读会话已建立", `${mqttHost()}:${mqttPort()} · CONNACK 0`);
    } catch (error) {
      showMqttResult({ error: error.message || String(error) });
      setNotice("error", "MQTT 连接失败", error.message || String(error));
    }
  });
  q("#mqtt-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: mqttSessionId() });
      showMqttResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "MQTT 断开失败");
      setNotice("success", "MQTT 会话已断开", "DISCONNECT · 不发布数据");
    } catch (error) {
      showMqttResult({ error: error.message || String(error) });
      setNotice("error", "MQTT 断开失败", error.message || String(error));
    }
  });
  q("#mqtt-subscribe", async () => {
    try {
      const result = await callBackend("mqtt_subscribe", { connectionId: mqttSessionId(), topicFilter: mqttTopicFilter(), qos: mqttQos() });
      showMqttResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "MQTT SUBSCRIBE 失败");
      setNotice("success", "MQTT 只读订阅成功", `${mqttTopicFilter()} · QoS ${mqttQos()}`);
    } catch (error) {
      showMqttResult({ error: error.message || String(error) });
      setNotice("error", "MQTT 订阅失败", error.message || String(error));
    }
  });
  q("#mqtt-read-publish", async () => {
    try {
      const result = await callBackend("mqtt_read_publish", { connectionId: mqttSessionId() });
      showMqttResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "MQTT PUBLISH 读取失败");
      setNotice("success", "MQTT PUBLISH 已读取", `${result?.topic || "Topic"} · QoS ${result?.qos ?? "—"}`);
    } catch (error) {
      showMqttResult({ error: error.message || String(error) });
      setNotice("error", "MQTT PUBLISH 读取失败", error.message || String(error));
    }
  });
  q("#mqtt-ping", async () => {
    try {
      const result = await callBackend("mqtt_ping", { connectionId: mqttSessionId() });
      showMqttResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "MQTT PING 失败");
      setNotice("success", "MQTT PING/PINGRESP 成功", "只做心跳诊断");
    } catch (error) {
      showMqttResult({ error: error.message || String(error) });
      setNotice("error", "MQTT PING 失败", error.message || String(error));
    }
  });
  q("#mqtt-build-connect", () => mqttBuild("mqtt_build_connect", { clientId: mqttClientId(), keepAlive: mqttKeepAlive(), cleanSession: Boolean(document.querySelector("#mqtt-clean-session")?.checked) }));
  q("#mqtt-build-subscribe", () => mqttBuild("mqtt_build_subscribe", { packetId: 1, topicFilter: mqttTopicFilter(), qos: mqttQos() }));
  q("#mqtt-build-pingreq", () => mqttBuild("mqtt_build_pingreq", {}));
  q("#mqtt-build-disconnect", () => mqttBuild("mqtt_build_disconnect", {}));
  q("#mqtt-parse-connack", () => mqttParse("mqtt_parse_connack"));
  q("#mqtt-parse-suback", () => mqttParse("mqtt_parse_suback"));
  q("#mqtt-parse-publish", () => mqttParse("mqtt_parse_publish"));
  q("#mqtt-clear", () => {
    const input = document.querySelector("#mqtt-frame-input");
    const output = document.querySelector("#mqtt-output");
    if (input) input.value = "";
    if (output) output.textContent = "先连接 Broker 或生成/解析 MQTT 报文。";
  });
}

// === IEC 60870-5-104（TCP 只读 Client/Master + 总召） ===

function showIec104Result(result) {
  const output = document.querySelector("#iec104-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "request", "response", "asdu"].includes(key) && Array.isArray(value)) return abFrameText(value);
    if (["receivedFrames", "acknowledgementFrames"].includes(key) && Array.isArray(value)) {
      return value.map((frame) => Array.isArray(frame) ? abFrameText(frame) : frame);
    }
    return value;
  }, 2);
}

function iec104SessionId() {
  return "iec104-readonly";
}

function iec104Host() {
  const value = guideValue("#iec104-host", "127.0.0.1").trim();
  if (!value) throw new Error("IEC104 RTU/IED 地址不能为空");
  return value;
}

function iec104Port() {
  return abUnsigned(document.querySelector("#iec104-port")?.value || "2404", 65535, "IEC104 TCP 端口") || 2404;
}

function iec104CommonAddress() {
  return abUnsigned(document.querySelector("#iec104-common-address")?.value || "1", 65535, "IEC104 公共地址");
}

function iec104OriginatorAddress() {
  return abUnsigned(document.querySelector("#iec104-originator")?.value || "0", 255, "IEC104 发起方地址");
}

function iec104Group() {
  return abUnsigned(document.querySelector("#iec104-group")?.value || "0", 16, "IEC104 总召组");
}

function iec104ReceiveSequence() {
  return abUnsigned(document.querySelector("#iec104-receive-sequence")?.value || "0", 32767, "IEC104 N(R)");
}

function writeIec104Frame(result) {
  const input = document.querySelector("#iec104-frame-input");
  const frame = result?.frame || result?.request;
  if (input && Array.isArray(frame)) input.value = result.frameHex || result.requestHex || abFrameText(frame);
}

async function iec104Build(command, payload) {
  try {
    const result = await callBackend(command, payload);
    showIec104Result(result);
    writeIec104Frame(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${command} 失败`);
    setNotice("success", "IEC104 报文已生成", `${command} · 只读软件编解码`);
  } catch (error) {
    showIec104Result({ error: error.message || String(error) });
    setNotice("error", "IEC104 编解码失败", error.message || String(error));
  }
}

function initIec104Ui() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#iec104-open-connection", async () => {
    try {
      const result = await callBackend("open_iec104_connection", {
        connectionId: iec104SessionId(),
        host: iec104Host(),
        port: iec104Port(),
        commonAddress: iec104CommonAddress(),
        originatorAddress: iec104OriginatorAddress(),
      });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 STARTDT 失败");
      setNotice("success", "IEC104 只读主站会话已建立", `${iec104Host()}:${iec104Port()} · CA ${iec104CommonAddress()} · STARTDT_CON`);
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 连接失败", error.message || String(error));
    }
  });
  q("#iec104-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: iec104SessionId() });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 断开失败");
      setNotice("success", "IEC104 会话已断开", "已 best-effort 发送 STOPDT_ACT；未开放任何控制写入");
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 断开失败", error.message || String(error));
    }
  });
  q("#iec104-test-frame", async () => {
    try {
      const result = await callBackend("iec104_test_frame", { connectionId: iec104SessionId() });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 TESTFR 失败");
      setNotice("success", "IEC104 TESTFR 已确认", "TESTFR_ACT / TESTFR_CON");
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 TESTFR 失败", error.message || String(error));
    }
  });
  q("#iec104-interrogate", async () => {
    try {
      const result = await callBackend("iec104_general_interrogation", {
        connectionId: iec104SessionId(),
        group: iec104Group(),
      });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 总召失败");
      setNotice("success", "IEC104 只读总召完成", `QOI ${result?.qualifier ?? 20 + iec104Group()} · ${result?.pointCount ?? 0} 点 · ACT_TERM`);
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 总召失败", error.message || String(error));
    }
  });
  q("#iec104-build-interrogation", () => iec104Build("iec104_build_general_interrogation", {
    commonAddress: iec104CommonAddress(),
    group: iec104Group(),
    originatorAddress: iec104OriginatorAddress(),
    sendSequence: 0,
    receiveSequence: 0,
  }));
  q("#iec104-build-s-frame", () => iec104Build("iec104_build_s_frame", {
    receiveSequence: iec104ReceiveSequence(),
  }));
  q("#iec104-build-u-frame", () => iec104Build("iec104_build_u_frame", {
    function: guideValue("#iec104-u-function", "TESTFR_ACT"),
  }));
  q("#iec104-parse-apdu", async () => {
    try {
      const frame = parseHexInput(guideValue("#iec104-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴 IEC104 APDU HEX");
      const result = await callBackend("iec104_parse_apdu", { frame });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 APDU 解析失败");
      setNotice("success", "IEC104 APDU 已解析", `${result?.format || result?.apdu?.format || "APDU"} · 严格长度/控制域校验`);
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 APDU 解析失败", error.message || String(error));
    }
  });
  q("#iec104-parse-asdu", async () => {
    try {
      let asdu = parseHexInput(guideValue("#iec104-frame-input", ""));
      if (asdu[0] === 0x68 && asdu.length >= 6) asdu = asdu.slice(6);
      if (!asdu.length) throw new Error("请先粘贴 IEC104 ASDU 或完整 I 帧 HEX");
      const result = await callBackend("iec104_parse_asdu", { asdu });
      showIec104Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "IEC104 ASDU 解析失败");
      setNotice("success", "IEC104 ASDU 已解析", `${result?.asdu?.typeName || "ASDU"} · ${result?.pointCount ?? 0} 点`);
    } catch (error) {
      showIec104Result({ error: error.message || String(error) });
      setNotice("error", "IEC104 ASDU 解析失败", error.message || String(error));
    }
  });
  q("#iec104-clear", () => {
    const input = document.querySelector("#iec104-frame-input");
    const output = document.querySelector("#iec104-output");
    if (input) input.value = "";
    if (output) output.textContent = "先连接独立 Outstation/真实站端，或生成/解析 IEC104 报文。";
  });
}

// === DNP3（TCP 只读 Master + Class 0/1/2/3） ===

function showDnp3Result(result) {
  const output = document.querySelector("#dnp3-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "pdu", "userData"].includes(key) && Array.isArray(value)) return abFrameText(value);
    if (["requestFrames", "responseFrames", "confirmationFrames"].includes(key) && Array.isArray(value)) {
      return value.map((frame) => Array.isArray(frame) ? abFrameText(frame) : frame);
    }
    return value;
  }, 2);
}

function dnp3SessionId() {
  return "dnp3-readonly";
}

function dnp3Host() {
  const value = guideValue("#dnp3-host", "127.0.0.1").trim();
  if (!value) throw new Error("DNP3 RTU/IED 地址不能为空");
  return value;
}

function dnp3Port() {
  return abUnsigned(document.querySelector("#dnp3-port")?.value || "20000", 65535, "DNP3 TCP 端口") || 20000;
}

function dnp3MasterAddress() {
  return abUnsigned(document.querySelector("#dnp3-master-address")?.value || "1", 65519, "DNP3 Master Link Address");
}

function dnp3OutstationAddress() {
  return abUnsigned(document.querySelector("#dnp3-outstation-address")?.value || "1024", 65519, "DNP3 Outstation Link Address");
}

function dnp3Class() {
  return abUnsigned(document.querySelector("#dnp3-class")?.value || "1", 3, "DNP3 Class");
}

function dnp3ClassPayload(selected = dnp3Class()) {
  return {
    class0: selected === 0,
    class1: selected === 1,
    class2: selected === 2,
    class3: selected === 3,
  };
}

function dnp3Group() {
  return abUnsigned(document.querySelector("#dnp3-group")?.value || "30", 255, "DNP3 Group");
}

function dnp3Variation() {
  return abUnsigned(document.querySelector("#dnp3-variation")?.value || "5", 255, "DNP3 Variation");
}

function dnp3Start() {
  return abUnsigned(document.querySelector("#dnp3-start")?.value || "0", 65535, "DNP3 Start");
}

function dnp3Stop() {
  return abUnsigned(document.querySelector("#dnp3-stop")?.value || "0", 65535, "DNP3 Stop");
}

function dnp3Sequence() {
  return abUnsigned(document.querySelector("#dnp3-sequence")?.value || "0", 15, "DNP3 Application SEQ");
}

function writeDnp3Bytes(result) {
  const input = document.querySelector("#dnp3-frame-input");
  const bytes = result?.frame || result?.pdu;
  if (input && Array.isArray(bytes)) input.value = result.frameHex || result.pduHex || abFrameText(bytes);
}

async function dnp3Build(command, payload) {
  try {
    const result = await callBackend(command, payload);
    showDnp3Result(result);
    writeDnp3Bytes(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${command} 失败`);
    setNotice("success", "DNP3 只读报文已生成", `${command} · 不生成 Select/Operate/校时`);
  } catch (error) {
    showDnp3Result({ error: error.message || String(error) });
    setNotice("error", "DNP3 编解码失败", error.message || String(error));
  }
}

function initDnp3Ui() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#dnp3-open-connection", async () => {
    try {
      const result = await callBackend("open_dnp3_connection", {
        connectionId: dnp3SessionId(),
        host: dnp3Host(),
        port: dnp3Port(),
        masterAddress: dnp3MasterAddress(),
        outstationAddress: dnp3OutstationAddress(),
      });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 连接失败");
      setNotice("success", "DNP3 只读 Master 已连接", `${dnp3Host()}:${dnp3Port()} · Link ${dnp3MasterAddress()} → ${dnp3OutstationAddress()}`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 连接失败", error.message || String(error));
    }
  });
  q("#dnp3-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: dnp3SessionId() });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 断开失败");
      setNotice("success", "DNP3 会话已断开", "未发送 Select/Operate、校时、Restart 或 Freeze");
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 断开失败", error.message || String(error));
    }
  });
  q("#dnp3-integrity-poll", async () => {
    try {
      const result = await callBackend("dnp3_integrity_poll", { connectionId: dnp3SessionId() });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 完整性轮询失败");
      setNotice("success", "DNP3 完整性轮询完成", `Class 0/1/2/3 · ${result?.pointCount ?? 0} 点 · IIN ${result?.iin?.labels?.join(", ") || "none"}`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 完整性轮询失败", error.message || String(error));
    }
  });
  q("#dnp3-class-scan", async () => {
    try {
      const selected = dnp3Class();
      const result = await callBackend("dnp3_class_scan", {
        connectionId: dnp3SessionId(),
        ...dnp3ClassPayload(selected),
      });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 Class 扫描失败");
      setNotice("success", `DNP3 Class ${selected} 扫描完成`, `${result?.pointCount ?? 0} 点 · 自发响应 ${result?.unsolicitedResponseCount ?? 0}`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 Class 扫描失败", error.message || String(error));
    }
  });
  q("#dnp3-read-range", async () => {
    try {
      const result = await callBackend("dnp3_read", {
        connectionId: dnp3SessionId(),
        group: dnp3Group(),
        variation: dnp3Variation(),
        start: dnp3Start(),
        stop: dnp3Stop(),
      });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 对象读取失败");
      setNotice("success", `DNP3 g${dnp3Group()}v${dnp3Variation()} 读取完成`, `${result?.pointCount ?? 0} 点 · 只读 READ`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 对象读取失败", error.message || String(error));
    }
  });
  q("#dnp3-build-integrity", () => dnp3Build("dnp3_build_class_scan", {
    sequence: dnp3Sequence(), class0: true, class1: true, class2: true, class3: true,
  }));
  q("#dnp3-build-read", () => dnp3Build("dnp3_build_read_request", {
    sequence: dnp3Sequence(), group: dnp3Group(), variation: dnp3Variation(), start: dnp3Start(), stop: dnp3Stop(),
  }));
  q("#dnp3-build-confirm", () => dnp3Build("dnp3_build_confirm", {
    sequence: dnp3Sequence(), unsolicited: false,
  }));
  q("#dnp3-parse-link", async () => {
    try {
      const frame = parseHexInput(guideValue("#dnp3-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴完整 DNP3 链路帧 HEX");
      const result = await callBackend("dnp3_parse_link_frame", { frame });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 链路帧解析失败");
      setNotice("success", "DNP3 链路帧已解析", `Link ${result?.link?.source} → ${result?.link?.destination} · CRC 全部通过`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 链路帧解析失败", error.message || String(error));
    }
  });
  q("#dnp3-parse-application", async () => {
    try {
      const pdu = parseHexInput(guideValue("#dnp3-frame-input", ""));
      if (!pdu.length) throw new Error("请先粘贴 DNP3 应用响应 PDU HEX");
      const result = await callBackend("dnp3_parse_application_response", { pdu });
      showDnp3Result(result);
      if (result?.ok === false) throw new Error(result.error?.message || "DNP3 应用响应解析失败");
      setNotice("success", "DNP3 应用响应已解析", `${result?.pointCount ?? 0} 点 · IIN ${result?.response?.iin?.labels?.join(", ") || "none"}`);
    } catch (error) {
      showDnp3Result({ error: error.message || String(error) });
      setNotice("error", "DNP3 应用响应解析失败", error.message || String(error));
    }
  });
  q("#dnp3-clear", () => {
    const input = document.querySelector("#dnp3-frame-input");
    const output = document.querySelector("#dnp3-output");
    if (input) input.value = "";
    if (output) output.textContent = "先连接独立 Outstation/真实站端，或生成/解析 DNP3 报文。";
  });
}

// === DL/T 645-1997/2007（共享 COM 只读电表） ===

function showDlt645Result(result) {
  const output = document.querySelector("#dlt645-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "tx", "rx", "payload", "data", "wireBytes", "addressBytes"].includes(key) && Array.isArray(value)) {
      return abFrameText(value);
    }
    return value;
  }, 2);
}

function dlt645Version() {
  const version = guideValue("#dlt645-version", "2007");
  if (!["1997", "2007"].includes(version)) throw new Error("DL/T 645 版本只能是 1997 或 2007");
  return version;
}

function dlt645Address() {
  const address = guideValue("#dlt645-address", "");
  if (!/^\d{12}$/.test(address)) throw new Error("DL/T 645 表地址必须是恰好 12 位十进制数字");
  if (address === "999999999999") throw new Error("只读请求不能使用广播地址 999999999999");
  return address;
}

function dlt645DataId() {
  const version = dlt645Version();
  const dataId = guideValue("#dlt645-data-id", "").replace(/\s/g, "").toUpperCase();
  const digits = version === "1997" ? 4 : 8;
  if (!new RegExp(`^[0-9A-F]{${digits}}$`).test(dataId)) {
    throw new Error(`DL/T 645-${version} DI 必须是 ${digits} 位十六进制`);
  }
  return dataId;
}

function dlt645Payload() {
  return {
    version: dlt645Version(),
    address: dlt645Address(),
    dataId: dlt645DataId(),
    preambleCount: abUnsigned(document.querySelector("#dlt645-preamble")?.value || "4", 4, "FE 前导数量"),
  };
}

async function dlt645BuildRead() {
  try {
    const payload = dlt645Payload();
    const result = await callBackend("dlt645_build_read_request", payload);
    showDlt645Result(result);
    if (result?.ok === false) throw new Error(result.error?.message || "DL/T 645 读请求生成失败");
    const input = document.querySelector("#dlt645-frame-input");
    if (input && Array.isArray(result?.frame)) input.value = result.frameHex || abFrameText(result.frame);
    setNotice("success", `DL/T 645-${payload.version} 读请求已生成`, `${payload.address} · DI ${payload.dataId} · 只读，不发送`);
  } catch (error) {
    showDlt645Result({ error: error.message || String(error) });
    setNotice("error", "DL/T 645 组帧失败", error.message || String(error));
  }
}

async function dlt645LiveRead() {
  try {
    const payload = {
      ...dlt645Payload(),
      timeoutMs: abUnsigned(document.querySelector("#dlt645-timeout")?.value || "1500", 600000, "DL/T 645 超时"),
      retries: abUnsigned(document.querySelector("#dlt645-retries")?.value || "1", 3, "DL/T 645 重试次数"),
      model: guideValue("#dlt645-model", ""),
      serialNumber: guideValue("#dlt645-device-serial", ""),
    };
    const result = await callBackend("dlt645_serial_read", payload);
    showDlt645Result(result);
    if (!result?.ok) throw new Error(result?.error?.message || "DL/T 645 共享 COM 读取失败");
    const input = document.querySelector("#dlt645-frame-input");
    if (input && Array.isArray(result.rx)) input.value = abFrameText(result.rx);
    const known = result.response?.knownValue;
    const exception = result.meterException;
    if (exception) {
      setNotice("error", "电表返回 DL/T 645 异常", `异常字 0x${Number(exception.code).toString(16).padStart(2, "0").toUpperCase()} · ${exception.flags?.join(", ") || "未标注"}`);
    } else {
      const value = known?.value?.decimal || known?.value?.isoDate || known?.value?.isoTime || known?.value?.raw || "原始数据已返回";
      setNotice("success", `DL/T 645-${payload.version} 只读完成`, `${payload.address} · DI ${payload.dataId} · ${value}${known?.unit ? ` ${known.unit}` : ""}`);
    }
  } catch (error) {
    showDlt645Result({ error: error.message || String(error) });
    setNotice("error", "DL/T 645 共享 COM 读取失败", error.message || String(error));
  }
}

async function dlt645Parse(command, payload, successTitle) {
  try {
    const result = await callBackend(command, payload);
    showDlt645Result(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    setNotice("success", successTitle, "BCD、字节序、+33H 和 CS 校验已由 Rust Core 执行");
  } catch (error) {
    showDlt645Result({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

function applyDlt645VersionDefaults(force = false) {
  const version = dlt645Version();
  const input = document.querySelector("#dlt645-data-id");
  if (!input) return;
  input.maxLength = version === "1997" ? 4 : 8;
  const validForVersion = new RegExp(version === "1997" ? "^[0-9A-Fa-f]{4}$" : "^[0-9A-Fa-f]{8}$").test(input.value.trim());
  if (force || !validForVersion) input.value = version === "1997" ? "9010" : "00010000";
}

function initDlt645Ui() {
  const q = (selector, fn) => document.querySelector(selector)?.addEventListener("click", fn);
  document.querySelector("#dlt645-version")?.addEventListener("change", () => applyDlt645VersionDefaults(true));
  for (const button of document.querySelectorAll(".dlt645-di-preset")) {
    button.addEventListener("click", () => {
      const version = document.querySelector("#dlt645-version");
      const input = document.querySelector("#dlt645-data-id");
      if (version) version.value = button.dataset.version || "2007";
      if (input) input.value = button.dataset.dataId || "00010000";
      applyDlt645VersionDefaults(false);
    });
  }
  q("#dlt645-live-read", () => void dlt645LiveRead());
  q("#dlt645-build-read", () => void dlt645BuildRead());
  q("#dlt645-parse-address", () => {
    try { void dlt645Parse("dlt645_parse_address", { address: dlt645Address() }, "DL/T 645 表地址有效"); }
    catch (error) { showDlt645Result({ error: error.message || String(error) }); setNotice("error", "表地址检查失败", error.message || String(error)); }
  });
  q("#dlt645-parse-data-id", () => {
    try { void dlt645Parse("dlt645_parse_data_id", { version: dlt645Version(), dataId: dlt645DataId() }, "DL/T 645 数据标识有效"); }
    catch (error) { showDlt645Result({ error: error.message || String(error) }); setNotice("error", "DI 检查失败", error.message || String(error)); }
  });
  q("#dlt645-parse-frame", () => {
    try {
      const frame = parseHexInput(guideValue("#dlt645-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴完整 DL/T 645 帧 HEX");
      void dlt645Parse("dlt645_parse_frame", { frame }, "DL/T 645 帧校验通过");
    } catch (error) { showDlt645Result({ error: error.message || String(error) }); setNotice("error", "帧解析失败", error.message || String(error)); }
  });
  q("#dlt645-parse-read-response", () => {
    try {
      const frame = parseHexInput(guideValue("#dlt645-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴完整 DL/T 645 读响应 HEX");
      const payload = dlt645Payload();
      void dlt645Parse("dlt645_parse_read_response", {
        version: payload.version,
        address: payload.address,
        dataId: payload.dataId,
        frame,
      }, "DL/T 645 读响应已解析");
    } catch (error) { showDlt645Result({ error: error.message || String(error) }); setNotice("error", "读响应解析失败", error.message || String(error)); }
  });
  q("#dlt645-clear", () => {
    const input = document.querySelector("#dlt645-frame-input");
    const output = document.querySelector("#dlt645-output");
    if (input) input.value = "";
    if (output) output.textContent = "先选择版本、填写表地址与 DI，再生成只读请求或复用已打开的 COM 读取。";
  });
  applyDlt645VersionDefaults(false);
}

// === CJ/T 188-2004（水/气/热表离线只读编解码）===

function showCjt188Result(result) {
  const output = document.querySelector("#cjt188-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "payload", "wireBytes", "addressBytes"].includes(key) && Array.isArray(value)) {
      return abFrameText(value);
    }
    return value;
  }, 2);
}

function cjt188MeterType() {
  const meterType = guideValue("#cjt188-meter-type", "cold-water");
  if (!["cold-water", "hot-water", "heat", "gas"].includes(meterType)) {
    throw new Error("CJ/T 188 表类型只能是冷水、热水、热量或燃气");
  }
  return meterType;
}

function cjt188Address() {
  const address = guideValue("#cjt188-address", "").replace(/\s/g, "").toUpperCase();
  if (!/^[0-9A-F]{14}$/.test(address)) {
    throw new Error("CJ/T 188 表地址必须是恰好 14 位十六进制/BCD 字符");
  }
  if (address === "AAAAAAAAAAAAAA") {
    throw new Error("本轮读数据不允许使用 AA×7 广播地址");
  }
  return address;
}

function cjt188DataId() {
  const dataId = guideValue("#cjt188-data-id", "").replace(/\s/g, "").toUpperCase();
  if (!/^[0-9A-F]{4}$/.test(dataId)) {
    throw new Error("CJ/T 188-2004 DI 必须是 4 位十六进制（2 字节）");
  }
  return dataId;
}

function cjt188Sequence() {
  return abUnsigned(document.querySelector("#cjt188-sequence")?.value || "1", 255, "CJ/T 188 SER 序列号");
}

function cjt188Payload() {
  return {
    meterType: cjt188MeterType(),
    address: cjt188Address(),
    dataId: cjt188DataId(),
    sequence: cjt188Sequence(),
    preambleCount: abUnsigned(document.querySelector("#cjt188-preamble")?.value || "0", 4, "FE 前导数量"),
  };
}

async function cjt188Run(command, payload, successTitle, successDetail) {
  try {
    const result = await callBackend(command, payload);
    showCjt188Result(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    setNotice("success", successTitle, successDetail);
    return result;
  } catch (error) {
    showCjt188Result({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
    return null;
  }
}

async function cjt188BuildRead() {
  const payload = cjt188Payload();
  const result = await cjt188Run(
    "cjt188_build_read_request",
    payload,
    "CJ/T 188 读请求已生成",
    `${payload.address} · DI ${payload.dataId} · SER ${payload.sequence} · 只生成帧，不发送`,
  );
  if (result?.ok !== false && Array.isArray(result?.frame)) {
    const input = document.querySelector("#cjt188-frame-input");
    if (input) input.value = result.frameHex || abFrameText(result.frame);
  }
}

function initCjt188Ui() {
  const q = (selector, fn) => document.querySelector(selector)?.addEventListener("click", fn);
  q("#cjt188-build-read", () => void cjt188BuildRead());
  q("#cjt188-parse-meter-type", () => {
    try {
      void cjt188Run("cjt188_parse_meter_type", { meterType: cjt188MeterType() }, "CJ/T 188 表类型有效", "表类型代码、单位和确认范围已由 Rust Core 校验");
    } catch (error) { showCjt188Result({ error: error.message || String(error) }); setNotice("error", "表类型检查失败", error.message || String(error)); }
  });
  q("#cjt188-parse-address", () => {
    try {
      void cjt188Run("cjt188_parse_address", { address: cjt188Address() }, "CJ/T 188 表地址有效", "14 位 BCD 与低位对先传字节序已由 Rust Core 校验");
    } catch (error) { showCjt188Result({ error: error.message || String(error) }); setNotice("error", "表地址检查失败", error.message || String(error)); }
  });
  q("#cjt188-parse-data-id", () => {
    try {
      void cjt188Run("cjt188_parse_data_id", { dataId: cjt188DataId() }, "CJ/T 188 数据标识有效", "2 字节 DI 与高位在前线序已由 Rust Core 校验");
    } catch (error) { showCjt188Result({ error: error.message || String(error) }); setNotice("error", "DI 检查失败", error.message || String(error)); }
  });
  q("#cjt188-parse-frame", () => {
    try {
      const frame = parseHexInput(guideValue("#cjt188-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴完整 CJ/T 188 帧 HEX");
      void cjt188Run("cjt188_parse_frame", { frame }, "CJ/T 188 帧校验通过", "单 68H、类型、地址、长度、算术和 CS 和 16H 已验证");
    } catch (error) { showCjt188Result({ error: error.message || String(error) }); setNotice("error", "帧解析失败", error.message || String(error)); }
  });
  q("#cjt188-parse-read-response", () => {
    try {
      const frame = parseHexInput(guideValue("#cjt188-frame-input", ""));
      if (!frame.length) throw new Error("请先粘贴完整 CJ/T 188 读响应 HEX");
      void cjt188Run("cjt188_parse_read_response", { ...cjt188Payload(), frame }, "CJ/T 188 读响应已解析", "表类型/地址/DI/SER 回显与 CS 已验证；未知 DI 保持原始数据");
    } catch (error) { showCjt188Result({ error: error.message || String(error) }); setNotice("error", "读响应解析失败", error.message || String(error)); }
  });
  q("#cjt188-clear", () => {
    const input = document.querySelector("#cjt188-frame-input");
    const output = document.querySelector("#cjt188-output");
    if (input) input.value = "";
    if (output) output.textContent = "先选择表类型并生成 901F 读请求向量，再解析响应帧；本页不提供 COM 发送入口。";
  });
}

// === BACnet/IP（Who-Is / I-Am 离线只读编解码）===

function showBacnetResult(result) {
  const output = document.querySelector("#bacnet-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (key === "frame" && Array.isArray(value)) return abFrameText(value);
    return value;
  }, 2);
}

function bacnetInstanceValue(selector, field) {
  return abUnsigned(document.querySelector(selector)?.value || "0", 4194303, field);
}

function bacnetWhoisPayload() {
  const global = document.querySelector("#bacnet-whois-global")?.checked ?? true;
  const broadcast = document.querySelector("#bacnet-whois-broadcast")?.checked ?? true;
  if (global) return { broadcast };
  const low = bacnetInstanceValue("#bacnet-whois-low", "Who-Is 低范围");
  const high = bacnetInstanceValue("#bacnet-whois-high", "Who-Is 高范围");
  if (low > high) throw new Error("Who-Is 低范围不能大于高范围");
  return { lowLimit: low, highLimit: high, broadcast };
}

function bacnetIamPayload() {
  const maxApdu = abUnsigned(
    document.querySelector("#bacnet-iam-max-apdu")?.value || "480",
    4294967295,
    "I-Am Max-APDU",
  );
  if (!maxApdu) throw new Error("I-Am Max-APDU 不能为 0");
  return {
    deviceInstance: bacnetInstanceValue("#bacnet-iam-instance", "I-Am Device Instance"),
    maxApdu,
    segmentation: guideValue("#bacnet-iam-segmentation", "none"),
    vendorId: abUnsigned(document.querySelector("#bacnet-iam-vendor")?.value || "0", 65535, "I-Am Vendor ID"),
    broadcast: document.querySelector("#bacnet-iam-broadcast")?.checked ?? false,
  };
}

function bacnetReadPropertyPayload() {
  const useIndex = document.querySelector("#bacnet-rp-use-index")?.checked ?? false;
  return {
    objectType: abUnsigned(document.querySelector("#bacnet-rp-object-type")?.value || "0", 1023, "BACnet Object Type"),
    objectInstance: bacnetInstanceValue("#bacnet-rp-object-instance", "BACnet Object Instance"),
    propertyIdentifier: bacnetInstanceValue("#bacnet-rp-property", "BACnet Property ID"),
    propertyArrayIndex: useIndex ? bacnetInstanceValue("#bacnet-rp-index", "BACnet Array Index") : undefined,
    invokeId: abUnsigned(document.querySelector("#bacnet-rp-invoke")?.value || "1", 255, "BACnet Invoke ID"),
  };
}

function bacnetConnectionId() {
  const id = guideValue("#bacnet-connection-id", "").trim();
  if (!id) throw new Error("BACnet connectionId 不能为空");
  return id;
}

function bacnetHost() {
  const host = guideValue("#bacnet-host", "").trim();
  if (!host) throw new Error("BACnet 对端 IP 不能为空");
  return host;
}

function bacnetPort() {
  return abUnsigned(document.querySelector("#bacnet-port")?.value || "47808", 65535, "BACnet UDP 端口") || 47808;
}

function bacnetTimeout() {
  return abUnsigned(document.querySelector("#bacnet-timeout")?.value || "1500", 5000, "BACnet 超时") || 1500;
}

async function bacnetBuild(command, payload, successTitle, successDetail) {
  try {
    const result = await callBackend(command, payload);
    showBacnetResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#bacnet-frame-input");
      if (input) input.value = result.frameHex || abFrameText(result.frame);
    }
    setNotice("success", successTitle, successDetail);
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

async function bacnetParseFrame() {
  try {
    const frame = parseHexInput(guideValue("#bacnet-frame-input", ""));
    if (!frame.length) throw new Error("请先粘贴完整 BACnet/IP UDP 载荷 HEX");
    const result = await callBackend("bacnet_ip_parse_frame", { frame });
    showBacnetResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || "BACnet/IP 帧解析失败");
    setNotice("success", "BACnet/IP 帧校验通过", "BVLC、本地 NPDU、Unconfirmed APDU 和 Who-Is/I-Am 标签已验证；未发送 UDP");
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "BACnet/IP 帧解析失败", error.message || String(error));
  }
}

async function bacnetReadProperty(command, payload, successTitle, successDetail) {
  try {
    const result = await callBackend(command, payload);
    showBacnetResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#bacnet-frame-input");
      if (input) input.value = result.frameHex || abFrameText(result.frame);
    }
    setNotice("success", successTitle, successDetail);
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

async function bacnetConnect() {
  try {
    const payload = {
      connectionId: bacnetConnectionId(),
      host: bacnetHost(),
      port: bacnetPort(),
    };
    const result = await callBackend("open_bacnet_ip_connection", payload);
    showBacnetResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || "BACnet UDP 连接失败");
    setNotice("success", "BACnet UDP 对端已连接", `${payload.host}:${payload.port} · 只读，不使用广播/BBMD`);
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "BACnet UDP 连接失败", error.message || String(error));
  }
}

async function bacnetDisconnect() {
  try {
    const payload = { connectionId: bacnetConnectionId() };
    const result = await callBackend("close_connection", payload);
    showBacnetResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || "BACnet UDP 断开失败");
    setNotice("success", "BACnet UDP 对端已断开", "本地只读会话已释放");
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "BACnet UDP 断开失败", error.message || String(error));
  }
}

async function bacnetWhoisLive() {
  try {
    const whois = bacnetWhoisPayload();
    const payload = {
      connectionId: bacnetConnectionId(),
      timeoutMs: bacnetTimeout(),
    };
    if (!whois.global) {
      payload.lowLimit = whois.lowLimit;
      payload.highLimit = whois.highLimit;
    }
    await bacnetReadProperty(
      "bacnet_ip_whois",
      payload,
      "BACnet Who-Is 已完成",
      "0AH 定向单播，不使用广播",
    );
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "BACnet Who-Is 失败", error.message || String(error));
  }
}

async function bacnetReadPropertyLive() {
  try {
    const read = bacnetReadPropertyPayload();
    const payload = {
      connectionId: bacnetConnectionId(),
      objectType: read.objectType,
      objectInstance: read.objectInstance,
      propertyIdentifier: read.propertyIdentifier,
      timeoutMs: bacnetTimeout(),
    };
    if (read.propertyArrayIndex != null) payload.propertyArrayIndex = read.propertyArrayIndex;
    await bacnetReadProperty(
      "bacnet_ip_read_property_live",
      payload,
      "BACnet ReadProperty 已完成",
      `Invoke ${read.invokeId} · ACK 已核对对象/属性/索引回显`,
    );
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "BACnet ReadProperty 失败", error.message || String(error));
  }
}

async function bacnetParseReadPropertyRequest() {
  try {
    const frame = parseHexInput(guideValue("#bacnet-frame-input", ""));
    if (!frame.length) throw new Error("请先粘贴完整 BACnet ReadProperty 请求 HEX");
    await bacnetReadProperty(
      "bacnet_ip_parse_read_property_request",
      { frame },
      "ReadProperty 请求已解析",
      "BVLC 0AH、DER、Confirmed 0CH、对象/属性/数组索引标签已验证；未发送 UDP",
    );
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "ReadProperty 请求解析失败", error.message || String(error));
  }
}

async function bacnetParseReadPropertyAck() {
  try {
    const frame = parseHexInput(guideValue("#bacnet-frame-input", ""));
    if (!frame.length) throw new Error("请先粘贴完整 BACnet ReadProperty ComplexACK HEX");
    await bacnetReadProperty(
      "bacnet_ip_parse_read_property_ack",
      { frame, expectedRequest: bacnetReadPropertyPayload() },
      "ReadProperty ACK 已解析",
      "Invoke ID、对象、属性、数组索引回显和 [3] 应用值已验证；未知标签保留原始字节",
    );
  } catch (error) {
    showBacnetResult({ error: error.message || String(error) });
    setNotice("error", "ReadProperty ACK 解析失败", error.message || String(error));
  }
}

function updateBacnetWhoisRangeState() {
  const global = document.querySelector("#bacnet-whois-global")?.checked ?? true;
  for (const selector of ["#bacnet-whois-low", "#bacnet-whois-high"]) {
    const input = document.querySelector(selector);
    if (input) input.disabled = global;
  }
}

function initBacnetUi() {
  const q = (selector, fn) => document.querySelector(selector)?.addEventListener("click", fn);
  document.querySelector("#bacnet-whois-global")?.addEventListener("change", updateBacnetWhoisRangeState);
  document.querySelector("#bacnet-rp-use-index")?.addEventListener("change", () => {
    const useIndex = document.querySelector("#bacnet-rp-use-index")?.checked ?? false;
    const input = document.querySelector("#bacnet-rp-index");
    if (input) input.disabled = !useIndex;
  });
  q("#bacnet-build-whois", () => {
    try {
      const payload = bacnetWhoisPayload();
      void bacnetBuild(
        "bacnet_ip_build_whois",
        payload,
        "BACnet Who-Is 已生成",
        payload.lowLimit == null ? "全局范围 · 只生成帧，不发送 UDP" : `范围 ${payload.lowLimit}..${payload.highLimit} · 只生成帧，不发送 UDP`,
      );
    } catch (error) { showBacnetResult({ error: error.message || String(error) }); setNotice("error", "Who-Is 构帧失败", error.message || String(error)); }
  });
  q("#bacnet-build-iam", () => {
    try {
      const payload = bacnetIamPayload();
      void bacnetBuild(
        "bacnet_ip_build_iam",
        payload,
        "BACnet I-Am 已生成",
        `Device ${payload.deviceInstance} · Max-APDU ${payload.maxApdu} · ${payload.segmentation} · 只生成帧`,
      );
    } catch (error) { showBacnetResult({ error: error.message || String(error) }); setNotice("error", "I-Am 构帧失败", error.message || String(error)); }
  });
  q("#bacnet-parse-frame", () => void bacnetParseFrame());
  q("#bacnet-rp-build", () => {
    try {
      const payload = bacnetReadPropertyPayload();
      void bacnetReadProperty(
        "bacnet_ip_build_read_property_request",
        payload,
        "BACnet ReadProperty 请求已生成",
        `Object ${payload.objectType}:${payload.objectInstance} · Property ${payload.propertyIdentifier} · Invoke ${payload.invokeId} · 只生成帧，不发送 UDP`,
      );
    } catch (error) { showBacnetResult({ error: error.message || String(error) }); setNotice("error", "ReadProperty 构帧失败", error.message || String(error)); }
  });
  q("#bacnet-rp-parse-request", () => void bacnetParseReadPropertyRequest());
  q("#bacnet-rp-parse-ack", () => void bacnetParseReadPropertyAck());
  q("#bacnet-connect", () => void bacnetConnect());
  q("#bacnet-disconnect", () => void bacnetDisconnect());
  q("#bacnet-whois-live", () => void bacnetWhoisLive());
  q("#bacnet-rp-live", () => void bacnetReadPropertyLive());
  q("#bacnet-clear", () => {
    const input = document.querySelector("#bacnet-frame-input");
    const output = document.querySelector("#bacnet-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成 Who-Is / I-Am，或粘贴 BACnet/IP UDP 载荷解析；本页不提供 UDP 发送入口。";
  });
  updateBacnetWhoisRangeState();
}

// === KNXnet/IP Tunneling v1（离线只读编解码）===

function showKnxResult(result) {
  const output = document.querySelector("#knx-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "payload", "wireBytes", "suggestedAck"].includes(key) && Array.isArray(value)) {
      return abFrameText(value);
    }
    return value;
  }, 2);
}

function knxGroupAddress() {
  const address = guideValue("#knx-group", "").trim();
  // 完整范围与格式校验保留在 Rust Core；这里只阻止空请求。
  if (!address) throw new Error("KNX 三层组地址不能为空");
  return address;
}

function knxFrameInput() {
  const frame = parseHexInput(guideValue("#knx-frame-input", ""));
  if (!frame.length) throw new Error("请先粘贴完整 KNXnet/IP UDP 载荷 HEX");
  return frame;
}

function knxConnectionId() {
  const id = guideValue("#knx-connection-id", "").trim();
  if (!id) throw new Error("KNX connectionId 不能为空");
  return id;
}

function knxHost() {
  const host = guideValue("#knx-host", "").trim();
  if (!host) throw new Error("KNX 网关 IP 不能为空");
  return host;
}

function knxPort() {
  return abUnsigned(document.querySelector("#knx-port")?.value || "3671", 65535, "KNX UDP 端口") || 3671;
}

function knxTimeout() {
  return abUnsigned(document.querySelector("#knx-timeout")?.value || "1500", 5000, "KNX 超时") || 1500;
}

async function knxLive(command, payload, successTitle, successDetail) {
  try {
    const result = await callBackend(command, payload);
    showKnxResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    setNotice("success", successTitle, successDetail);
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

async function knxConnectLive() {
  try {
    await knxLive(
      "open_knx_connection",
      {
        connectionId: knxConnectionId(),
        host: knxHost(),
        port: knxPort(),
        timeoutMs: knxTimeout(),
      },
      "KNX UDP 网关已连接",
      "Connect Response 已核对 Channel、数据端点和个体地址；只读隧道",
    );
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", "KNX 连接失败", error.message || String(error));
  }
}

async function knxGroupReadLive() {
  try {
    await knxLive(
      "knx_group_read",
      {
        connectionId: knxConnectionId(),
        address: knxGroupAddress(),
        timeoutMs: knxTimeout(),
      },
      "KNX GroupValueRead 已完成",
      "已核对网关 ACK、响应 Channel/Sequence/组地址并回送 ACK",
    );
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", "KNX GroupValueRead 失败", error.message || String(error));
  }
}

async function knxDisconnectLive() {
  try {
    await knxLive(
      "knx_disconnect",
      {
        connectionId: knxConnectionId(),
        timeoutMs: knxTimeout(),
      },
      "KNX 隧道已断开",
      "Disconnect Response 已核对 Channel 和 status",
    );
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", "KNX 断开失败", error.message || String(error));
  }
}

async function knxConnectionState() {
  try {
    await knxLive(
      "knx_connection_state",
      {
        connectionId: knxConnectionId(),
        timeoutMs: knxTimeout(),
      },
      "KNX Connection State 正常",
      "网关返回 status=00；连续超时或非零状态会释放本地会话，可重新连接",
    );
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", "KNX Connection State 失败", error.message || String(error));
  }
}

function knxKeepaliveInterval() {
  return abUnsigned(
    document.querySelector("#knx-keepalive-interval")?.value || "60000",
    600000,
    "KNX 保活间隔",
  ) || 60000;
}

async function knxKeepalive(command, successTitle, successDetail) {
  try {
    const result = await callBackend(command, {
      connectionId: knxConnectionId(),
      intervalMs: knxKeepaliveInterval(),
    });
    showKnxResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    setNotice("success", successTitle, successDetail);
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

async function knxRun(command, payload, successTitle, successDetail, putFrame = true) {
  try {
    const result = await callBackend(command, payload);
    showKnxResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || `${successTitle}失败`);
    if (putFrame) {
      const frame = result?.frame || result?.suggestedAck;
      if (Array.isArray(frame)) {
        const input = document.querySelector("#knx-frame-input");
        if (input) input.value = result.frameHex || result.suggestedAckHex || abFrameText(frame);
      }
    }
    setNotice("success", successTitle, successDetail);
  } catch (error) {
    showKnxResult({ error: error.message || String(error) });
    setNotice("error", `${successTitle}失败`, error.message || String(error));
  }
}

function initKnxUi() {
  const q = (selector, fn) => document.querySelector(selector)?.addEventListener("click", fn);
  q("#knx-build-connect", () => {
    void knxRun(
      "knx_build_connect_request",
      {
        localIp: guideValue("#knx-local-ip", "127.0.0.1"),
        localPort: abUnsigned(document.querySelector("#knx-local-port")?.value || "50000", 65535, "KNX 本机 UDP 端口"),
      },
      "KNX Connect Request 已生成",
      "06 10 公共头、两段 HPAI 和 Tunneling CRI 已由 Rust Core 校验；不发送 UDP",
    );
  });
  q("#knx-parse-connect-response", () => {
    try {
      void knxRun(
        "knx_parse_connect_response",
        { frame: knxFrameInput() },
        "KNX Connect Response 已解析",
        "通道、状态、数据端点和个体地址已验证；不代表隧道已连接",
        false,
      );
    } catch (error) { showKnxResult({ error: error.message || String(error) }); setNotice("error", "Connect Response 解析失败", error.message || String(error)); }
  });
  q("#knx-build-read", () => {
    try {
      const address = knxGroupAddress();
      void knxRun(
        "knx_build_group_read_request",
        {
          channelId: abUnsigned(document.querySelector("#knx-channel")?.value || "1", 255, "KNX Channel"),
          sequence: abUnsigned(document.querySelector("#knx-sequence")?.value || "0", 255, "KNX Sequence"),
          address,
        },
        "KNX GroupValueRead 已生成",
        "L_Data.req、三层组地址、APCI 和 APDU 长度已验证；只生成帧，不发送 UDP",
      );
    } catch (error) { showKnxResult({ error: error.message || String(error) }); setNotice("error", "GroupValueRead 构帧失败", error.message || String(error)); }
  });
  q("#knx-parse-request", () => {
    try {
      void knxRun(
        "knx_parse_tunneling_request",
        { frame: knxFrameInput() },
        "KNX Tunneling Request 已解析",
        "公共头、连接头、cEMI、组地址和 APCI 已验证；未发送 UDP",
        false,
      );
    } catch (error) { showKnxResult({ error: error.message || String(error) }); setNotice("error", "Tunneling Request 解析失败", error.message || String(error)); }
  });
  q("#knx-parse-response", () => {
    try {
      void knxRun(
        "knx_parse_group_value_response",
        { frame: knxFrameInput() },
        "KNX GroupValueResponse 已解析",
        "L_Data.ind、APCI Response、payload 和建议 ACK 已验证；未发送 UDP",
        false,
      );
    } catch (error) { showKnxResult({ error: error.message || String(error) }); setNotice("error", "GroupValueResponse 解析失败", error.message || String(error)); }
  });
  q("#knx-parse-ack", () => {
    try {
      void knxRun(
        "knx_parse_tunneling_ack",
        { frame: knxFrameInput() },
        "KNX Tunneling ACK 已解析",
        "通道、序号和状态已验证；未发送 UDP",
        false,
      );
    } catch (error) { showKnxResult({ error: error.message || String(error) }); setNotice("error", "Tunneling ACK 解析失败", error.message || String(error)); }
  });
  q("#knx-build-ack", () => {
    void knxRun(
      "knx_build_tunneling_ack",
      {
        channelId: abUnsigned(document.querySelector("#knx-channel")?.value || "1", 255, "KNX Channel"),
        sequence: abUnsigned(document.querySelector("#knx-sequence")?.value || "0", 255, "KNX Sequence"),
        status: 0,
      },
      "KNX Tunneling ACK 已生成",
      "按当前 Channel/Sequence 生成 status=00 ACK；只生成帧，不发送 UDP",
    );
  });
  q("#knx-connect", () => void knxConnectLive());
  q("#knx-state", () => void knxConnectionState());
  q("#knx-start-keepalive", () => {
    void knxKeepalive(
      "knx_start_keepalive",
      "KNX 周期保活已启动",
      "后台按间隔发送 Connection State；失败后释放本地会话，需显式重连",
    );
  });
  q("#knx-stop-keepalive", () => {
    void knxKeepalive(
      "knx_stop_keepalive",
      "KNX 周期保活已停止",
      "只停止后台保活，不主动断开当前隧道",
    );
  });
  q("#knx-keepalive-status", () => {
    void knxKeepalive(
      "knx_keepalive_status",
      "KNX 保活状态已查询",
      "会话不存在时显示未启用；恢复连接必须显式重新 Connect",
    );
  });
  q("#knx-live-read", () => void knxGroupReadLive());
  q("#knx-disconnect-live", () => void knxDisconnectLive());
  q("#knx-clear", () => {
    const input = document.querySelector("#knx-frame-input");
    const output = document.querySelector("#knx-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成 Connect/GroupValueRead，或粘贴 KNXnet/IP UDP 载荷解析；本页不提供 UDP 发送入口。";
  });
}

// === Keyence KV Host Link ASCII（TCP 只读会话 + 编解码）===

function showKeyenceResult(result) {
  const output = document.querySelector("#keyence-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, null, 2);
}

function keyenceAddress() {
  const value = document.querySelector("#keyence-address")?.value?.trim();
  if (!value) throw new Error("Keyence 地址不能为空");
  return value;
}

function keyenceCount() {
  return abUnsigned(document.querySelector("#keyence-count")?.value || "1", 256, "数量");
}

function keyenceBuild(command, payload) {
  return callBackend(command, payload).then((result) => {
    showKeyenceResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#keyence-response-input");
      if (input) input.value = result.text || new TextDecoder().decode(new Uint8Array(result.frame));
    }
    setNotice("success", "Keyence Host Link 报文已生成", `${command} · 软件编解码，不代表 KV PLC 实机响应`);
  }).catch((error) => {
    showKeyenceResult({ error: error.message || String(error) });
    setNotice("error", "Keyence 编解码失败", error.message || String(error));
  });
}

function keyenceResponse(command, payload) {
  return callBackend(command, payload).then((result) => {
    showKeyenceResult(result);
    setNotice("success", "Keyence 响应已解析", `${command} · 仅验证软件边界`);
  }).catch((error) => {
    showKeyenceResult({ error: error.message || String(error) });
    setNotice("error", "Keyence 响应解析失败", error.message || String(error));
  });
}

function keyenceStationPayload() {
  const station = abUnsigned(document.querySelector("#keyence-station")?.value || "0", 31, "站号");
  return { station, useStation: Boolean(document.querySelector("#keyence-use-station")?.checked) };
}

function keyenceHost() {
  const value = guideValue("#keyence-host", "127.0.0.1").trim();
  if (!value) throw new Error("Keyence TCP 主机不能为空");
  return value;
}

function keyencePort() {
  return abUnsigned(document.querySelector("#keyence-port")?.value || "8501", 65535, "Keyence TCP 端口") || 8501;
}

function keyenceSessionId() {
  return "keyence-live";
}

function initKeyenceUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#keyence-open-connection", async () => {
    try {
      const station = keyenceStationPayload();
      const result = await callBackend("open_keyence_connection", {
        connectionId: keyenceSessionId(),
        host: keyenceHost(),
        port: keyencePort(),
        ...station,
      });
      showKeyenceResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "Keyence TCP 握手失败");
      setNotice("success", "Keyence TCP 只读会话已建立", `${keyenceHost()}:${keyencePort()} · ${station.useStation ? `站号 ${station.station}` : "无站号"}`);
    } catch (error) {
      showKeyenceResult({ error: error.message || String(error) });
      setNotice("error", "Keyence TCP 连接失败", error.message || String(error));
    }
  });
  q("#keyence-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: keyenceSessionId() });
      showKeyenceResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "Keyence TCP 断开失败");
      setNotice("success", "Keyence TCP 会话已断开", "");
    } catch (error) {
      showKeyenceResult({ error: error.message || String(error) });
      setNotice("error", "Keyence TCP 断开失败", error.message || String(error));
    }
  });
  const keyenceLiveRead = (command, label) => async () => {
    try {
      const result = await callBackend(command, {
        connectionId: keyenceSessionId(),
        address: keyenceAddress(),
        count: keyenceCount(),
      });
      showKeyenceResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || `${label}失败`);
      setNotice("success", label, `${keyenceAddress()} · ${result.result?.dataAscii || result.dataAscii || ""}`);
    } catch (error) {
      showKeyenceResult({ error: error.message || String(error) });
      setNotice("error", label, error.message || String(error));
    }
  };
  q("#keyence-live-read-words", keyenceLiveRead("keyence_read_words", "Keyence TCP 只读字完成"));
  q("#keyence-live-read-bits", keyenceLiveRead("keyence_read_bits", "Keyence TCP 只读位完成"));
  q("#keyence-build-connect", () => {
    try { keyenceBuild("keyence_build_connect", keyenceStationPayload()); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-build-read-words", () => {
    try { keyenceBuild("keyence_build_read_words", { address: keyenceAddress(), count: keyenceCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-build-read-bits", () => {
    try { keyenceBuild("keyence_build_read_bits", { address: keyenceAddress(), count: keyenceCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-build-write-words", () => {
    try {
      const raw = document.querySelector("#keyence-write-values")?.value?.trim() || "";
      const values = raw.split(/[\s,]+/).filter(Boolean).map((value) => abUnsigned(value, 65535, "字写入值"));
      if (!values.length) throw new Error("字写入值不能为空");
      keyenceBuild("keyence_build_write_words", { address: keyenceAddress(), values });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-build-write-bit", () => {
    try { keyenceBuild("keyence_build_write_bit", { address: keyenceAddress(), value: Boolean(document.querySelector("#keyence-bit-value")?.checked) }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-parse-connect", () => keyenceResponse("keyence_parse_connect", { response: document.querySelector("#keyence-response-input")?.value || "" }));
  q("#keyence-parse-words", () => {
    try { keyenceResponse("keyence_parse_words", { response: document.querySelector("#keyence-response-input")?.value || "", expectedCount: keyenceCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-parse-bits", () => {
    try { keyenceResponse("keyence_parse_bits", { response: document.querySelector("#keyence-response-input")?.value || "", expectedCount: keyenceCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#keyence-parse-write", () => keyenceResponse("keyence_parse_write", { response: document.querySelector("#keyence-response-input")?.value || "" }));
  q("#keyence-clear", () => {
    const input = document.querySelector("#keyence-response-input");
    const output = document.querySelector("#keyence-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成或粘贴 ASCII 报文。";
  });
}

// === LS Electric XGT FEnet（首轮离线编解码）===

function showLsXgtResult(result) {
  const output = document.querySelector("#xgt-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "data"].includes(key) && Array.isArray(value)) return abFrameText(value);
    return value;
  }, 2);
}

function xgtUnsigned(selector, max, label, fallback) {
  return abUnsigned(document.querySelector(selector)?.value || String(fallback), max, label);
}

function xgtContextPayload() {
  return {
    invokeId: xgtUnsigned("#xgt-invoke", 65535, "InvokeId", 1),
    cpu: xgtUnsigned("#xgt-cpu", 255, "CPU", 160),
    baseNo: xgtUnsigned("#xgt-base", 15, "Base", 0),
    slotNo: xgtUnsigned("#xgt-slot", 15, "Slot", 3),
    companyId: guideValue("#xgt-company", "LSIS-XGT"),
  };
}

function xgtVariable() {
  const value = guideValue("#xgt-variable", "");
  if (!value) throw new Error("XGT 变量不能为空");
  return value;
}

function xgtDataType() {
  return xgtUnsigned("#xgt-data-type", 4, "单变量类型", 2);
}

function xgtByteCount() {
  return xgtUnsigned("#xgt-byte-count", 65535, "连续字节数", 4);
}

function xgtHost() {
  const value = guideValue("#xgt-host", "127.0.0.1").trim();
  if (!value) throw new Error("XGT TCP 主机不能为空");
  return value;
}

function xgtPort() {
  return abUnsigned(document.querySelector("#xgt-port")?.value || "2004", 65535, "XGT TCP 端口") || 2004;
}

function xgtSessionId() {
  return "xgt-live";
}

function xgtBuild(command, payload) {
  return callBackend(command, payload).then((result) => {
    showLsXgtResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#xgt-frame-input");
      if (input) input.value = abFrameText(result.frame);
    }
    setNotice("success", "XGT 报文已生成", `${command} · 软件编解码，不代表 LS Electric 实机响应`);
  }).catch((error) => {
    showLsXgtResult({ error: error.message || String(error) });
    setNotice("error", "XGT 编解码失败", error.message || String(error));
  });
}

function xgtParse(command, payload) {
  return callBackend(command, payload).then((result) => {
    showLsXgtResult(result);
    setNotice("success", "XGT 报文已解析", `${command} · 仅验证软件边界`);
  }).catch((error) => {
    showLsXgtResult({ error: error.message || String(error) });
    setNotice("error", "XGT 解析失败", error.message || String(error));
  });
}

function initLsXgtUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#xgt-open-connection", async () => {
    try {
      const context = xgtContextPayload();
      const result = await callBackend("open_ls_xgt_connection", {
        connectionId: xgtSessionId(),
        host: xgtHost(),
        port: xgtPort(),
        cpu: context.cpu,
        baseNo: context.baseNo,
        slotNo: context.slotNo,
        companyId: context.companyId,
      });
      showLsXgtResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "XGT TCP 连接失败");
      setNotice("success", "XGT TCP 只读会话已建立", `${xgtHost()}:${xgtPort()} · ${context.companyId}`);
    } catch (error) {
      showLsXgtResult({ error: error.message || String(error) });
      setNotice("error", "XGT TCP 连接失败", error.message || String(error));
    }
  });
  q("#xgt-close-connection", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: xgtSessionId() });
      showLsXgtResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "XGT TCP 断开失败");
      setNotice("success", "XGT TCP 会话已断开", "");
    } catch (error) {
      showLsXgtResult({ error: error.message || String(error) });
      setNotice("error", "XGT TCP 断开失败", error.message || String(error));
    }
  });
  q("#xgt-live-read", async () => {
    try {
      const result = await callBackend("ls_xgt_read", {
        connectionId: xgtSessionId(),
        variableName: xgtVariable(),
        dataType: xgtDataType(),
      });
      showLsXgtResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "XGT TCP 单变量只读失败");
      setNotice("success", "XGT TCP 单变量只读完成", `${xgtVariable()} · ${result.result?.dataHex || result.dataHex || ""}`);
    } catch (error) {
      showLsXgtResult({ error: error.message || String(error) });
      setNotice("error", "XGT TCP 单变量只读失败", error.message || String(error));
    }
  });
  q("#xgt-live-read-continuous", async () => {
    try {
      const result = await callBackend("ls_xgt_read_continuous", {
        connectionId: xgtSessionId(),
        variableName: xgtVariable(),
        byteCount: xgtByteCount(),
      });
      showLsXgtResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "XGT TCP 连续只读失败");
      setNotice("success", "XGT TCP 连续只读完成", `${xgtVariable()} · ${result.result?.dataHex || result.dataHex || ""}`);
    } catch (error) {
      showLsXgtResult({ error: error.message || String(error) });
      setNotice("error", "XGT TCP 连续只读失败", error.message || String(error));
    }
  });
  q("#xgt-build-read", () => {
    try { xgtBuild("ls_xgt_build_read", { ...xgtContextPayload(), variableName: xgtVariable(), dataType: xgtDataType() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#xgt-build-continuous-read", () => {
    try { xgtBuild("ls_xgt_build_continuous_read", { ...xgtContextPayload(), variableName: xgtVariable(), byteCount: xgtByteCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#xgt-build-write", () => {
    try {
      const value = parseHexInput(document.querySelector("#xgt-write-data")?.value || "");
      if (!value.length) throw new Error("XGT 写入 HEX 不能为空");
      xgtBuild("ls_xgt_build_write", { ...xgtContextPayload(), variableName: xgtVariable(), dataType: xgtDataType(), value });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#xgt-build-continuous-write", () => {
    try {
      const value = parseHexInput(document.querySelector("#xgt-write-data")?.value || "");
      if (!value.length) throw new Error("XGT 写入 HEX 不能为空");
      xgtBuild("ls_xgt_build_continuous_write", { ...xgtContextPayload(), variableName: xgtVariable(), value });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#xgt-parse-address", () => xgtParse("ls_xgt_parse_address", { address: xgtVariable() }));
  q("#xgt-parse-response", () => {
    try {
      const frame = parseHexInput(document.querySelector("#xgt-frame-input")?.value || "");
      if (!frame.length) throw new Error("请先粘贴或生成 XGT HEX 报文");
      xgtParse("ls_xgt_parse_response", { frame, expectedInvokeId: xgtUnsigned("#xgt-invoke", 65535, "InvokeId", 1) });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#xgt-clear", () => {
    const input = document.querySelector("#xgt-frame-input");
    const output = document.querySelector("#xgt-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成或粘贴 XGT 报文。";
  });
}

// === Panasonic MEWTOCOL-COM（首轮离线编解码）===

// === Delta DVP/AS Modbus 地址 profile ===

function showDeltaResult(result) {
  const output = document.querySelector("#delta-output");
  if (output) output.textContent = JSON.stringify(result, null, 2);
}

function deltaSeriesValue() {
  return guideValue("#delta-series", "dvp-modbus") === "as-modbus" ? "as" : "dvp";
}

function deltaAddressValue() {
  const value = guideValue("#delta-address", "");
  if (!value) throw new Error("Delta 软元件地址不能为空");
  return value;
}

function deltaQuantityValue() {
  return abUnsigned(document.querySelector("#delta-quantity")?.value || "1", 2000, "Delta 连续数量") || 1;
}

function deltaUnitValue() {
  return abUnsigned(document.querySelector("#delta-unit-id")?.value || "1", 247, "Modbus 站号");
}

function deltaReadPayload() {
  return {
    series: deltaSeriesValue(),
    address: deltaAddressValue(),
    quantity: deltaQuantityValue(),
    unitId: deltaUnitValue(),
    transport: guideValue("#delta-transport", "rtu").toLowerCase(),
    timeoutMs: abUnsigned(document.querySelector("#command-timeout")?.value || "1000", 600000, "Modbus 超时") || 1000,
    model: guideValue("#delta-model", ""),
    firmware: guideValue("#delta-firmware", ""),
  };
}

async function deltaPlanRange() {
  try {
    const payload = deltaReadPayload();
    const result = await callBackend("delta_modbus_plan", payload);
    showDeltaResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || "Delta 分段规划失败");
    setNotice("success", "Delta 分段规划完成", `${result.series} · ${result.segments?.length ?? 0} 段 · 只读规划`);
  } catch (error) {
    showDeltaResult({ error: error.message || String(error) });
    setNotice("error", "Delta 分段规划失败", error.message || String(error));
  }
}

async function deltaLiveRead() {
  try {
    const payload = deltaReadPayload();
    const result = await callBackend("delta_modbus_read", payload);
    showDeltaResult(result);
    if (result?.ok === false) throw new Error(result.error?.message || "Delta Modbus 只读失败");
    setNotice("success", "Delta Modbus 只读完成", `${result.series} · ${result.quantity} 点 · ${result.segments?.length ?? 0} 段 · 不执行写入`);
  } catch (error) {
    showDeltaResult({ error: error.message || String(error) });
    setNotice("error", "Delta Modbus 只读失败", error.message || String(error));
  }
}

function initDeltaUi() {
  const button = document.querySelector("#delta-parse-address");
  button?.addEventListener("click", async () => {
    const series = guideValue("#delta-series", "dvp-modbus");
    const address = guideValue("#delta-address", "");
    if (!address) {
      showDeltaResult({ error: "Delta 软元件地址不能为空" });
      setNotice("error", "Delta 地址无效", "请输入 D100、Y17、X1.2 等地址");
      return;
    }
    try {
      const result = await callBackend("delta_parse_address", {
        series: series === "as-modbus" ? "as" : "dvp",
        address,
      });
      showDeltaResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "Delta 地址解析失败");
      setNotice("success", "Delta 地址解析成功", `${result.series} · ${result.area} · FC${result.readFunction}`);
    } catch (error) {
      showDeltaResult({ error: error.message || String(error) });
      setNotice("error", "Delta 地址解析失败", error.message || String(error));
    }
  });
  document.querySelector("#delta-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#delta-output");
      if (output) output.textContent = "先选择系列并解析软元件地址。";
  });
  document.querySelector("#delta-plan-range")?.addEventListener("click", () => { void deltaPlanRange(); });
  document.querySelector("#delta-live-read")?.addEventListener("click", () => { void deltaLiveRead(); });
}

// === Inovance H3U/H5U Modbus 地址 profile ===

function showInovanceResult(result) {
  const output = document.querySelector("#inovance-output");
  if (output) output.textContent = JSON.stringify(result, null, 2);
}

function initInovanceUi() {
  document.querySelector("#inovance-parse-address")?.addEventListener("click", async () => {
    const series = guideValue("#inovance-series", "h3u-modbus");
    const address = guideValue("#inovance-address", "");
    const kind = guideValue("#inovance-kind", "auto");
    if (!address) {
      showInovanceResult({ error: "汇川软元件地址不能为空" });
      setNotice("error", "汇川地址无效", "请输入 D100、M0、X10、Y17 或 C200");
      return;
    }
    try {
      const result = await callBackend("inovance_parse_address", {
        series: series === "h5u-modbus" ? "h5u" : "h3u",
        address,
        kind,
      });
      showInovanceResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "汇川地址解析失败");
      setNotice("success", "汇川地址解析成功", `${result.series} · ${result.area} · FC${result.readFunction} · 底层 ${result.underlyingProtocol}`);
    } catch (error) {
      showInovanceResult({ error: error.message || String(error) });
      setNotice("error", "汇川地址解析失败", error.message || String(error));
    }
  });
  document.querySelector("#inovance-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#inovance-output");
    if (output) output.textContent = "先选择 H3U/H5U 并解析软元件地址。";
  });
}

// === Xinje XC/XD Modbus 地址 profile（首轮仅确认 D） ===

function showXinjeResult(result) {
  const output = document.querySelector("#xinje-output");
  if (output) output.textContent = JSON.stringify(result, null, 2);
}

function initXinjeUi() {
  document.querySelector("#xinje-parse-address")?.addEventListener("click", async () => {
    const series = guideValue("#xinje-series", "xc-modbus");
    const address = guideValue("#xinje-address", "");
    const kind = guideValue("#xinje-kind", "auto");
    if (!address) {
      showXinjeResult({ error: "信捷地址不能为空" });
      setNotice("error", "信捷地址无效", "首轮只确认 D100 这类 D 数据寄存器");
      return;
    }
    try {
      const result = await callBackend("xinjie_parse_address", {
        series: series === "xd-modbus" ? "xd" : "xc",
        address,
        kind,
      });
      showXinjeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "信捷地址解析失败");
      setNotice("success", "信捷地址解析成功", `${result.series} · ${result.area} · FC${result.readFunction} · 底层 ${result.underlyingProtocol}`);
    } catch (error) {
      showXinjeResult({ error: error.message || String(error) });
      setNotice("error", "信捷地址解析失败", error.message || String(error));
    }
  });
  document.querySelector("#xinje-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#xinje-output");
    if (output) output.textContent = "先选择 XC/XD 并解析 D 地址。";
  });
}

// === FATEK FBs 原生 ASCII（TCP 只读会话 + 编解码） ===

function showFatekResult(result) {
  const output = document.querySelector("#fatek-output");
  if (output) output.textContent = JSON.stringify(result, (key, value) => {
    if (key === "frame" || key === "data" || key === "response") return Array.isArray(value) ? abFrameText(value) : value;
    return value;
  }, 2);
}

function fatekStationValue() {
  return abUnsigned(document.querySelector("#fatek-station")?.value || "1", 254, "FATEK 站号") || 1;
}

function fatekCountValue() {
  return abUnsigned(document.querySelector("#fatek-count")?.value || "1", 255, "FATEK 数量") || 1;
}

function fatekHost() {
  const value = guideValue("#fatek-host", "127.0.0.1").trim();
  if (!value) throw new Error("FATEK TCP 主机不能为空");
  return value;
}

function fatekPort() {
  return abUnsigned(document.querySelector("#fatek-port")?.value || "5000", 65535, "FATEK TCP 端口") || 5000;
}

function fatekSessionId() {
  return "fatek-live";
}

async function initFatekUi() {
  const station = () => fatekStationValue();
  const address = () => guideValue("#fatek-address", "");
  const kind = () => guideValue("#fatek-kind", "auto");
  document.querySelector("#fatek-open-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("open_fatek_connection", {
        connectionId: fatekSessionId(),
        host: fatekHost(),
        port: fatekPort(),
        station: station(),
      });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK TCP 连接失败");
      setNotice("success", "FATEK TCP 只读会话已建立", `${fatekHost()}:${fatekPort()} · 站号 ${station()}`);
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK TCP 连接失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-close-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: fatekSessionId() });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK TCP 断开失败");
      setNotice("success", "FATEK TCP 会话已断开", "");
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK TCP 断开失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-live-read")?.addEventListener("click", async () => {
    try {
      const parsed = await callBackend("fatek_parse_address", { address: address(), kind: kind() });
      if (parsed?.ok === false) throw new Error(parsed.error?.message || "FATEK 地址解析失败");
      const isDiscrete = !!(parsed.result?.isDiscrete ?? parsed.isDiscrete);
      const command = isDiscrete ? "fatek_read_discrete" : "fatek_read_words";
      const result = await callBackend(command, {
        connectionId: fatekSessionId(),
        address: address(),
        count: fatekCountValue(),
      });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK TCP 只读失败");
      setNotice("success", "FATEK TCP 只读完成", `${address()} · ${result.result?.dataAscii || result.dataAscii || ""}`);
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK TCP 只读失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-parse-address")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("fatek_parse_address", { address: address(), kind: kind() });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK 地址解析失败");
      setNotice("success", "FATEK 地址解析成功", `${result.dataCode}${result.number} · ${result.kind}`);
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK 地址解析失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-build-read")?.addEventListener("click", async () => {
    try {
      const parsed = await callBackend("fatek_parse_address", { address: address(), kind: kind() });
      if (parsed?.ok === false) throw new Error(parsed.error?.message || "FATEK 地址解析失败");
      const command = parsed.result?.isDiscrete ? "fatek_build_read_discrete" : "fatek_build_read_words";
      const result = await callBackend(command, { station: station(), address: address(), count: fatekCountValue() });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK 读帧生成失败");
      setNotice("success", "FATEK 读帧已生成", result.frameHex || result.result?.frameHex || "");
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK 读帧生成失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-pack")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("fatek_pack_command", { station: station(), command: guideValue("#fatek-command", "40") });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK 命令封装失败");
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK 命令封装失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-parse-response")?.addEventListener("click", async () => {
    try {
      const response = parseHexInput(guideValue("#fatek-response-hex", ""));
      const result = await callBackend("fatek_parse_response", { station: station(), command: guideValue("#fatek-command", "46").slice(0, 2), response });
      showFatekResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "FATEK 响应解析失败");
      setNotice("success", "FATEK 响应解析成功", `${result.command} · status ${result.status}`);
    } catch (error) {
      showFatekResult({ error: error.message || String(error) });
      setNotice("error", "FATEK 响应解析失败", error.message || String(error));
    }
  });
  document.querySelector("#fatek-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#fatek-output");
    if (output) output.textContent = "先解析地址或生成 FATEK ASCII 报文。";
  });
}

// === Fuji MICREX-SX SPH Loader Command（TCP 只读会话 + 编解码） ===

function showFujiResult(result) {
  const output = document.querySelector("#fuji-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "data", "response"].includes(key) && Array.isArray(value)) return abFrameText(value);
    return value;
  }, 2);
}

function fujiConnectionId() {
  const text = guideValue("#fuji-connection-id", "FE").replace(/^0x/i, "");
  const value = Number.parseInt(text, 16);
  if (!Number.isInteger(value) || value < 0 || value > 0xFF) throw new Error("SPH 连接 ID 必须是 00..FF 十六进制");
  return value;
}

function fujiWordCount() {
  return abUnsigned(document.querySelector("#fuji-words")?.value || "1", 230, "SPH 字数量") || 1;
}

function fujiHost() {
  return guideValue("#fuji-host", "127.0.0.1");
}

function fujiPort() {
  return abUnsigned(document.querySelector("#fuji-port")?.value || "18245", 65535, "SPH TCP 端口") || 18245;
}

function fujiSessionId() {
  return "fuji-sph-live";
}

async function initFujiUi() {
  const address = () => guideValue("#fuji-address", "");
  document.querySelector("#fuji-open-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("open_fuji_sph_connection", {
        connectionId: fujiSessionId(),
        host: fujiHost(),
        port: fujiPort(),
        connectionIdByte: fujiConnectionId(),
      });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH TCP 连接失败");
      setNotice("success", "SPH TCP 只读会话已建立", `${fujiHost()}:${fujiPort()} · connection ${fujiConnectionId().toString(16).toUpperCase()}`);
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH TCP 连接失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-close-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: fujiSessionId() });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH TCP 断开失败");
      setNotice("success", "SPH TCP 会话已断开", "");
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH TCP 断开失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-live-read")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("fuji_sph_read", {
        connectionId: fujiSessionId(),
        address: address(),
        words: fujiWordCount(),
      });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH 只读失败");
      setNotice("success", "SPH 只读完成", `${address()} · ${result.result?.dataHex || result.dataHex || ""}`);
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH 只读失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-parse-address")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("fuji_sph_parse_address", { address: address() });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH 地址解析失败");
      setNotice("success", "SPH 地址解析成功", `${result.result?.canonical || result.canonical} · type ${result.result?.typeCodeHex || result.typeCodeHex}`);
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH 地址解析失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-build-read")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("fuji_sph_build_read", { connectionId: fujiConnectionId(), address: address(), words: fujiWordCount() });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH 读帧生成失败");
      setNotice("success", "SPH 00H 读帧已生成", result.result?.frameHex || result.frameHex || "");
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH 读帧生成失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-build-write")?.addEventListener("click", async () => {
    try {
      const data = parseHexInput(guideValue("#fuji-write-data", ""));
      const result = await callBackend("fuji_sph_build_write", { connectionId: fujiConnectionId(), address: address(), data });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH 写帧生成失败");
      setNotice("success", "SPH 01H 写帧已生成", result.result?.frameHex || result.frameHex || "（仅离线）");
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH 写帧生成失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-parse-response")?.addEventListener("click", async () => {
    try {
      const response = parseHexInput(guideValue("#fuji-response-hex", ""));
      const command = abUnsigned(document.querySelector("#fuji-command")?.value || "0", 1, "SPH 命令");
      const expectedDataBytes = abUnsigned(document.querySelector("#fuji-expected-bytes")?.value || "0", 65535, "SPH 期望数据字节") || 0;
      const result = await callBackend("fuji_sph_parse_response", { connectionId: fujiConnectionId(), command, expectedDataBytes, response });
      showFujiResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "SPH 响应解析失败");
      setNotice("success", "SPH 响应解析成功", `${result.result?.wordAddress ?? result.wordAddress} · ${result.result?.dataHex ?? result.dataHex ?? ""}`);
    } catch (error) {
      showFujiResult({ error: error.message || String(error) });
      setNotice("error", "SPH 响应解析失败", error.message || String(error));
    }
  });
  document.querySelector("#fuji-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#fuji-output");
    if (output) output.textContent = "先解析 SPH 地址或生成 Loader Command 报文。";
  });
}

function showGeResult(result) {
  const output = document.querySelector("#ge-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "data", "response"].includes(key) && Array.isArray(value)) return abFrameText(value);
    return value;
  }, 2);
}

function geTransactionId() {
  return abUnsigned(document.querySelector("#ge-transaction-id")?.value || "1", 65535, "GE SRTP 事务号") || 1;
}

function geElementCount() {
  return abUnsigned(document.querySelector("#ge-element-count")?.value || "1", 65535, "GE SRTP 元素数量") || 1;
}

function geBitAccess() {
  return !!document.querySelector("#ge-bit-access")?.checked;
}

function geHost() {
  const value = guideValue("#ge-host", "127.0.0.1").trim();
  if (!value) throw new Error("GE SRTP TCP 主机不能为空");
  return value;
}

function gePort() {
  return abUnsigned(document.querySelector("#ge-port")?.value || "18245", 65535, "GE SRTP TCP 端口") || 18245;
}

function geConnectionId() {
  const value = guideValue("#ge-connection-id", "ge-readonly").trim();
  if (!value) throw new Error("GE SRTP 连接 ID 不能为空");
  return value;
}

async function initGeUi() {
  const address = () => guideValue("#ge-address", "");
  const run = async (command, payload, successTitle, successDetail = "") => {
    try {
      const result = await callBackend(command, payload);
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || `${command} 失败`);
      setNotice("success", successTitle, successDetail || result.result?.frameHex || result.frameHex || "");
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", `${successTitle}失败`, error.message || String(error));
    }
  };
  document.querySelector("#ge-open-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("open_ge_srtp_connection", {
        connectionId: geConnectionId(),
        host: geHost(),
        port: gePort(),
      });
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "GE SRTP TCP 连接失败");
      setNotice("success", "GE SRTP TCP 只读连接已建立", `${geHost()}:${gePort()} · 已完成 56B 会话初始化 · L2 仍待实机`);
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP TCP 连接失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-close-connection")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("close_connection", { connectionId: geConnectionId() });
      showGeResult(result);
      setNotice("success", "GE SRTP 连接已断开", geConnectionId());
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP 断开失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-parse-address")?.addEventListener("click", () => run(
    "ge_srtp_parse_address",
    { address: address() },
    "GE SRTP 地址解析成功",
  ));
  document.querySelector("#ge-build-handshake")?.addEventListener("click", () => run(
    "ge_srtp_build_handshake",
    {},
    "GE SRTP 会话初始化帧已生成",
  ));
  document.querySelector("#ge-parse-handshake")?.addEventListener("click", async () => {
    try {
      const response = parseHexInput(guideValue("#ge-handshake-response", ""));
      const result = await callBackend("ge_srtp_parse_handshake", { response });
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "GE SRTP 会话响应解析失败");
      setNotice("success", "GE SRTP 会话响应解析成功", "sessionInitialized=true");
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP 会话响应解析失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-build-read")?.addEventListener("click", () => run(
    "ge_srtp_build_read",
    { transactionId: geTransactionId(), address: address(), elementCount: geElementCount(), bitAccess: geBitAccess() },
    "GE SRTP 读帧已生成",
  ));
  document.querySelector("#ge-live-read")?.addEventListener("click", async () => {
    try {
      const result = await callBackend("ge_srtp_read", {
        connectionId: geConnectionId(),
        address: address(),
        elementCount: geElementCount(),
        bitAccess: geBitAccess(),
      });
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "GE SRTP TCP 读取失败");
      setNotice("success", "GE SRTP TCP 只读成功", `${address()} · ${result?.dataHex || result?.result?.dataHex || ""} · 不执行写入`);
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP TCP 只读失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-build-write")?.addEventListener("click", async () => {
    try {
      const data = parseHexInput(guideValue("#ge-write-data", ""));
      const result = await callBackend("ge_srtp_build_write", {
        transactionId: geTransactionId(),
        address: address(),
        data,
        elementCount: geElementCount(),
        bitAccess: geBitAccess(),
      });
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "GE SRTP 写帧生成失败");
      setNotice("success", "GE SRTP 写帧已生成", result.result?.frameHex || result.frameHex || "（仅离线）");
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP 写帧生成失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-parse-response")?.addEventListener("click", async () => {
    try {
      const response = parseHexInput(guideValue("#ge-response-hex", ""));
      const expectedDataLength = abUnsigned(document.querySelector("#ge-expected-bytes")?.value || "0", 65535, "GE SRTP 期望数据字节") || 0;
      const result = await callBackend("ge_srtp_parse_response", {
        transactionId: geTransactionId(),
        expectedDataLength,
        response,
      });
      showGeResult(result);
      if (result?.ok === false) throw new Error(result.error?.message || "GE SRTP 响应解析失败");
      setNotice("success", "GE SRTP 响应解析成功", result.result?.dataHex || result.dataHex || "");
    } catch (error) {
      showGeResult({ error: error.message || String(error) });
      setNotice("error", "GE SRTP 响应解析失败", error.message || String(error));
    }
  });
  document.querySelector("#ge-clear")?.addEventListener("click", () => {
    const output = document.querySelector("#ge-output");
    if (output) output.textContent = "先解析 GE 地址或生成 SRTP 会话/读写报文。";
  });
}

function showPanasonicResult(result) {
  const output = document.querySelector("#panasonic-output");
  if (!output) return;
  output.textContent = JSON.stringify(result, (key, value) => {
    if (["frame", "data"].includes(key) && Array.isArray(value)) return abFrameText(value);
    return value;
  }, 2);
}

function panasonicStation() {
  return abUnsigned(document.querySelector("#panasonic-station")?.value || "1", 32, "MEWTOCOL 站号");
}

function panasonicWordCount() {
  return abUnsigned(document.querySelector("#panasonic-word-count")?.value || "1", 500, "字数量");
}

function panasonicDataAddress() {
  const value = guideValue("#panasonic-data-address", "");
  if (!value) throw new Error("MEWTOCOL 数据地址不能为空");
  return value;
}

function panasonicContactAddress() {
  const value = guideValue("#panasonic-contact-address", "");
  if (!value) throw new Error("MEWTOCOL 触点地址不能为空");
  return value;
}

function panasonicLivePayload() {
  return {
    station: panasonicStation(),
    timeoutMs: abUnsigned(document.querySelector("#panasonic-timeout")?.value || "1500", 600000, "MEWTOCOL 超时"),
    retries: abUnsigned(document.querySelector("#panasonic-retries")?.value || "1", 3, "MEWTOCOL 重试次数"),
    model: guideValue("#panasonic-model", ""),
    serialNumber: guideValue("#panasonic-device-serial", ""),
  };
}

async function panasonicLiveRead(mode = "data") {
  try {
    const payload = panasonicLivePayload();
    if (mode === "contact") payload.address = panasonicContactAddress();
    else {
      payload.address = panasonicDataAddress();
      payload.wordCount = panasonicWordCount();
    }
    const result = await callBackend("panasonic_serial_read", { ...payload, mode });
    if (result?.ok === false) throw new Error(result.error?.message || "MEWTOCOL COM 读取失败");
    showPanasonicResult(result);
    if (Array.isArray(result?.rx)) {
      const response = new TextDecoder().decode(Uint8Array.from(result.rx));
      const input = document.querySelector("#panasonic-response-input");
      if (input) input.value = response;
    }
    setNotice("success", "MEWTOCOL COM 只读成功", `${mode === "contact" ? "RCS" : "RD"} · 共享 COM · attempt ${result?.attempt ?? 1} · 不执行写入`);
  } catch (error) {
    showPanasonicResult({ error: error.message || String(error) });
    setNotice("error", "MEWTOCOL COM 只读失败", error.message || String(error));
  }
}

function panasonicBuild(command, payload) {
  return callBackend(command, payload).then((result) => {
    showPanasonicResult(result);
    if (Array.isArray(result?.frame)) {
      const input = document.querySelector("#panasonic-response-input");
      if (input) input.value = result.text || "";
    }
    setNotice("success", "MEWTOCOL 报文已生成", `${command} · 软件编解码，不代表 Panasonic FP 实机响应`);
  }).catch((error) => {
    showPanasonicResult({ error: error.message || String(error) });
    setNotice("error", "MEWTOCOL 编解码失败", error.message || String(error));
  });
}

function panasonicParse(command, payload) {
  return callBackend(command, payload).then((result) => {
    showPanasonicResult(result);
    setNotice("success", "MEWTOCOL 报文已解析", `${command} · 仅验证软件边界`);
  }).catch((error) => {
    showPanasonicResult({ error: error.message || String(error) });
    setNotice("error", "MEWTOCOL 解析失败", error.message || String(error));
  });
}

function initPanasonicUi() {
  const q = (id, fn) => document.querySelector(id)?.addEventListener("click", fn);
  q("#panasonic-build-read", () => {
    try { panasonicBuild("panasonic_build_read", { station: panasonicStation(), address: panasonicDataAddress(), wordCount: panasonicWordCount() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-build-write", () => {
    try {
      const data = parseHexInput(document.querySelector("#panasonic-write-data")?.value || "");
      if (!data.length) throw new Error("MEWTOCOL 写入 HEX 不能为空");
      panasonicBuild("panasonic_build_write", { station: panasonicStation(), address: panasonicDataAddress(), data });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-build-read-contact", () => {
    try { panasonicBuild("panasonic_build_read_contact", { station: panasonicStation(), address: panasonicContactAddress() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-build-write-contact", () => {
    try { panasonicBuild("panasonic_build_write_contact", { station: panasonicStation(), address: panasonicContactAddress(), value: Boolean(document.querySelector("#panasonic-contact-value")?.checked) }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-parse-address", () => {
    try { panasonicParse("panasonic_parse_data_address", { address: panasonicDataAddress() }); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-parse-response", () => {
    try {
      const response = Array.from(new TextEncoder().encode(document.querySelector("#panasonic-response-input")?.value || ""));
      if (!response.length) throw new Error("请先粘贴或生成 MEWTOCOL ASCII 响应");
      const expectedCommand = guideValue("#panasonic-expected-command", "RD").toUpperCase();
      const expectedHeader = guideValue("#panasonic-expected-header", "%");
      panasonicParse("panasonic_parse_response", { station: panasonicStation(), expectedCommand, expectedHeader, response });
    } catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-live-read", () => {
    try { void panasonicLiveRead("data"); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-live-read-contact", () => {
    try { void panasonicLiveRead("contact"); }
    catch (error) { setNotice("error", "参数无效", error.message); }
  });
  q("#panasonic-clear", () => {
    const input = document.querySelector("#panasonic-response-input");
    const output = document.querySelector("#panasonic-output");
    if (input) input.value = "";
    if (output) output.textContent = "先生成或粘贴 MEWTOCOL 报文。";
  });
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
    renderEmptyTableRow(tbody, 5, `COM 口枚举失败:${error.message || error}`);
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
    renderEmptyTableRow(tbody, 4, `USB 枚举失败:${error.message || error}`);
    if (stateEl) stateEl.textContent = "枚举失败";
    return;
  }
  if (!data.ok) {
    renderEmptyTableRow(tbody, 4, data.message || "USB 枚举失败");
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
  const pingBtn = document.querySelector("#if-ping-run");
  if (pingBtn) pingBtn.addEventListener("click", () => ifRunPing().catch((e) =>
    setNotice("error", "Ping 失败", e.message || String(e))));
  const pingHost = document.querySelector("#if-ping-host");
  if (pingHost) pingHost.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) {
      event.preventDefault();
      ifRunPing().catch((e) => setNotice("error", "Ping 失败", e.message || String(e)));
    }
  });
}

const PING_TARGET_SPLIT = /[\s,;，；]+/;
const MAX_PING_TARGETS = 16;

function splitPingTargets(text) {
  const hosts = [];
  const seen = new Set();
  for (const part of String(text ?? "").split(PING_TARGET_SPLIT)) {
    const host = part.trim();
    if (!host) continue;
    const key = host.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    hosts.push(host);
    if (hosts.length >= MAX_PING_TARGETS) break;
  }
  return hosts;
}

function pingSummaryLine(host, r) {
  if (!r?.ok) return `✗ ${host} 参数无效 · ${r?.error?.message ?? "无法 Ping"}`;
  const times = (r.timesMs ?? []).map((t) => `${t}`).join("/");
  if (r.alive) {
    const rtt = r.avgMs != null ? `${r.avgMs} ms` : "—";
    return `✓ ${host} 通 · 发送 ${r.sent ?? "—"} / 接收 ${r.received ?? "—"} / 丢包 ${r.lossPct ?? "—"}% · 平均 ${rtt}` +
      (times ? ` · ${times} ms` : "");
  }
  return `✗ ${host} 不通 · 丢包 ${r.lossPct ?? "—"}%`;
}

/** 网络连通性 Ping(interfaces 视图):支持一次多个 IP/主机名,逐个显示通断。 */
async function ifRunPing() {
  const hostEl = document.querySelector("#if-ping-host");
  const stateEl = document.querySelector("#if-ping-state");
  const resultEl = document.querySelector("#if-ping-result");
  const runBtn = document.querySelector("#if-ping-run");
  const hosts = splitPingTargets(hostEl?.value);
  if (!hosts.length) {
    setNotice("error", "参数无效", "请输入至少一个 IP 或主机名。多个地址用逗号、空格或换行分开。");
    return;
  }
  const uniqueCount = [...new Set(String(hostEl?.value ?? "").split(PING_TARGET_SPLIT).map((part) => part.trim().toLowerCase()).filter(Boolean))].length;
  if (uniqueCount > MAX_PING_TARGETS) {
    setNotice("info", `最多 ${MAX_PING_TARGETS} 个目标`, `已截取前 ${MAX_PING_TARGETS} 个地址。`);
  }
  const count = Number(document.querySelector("#if-ping-count")?.value) || 4;
  if (stateEl) {
    stateEl.textContent = hosts.length === 1 ? "Ping 中…" : `0/${hosts.length} 完成`;
    stateEl.style.color = "";
  }
  if (resultEl) resultEl.replaceChildren(document.createTextNode(
    hosts.length === 1 ? "Ping 中,请稍候…" : `准备 Ping ${hosts.length} 个目标…`,
  ));
  if (runBtn) runBtn.disabled = true;

  const list = document.createElement("div");
  list.className = "ping-result-list";
  const pending = hosts.map((host) => {
    const line = document.createElement("div");
    line.className = "ping-result-line is-pending";
    line.textContent = `… ${host} 等待中`;
    list.append(line);
    return line;
  });
  if (resultEl) resultEl.replaceChildren(list);

  try {
    let aliveCount = 0;
    let failedCount = 0;
    for (let i = 0; i < hosts.length; i += 1) {
      const host = hosts[i];
      pending[i].textContent = `… ${host} Ping 中`;
      if (stateEl) stateEl.textContent = `${i}/${hosts.length} 完成 · 正在 ${host}`;
      const r = await callBackend("ping_host", { host, count, timeoutMs: 1000 });
      pending[i].classList.remove("is-pending");
      pending[i].textContent = pingSummaryLine(host, r);
      if (r?.ok && r.alive) {
        pending[i].classList.add("is-alive");
        aliveCount += 1;
      } else {
        pending[i].classList.add("is-down");
        failedCount += 1;
      }
      if (hosts.length === 1 && r?.ok && r.raw) {
        const pre = document.createElement("pre");
        pre.className = "ping-result-raw";
        pre.textContent = r.raw;
        list.append(pre);
      }
    }
    if (stateEl) {
      stateEl.textContent = failedCount === 0
        ? (hosts.length === 1 ? `通 · ${aliveCount}/${hosts.length}` : `${aliveCount}/${hosts.length} 通`)
        : `${aliveCount}/${hosts.length} 通`;
      stateEl.style.color = failedCount === 0 ? "var(--ok, #3fb950)" : "var(--danger, #f85149)";
    }
  } finally {
    if (runBtn) runBtn.disabled = false;
  }
}

// === 西门子 S7comm ===

let s7Connected = false;
let s7SlaveRunning = false;
let s7FxWebApi = false; // Web API 模式分流
let s7ActiveVariant = null;
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
    return `<strong>S7-200 SMART —— 先核对 CPU 型号、固件与实际 IP:</strong><br/>
      · STEP 7-Micro/WIN SMART V2.8 对应 CPU V2.8 及更早；V3 软件对应 V3 CPU；端口 102<br/>
      · <strong>V3 的 Put/Get Server 默认关闭</strong>：通讯设置 → 启用 Put/Get Server → 保存并下载到 PLC<br/>
      · 建议同时启用通信写限制，只开放演示保留 V 区；Nexus 首次真机先只读，不写 PLC<br/>
      · rack=0 / slot=0，连接失败自动再试 slot=1；V 语法按 DB1 兼容映射（VW100 = DB1.DBW100）<br/>
      · CR20s/CR30s/CR40s/CR60s 无以太网口，只能走串口；以 CPU 铭牌/订货号为准`
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

function s7SelectedVariant() {
  return document.querySelector("#s7-variant")?.value || "s7comm";
}

function s7ActiveRoute() {
  return resolveSiemensRoute(s7ActiveVariant || s7SelectedVariant());
}

function s7ParseFetchWriteAddress(address) {
  const text = String(address || "").trim().toUpperCase();
  let match = /^DB(\d+)\.DB([BWD])(\d+)$/.exec(text);
  if (match) {
    return {
      area: "DB",
      db: Number(match[1]),
      address: Number(match[3]),
      elementBytes: { B: 1, W: 2, D: 4 }[match[2]],
    };
  }
  match = /^([MIQ])([BWD])?(\d+)$/.exec(text);
  if (match) {
    return {
      area: match[1],
      db: 0,
      address: Number(match[3]),
      elementBytes: { B: 1, W: 2, D: 4 }[match[2] || "B"],
    };
  }
  match = /^([CT])(\d+)$/.exec(text);
  if (match) {
    return { area: match[1], db: 0, address: Number(match[2]), elementBytes: 2 };
  }
  throw new Error("Fetch/Write 仅支持 DB1.DBB/W/Dn、MB/W/Dn、IB/W/Dn、QB/W/Dn、C/Tn；位地址不能直接访问");
}

function s7ParseUssAddress(address) {
  const text = String(address || "").trim().toUpperCase();
  const match = /^P(\d{1,4})(?:\[(\d{1,4})\])?$/.exec(text);
  if (!match) throw new Error("USS 参数地址格式为 P700 或 P2200[1]");
  const param = Number(match[1]);
  const index = match[2] === undefined ? 0 : Number(match[2]);
  if (param > 0x0FFF || index > 0xFFFF) throw new Error("USS 参数号/子索引超出范围");
  return { param, index };
}

function s7ParseWebApiValue(raw) {
  const text = String(raw || "").trim();
  if (!text) throw new Error("Web API 写入值不能为空");
  if (/^json:/i.test(text)) {
    try { return JSON.parse(text.slice(5).trim()); } catch (error) {
      throw new Error("JSON 写入值无效: " + (error.message || String(error)));
    }
  }
  if (/^text:/i.test(text)) return text.slice(5);
  if (/^hex:/i.test(text)) {
    const bytes = text.slice(4).trim().split(/[\s,]+/).filter(Boolean).map((v) => Number.parseInt(v, 16));
    if (!bytes.length || bytes.some((v) => !Number.isInteger(v) || v < 0 || v > 255)) {
      throw new Error("hex 写入值必须是 00-FF 字节列表");
    }
    return bytes;
  }
  if (text.includes(",")) {
    const values = text.split(",").map((v) => Number(v.trim()));
    if (values.some((v) => !Number.isFinite(v))) throw new Error("逗号分隔的 Web API 数组必须是数字");
    return values;
  }
  const numeric = Number(text);
  return Number.isFinite(numeric) ? numeric : text;
}

function s7ApplyModel() {
  const model = s7CurrentModel();
  const selectedVariant = s7SelectedVariant();
  const def = S7_MODEL_DEFAULTS[model] || S7_MODEL_DEFAULTS["1200"];
  const rack = document.querySelector("#s7-rack");
  const slot = document.querySelector("#s7-slot");
  if (rack) rack.value = def.rack;
  if (slot) slot.value = def.slot;
  const hint = document.querySelector("#s7-hint");
  if (hint) {
    hint.textContent = selectedVariant === "uss"
      ? "USS 参数语法:P700 / P2200[1] · 只读参数值，不发送控制字"
      : model === "smart"
      ? "SMART V 区语法:VW100(=DB1.DBW100) / VB100 / V100.3 · 或直接用 DB1.DBW100"
      : "地址语法:DB1.DBW20 / M10.3 / IW0 / T5 / C3";
  }
  const addr = document.querySelector("#s7-address");
  if (addr) addr.placeholder = selectedVariant === "uss" ? "P700 / P2200[1]" : model === "smart" ? "VW100 / VB100 / V100.3 / DB1.DBW100" : "DB1.DBW20 / M10.3 / IW0";
  const serialStationWrap = document.querySelector("#s7-serial-station-wrap");
  if (serialStationWrap) serialStationWrap.classList.toggle("hidden", !["ppi", "ppi-serial", "uss"].includes(selectedVariant));
  const serialStationLabel = document.querySelector("#s7-serial-station-label");
  if (serialStationLabel) serialStationLabel.textContent = selectedVariant === "uss" ? "USS 站号" : "PPI 站号";
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
  if (q("#s7-read")) q("#s7-read").disabled = !s7Connected || !s7ActiveRoute().readCommand;
  if (q("#s7-write")) q("#s7-write").disabled = !s7Connected || !s7ActiveRoute().writeCommand;
  if (q("#s7-variant")) q("#s7-variant").disabled = s7Connected;
  if (q("#s7-model")) q("#s7-model").disabled = s7Connected;
  const route = s7ActiveRoute();
  const cpuControlAvailable = s7Connected && route.kind === "s7comm";
  for (const id of ["#s7-read-status", "#s7-pwd-btn", "#s7-hot-start", "#s7-cold-start", "#s7-stop-cpu"]) {
    if (q(id)) q(id).disabled = !cpuControlAvailable;
  }
  const diagState = document.querySelector("#s7-diag-state");
  if (diagState) diagState.textContent = s7Connected ? "已连接" : "需要连接";
  if (q("#s7-start-slave")) q("#s7-start-slave").disabled = s7SlaveRunning;
  if (q("#s7-stop-slave")) q("#s7-stop-slave").disabled = !s7SlaveRunning;
}

async function s7Connect() {
  if (s7Connected) return;
  const variant = s7SelectedVariant();
  const route = resolveSiemensRoute(variant);
  const host = document.querySelector("#s7-host")?.value?.trim() || "127.0.0.1";
  const portInput = Number(document.querySelector("#s7-port")?.value);
  const port = variant === "fw"
    ? (portInput === 102 || !portInput ? 2000 : portInput)
    : (portInput || 102);
  const rack = Number(document.querySelector("#s7-rack")?.value) || 0;
  const slot = Number(document.querySelector("#s7-slot")?.value) || 0;
  const model = s7CurrentModel();
  const serialStation = Number(document.querySelector("#s7-serial-station")?.value);
  const stationMax = variant === "uss" ? 30 : 126;
  if (["ppi", "ppi-serial", "uss"].includes(variant) && (!Number.isInteger(serialStation) || serialStation < 0 || serialStation > stationMax)) {
    s7SetState("站号无效");
    setNotice("error", variant === "uss" ? "USS 站号无效" : "PPI 站号无效", `请输入 0 到 ${stationMax} 的整数`);
    s7SyncButtons();
    return;
  }
  s7FxWebApi = false;
  s7ActiveVariant = null;

  if (!route.online) {
    s7SetState("未接通");
    setNotice("error", route.label + " 暂不可在线连接", route.reason);
    s7SyncButtons();
    return;
  }

  switch (route.kind) {
    case "ppi-serial": {
      setNotice("info", "检查串口", "PPI 原生 COM 只读将复用主站页串口");
      try {
        const status = await callBackend(route.connectCommand, {});
        if (!status?.isOpen) throw new Error("请先在主站页打开 PPI 使用的 COM 串口(常见 9600 8E1)");
        s7Connected = true; s7ActiveVariant = variant;
        const cfg = status.config || {};
        s7SetState(`PPI COM 已就绪 ${cfg.portName || "串口"}(站 ${serialStation})`, true);
        setNotice("success", "PPI 原生串口已就绪", `${cfg.portName || "COM"} · 软件只读双拍;站 ${serialStation}`);
      } catch (error) {
        s7SetState("串口未就绪");
        setNotice("error", "PPI 串口不可用", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "ppi": {
      setNotice("info", "连接中", `${host}:${port} (PPI,站 ${serialStation})`);
      try {
        await callBackend(route.connectCommand, { connectionId: S7_CONN_ID, host, port, station: serialStation });
        s7Connected = true; s7ActiveVariant = variant;
        s7SetState("PPI 已连接 " + host + ":" + port + `(站 ${serialStation})`, true);
        setNotice("success", "PPI 已连接", `双拍确认;V 区=DB1;站 ${serialStation}`);
      } catch (error) {
        s7SetState("连接失败");
        setNotice("error", "PPI 连接失败", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "fetchwrite": {
      setNotice("info", "连接中", `${host}:${port} (Fetch/Write)`);
      try {
        await callBackend(route.connectCommand, { connectionId: S7_CONN_ID, host, port: port || 2000 });
        s7Connected = true; s7ActiveVariant = variant;
        const portInputEl = document.querySelector("#s7-port");
        if (portInputEl) portInputEl.value = String(port);
        s7SetState("FW 已连接 " + host + ":" + port, true);
        setNotice("success", "Fetch/Write 已连接", "S5 兼容通道(DB/M/I/Q 直读)");
      } catch (error) {
        s7SetState("连接失败");
        setNotice("error", "FW 连接失败", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "webapi": {
      const user = document.querySelector("#s7-webapi-user")?.value?.trim() || "";
      const password = document.querySelector("#s7-webapi-pass")?.value || "";
      if (!user) { setNotice("error", "Web 用户名为空", "CPU 属性 → 防护与安全 → 用户与权限里设置的 Web 账户"); return; }
      setNotice("info", "连接中", `${host}:443 (Web API)`);
      try {
        await callBackend(route.connectCommand, { host, port: 443, user, password });
        s7Connected = true; s7FxWebApi = true; s7ActiveVariant = variant;
        s7SetState(`Web API 已登录 ${host}`, true);
        setNotice("success", "Web API 已连接", "JSON-RPC 符号寻址(可读优化块)");
      } catch (error) {
        s7SetState("Web API 登录失败");
        setNotice("error", "Web API 登录失败", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "uss-serial": {
      setNotice("info", "检查串口", "USS 参数只读将复用主站页串口");
      try {
        const status = await callBackend(route.connectCommand, {});
        if (!status?.isOpen) throw new Error("请先在主站页打开 USS 使用的 COM 串口(常见 9600 8N1)");
        s7Connected = true; s7ActiveVariant = variant;
        const cfg = status.config || {};
        s7SetState(`USS COM 已就绪 ${cfg.portName || "串口"}(站 ${serialStation})`, true);
        setNotice("success", "USS 串口已就绪", `${cfg.portName || "COM"} · 参数只读;站 ${serialStation}`);
      } catch (error) {
        s7SetState("串口未就绪");
        setNotice("error", "USS 串口不可用", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "rk512-serial": {
      setNotice("info", "检查串口", "3964R/RK512 只读将复用主站页串口");
      try {
        const status = await callBackend(route.connectCommand, {});
        if (!status?.isOpen) throw new Error("请先在主站页打开 RK512 使用的 COM 串口，并核对 CP341/441 参数");
        s7Connected = true; s7ActiveVariant = variant;
        const cfg = status.config || {};
        s7SetState(`RK512 COM 已就绪 ${cfg.portName || "串口"}`, true);
        setNotice("success", "RK512 串口已就绪", `${cfg.portName || "COM"} · 3964R 链路只读`);
      } catch (error) {
        s7SetState("串口未就绪");
        setNotice("error", "RK512 串口不可用", error.message || String(error));
      } finally { s7SyncButtons(); }
      return;
    }
    case "s7comm": {
      setNotice("info", "连接中", `${host}:${port} (rack ${rack}/slot ${slot}, ${model.toUpperCase()})`);
      try {
        const def = S7_MODEL_DEFAULTS[model] || {};
        const connType = Number(document.querySelector("#s7-conn-type")?.value) || 1;
        const localTsap = document.querySelector("#s7-custom-localtsap")?.value?.trim() || def.localTsap || null;
        const remoteTsap = document.querySelector("#s7-custom-remotetsap")?.value?.trim() || def.remoteTsap || null;
        const attemptSlots = model === "smart" && !remoteTsap
          ? [...new Set([slot, slot === 0 ? 1 : 0])]
          : [slot];
        const errors = [];
        let r = null;
        let connectedSlot = slot;
        for (const attemptSlot of attemptSlots) {
          try {
            r = await callBackend(route.connectCommand, {
              connectionId: S7_CONN_ID, host, port, rack, slot: attemptSlot,
              connType, localTsap, remoteTsap,
            });
            connectedSlot = attemptSlot;
            break;
          } catch (error) {
            errors.push(`slot ${attemptSlot}: ${error.message || String(error)}`);
          }
        }
        if (!r) throw new Error(errors.join(" | "));
        if (connectedSlot !== slot) {
          const slotInput = document.querySelector("#s7-slot");
          if (slotInput) slotInput.value = connectedSlot;
        }
        s7Connected = true; s7ActiveVariant = variant;
        s7SetState(`已连接 · rack ${rack}/slot ${connectedSlot} · PDU ${r.pduSize}B`, true);
        const fallback = connectedSlot !== slot ? `；slot ${slot} 失败后自动改用 ${connectedSlot}` : "";
        setNotice("success", "S7 已连接", `协商 PDU ${r.pduSize} 字节(单次最多读 ${r.maxReadBytes}B/写 ${r.maxWriteBytes}B)${fallback}`);
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
      return;
    }
    default: {
      s7SetState("未接通");
      setNotice("error", route.label + " 不能走 S7comm", route.reason || "未知西门子变体已 fail-closed");
      s7SyncButtons();
    }
  }
}

async function s7Disconnect() {
  if (!s7Connected) return;
  const route = s7ActiveRoute();
  try {
    if (route.disconnectCommand === "s7web_disconnect") {
      await callBackend(route.disconnectCommand);
    } else if (route.disconnectCommand) {
      await callBackend(route.disconnectCommand, { connectionId: S7_CONN_ID });
    }
  } catch { /* 忽略 */ }
  s7Connected = false;
  s7FxWebApi = false;
  s7ActiveVariant = null;
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
    renderEmptyTableRow(tbody, 5, rcMsg || "无数据");
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

function s7RenderWebApiValue(address, value) {
  const tbody = document.querySelector("#s7-results");
  if (!tbody) return;
  tbody.replaceChildren();
  const rendered = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  const row = document.createElement("tr");
  for (const cell of ["1", address, "Web API ✓", rendered ?? "null", rendered ?? "null"]) {
    const td = document.createElement("td");
    td.textContent = cell;
    row.append(td);
  }
  tbody.append(row);
}

async function s7Read() {
  if (!s7Connected) return;
  const address = document.querySelector("#s7-address")?.value?.trim();
  const count = Number(document.querySelector("#s7-points")?.value) || 1;
  if (!address) { setNotice("error", "地址无效", "请输入 S7 地址(如 DB1.DBW20 / M10.3 / VW100)"); return; }
  const route = s7ActiveRoute();
  if (!route.online || !route.readCommand) {
    setNotice("error", route.label + " 未接入在线读取", route.reason || "请先选择已接通的协议变体");
    return;
  }
  try {
    if (route.kind === "webapi") {
      const value = await callBackend(route.readCommand, { varName: address, mode: "simple" });
      s7RenderWebApiValue(address, value);
      setNotice("success", "Web API 读取成功", address + "（符号变量结果已保留原始 JSON）");
      return;
    }
    if (route.kind === "ppi") {
      const r = await callBackend(route.readCommand, { connectionId: S7_CONN_ID, address, count });
      const item = r.items?.[0];
      if (!item) { setNotice("error", "PPI 读取失败", "无返回项"); return; }
      if (item.returnCode !== 0xFF) {
        s7RenderRows(address, item.data || [], item.returnCode, item.returnCodeMessage);
        setNotice("error", "PPI 返回码 0x" + item.returnCode.toString(16).toUpperCase().padStart(2, "0"), item.returnCodeMessage || "");
        return;
      }
      s7RenderRows(address, item.data || [], 0xFF, "");
      setNotice("success", "PPI 读取成功", address + " × " + count + "（" + (item.data?.length || 0) + " 字节）");
      return;
    }
    if (route.kind === "ppi-serial") {
      const r = await callBackend(route.readCommand, {
        station: Number(document.querySelector("#s7-ppi-station")?.value) || 2,
        master: 0,
        address,
        count,
        timeoutMs: 1500,
      });
      if (r?.ok === false) throw new Error(r.error?.message || "PPI 串口读取失败");
      const item = r.items?.[0];
      if (!item) { setNotice("error", "PPI 串口读取失败", "无返回项"); return; }
      if (item.returnCode !== 0xFF) {
        s7RenderRows(address, item.data || [], item.returnCode, item.returnCodeMessage);
        setNotice("error", "PPI 返回码 0x" + item.returnCode.toString(16).toUpperCase().padStart(2, "0"), item.returnCodeMessage || "");
        return;
      }
      s7RenderRows(address, item.data || [], 0xFF, "");
      const profileWarning = r.serialWarnings?.length ? `；参数提示：${r.serialWarnings.join("；")}` : "";
      setNotice("success", "PPI 原生串口读取成功", `${address} × ${count}（${item.data?.length || 0} 字节；双拍${r.attempts > 1 ? `；第 ${r.attempts} 次成功` : ""}）${profileWarning}`);
      return;
    }
    if (route.kind === "uss-serial") {
      const info = s7ParseUssAddress(address);
      const r = await callBackend(route.readCommand, {
        station: Number(document.querySelector("#s7-serial-station")?.value) || 1,
        param: info.param,
        pzdBytes: 4,
        timeoutMs: 1500,
      });
      if (r?.ok === false) throw new Error(r.error?.message || "USS 串口读取失败");
      const pzd = r.pzd || [];
      s7RenderRows(address, pzd, 0xFF, "");
      setNotice("success", "USS 参数读取成功", `${address} · AK ${r.pkeAkMessage || r.pkeAk || "响应"}（${pzd.length} 字节）`);
      return;
    }
    if (route.kind === "rk512-serial") {
      const r = await callBackend(route.readCommand, { address, count, timeoutMs: 1500 });
      if (r?.ok === false) throw new Error(r.error?.message || "RK512 串口读取失败");
      s7RenderRows(address, r.data || [], 0xFF, "");
      setNotice("success", "RK512 读取成功", `${address} × ${count}（${r.data?.length || 0} 字节；3964R 双向握手）`);
      return;
    }
    if (route.kind === "fetchwrite") {
      const info = s7ParseFetchWriteAddress(address);
      const length = info.elementBytes * count;
      if (!Number.isSafeInteger(length) || length < 1 || length > 0xFFFF) {
        throw new Error("Fetch/Write 单次读取长度必须在 1-65535 字节内");
      }
      const r = await callBackend(route.readCommand, {
        connectionId: S7_CONN_ID,
        area: info.area,
        db: info.db,
        address: info.address,
        length,
      });
      s7RenderRows(address, r.data || [], 0xFF, "");
      setNotice("success", "Fetch/Write 读取成功", address + " × " + count + "（" + (r.data?.length || 0) + " 字节）");
      return;
    }
    if (route.kind !== "s7comm") {
      setNotice("error", route.label + " 读取路径未知", route.reason || "未实现的西门子读取路由，已阻止回退到 S7comm");
      return;
    }
    const r = await callBackend(route.readCommand, { connectionId: S7_CONN_ID, items: [{ address, count }] });
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
  const route = s7ActiveRoute();
  if (!route.online || !route.writeCommand) {
    setNotice("error", route.label + " 未接入在线写入", route.reason || "请先选择已接通的协议变体");
    return;
  }
  if (route.kind === "webapi") {
    let value;
    try {
      value = s7ParseWebApiValue(raw);
    } catch (error) {
      setNotice("error", "Web API 写入值无效", error.message || String(error));
      return;
    }
    if (!confirm("将通过 Web API 写入变量 " + address + "，值为 " + JSON.stringify(value) + "。确认?")) return;
    try {
      await callBackend(route.writeCommand, { varName: address, value, mode: "simple" });
      setNotice("success", "Web API 写入成功", address + " 已提交并获得服务确认");
    } catch (error) {
      setNotice("error", "Web API 写入失败", error.message || String(error));
    }
    return;
  }
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
  if (!confirm("将通过 " + route.label + " 写入 " + values.length + " 字节到 " + address + "，确认?")) return;
  try {
    if (route.kind === "ppi") {
      const count = Number(document.querySelector("#s7-points")?.value) || 1;
      const r = await callBackend(route.writeCommand, { connectionId: S7_CONN_ID, address, count, values });
      const codes = r.returnCodes || [];
      const msgs = r.returnCodeMessages || [];
      if (codes.length && codes[0] !== 0xFF) {
        setNotice("error", "PPI 写入失败 0x" + codes[0].toString(16).toUpperCase().padStart(2, "0"), msgs[0] || "");
        return;
      }
      setNotice("success", "PPI 写入成功", address + " ← " + values.length + " 字节");
      return;
    }
    if (route.kind === "fetchwrite") {
      const info = s7ParseFetchWriteAddress(address);
      if (values.length % info.elementBytes !== 0) {
        setNotice("error", "Fetch/Write 写入长度无效", "当前地址元素宽度为 " + info.elementBytes + " 字节，写入数据必须整除该宽度");
        return;
      }
      const r = await callBackend(route.writeCommand, {
        connectionId: S7_CONN_ID,
        area: info.area,
        db: info.db,
        address: info.address,
        values,
      });
      if (r?.ok === false) {
        setNotice("error", "Fetch/Write 写入失败", "设备未确认写入");
        return;
      }
      setNotice("success", "Fetch/Write 写入成功", address + " ← " + values.length + " 字节");
      return;
    }
    if (route.kind !== "s7comm") {
      setNotice("error", route.label + " 写入路径未知", route.reason || "未实现的西门子写入路由，已阻止回退到 S7comm");
      return;
    }
    const r = await callBackend(route.writeCommand, { connectionId: S7_CONN_ID, items: [{ address, values }] });
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
async function s7Control(action, label) {
  const expectedWord = { hot: "HOT-START", cold: "COLD-START", stop: "STOP" }[action] ?? "CONFIRM";
  const auditBase = {
    protocol: "s7comm",
    transport: "tcp",
    connectionId: S7_CONN_ID,
    functionCode: null,
    address: label,
    quantity: null,
    oldValue: null,
    newValue: null,
  };
  if (!confirmHighRiskControl(`S7 CPU ${label}`, expectedWord)) {
    await recordWriteAudit({ ...auditBase, result: "cancelled", message: "用户取消或输入不一致" });
    setNotice("info", "已取消", "未发送 S7 CPU 控制命令。");
    return;
  }
  try {
    const result = await callBackend("s7_cpu_control", { connectionId: S7_CONN_ID, action });
    const message = `控制结果:${result.message}`;
    const output = document.querySelector("#s7-diag-result");
    if (output) output.textContent = message;
    setNotice("success", "S7 CPU 控制完成", message);
    await recordWriteAudit({ ...auditBase, result: "write-succeeded", message });
  } catch (error) {
    await recordWriteAudit({
      ...auditBase,
      result: "write-failed",
      errorCode: error.code ?? "S7_CPU_CONTROL_FAILED",
      message: error.message ?? String(error),
    });
    setNotice("error", "S7 CPU 控制失败", error.message || String(error));
  }
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
      const isSerial = ["fx-links", "fx-prog", "mc-c24"].includes(variantSel.value);
      document.querySelector("#mc-net-row")?.classList.toggle("hidden", isSerial);
      document.querySelector("#mc-serial-row")?.classList.toggle("hidden", !isSerial);
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
  const expectedWord = { mc_remote_run: "RUN", mc_remote_stop: "STOP", mc_remote_reset: "RESET" }[cmd] ?? "CONFIRM";
  const auditBase = {
    protocol: "melsec",
    transport: document.querySelector("#mc-frame-type")?.value ?? "3e",
    connectionId: MC_CONN_ID,
    functionCode: null,
    address: name,
    quantity: null,
    oldValue: null,
    newValue: null,
  };
  if (!confirmHighRiskControl(`${name}（${warning}）`, expectedWord)) {
    await recordWriteAudit({ ...auditBase, result: "cancelled", message: "用户取消或输入不一致" });
    setNotice("info", "已取消", `未发送 ${name} 命令。`);
    return;
  }
  try {
    const r = await callBackend(cmd, { connectionId: MC_CONN_ID });
    const ok = r.endCode === 0;
    setNotice(ok ? "success" : "error", name, ok ? "已执行" : `错误 ${r.endCode?.toString(16).toUpperCase()}: ${r.endCodeMessage}`);
    await recordWriteAudit({
      ...auditBase,
      result: ok ? "write-succeeded" : "write-failed",
      errorCode: ok ? null : `MC_END_CODE_${r.endCode?.toString(16).toUpperCase()}`,
      message: ok ? "已执行" : r.endCodeMessage,
    });
  } catch (error) {
    setNotice("error", `${name} 失败`, error.message || String(error));
    await recordWriteAudit({
      ...auditBase,
      result: "write-failed",
      errorCode: error.code ?? "MC_REMOTE_CONTROL_FAILED",
      message: error.message ?? String(error),
    });
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
    tab.trendSelection = [...trendSeries.keys()];
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
  if (!tab?.rowsHtml) renderPointRows();
  restoreTrendSelection(tab?.trendSelection ?? []);
  // 刷新计数显示
  const rowCount = elements.registerResults.querySelectorAll("tr:not(.empty-row)").length;
  elements.pointCount.textContent = `点位 ${rowCount}`;
}

function createSessionTabButton(name) {
  const btn = document.createElement("button");
  btn.className = "session-tab-btn";
  btn.dataset.session = name;
  btn.textContent = name === "default" ? "默认" : `${name} ×`;
  btn.title = name === "default" ? "切换到默认会话" : `切换到 ${name}（点击 × 关闭）`;
  btn.addEventListener("click", (e) => {
    if (name !== "default" && e.offsetX > btn.offsetWidth - 20) {
      closeSessionTab(name, btn);
    } else {
      activateSessionTab(name);
    }
  });
  return btn;
}

function addSessionTab() {
  let index = sessionTabs.size;
  while (sessionTabs.has(`会话${index}`)) index += 1;
  const name = `会话${index}`;
  // 先保存当前标签数据
  saveCurrentSessionData();
  const btn = createSessionTabButton(name);
  elements.sessionTabsBar?.append(btn);
  // 新标签:空表格 + 空点表
  sessionTabs.set(name, { rowsHtml: "", pointTable: [], btn });
  activateSessionTab(name);
  setNotice("info", "新标签", `已创建 ${name}（独立数据区）`);
}

function closeSessionTab(name, btn) {
  if (name === "default" || sessionTabs.size === 1) {
    setNotice("error", "不允许", name === "default" ? "默认标签不可关闭。" : "项目至少需要保留一个会话。");
    return;
  }
  const switchingAway = activeSession === name;
  sessionTabs.delete(name);
  btn.remove();
  if (switchingAway) activateSessionTab(sessionTabs.has("default") ? "default" : sessionTabs.keys().next().value);
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

let currentProjectName = "未命名项目";
let projectHasPath = false;

function collectSimulatorWorkspace() {
  return {
    modbus: {
      mode: elements.slaveMode?.value === "serial" ? "serial" : "tcp",
      port: String(Number(elements.slavePort?.value) || 502),
      allowedStations: String(elements.slaveStations?.value ?? "").trim().slice(0, 128),
    },
    melsec: {
      port: String(Number(document.querySelector("#mc-port")?.value) || 5000),
    },
    s7: {
      port: String(Number(document.querySelector("#s7-port")?.value) || 102),
    },
  };
}

function restoreSimulatorWorkspace(simulators = {}) {
  const modbus = simulators.modbus ?? {};
  const melsec = simulators.melsec ?? {};
  const s7 = simulators.s7 ?? {};
  if (elements.slaveMode && ["tcp", "serial"].includes(modbus.mode)) {
    elements.slaveMode.value = modbus.mode;
  }
  if (elements.slavePort && Number(modbus.port) >= 1 && Number(modbus.port) <= 65_535) {
    elements.slavePort.value = String(Number(modbus.port));
  }
  if (elements.slaveStations && typeof modbus.allowedStations === "string") {
    elements.slaveStations.value = modbus.allowedStations;
  }
  const mcPort = document.querySelector("#mc-port");
  if (mcPort && Number(melsec.port) >= 1 && Number(melsec.port) <= 65_535) {
    mcPort.value = String(Number(melsec.port));
  }
  const s7Port = document.querySelector("#s7-port");
  if (s7Port && Number(s7.port) >= 1 && Number(s7.port) <= 65_535) {
    s7Port.value = String(Number(s7.port));
  }
  updateSlaveModeVisibility();
}

function restoreHelpReference(reference) {
  if (!reference || typeof reference !== "object") {
    savedProtocolGuideReference = null;
    return;
  }
  const source = String(reference.source ?? "");
  const variant = String(reference.variant ?? "").slice(0, 64);
  savedProtocolGuideReference = variant ? { source, variant } : null;
}

function setProjectIdentity(name, filePath = "") {
  currentProjectName = String(name || "未命名项目").trim().slice(0, 128) || "未命名项目";
  projectHasPath = Boolean(filePath);
  if (elements.projectName) {
    elements.projectName.textContent = currentProjectName;
    elements.projectName.title = filePath || "当前项目尚未保存";
  }
}

function projectNameFromPath(filePath) {
  const filename = String(filePath || "").split(/[\\/]/).pop() || "";
  return filename.replace(/\.nexus\.json$/i, "") || currentProjectName;
}

function projectMigrationNotice(result) {
  const migration = result?.migration;
  if (!migration || migration.sourceSchemaVersion === migration.targetSchemaVersion) return "";
  return `；检测到旧格式 v${migration.sourceSchemaVersion}，已按 v${migration.targetSchemaVersion} 只读兼容打开，原文件未修改，显式保存后才升级。`;
}

function buildProjectDocument() {
  saveCurrentSessionData();
  return {
    format: "nexus-project",
    schemaVersion: 2,
    product: "Nexus 2.0",
    projectName: currentProjectName,
    savedAt: null,
    activeView,
    activeSession,
    config: collectPersistentConfig(),
    sessions: [...sessionTabs.entries()].map(([name, tab]) => ({
      name,
      pointTable: tab.pointTable.map((point) => ({ ...point })),
      trendSelection: [...(tab.trendSelection ?? [])],
    })),
    workspace: {
      commandList: commandList.map((command) => ({ ...command })),
      simulators: collectSimulatorWorkspace(),
      lastHelpReference: savedProtocolGuideReference ? { ...savedProtocolGuideReference } : null,
      frameDefinitions: frameDefs.map((def) => ({
        ...def,
        lengthField: def.lengthField ? { ...def.lengthField } : (def.lengthField ?? null),
        checksum: def.checksum ? { ...def.checksum } : (def.checksum ?? null),
        fields: (def.fields ?? []).map((field) => ({ ...field })),
      })),
    },
  };
}

function hasActiveProjectRuntime() {
  return busy || isConnected() || Boolean(activePollId) || Boolean(pointPollTimer)
    || slaveRunning || mcConnected || mcSlaveRunning || omConnected || omSlaveRunning
    || s7Connected || s7SlaveRunning || sseRunning;
}

function ensureProjectSwitchIsSafe() {
  if (!hasActiveProjectRuntime()) return true;
  setNotice("error", "请先停止通信", "新建或打开项目前，请关闭连接、轮询、从站模拟和实时推送。当前运行状态不会被强制中断。");
  return false;
}

function replaceProjectSessions(sessions, requestedActiveSession) {
  sessionTabs.clear();
  elements.sessionTabsBar?.replaceChildren();
  for (const session of sessions) {
    const btn = createSessionTabButton(session.name);
    elements.sessionTabsBar?.append(btn);
    sessionTabs.set(session.name, {
      rowsHtml: "",
      pointTable: session.pointTable.map((point) => ({ ...point })),
      trendSelection: [...(session.trendSelection ?? [])],
      btn,
    });
  }
  const nextSession = sessionTabs.has(requestedActiveSession)
    ? requestedActiveSession
    : sessionTabs.keys().next().value;
  activeSession = nextSession;
  restoreSessionData(nextSession);
  for (const tab of elements.sessionTabsBar?.querySelectorAll(".session-tab-btn") ?? []) {
    tab.classList.toggle("is-active", tab.dataset.session === nextSession);
  }
  persistPointTable();
}

function askProjectName() {
  const entered = window.prompt("项目名称", currentProjectName === "未命名项目" ? "Nexus项目" : currentProjectName);
  if (entered === null) return null;
  return entered.trim().slice(0, 128) || "Nexus项目";
}

async function saveProject(saveAs = false) {
  try {
    if (saveAs || !projectHasPath) {
      const name = askProjectName();
      if (name === null) return;
      setProjectIdentity(name);
    }
    const result = await callBackend(saveAs ? "project_save_as" : "project_save", { document: buildProjectDocument() });
    if (result?.canceled) return;
    const savedName = result.document?.projectName || projectNameFromPath(result.path);
    setProjectIdentity(savedName, result.path);
    persistConfig();
    setNotice("success", "项目已保存", `${savedName} · ${result.bytes} 字节`);
  } catch (error) {
    setNotice("error", "项目保存失败", error.message || String(error));
  }
}

async function openProject() {
  if (!ensureProjectSwitchIsSafe()) return;
  try {
    const result = await callBackend("project_open");
    if (result?.canceled) return;
    const project = result.document;
    applyPersistentConfig(project.config);
    replaceProjectSessions(project.sessions, project.activeSession);
    replaceCommandList(project.workspace?.commandList ?? []);
    restoreFrameDefinitions(project.workspace?.frameDefinitions ?? []);
    restoreSimulatorWorkspace(project.workspace?.simulators ?? {});
    restoreHelpReference(project.workspace?.lastHelpReference ?? null);
    activateView(project.activeView);
    setProjectIdentity(project.projectName || projectNameFromPath(result.path), result.path);
    persistConfig();
    setNotice("success", "项目已打开", `${currentProjectName} · 已恢复 ${project.sessions.length} 个会话、模拟器配置和帮助引用；未自动连接、启动模拟器或执行写入。${projectMigrationNotice(result)}`);
  } catch (error) {
    setNotice("error", "项目打开失败", error.message || String(error));
  }
}

async function exportSanitizedProject() {
  try {
    const result = await callBackend("project_export_sanitized", { document: buildProjectDocument() });
    if (result?.canceled) return;
    setNotice(
      "success",
      "脱敏项目已导出",
      `${result.path} · ${result.bytes} 字节；已移除项目名、会话/点位名、主机、串口名和写入值，当前工作区未切换。`,
    );
  } catch (error) {
    setNotice("error", "脱敏项目导出失败", error.message || String(error));
  }
}

async function newProject() {
  if (!ensureProjectSwitchIsSafe()) return;
  if (!window.confirm("新建项目将清空当前会话和点表。尚未保存的内容会丢失，是否继续？")) return;
  try {
    await callBackend("project_new");
    applyPersistentConfig({
      transport: "rtu",
      serial: { portName: "", baudRate: "9600", parity: "none", dataBits: "8", stopBits: "1" },
      tcp: { host: "127.0.0.1", port: "502" },
      command: { unitId: "1", functionCode: "3", startAddress: "0", quantity: "1", displayType: "Unsigned16", pollInterval: "1000" },
    });
    replaceProjectSessions([{ name: "default", pointTable: [] }], "default");
    replaceCommandList([]);
    restoreFrameDefinitions([]);
    restoreTrendSelection([]);
    restoreSimulatorWorkspace({
      modbus: { mode: "tcp", port: "502", allowedStations: "" },
      melsec: { port: "5000" },
      s7: { port: "102" },
    });
    restoreHelpReference(null);
    activateView("master");
    setProjectIdentity("未命名项目");
    persistConfig();
    setNotice("success", "已新建项目", "已建立空白项目；未连接任何设备。 ");
  } catch (error) {
    setNotice("error", "新建项目失败", error.message || String(error));
  }
}

/**
 * 启动时的一次性只读恢复：
 * - 恢复项目配置、点表、趋势选择、任务清单和显示视图；
 * - 不打开串口/TCP，不启动轮询/从站/实时推送，不执行指令；
 * - 恢复失败只提示，不阻塞应用启动。
 */
async function restoreLastProject() {
  if (!window.nexusDesktop) return;
  try {
    const result = await callBackend("project_restore_last");
    if (result?.canceled) {
      if (result?.reason === "restore-failed") {
        setNotice(
          "error",
          "上次项目恢复失败",
          `${result?.error?.message ?? "项目文件不可读取"}；已继续使用本地配置，未连接任何设备。`,
        );
      }
      return;
    }
    const project = result.document;
    applyPersistentConfig(project.config);
    replaceProjectSessions(project.sessions, project.activeSession);
    replaceCommandList(project.workspace?.commandList ?? []);
    restoreFrameDefinitions(project.workspace?.frameDefinitions ?? []);
    restoreSimulatorWorkspace(project.workspace?.simulators ?? {});
    restoreHelpReference(project.workspace?.lastHelpReference ?? null);
    activateView(project.activeView);
    setProjectIdentity(project.projectName || projectNameFromPath(result.path), result.path);
    persistConfig();
    setNotice(
      "info",
      "已按只读方式恢复上次项目",
      `${currentProjectName} · 已恢复配置、点表、趋势选择、任务清单、模拟器配置和帮助引用；不自动连接、启动模拟器或执行指令。${projectMigrationNotice(result)}`,
    );
  } catch (error) {
    setNotice("error", "上次项目恢复失败", `${error.message ?? String(error)}；已继续使用本地配置，未连接任何设备。`);
  }
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
      renderParserMessage(`解析失败: ${error.message || error}`);
    }
  }
}

function renderParseResult(info) {
  if (!elements.parserResult) return;
  if (!info || !info.isValid) {
    renderParserMessage(`无效报文: ${info?.error || "解析失败"}`);
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
  elements.parserResult.replaceChildren();
  for (const [label, value] of fields) {
    const field = document.createElement("div");
    field.className = "field";
    const labelNode = document.createElement("span");
    labelNode.className = "field-label";
    labelNode.textContent = label;
    const valueNode = document.createElement("span");
    valueNode.className = "field-value";
    valueNode.textContent = String(value ?? "—");
    field.append(labelNode, valueNode);
    elements.parserResult.append(field);
  }
}

function renderParserMessage(message) {
  if (!elements.parserResult) return;
  const node = document.createElement("div");
  node.style.color = "var(--danger)";
  node.textContent = String(message);
  elements.parserResult.replaceChildren(node);
}

function syncGx3Controls() {
  if (elements.gx3SelectProject) elements.gx3SelectProject.disabled = gx3Busy;
  if (elements.gx3AnalyzeProject) {
    elements.gx3AnalyzeProject.disabled = gx3Busy || !gx3Available || !elements.gx3ProjectPath?.value;
  }
  if (elements.gx3QueryDevice) {
    elements.gx3QueryDevice.disabled = gx3Busy || !activeGx3Analysis;
  }
  if (elements.gx3Device) elements.gx3Device.disabled = gx3Busy || !activeGx3Analysis;
}

function setGx3Busy(value, stateText) {
  gx3Busy = Boolean(value);
  if (stateText && elements.gx3AnalysisState) elements.gx3AnalysisState.textContent = stateText;
  syncGx3Controls();
}

function createGx3Node(tagName, className, text) {
  const node = document.createElement(tagName);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = String(text);
  return node;
}

function createGx3Section(title) {
  const section = createGx3Node("section", "gx3-report-section");
  section.append(createGx3Node("h3", "", title));
  return section;
}

function appendGx3Table(container, headers, rows) {
  const wrap = createGx3Node("div", "gx3-table-wrap");
  const table = createGx3Node("table", "gx3-readable-table");
  const head = document.createElement("thead");
  const headRow = document.createElement("tr");
  for (const header of headers) headRow.append(createGx3Node("th", "", header));
  head.append(headRow);
  const body = document.createElement("tbody");
  for (const row of rows) {
    const tr = document.createElement("tr");
    for (const value of row) tr.append(createGx3Node("td", "", value));
    body.append(tr);
  }
  table.append(head, body);
  wrap.append(table);
  container.append(wrap);
}

function renderGx3Summary(result) {
  if (!elements.gx3AnalysisSummary) return;
  const view = buildGx3Presentation(result);
  const fields = [
    ["当前工程", view.projectName, result.sourcePath],
    ["原始文件", view.sourceUnchanged ? "已验证未修改" : "需要复核", "解析前后进行 SHA-256 校验"],
    ["解析方式", "本机离线只读", result.cliVersion || result.cliPath],
  ];
  elements.gx3AnalysisSummary.replaceChildren();
  const grid = createGx3Node("div", "gx3-overview-grid");
  for (const [label, value, title] of fields) {
    const item = createGx3Node("div", "gx3-overview-item");
    item.append(createGx3Node("span", "gx3-overview-label", label));
    const valueNode = createGx3Node("span", "gx3-overview-value", value ?? "—");
    if (title) valueNode.title = String(title);
    item.append(valueNode);
    grid.append(item);
  }
  elements.gx3AnalysisSummary.append(grid);
}

function renderGx3HumanReport(result) {
  if (!elements.gx3HumanReport) return;
  const view = buildGx3Presentation(result);
  elements.gx3HumanReport.replaceChildren();

  const metrics = createGx3Node("section", "gx3-metric-grid");
  for (const metric of view.metrics) {
    const item = createGx3Node("div", "gx3-metric");
    item.append(
      createGx3Node("span", "gx3-metric-label", metric.label),
      createGx3Node("strong", "gx3-metric-value", metric.value),
      createGx3Node("span", "gx3-metric-detail", metric.detail),
    );
    metrics.append(item);
  }
  elements.gx3HumanReport.append(metrics);

  const purposeSection = createGx3Section("程序可能在做什么");
  for (const hint of view.purposeHints) {
    const card = createGx3Node("article", "gx3-purpose-card");
    card.append(
      createGx3Node("div", "gx3-purpose-title", hint.title),
      createGx3Node("p", "", hint.description),
      createGx3Node("small", "", `判断依据：${hint.evidence}`),
      createGx3Node("small", "", hint.caution),
    );
    purposeSection.append(card);
  }
  elements.gx3HumanReport.append(purposeSection);

  const qualitySection = createGx3Section("解析完整度");
  const qualityTitle = createGx3Node("div", "gx3-inline-note", `记录覆盖率 ${view.quality.coverageLabel}`);
  qualityTitle.append(createGx3Node("span", `gx3-badge ${view.quality.tone}`, view.quality.tone === "good" ? "完整" : "需要复核"));
  const qualityBar = createGx3Node("div", "gx3-quality-bar");
  const qualityFill = createGx3Node("div", `gx3-quality-fill ${view.quality.tone}`);
  qualityFill.style.width = `${Math.round(view.quality.coverageRate * 10000) / 100}%`;
  qualityBar.append(qualityFill);
  qualitySection.append(
    qualityTitle,
    qualityBar,
    createGx3Node("div", "gx3-inline-note", view.quality.summary),
    createGx3Node("div", "gx3-evidence", "覆盖率表示记录是否被保留，不代表所有指令参数都已完整解码，也不构成程序安全认证。"),
  );
  elements.gx3HumanReport.append(qualitySection);

  const structureSection = createGx3Section("程序结构");
  const structureRows = view.pous.length
    ? view.pous.map((pou) => [pou.displayName, pou.programFile, pou.decoded ? "名称已解码" : "名称未解码"])
    : [["未识别到 POU", "—", "请查看技术原始结果"]];
  appendGx3Table(structureSection, ["POU", "关联程序文件", "状态"], structureRows);
  if (view.programFiles.length) {
    structureSection.append(createGx3Node("div", "gx3-evidence", `程序文件：${view.programFiles.join("、")}`));
  }
  elements.gx3HumanReport.append(structureSection);

  if (view.deviceTypes.length) {
    const deviceSection = createGx3Section("软元件使用概况");
    appendGx3Table(
      deviceSection,
      ["类型", "用途分类", "引用数量", "识别状态"],
      view.deviceTypes.map((entry) => [entry.deviceType, entry.category, String(entry.count), entry.known ? "已识别" : "未知"]),
    );
    elements.gx3HumanReport.append(deviceSection);
  }

  if (view.unsupported.length || view.warnings.length) {
    const issueSection = createGx3Section("需要回到 GX Works3 复核的内容");
    const list = createGx3Node("ul", "gx3-plain-list");
    for (const instruction of view.unsupported) {
      const item = document.createElement("li");
      item.append(
        createGx3Node("span", "gx3-list-title", `${instruction.opcode} · ${instruction.name}（${instruction.count} 处）`),
        createGx3Node("span", "gx3-evidence", `${instruction.description} 当前有 ${instruction.partialRows} 条记录仅部分解析。`),
      );
      list.append(item);
    }
    for (const warning of view.warnings) list.append(createGx3Node("li", "", warning));
    issueSection.append(list);
    elements.gx3HumanReport.append(issueSection);
  }
}

function renderGx3DeviceSummary(result) {
  if (!elements.gx3DeviceSummary) return;
  const view = parseGx3DevicePresentation(result);
  elements.gx3DeviceSummary.replaceChildren();
  const header = createGx3Section(`${view.device} · ${view.description}`);
  const metrics = createGx3Node("div", "gx3-metric-grid");
  for (const metric of [
    ["总引用", view.occurrences, "索引记录"],
    ["写入位置", view.writers.length, "会改变该软元件"],
    ["读取位置", view.readers.length, "把该软元件作为条件或数据使用"],
    ["驱动行", view.driverRows, `条件使用 ${view.conditionUses} 处`],
  ]) {
    const item = createGx3Node("div", "gx3-metric");
    item.append(
      createGx3Node("span", "gx3-metric-label", metric[0]),
      createGx3Node("strong", "gx3-metric-value", metric[1]),
      createGx3Node("span", "gx3-metric-detail", metric[2]),
    );
    metrics.append(item);
  }
  header.append(metrics);
  elements.gx3DeviceSummary.append(header);

  const relationSection = createGx3Section("程序中的读写关系");
  const relations = [...view.writers, ...view.readers];
  if (relations.length) {
    appendGx3Table(
      relationSection,
      ["方向", "位置", "语句", "引用方式"],
      relations.map((entry) => [entry.direction, entry.location, String(entry.statement), entry.detail]),
    );
  } else {
    relationSection.append(createGx3Node("div", "empty-guide", "没有解析到可结构化展示的写入或读取位置，请展开原始结果核对。"));
  }
  elements.gx3DeviceSummary.append(relationSection);
}

async function refreshGx3Status() {
  if (!elements.gx3ToolState) return;
  if (!window.nexusDesktop) {
    gx3Available = false;
    elements.gx3ToolState.textContent = "仅桌面版可用";
    syncGx3Controls();
    return;
  }
  elements.gx3ToolState.textContent = "正在检查解析器…";
  try {
    const status = await callBackend("gx3_status");
    gx3Available = Boolean(status?.available);
    elements.gx3ToolState.textContent = gx3Available
      ? `可用 · ${status.version || "gx3-cli"}`
      : `不可用 · ${status?.error?.message || "未找到 gx3-cli"}`;
  } catch (error) {
    gx3Available = false;
    elements.gx3ToolState.textContent = `检查失败 · ${error.message || error}`;
  }
  syncGx3Controls();
}

async function selectGx3Project() {
  if (gx3Busy) return;
  try {
    const result = await callBackend("gx3_select_project");
    if (result?.canceled) return;
    elements.gx3ProjectPath.value = result.path || "";
    activeGx3Analysis = null;
    if (elements.gx3AnalysisState) elements.gx3AnalysisState.textContent = "等待解析";
    if (elements.gx3HumanReport) {
      const guide = createGx3Node("div", "empty-guide", "项目已选择。点击“开始只读解析”，Nexus 会生成中文程序概览。");
      elements.gx3HumanReport.replaceChildren(guide);
    }
    if (elements.gx3AnalysisOutput) elements.gx3AnalysisOutput.textContent = "已选择项目，尚未生成技术原始结果。";
    if (elements.gx3TechnicalDetails) elements.gx3TechnicalDetails.open = false;
    if (elements.gx3DeviceSummary) {
      const guide = createGx3Node("div", "empty-guide", "完成项目解析后，可查询软元件的中文说明和读写位置。");
      elements.gx3DeviceSummary.replaceChildren(guide);
    }
    if (elements.gx3DeviceOutput) elements.gx3DeviceOutput.textContent = "尚未查询软元件。";
    if (elements.gx3DeviceTechnical) {
      elements.gx3DeviceTechnical.open = false;
      elements.gx3DeviceTechnical.classList.remove("has-result");
    }
    if (elements.gx3AnalysisSummary) {
      const guide = document.createElement("div");
      guide.className = "empty-guide";
      guide.textContent = `已选择：${result.path}`;
      elements.gx3AnalysisSummary.replaceChildren(guide);
    }
    syncGx3Controls();
  } catch (error) {
    setNotice("error", "GX3 项目选择失败", error.message || String(error));
  }
}

async function analyzeGx3Project() {
  const sourcePath = elements.gx3ProjectPath?.value?.trim();
  if (!sourcePath || gx3Busy || !gx3Available) return;
  activeGx3Analysis = null;
  setGx3Busy(true, "正在复制并解析…");
  if (elements.gx3HumanReport) {
    const guide = createGx3Node("div", "empty-guide", "正在读取程序结构、软元件类型和交叉引用，请稍候…");
    elements.gx3HumanReport.replaceChildren(guide);
  }
  if (elements.gx3AnalysisOutput) {
    elements.gx3AnalysisOutput.textContent = "正在建立私有工作副本并运行 doctor、index-lite、xref…\n复杂项目可能需要几十秒。";
  }
  try {
    const result = await callBackend("gx3_analyze_project", { sourcePath });
    activeGx3Analysis = result;
    renderGx3Summary(result);
    renderGx3HumanReport(result);
    if (elements.gx3AnalysisOutput) elements.gx3AnalysisOutput.textContent = formatGx3TechnicalReport(result);
    if (elements.gx3TechnicalDetails) elements.gx3TechnicalDetails.open = false;
    if (elements.gx3AnalysisState) elements.gx3AnalysisState.textContent = "只读解析完成";
    setNotice("success", "GX3 项目解析完成", `识别到 ${result.programMap?.pous?.length ?? 0} 个 POU；原始 .gx3 未修改。`);
  } catch (error) {
    if (elements.gx3AnalysisState) elements.gx3AnalysisState.textContent = "解析失败";
    if (elements.gx3HumanReport) {
      const guide = createGx3Node("div", "empty-guide", `解析失败：${error.message || error}`);
      elements.gx3HumanReport.replaceChildren(guide);
    }
    if (elements.gx3AnalysisOutput) elements.gx3AnalysisOutput.textContent = `解析失败：${error.message || error}`;
    setNotice("error", "GX3 项目解析失败", error.message || String(error));
  } finally {
    setGx3Busy(false);
  }
}

async function queryGx3Device() {
  const device = elements.gx3Device?.value?.trim();
  if (!device || !activeGx3Analysis || gx3Busy) return;
  setGx3Busy(true, `正在查询 ${device.toUpperCase()}…`);
  if (elements.gx3DeviceSummary) {
    const guide = createGx3Node("div", "empty-guide", `正在整理 ${device.toUpperCase()} 的说明和程序读写关系…`);
    elements.gx3DeviceSummary.replaceChildren(guide);
  }
  if (elements.gx3DeviceOutput) elements.gx3DeviceOutput.textContent = "正在查询索引和交叉引用…";
  try {
    const result = await callBackend("gx3_query_device", {
      analysisId: activeGx3Analysis.analysisId,
      device,
    });
    if (elements.gx3Device) elements.gx3Device.value = result.device;
    renderGx3DeviceSummary(result);
    if (elements.gx3DeviceOutput) {
      elements.gx3DeviceOutput.textContent = [
        `[${result.device} · 索引查询]`,
        result.index || "没有索引结果。",
        "",
        `[${result.device} · 写入/读取交叉引用]`,
        result.xref || "没有交叉引用结果。",
      ].join("\n");
    }
    if (elements.gx3DeviceTechnical) {
      elements.gx3DeviceTechnical.classList.add("has-result");
      elements.gx3DeviceTechnical.open = false;
    }
    if (elements.gx3AnalysisState) elements.gx3AnalysisState.textContent = `已查询 ${result.device}`;
  } catch (error) {
    if (elements.gx3DeviceSummary) {
      const guide = createGx3Node("div", "empty-guide", `查询失败：${error.message || error}`);
      elements.gx3DeviceSummary.replaceChildren(guide);
    }
    if (elements.gx3DeviceOutput) elements.gx3DeviceOutput.textContent = `查询失败：${error.message || error}`;
    setNotice("error", "GX3 软元件查询失败", error.message || String(error));
  } finally {
    setGx3Busy(false);
  }
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
    if (value instanceof Node) cell.append(value);
    else cell.textContent = String(value ?? "—");
    row.append(cell);
  }
}

function renderEmptyTableRow(body, columnCount, message) {
  const row = document.createElement("tr");
  row.className = "empty-row";
  const cell = document.createElement("td");
  cell.colSpan = columnCount;
  cell.textContent = String(message);
  row.append(cell);
  body.replaceChildren(row);
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

function renderCoils(command, coils) {
  elements.registerResults.replaceChildren();
  const updatedAt = clockTime();
  const discrete = command.functionCode === 2;
  const prefix = discrete ? "DI" : "C";
  const area = discrete ? "离散输入" : "线圈";
  for (let i = 0; i < coils.length; i++) {
    const address = command.startAddress + i;
    const row = document.createElement("tr");
    appendCells(row, [
      "●",
      `${prefix} ${address}`,
      area,
      address,
      "Bool",
      "—",
      "1",
      "—",
      coils[i] ? "ON" : "OFF",
      "Good",
      updatedAt,
    ]);
    row.lastElementChild.previousElementSibling.classList.add("quality-good");
    elements.registerResults.append(row);
  }
  elements.pointCount.textContent = `点位 ${coils.length}`;
}

async function invokeModbusRead(command) {
  const backendCommand = resolveModbusReadCommand(command.transport, command.functionCode);
  if (!backendCommand) {
    throw new Error(`不支持的 Modbus 读组合：${command.transport} FC${command.functionCode}`);
  }
  const isBits = command.functionCode === 1 || command.functionCode === 2;
  if (modbusNetworkPrefix(command.transport)) {
    const result = await callBackend(backendCommand, {
      connectionId: "default",
      startAddress: command.startAddress,
      quantity: command.quantity,
    });
    return {
      ok: !result.exceptionCode,
      registers: result.registers || [],
      coils: result.coils || [],
      values: (isBits ? result.coils : result.registers) || [],
      tx: null,
      rx: null,
      elapsedMs: result.elapsedMs ?? null,
      crcValid: null,
      error: result.exceptionCode ? { message: `异常码 ${result.exceptionCode}` } : null,
    };
  }
  const result = await callBackend(backendCommand, command);
  return {
    ...result,
    values: (isBits ? result.coils : result.registers) || [],
  };
}

async function invokeModbusWrite(command, writePayload) {
  const backendCommand = resolveModbusWriteCommand(command.transport, command.functionCode);
  if (!backendCommand) {
    throw new Error(`不支持的 Modbus 写组合：${command.transport} FC${command.functionCode}`);
  }
  if (modbusNetworkPrefix(command.transport)) {
    const result = await callBackend(backendCommand, {
      connectionId: "default",
      address: command.startAddress,
      value: writePayload.value,
      values: writePayload.values,
    });
    return {
      ok: !result.exceptionCode,
      tx: null,
      rx: null,
      elapsedMs: result.elapsedMs ?? null,
      crcValid: null,
      error: result.exceptionCode ? { message: `异常码 ${result.exceptionCode}` } : null,
    };
  }
  return callBackend(backendCommand, {
    unitId: command.unitId,
    address: command.startAddress,
    timeoutMs: command.timeoutMs,
    transport: command.transport,
    ...writePayload,
  });
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
  const isBits = command.functionCode === 1 || command.functionCode === 2;
  const registerLabel = command.functionCode === 2
    ? "离散输入"
    : command.functionCode === 1
      ? "线圈"
      : command.functionCode === 4
        ? "输入寄存器"
        : "保持寄存器";
  elements.commandState.textContent = `正在执行 ${functionLabel}`;
  setNotice("info", "正在读取", `站号 ${command.unitId}，地址 ${command.startAddress}，数量 ${command.quantity}。`);
  try {
    const response = await invokeModbusRead(command);
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
    const valueCount = (response.values || []).length;
    appendTrace({
      direction: "RX",
      unitId: command.unitId,
      functionCode: command.functionCode,
      bytes: response.rx,
      crc: rxCrc,
      elapsedMs: response.elapsedMs,
      result: response.ok ? `${valueCount} 个${registerLabel}` : error?.message ?? "读取失败",
    });

    if (response.ok) {
      if (isBits) renderCoils(command, response.coils || response.values || []);
      else await renderRegisters(command, response.registers || response.values || []);
      setNotice("success", "读取完成", `收到 ${valueCount} 个${registerLabel}${response.crcValid === true ? "，CRC 校验通过" : ""}。`);
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

function writeQuantity(functionCode, writePayload) {
  if (functionCode === 5 || functionCode === 6) return 1;
  return (writePayload.values ?? []).length;
}

function writeNewValue(functionCode, writePayload) {
  if (functionCode === 5 || functionCode === 6) return [writePayload.value];
  return [...(writePayload.values ?? [])];
}

function formatWriteValue(value) {
  const items = Array.isArray(value) ? value : [value];
  return JSON.stringify(items.slice(0, 32)) + (items.length > 32 ? " …" : "");
}

async function readCurrentValueForWrite(command) {
  try {
    const response = await invokeModbusRead({
      ...command,
      functionCode: isModbusBitFunction(command.functionCode)
        ? (command.functionCode === 5 || command.functionCode === 15 ? 1 : 2)
        : (command.functionCode === 6 || command.functionCode === 16 ? 3 : 4),
    });
    return {
      ok: response.ok,
      values: response.values ?? [],
      error: response.error,
    };
  } catch (error) {
    return {
      ok: false,
      values: null,
      error: { code: error.code ?? "PRE_READ_FAILED", message: error.message ?? String(error) },
    };
  }
}

function confirmModbusWrite(command, oldValue, newValue) {
  const functionLabel = `FC${String(command.functionCode).padStart(2, "0")}`;
  return window.confirm(
    `确认执行 ${functionLabel} 写入？\n\n`
    + `传输：${command.transport.toUpperCase()}　站号：${command.unitId}\n`
    + `地址：${command.startAddress}..${command.startAddress + command.quantity - 1}\n`
    + `旧值：${formatWriteValue(oldValue)}\n`
    + `新值：${formatWriteValue(newValue)}\n\n`
    + "写入后将按同地址回读核对；请确认现场设备与工艺安全。",
  );
}

async function recordWriteAudit(entry) {
  try {
    return await callBackend("record_write_audit", {
      protocol: "modbus",
      timestamp: new Date().toISOString(),
      ...entry,
    });
  } catch (error) {
    appendAlarm({
      code: "WRITE_AUDIT_FAILED",
      message: `写入审计记录失败：${error.message ?? String(error)}`,
    });
    refreshStats();
    return null;
  }
}

async function readbackAfterWrite(command) {
  const readback = await readCurrentValueForWrite(command);
  return readback;
}

function valuesEqual(left, right) {
  if (!Array.isArray(left) || !Array.isArray(right) || left.length !== right.length) return false;
  return left.every((value, index) => value === right[index]);
}

function confirmHighRiskControl(name, expectedWord) {
  const entered = window.prompt(
    `高危设备控制：${name}\n\n`
    + `如确认继续，请输入 ${expectedWord}（区分大小写）。\n`
    + "取消或输入不一致时不会发送任何控制命令。",
  );
  return String(entered ?? "").trim() === expectedWord;
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
  command.quantity = writeQuantity(functionCode, writePayload);
  const newValue = writeNewValue(functionCode, writePayload);
  const auditBase = {
    transport,
    connectionId: ["tcp", "rtu-over-tcp", "ascii-over-tcp", "udp"].includes(transport) ? "default" : `serial:${unitId}`,
    unitId,
    functionCode,
    address,
    quantity: command.quantity,
    newValue,
  };

  if (unitId === 0) {
    await recordWriteAudit({
      ...auditBase,
      result: "pre-read-failed",
      errorCode: "BROADCAST_WRITE_BLOCKED",
      message: "广播写无法取得旧值或回读，安全写入门禁已阻止",
    });
    setNotice("error", "广播写已阻止", "当前安全写入流程要求写前旧值和写后回读；广播站号 0 无法满足。");
    return;
  }

  setBusy(true);
  const functionLabel = `FC${String(functionCode).padStart(2, "0")}`;
  elements.commandState.textContent = `正在读取 ${functionLabel} 写入旧值`;
  setNotice("info", "安全写入", `正在读取站号 ${unitId} 地址 ${address} 的旧值。`);
  const oldRead = await readCurrentValueForWrite(command);
  if (!oldRead.ok) {
    await recordWriteAudit({
      ...auditBase,
      oldValue: null,
      result: "pre-read-failed",
      errorCode: oldRead.error?.code ?? "PRE_READ_FAILED",
      message: oldRead.error?.message ?? "写前读取旧值失败",
    });
    stats.errors += 1;
    appendAlarm({ code: "PRE_READ_FAILED", message: oldRead.error?.message ?? "写前读取旧值失败" });
    refreshStats();
    setNotice("error", "写入已阻止", "无法读取旧值；当前安全流程不允许盲写。");
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先打开串口";
    return;
  }
  const oldValue = oldRead.values;
  if (!confirmModbusWrite(command, oldValue, newValue)) {
    await recordWriteAudit({ ...auditBase, oldValue, result: "cancelled" });
    setNotice("info", "已取消", "用户取消了本次写入。");
    setBusy(false);
    elements.commandState.textContent = isConnected() ? commandReadyText() : "请先打开串口";
    return;
  }

  elements.commandState.textContent = `正在执行 ${functionLabel} 写入`;
  setNotice("info", "正在写入", `站号 ${unitId}，地址 ${address}。`);
  try {
    const response = await invokeModbusWrite({
      ...command,
      startAddress: address,
    }, writePayload);

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
      elements.commandState.textContent = `正在回读 ${functionLabel}`;
      const readback = await readbackAfterWrite(command);
      if (!readback.ok) {
        await recordWriteAudit({
          ...auditBase,
          oldValue,
          result: "readback-failed",
          errorCode: readback.error?.code ?? "READBACK_FAILED",
          message: readback.error?.message ?? "写后回读失败",
        });
        stats.errors += 1;
        appendAlarm({ code: "READBACK_FAILED", message: readback.error?.message ?? "写后回读失败" });
        refreshStats();
        setNotice("error", "回读失败", "写入命令已被设备确认，但写后回读未完成；请手动核对当前值。");
        return;
      }
      if (!valuesEqual(readback.values, newValue)) {
        await recordWriteAudit({
          ...auditBase,
          oldValue,
          readbackValue: readback.values,
          result: "readback-mismatch",
          errorCode: "READBACK_MISMATCH",
          message: `写入后回读不一致：${formatWriteValue(readback.values)}`,
        });
        stats.errors += 1;
        appendAlarm({ code: "READBACK_MISMATCH", message: `期望 ${formatWriteValue(newValue)}，实际 ${formatWriteValue(readback.values)}` });
        refreshStats();
        setNotice("error", "回读不一致", `写入已确认但读回值不匹配；期望 ${formatWriteValue(newValue)}，实际 ${formatWriteValue(readback.values)}。`);
        return;
      }
      await recordWriteAudit({
        ...auditBase,
        oldValue,
        readbackValue: readback.values,
        result: "write-succeeded",
        message: "写入后回读一致",
      });
      setNotice("success", "写入完成并回读一致", `${functionLabel} 地址 ${address}；旧值 ${formatWriteValue(oldValue)} → 新值 ${formatWriteValue(newValue)}。`);
    } else {
      const code = error?.code ?? "WRITE_FAILED";
      await recordWriteAudit({
        ...auditBase,
        oldValue,
        result: "write-failed",
        errorCode: code,
        message: error?.message ?? "写入失败",
      });
      stats.errors += 1;
      if (code.includes("TIMEOUT")) stats.timeout += 1;
      appendAlarm({ code, message: error?.message ?? "写入失败" });
      setNotice("error", "写入失败", error?.message ?? "Modbus 写入未完成。");
    }
    refreshStats();
  } catch (error) {
    stats.errors += 1;
    await recordWriteAudit({
      ...auditBase,
      oldValue,
      result: "write-failed",
      errorCode: error.code ?? "IPC_ERROR",
      message: error.message ?? String(error),
    });
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
    const t0 = performance.now();
    const status = await callBackend("open_serial_port", { config });
    const ms = performance.now() - t0;
    renderStatus(status);
    elements.connectionLabel.textContent = `${config.portName} 已打开 · ${ms.toFixed(0)} ms`;
    setNotice("success", "串口句柄已打开", `${config.portName} 已建立独占句柄 · 打开耗时 ${ms.toFixed(0)} ms；尚未验证从站通信。`);
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
  if (elements.projectNew) elements.projectNew.addEventListener("click", newProject);
  if (elements.projectOpen) elements.projectOpen.addEventListener("click", openProject);
  if (elements.projectSave) elements.projectSave.addEventListener("click", () => saveProject(false));
  if (elements.projectSaveAs) elements.projectSaveAs.addEventListener("click", () => saveProject(true));
  if (elements.projectExportSanitized) elements.projectExportSanitized.addEventListener("click", exportSanitizedProject);
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
  if (elements.scanAll) elements.scanAll.addEventListener("click", scanAll);
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
  // 调试页实时曲线
  if (elements.plotAdd) elements.plotAdd.addEventListener("click", plotAddSelected);
  if (elements.plotPause) elements.plotPause.addEventListener("click", plotTogglePause);
  if (elements.plotClear) elements.plotClear.addEventListener("click", plotClearAll);
  if (elements.plotExport) elements.plotExport.addEventListener("click", () => void plotExportCsv());
  if (elements.plotManAdd) elements.plotManAdd.addEventListener("click", plotAddManual);
  if (elements.plotChannelSelect) {
    // 打开下拉前刷新通道列表(发现结果随收包动态变化)
    elements.plotChannelSelect.addEventListener("focus", plotRefreshChannelOptions);
    elements.plotChannelSelect.addEventListener("pointerdown", plotRefreshChannelOptions);
  }
  // 帧解析(批次 2)
  if (elements.fdMode) elements.fdMode.addEventListener("change", fdUpdateModeVisibility);
  if (elements.fdLenSrc) elements.fdLenSrc.addEventListener("change", fdUpdateModeVisibility);
  if (elements.fdAddField) elements.fdAddField.addEventListener("click", () => {
    elements.fdFieldsRows?.querySelector(".empty-row")?.remove();
    elements.fdFieldsRows?.append(fdFieldRow({}));
  });
  if (elements.fdSave) elements.fdSave.addEventListener("click", () => void fdSaveDefinition());
  if (elements.fdLoad) elements.fdLoad.addEventListener("click", fdLoadSelected);
  if (elements.fdDelete) elements.fdDelete.addEventListener("click", fdDeleteSelected);
  if (elements.fdApply) elements.fdApply.addEventListener("click", () => void fdApplyToCurve());
  if (elements.fdTry) elements.fdTry.addEventListener("click", () => void fdTryParse());
  fdRenderFields([]);
  fdUpdateModeVisibility();
  // 会话录制/回放(批次 3)
  if (elements.recToggle) elements.recToggle.addEventListener("click", () => void recToggle());
  if (elements.replayOpen) elements.replayOpen.addEventListener("click", () => void replayOpenFile());
  if (elements.replayToggle) elements.replayToggle.addEventListener("click", replayTogglePlay);
  if (elements.replaySpeed) elements.replaySpeed.addEventListener("change", replaySpeedChange);
  if (elements.replayStep) elements.replayStep.addEventListener("click", replayStepOnce);
  if (elements.replayExport) elements.replayExport.addEventListener("click", () => void replayExportCsv());
  // 接收 debug_frame 推送(收发记录 + 实时曲线喂点)
  if (window.nexusDesktop?.onDebugFrame) {
    window.nexusDesktop.onDebugFrame((record) => {
      appendDebugLog(record);
      plotFeedRecord(record);
    });
  }
  // 报文解析
  if (elements.parserParse) elements.parserParse.addEventListener("click", parseFrame);
  if (elements.parserClear) elements.parserClear.addEventListener("click", () => { if (elements.parserInput) elements.parserInput.value = ""; if (elements.parserResult) elements.parserResult.innerHTML = '<div class="console-empty">输入 HEX 后点"解析报文"</div>'; });
  // GX Works3 项目只读解析
  if (elements.gx3SelectProject) elements.gx3SelectProject.addEventListener("click", selectGx3Project);
  if (elements.gx3AnalyzeProject) elements.gx3AnalyzeProject.addEventListener("click", analyzeGx3Project);
  if (elements.gx3QueryDevice) elements.gx3QueryDevice.addEventListener("click", queryGx3Device);
  if (elements.gx3Device) {
    elements.gx3Device.addEventListener("keydown", (event) => {
      if (event.key === "Enter") {
        event.preventDefault();
        void queryGx3Device();
      }
    });
  }
  syncGx3Controls();
  // G8: 多会话标签页
  const defaultSessionButton = elements.sessionTabsBar?.querySelector('[data-session="default"]');
  if (defaultSessionButton) {
    defaultSessionButton.addEventListener("click", () => activateSessionTab("default"));
    sessionTabs.get("default").btn = defaultSessionButton;
  }
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
  startPlotLoop();

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

  // ── 布局面板折叠/展开(侧栏 + 报文面板),localStorage 记忆(2026-08-17 UX 升级) ──
  const sidebarEl = document.querySelector(".sidebar");
  const sidebarToggleBtn = document.querySelector("#toggle-sidebar");
  const packetToggleBtn = document.querySelector("#toggle-packet-panel");
  const packetPanelEl = document.querySelector(".packet-panel");
  const LAYOUT_KEY = "nexus-layout-v1";
  try {
    const savedLayout = JSON.parse(localStorage.getItem(LAYOUT_KEY) || "{}");
    if (savedLayout.sidebar) sidebarEl?.classList.add("sidebar-collapsed");
    if (savedLayout.packet) {
      packetPanelEl?.classList.add("packet-panel-collapsed");
      packetPanelEl?.classList.add("collapsed"); // 与 #toggle-console 旧钩子保持同步
    }
  } catch { /* localStorage 不可用时忽略,折叠态不记忆 */ }
  const persistLayout = () => {
    try {
      localStorage.setItem(LAYOUT_KEY, JSON.stringify({
        sidebar: !!sidebarEl?.classList.contains("sidebar-collapsed"),
        packet: !!packetPanelEl?.classList.contains("packet-panel-collapsed"),
      }));
    } catch { /* 忽略写入失败 */ }
  };
  sidebarToggleBtn?.addEventListener("click", () => {
    sidebarEl?.classList.toggle("sidebar-collapsed");
    persistLayout();
  });
  packetToggleBtn?.addEventListener("click", () => {
    if (!packetPanelEl) return;
    const willCollapse = !packetPanelEl.classList.contains("packet-panel-collapsed")
      && !packetPanelEl.classList.contains("collapsed");
    packetPanelEl.classList.toggle("packet-panel-collapsed", willCollapse);
    packetPanelEl.classList.toggle("collapsed", willCollapse); // 与旧钩子同步,避免两种 class 状态漂移
    persistLayout();
  });

  try {
    renderStatus(await callBackend("get_serial_status"));
  } catch (error) {
    setNotice("error", "状态读取失败", String(error));
  }
  await refreshPorts({ quiet: true });
  // 恢复上次配置和点表(localStorage 持久化)
  restoreConfig();
  restorePointTable();
  await restoreLastProject();
  // 三菱 MC 页面初始化
  initMelsecUi();
  initSiemensUi();
  initOmronUi();
  initAllenBradleyUi();
  initBeckhoffUi();
  initKeyenceUi();
  initLsXgtUi();
  initDeltaUi();
  initInovanceUi();
  initXinjeUi();
  initFatekUi();
  initFujiUi();
  initGeUi();
  initPanasonicUi();
  initMqttUi();
  initIec104Ui();
  initDnp3Ui();
  initDlt645Ui();
  initCjt188Ui();
  initBacnetUi();
  initKnxUi();
  initInterfacesUi();
  initProtocolGuides();
  initCardCollapse(document);
  const diagBtn = document.querySelector("#export-diagnostics");
  if (diagBtn) diagBtn.addEventListener("click", async () => {
    try {
      const r = await callBackend("export_diagnostics", {
        recentFrames: traceHistory.slice(-100),
        projectSnapshot: {
          projectName: currentProjectName,
          hasPath: projectHasPath,
          activeView,
          activeSession,
          config: collectPersistentConfig(),
          sessionCount: sessionTabs.size,
          pointCount: [...sessionTabs.values()].reduce((total, tab) => total + tab.pointTable.length, 0),
        },
      });
      if (r.ok) setNotice("success", "诊断报告已导出", `桌面: ${r.path}（另附 TXT 摘要）`);
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
