/**
 * 收发记录条件着色规则 —— 纯逻辑模块(spec B.9)。
 * 求值在渲染层(展示关注点),规则数据随 .nexus.json workspace.colorRules 持久化。
 *
 * 规则: { source: "direction"|"length"|"field", field, op, value, color }
 *   - direction: 值 TX/RX,仅支持 ==/!=
 *   - length:    帧字节数,数值比较
 *   - field:     当前已应用帧定义的解析字段值(field=字段名),数值比较
 * 规则按序求值,首条命中即返回其颜色;未命中返回 null。
 */

export const COLOR_RULE_SOURCES = ["direction", "length", "field"];
export const COLOR_RULE_OPS = ["==", "!=", ">", ">=", "<", "<="];
export const COLOR_RULE_DIRECTION_OPS = ["==", "!="];
const COLOR_RE = /^#[0-9a-fA-F]{6}$/;

/** 校验单条规则;合法返回 null,非法返回中文错误说明。 */
export function validateColorRule(rule) {
  if (!rule || typeof rule !== "object") return "规则必须是对象";
  if (!COLOR_RULE_SOURCES.includes(rule.source)) return `来源非法: ${rule.source}`;
  if (!COLOR_RULE_OPS.includes(rule.op)) return `条件非法: ${rule.op}`;
  if (!COLOR_RE.test(String(rule.color ?? ""))) return `颜色必须是 #RRGGBB: ${rule.color}`;
  if (rule.source === "direction") {
    if (!COLOR_RULE_DIRECTION_OPS.includes(rule.op)) return "方向只支持 == / !=";
    const value = String(rule.value ?? "").trim().toUpperCase();
    if (value !== "TX" && value !== "RX") return "方向的值只能是 TX 或 RX";
    return null;
  }
  if (rule.source === "field") {
    const name = String(rule.field ?? "").trim();
    if (!name) return "字段来源必须指定字段名";
    if (name.length > 50) return "字段名过长";
  }
  if (!Number.isFinite(Number(rule.value))) return `值必须是数字: ${rule.value}`;
  return null;
}

function compare(op, left, right) {
  switch (op) {
    case "==": return left === right;
    case "!=": return left !== right;
    case ">": return left > right;
    case ">=": return left >= right;
    case "<": return left < right;
    case "<=": return left <= right;
    default: return false;
  }
}

/**
 * 对一条记录求规则颜色。
 * @param {Array} rules 规则列表(顺序即优先级)
 * @param {{direction:"TX"|"RX", length:number, fields?:Array<{name:string,value:number}>|null}} ctx 记录上下文
 * @returns {string|null} 命中规则的 #RRGGBB,未命中 null
 */
export function evaluateColorRules(rules, ctx) {
  if (!Array.isArray(rules) || !ctx) return null;
  for (const rule of rules) {
    if (validateColorRule(rule) !== null) continue; // 坏规则跳过,不中断其余规则
    let hit = false;
    if (rule.source === "direction") {
      const target = String(rule.value).trim().toUpperCase();
      hit = compare(rule.op, String(ctx.direction ?? "").toUpperCase(), target);
    } else if (rule.source === "length") {
      hit = Number.isFinite(ctx.length) && compare(rule.op, ctx.length, Number(rule.value));
    } else if (rule.source === "field") {
      const field = (ctx.fields ?? []).find((f) => f && f.name === String(rule.field).trim());
      // 解析结果未回填(该帧不是目标帧/尚未解析)时,字段规则不参与
      if (field && Number.isFinite(Number(field.value))) {
        hit = compare(rule.op, Number(field.value), Number(rule.value));
      }
    }
    if (hit) return rule.color;
  }
  return null;
}
