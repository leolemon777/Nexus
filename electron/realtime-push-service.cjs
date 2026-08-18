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

class RealtimePushService {
  constructor() {
    this.server = null;
    this.clients = new Set();
  }

  /**
   * 启动 SSE 服务器。
   * @param {{ port?: number }} options
   */
  start({ port = 8080 } = {}) {
    if (this.server) return { started: false, error: "already running" };

    this.server = http.createServer((req, res) => {
      if (req.url === "/events" || req.url === "/") {
        // SSE 端点
        res.writeHead(200, {
          "Content-Type": "text/event-stream",
          "Cache-Control": "no-cache",
          Connection: "keep-alive",
          "Access-Control-Allow-Origin": "http://127.0.0.1:* http://localhost:*",
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
        res.writeHead(200, { "Content-Type": "application/json", "Access-Control-Allow-Origin": "http://127.0.0.1:* http://localhost:*" });
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
      this.server.listen(port, "127.0.0.1", () => {
        resolve({ started: true, port, url: `http://127.0.0.1:${port}/events` });
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

module.exports = { RealtimePushService };
