using System;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexus.OpcUa;

namespace Nexus.App.ViewModels;

public partial class OpcUaServerViewModel : ObservableObject, IDisposable
{
    private OpcUaServer? _server;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;

    public ObservableCollection<OpcUaNode> Nodes { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _port = 4840;
    [ObservableProperty] private string _endpointUrl = "";
    [ObservableProperty] private int _sessionCount;
    [ObservableProperty] private int _nodeCount;
    [ObservableProperty] private string _newNodeName = "Temperature";
    [ObservableProperty] private string _newNodeValue = "25.0";
    [ObservableProperty] private string _newNodeDataType = "Float";

    public string[] DataTypes { get; } = { "Bool", "Int16", "UInt16", "Int32", "UInt32", "Float", "Double", "String" };

    public OpcUaServerViewModel()
    {
        _dispatcher = Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }

    [RelayCommand]
    private void ToggleServer()
    {
        if (IsRunning)
        {
            _server?.Stop();
            IsRunning = false;
            EndpointUrl = "";
            AppendLog("[OPC-UA] 服务器已停止");
        }
        else
        {
            _server = new OpcUaServer();
            _server.OnLog += (_, msg) => _dispatcher.BeginInvoke(() => AppendLog(msg));
            _server.OnWrite += (_, req) =>
            {
                _dispatcher.BeginInvoke(() => AppendLog($"[OPC-UA] 写入: {req.NodeId} = {req.Value}"));
            };
            _server.Start(Port);
            IsRunning = true;
            EndpointUrl = _server.EndpointUrl;
            RefreshNodes();
        }
    }

    [RelayCommand]
    private void AddNode()
    {
        if (_server == null) return;
        _server.AddNode(NewNodeName, NewNodeName, double.TryParse(NewNodeValue, out var v) ? (object)v : NewNodeValue, NewNodeDataType);
        RefreshNodes();
        AppendLog($"[OPC-UA] 添加节点: {NewNodeName}");
    }

    [RelayCommand]
    private void RemoveNode(OpcUaNode? node)
    {
        if (node == null || _server == null) return;
        _server.RemoveNode(node.NodeId);
        RefreshNodes();
    }

    [RelayCommand]
    private void RefreshNodes()
    {
        if (_server == null) return;
        Nodes.Clear();
        foreach (var node in _server.GetAllNodes()) Nodes.Add(node);
        NodeCount = Nodes.Count;
    }

    private void AppendLog(string message)
    {
        LogLines.Insert(0, $"{DateTime.Now:HH:mm:ss.fff} {message}");
        if (LogLines.Count > 200) LogLines.RemoveAt(LogLines.Count - 1);
    }

    public void Dispose()
    {
        _server?.Dispose();
    }
}
