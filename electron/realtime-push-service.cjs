/**
 * 实时数据推送服务 —— 用 SSE(Server-Sent Events) 替代 WebSocket。
 *
 * 功能等价于 Modbus Poll 的 VBA/Excel 联动 —— 外部工具(Node-RED/浏览器/curl)
 * 可以订阅 SSE 流,实时获取 Modbus 寄存器数据。
 *
 * 优势:纯 HTTP,无需额外 npm 依赖,浏览器原生支持 EventSource API。
 *
 * 用法:
 *   1. start({ port: 8080 }) → 启动 HTTP 服务器
 *   2. 外部工具访问 http://localhost:8080/events → 建立 SSE 连接
 *   3. push(event) → 推送给所有连接的客户端
 *   4. stop() → 关闭服务器
 */

const http = require("node:http");
const LOOPBACK_BIND_ADDRESS = "127.0.0.1";
const DEFAULT_REALTIME_PUSH_PORT = 8080;

function isLoopbackHostHeader(value) {
  return /^(?:127\.0\.0\.1|localhost)(?::\d+)?$/i.test(String(value ?? ""));
}

function allowedLoopbackOrigin(value) {
  if (!value) return null;
  try {
    const url = new URL(value);
    return url.protocol === "http:" && (url.hostname === "127.0.0.1" || url.hostname === "localhost")
      ? url.origin
      : null;
  } catch {
    return null;
  }
}

class RealtimePushService {
  constructor() {
    this.server = null;
    this.clients = new Set();
  }

  /**
   * 启动 SSE 服务器。
   * @param {{ port?: number }} options
   */
  start({ port = DEFAULT_REALTIME_PUSH_PORT } = {}) {
    if (this.server) return { started: false, error: "already running" };
    const requestedPort = Number(port);
    if (!Number.isInteger(requestedPort) || requestedPort < 0 || requestedPort > 65_535) {
      return Promise.reject(new Error("SSE 端口必须是 0-65535 的整数；0 表示由系统分配临时端口"));
    }

    this.server = http.createServer((req, res) => {
      if (!isLoopbackHostHeader(req.headers.host)) {
        res.writeHead(403, { "Content-Type": "text/plain; charset=utf-8" });
        res.end("Forbidden");
        return;
      }
      const corsOrigin = allowedLoopbackOrigin(req.headers.origin);
      const corsHeaders = corsOrigin
        ? { "Access-Control-Allow-Origin": corsOrigin, Vary: "Origin" }
        : {};
      if (req.url === "/events" || req.url === "/") {
        // SSE 端点
        res.writeHead(200, {
          "Content-Type": "text/event-stream",
          "Cache-Control": "no-cache",
          Connection: "keep-alive",
          ...corsHeaders,
        });
        res.write("retry: 2000\n\n");
        this.clients.add(res);

        // 发送初始事件
        res.write(`data: ${JSON.stringify({ type: "connected", timestamp: Date.now() })}\n\n`);

        req.on("close", () => {
          this.clients.delete(res);
        });
      } else if (req.url === "/status") {
        // 状态端点
        res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders });
        res.end(
          JSON.stringify({
            service: "nexus-realtime-push",
            connectedClients: this.clients.size,
            uptime: process.uptime(),
          }),
        );
      } else {
        res.writeHead(404);
        res.end("Not Found");
      }
    });

    return new Promise((resolve, reject) => {
      // 端口被占用等 listen 错误若不监听,error 事件未捕获会直接崩溃主进程
      this.server.once("error", (error) => {
        this.server = null;
        reject(new Error(`SSE 推送服务启动失败(端口 ${port} 可能被其它软件占用): ${error.message}`));
      });
      this.server.listen(requestedPort, LOOPBACK_BIND_ADDRESS, () => {
        const boundPort = this.server.address().port;
        resolve({
          started: true,
          port: boundPort,
          requestedPort,
          bindAddress: LOOPBACK_BIND_ADDRESS,
          url: `http://${LOOPBACK_BIND_ADDRESS}:${boundPort}/events`,
        });
      });
    });
  }

  /**
   * 推送一个事件给所有连接的客户端。
   * @param {{ type: string, [key: string]: any }} event
   */
  push(event) {
    const data = `data: ${JSON.stringify(event)}\n\n`;
    for (const client of this.clients) {
      try {
        client.write(data);
      } catch {
        this.clients.delete(client);
      }
    }
  }

  /**
   * 停止服务器。
   */
  async stop() {
    if (!this.server) return;
    for (const client of this.clients) {
      try {
        client.end();
      } catch {
        // 忽略
      }
    }
    this.clients.clear();
    return new Promise((resolve) => {
      this.server.close(() => {
        this.server = null;
        resolve({ stopped: true });
      });
    });
  }

  get connectedClients() {
    return this.clients.size;
  }
}

module.exports = {
  DEFAULT_REALTIME_PUSH_PORT,
  LOOPBACK_BIND_ADDRESS,
  RealtimePushService,
  allowedLoopbackOrigin,
  isLoopbackHostHeader,
};
