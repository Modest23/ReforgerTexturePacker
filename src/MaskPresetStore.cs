using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace ReforgerTexturePacker
{
    // Remembers the mask generator state per texture set (keyed by the normal map's path) in
    // %APPDATA%\ReforgerTexturePacker\masks\ - nothing is written next to the textures.
    public static class MaskPresetStore
    {
        public class State
        {
            public RegionSettings Regions = new RegionSettings();
            public MatChannelSettings[] Channels = { new MatChannelSettings(), new MatChannelSettings(), new MatChannelSettings() };
            public LayerSettings Dirt = LayerSettings.Preset(0);
            public LayerSettings Mud = LayerSettings.Preset(1);
            public int[] Assign;
            public int AssignCount;
            public sbyte[] Paint;
            public int PaintW, PaintH;
            public int WorkRes = 1024;
            public int ExportSize = 1024;
            public string BakeModel = "", BakeMaterials = "";
        }

        private static string PathFor(string normalPath)
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker", "masks");
            string full = System.IO.Path.GetFullPath(normalPath).ToLowerInvariant();
            uint hsh = 2166136261;
            foreach (char c in full)
                hsh = unchecked((hsh ^ c) * 16777619);
            string name = System.IO.Path.GetFileNameWithoutExtension(normalPath);
            foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return System.IO.Path.Combine(dir, name + "_" + hsh.ToString("x8") + ".txt");
        }

        // Folder for this texture set's model bake (ao/normal/height.png), next to the saved state.
        public static string BakeDirFor(string normalPath)
        {
            string p = PathFor(normalPath);
            string masks = System.IO.Path.GetDirectoryName(p);
            return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(masks), "bakes", System.IO.Path.GetFileNameWithoutExtension(p));
        }

        public static void Save(string normalPath, State st)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Reforger Texture Packer - mask generator state");
                sb.AppendLine("source=" + normalPath);
                WriteFields(sb, "regions", st.Regions);
                for (int i = 0; i < 3; i++)
                    WriteFields(sb, "ch" + i, st.Channels[i]);
                WriteFields(sb, "dirt", st.Dirt);
                WriteFields(sb, "mud", st.Mud);
                sb.AppendLine("workres=" + st.WorkRes);
                sb.AppendLine("exportsize=" + st.ExportSize);
                sb.AppendLine("bakemodel=" + (st.BakeModel ?? ""));
                sb.AppendLine("bakemats=" + (st.BakeMaterials ?? ""));
                if (st.Assign != null)
                {
                    string[] a = new string[st.Assign.Length];
                    for (int i = 0; i < a.Length; i++)
                        a[i] = st.Assign[i].ToString();
                    sb.AppendLine("assign=" + string.Join(",", a));
                }
                if (st.Paint != null && HasAny(st.Paint))
                {
                    byte[] raw = new byte[st.Paint.Length];
                    Buffer.BlockCopy(st.Paint, 0, raw, 0, raw.Length);
                    sb.AppendLine("paint=" + st.PaintW + "x" + st.PaintH + ":" + Convert.ToBase64String(Deflate(raw)));
                }
                string p = PathFor(normalPath);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                File.WriteAllText(p, sb.ToString());
            }
            catch (Exception) { }
        }

        public static State Load(string normalPath)
        {
            string p = PathFor(normalPath);
            if (!File.Exists(p))
                return null;
            try
            {
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(p))
                {
                    if (line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0) kv[line.Substring(0, eq)] = line.Substring(eq + 1);
                }
                State st = new State();
                st.Dirt = new LayerSettings();
                st.Mud = new LayerSettings();
                ReadFields(kv, "regions", st.Regions);
                for (int i = 0; i < 3; i++)
                    ReadFields(kv, "ch" + i, st.Channels[i]);
                ReadFields(kv, "dirt", st.Dirt);
                ReadFields(kv, "mud", st.Mud);
                string v;
                if (kv.TryGetValue("workres", out v)) int.TryParse(v, out st.WorkRes);
                if (kv.TryGetValue("exportsize", out v)) int.TryParse(v, out st.ExportSize);
                if (kv.TryGetValue("bakemodel", out v)) st.BakeModel = v;
                if (kv.TryGetValue("bakemats", out v)) st.BakeMaterials = v;
                if (kv.TryGetValue("assign", out v) && v.Length > 0)
                {
                    string[] parts = v.Split(',');
                    st.Assign = new int[parts.Length];
                    for (int i = 0; i < parts.Length; i++)
                        int.TryParse(parts[i], out st.Assign[i]);
                    st.AssignCount = parts.Length;
                }
                if (kv.TryGetValue("paint", out v))
                {
                    int colon = v.IndexOf(':');
                    string[] dims = v.Substring(0, colon).Split('x');
                    st.PaintW = int.Parse(dims[0]);
                    st.PaintH = int.Parse(dims[1]);
                    byte[] raw = Inflate(Convert.FromBase64String(v.Substring(colon + 1)), st.PaintW * st.PaintH);
                    st.Paint = new sbyte[raw.Length];
                    Buffer.BlockCopy(raw, 0, st.Paint, 0, raw.Length);
                }
                return st;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool HasAny(sbyte[] p)
        {
            for (int i = 0; i < p.Length; i++)
                if (p[i] >= 0) return true;
            return false;
        }

        private static void WriteFields(StringBuilder sb, string prefix, object o)
        {
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object val = f.GetValue(o);
                string s = val is double ? ((double)val).ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(val, CultureInfo.InvariantCulture);
                sb.AppendLine(prefix + "." + f.Name + "=" + s);
            }
        }

        private static void ReadFields(Dictionary<string, string> kv, string prefix, object o)
        {
            foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string v;
                if (!kv.TryGetValue(prefix + "." + f.Name, out v)) continue;
                try
                {
                    if (f.FieldType == typeof(double)) f.SetValue(o, double.Parse(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(int)) f.SetValue(o, int.Parse(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(bool)) f.SetValue(o, bool.Parse(v));
                    else if (f.FieldType == typeof(string)) f.SetValue(o, v);
                }
                catch (Exception) { }
            }
        }

        private static byte[] Deflate(byte[] raw)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress))
                    ds.Write(raw, 0, raw.Length);
                return ms.ToArray();
            }
        }

        private static byte[] Inflate(byte[] comp, int len)
        {
            byte[] outv = new byte[len];
            using (DeflateStream ds = new DeflateStream(new MemoryStream(comp), CompressionMode.Decompress))
            {
                int off = 0;
                while (off < len)
                {
                    int r = ds.Read(outv, off, len - off);
                    if (r <= 0) break;
                    off += r;
                }
            }
            return outv;
        }
    }
}
