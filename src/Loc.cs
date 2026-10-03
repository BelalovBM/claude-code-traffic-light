using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace Semaphore
{
    static class Loc
    {
        public sealed class Lang
        {
            public string Code;
            public string Name;
            public Lang(string code, string name) { Code = code; Name = name; }
        }

        public static readonly Lang[] Languages =
        {
            new Lang("en", "English"),
            new Lang("ru", "Русский"),
            new Lang("es", "Español"),
            new Lang("de", "Deutsch"),
            new Lang("fr", "Français"),
            new Lang("pt", "Português"),
            new Lang("zh", "中文"),
        };

        static Dictionary<string, string> english = new Dictionary<string, string>();
        static Dictionary<string, string> current = new Dictionary<string, string>();

        public static string Code { get; private set; }

        public static string Resolve(string setting)
        {
            if (!string.IsNullOrEmpty(setting) && setting != "auto")
                foreach (var l in Languages)
                    if (l.Code == setting) return setting;

            string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            foreach (var l in Languages)
                if (l.Code == sys) return sys;
            return "en";
        }

        public static void Load(string setting)
        {
            english = Read("en");
            Code = Resolve(setting);
            current = Code == "en" ? english : Read(Code);
        }

        // A file lang\<code>.txt next to the exe overrides the embedded one,
        // so additional languages can be added without rebuilding.
        static Dictionary<string, string> Read(string code)
        {
            var map = new Dictionary<string, string>();
            try
            {
                string text = null;
                string external = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang", code + ".txt");
                if (File.Exists(external))
                {
                    text = File.ReadAllText(external, Encoding.UTF8);
                }
                else
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Lang." + code + ".txt"))
                    {
                        if (s != null)
                            using (var r = new StreamReader(s, Encoding.UTF8))
                                text = r.ReadToEnd();
                    }
                }
                if (text != null)
                {
                    foreach (string raw in text.Split('\n'))
                    {
                        string line = raw.Trim('\r', '﻿');
                        if (line.Length == 0 || line[0] == '#') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Replace("\\n", "\n");
                    }
                }
            }
            catch (Exception ex) { Log.Write("Language load failed (" + code + "): " + ex.Message); }
            return map;
        }

        public static string T(string key)
        {
            string v;
            if (current.TryGetValue(key, out v)) return v;
            if (english.TryGetValue(key, out v)) return v;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException) { return T(key); }
        }
    }
}
