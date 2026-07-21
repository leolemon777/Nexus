using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Nexus.App.Models;
using Nexus.App.Services;
using Nexus.Modbus;
using Nexus;

namespace Nexus.App.ViewModels;

public abstract partial class ProtocolViewModelBase : ObservableObject, IDisposable
{
    [ObservableProperty] private string _address = "D100";
    [ObservableProperty] private string _writeValue = string.Empty;
    [ObservableProperty] private string _dataType = "Int16";
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _lastResult = "--";
    [ObservableProperty] private string _addressValidationHint = string.Empty;
    [ObservableProperty] private bool _isAddressValid = true;
    [ObservableProperty] private string _multiFormatResult = string.Empty;

    // ── H-1: 实时报文监控属性 ──
    [ObservableProperty] private double _lastLatencyMs;
    [ObservableProperty] private int _totalSent;
    [ObservableProperty] private int _totalReceived;
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private int _totalOperations;
    [ObservableProperty] private bool _isPacketMonitorPaused;
    [ObservableProperty] private string _statusBarText = "未连接";

    public string[] DataTypes { get; } =
    {
        "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Float", "Double", "String", "Bool", "Bytes",
        "Hex16", "Hex32", "Hex64",
        "BCD16", "BCD32",
        "Word", "DWord",
        "Char",
        "Int16-Swap", "Int32-Swap", "Float-Swap", "Double-Swap"
    };

    public virtual string AddressHint => "e.g. D100, M200";
    public abstract string ProtocolName { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>H-1: 实时报文列表（右侧面板绑定）。</summary>
    public ObservableCollection<Models.PacketEntry> Packets { get; } = new();
    private const int PacketCap = 200;

    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private const int LogCap = 500;

    // ── 可选包记录服务 ──────────────────────
    private readonly PacketRecorderService? _packetRecorder;
    private readonly string _packetProtocol = string.Empty;
    private readonly ModbusPacketTransport _packetTransport;

    // ── 写入确认 / 审计（可注入，WS-C）──────────────────────
    // 子类不感知此字段：通过服务定位（App.Services）惰性解析默认实现，
    // 单测可通过 WriteConfirmation 属性注入 fake，避免弹真实 MessageBox。
    private IWriteConfirmationService? _writeConfirmation;

    /// <summary>
    /// 写入确认 + 审计服务。子类构造时无需传入；首次访问时从
    /// <c>App.Services</c> 解析（与 DiagnosticBundleService 同样的既有模式）。
    /// 单测可直接赋值 fake 以避免弹窗。
    /// </summary>
    protected IWriteConfirmationService WriteConfirmation
    {
        get => _writeConfirmation ??= ResolveWriteConfirmation();
        set => _writeConfirmation = value;
    }

    /// <summary>
    /// 从 <c>App.Services</c> 解析 <see cref="IWriteConfirmationService"/>；
    /// 若容器不可用（设计时/早期启动/单测无 host）则回退到
    /// <see cref="NullWriteConfirmation"/>（放行但不审计），避免 NRE 中断 UI。
    /// </summary>
    private static IWriteConfirmationService ResolveWriteConfirmation()
    {
        try
        {
            var app = Application.Current as App;
            if (app != null)
            {
                var svc = app.Services.GetService(typeof(IWriteConfirmationService)) as IWriteConfirmationService;
                if (svc != null) return svc;
            }
        }
        catch
        {
            // 容器未就绪 — 回退到安全默认。
        }
        return NullWriteConfirmation.Instance;
    }

    protected ProtocolViewModelBase()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AppendLog(ProtocolName + " ready.");
    }

    /// <summary>
    /// 构造并启用 Modbus 包记录。
    /// </summary>
    /// <param name="packetRecorder">共享包记录服务。</param>
    /// <param name="protocol">协议标签（如 "modbus-udp"）。</param>
    /// <param name="transport">Modbus 传输类型。</param>
    protected ProtocolViewModelBase(PacketRecorderService packetRecorder, string protocol, ModbusPacketTransport transport)
    {
        _packetRecorder = packetRecorder;
        _packetProtocol = protocol;
        _packetTransport = transport;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AppendLog(ProtocolName + " ready.");
    }

    protected void UpdateMultiFormatResult(byte[] data)
    {
        if (data == null || data.Length < 2)
        {
            MultiFormatResult = "(数据不足)";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"原始字节: {DataConverter.ToHexString(data, 0, Math.Min(data.Length, 8))}");

        if (data.Length >= 2)
        {
            short i16 = DataConverter.ToInt16(data, 0);
            ushort u16 = DataConverter.ToUInt16(data, 0);
            sb.AppendLine($"Int16:    {i16,10}    UInt16:   {u16,10}");
            sb.AppendLine($"Hex16:    0x{u16:X4}          BCD16:    {DataConverter.DecodeBcd16(u16),10}");
            sb.AppendLine($"Word:     {u16} (0x{u16:X4})");
        }

        if (data.Length >= 4)
        {
            int i32 = DataConverter.ToInt32(data, 0);
            uint u32 = DataConverter.ToUInt32(data, 0);
            float f32 = DataConverter.ToFloat(data, 0);
            sb.AppendLine($"Int32:    {i32,10}    UInt32:   {u32,10}");
            sb.AppendLine($"Hex32:    0x{u32:X8}  BCD32:    {DataConverter.DecodeBcd32(u32),10}");
            sb.AppendLine($"Float:    {f32,10:F4}    DWord:    {u32} (0x{u32:X8})");
        }

        if (data.Length >= 8)
        {
            long i64 = DataConverter.ToInt64(data, 0);
            double f64 = DataConverter.ToDouble(data, 0);
            sb.AppendLine($"Int64:    {i64,20}    Double:   {f64,20:F6}");
            sb.AppendLine($"Hex64:    0x{i64:X16}");
        }

        MultiFormatResult = sb.ToString();
    }

    protected abstract OperateResult DoConnect();
    protected abstract void DoDisconnect();
    protected abstract IReadWriteDevice? GetClient();

    // ── 地址变更时自动校验 ───────────────

    partial void OnAddressChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AddressValidationHint = string.Empty;
            IsAddressValid = true;
            return;
        }
        var result = AddressValidator.Validate(ProtocolName, value);
        IsAddressValid = result.IsValid;
        AddressValidationHint = result.IsValid ? $"✔ {result.Area}: {result.Message}" : $"✘ {result.Message}";
    }

    // ── 连接 ──────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected || _disposed) return;
        AppendLog("Connecting...");
        try
        {
            var result = await Task.Run(() => DoConnect()).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                AppendLog("[ERR] Connect failed: " + result.Message);
                AppendDiagnostic(result.Message);
                IsConnected = false;
                ConnectionStatus = "Failed";
                return;
            }
            IsConnected = true;
            ConnectionStatus = "Connected";
            AppendLog("[OK] Connected.");
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Connect error: " + ex.Message);
            AppendDiagnostic(ex.Message, ex);
            IsConnected = false;
            ConnectionStatus = "Disconnected";
        }
    }

    [RelayCommand]
    private void DisconnectCmd()
    {
        try { DoDisconnect(); } catch { }
        IsConnected = false;
        ConnectionStatus = "Disconnected";
        AppendLog("[--] Disconnected.");
    }

    // ── 读取（含地址校验）──────────────────

    [RelayCommand]
    private async Task ReadAsync()
    {
        var client = GetClient();
        if (client == null) { AppendLog("[ERR] Not connected."); return; }
        string addr = Address.Trim();
        if (string.IsNullOrEmpty(addr)) { AppendLog("[ERR] Address is empty."); return; }

        // 地址校验
        var validation = AddressValidator.Validate(ProtocolName, addr);
        if (!validation.IsValid)
        {
            AppendLog("[WARN] Address validation: " + validation.Message);
        }

        try
        {
            string? resultText = DataType switch
            {
                "Int16"       => await ReadFmt(addr, () => client.ReadInt16Async(addr)),
                "UInt16"      => await ReadFmt(addr, () => client.ReadUInt16Async(addr)),
                "Int32"       => await ReadFmt(addr, () => client.ReadInt32Async(addr)),
                "UInt32"      => await ReadFmt(addr, () => client.ReadUInt32Async(addr)),
                "Int64"       => await ReadFmt(addr, () => client.ReadInt64Async(addr)),
                "UInt64"      => await ReadFmt(addr, () => client.ReadUInt64Async(addr)),
                "Float"       => await ReadFmt(addr, () => client.ReadFloatAsync(addr)),
                "Double"      => await ReadFmt(addr, () => client.ReadDoubleAsync(addr)),
                "String"      => await ReadFmt(addr, () => client.ReadStringAsync(addr, 20)),
                "Bool"        => await ReadFmt(addr, () => client.ReadBoolAsync(addr)),
                "Bytes"       => await ReadBytesFmt(client, addr, 10),
                "BCD16"       => await ReadRawFmt(client, addr, 2, d => DataConverter.DecodeBcd16(DataConverter.ToUInt16(d, 0)).ToString()),
                "Hex16"       => await ReadRawFmt(client, addr, 2, d => DataConverter.ToHex16(DataConverter.ToUInt16(d, 0))),
                "Word"        => await ReadRawFmt(client, addr, 2, d => DataConverter.ToWordString(d)),
                "Char"        => await ReadRawFmt(client, addr, 1, d => DataConverter.ToChar(d).ToString()),
                "Hex32"       => await ReadRawFmt(client, addr, 4, d => DataConverter.ToHex32(DataConverter.ToUInt32(d, 0))),
                "BCD32"       => await ReadRawFmt(client, addr, 4, d => DataConverter.DecodeBcd32(DataConverter.ToUInt32(d, 0)).ToString()),
                "DWord"       => await ReadRawFmt(client, addr, 4, d => DataConverter.ToDWordString(d)),
                "Hex64"       => await ReadRawFmt(client, addr, 8, d => DataConverter.ToHex64(DataConverter.ToInt64(d, 0))),
                "Int16-Swap"  => await ReadRawFmt(client, addr, 2, d => { var s = (byte[])d.Clone(); DataConverter.Reorder(s, 0, 2, Endianness.LittleEndian); return DataConverter.ToInt16(s, 0).ToString(); }),
                "Int32-Swap"  => await ReadRawFmt(client, addr, 4, d => { var s = (byte[])d.Clone(); DataConverter.Reorder(s, 0, 4, Endianness.LittleEndian); return DataConverter.ToInt32(s, 0).ToString(); }),
                "Float-Swap"  => await ReadRawFmt(client, addr, 4, d => { var s = (byte[])d.Clone(); DataConverter.Reorder(s, 0, 4, Endianness.LittleEndian); return DataConverter.ToFloat(s, 0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture); }),
                "Double-Swap" => await ReadRawFmt(client, addr, 8, d => { var s = (byte[])d.Clone(); DataConverter.Reorder(s, 0, 8, Endianness.LittleEndian); return DataConverter.ToDouble(s, 0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture); }),
                _             => "[ERR] Unknown data type",
            };
            if (resultText != null) LastResult = resultText;
        }
        catch (Exception ex) { AppendLog("[ERR] Read error: " + ex.Message); AppendDiagnostic(ex.Message, ex); }
    }

    private async Task<string?> ReadFmt<T>(string addr, Func<Task<OperateResult<T>>> action)
    {
        var r = await action().ConfigureAwait(true);
        if (!r.IsSuccess) { AppendLog("[ERR] Read " + addr + " failed: " + r.Message); return null; }
        object? content = r.Content;
        string text = content?.ToString() ?? "--";
        AppendLog("[RD] " + addr + " (" + DataType + ") = " + text);
        return text;
    }

    private async Task<string?> ReadBytesFmt(IReadWriteDevice client, string addr, ushort len)
    {
        var r = await client.ReadBytesAsync(addr, len).ConfigureAwait(true);
        if (!r.IsSuccess) { AppendLog("[ERR] Read " + addr + " failed: " + r.Message); return null; }
        string hex = BitConverter.ToString(r.Content).Replace("-", " ");
        AppendLog("[RD] " + addr + " (" + len + " bytes) = " + hex);
        return hex;
    }

    private async Task<string?> ReadRawFmt(IReadWriteDevice client, string addr, ushort len, Func<byte[], string> convert)
    {
        var r = await client.ReadBytesAsync(addr, len).ConfigureAwait(true);
        if (!r.IsSuccess) { AppendLog("[ERR] Read " + addr + " failed: " + r.Message); return null; }
        string text = convert(r.Content);
        UpdateMultiFormatResult(r.Content);
        AppendLog("[RD] " + addr + " (" + DataType + ") = " + text);
        return text;
    }

    // ── 写入（含地址校验 + 确认对话框）──────

    [RelayCommand]
    private async Task WriteAsync()
    {
        var client = GetClient();
        if (client == null) { AppendLog("[ERR] Not connected."); return; }
        string addr = Address.Trim();
        if (string.IsNullOrEmpty(addr)) { AppendLog("[ERR] Address is empty."); return; }

        // 地址校验
        var validation = AddressValidator.Validate(ProtocolName, addr);
        if (!validation.IsValid)
        {
            AppendLog("[WARN] Address validation: " + validation.Message);
        }

        // 写入确认（同时追加 confirmed/skipped 审计记录）
        if (!WriteConfirmation.ConfirmWrite(ProtocolName, addr, DataType, WriteValue))
        {
            AppendLog("[SKIP] Write cancelled by user.");
            return;
        }

        try
        {
            string result = DataType switch
            {
                "Int16"  => await DoWrite(addr, () => client.WriteAsync(addr, ParseShort(WriteValue))),
                "UInt16" => await DoWrite(addr, () => client.WriteAsync(addr, ParseUShort(WriteValue))),
                "Int32"  => await DoWrite(addr, () => client.WriteAsync(addr, ParseInt(WriteValue))),
                "Float"  => await DoWrite(addr, () => client.WriteAsync(addr, ParseFloat(WriteValue))),
                "String" => await DoWrite(addr, () => client.WriteAsync(addr, WriteValue ?? string.Empty)),
                "Bool"   => await DoWrite(addr, () => client.WriteAsync(addr, ParseBool(WriteValue))),
                _        => "[ERR] Unsupported type for writing",
            };
            AppendLog(result);
            bool ok = result.StartsWith("[WR]", StringComparison.Ordinal);
            WriteConfirmation.RecordOutcome(ProtocolName, addr, DataType, WriteValue, ok, ok ? null : result);
        }
        catch (FormatException fex)
        {
            AppendLog("[ERR] Write parse: " + fex.Message);
            WriteConfirmation.RecordOutcome(ProtocolName, addr, DataType, WriteValue, succeeded: false, fex.Message);
        }
        catch (Exception ex)
        {
            AppendLog("[ERR] Write error: " + ex.Message);
            AppendDiagnostic(ex.Message, ex);
            WriteConfirmation.RecordOutcome(ProtocolName, addr, DataType, WriteValue, succeeded: false, ex.Message);
        }
    }

    private static async Task<string> DoWrite(string addr, Func<Task<OperateResult>> action)
    {
        var r = await action().ConfigureAwait(true);
        return r.IsSuccess ? "[WR] " + addr + " OK" : "[ERR] Write " + addr + " failed: " + r.Message;
    }

    private static short ParseShort(string s)
        => short.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
            : throw new FormatException("Cannot parse Int16: " + s);
    private static ushort ParseUShort(string s)
        => ushort.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
            : throw new FormatException("Cannot parse UInt16: " + s);
    private static int ParseInt(string s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v
            : throw new FormatException("Cannot parse Int32: " + s);
    private static float ParseFloat(string s)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v
            : throw new FormatException("Cannot parse Float: " + s);
    private static bool ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().ToLowerInvariant();
        return s is "1" or "true" or "on" or "yes";
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogLines.Clear();
        _packetRecorder?.Clear();
    }

    [RelayCommand]
    private void ExportLog()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "文本文件|*.txt|CSV文件|*.csv|所有文件|*.*",
            FileName = $"{ProtocolName}_log_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                File.WriteAllLines(dialog.FileName, LogLines);
                AppendLog($"[OK] 日志已导出: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                AppendLog($"[ERR] 导出失败: {ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void ExportDiagnosticBundle()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = $"nexus-diag-{ProtocolName}-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                Filter = "ZIP 文件 (*.zip)|*.zip",
                Title = "导出诊断包"
            };

            if (dialog.ShowDialog() != true) return;

            var bundle = ((App)Application.Current).Services.GetService(typeof(DiagnosticBundleService)) as DiagnosticBundleService;
            if (bundle == null)
            {
                AppendLog("[ERR] 诊断服务不可用");
                return;
            }

            var sessionLog = string.Join(Environment.NewLine, LogLines);
            var info = BuildConnectionInfo();

            var result = bundle.ExportBundle(dialog.FileName, info, sessionLog);
            if (result.IsSuccess)
                AppendLog($"✅ 诊断包已导出: {dialog.FileName}");
            else
                AppendLog($"[ERR] 导出失败: {result.Message}");
        }
        catch (Exception ex)
        {
            AppendLog($"[ERR] 导出异常: {ex.Message}");
        }
    }

    private ConnectionInfo? BuildConnectionInfo()
    {
        var info = new ConnectionInfo { Protocol = ProtocolName };

        // Try to extract connection details from the ViewModel's properties
        // via reflection to avoid tight coupling with specific VM types
        var type = GetType();

        var ipProp = type.GetProperty("IpAddress");
        if (ipProp != null) info.Host = ipProp.GetValue(this)?.ToString() ?? "";

        var portProp = type.GetProperty("Port");
        if (portProp != null && portProp.GetValue(this) is int port) info.Port = port;

        var slaveProp = type.GetProperty("SlaveId");
        if (slaveProp != null && slaveProp.GetValue(this) is byte station) info.Station = station;

        var timeoutProp = type.GetProperty("Timeout");
        if (timeoutProp != null && timeoutProp.GetValue(this) is int timeout) info.Timeout = timeout;

        return info;
    }

    protected void AppendLog(string line)
    {
        string stamped = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line;
        if (_dispatcher.CheckAccess()) { DoAppend(stamped); }
        else { _dispatcher.BeginInvoke(new Action(() => DoAppend(stamped))); }
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

    // ── H-1: 报文记录（右侧面板）──────────────────

    /// <summary>记录一条报文到右侧实时监控面板。</summary>
    /// <param name="isTX">true=发送, false=接收。</param>
    /// <param name="data">原始字节。</param>
    /// <param name="latencyMs">延迟（仅 RX 行有效）。</param>
    protected void RecordPacket(bool isTX, byte[] data, double latencyMs = 0)
    {
        if (data == null || data.Length == 0) return;
        if (IsPacketMonitorPaused) return;

        var entry = new Models.PacketEntry
        {
            IsTX = isTX,
            HexData = DataConverter.ToHexString(data),
            LatencyMs = latencyMs
        };

        if (isTX) TotalSent++;
        else
        {
            TotalReceived++;
            if (latencyMs > 0) LastLatencyMs = latencyMs;
        }

        if (_dispatcher.CheckAccess()) { DoRecordPacket(entry); }
        else { _dispatcher.BeginInvoke(new Action(() => DoRecordPacket(entry))); }
    }

    private void DoRecordPacket(Models.PacketEntry entry)
    {
        Packets.Add(entry);
        if (Packets.Count > PacketCap)
            Packets.RemoveAt(0);
    }

    /// <summary>成功率百分比文本。</summary>
    public string SuccessRateText =>
        TotalOperations == 0 ? "--" : $"{(double)SuccessCount / TotalOperations * 100:F1}%";

    /// <summary>切换报文监控暂停状态（右侧面板按钮绑定）。</summary>
    [RelayCommand]
    private void PausePacketMonitor()
    {
        IsPacketMonitorPaused = !IsPacketMonitorPaused;
    }

    /// <summary>记录一次操作结果（用于统计成功率）。</summary>
    protected void RecordOperationResult(bool success)
    {
        TotalOperations++;
        if (success) SuccessCount++;
        OnPropertyChanged(nameof(SuccessRateText));
    }

    /// <summary>
    /// 记录 Modbus 报文并追加解析后的 [PKT] 行到日志。
    /// 仅在构造时传入了 <see cref="PacketRecorderService"/> 时生效。
    /// </summary>
    protected void RecordPacket(string direction, string hex, ModbusPacketDirection packetDirection)
    {
        if (_packetRecorder == null)
        {
            // 没有包记录服务，回退到简单的 raw 日志
            AppendLog("[" + direction + "] " + hex);
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            RecordPacketCore(direction, hex, packetDirection);
        }
        else
        {
            string hexCopy = hex;
            _dispatcher.BeginInvoke(new Action(() =>
                RecordPacketCore(direction, hexCopy, packetDirection)));
        }
    }

    private void RecordPacketCore(string direction, string hex, ModbusPacketDirection packetDirection)
    {
        var recorder = _packetRecorder;
        if (recorder == null) return;

        switch (_packetTransport)
        {
            case ModbusPacketTransport.Tcp:
            case ModbusPacketTransport.Udp:
                recorder.RecordMbap(_packetProtocol, direction, hex, packetDirection, AppendLog);
                break;
            case ModbusPacketTransport.Rtu:
            case ModbusPacketTransport.RtuOverTcp:
                recorder.RecordRtu(_packetProtocol, direction, hex, packetDirection, AppendLog);
                break;
            case ModbusPacketTransport.Ascii:
                recorder.RecordAscii(_packetProtocol, direction, hex, packetDirection, AppendLog);
                break;
        }
    }

    /// <summary>
    /// 将异常翻译为中文诊断建议并追加到日志。
    /// </summary>
    private void AppendDiagnostic(string message, Exception? ex = null)
    {
        try
        {
            var diag = ChineseDiagnostics.Diagnose(
                ex ?? new Exception(message), ProtocolName);
            AppendLog($"💡 {diag.Title}: {diag.Detail}");
            foreach (var line in diag.Suggestions.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    AppendLog($"   {trimmed}");
            }
        }
        catch
        {
            // 诊断本身不应中断主流程
        }
    }

    // ── 示例代码 ──────────────────────────────

    /// <summary>当前协议的示例代码（子类覆盖）。</summary>
    public virtual string SampleCode => $"// {ProtocolName}\n// 在此协议页面查看示例代码";

    /// <summary>复制示例代码到剪贴板。</summary>
    [RelayCommand]
    private void CopyCode()
    {
        try { Clipboard.SetText(SampleCode); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisconnectCmd(); } catch { }
        GC.SuppressFinalize(this);
    }
}
