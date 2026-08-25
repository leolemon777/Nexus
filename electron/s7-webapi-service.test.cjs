const test = require("node:test");
const assert = require("node:assert/strict");
const {
  createS7WebApiService,
  normalizeEndpoint,
  normalizeMaxResponseBytes,
  normalizeVariableName,
} = require("./s7-webapi-service.cjs");

function response(body, overrides = {}) {
  return { ok: true, status: 200, async json() { return body; }, ...overrides };
}

test("validates Web API endpoint and rejects URL/path injection", () => {
  assert.deepEqual(normalizeEndpoint({ host: "192.168.1.20" }), {
    host: "192.168.1.20", port: 443, baseUrl: "https://192.168.1.20:443",
  });
  assert.throws(() => normalizeEndpoint({ host: "https://user:pass@127.0.0.1/x" }), /纯 IP\/主机名/);
  assert.throws(() => normalizeEndpoint({ host: "127.0.0.1", port: 0 }), /端口/);
});

test("keeps credentials out of returned connection metadata and uses token for RPC", async () => {
  const calls = [];
  const service = createS7WebApiService({
    now: () => 1000,
    fetchImpl: async (url, init) => {
      calls.push({ url, init });
      const body = JSON.parse(init.body);
      if (body.method === "Api.Login") return response({ result: { token: "secret-token-123" } });
      if (body.method === "Api.Logout") return response({ result: {} });
      if (body.method === "PlcProgram.Read") return response({ result: { value: 42 } });
      throw new Error("unexpected method");
    },
  });

  const connected = await service.connect({ host: "127.0.0.1", user: "operator", password: "dont-log-me" });
  assert.deepEqual(connected, { token: "secret-t…", host: "127.0.0.1", port: 443 });
  assert.equal(JSON.stringify(connected).includes("dont-log-me"), false);
  assert.deepEqual(await service.readVariable("DB1.Value"), { value: 42 });
  const read = calls.find((call) => JSON.parse(call.init.body).method === "PlcProgram.Read");
  assert.equal(read.init.headers["X-Auth-Token"], "secret-token-123");
  await service.disconnect();
  assert.equal(service.isConnected(), false);
});

test("fails closed on invalid login payloads and empty variable names", async () => {
  const service = createS7WebApiService({ fetchImpl: async () => response({ result: { token: "short" } }) });
  await assert.rejects(service.connect({ host: "127.0.0.1", user: "", password: "x" }), (error) => error.code === "S7_WEBAPI_AUTH_INVALID");
  await assert.rejects(service.connect({ host: "127.0.0.1", user: "u", password: "x" }), (error) => error.code === "S7_WEBAPI_AUTH_FAILED");
  await assert.rejects(service.readVariable(""), (error) => error.code === "S7_WEBAPI_VARIABLE_INVALID");
});

test("maps vendor authentication failure without leaking the password", async () => {
  const service = createS7WebApiService({
    fetchImpl: async () => response({ error: { code: 401, message: "bad credentials" } }, { ok: false, status: 401 }),
  });
  await assert.rejects(service.connect({ host: "plc.local", user: "operator", password: "secret" }), (error) => {
    assert.equal(error.code, "S7_WEBAPI_AUTH_FAILED");
    assert.equal(error.message.includes("secret"), false);
    return true;
  });
});

test("rejects unsafe variable names and invalid response limits before an RPC", () => {
  assert.equal(normalizeVariableName('"DB1".Value'), '"DB1".Value');
  assert.throws(() => normalizeVariableName(""), (error) => error.code === "S7_WEBAPI_VARIABLE_INVALID");
  assert.throws(() => normalizeVariableName("A".repeat(257)), (error) => error.code === "S7_WEBAPI_VARIABLE_INVALID");
  assert.throws(() => normalizeVariableName("DB1\u0000.Value"), (error) => error.code === "S7_WEBAPI_VARIABLE_INVALID");
  assert.equal(normalizeMaxResponseBytes(4096), 4096);
  assert.throws(() => normalizeMaxResponseBytes(512), (error) => error.code === "S7_WEBAPI_LIMIT_INVALID");
});

test("returns structured RPC errors and clears an expired token", async () => {
  let calls = 0;
  const service = createS7WebApiService({
    fetchImpl: async (_url, init) => {
      calls += 1;
      const method = JSON.parse(init.body).method;
      if (method === "Api.Login") return response({ result: { token: "secret-token-123" } });
      return response({ error: { code: 401, message: "expired" } }, { ok: false, status: 401 });
    },
  });
  await service.connect({ host: "127.0.0.1", user: "operator", password: "secret" });
  await assert.rejects(service.readVariable("DB1.Value"), (error) => {
    assert.equal(error.code, "S7_WEBAPI_AUTH_EXPIRED");
    assert.equal(error.details.vendorCode, 401);
    assert.equal(error.details.method, "PlcProgram.Read");
    return true;
  });
  assert.equal(service.isConnected(), false);
  assert.equal(calls, 2);
});

test("fails closed when a Web API response exceeds the configured limit", async () => {
  const service = createS7WebApiService({
    maxResponseBytes: 1024,
    fetchImpl: async (_url, init) => {
      const method = JSON.parse(init.body).method;
      if (method === "Api.Login") return response({ result: { token: "secret-token-123" } });
      return response({ result: { value: "X".repeat(2000) } });
    },
  });
  await service.connect({ host: "127.0.0.1", user: "operator", password: "secret" });
  await assert.rejects(service.readVariable("DB1.Value"), (error) => error.code === "S7_WEBAPI_RESPONSE_LIMIT");
});

test("rejects an undefined JSON body as a structured response error", async () => {
  const service = createS7WebApiService({
    fetchImpl: async () => ({ ok: true, status: 200, async json() { return undefined; } }),
  });
  await assert.rejects(
    service.connect({ host: "127.0.0.1", user: "operator", password: "secret" }),
    (error) => error.code === "S7_WEBAPI_RESPONSE_INVALID",
  );
});
