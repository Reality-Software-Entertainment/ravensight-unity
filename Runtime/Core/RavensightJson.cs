using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealitySE
{
    /// <summary>
    /// Tiny JSON reader and writer with no dependency on UnityEngine.
    ///
    /// Unity's JsonUtility cannot round trip dictionaries or heterogeneous
    /// values, which is exactly what an analytics payload is, so the SDK
    /// carries its own. Parsed objects come back as
    /// Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double,
    /// bool or null.
    /// </summary>
    public static class RavensightJson
    {
        // --- Writing ---------------------------------------------------------

        public static string Write(object value)
        {
            StringBuilder sb = new StringBuilder(256);
            WriteValue(sb, value, 0);
            return sb.ToString();
        }

        private const int MaxDepth = 24;

        private static void WriteValue(StringBuilder sb, object value, int depth)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (depth > MaxDepth)
            {
                // Defensive: a cyclic or absurdly nested payload becomes null
                // rather than blowing the stack inside a player build.
                sb.Append("null");
                return;
            }

            string asString = value as string;
            if (asString != null)
            {
                WriteString(sb, asString);
                return;
            }

            if (value is bool)
            {
                sb.Append(((bool)value) ? "true" : "false");
                return;
            }

            if (value is float || value is double || value is decimal)
            {
                double d = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                {
                    sb.Append("null");
                    return;
                }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is DateTime)
            {
                WriteString(sb, ((DateTime)value).ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                return;
            }

            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key == null) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value, depth + 1);
                }
                sb.Append('}');
                return;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item, depth + 1);
                }
                sb.Append(']');
                return;
            }

            if (value is Enum)
            {
                WriteString(sb, value.ToString());
                return;
            }

            WriteString(sb, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        // --- Reading ---------------------------------------------------------

        /// <summary>
        /// Parses JSON text. Returns null on malformed input rather than
        /// throwing, because every caller here is handling a network response
        /// that it must not crash on.
        /// </summary>
        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                int index = 0;
                object value = ParseValue(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length) return null;
                return value;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("json nested too deeply");
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of json");

            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth);
            if (c == '[') return ParseArray(s, ref i, depth);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't') { Expect(s, ref i, "true"); return true; }
            if (c == 'f') { Expect(s, ref i, "false"); return false; }
            if (c == 'n') { Expect(s, ref i, "null"); return null; }
            return ParseNumber(s, ref i);
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }

            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected object key");
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                result[key] = ParseValue(s, ref i, depth + 1);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            List<object> result = new List<object>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }

            while (true)
            {
                result.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // consume opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                if (i >= s.Length) throw new FormatException("unterminated escape");
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
                        if (i + 4 > s.Length) throw new FormatException("bad unicode escape");
                        int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("bad escape sequence");
                }
            }
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') i++;
                else break;
            }
            if (i == start) throw new FormatException("expected a number");

            double value;
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
            {
                throw new FormatException("bad number");
            }
            return value;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("expected literal " + literal);
            }
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        // --- Convenience accessors -------------------------------------------

        public static Dictionary<string, object> AsObject(object node)
        {
            return node as Dictionary<string, object>;
        }

        public static string GetString(object node, string key, string fallback)
        {
            Dictionary<string, object> map = node as Dictionary<string, object>;
            object value;
            if (map != null && map.TryGetValue(key, out value) && value != null)
            {
                string s = value as string;
                if (s != null) return s;
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }
            return fallback;
        }

        public static bool TryGetBool(object node, string key, out bool value)
        {
            value = false;
            Dictionary<string, object> map = node as Dictionary<string, object>;
            object raw;
            if (map == null || !map.TryGetValue(key, out raw)) return false;
            if (raw is bool)
            {
                value = (bool)raw;
                return true;
            }
            return false;
        }

        public static bool TryGetDouble(object node, string key, out double value)
        {
            value = 0d;
            Dictionary<string, object> map = node as Dictionary<string, object>;
            object raw;
            if (map == null || !map.TryGetValue(key, out raw) || raw == null) return false;
            if (raw is double)
            {
                value = (double)raw;
                return true;
            }
            string s = raw as string;
            if (s != null)
            {
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            }
            return false;
        }

        public static List<object> GetArray(object node, string key)
        {
            Dictionary<string, object> map = node as Dictionary<string, object>;
            object raw;
            if (map != null && map.TryGetValue(key, out raw))
            {
                return raw as List<object>;
            }
            return null;
        }
    }
}
