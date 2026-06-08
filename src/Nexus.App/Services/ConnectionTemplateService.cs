using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.App.Services
{
    /// <summary>
    /// 连接配置模板 — 保存/加载常用设备的连接参数。
    /// </summary>
    public sealed class ConnectionTemplate
    {
        public string Name { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public string IpAddress { get; set; } = "192.168.1.1";
        public int Port { get; set; }
        public byte Station { get; set; } = 1;
        public byte SlaveId { get; set; } = 1;
        public int Rack { get; set; }
        public int Slot { get; set; } = 1;
        public string ComPort { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public string TargetNetId { get; set; } = string.Empty;
        public int TargetPort { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime LastUsedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// 连接模板服务 — 管理保存/加载/删除连接配置。
    /// 配置保存在 %APPDATA%/Nexus/connection-templates.json
    /// </summary>
    public sealed class ConnectionTemplateService
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexus", "connection-templates.json");

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private List<ConnectionTemplate> _templates = new();

        public IReadOnlyList<ConnectionTemplate> Templates => _templates;

        /// <summary>加载已保存的模板。</summary>
        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                _templates = JsonSerializer.Deserialize<List<ConnectionTemplate>>(json, JsonOpts)
                             ?? new List<ConnectionTemplate>();
            }
            catch
            {
                _templates = new List<ConnectionTemplate>();
            }
        }

        /// <summary>保存当前模板到磁盘。</summary>
        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_templates, JsonOpts);
                File.WriteAllText(FilePath, json);
            }
            catch { /* 静默失败 — 不影响主流程 */ }
        }

        /// <summary>添加或更新模板（同名则覆盖）。</summary>
        public void AddOrUpdate(ConnectionTemplate template)
        {
            var idx = _templates.FindIndex(t => t.Name == template.Name);
            if (idx >= 0)
                _templates[idx] = template;
            else
                _templates.Add(template);
            Save();
        }

        /// <summary>按名称删除模板。</summary>
        public bool Remove(string name)
        {
            int removed = _templates.RemoveAll(t => t.Name == name);
            if (removed > 0) Save();
            return removed > 0;
        }

        /// <summary>按名称查找模板。</summary>
        public ConnectionTemplate? Find(string name)
            => _templates.Find(t => t.Name == name);
    }
}
