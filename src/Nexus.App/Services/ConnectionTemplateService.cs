using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// <para>单一事实来源：旧的 MainViewModel 独立 store（snake_case
    /// <c>connection_templates.json</c>）已并入本类，详见 <see cref="MigrateFromLegacyFiles"/>。</para>
    /// </summary>
    public sealed class ConnectionTemplateService
    {
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexus");

        private static readonly string FilePath = Path.Combine(DirectoryPath, "connection-templates.json");

        // 旧 MainViewModel 用的 snake_case 文件（schema 不一致：Dict<string, Dict<string,string>>）。
        private static readonly string LegacySnakeCasePath = Path.Combine(DirectoryPath, "connection_templates.json");

        // 本类早期版本曾用过的同名文件（已并入下方 FilePath 的 camelCase 列表格式）。
        // 该路径 == FilePath，无需单独处理；保留注释避免后人误加第二个 kebab 文件。

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly object _stateLock = new object();
        private List<ConnectionTemplate> _templates = new();
        private bool _loaded;

        public IReadOnlyList<ConnectionTemplate> Templates => _templates;

        /// <summary>
        /// 幂等地加载磁盘模板并执行一次性旧文件迁移。
        /// 首次调用：读 <c>connection-templates.json</c>；若存在旧 snake_case 文件，
        /// 则把其中的模板名导入当前 store（仅名字 + 时间戳，因旧 schema 不含连接字段），
        /// 然后把旧文件改名为 <c>connection_templates.json.bak</c>（永不删除用户数据）。
        /// </summary>
        public void EnsureLoaded()
        {
            lock (_stateLock)
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    if (File.Exists(FilePath))
                    {
                        var json = File.ReadAllText(FilePath);
                        _templates = JsonSerializer.Deserialize<List<ConnectionTemplate>>(json, JsonOpts)
                                     ?? new List<ConnectionTemplate>();
                    }
                }
                catch
                {
                    _templates = new List<ConnectionTemplate>();
                }

                MigrateFromLegacyFiles();
            }
        }

        /// <summary>
        /// 保留同名方法以兼容既有调用方（如 ModbusTcpViewModel.LoadTemplates）。
        /// 等价于 <see cref="EnsureLoaded"/> 的幂等首次加载。
        /// </summary>
        public void Load() => EnsureLoaded();

        /// <summary>
        /// 一次性迁移：旧 MainViewModel 把模板写进 <c>%APPDATA%/Nexus/connection_templates.json</c>
        /// （格式 <c>Dict&lt;string, Dict&lt;string,string&gt;&gt;</c>，仅 SavedAt 一字段）。
        /// 本方法把旧文件中的模板名作为占位模板导入当前 typed store，
        /// 随后把旧文件改名为 <c>.bak</c>（不删除用户数据；重复运行不会再触发）。
        /// 幂等：旧文件不存在 / 已是 .bak 则不做任何事。
        /// </summary>
        private void MigrateFromLegacyFiles()
        {
            try
            {
                if (!File.Exists(LegacySnakeCasePath)) return;

                var json = File.ReadAllText(LegacySnakeCasePath);
                var legacy = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(json)
                             ?? new Dictionary<string, Dictionary<string, string>>();

                foreach (var kv in legacy)
                {
                    string? trimmed = kv.Key?.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;
                    string name = trimmed;   // 此处起非空
                    if (_templates.Exists(t => t.Name == name)) continue;   // 已存在则跳过

                    // 旧 schema 没有协议/IP 等字段；只保留名字 + 时间戳作为占位。
                    string savedAt = kv.Value != null && kv.Value.TryGetValue("SavedAt", out var s) ? s : string.Empty;
                    DateTime parsed = DateTime.Now;
                    if (!string.IsNullOrEmpty(savedAt))
                        DateTime.TryParse(savedAt, out parsed);

                    _templates.Add(new ConnectionTemplate
                    {
                        Name = name,
                        Protocol = "(migrated)",
                        CreatedAt = parsed,
                        LastUsedAt = parsed
                    });
                }

                Save();

                // 改名而非删除——永不丢失用户数据；后续运行检测不到原文件即不再迁移。
                string bak = LegacySnakeCasePath + ".bak";
                try
                {
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(LegacySnakeCasePath, bak);
                }
                catch
                {
                    // .bak 改名失败不影响功能（已导入并 Save）；下次启动会再次跳过同名模板。
                }
            }
            catch
            {
                // 迁移失败不应阻塞启动——主流程仍可继续用已加载的 store。
            }
        }

        /// <summary>保存当前模板到磁盘。</summary>
        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
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

        /// <summary>
        /// 把模板的连接字段反射写回协议 ViewModel（按属性名匹配：IpAddress/Port/SlaveId/Station/Rack/Slot/ComPort/BaudRate）。
        /// 与 <see cref="ViewModels.ProtocolViewModelBase.BuildConnectionInfo"/> 采用同样的反射策略，避免与具体 VM 类型耦合。
        /// 缺失或只读属性静默跳过；任一字段写失败不影响其余字段。
        /// </summary>
        /// <param name="template">要应用的模板（不可为 null）。</param>
        /// <param name="target">协议页 ViewModel 实例（任意类型，按属性名匹配）。</param>
        /// <returns>是否至少写入了一个字段。</returns>
        public bool ApplyToProtocol(ConnectionTemplate template, object target)
        {
            if (template == null || target == null) return false;
            Type type = target.GetType();
            bool wroteAny = false;

            void Set(string propName, object value)
            {
                try
                {
                    var prop = type.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                    if (prop == null || !prop.CanWrite) return;
                    if (value == null) return;

                    Type pt = prop.PropertyType;
                    object? converted;
                    if (pt.IsAssignableFrom(value.GetType()))
                        converted = value;
                    else
                        converted = Convert.ChangeType(value, pt);
                    if (converted == null) return;
                    prop.SetValue(target, converted);
                    wroteAny = true;
                }
                catch
                {
                    // 单字段写失败不影响其它字段。
                }
            }

            // 站号兼容：Modbus 用 SlaveId，部分协议用 Station——两个名字都尝试。
            Set("IpAddress", template.IpAddress);
            if (template.Port > 0) Set("Port", template.Port);
            if (template.SlaveId > 0)
            {
                Set("SlaveId", template.SlaveId);
                Set("Station", template.SlaveId);
            }
            if (template.Station > 0)
            {
                Set("Station", template.Station);
                Set("SlaveId", template.Station);
            }
            if (template.Rack > 0) Set("Rack", template.Rack);
            if (template.Slot > 0) Set("Slot", template.Slot);
            if (!string.IsNullOrWhiteSpace(template.ComPort)) Set("ComPort", template.ComPort);
            if (template.BaudRate > 0) Set("BaudRate", template.BaudRate);

            return wroteAny;
        }

        /// <summary>
        /// 查找并把模板字段应用到协议 ViewModel；返回是否找到且至少写了一个字段。
        /// 便捷封装：等价于 <c>Find(name)</c> + <c>ApplyToProtocol(tpl, vm)</c>，并更新 LastUsedAt。
        /// </summary>
        public bool ApplyToProtocol(string templateName, object target)
        {
            var tpl = Find(templateName);
            if (tpl == null) return false;
            bool ok = ApplyToProtocol(tpl, target);
            tpl.LastUsedAt = DateTime.Now;
            AddOrUpdate(tpl);
            return ok;
        }
    }
}
