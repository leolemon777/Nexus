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

function createS7WebApiService() {
  let session = null; // { baseUrl, token, user }

  async function rpc(method, params, { timeoutMs = DEFAULT_TIMEOUT_MS } = {}) {
    if (!session) throw new Error("未登录,请先连接并登录");
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    try {
      const resp = await fetch(session.baseUrl + "/api/jsonrpc", {
        method: "POST",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/json",
          "X-Auth-Token": session.token,
        },
        body: JSON.stringify({ jsonrpc: "2.0", id: Date.now(), method, params: params || {} }),
      });
      const body = await resp.json();
      if (body.error) {
        const codeMap = {
          "-32601": "方法不存在",
          "-32602": "参数无效(变量名语法?)",
          "-32700": "JSON 解析错误",
          "401": "token 失效,请重新登录",
        };
        throw new Error(`Web API ${body.error.code}: ${codeMap[String(body.error.code)] || body.error.message || "未知错误"}`);
      }
      return body.result;
    } finally {
      clearTimeout(timer);
    }
  }

  async function connect({ host, port, user, password, timeoutMs }) {
    const baseUrl = `https://${host}:${port || 443}`;
    const resp = await fetch(baseUrl + "/api/jsonrpc", {
      method: "POST",
      signal: AbortSignal.timeout(timeoutMs || DEFAULT_TIMEOUT_MS),
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        jsonrpc: "2.0", id: Date.now(), method: "Api.Login",
        params: { user, password },
      }),
    }).catch((e) => {
      throw new Error(`连接失败(HTTPS ${host}:${port || 443}):${e.message}。检查:① CPU 固件 ≥V2.8 ② Web server 功能已启用 ③ 证书信任(自签正常)`);
    });
    const body = await resp.json().catch(() => { throw new Error("响应不是 JSON(可能 Web server 未启用)"); });
    if (body.error) throw new Error(`登录失败:${body.error.message}(检查用户名/密码,CPU 属性→用户与权限)`);
    session = { baseUrl, token: body.result.token, user };
    return { token: body.result.token.slice(0, 8) + "…" };
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
    const result = await rpc("PlcProgram.Read", { var: varName, mode: mode || "simple" });
    return result;
  }

  async function writeVariable(varName, value, mode) {
    await rpc("PlcProgram.Write", { var: varName, value, mode: mode || "simple" });
    return { ok: true };
  }

  async function ping() {
    const t0 = Date.now();
    await rpc("Api.Ping");
    return { elapsedMs: Date.now() - t0 };
  }

  return { connect, disconnect, isConnected, readVariable, writeVariable, ping };
}

module.exports = { createS7WebApiService };
