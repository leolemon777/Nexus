"use strict";

/**
 * 网络连通性 Ping 服务 —— 封装系统 ping 命令(execFile 参数数组化,无 shell 注入面),
 * 解析中/英文 Windows 与 *nix 输出为结构化结果。独立模块便于 node --test 单测。
 */

const { execFile } = require("node:child_process");

const HOST_RE = /^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,251}[A-Za-z0-9])?$/;
const TARGET_SPLIT_RE = /[\s,;，；]+/;
const MAX_PING_TARGETS = 16;

function isValidHost(host) {
  return typeof host === "string" && HOST_RE.test(host);
}

/**
 * 把一次输入拆成多个 Ping 目标。分隔符：空格、逗号、分号、中文逗号/分号、换行。
 * 去空白、去重（大小写不敏感），最多 16 个。不在这里做 host 合法性校验。
 */
function parsePingTargets(input) {
  const parts = Array.isArray(input)
    ? input.map((item) => String(item ?? ""))
    : String(input ?? "").split(TARGET_SPLIT_RE);
  const hosts = [];
  const seen = new Set();
  for (const part of parts) {
    const host = part.trim();
    if (!host) continue;
    const key = host.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    hosts.push(host);
    if (hosts.length >= MAX_PING_TARGETS) break;
  }
  return hosts;
}

/**
 * 解析 ping 原始输出(中英文 Windows / *nix)。
 * 返回 { alive, sent, received, lossPct, timesMs, minMs, avgMs, maxMs }。
 * 判活依据:存在任一往返时间样本或 "TTL" 字样。
 */
function parsePingOutput(raw) {
  const text = String(raw ?? "");
  const timesMs = [];
  const timeRe = /(?:时间|time)([=<])\s*(\d+)\s*ms/gi; // "时间<1ms"=亚毫秒记 0
  let m;
  while ((m = timeRe.exec(text)) !== null) timesMs.push(m[1] === "<" ? 0 : Number(m[2]));

  let sent = null;
  let received = null;
  const statZh = text.match(/已发送\s*=\s*(\d+)[\s,，]*已接收\s*=\s*(\d+)/);
  const statEn = text.match(/sent\s*=\s*(\d+)[\s,，]*received\s*=\s*(\d+)/i)
    ?? text.match(/(\d+)\s*packets\s+transmitted,\s*(\d+)\s+(?:received|packets received)/i);
  if (statZh) { sent = Number(statZh[1]); received = Number(statZh[2]); }
  else if (statEn) { sent = Number(statEn[1]); received = Number(statEn[2]); }

  const lossMatch = text.match(/\((\d+)%\s*(?:丢失|loss)\)/i);
  const ttlSeen = /ttl[=:]/i.test(text);
  const alive = timesMs.length > 0 || (received != null && received > 0) || (ttlSeen && (lossMatch ? Number(lossMatch[1]) < 100 : true));

  const avgMatch = text.match(/(?:平均|average)\s*=\s*(\d+)\s*ms/i);
  const minMatch = text.match(/(?:最短|min(?:imum)?)\s*=\s*(\d+)\s*ms/i);
  const maxMatch = text.match(/(?:最长|max(?:imum)?)\s*=\s*(\d+)\s*ms/i);

  return {
    alive,
    sent,
    received,
    lossPct: lossMatch != null ? Number(lossMatch[1]) : (sent ? Math.round((1 - (received ?? 0) / sent) * 100) : null),
    timesMs,
    minMs: minMatch != null ? Number(minMatch[1]) : (timesMs.length ? Math.min(...timesMs) : null),
    avgMs: avgMatch != null ? Number(avgMatch[1]) : (timesMs.length ? Math.round(timesMs.reduce((a, b) => a + b, 0) / timesMs.length) : null),
    maxMs: maxMatch != null ? Number(maxMatch[1]) : (timesMs.length ? Math.max(...timesMs) : null),
  };
}

function createPingService({ execFileImpl = execFile } = {}) {
  /**
   * ping(host, {count=4, timeoutMs=1000}) →
   * { ok, alive, sent, received, lossPct, timesMs, minMs, avgMs, maxMs, raw }
   * ok=false 表示参数非法或本地执行失败(与"目标不通"的 ok=true/alive=false 区分)。
   */
  async function ping(host, { count = 4, timeoutMs = 1000 } = {}) {
    if (!isValidHost(host)) {
      return { ok: false, error: { code: "INVALID_HOST", message: "目标须为合法 IP 或主机名(字母/数字/点/连字符)" } };
    }
    count = Math.min(Math.max(Math.floor(count) || 4, 1), 10);
    timeoutMs = Math.min(Math.max(Math.floor(timeoutMs) || 1000, 100), 10000);
    const isWin = process.platform === "win32";
    const args = isWin
      ? ["-n", String(count), "-w", String(timeoutMs), host]
      : ["-c", String(count), "-W", String(Math.max(1, Math.round(timeoutMs / 1000))), host];

    const raw = await new Promise((resolve) => {
      execFileImpl("ping", args, { windowsHide: true, timeout: count * timeoutMs + 4000, maxBuffer: 64 * 1024 },
        (error, stdout, stderr) => resolve({ stdout: String(stdout ?? ""), stderr: String(stderr ?? ""), error }));
    });
    // Windows ping 对"目标不可达"也可能返回非 0 退出码,判活一律以输出文本为准
    const parsed = parsePingOutput(raw.stdout + "\n" + raw.stderr);
    return { ok: true, host, ...parsed, raw: (raw.stdout + raw.stderr).trim() };
  }

  async function pingMany(input, options) {
    const hosts = parsePingTargets(input);
    if (hosts.length === 0) {
      return {
        ok: false,
        error: { code: "INVALID_HOST", message: "请输入至少一个 IP 或主机名" },
        results: [],
      };
    }
    const results = [];
    for (const host of hosts) {
      results.push(await ping(host, options));
    }
    return { ok: true, results };
  }

  return { ping, pingMany };
}

module.exports = {
  createPingService,
  parsePingOutput,
  parsePingTargets,
  isValidHost,
  MAX_PING_TARGETS,
};
