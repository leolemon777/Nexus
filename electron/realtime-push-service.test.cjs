"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const {
  DEFAULT_REALTIME_PUSH_PORT,
  LOOPBACK_BIND_ADDRESS,
  allowedLoopbackOrigin,
  isLoopbackHostHeader,
  RealtimePushService,
} = require("./realtime-push-service.cjs");

test("accepts only loopback Host headers", () => {
  for (const host of ["127.0.0.1", "127.0.0.1:8080", "localhost:4173"]) {
    assert.equal(isLoopbackHostHeader(host), true, host);
  }
  for (const host of ["evil.example", "192.168.1.20:8080", "127.0.0.1.evil.example", ""]) {
    assert.equal(isLoopbackHostHeader(host), false, host);
  }
});

test("allows browser CORS only from an HTTP loopback origin", () => {
  assert.equal(allowedLoopbackOrigin("http://127.0.0.1:1420"), "http://127.0.0.1:1420");
  assert.equal(allowedLoopbackOrigin("http://localhost:4173/path"), "http://localhost:4173");
  assert.equal(allowedLoopbackOrigin("https://example.com"), null);
  assert.equal(allowedLoopbackOrigin("https://127.0.0.1:1420"), null);
  assert.equal(allowedLoopbackOrigin("not a url"), null);
});

test("realtime API binds only loopback and rejects non-loopback Host headers on a live socket", async () => {
  const service = new RealtimePushService();
  try {
    const started = await service.start({ port: 0, host: "0.0.0.0" });
    assert.equal(started.started, true);
    assert.equal(started.bindAddress, LOOPBACK_BIND_ADDRESS);
    assert.equal(started.requestedPort, 0);
    assert.equal(started.port, service.server.address().port);
    assert.equal(started.url, `http://${LOOPBACK_BIND_ADDRESS}:${started.port}/events`);
    assert.equal(service.server.address().address, LOOPBACK_BIND_ADDRESS);
    assert.equal(DEFAULT_REALTIME_PUSH_PORT, 8080);

    const allowed = await requestStatus(started.port, `127.0.0.1:${started.port}`);
    assert.equal(allowed.statusCode, 200);
    assert.equal(JSON.parse(allowed.body).service, "nexus-realtime-push");

    const denied = await requestStatus(started.port, "evil.example");
    assert.equal(denied.statusCode, 403);
    assert.equal(denied.body, "Forbidden");
  } finally {
    await service.stop();
  }

  await assert.rejects(() => service.start({ port: 65_536 }), /0-65535/);
});

function requestStatus(port, hostHeader) {
  return new Promise((resolve, reject) => {
    const request = http.get({
      host: LOOPBACK_BIND_ADDRESS,
      port,
      path: "/status",
      headers: { host: hostHeader },
    }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => { body += chunk; });
      response.on("end", () => resolve({ statusCode: response.statusCode, body }));
    });
    request.on("error", reject);
  });
}
