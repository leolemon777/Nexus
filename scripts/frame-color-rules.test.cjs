// 收发记录条件着色规则纯逻辑单测(spec B.9)。
const test = require("node:test");
const assert = require("node:assert");

const modPromise = import("../src/frame-color-rules.js");

const BASE_CTX = { direction: "RX", length: 8, fields: [{ name: "temp", value: 25.1 }, { name: "flag", value: 1 }] };

test("validateColorRule accepts well-formed rules and rejects malformed ones", async () => {
  const { validateColorRule } = await modPromise;
  assert.equal(validateColorRule({ source: "direction", op: "==", value: "rx", color: "#D52B1E" }), null);
  assert.equal(validateColorRule({ source: "length", op: ">", value: "64", color: "#1A7F45" }), null);
  assert.equal(validateColorRule({ source: "field", field: "temp", op: ">=", value: 50, color: "#D52B1E" }), null);

  assert.match(validateColorRule(null), /对象/);
  assert.match(validateColorRule({ source: "hex", op: "==", value: "AA", color: "#111111" }), /来源非法/);
  assert.match(validateColorRule({ source: "direction", op: ">", value: "RX", color: "#111111" }), /== \/ !=/);
  assert.match(validateColorRule({ source: "direction", op: "==", value: "xx", color: "#111111" }), /TX 或 RX/);
  assert.match(validateColorRule({ source: "length", op: ">", value: "abc", color: "#111111" }), /数字/);
  assert.match(validateColorRule({ source: "field", field: "", op: ">", value: 1, color: "#111111" }), /字段名/);
  assert.match(validateColorRule({ source: "length", op: ">", value: 8, color: "red" }), /#RRGGBB/);
  assert.match(validateColorRule({ source: "length", op: "~", value: 8, color: "#111111" }), /条件非法/);
});

test("evaluateColorRules matches direction, length and field contexts in order", async () => {
  const { evaluateColorRules } = await modPromise;
  const rules = [
    { source: "direction", op: "==", value: "TX", color: "#555555" },
    { source: "field", field: "temp", op: ">", value: 50, color: "#D52B1E" },
    { source: "length", op: "==", value: 8, color: "#3366AA" },
  ];
  // TX 优先命中第一条
  assert.equal(evaluateColorRules(rules, { ...BASE_CTX, direction: "TX" }), "#555555");
  // RX + temp 25.1 不超 50 → 落到 length == 8
  assert.equal(evaluateColorRules(rules, BASE_CTX), "#3366AA");
  // temp 超阈值命中第二条
  assert.equal(
    evaluateColorRules(rules, { ...BASE_CTX, fields: [{ name: "temp", value: 80 }] }),
    "#D52B1E",
  );
  // 都不命中 → null
  assert.equal(evaluateColorRules(rules, { ...BASE_CTX, length: 9 }), null);
  // 大小写不敏感的方向比较
  assert.equal(
    evaluateColorRules([{ source: "direction", op: "!=", value: "tx", color: "#111111" }], BASE_CTX),
    "#111111",
  );
});

test("evaluateColorRules skips field rules until parse results arrive and tolerates bad rules", async () => {
  const { evaluateColorRules, COLOR_RULE_OPS } = await modPromise;
  const fieldRule = { source: "field", field: "temp", op: ">", value: 10, color: "#D52B1E" };
  // fields 为 null(解析未回填):不命中,返回 null
  assert.equal(evaluateColorRules([fieldRule], { ...BASE_CTX, fields: null }), null);
  // 字段不存在:跳过
  assert.equal(
    evaluateColorRules([fieldRule], { ...BASE_CTX, fields: [{ name: "other", value: 99 }] }),
    null,
  );
  // 坏规则被跳过,不影响后面的好规则
  const rules = [
    { source: "length", op: ">", value: "abc", color: "#111111" },
    { source: "length", op: "<", value: 100, color: "#1A7F45" },
  ];
  assert.equal(evaluateColorRules(rules, BASE_CTX), "#1A7F45");
  // 全坏规则 / 空列表 / 空 ctx
  assert.equal(evaluateColorRules([], BASE_CTX), null);
  assert.equal(evaluateColorRules(rules, null), null);
  // 非数值字段值不参与
  assert.equal(
    evaluateColorRules([fieldRule], { ...BASE_CTX, fields: [{ name: "temp", value: Number.NaN }] }),
    null,
  );
  assert.ok(COLOR_RULE_OPS.length === 6);
});
