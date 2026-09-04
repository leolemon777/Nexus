/**
 * 三菱软元件序列标签生成。
 *
 * FX/X/Y 为八进制编号(Y0~Y7 之后是 Y10,不存在 Y8/Y9);
 * D/M/T/S/C 等其余软元件为十进制编号。
 * 协议层(rust-core fx_prog_parse_number)对 X/Y 已按八进制解析编址,
 * 本模块负责把连续读回的位/字序列按同样进制生成显示标签。
 */

const OCTAL_DEVICES = new Set(["X", "Y"]);

/**
 * 生成从 startText 起连续 count 个软元件的显示标签。
 * @param {string} prefix 软元件字母(如 "Y"、"D";大小写不敏感)
 * @param {string} startText 起始编号(书写形式,与用户输入一致)
 * @param {number} count 数量
 * @returns {string[] | null} 标签数组;前缀非单字母或编号非法时返回 null
 */
export function formatDeviceSeries(prefix, startText, count) {
  const key = String(prefix ?? "").toUpperCase();
  if (!/^[A-Z]+$/.test(key)) return null;
  const isOctal = OCTAL_DEVICES.has(key);
  const radix = isOctal ? 8 : 10;
  const start = Number.parseInt(String(startText ?? "").trim(), radix);
  if (!Number.isInteger(start) || start < 0) return null;
  const labels = [];
  for (let i = 0; i < count; i++) labels.push(key + (start + i).toString(radix));
  return labels;
}
