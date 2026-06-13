using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Nexus.Security;

public enum UserRole
{
    Viewer,
    Operator,
    Engineer,
    Admin
}

public class User
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Viewer;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastLogin { get; set; }
    public bool Enabled { get; set; } = true;
}

public class AuditEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
}

public sealed class UserService
{
    private readonly Dictionary<string, User> _users = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
    private readonly List<AuditEntry> _auditLog = new List<AuditEntry>();
    private readonly object _lock = new object();
    private User? _currentUser;

    private static readonly string UsersFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexus", "users.json");
    private static readonly string AuditFilePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Nexus", "audit.jsonl");

    public User? CurrentUser => _currentUser;
    public bool IsAuthenticated => _currentUser != null;
    public event EventHandler<AuditEntry>? OnAudit;

    public UserService()
    {
        LoadUsers();
        if (_users.Count == 0)
        {
            CreateUser("admin", "admin123", UserRole.Admin, "系统管理员");
        }
    }

    // ── Authentication ──────────────────

    public bool Login(string username, string password)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var user) || !user.Enabled)
            {
                LogAudit("", "LoginFailed", "用户名: " + username);
                return false;
            }

            if (!VerifyPassword(password, user.Salt, user.PasswordHash))
            {
                LogAudit(username, "LoginFailed", "密码错误");
                return false;
            }

            user.LastLogin = DateTime.Now;
            _currentUser = user;
            LogAudit(username, "Login", "登录成功");
            SaveUsers();
            return true;
        }
    }

    public void Logout()
    {
        if (_currentUser != null)
        {
            LogAudit(_currentUser.Username, "Logout", "登出");
            _currentUser = null;
        }
    }

    // ── User Management ──────────────────

    public bool CreateUser(string username, string password, UserRole role, string displayName = "")
    {
        lock (_lock)
        {
            if (_users.ContainsKey(username)) return false;
            var salt = GenerateSalt();
            var user = new User
            {
                Username = username,
                PasswordHash = HashPassword(password, salt),
                Salt = salt,
                Role = role,
                DisplayName = string.IsNullOrEmpty(displayName) ? username : displayName
            };
            _users[username] = user;
            SaveUsers();
            LogAudit(_currentUser?.Username ?? "System", "CreateUser",
                "创建用户: " + username + ", 角色: " + role);
            return true;
        }
    }

    public bool ChangePassword(string username, string oldPassword, string newPassword)
    {
        lock (_lock)
        {
            if (!_users.TryGetValue(username, out var user)) return false;
            if (!VerifyPassword(oldPassword, user.Salt, user.PasswordHash)) return false;
            var salt = GenerateSalt();
            user.PasswordHash = HashPassword(newPassword, salt);
            user.Salt = salt;
            SaveUsers();
            LogAudit(username, "ChangePassword", "修改密码");
            return true;
        }
    }

    public bool HasPermission(UserRole requiredRole)
    {
        return _currentUser != null && _currentUser.Role >= requiredRole;
    }

    public List<User> GetAllUsers()
    {
        lock (_lock) { return _users.Values.ToList(); }
    }

    // ── Audit ──────────────────

    public void LogAudit(string username, string action, string details)
    {
        var entry = new AuditEntry
        {
            Username = username,
            Action = action,
            Details = details
        };
        lock (_lock)
        {
            _auditLog.Add(entry);
            if (_auditLog.Count > 10000) _auditLog.RemoveAt(0);
        }
        OnAudit?.Invoke(this, entry);

        try
        {
            var dir = Path.GetDirectoryName(AuditFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = SerializeAuditEntry(entry);
            File.AppendAllText(AuditFilePath, json + Environment.NewLine);
        }
        catch { }
    }

    public List<AuditEntry> GetAuditLog(int count = 100)
    {
        lock (_lock)
        {
            int start = Math.Max(0, _auditLog.Count - count);
            return _auditLog.GetRange(start, _auditLog.Count - start);
        }
    }

    // ── Persistence (manual JSON, netstandard2.0 compatible) ──────────────────

    private void SaveUsers()
    {
        try
        {
            var dir = Path.GetDirectoryName(UsersFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = SerializeUserList(_users.Values.ToList());
            File.WriteAllText(UsersFilePath, json);
        }
        catch { }
    }

    private void LoadUsers()
    {
        try
        {
            if (!File.Exists(UsersFilePath)) return;
            var json = File.ReadAllText(UsersFilePath);
            var list = DeserializeUserList(json);
            foreach (var user in list) _users[user.Username] = user;
        }
        catch { }
    }

    // ── Password Hashing ──────────────────

    private static string GenerateSalt()
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static string HashPassword(string password, string salt)
    {
        using (var sha256 = SHA256.Create())
        {
            var combined = Encoding.UTF8.GetBytes(password + salt);
            var hash = sha256.ComputeHash(combined);
            return Convert.ToBase64String(hash);
        }
    }

    private static bool VerifyPassword(string password, string salt, string hash)
    {
        return HashPassword(password, salt) == hash;
    }

    // ── Manual JSON Serialization ──────────────────

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private static void WriteStr(StringBuilder sb, string key, string val)
    {
        sb.Append('"').Append(EscapeJson(key)).Append("\": \"").Append(EscapeJson(val)).Append('"');
    }

    private static void WriteBool(StringBuilder sb, string key, bool val)
    {
        sb.Append('"').Append(key).Append("\": ").Append(val ? "true" : "false");
    }

    private static void WriteEnum(StringBuilder sb, string key, UserRole val)
    {
        sb.Append('"').Append(key).Append("\": \"").Append(val.ToString()).Append('"');
    }

    private static void WriteDate(StringBuilder sb, string key, DateTime val)
    {
        sb.Append('"').Append(key).Append("\": \"").Append(val.ToString("o")).Append('"');
    }

    private static void WriteNullableDate(StringBuilder sb, string key, DateTime? val)
    {
        if (val.HasValue) WriteDate(sb, key, val.Value);
        else sb.Append('"').Append(key).Append("\": null");
    }

    private static string SerializeUserList(List<User> users)
    {
        var sb = new StringBuilder();
        sb.Append("[\n");
        for (int i = 0; i < users.Count; i++)
        {
            if (i > 0) sb.Append(",\n");
            sb.Append("  ");
            SerializeUser(sb, users[i]);
        }
        sb.Append("\n]");
        return sb.ToString();
    }

    private static void SerializeUser(StringBuilder sb, User u)
    {
        sb.Append('{');
        WriteStr(sb, "Username", u.Username); sb.Append(", ");
        WriteStr(sb, "PasswordHash", u.PasswordHash); sb.Append(", ");
        WriteStr(sb, "Salt", u.Salt); sb.Append(", ");
        WriteEnum(sb, "Role", u.Role); sb.Append(", ");
        WriteStr(sb, "DisplayName", u.DisplayName); sb.Append(", ");
        WriteDate(sb, "CreatedAt", u.CreatedAt); sb.Append(", ");
        WriteNullableDate(sb, "LastLogin", u.LastLogin); sb.Append(", ");
        WriteBool(sb, "Enabled", u.Enabled);
        sb.Append('}');
    }

    private static string SerializeAuditEntry(AuditEntry e)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        WriteDate(sb, "Timestamp", e.Timestamp); sb.Append(", ");
        WriteStr(sb, "Username", e.Username); sb.Append(", ");
        WriteStr(sb, "Action", e.Action); sb.Append(", ");
        WriteStr(sb, "Details", e.Details); sb.Append(", ");
        WriteStr(sb, "IpAddress", e.IpAddress);
        sb.Append('}');
        return sb.ToString();
    }

    private static List<User> DeserializeUserList(string json)
    {
        var result = new List<User>();
        json = json.Trim();
        if (!json.StartsWith("[")) return result;
        json = json.Substring(1).TrimStart();
        while (json.Length > 0)
        {
            json = json.TrimStart();
            if (json.StartsWith("]")) break;
            if (json.StartsWith(",")) { json = json.Substring(1).TrimStart(); }
            if (!json.StartsWith("{")) break;
            var (user, rest) = ParseUserObject(json);
            if (user != null) result.Add(user);
            json = rest;
        }
        return result;
    }

    private static (User?, string) ParseUserObject(string json)
    {
        if (!json.StartsWith("{")) return (null, json);
        json = json.Substring(1).TrimStart();
        var user = new User();
        while (json.Length > 0)
        {
            json = json.TrimStart();
            if (json.StartsWith("}"))
            {
                json = json.Substring(1);
                break;
            }
            if (json.StartsWith(",")) { json = json.Substring(1).TrimStart(); }
            var (key, rest1) = ParseJsonString(json);
            if (key == null) break;
            rest1 = rest1.TrimStart();
            if (!rest1.StartsWith(":")) break;
            rest1 = rest1.Substring(1).TrimStart();

            if (rest1.StartsWith("\""))
            {
                var (sv, r) = ParseJsonString(rest1);
                rest1 = r;
                switch (key)
                {
                    case "Username": user.Username = sv ?? ""; break;
                    case "PasswordHash": user.PasswordHash = sv ?? ""; break;
                    case "Salt": user.Salt = sv ?? ""; break;
                    case "DisplayName": user.DisplayName = sv ?? ""; break;
                    case "IpAddress": break;
                    case "Role":
                        if (sv != null && Enum.TryParse<UserRole>(sv, true, out var role))
                            user.Role = role;
                        break;
                    case "CreatedAt":
                        if (sv != null && DateTime.TryParse(sv, null, DateTimeStyles.RoundtripKind, out var ct))
                            user.CreatedAt = ct;
                        break;
                    case "LastLogin":
                        if (sv != null && DateTime.TryParse(sv, null, DateTimeStyles.RoundtripKind, out var ll))
                            user.LastLogin = ll;
                        break;
                }
            }
            else if (rest1.StartsWith("null"))
            {
                rest1 = rest1.Substring(4);
            }
            else if (rest1.StartsWith("true"))
            {
                if (key == "Enabled") user.Enabled = true;
                rest1 = rest1.Substring(4);
            }
            else if (rest1.StartsWith("false"))
            {
                if (key == "Enabled") user.Enabled = false;
                rest1 = rest1.Substring(5);
            }
            else
            {
                int end = 0;
                while (end < rest1.Length && rest1[end] != ',' && rest1[end] != '}' && rest1[end] != ' ')
                    end++;
                rest1 = rest1.Substring(end);
            }
            json = rest1;
        }
        return (user, json);
    }

    private static (string?, string) ParseJsonString(string json)
    {
        if (!json.StartsWith("\"")) return (null, json);
        int i = 1;
        var sb = new StringBuilder();
        while (i < json.Length)
        {
            if (json[i] == '\\')
            {
                i++;
                if (i >= json.Length) break;
                switch (json[i])
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    default: sb.Append(json[i]); break;
                }
                i++;
            }
            else if (json[i] == '"')
            {
                i++;
                return (sb.ToString(), json.Substring(i));
            }
            else
            {
                sb.Append(json[i]);
                i++;
            }
        }
        return (sb.ToString(), json.Substring(i));
    }
}
