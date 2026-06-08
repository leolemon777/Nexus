using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.App.Services;

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

    public string[] DataTypes { get; } =
    {
        "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Float", "Double", "String", "Bool", "Bytes"
    };

    public virtual string AddressHint => "e.g. D100, M200";
    public abstract string ProtocolName { get; }
    public ObservableCollection<string> LogLines { get; } = new();
    private readonly Dispatcher _dispatcher;
    private bool _disposed;
    private const int LogCap = 500;

    protected ProtocolViewModelBase()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        AppendLog(ProtocolName + " ready.");
    }

    protected abstract OperateResult DoConnect();
    protected abstract void DoDisconnect();
    protected abstract IReadWriteDevice? GetClient();

    // ── 地址变更时自动校验 ────────────────

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
                "Int16"  => await ReadFmt(addr, () => client.ReadInt16Async(addr)),
                "UInt16" => await ReadFmt(addr, () => client.ReadUInt16Async(addr)),
                "Int32"  => await ReadFmt(addr, () => client.ReadInt32Async(addr)),
                "UInt32" => await ReadFmt(addr, () => client.ReadUInt32Async(addr)),
                "Int64"  => await ReadFmt(addr, () => client.ReadInt64Async(addr)),
                "UInt64" => await ReadFmt(addr, () => client.ReadUInt64Async(addr)),
                "Float"  => await ReadFmt(addr, () => client.ReadFloatAsync(addr)),
                "Double" => await ReadFmt(addr, () => client.ReadDoubleAsync(addr)),
                "String" => await ReadFmt(addr, () => client.ReadStringAsync(addr, 20)),
                "Bool"   => await ReadFmt(addr, () => client.ReadBoolAsync(addr)),
                "Bytes"  => await ReadBytesFmt(client, addr, 10),
                _        => "[ERR] Unknown data type",
            };
            if (resultText != null) LastResult = resultText;
        }
        catch (Exception ex) { AppendLog("[ERR] Read error: " + ex.Message); AppendDiagnostic(ex.Message, ex); }
    }

    private async Task<string?> ReadFmt<T>(string addr, Func<Task<OperateResult<T>>> action)
    {
        var r = await action().ConfigureAwait(true);
        if (!r.IsSuccess) { AppendLog("[ERR] Read " + addr + " failed: " + r.Message); return null; }
        string text = r.Content?.ToString() ?? "--";
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

        // 写入确认
        if (!WriteConfirmationService.Confirm(addr, DataType, WriteValue))
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
        }
        catch (FormatException fex) { AppendLog("[ERR] Write parse: " + fex.Message); }
        catch (Exception ex) { AppendLog("[ERR] Write error: " + ex.Message); AppendDiagnostic(ex.Message, ex); }
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
    private void ClearLog() => LogLines.Clear();

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisconnectCmd(); } catch { }
        GC.SuppressFinalize(this);
    }
}
