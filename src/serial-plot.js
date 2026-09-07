/**
 * 串口调试实时曲线 —— 数据模型与绘制。
 * 与主站页 trend 实现相互独立(trend 代码不动),逻辑抽到本模块便于 node --test。
 *
 * 通道来源:
 *   - 自动解析: RX 帧经 parse_frame_online 后,与最近一次 TX 读请求配对得到 HR[n]/IR[n] 寄存器通道
 *   - 手动规则: 帧头过滤 + 字节偏移 + 类型/字节序/缩放(非标协议设备),纯渲染层求值
 */

export const PLOT_WINDOW_MS = 60000; // X 轴时间窗口: 最近 60 秒(与主站趋势一致)
export const PLOT_COLORS = ["#111111", "#1A7F45", "#9A6B00", "#D52B1E", "#555555", "#3366AA"];
export const PLOT_MAX_DISCOVERED = 300; // 自动解析发现通道的登记上限,防失控

const TYPE_SIZE = { u8: 1, u16: 2, i16: 2, u32: 4, i32: 4, f32: 4 };

/** 通道集合: 有序 Map(key -> series),60s 滚动窗口 + 每通道点数上限 */
export class SeriesStore {
  constructor({ windowMs = PLOT_WINDOW_MS, maxPoints = 300 } = {}) {
    this.windowMs = windowMs;
    this.maxPoints = maxPoints;
    this.series = new Map();
    this.paused = false; // 仅冻结绘制,数据继续入环(暂停可回补)
    this.yOverride = null; // 手动 Y 轴量程 {min,max} | null=自动(B.10)
  }

  /** @returns {boolean} 是否新增成功(key 已存在时 false) */
  add(key, { name, unit = "", rule = null }) {
    if (this.series.has(key)) return false;
    this.series.set(key, {
      key,
      name: String(name || key),
      unit: String(unit || ""),
      color: this.nextColor(),
      rule,
      dataPoints: [],
      maxPoints: this.maxPoints,
    });
    return true;
  }

  has(key) {
    return this.series.has(key);
  }

  get(key) {
    return this.series.get(key) ?? null;
  }

  remove(key) {
    return this.series.delete(key);
  }

  clear() {
    this.series.clear();
  }

  entries() {
    return this.series.values();
  }

  get size() {
    return this.series.size;
  }

  /** 推入数据点并裁剪: 超窗旧点 + 超点数上限。非数值忽略。 */
  feed(key, t, value) {
    const series = this.series.get(key);
    if (!series) return false;
    const numeric = Number(value);
    if (!Number.isFinite(numeric)) return false;
    series.dataPoints.push({ t, v: numeric });
    const cutoff = t - this.windowMs - 2000;
    while (series.dataPoints.length > 0 && series.dataPoints[0].t < cutoff) series.dataPoints.shift();
    if (series.dataPoints.length > series.maxPoints) {
      series.dataPoints.splice(0, series.dataPoints.length - series.maxPoints);
    }
    return true;
  }

  nextColor() {
    const used = new Set([...this.series.values()].map((s) => s.color));
    return PLOT_COLORS.find((c) => !used.has(c)) ?? PLOT_COLORS[this.series.size % PLOT_COLORS.length];
  }
}

/** 解析帧头 HEX 文本("01 03"/"0103")。空/纯空白返回 null(不过滤);非法字节抛错。 */
export function parseHeadHex(text) {
  const cleaned = String(text ?? "").replace(/0x/gi, "").trim();
  if (!cleaned) return null;
  const parts = cleaned.split(/[\s,]+/).filter(Boolean);
  return parts.map((p) => {
    const n = parseInt(p, 16);
    if (Number.isNaN(n) || n < 0 || n > 255) throw new Error(`非法帧头字节: ${p}`);
    return n;
  });
}

/** 求值手动字节偏移规则: 帧头不匹配/长度不足返回 null(调用方静默跳过)。 */
export function evalManualRule(rule, bytes) {
  if (!rule) return null;
  const frame = bytes ?? [];
  if (rule.head) {
    if (frame.length < rule.head.length) return null;
    for (let i = 0; i < rule.head.length; i++) {
      if (frame[i] !== rule.head[i]) return null;
    }
  }
  const size = TYPE_SIZE[rule.type];
  if (!size) return null;
  const offset = rule.offset;
  if (!Number.isInteger(offset) || offset < 0 || offset + size > frame.length) return null;
  const b = frame;
  let value;
  switch (rule.type) {
    case "u8":
      value = b[offset];
      break;
    case "u16":
      value = rule.order === "le" ? b[offset] | (b[offset + 1] << 8) : (b[offset] << 8) | b[offset + 1];
      break;
    case "i16": {
      const raw = rule.order === "le" ? b[offset] | (b[offset + 1] << 8) : (b[offset] << 8) | b[offset + 1];
      value = raw & 0x8000 ? raw - 0x10000 : raw;
      break;
    }
    case "u32":
      value = rule.order === "le"
        ? (b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16)) + b[offset + 3] * 0x1000000
        : b[offset] * 0x1000000 + ((b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
      break;
    case "i32": {
      const raw = rule.order === "le"
        ? (b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16)) + b[offset + 3] * 0x1000000
        : b[offset] * 0x1000000 + ((b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
      value = raw >= 0x80000000 ? raw - 0x100000000 : raw;
      break;
    }
    case "f32":
      value = new DataView(Uint8Array.from(b).buffer).getFloat32(offset, rule.order === "le");
      break;
    default:
      return null;
  }
  const scale = Number(rule.scale);
  return Number.isFinite(scale) && scale !== 1 ? value * scale : value;
}

/**
 * 配对出寄存器通道: RX 读响应 + 最近 TX 读请求(FC03/FC04, 提供起始地址)。
 * 无可用 TX 配对时退化为 "HR+i/IR+i"(响应内偏移);单位始终为空(原始寄存器值)。
 * @returns Array<{ key, name, value }>
 */
export function pairRegisterChannels(txInfo, rxInfo) {
  if (!rxInfo || !rxInfo.isValid || rxInfo.isException) return [];
  const regs = Array.isArray(rxInfo.registers) ? rxInfo.registers.map(Number) : [];
  if (regs.length === 0) return [];
  const area = rxInfo.baseFunctionCode === 4 ? "IR" : "HR";
  const paired = Boolean(
    txInfo
      && txInfo.isValid
      && txInfo.direction === "request"
      && (txInfo.baseFunctionCode === 3 || txInfo.baseFunctionCode === 4)
      && Number.isInteger(txInfo.address)
      && txInfo.address >= 0
      && (txInfo.unitId == null || rxInfo.unitId == null || txInfo.unitId === rxInfo.unitId),
  );
  const base = paired ? txInfo.address : null;
  return regs.map((value, i) => (base === null
    ? { key: `reg:${area}+${i}`, name: `${area}+${i}`, value }
    : { key: `reg:${area}:${base + i}`, name: `${area}[${base + i}]`, value }));
}

/** 导出 CSV 行: 按时间戳合并所有通道(同帧数据共享时间戳),稀疏单元格留空。 */
export function buildCsvRows(store) {
  const list = [...store.series.values()].filter((s) => s.dataPoints.length > 0);
  if (list.length === 0) return [];
  const used = new Set();
  const columns = list.map((series) => {
    let name = series.unit ? `${series.name}(${series.unit})` : series.name;
    if (used.has(name)) name = `${name}#${series.key}`;
    used.add(name);
    return { series, name };
  });
  const byTime = new Map();
  for (const { series, name } of columns) {
    for (const p of series.dataPoints) {
      let row = byTime.get(p.t);
      if (!row) {
        row = { time: new Date(p.t).toISOString() };
        byTime.set(p.t, row);
      }
      row[name] = p.v;
    }
  }
  return [...byTime.keys()].sort((a, b) => a - b).map((t) => {
    const source = byTime.get(t);
    const row = { time: source.time };
    for (const { name } of columns) row[name] = source[name] ?? "";
    return row;
  });
}

function formatTick(v) {
  const abs = Math.abs(v);
  if (abs >= 10000) return v.toExponential(1);
  if (abs >= 100) return v.toFixed(0);
  if (abs >= 1) return String(Math.round(v * 10) / 10);
  return Number(v.toPrecision(2)).toString();
}

function formatTime(t) {
  const d = new Date(t);
  return `${String(d.getMinutes()).padStart(2, "0")}:${String(d.getSeconds()).padStart(2, "0")}`;
}

/**
 * Y 轴量程(B.10,纯函数): 自动 = 窗口内 min/max + 8% padding + 退化保护;
 * override 为合法 {min,max}(有限且 min<max)时精确使用、不加 padding。
 * @param {SeriesStore} store 通道集合
 * @param {{min:number,max:number}|null} override 手动量程(空/非法 = 自动)
 * @param {number} tStart 窗口起点(ms),调用方传入保持纯函数
 */
export function computeYRange(store, override, tStart) {
  if (
    override
    && Number.isFinite(override.min)
    && Number.isFinite(override.max)
    && override.min < override.max
  ) {
    return { min: override.min, max: override.max };
  }
  let min = Infinity;
  let max = -Infinity;
  for (const series of store.entries()) {
    for (const p of series.dataPoints) {
      if (p.t < tStart) continue;
      if (p.v < min) min = p.v;
      if (p.v > max) max = p.v;
    }
  }
  if (!Number.isFinite(min) || !Number.isFinite(max)) { min = 0; max = 1; }
  if (min === max) { min -= 1; max += 1; }
  const padY = (max - min) * 0.08;
  return { min: min - padY, max: max + padY };
}

/** 绘制曲线卡画布: 统一 Y 轴 / 60s 滚动窗口 / HiDPI(实现口径与主站 drawTrendChart 一致)。 */
export function drawSerialPlot(canvas, store, { legendHost = null, emptyText = "添加通道后开始绘制" } = {}) {
  if (!canvas) return;
  const dpr = window.devicePixelRatio || 1;
  const cssW = canvas.clientWidth;
  const cssH = canvas.clientHeight;
  if (cssW === 0 || cssH === 0) return;
  if (canvas.width !== Math.round(cssW * dpr) || canvas.height !== Math.round(cssH * dpr)) {
    canvas.width = Math.round(cssW * dpr);
    canvas.height = Math.round(cssH * dpr);
  }
  const ctx = canvas.getContext("2d");
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, cssW, cssH);
  ctx.font = '10px "Cascadia Mono", "Consolas", monospace';
  ctx.textBaseline = "middle";

  if (store.size === 0) {
    ctx.font = '12px "Manrope", "Microsoft YaHei UI", sans-serif';
    ctx.fillStyle = "#999999";
    ctx.textAlign = "center";
    ctx.fillText(emptyText, cssW / 2, cssH / 2);
    ctx.textAlign = "left";
    return;
  }

  const now = Date.now();
  const tStart = now - store.windowMs;
  const { min, max } = computeYRange(store, store.yOverride, tStart);

  const labelTexts = [];
  for (let i = 0; i <= 5; i++) labelTexts.push(formatTick(max - ((max - min) * i) / 5));
  let gutterR = 0;
  for (const text of labelTexts) gutterR = Math.max(gutterR, ctx.measureText(text).width);
  gutterR += 12;
  const plot = { x: 8, y: 8, w: cssW - 8 - gutterR, h: cssH - 8 - 16 };
  if (plot.w < 40 || plot.h < 30) return;

  const xOf = (t) => plot.x + ((t - tStart) / store.windowMs) * plot.w;
  const yOf = (v) => plot.y + (1 - (v - min) / (max - min)) * plot.h;

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
  for (let i = 0; i <= 3; i++) {
    const t = tStart + (store.windowMs * i) / 3;
    const label = i === 3 ? "现在" : formatTime(t);
    if (i === 0) ctx.textAlign = "left";
    else if (i === 3) ctx.textAlign = "right";
    else ctx.textAlign = "center";
    ctx.fillText(label, plot.x + (plot.w * i) / 3, plot.y + plot.h + 8);
  }
  ctx.textAlign = "left";
  ctx.strokeStyle = "rgba(0, 0, 0, 0.16)";
  ctx.strokeRect(plot.x + 0.5, plot.y + 0.5, plot.w - 1, plot.h - 1);

  ctx.save();
  ctx.beginPath();
  ctx.rect(plot.x, plot.y, plot.w, plot.h);
  ctx.clip();
  for (const series of store.entries()) {
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
      ctx.beginPath();
      ctx.arc(lastX, lastY, 2.5, 0, Math.PI * 2);
      ctx.fill();
    }
  }
  ctx.restore();

  if (legendHost) {
    for (const series of store.entries()) {
      const chipValue = legendHost.querySelector(`.trend-chip[data-key="${series.key}"] .trend-chip-val`);
      if (!chipValue) continue;
      const last = series.dataPoints[series.dataPoints.length - 1];
      const text = last ? formatTick(last.v) : "—";
      chipValue.textContent = series.unit ? `${text} ${series.unit}` : text;
    }
  }
}

/** 渲染通道图例(色块 + 名称 + 最新值 + × 移除),复用主站 trend-chip 样式。 */
export function renderPlotLegend(host, store, onRemove) {
  if (!host) return;
  host.replaceChildren();
  for (const series of store.entries()) {
    const chip = document.createElement("span");
    chip.className = "trend-chip";
    chip.dataset.key = series.key;
    chip.title = series.rule ? "手动字节偏移通道" : "自动解析寄存器通道";
    const swatch = document.createElement("span");
    swatch.className = "trend-swatch";
    swatch.style.background = series.color;
    const label = document.createElement("span");
    label.className = "trend-chip-name";
    label.textContent = series.name;
    const value = document.createElement("span");
    value.className = "trend-chip-val";
    value.textContent = "—";
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "trend-remove";
    remove.textContent = "×";
    remove.setAttribute("aria-label", `移除通道 ${series.name}`);
    remove.addEventListener("click", () => onRemove?.(series.key));
    chip.append(swatch, label, value, remove);
    host.append(chip);
  }
}
