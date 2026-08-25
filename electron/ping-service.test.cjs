"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const {
  createPingService,
  parsePingOutput,
  parsePingTargets,
  isValidHost,
  MAX_PING_TARGETS,
} = require("./ping-service.cjs");

test("解析中文 Windows ping 成功输出", () => {
  const raw = [
    "",
    "正在 Ping 192.168.1.20 具有 32 字节的数据:",
    "来自 192.168.1.20 的回复: 字节=32 时间=2ms TTL=64",
    "来自 192.168.1.20 的回复: 字节=32 时间<1ms TTL=64",
    "来自 192.168.1.20 的回复: 字节=32 时间=3ms TTL=64",
    "来自 192.168.1.20 的回复: 字节=32 时间=2ms TTL=64",
    "",
    "192.168.1.20 的 Ping 统计信息:",
    "    数据包: 已发送 = 4，已接收 = 4，丢失 = 0 (0% 丢失)，",
    "往返行程的估计时间(以毫秒为单位):",
    "    最短 = 0ms，最长 = 3ms，平均 = 1ms",
  ].join("\r\n");
  const r = parsePingOutput(raw);
  assert.equal(r.alive, true);
  assert.deepEqual(r.timesMs, [2, 0, 3, 2]); // 时间<1ms 记 0
  assert.equal(r.sent, 4);
  assert.equal(r.received, 4);
  assert.equal(r.lossPct, 0);
  assert.equal(r.minMs, 0);
  assert.equal(r.avgMs, 1);
  assert.equal(r.maxMs, 3);
});

test("解析中文全丢包输出(目标不通)", () => {
  const raw = [
    "正在 Ping 192.168.1.20 具有 32 字节的数据:",
    "请求超时。",
    "请求超时。",
    "192.168.1.20 的 Ping 统计信息:",
    "    数据包: 已发送 = 2，已接收 = 0，丢失 = 2 (100% 丢失)，",
  ].join("\r\n");
  const r = parsePingOutput(raw);
  assert.equal(r.alive, false);
  assert.equal(r.sent, 2);
  assert.equal(r.received, 0);
  assert.equal(r.lossPct, 100);
  assert.equal(r.avgMs, null);
});

test("解析英文 Windows ping 输出", () => {
  const raw = [
    "Pinging 10.0.0.1 with 32 bytes of data:",
    "Reply from 10.0.0.1: bytes=32 time=1ms TTL=118",
    "Reply from 10.0.0.1: bytes=32 time=2ms TTL=118",
    "",
    "Ping statistics for 10.0.0.1:",
    "    Packets: Sent = 2, Received = 2, Lost = 0 (0% loss),",
    "Approximate round trip times in milli-seconds:",
    "    Minimum = 1ms, Maximum = 2ms, Average = 1ms",
  ].join("\r\n");
  const r = parsePingOutput(raw);
  assert.equal(r.alive, true);
  assert.deepEqual(r.timesMs, [1, 2]);
  assert.equal(r.sent, 2);
  assert.equal(r.lossPct, 0);
  assert.equal(r.avgMs, 1);
});

test("解析 *nix ping 输出", () => {
  const raw = [
    "PING 8.8.8.8 (8.8.8.8) 56(84) bytes of data.",
    "64 bytes from 8.8.8.8: icmp_seq=1 ttl=118 time=9.8 ms",
    "",
    "--- 8.8.8.8 ping statistics ---",
    "1 packets transmitted, 1 packets received, 0% packet loss",
  ].join("\n");
  const r = parsePingOutput(raw);
  assert.equal(r.alive, true);
  assert.equal(r.sent, 1);
  assert.equal(r.received, 1);
});

test("host 校验拒绝非法输入(注入面封死)", () => {
  assert.equal(isValidHost("192.168.1.20"), true);
  assert.equal(isValidHost("plc.example.com"), true);
  assert.equal(isValidHost("8.8.8.8"), true);
  for (const bad of ["", "a b", "1.2.3.4; rm -rf", "host|whoami", "&&echo", "x".repeat(300), null, 42]) {
    assert.equal(isValidHost(bad), false, `应拒绝: ${JSON.stringify(bad)}`);
  }
});

test("ping 服务:非法 host 返回 INVALID_HOST 且不执行命令", async () => {
  const calls = [];
  const svc = createPingService({
    execFileImpl: (cmd, args, opts, cb) => { calls.push({ cmd, args }); cb(null, "", ""); },
  });
  const r = await svc.ping("bad host");
  assert.equal(r.ok, false);
  assert.equal(r.error.code, "INVALID_HOST");
  assert.deepEqual(calls, []);
});

test("ping 服务:目标不通(全超时)结构化为 alive=false", async () => {
  const svc = createPingService({
    execFileImpl: (_cmd, _args, _opts, cb) => cb(null,
      "正在 Ping 10.255.255.1 具有 32 字节的数据:\r\n请求超时。\r\n    数据包: 已发送 = 1，已接收 = 0，丢失 = 1 (100% 丢失)，\r\n", ""),
  });
  const r = await svc.ping("10.255.255.1", { count: 1 });
  assert.equal(r.ok, true);
  assert.equal(r.alive, false);
  assert.equal(r.lossPct, 100);
});

test("parsePingTargets 拆分逗号/空格/换行/中文逗号并去重", () => {
  assert.deepEqual(parsePingTargets("192.168.1.20, 192.168.1.30"), ["192.168.1.20", "192.168.1.30"]);
  assert.deepEqual(parsePingTargets("a.lan\nb.lan；c.lan"), ["a.lan", "b.lan", "c.lan"]);
  assert.deepEqual(parsePingTargets("PLC.local, plc.local"), ["PLC.local"]);
  assert.deepEqual(parsePingTargets(["  10.0.0.1  ", "", "10.0.0.1"]), ["10.0.0.1"]);
  assert.deepEqual(parsePingTargets("   "), []);
});

test("parsePingTargets 最多保留 16 个目标", () => {
  const input = Array.from({ length: 20 }, (_, index) => `h${index}.lan`).join(",");
  const hosts = parsePingTargets(input);
  assert.equal(MAX_PING_TARGETS, 16);
  assert.equal(hosts.length, 16);
  assert.equal(hosts[0], "h0.lan");
  assert.equal(hosts[15], "h15.lan");
});

test("pingMany 按解析顺序逐个 ping 且不把分隔符拼进 host", async () => {
  const calls = [];
  const svc = createPingService({
    execFileImpl: (_cmd, args, _opts, cb) => {
      calls.push(args.at(-1));
      const host = args.at(-1);
      cb(null, `Reply from ${host}: bytes=32 time=1ms TTL=64\r\nPackets: Sent = 1, Received = 1, Lost = 0 (0% loss),\r\nAverage = 1ms\r\n`, "");
    },
  });
  const r = await svc.pingMany("10.0.0.1; 10.0.0.2", { count: 1 });
  assert.equal(r.ok, true);
  assert.equal(r.results.length, 2);
  assert.deepEqual(calls, ["10.0.0.1", "10.0.0.2"]);
  assert.equal(r.results[0].alive, true);
  assert.equal(r.results[1].host, "10.0.0.2");
});

test("pingMany 空输入不执行命令", async () => {
  const calls = [];
  const svc = createPingService({
    execFileImpl: (cmd, args, opts, cb) => { calls.push({ cmd, args }); cb(null, "", ""); },
  });
  const r = await svc.pingMany("  , ;  ");
  assert.equal(r.ok, false);
  assert.equal(r.error.code, "INVALID_HOST");
  assert.deepEqual(calls, []);
});

test("本机接口 Ping UI 支持多目标输入与逐个调用", () => {
  const html = fs.readFileSync(path.join(__dirname, "..", "index.html"), "utf8");
  const renderer = fs.readFileSync(path.join(__dirname, "..", "src", "main.js"), "utf8");
  assert.match(html, /<textarea id="if-ping-host"/);
  assert.match(html, /最多 16 个/);
  assert.match(renderer, /function splitPingTargets/);
  assert.match(renderer, /MAX_PING_TARGETS = 16/);
  assert.match(renderer, /for \(let i = 0; i < hosts\.length/);
  assert.match(renderer, /callBackend\("ping_host"/);
});
