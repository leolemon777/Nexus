using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nexus.App.Services
{
    /// <summary>
    /// 配方管理服务 — 保存/加载/导入/导出参数组（配方）。
    /// <para>对标 HSL RecipeManager，用于批量读写 PLC 参数。</para>
    /// </summary>
    public sealed class RecipeService
    {
        private readonly string _recipeDir;

        public RecipeService()
        {
            _recipeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nexus", "Recipes");
            if (!Directory.Exists(_recipeDir)) Directory.CreateDirectory(_recipeDir);
        }

        /// <summary>获取所有配方名</summary>
        public List<string> ListRecipes()
        {
            return Directory.GetFiles(_recipeDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>加载配方</summary>
        public Recipe? LoadRecipe(string name)
        {
            var path = GetPath(name);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Recipe>(json);
        }

        /// <summary>保存配方</summary>
        public void SaveRecipe(Recipe recipe)
        {
            recipe.UpdatedAt = DateTime.Now;
            if (recipe.CreatedAt == default) recipe.CreatedAt = DateTime.Now;
            var json = JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetPath(recipe.Name), json);
        }

        /// <summary>删除配方</summary>
        public void DeleteRecipe(string name)
        {
            var path = GetPath(name);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>导出配方为 JSON 字符串</summary>
        public string ExportToJson(string name)
        {
            var recipe = LoadRecipe(name);
            return recipe != null ? JsonSerializer.Serialize(recipe, new JsonSerializerOptions { WriteIndented = true }) : "{}";
        }

        /// <summary>从 JSON 字符串导入配方</summary>
        public Recipe? ImportFromJson(string json, string? newName = null)
        {
            var recipe = JsonSerializer.Deserialize<Recipe>(json);
            if (recipe == null) return null;
            if (!string.IsNullOrEmpty(newName)) recipe.Name = newName;
            SaveRecipe(recipe);
            return recipe;
        }

        /// <summary>批量应用配方（写入所有参数到设备）</summary>
        public async Task<(int success, int failed)> ApplyRecipeAsync(IReadWriteDevice device, Recipe recipe)
        {
            int success = 0, failed = 0;
            foreach (var param in recipe.Parameters)
            {
                try
                {
                    OperateResult result;
                    switch (param.DataType)
                    {
                        case "Int16": result = await device.WriteAsync(param.Address, short.Parse(param.Value)); break;
                        case "UInt16": result = await device.WriteAsync(param.Address, ushort.Parse(param.Value)); break;
                        case "Int32": result = await device.WriteAsync(param.Address, int.Parse(param.Value)); break;
                        case "Float": result = await device.WriteAsync(param.Address, float.Parse(param.Value)); break;
                        case "String": result = await device.WriteAsync(param.Address, param.Value); break;
                        case "Bool": result = await device.WriteAsync(param.Address, param.Value is "1" or "true" or "True"); break;
                        default: result = OperateResult.Failed("Unsupported type: " + param.DataType); break;
                    }
                    if (result.IsSuccess) success++;
                    else failed++;
                }
                catch { failed++; }
            }
            return (success, failed);
        }

        /// <summary>从当前监控地址创建配方</summary>
        public Recipe CreateFromCurrentValues(string name, IEnumerable<MonitoredAddress> addresses)
        {
            var recipe = new Recipe
            {
                Name = name,
                Description = $"从当前监控值创建于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
            };
            foreach (var addr in addresses)
            {
                recipe.Parameters.Add(new RecipeParameter
                {
                    Name = addr.Alias,
                    Address = addr.Address,
                    Value = addr.CurrentValueText,
                    DataType = addr.DataType,
                    Description = addr.Alias
                });
            }
            return recipe;
        }

        /// <summary>从设备读取配方参数当前值</summary>
        public async Task<Recipe> ReadCurrentValuesAsync(IReadWriteDevice device, Recipe recipe)
        {
            foreach (var param in recipe.Parameters)
            {
                try
                {
                    string val = param.DataType switch
                    {
                        "Int16" => (await device.ReadInt16Async(param.Address)).Content.ToString(),
                        "UInt16" => (await device.ReadUInt16Async(param.Address)).Content.ToString(),
                        "Int32" => (await device.ReadInt32Async(param.Address)).Content.ToString(),
                        "Float" => (await device.ReadFloatAsync(param.Address)).Content.ToString(),
                        "String" => (await device.ReadStringAsync(param.Address, 20)).Content ?? "",
                        "Bool" => (await device.ReadBoolAsync(param.Address)).Content.ToString(),
                        _ => "N/A"
                    };
                    param.Value = val;
                }
                catch { param.Value = "ERR"; }
            }
            return recipe;
        }

        private string GetPath(string name) => Path.Combine(_recipeDir, $"{Sanitize(name)}.json");
        private static string Sanitize(string name) => string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// 配方模型 — 一组参数的集合
    /// </summary>
    public sealed class Recipe
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ProtocolName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<RecipeParameter> Parameters { get; set; } = new();
    }

    /// <summary>
    /// 配方参数
    /// </summary>
    public sealed class RecipeParameter
    {
        public string Name { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string DataType { get; set; } = "Int16";
        public string Value { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }
}
