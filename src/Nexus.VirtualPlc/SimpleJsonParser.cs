using System;
using System.Collections.Generic;
using System.Text;

namespace Nexus.VirtualPlc
{
    internal static class SimpleJsonParser
    {
        public static ScenarioDefinition ParseScenarioDefinition(string json)
        {
            var def = new ScenarioDefinition();
            int i = 0;
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return def;
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                var key = ReadString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWhitespace(json, ref i);

                if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                {
                    def.Name = ReadStringValue(json, ref i);
                }
                else if (string.Equals(key, "description", StringComparison.OrdinalIgnoreCase))
                {
                    def.Description = ReadStringValue(json, ref i);
                }
                else if (string.Equals(key, "registers", StringComparison.OrdinalIgnoreCase))
                {
                    def.Registers = ParseRegisterArray(json, ref i);
                }
                else if (string.Equals(key, "coils", StringComparison.OrdinalIgnoreCase))
                {
                    def.Coils = ParseCoilArray(json, ref i);
                }
                else if (string.Equals(key, "rules", StringComparison.OrdinalIgnoreCase))
                {
                    def.Rules = ParseRuleArray(json, ref i);
                }
                else
                {
                    SkipValue(json, ref i);
                }
            }

            return def;
        }

        private static string ReadStringValue(string json, ref int i)
        {
            if (i < json.Length && json[i] == '"')
                return ReadString(json, ref i);
            return ReadRawToken(json, ref i);
        }

        private static List<RegisterPreset> ParseRegisterArray(string json, ref int i)
        {
            var list = new List<RegisterPreset>();
            if (i >= json.Length || json[i] != '[') return list;
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == ']') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                var preset = new RegisterPreset();
                ParseObjectFields(json, ref i, (field, val) =>
                {
                    if (string.Equals(field, "address", StringComparison.OrdinalIgnoreCase))
                        preset.Address = int.Parse(val);
                    else if (string.Equals(field, "value", StringComparison.OrdinalIgnoreCase))
                        preset.Value = short.Parse(val);
                    else if (string.Equals(field, "comment", StringComparison.OrdinalIgnoreCase))
                        preset.Comment = val;
                });
                list.Add(preset);
            }
            return list;
        }

        private static List<CoilPreset> ParseCoilArray(string json, ref int i)
        {
            var list = new List<CoilPreset>();
            if (i >= json.Length || json[i] != '[') return list;
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == ']') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                var preset = new CoilPreset();
                ParseObjectFields(json, ref i, (field, val) =>
                {
                    if (string.Equals(field, "address", StringComparison.OrdinalIgnoreCase))
                        preset.Address = int.Parse(val);
                    else if (string.Equals(field, "value", StringComparison.OrdinalIgnoreCase))
                        preset.Value = val == "true";
                    else if (string.Equals(field, "comment", StringComparison.OrdinalIgnoreCase))
                        preset.Comment = val;
                });
                list.Add(preset);
            }
            return list;
        }

        private static List<ScenarioRule> ParseRuleArray(string json, ref int i)
        {
            var list = new List<ScenarioRule>();
            if (i >= json.Length || json[i] != '[') return list;
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == ']') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                var rule = new ScenarioRule();
                ParseObjectFields(json, ref i, (field, val) =>
                {
                    if (string.Equals(field, "name", StringComparison.OrdinalIgnoreCase))
                        rule.Name = val;
                    else if (string.Equals(field, "watchAddress", StringComparison.OrdinalIgnoreCase))
                        rule.WatchAddress = int.Parse(val);
                    else if (string.Equals(field, "triggerValue", StringComparison.OrdinalIgnoreCase))
                        rule.TriggerValue = int.Parse(val);
                    else if (string.Equals(field, "targetAddress", StringComparison.OrdinalIgnoreCase))
                        rule.TargetAddress = int.Parse(val);
                    else if (string.Equals(field, "targetValue", StringComparison.OrdinalIgnoreCase))
                        rule.TargetValue = short.Parse(val);
                    else if (string.Equals(field, "isBoolTarget", StringComparison.OrdinalIgnoreCase))
                        rule.IsBoolTarget = val == "true";
                    else if (string.Equals(field, "delayMs", StringComparison.OrdinalIgnoreCase))
                        rule.DelayMs = int.Parse(val);
                    else if (string.Equals(field, "action", StringComparison.OrdinalIgnoreCase))
                        rule.Action = val;
                });
                list.Add(rule);
            }
            return list;
        }

        private static void ParseObjectFields(string json, ref int i, Action<string, string> onField)
        {
            SkipWhitespace(json, ref i);
            if (i >= json.Length || json[i] != '{') return;
            i++;

            while (i < json.Length)
            {
                SkipWhitespace(json, ref i);
                if (i >= json.Length) break;
                if (json[i] == '}') { i++; break; }
                if (json[i] == ',') { i++; continue; }

                var key = ReadString(json, ref i);
                SkipWhitespace(json, ref i);
                if (i < json.Length && json[i] == ':') i++;
                SkipWhitespace(json, ref i);

                string value;
                if (i < json.Length && json[i] == '"')
                    value = ReadString(json, ref i);
                else
                    value = ReadRawToken(json, ref i);

                onField(key, value);
            }
        }

        private static void SkipValue(string json, ref int i)
        {
            if (i >= json.Length) return;
            if (json[i] == '"') { ReadString(json, ref i); return; }
            if (json[i] == '[')
            {
                i++;
                int depth = 1;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == '[') depth++;
                    else if (json[i] == ']') depth--;
                    else if (json[i] == '"') ReadString(json, ref i);
                    if (depth > 0) i++;
                }
                if (i < json.Length) i++;
                return;
            }
            if (json[i] == '{')
            {
                i++;
                int depth = 1;
                while (i < json.Length && depth > 0)
                {
                    if (json[i] == '{') depth++;
                    else if (json[i] == '}') depth--;
                    else if (json[i] == '"') ReadString(json, ref i);
                    if (depth > 0) i++;
                }
                if (i < json.Length) i++;
                return;
            }
            ReadRawToken(json, ref i);
        }

        private static string ReadString(string json, ref int i)
        {
            if (i >= json.Length || json[i] != '"') return "";
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                if (json[i] == '\\' && i + 1 < json.Length)
                {
                    i++;
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
                    break;
                }
                else
                {
                    sb.Append(json[i]);
                    i++;
                }
            }
            return sb.ToString();
        }

        private static string ReadRawToken(string json, ref int i)
        {
            int start = i;
            while (i < json.Length && json[i] != ',' && json[i] != '}' && json[i] != ']')
                i++;
            return json.Substring(start, i - start).Trim();
        }

        private static void SkipWhitespace(string json, ref int i)
        {
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n'))
                i++;
        }
    }
}
