#nullable disable warnings
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nexus.OpcUa;

/// <summary>
/// OPC UA Server — exposes data as OPC UA nodes for other clients to read/write.
/// Implements a subset of OPC UA sufficient for basic interoperability.
/// </summary>
public sealed class OpcUaServer : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly ConcurrentDictionary<string, OpcUaNode> _nodes = new();
    private readonly ConcurrentDictionary<string, List<OpcUaHistoryEntry>> _history = new();
    private readonly ConcurrentDictionary<int, OpcUaServerSession> _sessions = new();
    private int _nextSessionId = 1;
    private int _nextNodeId = 1000;
    private volatile bool _isRunning;

    public string EndpointUrl { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public bool IsRunning => _isRunning;
    public int SessionCount => _sessions.Count;
    public int NodeCount => _nodes.Count;
    public string ServerName { get; set; } = "Nexus OPC UA Server";

    public event EventHandler<string>? OnLog;
    public event EventHandler<OpcUaWriteRequest>? OnWrite;

    // ── Node management ──────────────────

    public string AddNode(string browseName, string displayName, object defaultValue, string dataType = "Int16", string parentNodeId = "ns=0;i=85")
    {
        string nodeId = $"ns=1;s={Interlocked.Increment(ref _nextNodeId)}";
        _nodes[nodeId] = new OpcUaNode
        {
            NodeId = nodeId,
            BrowseName = browseName,
            DisplayName = displayName,
            Value = defaultValue,
            DataType = dataType,
            ParentNodeId = parentNodeId,
            Timestamp = DateTime.UtcNow
        };
        return nodeId;
    }

    public bool UpdateNode(string nodeId, object value)
    {
        if (!_nodes.TryGetValue(nodeId, out var node)) return false;
        node.Value = value;
        node.Timestamp = DateTime.UtcNow;
        node.StatusCode = 0; // Good
        var list = _history.GetOrAdd(nodeId, _ => new List<OpcUaHistoryEntry>());
        lock (list) { list.Add(new OpcUaHistoryEntry { Value = value, Timestamp = node.Timestamp }); }
        return true;
    }

    public bool RemoveNode(string nodeId)
    {
        _history.TryRemove(nodeId, out _);
        return _nodes.TryRemove(nodeId, out _);
    }

    public OpcUaNode? GetNode(string nodeId) => _nodes.TryGetValue(nodeId, out var node) ? node : null;

    public IReadOnlyList<OpcUaNode> GetAllNodes() => _nodes.Values.ToList().AsReadOnly();

    public IReadOnlyList<OpcUaHistoryEntry> GetHistory(string nodeId, DateTime? startTime = null, DateTime? endTime = null)
    {
        if (!_history.TryGetValue(nodeId, out var list)) return new List<OpcUaHistoryEntry>().AsReadOnly();
        lock (list)
        {
            IEnumerable<OpcUaHistoryEntry> query = list;
            if (startTime.HasValue) query = query.Where(h => h.Timestamp >= startTime.Value);
            if (endTime.HasValue) query = query.Where(h => h.Timestamp < endTime.Value);
            return query.ToList().AsReadOnly();
        }
    }

    // ── Server lifecycle ──────────────────

    public void Start(int port = 4840)
    {
        if (IsRunning) return;
        Port = port;
        EndpointUrl = $"opc.tcp://localhost:{port}/Nexus";
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _isRunning = true;
        _cts = new CancellationTokenSource();
        _listenTask = AcceptClientsAsync(_cts.Token);
        AddDefaultNodes();
        OnLog?.Invoke(this, $"[OPC-UA] 服务器已启动: {EndpointUrl} (端口 {port})");
    }

    public void Stop()
    {
        _isRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        foreach (var session in _sessions.Values) session.Dispose();
        _sessions.Clear();
        _cts?.Dispose();
        _cts = null;
        _listenTask = null;
        OnLog?.Invoke(this, "[OPC-UA] 服务器已停止");
    }

    public void Dispose() => Stop();

    private void AddDefaultNodes()
    {
        _nodes["ns=0;i=85"] = new OpcUaNode { NodeId = "ns=0;i=85", BrowseName = "Objects", DisplayName = "Objects", Value = null, NodeClass = 1 };
        _nodes["ns=0;i=2253"] = new OpcUaNode { NodeId = "ns=0;i=2253", BrowseName = "Server", DisplayName = "Server", Value = null, NodeClass = 1 };
    }

    // ── Client handling ──────────────────

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                var tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                int sessionId = Interlocked.Increment(ref _nextSessionId);
                var session = new OpcUaServerSession(sessionId, tcp, this);
                _sessions[sessionId] = session;
                _ = Task.Run(() => session.HandleClientAsync(ct), ct);
                OnLog?.Invoke(this, $"[OPC-UA] 客户端连接: {tcp.Client.RemoteEndPoint} (会话 {sessionId})");
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
        }
    }

    internal void RemoveSession(int sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
        OnLog?.Invoke(this, $"[OPC-UA] 会话断开: {sessionId}");
    }

    internal void RaiseOnWrite(OpcUaWriteRequest request) => OnWrite?.Invoke(this, request);
}

public class OpcUaNode
{
    public string NodeId { get; set; } = string.Empty;
    public string BrowseName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public object? Value { get; set; }
    public string DataType { get; set; } = "Int16";
    public string ParentNodeId { get; set; } = "ns=0;i=85";
    public int NodeClass { get; set; } = 2; // Variable
    public uint StatusCode { get; set; } = 0;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class OpcUaHistoryEntry
{
    public object? Value { get; set; }
    public DateTime Timestamp { get; set; }
}

public class OpcUaWriteRequest
{
    public string NodeId { get; set; } = string.Empty;
    public object? Value { get; set; }
    public bool Handled { get; set; }
}

internal sealed class OpcUaServerSession : IDisposable
{
    private readonly int _sessionId;
    private readonly TcpClient _tcp;
    private readonly OpcUaServer _server;
    private NetworkStream? _stream;
    private bool _disposed;
    private uint _secureChannelId;
    private uint _tokenId = 1;

    public OpcUaServerSession(int sessionId, TcpClient tcp, OpcUaServer server)
    {
        _sessionId = sessionId;
        _tcp = tcp;
        _server = server;
        _stream = tcp.GetStream();
        _secureChannelId = (uint)sessionId;
    }

    public async Task HandleClientAsync(CancellationToken ct)
    {
        try
        {
            var buffer = new byte[8192];
            while (!ct.IsCancellationRequested && _tcp.Connected)
            {
                int bytesRead = await _stream!.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;

                var request = new byte[bytesRead];
                Buffer.BlockCopy(buffer, 0, request, 0, bytesRead);
                var response = ProcessRequest(request);
                if (response != null)
                    await _stream.WriteAsync(response, 0, response.Length, ct).ConfigureAwait(false);
            }
        }
        catch (Exception) { }
        finally
        {
            Dispose();
            _server.RemoveSession(_sessionId);
        }
    }

    private byte[]? ProcessRequest(byte[] request)
    {
        if (request.Length < 8) return null;

        string messageType = Encoding.ASCII.GetString(request, 0, 3);
        byte chunkType = request[3];

        return messageType switch
        {
            "HEL" => ProcessHello(request),
            "OPN" => ProcessOpenSecureChannel(request),
            "CLO" => ProcessCloseSecureChannel(request),
            "MSG" => ProcessMessage(request),
            _ => null
        };
    }

    private byte[] ProcessHello(byte[] request)
    {
        var ack = new byte[28];
        ack[0] = (byte)'A'; ack[1] = (byte)'C'; ack[2] = (byte)'K'; ack[3] = (byte)'F';
        ack[4] = 0; ack[5] = 0; ack[6] = 0; ack[7] = 28;
        ack[8] = 0; ack[9] = 0; ack[10] = 0; ack[11] = 0;
        ack[12] = 0; ack[13] = 0; ack[14] = 0xFF; ack[15] = 0xFF;
        ack[16] = 0; ack[17] = 0; ack[18] = 0xFF; ack[19] = 0xFF;
        ack[20] = 0x01; ack[21] = 0x00; ack[22] = 0x00; ack[23] = 0x00;
        ack[24] = 0; ack[25] = 0; ack[26] = 0; ack[27] = 1;
        return ack;
    }

    private byte[] ProcessOpenSecureChannel(byte[] request)
    {
        var resp = new byte[128];
        resp[0] = (byte)'O'; resp[1] = (byte)'P'; resp[2] = (byte)'N'; resp[3] = (byte)'F';
        resp[4] = 0; resp[5] = 0; resp[6] = 0; resp[7] = 128;
        resp[8] = 0; resp[9] = 0; resp[10] = 0; resp[11] = 0;
        resp[12] = 0; resp[13] = 0; resp[14] = 0; resp[15] = 0;
        resp[16] = 0x01; resp[17] = 0x00; resp[18] = 0x00; resp[19] = 0x00;
        resp[20] = (byte)(_tokenId); resp[21] = 0; resp[22] = 0; resp[23] = 0;
        resp[24] = 0; resp[25] = 0; resp[26] = 0; resp[27] = 0;
        return resp;
    }

    private byte[]? ProcessCloseSecureChannel(byte[] request)
    {
        return null;
    }

    private byte[]? ProcessMessage(byte[] request)
    {
        if (request.Length < 24) return null;

        try
        {
            using var ms = new MemoryStream(request);
            using var r = new BinaryReader(ms);
            r.ReadBytes(4);
            r.ReadInt32();
            r.ReadInt32();
            r.ReadInt32();
            r.ReadUInt32();
            r.ReadInt32();
            r.ReadByte();
            int idLength = r.ReadByte();
            byte[] idBytes = r.ReadBytes(idLength);
            string serviceId = Encoding.UTF8.GetString(idBytes);

            return serviceId switch
            {
                "2291" => HandleCreateSession(request),
                "467" => HandleRead(request),
                "669" => HandleWrite(request),
                _ => BuildServiceFault(0x80010000)
            };
        }
        catch
        {
            return BuildServiceFault(0x80010000);
        }
    }

    private byte[] HandleCreateSession(byte[] request)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        byte[] sessionId = Guid.NewGuid().ToByteArray();
        byte[] authToken = Guid.NewGuid().ToByteArray();

        w.Write((uint)0x00000000);
        w.Write((byte)0x01);
        w.Write(sessionId, 0, 16);
        w.Write(authToken, 0, 16);

        Encoding.UTF8.GetBytes("Nexus OPC UA Session").ToList().ForEach(b => w.Write(b));
        w.Write((byte)0);

        w.Write((uint)60000);
        w.Write((uint)0x00000000);

        return BuildMessageResponse(request, ms.ToArray());
    }

    private byte[] HandleRead(byte[] request)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((uint)0x00000000);
        w.Write((int)1);

        foreach (var node in _server.GetAllNodes())
        {
            WriteNodeId(w, node.NodeId);

            object? val = node.Value;
            if (val is short i16) { w.Write((byte)0x04); w.Write(i16); }
            else if (val is ushort u16) { w.Write((byte)0x05); w.Write(u16); }
            else if (val is int i32) { w.Write((byte)0x06); w.Write(i32); }
            else if (val is uint u32) { w.Write((byte)0x07); w.Write(u32); }
            else if (val is float f) { w.Write((byte)0x0A); w.Write(f); }
            else if (val is double d) { w.Write((byte)0x0B); w.Write(d); }
            else if (val is bool bo) { w.Write((byte)0x01); w.Write(bo); }
            else if (val is string s) { w.Write((byte)0x0C); WriteOpcString(w, s); }
            else { w.Write((byte)0x00); }

            w.Write(node.StatusCode);
            WriteTimestamp(w, node.Timestamp);
        }

        return BuildMessageResponse(request, ms.ToArray());
    }

    private byte[] HandleWrite(byte[] request)
    {
        try
        {
            using var reqMs = new MemoryStream(request);
            using var r = new BinaryReader(reqMs);
            r.ReadBytes(4);
            r.ReadInt32();
            r.ReadInt32();
            r.ReadInt32();
            r.ReadUInt32();
            r.ReadInt32();
            r.ReadByte();
            int idLen = r.ReadByte();
            r.ReadBytes(idLen);

            string nodeId = ReadNodeId(r);
            object? newValue = ReadVariant(r);

            var writeReq = new OpcUaWriteRequest
            {
                NodeId = nodeId,
                Value = newValue,
                Handled = false
            };
            _server.RaiseOnWrite(writeReq);

            if (!writeReq.Handled)
            {
                _server.UpdateNode(nodeId, newValue);
            }
        }
        catch { }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((uint)0x00000000);
        return BuildMessageResponse(request, ms.ToArray());
    }

    private void WriteNodeId(BinaryWriter w, string nodeIdString)
    {
        var nodeId = OpcUaNodeId.Parse(nodeIdString);
        nodeId.EncodeTo(w);
    }

    private string ReadNodeId(BinaryReader r)
    {
        byte encoding = r.ReadByte();
        ushort ns;
        switch (encoding & 0x0F)
        {
            case 0: ns = 0; break;
            case 1: ns = r.ReadByte(); break;
            case 2: ns = r.ReadUInt16(); break;
            default: ns = r.ReadUInt16(); break;
        }

        switch (encoding & 0x0F)
        {
            case 0: return $"ns={ns};i={r.ReadByte()}";
            case 1: return $"ns={ns};i={r.ReadUInt16()}";
            case 2: return $"ns={ns};i={r.ReadUInt32()}";
            case 3:
                int len = r.ReadInt32();
                if (len < 0) return $"ns={ns};s=";
                return $"ns={ns};s={Encoding.UTF8.GetString(r.ReadBytes(len))}";
            case 4:
                var guidBytes = r.ReadBytes(16);
                return $"ns={ns};g={new Guid(guidBytes)}";
            default:
                return $"ns={ns};i=0";
        }
    }

    private object? ReadVariant(BinaryReader r)
    {
        byte typeId = r.ReadByte();
        if ((typeId & 0x80) != 0) return null;

        switch (typeId)
        {
            case 0x00: return null;
            case 0x01: return r.ReadBoolean();
            case 0x04: return r.ReadInt16();
            case 0x05: return r.ReadUInt16();
            case 0x06: return r.ReadInt32();
            case 0x07: return r.ReadUInt32();
            case 0x08: return r.ReadInt64();
            case 0x09: return r.ReadUInt64();
            case 0x0A: return r.ReadSingle();
            case 0x0B: return r.ReadDouble();
            case 0x0C:
                int len = r.ReadInt32();
                if (len < 0) return string.Empty;
                return Encoding.UTF8.GetString(r.ReadBytes(len));
            default: return null;
        }
    }

    private void WriteOpcString(BinaryWriter w, string value)
    {
        if (value == null) { w.Write(-1); return; }
        var bytes = Encoding.UTF8.GetBytes(value);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private void WriteTimestamp(BinaryWriter w, DateTime timestamp)
    {
        DateTime utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        long opcTicks = utc.Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        w.Write(opcTicks);
    }

    private byte[] BuildMessageResponse(byte[] request, byte[] payload)
    {
        int totalSize = 24 + payload.Length;
        var resp = new byte[totalSize];
        resp[0] = (byte)'M'; resp[1] = (byte)'S'; resp[2] = (byte)'G'; resp[3] = (byte)'F';
        resp[4] = (byte)(totalSize >> 24); resp[5] = (byte)(totalSize >> 16);
        resp[6] = (byte)(totalSize >> 8); resp[7] = (byte)totalSize;
        resp[8] = 0; resp[9] = 0; resp[10] = 0; resp[11] = 0;
        resp[12] = 0; resp[13] = 0; resp[14] = 0; resp[15] = 0;
        resp[16] = (byte)(_secureChannelId); resp[17] = 0; resp[18] = 0; resp[19] = 0;
        resp[20] = (byte)(_tokenId); resp[21] = 0; resp[22] = 0; resp[23] = 0;
        Buffer.BlockCopy(payload, 0, resp, 24, payload.Length);
        return resp;
    }

    private byte[] BuildServiceFault(uint statusCode)
    {
        var resp = new byte[32];
        resp[0] = (byte)'E'; resp[1] = (byte)'R'; resp[2] = (byte)'R'; resp[3] = (byte)'F';
        resp[4] = 0; resp[5] = 0; resp[6] = 0; resp[7] = 32;
        resp[8] = (byte)(statusCode >> 24); resp[9] = (byte)(statusCode >> 16);
        resp[10] = (byte)(statusCode >> 8); resp[11] = (byte)statusCode;
        return resp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream?.Dispose(); } catch { }
        try { _tcp.Close(); } catch { }
    }
}
