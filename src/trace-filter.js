/**
 * 报文(报文面板)搜索过滤(纯逻辑,零 DOM/零 npm 依赖)。
 *
 * record 结构与 src/main.js 中 traceHistory 的记录保持一致:
 *   { timestamp, direction, unitId, functionCode, hex, crc, elapsedMs, result }
 *
 * 匹配语义:
 *   - 大小写不敏感
 *   - 任一字段(时间/方向/站号/FC/HEX/CRC/耗时/结果)包含 query 子串即命中
 *   - query 支持空格分隔多关键词,全部命中(AND)才算命中
 *   - 空/空白 query 视为"无过滤",返回全部
 */

/**
 * 判断单条报文是否匹配 query。
 * @param {object} record - 报文记录(字段可能缺失,做安全降级)
 * @param {string} query - 查询串(可含空格分隔的多关键词)
 * @returns {boolean}
 */
export function matchTrace(record, query) {
  if (query == null || String(query).trim() === "") return true;
  if (!record || typeof record !== "object") return false;

  // 把一条记录的所有可搜索字段拼成一坨文本,统一做小写包含判断。
  // timestamp 转字符串(数字时间戳或已格式化字符串都可),数字字段也转字符串。
  const haystack = [
    record.timestamp,
    record.direction,
    record.unitId,
    record.functionCode != null ? String(record.functionCode).padStart(2, "0") : "",
    record.functionCode, // 同时收录不带前缀的形式,方便用户按 "3" 命中 FC03
    record.hex,
    record.crc,
    record.elapsedMs != null ? `${record.elapsedMs}` : "",
    record.elapsedMs != null ? `${record.elapsedMs} ms` : "",
    record.result,
  ]
    .map((v) => (v == null ? "" : String(v)))
    .join("\u0001")
    .toLowerCase();

  // 空白拆分多关键词,AND 语义
  const keywords = String(query)
    .toLowerCase()
    .split(/\s+/)
    .filter((k) => k.length > 0);
  return keywords.every((k) => haystack.includes(k));
}

/**
 * 对报文记录数组做过滤。
 * @param {Array} records - 报文记录数组
 * @param {string} query - 查询串
 * @returns {Array} 新数组(不修改原数组),保持原相对顺序
 */
export function filterTrace(records, query) {
  if (!Array.isArray(records)) return [];
  if (query == null || String(query).trim() === "") return [...records];
  return records.filter((r) => matchTrace(r, query));
}
