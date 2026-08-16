using System;
using System.IO;

namespace ReforgerTexturePacker
{
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
