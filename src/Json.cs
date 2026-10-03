using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;

namespace Semaphore
{
    static class Json
    {
        public static object Parse(string text)
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s.DeserializeObject(text);
        }

        public static string Pretty(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, int level)
        {
            sb.Append(' ', level * 2);
        }

        static void Write(StringBuilder sb, object v, int level)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string) { Quote(sb, (string)v); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }

            var dict = v as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    Indent(sb, level + 1);
                    Quote(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(": ");
                    Write(sb, e.Value, level + 1);
                }
                sb.Append('\n');
                Indent(sb, level);
                sb.Append('}');
                return;
            }

            var list = v as IEnumerable;
            if (list != null)
            {
                var items = new List<object>();
                foreach (object o in list) items.Add(o);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    Indent(sb, level + 1);
                    Write(sb, items[i], level + 1);
                }
                sb.Append('\n');
                Indent(sb, level);
                sb.Append(']');
                return;
            }

            var conv = v as IConvertible;
            if (conv != null)
            {
                sb.Append(conv.ToString(CultureInfo.InvariantCulture));
                return;
            }
            Quote(sb, v.ToString());
        }

        static void Quote(StringBuilder sb, string s)
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
    }
}
