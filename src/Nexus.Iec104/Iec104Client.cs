using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nexus;
using System.Linq;

namespace Nexus.Iec104
{
    public class Iec104Client : TcpDeviceBase, IBatchReadWrite, ISubscribeDevice
    {
        private const byte START_BYTE = 0x68;

        private const byte U_STARTDT_ACT = 0x07;
        private const byte U_STARTDT_CON = 0x0B;
        private const byte U_STOPDT_ACT = 0x13;
        private const byte U_STOPDT_CON = 0x23;
        private const byte U_TESTFR_ACT = 0x43;
        private const byte U_TESTFR_CON = 0x83;

        public int CommonAddress { get; set; } = 1;
        public int T0 { get; set; } = 30000;
        public int T1 { get; set; } = 15000;
        public int T2 { get; set; } = 10000;
        public int T3 { get; set; } = 20000;

        private int _sendSeq;
        private int _recvSeq;
        private int _ackSeq;
        private readonly object _seqLock = new object();

        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private volatile bool _started;

        private readonly ConcurrentDictionary<int, Iec104DataPoint> _dataCache
            = new ConcurrentDictionary<int, Iec104DataPoint>();

        private readonly ConcurrentDictionary<int, TaskCompletionSource<Iec104Asdu>> _pendingRequests
            = new ConcurrentDictionary<int, TaskCompletionSource<Iec104Asdu>>();

        private DateTime _lastActivity;
        private Timer? _t3Timer;

        public event EventHandler<Iec104DataPoint>? OnDataReceived;
        public event EventHandler<Iec104Asdu>? OnAsduReceived;

        public Iec104Client(string ip, int port = 2404, int timeout = 10000)
            : base(ip, port, timeout)
        {
            SetPersistentConnection();
        }

        protected override int ResponseHeaderLength => 2;

        protected override int GetResponsePayloadLength(byte[] header) => header[1];

        // ── Connection Management ─────────────────

        public override OperateResult Connect()
        {
            var result = base.Connect();
            if (!result.IsSuccess) return result;

            var startResult = PerformStartDT();
            if (!startResult.IsSuccess)
            {
                Disconnect();
                return startResult;
            }

            StartReceiveLoop();
            ResetT3Timer();
            return OperateResult.Success();
        }

        public override async Task<OperateResult> ConnectAsync()
        {
            var result = await base.ConnectAsync().ConfigureAwait(false);
            if (!result.IsSuccess) return result;

            var startResult = await PerformStartDTAsync().ConfigureAwait(false);
            if (!startResult.IsSuccess)
            {
                Disconnect();
                return startResult;
            }

            StartReceiveLoop();
            ResetT3Timer();
            return OperateResult.Success();
        }

        public new void Disconnect()
        {
            if (_started)
            {
                try { SendUFrame(U_STOPDT_ACT); } catch { }
            }
            StopReceiveLoop();
            _dataCache.Clear();
            lock (_seqLock)
            {
                foreach (var tcs in _pendingRequests.Values)
                    tcs.TrySetCanceled();
                _pendingRequests.Clear();
            }
            base.Disconnect();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopReceiveLoop();
                _t3Timer?.Dispose();
                _t3Timer = null;
            }
            base.Dispose(disposing);
        }

        // ── STARTDT/STOPDT ────────────────────────

        private OperateResult PerformStartDT()
        {
            SendUFrame(U_STARTDT_ACT);
            var frame = ReadOneFrame(T1);
            if (frame == null) return OperateResult.Failed("STARTDT 确认超时");
            if (frame.Length < 3 || frame[2] != U_STARTDT_CON) return OperateResult.Failed("STARTDT 确认无效");
            _started = true;
            Log.Info("STARTDT 已激活");
            return OperateResult.Success();
        }

        private async Task<OperateResult> PerformStartDTAsync()
        {
            SendUFrame(U_STARTDT_ACT);
            var frame = await ReadOneFrameAsync(T1).ConfigureAwait(false);
            if (frame == null) return OperateResult.Failed("STARTDT 确认超时");
            if (frame.Length < 3 || frame[2] != U_STARTDT_CON) return OperateResult.Failed("STARTDT 确认无效");
            _started = true;
            Log.Info("STARTDT 已激活");
            return OperateResult.Success();
        }

        private byte[]? ReadOneFrame(int timeoutMs)
        {
            NetworkStream? ns;
            lock (_lock) { ns = _stream; }
            if (ns == null) return null;

            var oldTimeout = ns.ReadTimeout;
            ns.ReadTimeout = timeoutMs;
            try
            {
                var startBuf = ReadExactSync(ns, 2);
                if (startBuf == null || startBuf[0] != START_BYTE) return null;

                int apduLen = startBuf[1];
                if (apduLen < 4 || apduLen > 253) return null;

                var payload = ReadExactSync(ns, apduLen);
                if (payload == null) return null;

                byte[] frame = new byte[2 + apduLen];
                frame[0] = startBuf[0];
                frame[1] = startBuf[1];
                Buffer.BlockCopy(payload, 0, frame, 2, apduLen);
                return frame;
            }
            catch { return null; }
            finally { ns.ReadTimeout = oldTimeout; }
        }

        private async Task<byte[]?> ReadOneFrameAsync(int timeoutMs)
        {
            NetworkStream? ns;
            lock (_lock) { ns = _stream; }
            if (ns == null) return null;

            using (var cts = new CancellationTokenSource(timeoutMs))
            {
                try
                {
                    var startBuf = await ReadExactAsync(ns, 2, cts.Token).ConfigureAwait(false);
                    if (startBuf == null || startBuf[0] != START_BYTE) return null;

                    int apduLen = startBuf[1];
                    if (apduLen < 4 || apduLen > 253) return null;

                    var payload = await ReadExactAsync(ns, apduLen, cts.Token).ConfigureAwait(false);
                    if (payload == null) return null;

                    byte[] frame = new byte[2 + apduLen];
                    frame[0] = startBuf[0];
                    frame[1] = startBuf[1];
                    Buffer.BlockCopy(payload, 0, frame, 2, apduLen);
                    return frame;
                }
                catch { return null; }
            }
        }

        // ── APCI Framing ──────────────────────────

        private byte[] BuildIFrame(byte[] asdu)
        {
            int sendSeq, recvSeq;
            lock (_seqLock)
            {
                sendSeq = _sendSeq;
                _sendSeq = (_sendSeq + 1) & 0x7FFF;
                recvSeq = _recvSeq;
            }

            int apduLen = 4 + asdu.Length;
            byte[] frame = new byte[2 + apduLen];
            frame[0] = START_BYTE;
            frame[1] = (byte)apduLen;
            frame[2] = (byte)((sendSeq << 1) & 0xFF);
            frame[3] = (byte)((sendSeq >> 7) & 0xFF);
            frame[4] = (byte)((recvSeq << 1) & 0xFF);
            frame[5] = (byte)((recvSeq >> 7) & 0xFF);
            Buffer.BlockCopy(asdu, 0, frame, 6, asdu.Length);
            return frame;
        }

        private byte[] BuildSFrame()
        {
            int recvSeq;
            lock (_seqLock) { recvSeq = _recvSeq; }
            return new byte[]
            {
                START_BYTE, 0x04,
                0x01, 0x00,
                (byte)((recvSeq << 1) & 0xFF),
                (byte)((recvSeq >> 7) & 0xFF)
            };
        }

        private byte[] BuildUFrame(byte control)
        {
            return new byte[]
            {
                START_BYTE, 0x04,
                control, 0x00, 0x00, 0x00
            };
        }

        private void SendUFrame(byte control)
        {
            var frame = BuildUFrame(control);
            SendRawFrame(frame);
        }

        private void SendSFrame()
        {
            var frame = BuildSFrame();
            SendRawFrame(frame);
        }

        private void SendIFrame(Iec104Asdu asdu)
        {
            byte[] asduBytes = asdu.Encode();
            byte[] frame = BuildIFrame(asduBytes);
            SendRawFrame(frame);
        }

        private void SendRawFrame(byte[] frame)
        {
            NetworkStream? ns;
            lock (_lock) { ns = _stream; }
            if (ns == null) throw new InvalidOperationException("未连接");

            Log.Debug($"TX → {DataConverter.ToHexString(frame)}");
            RaiseMessageSent(DataConverter.ToHexString(frame));
            ns.Write(frame, 0, frame.Length);
            lock (_seqLock) { _lastActivity = DateTime.UtcNow; }
        }

        // ── Receive Loop ──────────────────────────

        private void StartReceiveLoop()
        {
            _cts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
        }

        private void StopReceiveLoop()
        {
            _cts?.Cancel();
            try { _receiveTask?.Wait(2000); } catch { }
            _cts?.Dispose();
            _cts = null;
            _receiveTask = null;
        }

        private void ReceiveLoop(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = ReadFrame(ct);
                    if (frame == null) break;

                    Log.Debug($"RX ← {DataConverter.ToHexString(frame)}");
                    RaiseMessageReceived(DataConverter.ToHexString(frame));
                    lock (_seqLock) { _lastActivity = DateTime.UtcNow; }

                    if (frame.Length < 2) continue;
                    byte control0 = frame[2];

                    if ((control0 & 0x01) == 0)
                    {
                        ProcessIFrame(frame);
                    }
                    else if ((control0 & 0x03) == 0x01)
                    {
                        ProcessSFrame(frame);
                    }
                    else if ((control0 & 0x03) == 0x03)
                    {
                        ProcessUFrame(frame);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    Log.Error($"接收循环异常: {ex.Message}");
                    RaiseError(ex.Message);
                }
            }
        }

        private byte[]? ReadFrame(CancellationToken ct)
        {
            NetworkStream? ns;
            lock (_lock) { ns = _stream; }
            if (ns == null) return null;

            var startBuf = ReadExactSync(ns, 2);
            if (startBuf == null || startBuf[0] != START_BYTE) return null;

            int apduLen = startBuf[1];
            if (apduLen < 4 || apduLen > 253) return null;

            var payload = ReadExactSync(ns, apduLen);
            if (payload == null) return null;

            byte[] frame = new byte[2 + apduLen];
            frame[0] = startBuf[0];
            frame[1] = startBuf[1];
            Buffer.BlockCopy(payload, 0, frame, 2, apduLen);
            return frame;
        }

        private static byte[]? ReadExactSync(NetworkStream ns, int count)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = ns.Read(buf, offset, count - offset);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        private static async Task<byte[]?> ReadExactAsync(NetworkStream ns, int count, CancellationToken ct)
        {
            byte[] buf = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = await ns.ReadAsync(buf, offset, count - offset, ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return buf;
        }

        // ── Frame Processing ──────────────────────

        private void ProcessIFrame(byte[] frame)
        {
            if (frame.Length < 6) return;

            int remoteSeq = (frame[2] >> 1) | ((frame[3] & 0x7F) << 7);
            lock (_seqLock) { _ackSeq = remoteSeq; }

            int recvSeq = (frame[4] >> 1) | ((frame[5] & 0x7F) << 7);
            lock (_seqLock)
            {
                _recvSeq = (recvSeq + 1) & 0x7FFF;
            }

            byte[] asduData = new byte[frame.Length - 6];
            Buffer.BlockCopy(frame, 6, asduData, 0, asduData.Length);

            Iec104Asdu asdu;
            try { asdu = Iec104Asdu.Decode(asduData, 0); }
            catch (Exception ex)
            {
                Log.Error($"ASDU 解码失败: {ex.Message}");
                return;
            }

            OnAsduReceived?.Invoke(this, asdu);
            DispatchAsdu(asdu);
        }

        private void ProcessSFrame(byte[] frame)
        {
            if (frame.Length < 6) return;
            int remoteSeq = (frame[4] >> 1) | ((frame[5] & 0x7F) << 7);
            lock (_seqLock) { _ackSeq = remoteSeq; }
        }

        private void ProcessUFrame(byte[] frame)
        {
            if (frame.Length < 6) return;
            byte control = frame[2];
            switch (control)
            {
                case U_TESTFR_ACT:
                    SendUFrame(U_TESTFR_CON);
                    Log.Debug("回复 TESTFR 确认");
                    break;
                case U_TESTFR_CON:
                    Log.Debug("收到 TESTFR 确认");
                    break;
                case U_STARTDT_CON:
                    _started = true;
                    break;
                case U_STOPDT_CON:
                    _started = false;
                    break;
            }
        }

        // ── ASDU Dispatch ─────────────────────────

        private void DispatchAsdu(Iec104Asdu asdu)
        {
            // Complete pending request for activation confirmation
            if (asdu.Cause == CauseOfTransmission.ActivationCon ||
                asdu.Cause == CauseOfTransmission.DeactivationCon)
            {
                if (_pendingRequests.TryRemove((int)asdu.TypeId, out var tcs))
                    tcs.TrySetResult(asdu);

                if (asdu.TypeId == TypeId.C_IC_NA_1)
                    Log.Info($"总召唤 {(asdu.IsNegative ? "否定" : "肯定")} 确认");
                else if (asdu.TypeId == TypeId.C_CS_NA_1)
                    Log.Info($"时钟同步 {(asdu.IsNegative ? "否定" : "肯定")} 确认");
                else if (asdu.TypeId == TypeId.C_CI_NA_1)
                    Log.Info($"计数器读取 {(asdu.IsNegative ? "否定" : "肯定")} 确认");
                else if (asdu.TypeId == TypeId.C_TS_TA_1)
                    Log.Info($"测试命令 {(asdu.IsNegative ? "否定" : "肯定")} 确认");
                return;
            }

            if (asdu.Cause == CauseOfTransmission.ActivationTerm)
            {
                if (_pendingRequests.TryRemove((int)asdu.TypeId, out var tcs))
                    tcs.TrySetResult(asdu);
                Log.Info("总召唤结束");
                return;
            }

            // Handle response to C_RD_NA_1
            if (asdu.Cause == CauseOfTransmission.Request)
            {
                if (_pendingRequests.TryRemove(C_RD_NA_1_KEY, out var readTcs))
                    readTcs.TrySetResult(asdu);
            }

            // Handle Spontaneous responses to clock sync, counter read, test command
            if (asdu.Cause == CauseOfTransmission.Spontaneous)
            {
                if (asdu.TypeId == TypeId.C_CS_NA_1 && _pendingRequests.TryRemove(C_CS_NA_1_KEY, out var csTcs))
                    csTcs.TrySetResult(asdu);
                else if (asdu.TypeId == TypeId.C_CI_NA_1 && _pendingRequests.TryRemove(C_CI_NA_1_KEY, out var ciTcs))
                    ciTcs.TrySetResult(asdu);
                else if (asdu.TypeId == TypeId.C_TS_TA_1 && _pendingRequests.TryRemove(C_TS_TA_1_KEY, out var tsTcs))
                    tsTcs.TrySetResult(asdu);
            }

            // Process monitoring data and update cache
            ProcessMonitoringData(asdu);
        }

        private const int C_RD_NA_1_KEY = -102;

        private void ProcessMonitoringData(Iec104Asdu asdu)
        {
            foreach (var obj in asdu.Objects)
            {
                Iec104DataPoint? point = null;

                switch (asdu.TypeId)
                {
                    case TypeId.M_SP_NA_1:
                        var sp = Iec104Asdu.DecodeSinglePoint(obj);
                        point = new Iec104DataPoint
                        {
                            Address = sp.Address,
                            Type = TypeId.M_SP_NA_1,
                            Value = sp.Value,
                            Quality = sp.Quality,
                            Timestamp = DateTime.Now,
                        };
                        break;

                    case TypeId.M_DP_NA_1:
                        var dp = Iec104Asdu.DecodeDoublePoint(obj);
                        point = new Iec104DataPoint
                        {
                            Address = dp.Address,
                            Type = TypeId.M_DP_NA_1,
                            Value = dp.IsOn,
                            Quality = dp.Quality,
                            Timestamp = DateTime.Now,
                        };
                        break;

                    case TypeId.M_ME_NA_1:
                        var mn = Iec104Asdu.DecodeMeasuredNormalized(obj);
                        point = new Iec104DataPoint
                        {
                            Address = mn.Address,
                            Type = TypeId.M_ME_NA_1,
                            Value = mn.Value,
                            Quality = mn.Quality,
                            Timestamp = DateTime.Now,
                        };
                        break;

                    case TypeId.M_ME_NC_1:
                        var mf = Iec104Asdu.DecodeMeasuredFloat(obj);
                        point = new Iec104DataPoint
                        {
                            Address = mf.Address,
                            Type = TypeId.M_ME_NC_1,
                            Value = mf.Value,
                            Quality = mf.Quality,
                            Timestamp = DateTime.Now,
                        };
                        break;
                }

                if (point != null)
                {
                    _dataCache[point.Address] = point;
                    OnDataReceived?.Invoke(this, point);
                }
            }
        }

        // ── Timeout Timers ────────────────────────

        private void ResetT3Timer()
        {
            _t3Timer?.Dispose();
            _t3Timer = new Timer(OnT3Timeout, null, T3, T3);
        }

        private void OnT3Timeout(object? state)
        {
            if (!_started) return;
            try
            {
                SendUFrame(U_TESTFR_ACT);
                Log.Debug("发送 TESTFR 测试帧");
            }
            catch (Exception ex)
            {
                Log.Error($"TESTFR 发送失败: {ex.Message}");
            }
        }

        // ── Public IEC 104 Operations ─────────────

        private const int C_CS_NA_1_KEY = -103;
        private const int C_CI_NA_1_KEY = -101;
        private const int C_TS_TA_1_KEY = -104;

        public OperateResult SendGeneralInterrogation()
        {
            return SendGeneralInterrogation(0);
        }

        public OperateResult SendGeneralInterrogation(int groupNumber)
        {
            if (!_started) return OperateResult.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[(int)TypeId.C_IC_NA_1] = tcs;

            try
            {
                var asdu = Iec104Asdu.BuildGeneralInterrogation(CommonAddress, (byte)groupNumber);
                SendIFrame(asdu);
                Log.Info($"发送总召唤命令 Group={groupNumber}");

                if (tcs.Task.Wait(T1))
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    return result.IsNegative
                        ? OperateResult.Failed("总召唤被否定")
                        : OperateResult.Success();
                }
                return OperateResult.Failed("总召唤超时");
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"总召唤失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove((int)TypeId.C_IC_NA_1, out _);
            }
        }

        public async Task<OperateResult> SendGeneralInterrogationAsync(CancellationToken ct = default)
        {
            return await SendGeneralInterrogationAsync(0, ct).ConfigureAwait(false);
        }

        public async Task<OperateResult> SendGeneralInterrogationAsync(int groupNumber, CancellationToken ct = default)
        {
            if (!_started) return OperateResult.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[(int)TypeId.C_IC_NA_1] = tcs;

            try
            {
                var asdu = Iec104Asdu.BuildGeneralInterrogation(CommonAddress, (byte)groupNumber);
                SendIFrame(asdu);
                Log.Info($"发送总召唤命令 Group={groupNumber}");

                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    if (await Task.WhenAny(tcs.Task, Task.Delay(T1, ct)) == tcs.Task)
                    {
                        var result = await tcs.Task.ConfigureAwait(false);
                        return result.IsNegative
                            ? OperateResult.Failed("总召唤被否定")
                            : OperateResult.Success();
                    }
                }
                return OperateResult.Failed("总召唤超时");
            }
            catch (OperationCanceledException) { return OperateResult.Failed("总召唤已取消"); }
            catch (Exception ex) { return OperateResult.Failed($"总召唤失败: {ex.Message}"); }
            finally { _pendingRequests.TryRemove((int)TypeId.C_IC_NA_1, out _); }
        }

        public OperateResult SendSingleCommand(int ioa, bool value)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return SendCommand(Iec104Asdu.BuildSingleCommand(CommonAddress, ioa, value));
        }

        public OperateResult SendDoubleCommand(int ioa, bool on)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return SendCommand(Iec104Asdu.BuildDoubleCommand(CommonAddress, ioa, on));
        }

        public OperateResult SendSetpointCommand(int ioa, float value)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return SendCommand(Iec104Asdu.BuildSetpointNormalized(CommonAddress, ioa, value));
        }

        public OperateResult<DateTime> SynchronizeClock()
        {
            if (!_started) return OperateResult<DateTime>.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[C_CS_NA_1_KEY] = tcs;

            try
            {
                DateTime now = DateTime.UtcNow;
                var asdu = Iec104Asdu.BuildClockSyncCommand(CommonAddress, now);
                SendIFrame(asdu);
                Log.Info("发送时钟同步命令");

                if (tcs.Task.Wait(T1))
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    if (result.IsNegative)
                        return OperateResult<DateTime>.Failed("时钟同步被否定");

                    if (result.Objects.Count > 0 && result.Objects[0].Data.Length >= 7)
                    {
                        DateTime syncedTime = Iec104Asdu.DecodeCP56Time2a(result.Objects[0].Data, 0);
                        return OperateResult<DateTime>.Success(syncedTime);
                    }
                    return OperateResult<DateTime>.Success(now);
                }
                return OperateResult<DateTime>.Failed("时钟同步超时");
            }
            catch (Exception ex)
            {
                return OperateResult<DateTime>.Failed($"时钟同步失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove(C_CS_NA_1_KEY, out _);
            }
        }

        public async Task<OperateResult<DateTime>> SynchronizeClockAsync(CancellationToken ct = default)
        {
            if (!_started) return OperateResult<DateTime>.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[C_CS_NA_1_KEY] = tcs;

            try
            {
                DateTime now = DateTime.UtcNow;
                var asdu = Iec104Asdu.BuildClockSyncCommand(CommonAddress, now);
                SendIFrame(asdu);
                Log.Info("发送时钟同步命令");

                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    if (await Task.WhenAny(tcs.Task, Task.Delay(T1, ct)) == tcs.Task)
                    {
                        var result = await tcs.Task.ConfigureAwait(false);
                        if (result.IsNegative)
                            return OperateResult<DateTime>.Failed("时钟同步被否定");

                        if (result.Objects.Count > 0 && result.Objects[0].Data.Length >= 7)
                        {
                            DateTime syncedTime = Iec104Asdu.DecodeCP56Time2a(result.Objects[0].Data, 0);
                            return OperateResult<DateTime>.Success(syncedTime);
                        }
                        return OperateResult<DateTime>.Success(now);
                    }
                }
                return OperateResult<DateTime>.Failed("时钟同步超时");
            }
            catch (OperationCanceledException) { return OperateResult<DateTime>.Failed("时钟同步已取消"); }
            catch (Exception ex) { return OperateResult<DateTime>.Failed($"时钟同步失败: {ex.Message}"); }
            finally { _pendingRequests.TryRemove(C_CS_NA_1_KEY, out _); }
        }

        public OperateResult ReadCounters()
        {
            if (!_started) return OperateResult.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[C_CI_NA_1_KEY] = tcs;

            try
            {
                var asdu = Iec104Asdu.BuildCounterReadCommand(CommonAddress);
                SendIFrame(asdu);
                Log.Info("发送读取计数器命令");

                if (tcs.Task.Wait(T1))
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    return result.IsNegative
                        ? OperateResult.Failed("读取计数器被否定")
                        : OperateResult.Success();
                }
                return OperateResult.Failed("读取计数器超时");
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"读取计数器失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove(C_CI_NA_1_KEY, out _);
            }
        }

        public OperateResult TestCommand()
        {
            if (!_started) return OperateResult.Failed("连接未激活，请先连接");

            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[C_TS_TA_1_KEY] = tcs;

            try
            {
                var asdu = Iec104Asdu.BuildTestCommand(CommonAddress, 0x1234, DateTime.UtcNow);
                SendIFrame(asdu);
                Log.Info("发送测试命令");

                if (tcs.Task.Wait(T1))
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    return result.IsNegative
                        ? OperateResult.Failed("测试命令被否定")
                        : OperateResult.Success();
                }
                return OperateResult.Failed("测试命令超时");
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"测试命令失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove(C_TS_TA_1_KEY, out _);
            }
        }

        private OperateResult SendCommand(Iec104Asdu asdu)
        {
            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[(int)asdu.TypeId] = tcs;

            try
            {
                SendIFrame(asdu);
                Log.Debug($"发送命令 TypeID={asdu.TypeId}");

                if (tcs.Task.Wait(T1))
                {
                    var result = tcs.Task.GetAwaiter().GetResult();
                    return result.IsNegative
                        ? OperateResult.Failed("命令被否定确认")
                        : OperateResult.Success();
                }
                return OperateResult.Failed("命令确认超时");
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"命令发送失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove((int)asdu.TypeId, out _);
            }
        }

        // ── Data Cache ────────────────────────────

        public Iec104DataPoint? GetCachedData(int ioa)
        {
            _dataCache.TryGetValue(ioa, out var point);
            return point;
        }

        public IDictionary<int, Iec104DataPoint> GetAllCachedData()
        {
            return new Dictionary<int, Iec104DataPoint>(_dataCache);
        }

        // ── Address Parsing ───────────────────────

        private static (PointType type, int ioa) ParseAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址不能为空");

            address = address.Trim();
            int sep = address.IndexOf(':');

            if (sep > 0)
            {
                string prefix = address.Substring(0, sep).ToUpperInvariant();
                int ioa = int.Parse(address.Substring(sep + 1));

                switch (prefix)
                {
                    case "SP": return (PointType.SinglePoint, ioa);
                    case "DP": return (PointType.DoublePoint, ioa);
                    case "MN": return (PointType.MeasuredNormalized, ioa);
                    case "MF": return (PointType.MeasuredFloat, ioa);
                    case "SC": return (PointType.SingleCommand, ioa);
                    case "DC": return (PointType.DoubleCommand, ioa);
                    case "SN": return (PointType.SetpointNormalized, ioa);
                    default: throw new ArgumentException($"未知地址前缀: {prefix}");
                }
            }

            return (PointType.MeasuredFloat, int.Parse(address));
        }

        // ── IReadWriteDevice ──────────────────────

        public override OperateResult<bool> ReadBool(string address)
        {
            var (type, ioa) = ParseAddress(address);

            if (_dataCache.TryGetValue(ioa, out var cached))
            {
                if (type == PointType.SinglePoint || type == PointType.DoublePoint)
                    return OperateResult<bool>.Success((bool)cached.Value);
            }

            var readResult = ReadFromServer(ioa);
            if (!readResult.IsSuccess)
                return OperateResult<bool>.Failed(readResult.Message, readResult.ErrorCode);

            if (_dataCache.TryGetValue(ioa, out cached))
                return OperateResult<bool>.Success((bool)cached.Value);

            return OperateResult<bool>.Failed($"读取单点失败: IOA={ioa}");
        }

        public override OperateResult<float> ReadFloat(string address)
        {
            var (type, ioa) = ParseAddress(address);

            if (_dataCache.TryGetValue(ioa, out var cached))
            {
                if (type == PointType.MeasuredFloat || type == PointType.MeasuredNormalized)
                    return OperateResult<float>.Success((float)cached.Value);
            }

            var readResult = ReadFromServer(ioa);
            if (!readResult.IsSuccess)
                return OperateResult<float>.Failed(readResult.Message, readResult.ErrorCode);

            if (_dataCache.TryGetValue(ioa, out cached))
                return OperateResult<float>.Success((float)cached.Value);

            return OperateResult<float>.Failed($"读取测量值失败: IOA={ioa}");
        }

        public override OperateResult<double> ReadDouble(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<double>.Success((double)r.Content)
                : OperateResult<double>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<short> ReadInt16(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<short>.Success((short)r.Content)
                : OperateResult<short>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ushort> ReadUInt16(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<ushort>.Success((ushort)r.Content)
                : OperateResult<ushort>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<int> ReadInt32(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<int>.Success((int)r.Content)
                : OperateResult<int>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<uint> ReadUInt32(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<uint>.Success((uint)r.Content)
                : OperateResult<uint>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<long> ReadInt64(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<long>.Success((long)r.Content)
                : OperateResult<long>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<ulong> ReadUInt64(string address)
        {
            var r = ReadFloat(address);
            return r.IsSuccess
                ? OperateResult<ulong>.Success((ulong)r.Content)
                : OperateResult<ulong>.Failed(r.Message, r.ErrorCode);
        }

        public override OperateResult<string> ReadString(string address, ushort length)
            => OperateResult<string>.Failed("IEC 104 不支持字符串读取");

        public override OperateResult<byte[]> ReadBytes(string address, ushort length)
            => OperateResult<byte[]>.Failed("IEC 104 不支持字节数组读取");

        // ── Write (Commands) ──────────────────────

        public override OperateResult Write(string address, bool value)
        {
            var (type, ioa) = ParseAddress(address);
            switch (type)
            {
                case PointType.SingleCommand:
                    return SendSingleCommand(ioa, value);
                case PointType.DoubleCommand:
                    return SendDoubleCommand(ioa, value);
                default:
                    return OperateResult.Failed($"地址类型不支持布尔写入: {type}");
            }
        }

        public override OperateResult Write(string address, short value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, ushort value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, int value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, uint value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, long value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, ulong value)
            => SendSetpointCommand(ParseAddress(address).ioa, value / 32767.0f);

        public override OperateResult Write(string address, float value)
        {
            var (type, ioa) = ParseAddress(address);
            if (type == PointType.SetpointNormalized)
                return SendSetpointCommand(ioa, value);
            return OperateResult.Failed($"地址类型不支持浮点写入: {type}");
        }

        public override OperateResult Write(string address, double value)
            => Write(address, (float)value);

        public override OperateResult Write(string address, string value)
            => OperateResult.Failed("IEC 104 不支持字符串写入");

        public override OperateResult Write(string address, byte[] data)
            => OperateResult.Failed("IEC 104 不支持字节数组写入");

        // ── Read from Server (C_RD_NA_1) ──────────

        private OperateResult ReadFromServer(int ioa)
        {
            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[C_RD_NA_1_KEY] = tcs;

            try
            {
                var asdu = Iec104Asdu.BuildReadCommand(CommonAddress, ioa);
                SendIFrame(asdu);

                if (tcs.Task.Wait(T1))
                    return OperateResult.Success();

                return OperateResult.Failed("读取超时");
            }
            catch (Exception ex)
            {
                return OperateResult.Failed($"读取失败: {ex.Message}");
            }
            finally
            {
                _pendingRequests.TryRemove(C_RD_NA_1_KEY, out _);
            }
        }

        // ── Async command wrappers ────────────────

        public async Task<OperateResult> SendSingleCommandAsync(int ioa, bool value, CancellationToken ct = default)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return await SendCommandAsync(Iec104Asdu.BuildSingleCommand(CommonAddress, ioa, value), ct)
                .ConfigureAwait(false);
        }

        public async Task<OperateResult> SendDoubleCommandAsync(int ioa, bool on, CancellationToken ct = default)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return await SendCommandAsync(Iec104Asdu.BuildDoubleCommand(CommonAddress, ioa, on), ct)
                .ConfigureAwait(false);
        }

        public async Task<OperateResult> SendSetpointCommandAsync(int ioa, float value, CancellationToken ct = default)
        {
            if (!_started) return OperateResult.Failed("连接未激活");
            return await SendCommandAsync(Iec104Asdu.BuildSetpointNormalized(CommonAddress, ioa, value), ct)
                .ConfigureAwait(false);
        }

        private async Task<OperateResult> SendCommandAsync(Iec104Asdu asdu, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<Iec104Asdu>();
            _pendingRequests[(int)asdu.TypeId] = tcs;

            try
            {
                SendIFrame(asdu);
                using (ct.Register(() => tcs.TrySetCanceled()))
                {
                    if (await Task.WhenAny(tcs.Task, Task.Delay(T1, ct)) == tcs.Task)
                    {
                        var result = await tcs.Task.ConfigureAwait(false);
                        return result.IsNegative
                            ? OperateResult.Failed("命令被否定确认")
                            : OperateResult.Success();
                    }
                }
                return OperateResult.Failed("命令确认超时");
            }
            catch (OperationCanceledException) { return OperateResult.Failed("命令已取消"); }
            catch (Exception ex) { return OperateResult.Failed($"命令发送失败: {ex.Message}"); }
            finally { _pendingRequests.TryRemove((int)asdu.TypeId, out _); }
        }

        // ═══════════════════════════════════════════
        //  IBatchReadWrite — 批量读写接口
        // ═══════════════════════════════════════════

        /// <summary>批量读取多个地址的值。</summary>
        public OperateResult<Dictionary<string, object?>> BatchRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, object?>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, object?>();
            foreach (var addr in addrList)
            {
                var r = ReadInt16(addr);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, object?>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, object?>>.Success(result);
        }

        /// <summary>批量读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, object?>>> BatchReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchRead(addresses));

        /// <summary>随机读取多个不连续地址（返回原始字节）。</summary>
        public OperateResult<Dictionary<string, byte[]>> RandomRead(IEnumerable<string> addresses)
        {
            var addrList = addresses.ToList();
            if (addrList.Count == 0)
                return OperateResult<Dictionary<string, byte[]>>.Failed("地址列表不能为空");
            var result = new Dictionary<string, byte[]>();
            foreach (var addr in addrList)
            {
                var r = ReadBytes(addr, 1);
                if (!r.IsSuccess)
                    return OperateResult<Dictionary<string, byte[]>>.Failed(r.Message, r.ErrorCode);
                result[addr] = r.Content;
            }
            return OperateResult<Dictionary<string, byte[]>>.Success(result);
        }

        /// <summary>随机读取（异步）。</summary>
        public Task<OperateResult<Dictionary<string, byte[]>>> RandomReadAsync(
            IEnumerable<string> addresses, CancellationToken cancellationToken = default)
            => Task.FromResult(RandomRead(addresses));

        /// <summary>批量写入多个地址的值。</summary>
        public OperateResult BatchWrite(IEnumerable<KeyValuePair<string, object>> items)
        {
            var itemList = items.ToList();
            if (itemList.Count == 0)
                return OperateResult.Failed("写入列表不能为空");
            foreach (var kv in itemList)
            {
                OperateResult r = kv.Value switch
                {
                    bool b => Write(kv.Key, b),
                    short s => Write(kv.Key, s),
                    ushort us => Write(kv.Key, us),
                    int i => Write(kv.Key, i),
                    uint ui => Write(kv.Key, ui),
                    float f => Write(kv.Key, f),
                    string s => Write(kv.Key, s),
                    byte[] b => Write(kv.Key, b),
                    _ => OperateResult.Failed($"不支持的类型: {kv.Value?.GetType().Name}")
                };
                if (!r.IsSuccess) return r;
            }
            return OperateResult.Success();
        }

        /// <summary>批量写入（异步）。</summary>
        public Task<OperateResult> BatchWriteAsync(
            IEnumerable<KeyValuePair<string, object>> items, CancellationToken cancellationToken = default)
            => Task.FromResult(BatchWrite(items));

        // ═══════════════════════════════════════════
        //  ISubscribeDevice — 数据订阅接口
        // ═══════════════════════════════════════════

        private readonly object _monitorLock = new object();
        private readonly Dictionary<string, MonitorEntry> _monitors = new Dictionary<string, MonitorEntry>();
        private bool _monitoring;
        private Timer? _monitorTimer;

        private class MonitorEntry
        {
            public string Address = "";
            public string DataType = "Int16";
            public int IntervalMs = 1000;
            public object? LastValue;
        }

        /// <summary>数据变化事件。</summary>
        public event EventHandler<DataChangeEventArgs>? OnDataChanged;

        /// <summary>订阅指定地址的数据变化。</summary>
        public void Subscribe(string address, int intervalMs = 1000, string dataType = "Int16")
        {
            lock (_monitorLock)
            {
                _monitors[address] = new MonitorEntry
                {
                    Address = address,
                    DataType = dataType,
                    IntervalMs = intervalMs,
                    LastValue = null
                };
            }
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe(string address)
        {
            lock (_monitorLock) { _monitors.Remove(address); }
        }

        /// <summary>启动所有订阅。</summary>
        public void StartSubscriptions(int globalIntervalMs = 500)
        {
            if (_monitoring) return;
            _monitoring = true;
            _monitorTimer = new Timer(PollMonitors, null, globalIntervalMs, globalIntervalMs);
        }

        /// <summary>停止所有订阅。</summary>
        public void StopSubscriptions()
        {
            _monitoring = false;
            _monitorTimer?.Dispose();
            _monitorTimer = null;
        }

        private void PollMonitors(object? state)
        {
            if (!_monitoring) return;
            try
            {
                List<MonitorEntry> entries;
                lock (_monitorLock) { entries = new List<MonitorEntry>(_monitors.Values); }

                foreach (var entry in entries)
                {
                    try
                    {
                        object? current = entry.DataType switch
                        {
                            "Int16" => ReadInt16(entry.Address).Content,
                            "UInt16" => ReadUInt16(entry.Address).Content,
                            "Int32" => ReadInt32(entry.Address).Content,
                            "Float" => ReadFloat(entry.Address).Content,
                            "Bool" => ReadBool(entry.Address).Content,
                            "String" => ReadString(entry.Address, 10).Content,
                            _ => null
                        };

                        if (current != null && !Equals(current, entry.LastValue))
                        {
                            if (entry.LastValue == null) { entry.LastValue = current; continue; }
                            var args = new DataChangeEventArgs
                            {
                                Address = entry.Address,
                                OldValue = entry.LastValue,
                                NewValue = current,
                                Timestamp = DateTime.Now,
                                Quality = "Good"
                            };
                            entry.LastValue = current;
                            OnDataChanged?.Invoke(this, args);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        /// <inheritdoc/>
        protected override byte[]? BuildHeartbeat()
        {
            try { return BuildUFrame(U_STARTDT_ACT); }
            catch { return null; }
        }
    }
}
