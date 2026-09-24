using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ReforgerTexturePacker
{
    // The texture set of one material inside one UDIM tile of a model - everything the main window's
    // slots hold, so switching sets just swaps what the slots show. Public fields only (saved by reflection).
    public class MaterialSet
    {
        public string Material = "";
        public int Tile = 1001;
        public int Tris;
        public string Base = "", Rough = "", Opacity = "", Normal = "", Metal = "", Ao = "";
        public string RoughCh = "R", MetalCh = "R", AoCh = "R", OpacityCh = "R";
        public bool RoughInvert, FlipGreen;
        public double RoughDef = 0.5, MetalDef = 0, AoDef = 1;
        public string OutDir = "", BaseName = "";
        public bool Skip;

        public string Key { get { return ModelPreview.KeyFor(Material, Tile); } }

        public int MapCount
        {
            get
            {
                int n = 0;
                foreach (string p in new string[] { Base, Rough, Opacity, Normal, Metal, Ao })
                    if (!string.IsNullOrEmpty(p)) n++;
                return n;
            }
        }

        public bool HasTextures { get { return Base.Length > 0 || Normal.Length > 0; } }

        public override string ToString()
        {
            string state = Skip ? "skipped" : (HasTextures ? MapCount + (MapCount == 1 ? " map" : " maps") : "no textures");
            return string.Format("{0}{1}   -   {2}", Material.Length > 0 ? Material : "(no material)", Tile != 1001 ? "  [tile " + Tile + "]" : "", state);
        }
    }

    public static class MaterialSets
    {
        // One set per (material, tile) that actually has faces, biggest material first.
        public static List<MaterialSet> FromMesh(ModelMesh m)
        {
            Dictionary<string, MaterialSet> d = new Dictionary<string, MaterialSet>();
            for (int t = 0; t < m.TriCount; t++)
            {
                string mat = m.TriMat[t] < m.Materials.Count ? m.Materials[m.TriMat[t]] : "";
                string key = ModelPreview.KeyFor(mat, m.TriTile[t]);
                MaterialSet s;
                if (!d.TryGetValue(key, out s))
                {
                    s = new MaterialSet();
                    s.Material = mat;
                    s.Tile = m.TriTile[t];
                    d[key] = s;
                }
                s.Tris++;
            }
            List<MaterialSet> list = new List<MaterialSet>(d.Values);
            Dictionary<string, int> matTris = new Dictionary<string, int>();
            foreach (MaterialSet s in list)
            {
                int n;
                matTris.TryGetValue(s.Material, out n);
                matTris[s.Material] = n + s.Tris;
            }
            list.Sort(delegate(MaterialSet a, MaterialSet b)
            {
                int c = matTris[b.Material].CompareTo(matTris[a.Material]);
                if (c == 0) c = string.Compare(a.Material, b.Material, StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : a.Tile.CompareTo(b.Tile);
            });
            return list;
        }

        private static string Norm(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // Fills every set that has no textures yet from `folder`: a texture set whose name contains the
        // material's name (and whose UDIM tile matches) goes to that material. Returns how many sets were filled.
        public static int AutoMatch(List<MaterialSet> sets, string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return 0;
            HashSet<string> exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".tif", ".tiff", ".tga", ".jpg", ".jpeg", ".bmp" };
            // one representative file per texture set in the folder (base name + tile)
            Dictionary<string, string> candidates = new Dictionary<string, string>();
            foreach (string f in Directory.GetFiles(folder))
            {
                if (!exts.Contains(Path.GetExtension(f))) continue;
                string tile;
                string name = TextureSetMatcher.SplitUdim(Path.GetFileNameWithoutExtension(f), out tile);
                string bl; MapType mt; string tok;
                if (!TextureSetMatcher.TryStripSuffix(name.ToLowerInvariant(), out bl, out mt, out tok)) continue;
                if (mt != MapType.BaseColor && mt != MapType.Normal) continue;
                string key = bl + "|" + (tile.Length > 0 ? tile : "1001");
                if (!candidates.ContainsKey(key) || mt == MapType.BaseColor)
                    candidates[key] = f;
            }
            int filled = 0;
            foreach (MaterialSet s in sets)
            {
                if (s.HasTextures || s.Material.Length == 0) continue;
                string mat = Norm(s.Material);
                string best = null;
                int bestLen = int.MaxValue;
                foreach (KeyValuePair<string, string> kv in candidates)
                {
                    int bar = kv.Key.LastIndexOf('|');
                    if (kv.Key.Substring(bar + 1) != s.Tile.ToString()) continue;
                    string nb = Norm(kv.Key.Substring(0, bar));
                    if (!nb.Contains(mat)) continue;
                    // prefer the tightest name: "body" should match "car_body", not "car_body_trim"
                    if (nb.Length < bestLen) { bestLen = nb.Length; best = kv.Value; }
                }
                // a material name that's a substring of another material's name must not steal its textures
                if (best != null)
                {
                    string bn = Norm(Path.GetFileNameWithoutExtension(best));
                    foreach (MaterialSet o in sets)
                        if (o != s && Norm(o.Material).Length > mat.Length && Norm(o.Material).Contains(mat) && bn.Contains(Norm(o.Material)))
                            best = null;
                }
                if (best == null) continue;
                Fill(s, TextureSetMatcher.Match(best));
                filled++;
            }
            return filled;
        }

        public static void Fill(MaterialSet s, TextureSetResult r)
        {
            s.Base = r.BaseColor ?? ""; s.Normal = r.Normal ?? ""; s.Opacity = r.Opacity ?? "";
            s.Rough = r.Roughness ?? ""; s.Metal = r.Metalness ?? ""; s.Ao = r.Ao ?? "";
            s.RoughCh = r.RoughnessChannel; s.MetalCh = r.MetalnessChannel; s.AoCh = r.AoChannel;
            s.RoughInvert = r.RoughnessIsGloss;
            s.FlipGreen = r.NormalIsOpenGL;
            s.OutDir = r.Folder ?? "";
            s.BaseName = (r.BaseName ?? "").TrimEnd('_', '-', ' ', '.');
        }

        // ---- persistence: per model, in %APPDATA%\ReforgerTexturePacker\sets\ ----------------

        private static string PathFor(string model)
        {
            string full = Path.GetFullPath(model).ToLowerInvariant();
            uint h = 2166136261;
            foreach (char c in full)
                h = unchecked((h ^ c) * 16777619);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker", "sets");
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(model) + "_" + h.ToString("x8") + ".txt");
        }

        // key=value text: header lines, then one [material|tile] section per set. Also the body of a .rtp project.
        public static string Serialize(List<MaterialSet> sets, MaterialSet current, string header)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(header)) sb.Append(header);
            if (current != null) sb.AppendLine("current=" + current.Key);
            for (int i = 0; i < sets.Count; i++)
            {
                sb.AppendLine("[" + sets[i].Key + "]");
                foreach (FieldInfo f in typeof(MaterialSet).GetFields())
                {
                    object v = f.GetValue(sets[i]);
                    sb.AppendLine(f.Name + "=" + (v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)));
                }
            }
            return sb.ToString();
        }

        public static void Save(string model, List<MaterialSet> sets, MaterialSet current)
        {
            try
            {
                string p = PathFor(model);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, Serialize(sets, current, "model=" + model + Environment.NewLine));
            }
            catch (Exception) { }
        }

        // Copies saved texture assignments onto the matching sets; returns the saved current key (or null).
        public static string Restore(string model, List<MaterialSet> sets)
        {
            string p = PathFor(model);
            if (!File.Exists(p))
                return null;
            try { return RestoreLines(File.ReadAllLines(p), sets); }
            catch (Exception) { return null; }
        }

        public static string RestoreLines(string[] lines, List<MaterialSet> sets)
        {
            string current = null;
            Dictionary<string, MaterialSet> byKey = new Dictionary<string, MaterialSet>();
            foreach (MaterialSet s in sets) byKey[s.Key] = s;
            MaterialSet cur = null;
            foreach (string line in lines)
            {
                if (line.StartsWith("current=")) { current = line.Substring(8); continue; }
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    byKey.TryGetValue(line.Substring(1, line.Length - 2), out cur);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (cur == null || eq <= 0) continue;
                string name = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (name == "Material" || name == "Tile" || name == "Tris") continue; // these come from the model
                FieldInfo f = typeof(MaterialSet).GetField(name);
                if (f == null) continue;
                try
                {
                    if (f.FieldType == typeof(string)) f.SetValue(cur, v);
                    else if (f.FieldType == typeof(bool)) f.SetValue(cur, bool.Parse(v));
                    else if (f.FieldType == typeof(double)) f.SetValue(cur, double.Parse(v, CultureInfo.InvariantCulture));
                    else if (f.FieldType == typeof(int)) f.SetValue(cur, int.Parse(v, CultureInfo.InvariantCulture));
                }
                catch (Exception) { }
            }
            // forget files that have since been moved or deleted
            foreach (MaterialSet s in sets)
                foreach (string fn in new string[] { "Base", "Rough", "Opacity", "Normal", "Metal", "Ao" })
                {
                    FieldInfo f = typeof(MaterialSet).GetField(fn);
                    string path = (string)f.GetValue(s);
                    if (path.Length > 0 && !File.Exists(path)) f.SetValue(s, "");
                }
            return current;
        }

        // The sets a saved file describes, without a model (used when a project's model can't be loaded).
        public static List<MaterialSet> FromLines(string[] lines)
        {
            List<MaterialSet> sets = new List<MaterialSet>();
            foreach (string line in lines)
            {
                if (!(line.StartsWith("[") && line.EndsWith("]"))) continue;
                string key = line.Substring(1, line.Length - 2);
                int bar = key.LastIndexOf('|');
                MaterialSet s = new MaterialSet();
                s.Material = bar >= 0 ? key.Substring(0, bar) : key;
                int tile;
                s.Tile = bar >= 0 && int.TryParse(key.Substring(bar + 1), out tile) ? tile : 1001;
                sets.Add(s);
            }
            RestoreLines(lines, sets);
            return sets;
        }
    }
}
