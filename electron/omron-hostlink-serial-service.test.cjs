const test = require("node:test");
const assert = require("node:assert/strict");
const {
  createOmronHostLinkSerialService,
  parseDmAddress,
  parseFinsWordAddress,
  parseResponseStation,
  parseFinsResponseStation,
} = require("./omron-hostlink-serial-service.cjs");

function responseFrame(station = "00", words = [0x1234, 0xABCD]) {
  const body = Buffer.from(`@${station}RR00${words.map((word) => word.toString(16).padStart(4, "0").toUpperCase()).join("")}`, "ascii");
  let fcs = 0;
  for (const byte of body) fcs ^= byte;
  return [...body, ...Buffer.from(fcs.toString(16).padStart(2, "0").toUpperCase(), "ascii"), 0x2A, 0x0D, 0x0A];
}

function finsResponseFrame(station = "00", data = [0x12, 0x34, 0xAB, 0xCD]) {
  const fins = [0xC0, 0x00, 0x02, 0, 0, 0, 0, 0, 0, 1, 0x01, 0x01, 0x00, 0x00, ...data];
  const body = Buffer.from(`@${station}FA${fins.map((byte) => byte.toString(16).padStart(2, "0").toUpperCase()).join("")}`, "ascii");
  let fcs = 0;
  for (const byte of body) fcs ^= byte;
  return [...body, ...Buffer.from(fcs.toString(16).padStart(2, "0").toUpperCase(), "ascii"), 0x2A, 0x0D, 0x0A];
}

test("executes a read-only HostLink C-mode DM transaction", async () => {
  const calls = [];
  const service = createOmronHostLinkSerialService({
    request: async (command, payload) => {
      calls.push({ command, payload });
      if (command === "hostlink_build_cmode_read") return { frame: [...Buffer.from("@00RR00640002", "ascii"), 0x00, 0x2A, 0x0D, 0x0A] };
      return { words: [0x1234, 0xABCD] };
    },
    transact: async ({ request, framing }) => ({ request, tx: request, rx: responseFrame(), elapsedMs: 7, framing }),
    getSerialStatus: () => ({ isOpen: true }),
  });
  const result = await service.read({ station: 0, address: "D100", count: 2, timeoutMs: 100 });
  assert.equal(result.protocol, "omron-hostlink-cmode-serial");
  assert.deepEqual(result.words, [0x1234, 0xABCD]);
  assert.equal(result.readOnly, true);
  assert.deepEqual(calls.map((call) => call.command), ["hostlink_build_cmode_read", "hostlink_parse_cmode_read"]);
  assert.deepEqual(calls[0].payload, { station: 0, dmStart: 100, wordCount: 2 });
});

test("fails closed when COM is not open or address/count is unsafe", async () => {
  const service = createOmronHostLinkSerialService({
    request: async () => ({ frame: [0x40] }),
    transact: async () => ({ rx: [] }),
    getSerialStatus: () => ({ isOpen: false }),
  });
  await assert.rejects(() => service.read({ address: "D100" }), { code: "SERIAL_NOT_OPEN" });
  assert.throws(() => parseDmAddress("CIO0"), { code: "OMRON_HOSTLINK_PARAM_INVALID" });
  assert.throws(() => parseResponseStation(Buffer.from("@ZZRR", "ascii")), { code: "OMRON_HOSTLINK_RESPONSE_INVALID" });
  assert.throws(() => parseResponseStation(Buffer.from("@0GRR", "ascii")), { code: "OMRON_HOSTLINK_RESPONSE_INVALID" });
  assert.throws(() => parseResponseStation(Buffer.from("@FFRR", "ascii")), { code: "OMRON_HOSTLINK_RESPONSE_INVALID" });
  const openService = createOmronHostLinkSerialService({
    request: async () => ({ frame: [0x40] }),
    transact: async () => ({ rx: [] }),
    getSerialStatus: () => ({ isOpen: true }),
  });
  await assert.rejects(() => openService.read({ address: "D65535", count: 2 }), { code: "OMRON_HOSTLINK_PARAM_INVALID" });
});

test("rejects a response from another HostLink station", async () => {
  const service = createOmronHostLinkSerialService({
    request: async (command) => command === "hostlink_build_cmode_read"
      ? { frame: [...Buffer.from("@01RR00000001", "ascii"), 0x00, 0x2A, 0x0D, 0x0A] }
      : { words: [1] },
    transact: async () => ({ tx: [], rx: responseFrame("01", [1]) }),
    getSerialStatus: () => ({ isOpen: true }),
  });
  await assert.rejects(() => service.read({ station: 0, address: "D0", count: 1 }), { code: "OMRON_HOSTLINK_STATION_CONFLICT" });
});

test("executes a read-only HostLink FINS word transaction", async () => {
  const calls = [];
  const service = createOmronHostLinkSerialService({
    request: async (command, payload) => {
      calls.push({ command, payload });
      if (command === "hostlink_build_fins") return { frame: [...Buffer.from("@00FA", "ascii"), 0x2A, 0x0D, 0x0A] };
      return { sid: 1, endCode: 0, data: [0x12, 0x34, 0xAB, 0xCD] };
    },
    transact: async ({ request }) => ({ tx: request, rx: finsResponseFrame(), elapsedMs: 8 }),
    getSerialStatus: () => ({ isOpen: true }),
  });
  const result = await service.readFins({ station: 0, address: "D100", count: 2, timeoutMs: 100 });
  assert.equal(result.protocol, "omron-hostlink-fins-serial");
  assert.deepEqual(result.values, [0x1234, 0xABCD]);
  assert.deepEqual(calls[0].payload, { station: 0, area: "DM", byte: 100, count: 2 });
  assert.deepEqual(calls.map((call) => call.command), ["hostlink_build_fins", "hostlink_parse_fins"]);
});

test("validates HostLink FINS word addresses and headers", () => {
  assert.deepEqual(parseFinsWordAddress(" cio10 "), { area: "CIO", byte: 10, address: "CIO10" });
  assert.throws(() => parseFinsWordAddress("D10.1"), { code: "OMRON_HOSTLINK_PARAM_INVALID" });
  assert.equal(parseFinsResponseStation(Buffer.from("@00FA", "ascii")), 0);
  assert.throws(() => parseFinsResponseStation(Buffer.from("@0GFA", "ascii")), { code: "OMRON_HOSTLINK_RESPONSE_INVALID" });
  assert.throws(() => parseFinsResponseStation(Buffer.from("@FFFA", "ascii")), { code: "OMRON_HOSTLINK_RESPONSE_INVALID" });
});

test("fails closed on HostLink FINS device errors and short data", async () => {
  const makeService = (parsed) => createOmronHostLinkSerialService({
    request: async (command) => command === "hostlink_build_fins"
      ? { frame: [...Buffer.from("@00FA", "ascii"), 0x2A, 0x0D, 0x0A] }
      : parsed,
    transact: async ({ request }) => ({ tx: request, rx: finsResponseFrame("00", [0x12, 0x34]) }),
    getSerialStatus: () => ({ isOpen: true }),
  });
  await assert.rejects(() => makeService({ sid: 1, endCode: 0x0201, data: [0x12, 0x34] }).readFins({ count: 1 }), { code: "OMRON_HOSTLINK_DEVICE_ERROR" });
  await assert.rejects(() => makeService({ sid: 1, endCode: 0, data: [] }).readFins({ count: 1 }), { code: "OMRON_HOSTLINK_LENGTH_MISMATCH" });
  await assert.rejects(() => makeService({ sid: 2, endCode: 0, data: [0x12, 0x34] }).readFins({ count: 1 }), { code: "OMRON_HOSTLINK_SID_MISMATCH" });
  await assert.rejects(() => makeService({ sid: 1, endCode: 0, data: [0x12, 0x34, 0x56, 0x78] }).readFins({ address: "D16777215", count: 2 }), { code: "OMRON_HOSTLINK_PARAM_INVALID" });
});
