/**
 * 轮询计划优化器(纯逻辑,零 DOM/零 npm 依赖)。
 *
 * 把逐点读取的点表合并为"同站号 + 同功能码 + 地址连续(或间隙 ≤ maxGap)"
 * 的批量读取批次,从而把 N 次事务压缩为尽可能少的大块读事务。
 *
 * 合并规则(详见 buildPollPlan 注释):
 *   1. 只有 unitId 与 fc 都相同的点位才能进同一批
 *   2. 地址区间连续或间隙 ≤ maxGap 才合并,合并后批次覆盖 [minStart, maxEnd)
 *   3. 单次读取数量受 Modbus 协议硬上限约束:
 *        - FC03/04(寄存器) quantity ≤ 125
 *        - FC01/02(线圈/离散输入) quantity ≤ 2000
 *      超出上限必须拆批(单个点位 quantity 超限时同样拆)
 *   4. FC05/06/15/16 是写功能码,不属于轮询范围,本模块直接忽略
 *      (理由:轮询只读不写;若抛错会让点表里混入一个写点位就整表停摆,
 *       忽略更安全且对设计者更友好)
 *   5. pointIndexes 记录该批次覆盖的原 points 数组索引,供调用方拆分回写
 */

/** 写功能码集合(不在轮询范围内,会被忽略) */
const WRITE_FCS = new Set([5, 6, 15, 16]);

/** 线圈/离散输入类功能码 */
const COIL_FCS = new Set([1, 2]);

/**
 * 返回该功能码单次读取的硬上限。
 * 已知读功能码:1/2 上限 2000,3/4 上限 125;
 * 其他(理论上不应出现在轮询点表里)按最保守的 125 处理。
 */
function readCap(fc) {
  return COIL_FCS.has(fc) ? 2000 : 125;
}

/**
 * 把点位列表合并成最优读取计划。
 * @param {Array} points - [{unitId, fc, address, quantity, dataType?, scale?, unit?, name?}]
 * @param {object} [opts]
 * @param {number} [opts.maxGap=0] 允许的最大地址间隙(超出则拆批)
 * @returns {Array} batches - [{unitId, fc, startAddress, quantity, pointIndexes:[...]}]
 */
export function buildPollPlan(points, opts = {}) {
  if (!Array.isArray(points) || points.length === 0) return [];

  const maxGap = Number.isFinite(opts.maxGap) ? Number(opts.maxGap) : 0;

  // 1. 规整化并过滤:写功能码 / 非法数量直接丢弃
  const segments = [];
  for (let i = 0; i < points.length; i++) {
    const p = points[i] || {};
    const fc = Number(p.fc);
    if (WRITE_FCS.has(fc)) continue; // 写功能码忽略
    const start = Number(p.address);
    const qty = Number(p.quantity);
    if (!Number.isFinite(start) || !Number.isFinite(qty) || qty <= 0) continue;
    segments.push({
      pointIndex: i,
      unitId: Number(p.unitId),
      fc,
      start,
      endExclusive: start + qty,
    });
  }
  if (segments.length === 0) return [];

  // 2. 按 (unitId, fc) 分组(用 \u0000 分隔,避免与数字字符冲突)
  const groups = new Map();
  for (const seg of segments) {
    const key = `${seg.unitId}\u0000${seg.fc}`;
    const arr = groups.get(key);
    if (arr) arr.push(seg);
    else groups.set(key, [seg]);
  }

  const batches = [];

  // 3. 每组内按地址排序后贪心合并,再按 cap 切块
  for (const segs of groups.values()) {
    segs.sort((a, b) => a.start - b.start || a.endExclusive - b.endExclusive);
    const cap = readCap(segs[0].fc);

    /** @type {{unitId:number,fc:number,start:number,endExclusive:number,segments:object[]}|null} */
    let current = null;

    const flush = () => {
      if (!current) return;
      // 4. 按 cap 切块:cursor 从 start 走到 endExclusive,每块 ≤ cap
      let cursor = current.start;
      while (cursor < current.endExclusive) {
        const chunkEnd = Math.min(cursor + cap, current.endExclusive);
        // 落在该块区间内的点位(部分跨界点位会同时出现在相邻块)
        const overlapping = current.segments.filter(
          (s) => s.endExclusive > cursor && s.start < chunkEnd,
        );
        batches.push({
          unitId: current.unitId,
          fc: current.fc,
          startAddress: cursor,
          quantity: chunkEnd - cursor,
          pointIndexes: overlapping.map((s) => s.pointIndex),
        });
        cursor = chunkEnd;
      }
      current = null;
    };

    for (const seg of segs) {
      if (!current) {
        current = {
          unitId: seg.unitId,
          fc: seg.fc,
          start: seg.start,
          endExclusive: seg.endExclusive,
          segments: [seg],
        };
        continue;
      }
      const gap = seg.start - current.endExclusive;
      const newEnd = Math.max(current.endExclusive, seg.endExclusive);
      const newQty = newEnd - current.start;
      // 间隙够小且合并后不超 cap 才合并;否则先把当前批落盘
      if (gap <= maxGap && newQty <= cap) {
        current.endExclusive = newEnd;
        current.segments.push(seg);
      } else {
        flush();
        current = {
          unitId: seg.unitId,
          fc: seg.fc,
          start: seg.start,
          endExclusive: seg.endExclusive,
          segments: [seg],
        };
      }
    }
    flush();
  }

  return batches;
}

/**
 * 把一次批量读取的返回值数组按 pointIndexes 拆回每个点位的值切片。
 *
 * @param {{startAddress:number, pointIndexes:number[]}} batch - 单个批次(含起始地址与覆盖的点位索引)
 * @param {Array} points - 原 points 数组(用于查每个点位的 address/quantity)
 * @param {Array} values - 批次读取返回的值数组(长度应等于 batch.quantity)
 * @returns {Map<number, Array>} pointIndex -> 该点位对应的值切片
 *   间隙处读取的填充值会被自动跳过(切片基于绝对地址偏移计算)
 */
export function splitBatchResult(batch, points, values) {
  const result = new Map();
  const startAddr = Number(batch.startAddress);
  const arr = Array.isArray(values) ? values : [];
  for (const idx of batch.pointIndexes ?? []) {
    const p = points[idx];
    if (!p) continue;
    const offset = Number(p.address) - startAddr;
    const qty = Number(p.quantity);
    if (!Number.isFinite(offset) || !Number.isFinite(qty)) continue;
    result.set(idx, arr.slice(offset, offset + qty));
  }
  return result;
}
