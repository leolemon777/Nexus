const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const root = path.resolve(__dirname, "..");

async function loadGuides() {
  return import(pathToFileURL(path.join(root, "src", "protocol-guides.js")).href);
}

const EXPECTED_VARIANTS = {
  master: ["rtu", "ascii", "tcp", "udp", "rtu-over-tcp", "ascii-over-tcp"],
  slave: ["tcp", "serial"],
  debug: ["serial"],
  melsec: ["3e", "4e", "ascii-3e", "ascii-4e", "mc-udp-3e", "mc-udp-4e", "mc-1e", "mc-c24", "fx-links", "fx-prog"],
  siemens: ["s7comm", "smart", "ppi", "ppi-serial", "fw", "uss", "rk512", "webapi"],
  omron: ["tcp", "udp", "hostlink-serial", "hostlink-fins-serial"],
  "allen-bradley": ["cip"],
  beckhoff: ["ads"],
  keyence: ["kv-host-link"],
  "ls-electric": ["xgt-fenet"],
  delta: ["dvp-modbus", "as-modbus"],
  inovance: ["h3u-modbus", "h5u-modbus"],
  xinje: ["xc-modbus", "xd-modbus"],
  fatek: ["ascii"],
  fuji: ["sph"],
  ge: ["srtp"],
  panasonic: ["mewtocol-com"],
  mqtt: ["mqtt-311"],
  iec: ["iec-60870-5-104"],
  dnp: ["dnp3-tcp"],
  dlt: ["dlt645-2007", "dlt645-1997"],
  cjt: ["cjt188-2004"],
  bacnet: ["bacnet-ip"],
  knx: ["tunneling-v1"],
};

test("protocol guide catalog covers every selectable communication variant", async () => {
  const { PROTOCOL_GUIDE_SOURCES, listProtocolGuideVariants, resolveProtocolGuide } = await loadGuides();
  assert.deepEqual(Object.keys(PROTOCOL_GUIDE_SOURCES), Object.keys(EXPECTED_VARIANTS));

  for (const [source, expected] of Object.entries(EXPECTED_VARIANTS)) {
    assert.deepEqual(listProtocolGuideVariants(source).map((entry) => entry.value), expected, source);
    for (const variant of expected) {
      const guide = resolveProtocolGuide(source, variant);
      assert.equal(guide.source, source);
      assert.equal(guide.variant, variant);
      assert.ok(["network", "serial"].includes(guide.medium), `${source}/${variant} medium`);
      assert.ok(guide.label.length > 2, `${source}/${variant} label`);
      assert.ok(guide.summary.length > 10, `${source}/${variant} summary`);
      assert.ok(guide.parameters.length >= 3, `${source}/${variant} parameters`);
      assert.ok(guide.deviceSteps.length >= 2, `${source}/${variant} device steps`);
      assert.ok(guide.pcSteps.length >= 3, `${source}/${variant} PC steps`);
      assert.ok(guide.checks.length >= 3, `${source}/${variant} checks`);
      assert.ok(guide.warnings.length >= 2, `${source}/${variant} warnings`);
    }
  }

  assert.equal(resolveProtocolGuide("missing", "tcp"), null);
  assert.equal(resolveProtocolGuide("master", "missing").variant, "rtu");
});

test("high-risk field distinctions are explicit in the guide content", async () => {
  const { resolveProtocolGuide } = await loadGuides();
  const a1e = JSON.stringify(resolveProtocolGuide("melsec", "mc-1e"));
  const c24 = JSON.stringify(resolveProtocolGuide("melsec", "mc-c24"));
  const smart = JSON.stringify(resolveProtocolGuide("siemens", "smart"));
  const ppi = JSON.stringify(resolveProtocolGuide("siemens", "ppi"));
  const uss = JSON.stringify(resolveProtocolGuide("siemens", "uss"));
  const slave = JSON.stringify(resolveProtocolGuide("slave", "tcp"));
  const iec104 = JSON.stringify(resolveProtocolGuide("iec", "iec-60870-5-104"));
  const dnp3 = JSON.stringify(resolveProtocolGuide("dnp", "dnp3-tcp"));
  const dlt2007 = JSON.stringify(resolveProtocolGuide("dlt", "dlt645-2007"));
  const dlt1997 = JSON.stringify(resolveProtocolGuide("dlt", "dlt645-1997"));
  assert.match(a1e, /FX3U/);
  assert.match(a1e, /不要选 3E/);
  assert.match(c24, /只读/);
  assert.match(smart, /Put\/Get Server/);
  assert.match(smart, /只读/);
  assert.match(ppi, /网关/);
  assert.match(uss, /只读/);
  assert.match(slave, /127\.0\.0\.1/);
  assert.match(iec104, /只读/);
  assert.match(iec104, /遥控/);
  assert.match(iec104, /ACT_TERM/);
  assert.match(dnp3, /只读/);
  assert.match(dnp3, /Select/);
  assert.match(dnp3, /Class 0/);
  assert.match(dnp3, /非商业\/非生产/);
  assert.match(dlt2007, /只读/);
  assert.match(dlt2007, /\+33H/);
  assert.match(dlt2007, /拉合闸/);
  assert.match(dlt1997, /已被 2007 全部代替/);
  assert.match(dlt1997, /01H\/81H\/A1H\/C1H/);
});

test("all communication pages are wired to one accessible shared dialog", () => {
  const html = fs.readFileSync(path.join(root, "index.html"), "utf8");
  const main = fs.readFileSync(path.join(root, "src", "main.js"), "utf8");
  const css = fs.readFileSync(path.join(root, "src", "app.css"), "utf8");

  for (const source of Object.keys(EXPECTED_VARIANTS)) {
    assert.match(html, new RegExp(`data-protocol-help=["']${source}["']`), source);
  }
  assert.equal((html.match(/id="protocol-guide-dialog"/g) || []).length, 1);
  assert.match(html, /aria-labelledby="protocol-guide-title"/);
  assert.match(html, /id="protocol-guide-variant"/);
  assert.match(main, /listProtocolGuideVariants/);
  assert.match(main, /resolveProtocolGuide/);
  assert.match(main, /initProtocolGuides\(\)/);
  assert.match(css, /\.protocol-guide-dialog::backdrop/);
  assert.match(css, /\.protocol-guide-grid/);
});
