using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus;
using Nexus.Modbus;

namespace Nexus.App.ViewModels;

/// <summary>
/// Modbus TCP 调试器 ViewModel。
///
/// 线程模型
///   - 命令/属性变更运行在 UI 线程（默认 DispatcherSynchronizationContext 捕获）。
///   - 协议 I/O 实际由 <see cref="TcpDeviceBase"/> 的 Task.Run 包装在线程池上执行。
///   - OnMessageSent / OnMessageReceived / OnError 事件从线程池触发，
///     处理器统一通过 _dispatcher.BeginInvoke 回到 UI 线程追加 LogLines。
///   - LogLines 容量上限 500 行（FIFO 丢弃最早项），避免长时间调试时 UI 失控。
///
/// 生命周期
///   - 由 ModbusTcpPage 在构造时创建并作为 DataContext。
///   - ModbusTcpPage.OnUnloaded 调用 Dispose() 释放 client + server。
/// </summary>
public partial class ModbusTcpViewModel : ObservableObject, IDisposable
{
    // ── 输入参数 ──────────────────────────────
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _slaveId = 1;
    [ObservableProperty] private int _startAddress = 0;
    [ObservableProperty] private int _quantity = 1;
    [ObservableProperty] private string _writeValue = string.Empty;
    [ObservableProperty] private string _dataType = "Int16";
    [ObservableProperty] private string _functionCode = "Read Holding Registers (03)";

    // ── 状态 ──────────────────────────────────
    [ObservableProperty] private string _connectionStatus = "未连接";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private string _lastResult = "--";

    /// <summary>支持的数据类型列表（绑定到 ComboBox）。</summary>
    public string[] DataTypes { get; } =
    {
        "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Float", "Double", "String", "Bool", "Bytes"
    };

    /// <summary>支持的功能码列表（绑定到 ComboBox）。</summary>
    public string[] FunctionCodes { get; } =
    {
        "Read Coils (01)",
        "Read Discrete Inputs (02)",
        "Read Holding Registers (03)",
        "Read Input Registers (04)",
        "Write Single Coil (05)",
        "Write Single Register (06)",
        "Write Multiple Coils (15)",
        "Write Multiple Registers (16)"
    };

    /// <summary>通讯日志（绑定到 ItemsControl）。</summary>
    public ObservableCollection<string> LogLines { get; } = new();

    // ── 内部状态 ──────────────────────────────
    private ModbusTcpClient? _client;
    private ModbusTcpServer? _server;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    private const int LogCap = 500;
    private const int ServerPort = 15020;   // 避开系统 502（需要 root/管理员）

    public ModbusTcpViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AppendLog("Modbus TCP 调试器已就绪。");
        AppendLog("提示：可先点击\"启动内置 Server\"在 127.0.0.1:15020 模拟 PLC，再连接并读写。");
    }

    // ═══════════════════════════════════════════
    //  连接
    // ═══════════════════════════════════════════

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || _disposed) return;

        // 复用旧 client 时先清理
        if (_client != null)
        {
            DetachClientEvents(_client);
            _client.Dispose();
            _client = null;
        }

        var client = new ModbusTcpClient(IpAddress, Port, SlaveId, 3000);
        AttachClientEvents(client);
        AppendLog($"正在连接 {IpAddress}:{Port} (站号={SlaveId}) ...");

        try
        {
            var result = await client.ConnectAsync().ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                AppendLog($"[ERR] 连接失败: {result.Message}");
                DetachClientEvents(client);
                client.Dispose();
                IsConnected = false;
                ConnectionStatus = "连接失败";
                return;
            }

            _client = client;
            IsConnected = true;
            ConnectionStatus = "已连接";
            AppendLog($"[OK] 已连接 {IpAddress}:{Port}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 连接异常: {ex.Message}");
            DetachClientEvents(client);
            client.Dispose();
            IsConnected = false;
            ConnectionStatus = "未连接";
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        var client = _client;
        if (client == null) return;

        try { client.Disconnect(); } catch { /* 忽略 */ }
        DetachClientEvents(client);
        client.Dispose();
        _client = null;

        IsConnected = false;
        ConnectionStatus = "已断开";
        AppendLog("[--] 连接已断开");
    }

    // ═══════════════════════════════════════════
    //  读取 — DataType 路由（全部走 Async API）
    // ═══════════════════════════════════════════

    [RelayCommand]
    private async Task ReadAsync()
    {
        if (_client == null) { AppendLog("[ERR] 未连接，无法读取。"); return; }
        if (StartAddress < 0 || StartAddress > 65535) { AppendLog("[ERR] 起始地址越界 (0-65535)"); return; }
        if (Quantity < 1) { AppendLog("[ERR] 数量必须 ≥ 1"); return; }
        if (Quantity > 125) { AppendLog("[ERR] 寄存器数量超出 Modbus 规范 (≤125)"); return; }

        string addr = StartAddress.ToString();
        try
        {
            string? resultText = DataType switch
            {
                "Int16"  => await ReadAndFormatAsync(addr, () => _client.ReadInt16Async(addr)),
                "UInt16" => await ReadAndFormatAsync(addr, () => _client.ReadUInt16Async(addr)),
                "Int32"  => await ReadAndFormatAsync(addr, () => _client.ReadInt32Async(addr)),
                "UInt32" => await ReadAndFormatAsync(addr, () => _client.ReadUInt32Async(addr)),
                "Int64"  => await ReadAndFormatAsync(addr, () => _client.ReadInt64Async(addr)),
                "UInt64" => await ReadAndFormatAsync(addr, () => _client.ReadUInt64Async(addr)),
                "Float"  => await ReadAndFormatAsync(addr, () => _client.ReadFloatAsync(addr)),
                "Double" => await ReadAndFormatAsync(addr, () => _client.ReadDoubleAsync(addr)),
                "String" => await ReadAndFormatAsync(addr, () => _client.ReadStringAsync(addr, (ushort)Quantity)),
                "Bool"   => await ReadAndFormatAsync(addr, () => _client.ReadBoolAsync(addr)),
                "Bytes"  => await ReadBytesAndFormatAsync(addr, (ushort)Quantity),
                _        => "[ERR] 未知数据类型",
            };

            if (resultText != null) LastResult = resultText;
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 读取异常: {ex.Message}");
        }
    }

    private async Task<string?> ReadAndFormatAsync<T>(string addr, Func<Task<OperateResult<T>>> action)
    {
        var r = await action().ConfigureAwait(true);
        if (!r.IsSuccess)
        {
            AppendLog($"[ERR] 读取 {addr} 失败: {r.Message}");
            return null;
        }
        string text = r.Content?.ToString() ?? "--";
        AppendLog($"[RD] {addr} ({DataType}) = {text}");
        return text;
    }

    private async Task<string?> ReadBytesAndFormatAsync(string addr, ushort len)
    {
        var r = await _client!.ReadBytesAsync(addr, len).ConfigureAwait(true);
        if (!r.IsSuccess)
        {
            AppendLog($"[ERR] 读取 {addr} 失败: {r.Message}");
            return null;
        }
        string hex = BitConverter.ToString(r.Content).Replace("-", " ");
        AppendLog($"[RD] {addr} ({len} bytes) = {hex}");
        return hex;
    }

    // ═══════════════════════════════════════════
    //  写入 — DataType 路由（全部走 Async API）
    // ═══════════════════════════════════════════

    [RelayCommand]
    private async Task WriteAsync()
    {
        if (_client == null) { AppendLog("[ERR] 未连接，无法写入。"); return; }
        if (StartAddress < 0 || StartAddress > 65535) { AppendLog("[ERR] 起始地址越界 (0-65535)"); return; }

        string addr = StartAddress.ToString();
        try
        {
            string result = DataType switch
            {
                "Int16"  => await DoWriteAsync(addr, () => _client.WriteAsync(addr, ParseShort(WriteValue, "Int16"))),
                "UInt16" => await DoWriteAsync(addr, () => _client.WriteAsync(addr, ParseUShort(WriteValue))),
                "Int32"  => await DoWriteAsync(addr, () => _client.WriteAsync(addr, ParseInt(WriteValue, "Int32"))),
                "Float"  => await DoWriteAsync(addr, () => _client.WriteAsync(addr, ParseFloat(WriteValue))),
                "String" => await DoWriteAsync(addr, () => _client.WriteAsync(addr, WriteValue ?? string.Empty)),
                "Bool"   => await DoWriteAsync(addr, () => _client.WriteAsync(addr, ParseBool(WriteValue))),
                _        => "[ERR] 当前数据类型在最小可用版本不支持写入",
            };
            AppendLog(result);
        }
        catch (FormatException fex)
        {
            AppendLog($"[ERR] 写入失败: {fex.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 写入异常: {ex.Message}");
        }
    }

    private async Task<string> DoWriteAsync(string addr, Func<Task<OperateResult>> action)
    {
        var r = await action().ConfigureAwait(true);
        return r.IsSuccess
            ? $"[WR] {addr} ({DataType}) = {WriteValue} 成功"
            : $"[ERR] 写入 {addr} 失败: {r.Message}";
    }

    // ── 解析辅助 ──

    private static short ParseShort(string s, string type)
        => short.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"{type} 值无法解析: '{s}'");

    private static ushort ParseUShort(string s)
        => ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"UInt16 值无法解析: '{s}'");

    private static int ParseInt(string s, string type)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"{type} 值无法解析: '{s}'");

    private static float ParseFloat(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new FormatException($"Float 值无法解析: '{s}'");

    private static bool ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().ToLowerInvariant();
        return s is "1" or "true" or "on" or "yes";
    }

    // ═══════════════════════════════════════════
    //  内置 Server
    // ═══════════════════════════════════════════

    [RelayCommand]
    private void StartServer()
    {
        if (IsServerRunning) return;
        try
        {
            var server = new ModbusTcpServer(ServerPort);
            // 预设 4 区数据，供联机自测
            server.SetHoldingRegister(0, 0x1234);
            server.SetHoldingRegister(1, 0x5678);
            server.SetCoil(0, true);
            server.SetCoil(1, false);
            server.Start();

            _server = server;
            IsServerRunning = true;
            AppendLog($"[SRV] 内置 Modbus TCP Server 已启动: 127.0.0.1:{ServerPort}");
            AppendLog("[SRV] 预设: HR40001=0x1234, HR40002=0x5678, 线圈00001=ON, 线圈00002=OFF");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 启动 Server 失败: {ex.Message}");
            _server?.Dispose();
            _server = null;
            IsServerRunning = false;
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        var server = _server;
        if (server == null) return;
        try
        {
            server.Stop();
            AppendLog("[SRV] 内置 Server 已停止");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 停止 Server 异常: {ex.Message}");
        }
        finally
        {
            server.Dispose();
            _server = null;
            IsServerRunning = false;
        }
    }

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    // ═══════════════════════════════════════════
    //  事件订阅（client）
    // ═══════════════════════════════════════════

    private void AttachClientEvents(ModbusTcpClient client)
    {
        client.OnMessageSent     += Client_OnMessageSent;
        client.OnMessageReceived += Client_OnMessageReceived;
        client.OnError           += Client_OnError;
        client.OnDisconnected    += Client_OnDisconnected;
    }

    private void DetachClientEvents(ModbusTcpClient client)
    {
        client.OnMessageSent     -= Client_OnMessageSent;
        client.OnMessageReceived -= Client_OnMessageReceived;
        client.OnError           -= Client_OnError;
        client.OnDisconnected    -= Client_OnDisconnected;
    }

    private void Client_OnMessageSent(object? sender, string hex)     => AppendLog($"[TX] {hex}");
    private void Client_OnMessageReceived(object? sender, string hex) => AppendLog($"[RX] {hex}");
    private void Client_OnError(object? sender, string e)            => AppendLog($"[ERR] {e}");

    private void Client_OnDisconnected(object? sender, EventArgs e)
    {
        // 服务器主动断开（网络线程）— 切回 UI 线程更新状态
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsConnected) return;   // 已被本地 Disconnect 处理过
            IsConnected = false;
            ConnectionStatus = "连接已断开";
            AppendLog("[--] 服务器断开连接");
        }));
    }

    // ═══════════════════════════════════════════
    //  日志（统一在 UI 线程追加 + 容量上限）
    // ═══════════════════════════════════════════

    private void AppendLog(string line)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        if (_dispatcher.CheckAccess())
        {
            DoAppend(stamped);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => DoAppend(stamped)));
        }
    }

    private void DoAppend(string stamped)
    {
        LogLines.Add(stamped);
        if (LogLines.Count > LogCap)
        {
            int remove = LogLines.Count - LogCap;
            for (int i = 0; i < remove; i++) LogLines.RemoveAt(0);
        }
    }

    // ═══════════════════════════════════════════
    //  释放
    // ═══════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Disconnect(); } catch { /* 忽略 */ }
        try { StopServer(); } catch { /* 忽略 */ }

        GC.SuppressFinalize(this);
    }
}
