using System;
using System.IO;

namespace ReforgerTexturePacker
{
    // Small key=value preferences (export size, pane positions) in %APPDATA%\ReforgerTexturePacker_prefs.txt.
    public static class Prefs
    {
        private static string PrefsPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker_prefs.txt"); }
        }

        private static System.Collections.Generic.Dictionary<string, string> _cache;

        private static System.Collections.Generic.Dictionary<string, string> All()
        {
            if (_cache != null) return _cache;
            _cache = new System.Collections.Generic.Dictionary<string, string>();
            try
            {
                if (File.Exists(PrefsPath))
                    foreach (string line in File.ReadAllLines(PrefsPath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) _cache[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (Exception) { }
            return _cache;
        }

        public static string Get(string key, string def)
        {
            string v;
            return All().TryGetValue(key, out v) ? v : def;
        }

        public static void Set(string key, string value)
        {
            All()[key] = value ?? "";
            try
            {
                System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
                foreach (System.Collections.Generic.KeyValuePair<string, string> kv in _cache)
                    lines.Add(kv.Key + "=" + kv.Value);
                File.WriteAllLines(PrefsPath, lines.ToArray());
            }
            catch (Exception) { }
        }
    }

    // Persists the theme choice in %APPDATA%\ReforgerTexturePacker.cfg.
    public static class Settings
    {
        private static string CfgPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker.cfg"); }
        }

        public static bool LoadDark()
        {
            try
            {
                if (File.Exists(CfgPath))
                    return File.ReadAllText(CfgPath).Trim().ToLowerInvariant() != "light";
            }
            catch (Exception) { }
            return true;
        }

        public static void SaveDark(bool dark)
        {
            try { File.WriteAllText(CfgPath, dark ? "dark" : "light"); }
            catch (Exception) { }
        }
    }
}
