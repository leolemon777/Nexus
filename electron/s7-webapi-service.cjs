/**
 * S7-1500 Web API 服务(JSON-RPC 2.0 over HTTPS,固件 ≥V2.8)。
 *
 * 协议要点(grok 调研 + 西门子官方 simatic-s7-webserver-api):
 * - 端点 POST https://<ip>/api/jsonrpc
 * - Api.Login {user, password} → result.token → 后续请求头 X-Auth-Token
 * - PlcProgram.Read {var: "\"DB1\".MyVar", mode: "simple"} / Write {var, value}
 * - token 空闲 2-2.5 分钟失效;Api.Ping 不会续期(需重新 Login)
 * - TLS:PLC 自签证书;per-request rejectUnauthorized=false(不设进程级),
 *   UI 显式提示;量产可钉 fingerprint256
 */

const DEFAULT_TIMEOUT_MS = 10_000;
const DEFAULT_MAX_RESPONSE_BYTES = 1_048_576;
const MAX_VARIABLE_NAME_LENGTH = 256;

function webApiError(message, code = "S7_WEBAPI_ERROR", details) {
  const error = new Error(message);
  error.code = code;
  if (details !== undefined) error.details = details;
  return error;
}

function normalizeEndpoint({ host, port = 443 }) {
  const textHost = String(host ?? "").trim();
  if (!textHost || /[\\/@?#\s]/.test(textHost) || textHost.includes(":")) {
    throw webApiError("Web API 主机必须是纯 IP/主机名，不能包含路径、端口或凭据", "S7_WEBAPI_HOST_INVALID");
  }
  const numericPort = Number(port);
  if (!Number.isInteger(numericPort) || numericPort < 1 || numericPort > 65535) {
    throw webApiError("Web API 端口必须是 1..65535 的整数", "S7_WEBAPI_PORT_INVALID");
  }
  return { host: textHost, port: numericPort, baseUrl: `https://${textHost}:${numericPort}` };
}

function normalizeTimeout(value) {
  const timeoutMs = Number(value ?? DEFAULT_TIMEOUT_MS);
  if (!Number.isInteger(timeoutMs) || timeoutMs < 1 || timeoutMs > 600_000) {
    throw webApiError("Web API 超时必须是 1..600000 毫秒", "S7_WEBAPI_TIMEOUT_INVALID");
  }
  return timeoutMs;
}

function normalizeMaxResponseBytes(value) {
  const maxBytes = Number(value ?? DEFAULT_MAX_RESPONSE_BYTES);
  if (!Number.isInteger(maxBytes) || maxBytes < 1024 || maxBytes > 16 * 1024 * 1024) {
    throw webApiError("Web API 最大响应大小必须是 1024..16777216 字节", "S7_WEBAPI_LIMIT_INVALID");
  }
  return maxBytes;
}

function normalizeVariableName(value) {
  const name = String(value ?? "").trim();
  if (!name) throw webApiError("Web API 变量名不能为空", "S7_WEBAPI_VARIABLE_INVALID");
  if (name.length > MAX_VARIABLE_NAME_LENGTH || /[\u0000-\u001F\u007F]/.test(name)) {
    throw webApiError("Web API 变量名过长或包含控制字符", "S7_WEBAPI_VARIABLE_INVALID");
  }
  return name;
}

function createS7WebApiService({ fetchImpl = globalThis.fetch, now = () => Date.now(), maxResponseBytes } = {}) {
  if (typeof fetchImpl !== "function") throw new TypeError("fetchImpl must be a function");
  const responseLimit = normalizeMaxResponseBytes(maxResponseBytes);
  let session = null; // { baseUrl, token, user }
  let requestSequence = 0;

  function parseJsonResponse(body, method) {
    let encoded;
    try {
      encoded = JSON.stringify(body);
    } catch {
      throw webApiError(`Web API ${method} 返回值不可序列化`, "S7_WEBAPI_RESPONSE_INVALID", { method });
    }
    if (typeof encoded !== "string") {
      throw webApiError(`Web API ${method} 返回值不是 JSON 对象`, "S7_WEBAPI_RESPONSE_INVALID", { method });
    }
    if (Buffer.byteLength(encoded, "utf8") > responseLimit) {
      throw webApiError(`Web API ${method} 响应超过 ${responseLimit} 字节限制`, "S7_WEBAPI_RESPONSE_LIMIT", { method, maxBytes: responseLimit });
    }
    return body;
  }

  async function rpc(method, params, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
    if (!session) throw new Error("未登录,请先连接并登录");
    const timeout = normalizeTimeout(timeoutMs);
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
      const resp = await fetchImpl(session.baseUrl + "/api/jsonrpc", {
        method: "POST",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/json",
          "X-Auth-Token": session.token,
        },
        body: JSON.stringify({ jsonrpc: "2.0", id: now() + (++requestSequence % 1000), method, params: params || {} }),
      });
      if (!resp || typeof resp.json !== "function") throw webApiError("Web API 响应对象无效", "S7_WEBAPI_RESPONSE_INVALID");
      const body = parseJsonResponse(await resp.json(), method);
      if (!resp.ok && !body?.error) throw webApiError(`Web API HTTP ${resp.status ?? "?"}`, "S7_WEBAPI_HTTP_ERROR");
      if (body.error) {
        const codeMap = {
          "-32601": "方法不存在",
          "-32602": "参数无效(变量名语法?)",
          "-32700": "JSON 解析错误",
          "401": "token 失效,请重新登录",
        };
        const vendorCode = body.error.code;
        const expired = String(vendorCode) === "401";
        if (expired) session = null;
        throw webApiError(
          `Web API ${vendorCode}: ${codeMap[String(vendorCode)] || body.error.message || "未知错误"}`,
          expired ? "S7_WEBAPI_AUTH_EXPIRED" : "S7_WEBAPI_RPC_ERROR",
          { method, vendorCode },
        );
      }
      return body.result;
    } finally {
      clearTimeout(timer);
    }
  }

  async function connect({ host, port, user, password, timeoutMs }) {
    const endpoint = normalizeEndpoint({ host, port });
    const timeout = normalizeTimeout(timeoutMs);
    const username = String(user ?? "").trim();
    if (!username) throw webApiError("Web API 用户名不能为空", "S7_WEBAPI_AUTH_INVALID");
    if (typeof password !== "string") throw webApiError("Web API 密码参数无效", "S7_WEBAPI_AUTH_INVALID");
    const resp = await fetchImpl(endpoint.baseUrl + "/api/jsonrpc", {
      method: "POST",
      signal: AbortSignal.timeout(timeout),
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        jsonrpc: "2.0", id: Date.now(), method: "Api.Login",
        params: { user: username, password },
      }),
    }).catch((e) => {
      if (e?.code?.startsWith("S7_WEBAPI_")) throw e;
      throw webApiError(`连接失败(HTTPS ${endpoint.host}:${endpoint.port}):${e.message}。检查:① CPU 固件 ≥V2.8 ② Web server 功能已启用 ③ 证书信任`, "S7_WEBAPI_CONNECT_FAILED");
    });
    if (!resp || typeof resp.json !== "function") throw webApiError("响应对象无效", "S7_WEBAPI_RESPONSE_INVALID");
    const body = await resp.json()
      .then((value) => parseJsonResponse(value, "Api.Login"))
      .catch((error) => {
        if (error?.code?.startsWith("S7_WEBAPI_")) throw error;
        throw webApiError("响应不是 JSON(可能 Web server 未启用)", "S7_WEBAPI_RESPONSE_INVALID");
      });
    if (!resp.ok && !body?.result) throw webApiError(`登录失败(HTTP ${resp.status ?? "?"})`, "S7_WEBAPI_AUTH_FAILED");
    if (body.error) throw webApiError(`登录失败:${body.error.message || "未知错误"}(检查用户名/密码,CPU 属性→用户与权限)`, "S7_WEBAPI_AUTH_FAILED", { vendorCode: body.error.code });
    const token = body?.result?.token;
    if (typeof token !== "string" || token.length < 8) throw webApiError("登录响应缺少有效 token", "S7_WEBAPI_AUTH_FAILED");
    session = { baseUrl: endpoint.baseUrl, token, user: username };
    return { token: token.slice(0, 8) + "…", host: endpoint.host, port: endpoint.port };
  }

  async function disconnect() {
    if (session) {
      try { await rpc("Api.Logout"); } catch { /* token 可能已过期 */ }
    }
    session = null;
  }

  function isConnected() {
    return session !== null;
  }

  async function readVariable(varName, mode) {
    const name = normalizeVariableName(varName);
    const result = await rpc("PlcProgram.Read", { var: name, mode: mode || "simple" });
    return result;
  }

  async function writeVariable(varName, value, mode) {
    const name = normalizeVariableName(varName);
    await rpc("PlcProgram.Write", { var: name, value, mode: mode || "simple" });
    return { ok: true };
  }

  async function ping() {
    const t0 = Date.now();
    await rpc("Api.Ping");
    return { elapsedMs: Date.now() - t0 };
  }

  return { connect, disconnect, isConnected, readVariable, writeVariable, ping, normalizeEndpoint };
}

module.exports = {
  createS7WebApiService,
  normalizeEndpoint,
  normalizeTimeout,
  normalizeMaxResponseBytes,
  normalizeVariableName,
  webApiError,
};
