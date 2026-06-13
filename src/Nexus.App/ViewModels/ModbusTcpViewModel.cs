using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Nexus;
using Nexus.App.Configuration;
using Nexus.App.Services;
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
/// DI
///   - 由 DI 容器构造（AddTransient&lt;ModbusTcpViewModel&gt;()），通过 IOptions&lt;ModbusOptions&gt;
///     注入 appsettings.json 中 Modbus 节的默认值。
///   - 避免硬编码 IP/Port/超时等业务参数；Server 端口也由配置驱动。
///
/// 生命周期
///   - 由 ModbusTcpPage 通过 App.Services 解析，作为 DataContext。
///   - ModbusTcpPage.OnUnloaded 调用 Dispose() 释放 client + server。
/// </summary>
public partial class ModbusTcpViewModel : ObservableObject, IDisposable
{
    private readonly ModbusOptions _options;
    private readonly PacketRecorderService _packetRecorder;

    // ── 输入参数 ──────────────────────────────
    [ObservableProperty] private string _ipAddress = "127.0.0.1";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _slaveId = 1;
    [ObservableProperty] private int _startAddress = 0;
    [ObservableProperty] private int _quantity = 1;
    [ObservableProperty] private string _writeValue = string.Empty;
    [ObservableProperty] private string _dataType = "Int16";
    [ObservableProperty] private string _functionCode = "Read Holding Registers (03)";
    [ObservableProperty] private bool _useConnectionPool;
    [ObservableProperty] private int _connectionPoolSize = 4;

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
    private ModbusTcpConnectionPool? _pool;
    private ModbusTcpServer? _server;
    private readonly Dispatcher _dispatcher;
    private readonly Endianness _byteOrder;
    private readonly int _timeoutMs;
    private bool _disposed;

    private const int LogCap = 500;

    /// <summary>内置 Server 端口（来自 appsettings.json 的 <c>Modbus:VirtualServerPort</c>）。</summary>
    public int VirtualServerPort => _options.VirtualServerPort;

    private bool HasConnection => _client != null || _pool != null;

    public ModbusTcpViewModel(IOptions<ModbusOptions> options, PacketRecorderService packetRecorder)
    {
        _options = options.Value;
        _packetRecorder = packetRecorder;
        _ipAddress = _options.DefaultIp;
        _port = _options.DefaultPort;
        _slaveId = _options.DefaultSlaveId;
        _useConnectionPool = _options.UseConnectionPool;
        _connectionPoolSize = Math.Max(1, _options.ConnectionPoolSize);
        _timeoutMs = Math.Max(100, _options.DefaultTimeoutMs);
        _byteOrder = ParseByteOrder(_options.DefaultByteOrder);

        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AppendLog("Modbus TCP 调试器已就绪。");
        AppendLog($"提示：可先点击\"启动内置 Server\"在 127.0.0.1:{VirtualServerPort} 模拟 PLC，再连接并读写。");
    }

    // ═══════════════════════════════════════════
    //  连接
    // ═══════════════════════════════════════════

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || _disposed) return;

        CloseClient();
        ClosePool();

        if (UseConnectionPool)
        {
            await ConnectWithPoolAsync().ConfigureAwait(true);
            return;
        }

        var client = new ModbusTcpClient(IpAddress, Port, SlaveId, _timeoutMs)
        {
            ByteOrder = _byteOrder
        };
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

    private async Task ConnectWithPoolAsync()
    {
        int poolSize = Math.Max(1, ConnectionPoolSize);
        ConnectionPoolSize = poolSize;

        var pool = new ModbusTcpConnectionPool(
            IpAddress,
            Port,
            SlaveId,
            _timeoutMs,
            _byteOrder,
            poolSize);
        AttachPoolEvents(pool);
        AppendLog($"正在创建连接池 {IpAddress}:{Port} (站号={SlaveId}, 大小={poolSize}) ...");

        try
        {
            var result = await pool.ExecuteAsync(_ => Task.FromResult(OperateResult.Success())).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                AppendLog($"[ERR] 连接池初始化失败: {result.Message}");
                DetachPoolEvents(pool);
                pool.Dispose();
                IsConnected = false;
                ConnectionStatus = "连接失败";
                return;
            }

            _pool = pool;
            IsConnected = true;
            ConnectionStatus = "连接池已就绪";
            AppendLog($"[OK] 连接池已就绪 {IpAddress}:{Port} (活动={pool.ActiveCount}, 空闲={pool.IdleCount})");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 连接池异常: {ex.Message}");
            DetachPoolEvents(pool);
            pool.Dispose();
            IsConnected = false;
            ConnectionStatus = "未连接";
        }
    }

    [RelayCommand]
    private void Disconnect()
    {
        bool hadConnection = _client != null || _pool != null || IsConnected;
        if (!hadConnection) return;

        CloseClient();
        ClosePool();
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
        if (!HasConnection) { AppendLog("[ERR] 未连接，无法读取。"); return; }
        if (StartAddress < 0 || StartAddress > 65535) { AppendLog("[ERR] 起始地址越界 (0-65535)"); return; }
        if (Quantity < 1) { AppendLog("[ERR] 数量必须 ≥ 1"); return; }
        if (Quantity > 125) { AppendLog("[ERR] 寄存器数量超出 Modbus 规范 (≤125)"); return; }

        string addr = StartAddress.ToString();
        try
        {
            string? resultText = DataType switch
            {
                "Int16"  => await ReadAndFormatAsync(addr, client => client.ReadInt16Async(addr)),
                "UInt16" => await ReadAndFormatAsync(addr, client => client.ReadUInt16Async(addr)),
                "Int32"  => await ReadAndFormatAsync(addr, client => client.ReadInt32Async(addr)),
                "UInt32" => await ReadAndFormatAsync(addr, client => client.ReadUInt32Async(addr)),
                "Int64"  => await ReadAndFormatAsync(addr, client => client.ReadInt64Async(addr)),
                "UInt64" => await ReadAndFormatAsync(addr, client => client.ReadUInt64Async(addr)),
                "Float"  => await ReadAndFormatAsync(addr, client => client.ReadFloatAsync(addr)),
                "Double" => await ReadAndFormatAsync(addr, client => client.ReadDoubleAsync(addr)),
                "String" => await ReadAndFormatAsync(addr, client => client.ReadStringAsync(addr, (ushort)Quantity)),
                "Bool"   => await ReadAndFormatAsync(addr, client => client.ReadBoolAsync(addr)),
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

    private async Task<string?> ReadAndFormatAsync<T>(string addr, Func<ModbusTcpClient, Task<OperateResult<T>>> action)
    {
        var r = await ExecuteReadAsync(action).ConfigureAwait(true);
        if (!r.IsSuccess)
        {
            AppendLog($"[ERR] 读取 {addr} 失败: {r.Message}");
            return null;
        }
        object? content = r.Content;
        string text = content is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : Convert.ToString(content, CultureInfo.InvariantCulture) ?? "--";
        AppendLog($"[RD] {addr} ({DataType}) = {text}");
        return text;
    }

    private async Task<string?> ReadBytesAndFormatAsync(string addr, ushort len)
    {
        var r = await ExecuteReadAsync(client => client.ReadBytesAsync(addr, len)).ConfigureAwait(true);
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
        if (!HasConnection) { AppendLog("[ERR] 未连接，无法写入。"); return; }
        if (StartAddress < 0 || StartAddress > 65535) { AppendLog("[ERR] 起始地址越界 (0-65535)"); return; }

        string addr = StartAddress.ToString();
        try
        {
            string result = DataType switch
            {
                "Int16"  => await DoWriteAsync(addr, client => client.WriteAsync(addr, ParseShort(WriteValue, "Int16"))),
                "UInt16" => await DoWriteAsync(addr, client => client.WriteAsync(addr, ParseUShort(WriteValue))),
                "Int32"  => await DoWriteAsync(addr, client => client.WriteAsync(addr, ParseInt(WriteValue, "Int32"))),
                "Float"  => await DoWriteAsync(addr, client => client.WriteAsync(addr, ParseFloat(WriteValue))),
                "String" => await DoWriteAsync(addr, client => client.WriteAsync(addr, WriteValue ?? string.Empty)),
                "Bool"   => await DoWriteAsync(addr, client => client.WriteAsync(addr, ParseBool(WriteValue))),
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

    private async Task<string> DoWriteAsync(string addr, Func<ModbusTcpClient, Task<OperateResult>> action)
    {
        var r = await ExecuteWriteAsync(action).ConfigureAwait(true);
        return r.IsSuccess
            ? $"[WR] {addr} ({DataType}) = {WriteValue} 成功"
            : $"[ERR] 写入 {addr} 失败: {r.Message}";
    }

    private Task<OperateResult<T>> ExecuteReadAsync<T>(Func<ModbusTcpClient, Task<OperateResult<T>>> operation)
    {
        if (_pool != null) return _pool.ExecuteAsync(operation);
        if (_client != null) return operation(_client);
        return Task.FromResult(OperateResult<T>.Failed("未连接"));
    }

    private Task<OperateResult> ExecuteWriteAsync(Func<ModbusTcpClient, Task<OperateResult>> operation)
    {
        if (_pool != null) return _pool.ExecuteAsync(operation);
        if (_client != null) return operation(_client);
        return Task.FromResult(OperateResult.Failed("未连接"));
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

    private static Endianness ParseByteOrder(string? value)
    {
        return Enum.TryParse<Endianness>(value, ignoreCase: true, out var parsed)
            ? parsed
            : Endianness.BigEndian;
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
            var server = new ModbusTcpServer(VirtualServerPort);
            // 预设 4 区数据，供联机自测
            server.SetHoldingRegister(0, 0x1234);
            server.SetHoldingRegister(1, 0x5678);
            server.SetCoil(0, true);
            server.SetCoil(1, false);
            server.Start();

            _server = server;
            IsServerRunning = true;
            AppendLog($"[SRV] 内置 Modbus TCP Server 已启动: 127.0.0.1:{VirtualServerPort}");
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
    private void ClearLog()
    {
        LogLines.Clear();
        _packetRecorder.Clear();
    }

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

    private void AttachPoolEvents(ModbusTcpConnectionPool pool)
    {
        pool.OnMessageSent += Pool_OnMessageSent;
        pool.OnMessageReceived += Pool_OnMessageReceived;
        pool.OnError += Pool_OnError;
    }

    private void DetachPoolEvents(ModbusTcpConnectionPool pool)
    {
        pool.OnMessageSent -= Pool_OnMessageSent;
        pool.OnMessageReceived -= Pool_OnMessageReceived;
        pool.OnError -= Pool_OnError;
    }

    private void Client_OnMessageSent(object? sender, string hex)     => RecordPacket("TX", hex, ModbusPacketDirection.Request);
    private void Client_OnMessageReceived(object? sender, string hex) => RecordPacket("RX", hex, ModbusPacketDirection.Response);
    private void Client_OnError(object? sender, string e)            => AppendLog($"[ERR] {e}");
    private void Pool_OnMessageSent(object? sender, string hex)       => RecordPacket("TX", hex, ModbusPacketDirection.Request);
    private void Pool_OnMessageReceived(object? sender, string hex)   => RecordPacket("RX", hex, ModbusPacketDirection.Response);
    private void Pool_OnError(object? sender, string e)               => AppendLog($"[ERR] {e}");

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

    private void RecordPacket(string direction, string hex, ModbusPacketDirection packetDirection)
    {
        // 解析在任意线程（线程安全），日志输出回 UI 线程
        if (_dispatcher.CheckAccess())
        {
            _packetRecorder.RecordMbap("modbus-tcp", direction, hex, packetDirection, AppendLog);
        }
        else
        {
            string hexCopy = hex;
            _dispatcher.BeginInvoke(new Action(() =>
                _packetRecorder.RecordMbap("modbus-tcp", direction, hexCopy, packetDirection, AppendLog)));
        }
    }

    private void CloseClient()
    {
        var client = _client;
        if (client == null) return;

        try { client.Disconnect(); } catch { /* 忽略 */ }
        DetachClientEvents(client);
        client.Dispose();
        _client = null;
    }

    private void ClosePool()
    {
        var pool = _pool;
        if (pool == null) return;

        DetachPoolEvents(pool);
        pool.Dispose();
        _pool = null;
    }

    // ═══════════════════════════════════════════
    //  导出
    // ═══════════════════════════════════════════

    [RelayCommand]
    private void ExportLog()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"Nexus_ModbusTcp_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllLines(dlg.FileName, LogLines);
            AppendLog("[OK] 日志已导出: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 导出日志失败: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportPacketJsonl()
    {
        if (_packetRecorder.Count == 0)
        {
            AppendLog("[WARN] 没有可导出的 Modbus 报文。");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "JSON Lines (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            FileName = $"Nexus_ModbusTcp_Packets_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            if (_packetRecorder.ExportJsonl(dlg.FileName))
                AppendLog("[OK] 报文 JSONL 已导出: " + dlg.FileName);
            else
                AppendLog("[ERR] 导出 JSONL 失败");
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 导出 JSONL 失败: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    //  批量读写
    // ═══════════════════════════════════════════

    /// <summary>批量读写条目列表。</summary>
    public ObservableCollection<BatchReadWriteEntry> BatchEntries { get; } = new();

    [ObservableProperty] private string _batchAddressInput = "0, 1, 2, 3";

    [RelayCommand]
    private void ParseBatchAddresses()
    {
        BatchEntries.Clear();
        if (string.IsNullOrWhiteSpace(BatchAddressInput)) return;

        foreach (var part in BatchAddressInput.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var addr))
                BatchEntries.Add(new BatchReadWriteEntry { Address = addr, DataType = "Int16" });
        }
        AppendLog($"[BAT] 已解析 {BatchEntries.Count} 个地址");
    }

    [RelayCommand]
    private async Task BatchReadAllAsync()
    {
        if (!HasConnection) { AppendLog("[ERR] 未连接"); return; }
        if (BatchEntries.Count == 0) { AppendLog("[ERR] 批量列表为空"); return; }

        int ok = 0, fail = 0;
        foreach (var entry in BatchEntries)
        {
            try
            {
                string addr = entry.Address.ToString();
                string? result = entry.DataType switch
                {
                    "Int16"  => await ReadBatchEntryAsync<short>(addr, c => c.ReadInt16Async(addr)),
                    "UInt16" => await ReadBatchEntryAsync<ushort>(addr, c => c.ReadUInt16Async(addr)),
                    "Int32"  => await ReadBatchEntryAsync<int>(addr, c => c.ReadInt32Async(addr)),
                    "Float"  => await ReadBatchEntryAsync<float>(addr, c => c.ReadFloatAsync(addr)),
                    "Bool"   => await ReadBatchEntryAsync<bool>(addr, c => c.ReadBoolAsync(addr)),
                    _        => await ReadBatchEntryAsync<short>(addr, c => c.ReadInt16Async(addr)),
                };

                if (result != null)
                {
                    entry.Value = result;
                    ok++;
                }
                else
                {
                    entry.Value = "(失败)";
                    fail++;
                }
            }
            catch (Exception ex)
            {
                entry.Value = $"ERR: {ex.Message}";
                fail++;
            }
        }
        AppendLog($"[BAT] 批量读取完成: {ok} 成功, {fail} 失败");
    }

    private async Task<string?> ReadBatchEntryAsync<T>(string addr, Func<ModbusTcpClient, Task<OperateResult<T>>> action)
    {
        var r = await ExecuteReadAsync(action).ConfigureAwait(true);
        if (!r.IsSuccess) return null;
        return r.Content is IFormattable f
            ? f.ToString(null, CultureInfo.InvariantCulture)
            : Convert.ToString(r.Content, CultureInfo.InvariantCulture);
    }

    [RelayCommand]
    private void AddBatchEntry()
    {
        int nextAddr = BatchEntries.Count > 0
            ? BatchEntries[BatchEntries.Count - 1].Address + 1
            : 0;
        BatchEntries.Add(new BatchReadWriteEntry { Address = nextAddr, DataType = "Int16" });
    }

    [RelayCommand]
    private void RemoveBatchEntry(BatchReadWriteEntry entry)
    {
        BatchEntries.Remove(entry);
    }

    [RelayCommand]
    private void ClearBatchEntries()
    {
        BatchEntries.Clear();
    }

    [RelayCommand]
    private async Task ExportBatchCsvAsync()
    {
        if (BatchEntries.Count == 0) { AppendLog("[WARN] 批量列表为空"); return; }

        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|All (*.*)|*.*",
            FileName = $"Nexus_BatchRead_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using (var writer = new StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8))
            {
                await writer.WriteLineAsync("Address,DataType,Value").ConfigureAwait(false);
                foreach (var entry in BatchEntries)
                    await writer.WriteLineAsync($"{entry.Address},{entry.DataType},{entry.Value}").ConfigureAwait(false);
            }
            AppendLog("[OK] 批量数据已导出: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] 导出失败: " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════
    //  简易批量读取（逗号分隔地址）
    // ═══════════════════════════════════════════

    [ObservableProperty] private string _batchAddresses = "0, 10, 20, 100";
    [ObservableProperty] private string _batchResults = string.Empty;

    [RelayCommand]
    private async Task BatchRead()
    {
        if (!HasConnection) { AppendLog("[ERR] 未连接"); return; }

        var addresses = BatchAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a.Trim()).ToList();

        var sb = new StringBuilder();
        foreach (var addr in addresses)
        {
            var result = await ExecuteReadAsync(client => client.ReadInt16Async(addr)).ConfigureAwait(true);
            sb.AppendLine(result.IsSuccess ? $"{addr} = {result.Content}" : $"{addr} = 错误: {result.Message}");
        }
        BatchResults = sb.ToString();
    }

    // ═══════════════════════════════════════════
    //  释放
    // ═══════════════════════════════════════════

    // ── 示例代码 ──────────────────────────────

    public string SampleCode => @"using Nexus.Modbus;

// 创建客户端
var client = new ModbusTcpClient(""192.168.1.100"", 502) { SlaveAddress = 1 };
client.Connect();

// 读取保持寄存器 (FC03)
var result = client.ReadInt16(""40001"");
if (result.IsSuccess)
    Console.WriteLine($""值: {result.Content}"");

// 读取输入寄存器 (FC04)
var input = client.ReadInt16(""30001"");

// 读取线圈 (FC01)
var coil = client.ReadBool(""00001"");

// 写入单个寄存器 (FC06)
client.Write(""40001"", (short)123);

// 写入多个寄存器 (FC16)
client.Write(""40001"", (int)12345);

// 写入线圈 (FC05)
client.Write(""00001"", true);

// 批量读写 (FC23)
var batch = client.ReadWriteMultipleRegisters(
    readAddress: 0, readCount: 10,
    writeAddress: 100, writeData: new byte[] { 0x00, 0x01 });

client.Disconnect();";

    [RelayCommand]
    private void CopyCode()
    {
        try { Clipboard.SetText(SampleCode); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Disconnect(); } catch { /* 忽略 */ }
        try { StopServer(); } catch { /* 忽略 */ }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 批量读写条目 — 用于表格形式的多地址读写。
/// </summary>
public class BatchReadWriteEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private int _address;
    private string _dataType = "Int16";
    private string _value = "";

    public int Address { get => _address; set => SetProperty(ref _address, value); }
    public string DataType { get => _dataType; set => SetProperty(ref _dataType, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}
