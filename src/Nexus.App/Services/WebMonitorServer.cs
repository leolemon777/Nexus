using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.App.Services;

/// <summary>
/// Embedded web server for remote monitoring.
/// Serves a dashboard UI and REST API on a configurable port.
/// </summary>
public sealed class WebMonitorServer : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly ConcurrentDictionary<string, Func<HttpListenerContext, Task>> _routes = new();

    private Func<List<object>>? _getDevices;
    private Func<List<object>>? _getMonitoredAddresses;
    private Func<string, string, Task>? _writeValue;

    public bool IsRunning => _listener?.IsListening == true;
    public int Port { get; private set; }
    public string? Url { get; private set; }
    public int ConnectedClients { get; private set; }

    public event EventHandler<string>? OnLog;

    public void Configure(
        Func<List<object>> getDevices,
        Func<List<object>> getMonitoredAddresses,
        Func<string, string, Task>? writeValue = null)
    {
        _getDevices = getDevices;
        _getMonitoredAddresses = getMonitoredAddresses;
        _writeValue = writeValue;
    }

    public void Start(int port = 8080)
    {
        if (IsRunning) return;

        Port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }

        Url = $"http://localhost:{port}";
        _cts = new CancellationTokenSource();
        _listenTask = ListenLoopAsync(_cts.Token);
        OnLog?.Invoke(this, $"[Web] 服务器已启动: {Url}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
        ConnectedClients = 0;
        OnLog?.Invoke(this, "[Web] 服务器已停止");
    }

    public void Dispose() => Stop();

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context), ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
            catch (Exception ex)
            {
                OnLog?.Invoke(this, $"[Web] 错误: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var req = context.Request;
        var resp = context.Response;

        try
        {
            string path = req.Url?.AbsolutePath ?? "/";

            resp.Headers.Add("Access-Control-Allow-Origin", "*");
            resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

            if (req.HttpMethod == "OPTIONS")
            {
                resp.StatusCode = 200;
                resp.Close();
                return;
            }

            switch (path)
            {
                case "/":
                case "/index.html":
                    await ServeDashboardAsync(resp);
                    break;
                case "/api/devices":
                    await ServeJsonAsync(resp, _getDevices?.Invoke() ?? new());
                    break;
                case "/api/addresses":
                    await ServeJsonAsync(resp, _getMonitoredAddresses?.Invoke() ?? new());
                    break;
                case "/api/stream":
                    await ServeSseStreamAsync(context);
                    break;
                case "/api/write":
                    if (req.HttpMethod == "POST")
                        await HandleWriteAsync(req, resp);
                    else
                        resp.StatusCode = 405;
                    break;
                default:
                    resp.StatusCode = 404;
                    break;
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke(this, $"[Web] 请求处理错误: {ex.Message}");
        }
        finally
        {
            try { resp.Close(); } catch { }
        }
    }

    private async Task ServeDashboardAsync(HttpListenerResponse resp)
    {
        resp.ContentType = "text/html; charset=utf-8";
        resp.StatusCode = 200;
        var bytes = Encoding.UTF8.GetBytes(GetDashboardHtml());
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private async Task ServeJsonAsync(HttpListenerResponse resp, object data)
    {
        resp.ContentType = "application/json; charset=utf-8";
        resp.StatusCode = 200;
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
        var bytes = Encoding.UTF8.GetBytes(json);
        await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private async Task ServeSseStreamAsync(HttpListenerContext context)
    {
        var resp = context.Response;
        resp.ContentType = "text/event-stream";
        resp.Headers.Add("Cache-Control", "no-cache");
        resp.StatusCode = 200;

        ConnectedClients++;
        try
        {
            while (_listener?.IsListening == true)
            {
                var data = _getMonitoredAddresses?.Invoke() ?? new();
                var json = JsonSerializer.Serialize(data);
                var msg = $"data: {json}\n\n";
                var bytes = Encoding.UTF8.GetBytes(msg);
                await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length);
                await resp.OutputStream.FlushAsync();
                await Task.Delay(1000);
            }
        }
        catch { }
        finally
        {
            ConnectedClients--;
        }
    }

    private async Task HandleWriteAsync(HttpListenerRequest req, HttpListenerResponse resp)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var body = await reader.ReadToEndAsync();

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string address = root.GetProperty("address").GetString() ?? "";
            string value = root.GetProperty("value").GetString() ?? "";

            if (_writeValue != null)
            {
                await _writeValue(address, value);
                resp.StatusCode = 200;
                await ServeJsonAsync(resp, new { ok = true });
            }
            else
            {
                resp.StatusCode = 501;
                await ServeJsonAsync(resp, new { error = "写入功能未配置" });
            }
        }
        catch (Exception ex)
        {
            resp.StatusCode = 400;
            await ServeJsonAsync(resp, new { error = ex.Message });
        }
    }

    private static string GetDashboardHtml()
    {
        return @"<!DOCTYPE html>
<html lang=""zh-CN"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>Nexus 远程监控</title>
<style>
* { margin:0; padding:0; box-sizing:border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background:#0d1117; color:#c9d1d9; padding:16px; }
h1 { font-size:20px; margin-bottom:16px; color:#58a6ff; }
.card { background:#161b22; border:1px solid #30363d; border-radius:8px; padding:16px; margin-bottom:12px; }
.card h2 { font-size:14px; color:#8b949e; margin-bottom:8px; }
table { width:100%; border-collapse:collapse; font-size:13px; }
th,td { padding:6px 8px; text-align:left; border-bottom:1px solid #21262d; }
th { color:#8b949e; font-weight:600; }
.value { font-family:'Consolas',monospace; font-size:16px; font-weight:bold; color:#58a6ff; }
.ok { color:#3fb950; } .warn { color:#d29922; } .err { color:#f85149; }
.badge { display:inline-block; padding:2px 8px; border-radius:12px; font-size:11px; font-weight:600; }
.badge-ok { background:#238636; color:#fff; } .badge-err { background:#da3633; color:#fff; }
.grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(300px,1fr)); gap:12px; }
#status { font-size:12px; color:#8b949e; margin-bottom:16px; }
</style>
</head>
<body>
<h1>Nexus 远程监控</h1>
<div id=""status"">连接中...</div>

<div class=""card"">
<h2>设备状态</h2>
<div id=""devices"">加载中...</div>
</div>

<div class=""card"">
<h2>监控数据</h2>
<div id=""addresses"">加载中...</div>
</div>

<script>
const statusEl = document.getElementById('status');
const devicesEl = document.getElementById('devices');
const addressesEl = document.getElementById('addresses');

async function loadData() {
    try {
        const [devResp, addrResp] = await Promise.all([
            fetch('/api/devices'),
            fetch('/api/addresses')
        ]);
        const devices = await devResp.json();
        const addresses = await addrResp.json();
        renderDevices(devices);
        renderAddresses(addresses);
    } catch(e) { statusEl.textContent = '加载失败: ' + e.message; }
}

function renderDevices(devices) {
    if (!devices.length) { devicesEl.innerHTML = '<p style=""color:#8b949e"">暂无设备连接</p>'; return; }
    let html = '<table><tr><th>设备</th><th>协议</th><th>地址</th><th>状态</th></tr>';
    devices.forEach(d => {
        const status = d.isConnected ? '<span class=""badge badge-ok"">已连接</span>' : '<span class=""badge badge-err"">未连接</span>';
        html += '<tr><td>' + d.name + '</td><td>' + d.protocol + '</td><td>' + d.address + '</td><td>' + status + '</td></tr>';
    });
    html += '</table>';
    devicesEl.innerHTML = html;
}

function renderAddresses(addresses) {
    if (!addresses.length) { addressesEl.innerHTML = '<p style=""color:#8b949e"">暂无监控地址</p>'; return; }
    let html = '<table><tr><th>地址</th><th>别名</th><th>类型</th><th>当前值</th><th>质量</th><th>更新时间</th></tr>';
    addresses.forEach(a => {
        const qualityClass = a.quality === 'Good' ? 'ok' : 'err';
        const time = a.lastUpdateTime ? new Date(a.lastUpdateTime).toLocaleTimeString() : '--';
        html += '<tr><td>' + a.address + '</td><td>' + (a.alias||'-') + '</td><td>' + a.dataType + '</td>' +
            '<td class=""value"">' + a.currentValueText + '</td>' +
            '<td class=""' + qualityClass + '"">' + a.quality + '</td><td>' + time + '</td></tr>';
    });
    html += '</table>';
    addressesEl.innerHTML = html;
}

function connectSSE() {
    const es = new EventSource('/api/stream');
    es.onopen = function() { statusEl.innerHTML = '<span class=""ok"">已连接 · 实时更新中</span>'; };
    es.onmessage = function(e) {
        try {
            const addresses = JSON.parse(e.data);
            renderAddresses(addresses);
        } catch(err) {}
    };
    es.onerror = function() { statusEl.innerHTML = '<span class=""warn"">连接断开，重新连接中...</span>'; };
}

loadData();
connectSSE();
setInterval(loadData, 30000);
</script>
</body>
</html>";
    }
}
