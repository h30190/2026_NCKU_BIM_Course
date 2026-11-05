using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShaftChecker.Revit.Bridge
{
    /// <summary>
    /// 極簡 JSON 讀寫：不引入任何套件，避免與 Revit 內建的 Newtonsoft.Json 版本衝突。
    /// Parse 產生 Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null。
    /// </summary>
    public static class MiniJson
    {
        // ------------------------------------------------------------ 輸出

        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }

            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }

            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(Math.Round(d, 3).ToString("0.###", CultureInfo.InvariantCulture));
                return;
            }

            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }

            if (v is Enum) { WriteString(sb, v.ToString()); return; }

            var dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }

            var list = v as IEnumerable;
            if (list != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, v.ToString());
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------ 讀取

        public static object Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            int i = 0;
            object v = ReadValue(json, ref i);
            return v;
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            return Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object ReadValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON 不完整");
            char c = s[i];
            if (c == '{') return ReadObject(s, ref i);
            if (c == '[') return ReadArray(s, ref i);
            if (c == '"') return ReadString(s, ref i);
            if (c == 't' && Match(s, ref i, "true")) return true;
            if (c == 'f' && Match(s, ref i, "false")) return false;
            if (c == 'n' && Match(s, ref i, "null")) return null;
            return ReadNumber(s, ref i);
        }

        private static Dictionary<string, object> ReadObject(string s, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                string key = ReadString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("JSON 缺少冒號");
                i++;
                d[key] = ReadValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("JSON 物件格式錯誤");
            }
            throw new FormatException("JSON 物件未結束");
        }

        private static List<object> ReadArray(string s, ref int i)
        {
            var list = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                list.Add(ReadValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("JSON 陣列格式錯誤");
            }
            throw new FormatException("JSON 陣列未結束");
        }

        private static string ReadString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("JSON 字串應以雙引號開始");
            i++;
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber));
                        i += 4;
                        break;
                }
            }
            throw new FormatException("JSON 字串未結束");
        }

        private static object ReadNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw new FormatException("JSON 無法辨識的字元：" + s[i]);
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        // ------------------------------------------------------------ 讀取輔助

        public static List<string> GetStringList(Dictionary<string, object> d, string key)
        {
            var result = new List<string>();
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return result;
            var list = v as List<object>;
            if (list != null)
            {
                foreach (var item in list)
                    if (item != null) result.Add(Convert.ToString(item, CultureInfo.InvariantCulture));
            }
            else
            {
                foreach (var part in Convert.ToString(v, CultureInfo.InvariantCulture).Split(','))
                    if (part.Trim().Length > 0) result.Add(part.Trim());
            }
            return result;
        }

        /// <summary>讀 JSON 陣列（元素可以是物件、字串、數字）；沒有或不是陣列時回傳空清單。</summary>
        public static List<object> GetList(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return new List<object>();
            return v as List<object> ?? new List<object>();
        }

        public static bool? GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes";
        }

        public static string GetString(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
