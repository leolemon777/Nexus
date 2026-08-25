const test = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");
const { pathToFileURL } = require("node:url");

const registryUrl = pathToFileURL(
  path.join(__dirname, "..", "src", "protocol-registry.js"),
).href;
const guidesUrl = pathToFileURL(
  path.join(__dirname, "..", "src", "protocol-guides.js"),
).href;

const html = require("node:fs").readFileSync(
  path.join(__dirname, "..", "index.html"),
  "utf8",
);

function selectBlock(id) {
  const match = html.match(new RegExp(`<select\\b[^>]*\\bid=["']${id}["'][^>]*>([\\s\\S]*?)</select>`, "i"));
  assert.ok(match, `index.html 缺少协议选择器 #${id}`);
  return match[1];
}

function optionEntries(markup) {
  return [...markup.matchAll(/<option\b([^>]*)>/gi)]
    .map((match) => {
      const attrs = match[1];
      const value = attrs.match(/\bvalue=["']([^"']+)["']/i)?.[1];
      return value ? { value, disabled: /\bdisabled(?:\s|=|$)/i.test(attrs) } : null;
    })
    .filter(Boolean);
}

function radioEntries(name) {
  return [...html.matchAll(/<input\b([^>]*)>/gi)]
    .map((match) => {
      const attrs = match[1];
      if (attrs.match(/\bname=["']([^"']+)["']/i)?.[1] !== name) return null;
      const value = attrs.match(/\bvalue=["']([^"']+)["']/i)?.[1];
      return value ? { value, disabled: /\bdisabled(?:\s|=|$)/i.test(attrs) } : null;
    })
    .filter(Boolean);
}

test("registry contains the 53 current selectable protocol variants", async () => {
  const { PROTOCOL_REGISTRY, listProtocolSources } = await import(registryUrl);
  assert.equal(PROTOCOL_REGISTRY.length, 53);
  assert.deepEqual(listProtocolSources(), ["master", "slave", "debug", "melsec", "siemens", "omron", "allen-bradley", "beckhoff", "keyence", "ls-electric", "panasonic", "delta", "inovance", "xinje", "fatek", "fuji", "ge", "mqtt", "iec", "dnp", "dlt", "cjt", "bacnet", "knx"]);
  for (const entry of PROTOCOL_REGISTRY) {
    assert.match(entry.id, /^[a-z-]+\/[a-z0-9-]+$/);
    assert.ok(entry.label.length > 2);
    assert.ok(entry.family.length > 1);
    assert.ok(entry.commandFamily.length > 1);
    assert.ok(entry.roles.length > 0);
    assert.ok(entry.capabilities.length > 0);
    assert.match(entry.evidence, /L2 pending/);
    assert.match(entry.state, /^(implemented|offline-codec|online-readonly|online-readwrite|hardware-pending|blocked)$/);
  }
});

test("protocol identifiers stay separate from display labels", async () => {
  const { PROTOCOL_REGISTRY } = await import(registryUrl);
  const { listProtocolGuideVariants } = await import(guidesUrl);
  const ids = new Set(PROTOCOL_REGISTRY.map((entry) => entry.id));
  const labels = new Set(PROTOCOL_REGISTRY.map((entry) => entry.label));

  assert.equal(ids.size, PROTOCOL_REGISTRY.length);
  assert.equal(labels.size, PROTOCOL_REGISTRY.length);
  for (const entry of PROTOCOL_REGISTRY) {
    assert.notEqual(entry.id, entry.label);
    assert.notEqual(entry.source, entry.label);
    assert.notEqual(entry.variant, entry.label);
    assert.match(entry.id, /^[a-z0-9]+(?:-[a-z0-9]+)*\/[a-z0-9]+(?:-[a-z0-9]+)*$/);
  }

  for (const source of ["master", "melsec", "siemens", "omron", "delta", "mqtt", "bacnet", "knx"]) {
    for (const entry of listProtocolGuideVariants(source)) {
      assert.match(entry.value, /^[a-z0-9-]+$/);
      assert.notEqual(entry.value, entry.label);
      assert.ok(ids.has(`${source}/${entry.value}`));
    }
  }
});

test("registry and connection help expose the same variant set", async () => {
  const { listProtocolVariants } = await import(registryUrl);
  const { listProtocolGuideVariants } = await import(guidesUrl);
  for (const source of ["master", "slave", "debug", "melsec", "siemens", "omron", "allen-bradley", "beckhoff", "keyence", "ls-electric", "panasonic", "delta", "inovance", "xinje", "fatek", "fuji", "ge", "mqtt", "iec", "dnp", "dlt", "cjt", "bacnet", "knx"]) {
    assert.deepEqual(
      listProtocolGuideVariants(source).map((entry) => entry.value),
      listProtocolVariants(source).map((entry) => entry.variant),
      source,
    );
  }
});

test("registered protocol variants are the selectable variants rendered by index.html", async () => {
  const { listProtocolVariants } = await import(registryUrl);
  const selectors = {
    master: radioEntries("transport"),
    slave: optionEntries(selectBlock("slave-mode")),
    melsec: optionEntries(selectBlock("mc-frame-type")),
    siemens: optionEntries(selectBlock("s7-variant")),
    omron: optionEntries(selectBlock("om-transport")),
  };

  for (const [source, entries] of Object.entries(selectors)) {
    const registered = listProtocolVariants(source).map((entry) => entry.variant);
    assert.deepEqual(
      entries.filter((entry) => !entry.disabled).map((entry) => entry.value).sort(),
      [...registered].sort(),
      source,
    );
    assert.equal(new Set(entries.map((entry) => entry.value)).size, entries.length, `${source} selector duplicate`);
    assert.ok(entries.every((entry) => !entry.disabled), `${source} registered option is disabled`);
  }

  assert.match(html, /data-protocol-help=["']allen-bradley["']/);
  assert.match(html, /id=["']allen-bradley-view["']/);
  assert.equal(listProtocolVariants("allen-bradley").map((entry) => entry.variant).join(","), "cip");
  assert.match(html, /data-protocol-help=["']beckhoff["']/);
  assert.match(html, /id=["']beckhoff-view["']/);
  assert.equal(listProtocolVariants("beckhoff").map((entry) => entry.variant).join(","), "ads");
  assert.match(html, /data-protocol-help=["']keyence["']/);
  assert.match(html, /id=["']keyence-view["']/);
  assert.equal(listProtocolVariants("keyence").map((entry) => entry.variant).join(","), "kv-host-link");
  assert.match(html, /data-protocol-help=["']ls-electric["']/);
  assert.match(html, /id=["']ls-electric-view["']/);
  assert.equal(listProtocolVariants("ls-electric").map((entry) => entry.variant).join(","), "xgt-fenet");
  assert.match(html, /data-protocol-help=["']panasonic["']/);
  assert.match(html, /id=["']panasonic-view["']/);
  assert.equal(listProtocolVariants("panasonic").map((entry) => entry.variant).join(","), "mewtocol-com");
  assert.match(html, /data-protocol-help=["']delta["']/);
  assert.match(html, /id=["']delta-view["']/);
  assert.equal(listProtocolVariants("delta").map((entry) => entry.variant).join(","), "dvp-modbus,as-modbus");
  assert.match(html, /data-protocol-help=["']inovance["']/);
  assert.match(html, /id=["']inovance-view["']/);
  assert.equal(listProtocolVariants("inovance").map((entry) => entry.variant).join(","), "h3u-modbus,h5u-modbus");
  assert.match(html, /data-protocol-help=["']xinje["']/);
  assert.match(html, /id=["']xinje-view["']/);
  assert.equal(listProtocolVariants("xinje").map((entry) => entry.variant).join(","), "xc-modbus,xd-modbus");
  assert.match(html, /data-protocol-help=["']fatek["']/);
  assert.match(html, /id=["']fatek-view["']/);
  assert.equal(listProtocolVariants("fatek").map((entry) => entry.variant).join(","), "ascii");
  assert.match(html, /data-protocol-help=["']fuji["']/);
  assert.match(html, /id=["']fuji-view["']/);
  assert.equal(listProtocolVariants("fuji").map((entry) => entry.variant).join(","), "sph");
  assert.match(html, /data-protocol-help=["']ge["']/);
  assert.match(html, /id=["']ge-view["']/);
  assert.equal(listProtocolVariants("ge").map((entry) => entry.variant).join(","), "srtp");
  assert.match(html, /data-protocol-help=["']mqtt["']/);
  assert.match(html, /id=["']mqtt-view["']/);
  assert.equal(listProtocolVariants("mqtt").map((entry) => entry.variant).join(","), "mqtt-311");
  assert.match(html, /data-protocol-help=["']iec["']/);
  assert.match(html, /id=["']iec104-view["']/);
  assert.equal(listProtocolVariants("iec").map((entry) => entry.variant).join(","), "iec-60870-5-104");
  assert.match(html, /data-protocol-help=["']dnp["']/);
  assert.match(html, /id=["']dnp3-view["']/);
  assert.equal(listProtocolVariants("dnp").map((entry) => entry.variant).join(","), "dnp3-tcp");
  assert.match(html, /data-protocol-help=["']dlt["']/);
  assert.match(html, /id=["']dlt645-view["']/);
  assert.equal(listProtocolVariants("dlt").map((entry) => entry.variant).join(","), "dlt645-2007,dlt645-1997");
  assert.match(html, /data-protocol-help=["']cjt["']/);
  assert.match(html, /id=["']cjt188-view["']/);
  assert.equal(listProtocolVariants("cjt").map((entry) => entry.variant).join(","), "cjt188-2004");
  assert.match(html, /data-protocol-help=["']bacnet["']/);
  assert.match(html, /id=["']bacnet-view["']/);
  assert.equal(listProtocolVariants("bacnet").map((entry) => entry.variant).join(","), "bacnet-ip");
  assert.match(html, /data-protocol-help=["']knx["']/);
  assert.match(html, /id=["']knx-view["']/);
  assert.equal(listProtocolVariants("knx").map((entry) => entry.variant).join(","), "tunneling-v1");

  // 串口调试只有一个固定模式，页面没有第二个变体选择器；帮助入口仍必须存在。
  assert.match(html, /data-protocol-help=["']debug["']/);
  assert.match(html, /id=["']debug-view["']/);
  assert.equal(listProtocolVariants("debug").map((entry) => entry.variant).join(","), "serial");

  const omPoints = html.match(/<input\b[^>]*\bid=["']om-points["'][^>]*>/i)?.[0] || "";
  assert.match(omPoints, /\bmin=["']1["']/i);
  assert.match(omPoints, /\bmax=["']512["']/i);
  assert.match(omPoints, /HostLink FINS\/C-mode 首轮上限 100 点/);
});

test("unknown protocol or variant is not silently assigned a capability descriptor", async () => {
  const { getProtocolDescriptor } = await import(registryUrl);
  assert.equal(getProtocolDescriptor("missing", "tcp"), null);
  assert.equal(getProtocolDescriptor("siemens", "missing"), null);
});

test("three-family registry states keep C24 and serial Siemens read-only", async () => {
  const { getProtocolDescriptor } = await import(registryUrl);
  const c24 = getProtocolDescriptor("melsec", "mc-c24");
  assert.equal(c24.state, "online-readonly");
  assert.ok(!c24.capabilities.includes("write"));
  assert.equal(c24.safety, "read-only");

  for (const variant of ["ppi-serial", "uss", "rk512"]) {
    const entry = getProtocolDescriptor("siemens", variant);
    assert.equal(entry.state, "online-readonly");
    assert.ok(!entry.capabilities.includes("write"));
  }

  const modbusTcp = getProtocolDescriptor("master", "tcp");
  assert.equal(modbusTcp.state, "online-readwrite");
  const s7 = getProtocolDescriptor("siemens", "s7comm");
  assert.equal(s7.state, "online-readwrite");
  const mc3e = getProtocolDescriptor("melsec", "3e");
  assert.equal(mc3e.state, "online-readwrite");
});
