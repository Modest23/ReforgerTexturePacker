using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // Runs Blender in the background (rz_bake.py, embedded in the exe) to bake model-space maps
    // onto the texture's UVs: ambient occlusion, world normal and height. Blender reads .fbx/.obj/.blend
    // itself and bakes with Cycles, so we get what Substance gets from the mesh.
    public static class BlenderBaker
    {
        public class MaterialInfo
        {
            public string Name;
            public int Faces;
            public List<int> Tiles = new List<int>();
        }

        private static string CfgPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker_blender.txt"); }
        }

        // Remembered path, then the usual install folders. Null when not found.
        public static string FindBlender()
        {
            try
            {
                if (File.Exists(CfgPath))
                {
                    string p = File.ReadAllText(CfgPath).Trim();
                    if (File.Exists(p)) return p;
                }
            }
            catch (Exception) { }
            List<string> roots = new List<string>();
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation"));
            roots.Add(@"C:\Program Files (x86)\Steam\steamapps\common\Blender");
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed) continue;
                roots.Add(Path.Combine(d.RootDirectory.FullName, "Blender"));
                roots.Add(Path.Combine(d.RootDirectory.FullName, "Program Files", "Blender Foundation"));
                roots.Add(Path.Combine(d.RootDirectory.FullName, "SteamLibrary", "steamapps", "common", "Blender"));
            }
            foreach (string r in roots)
            {
                try
                {
                    if (!Directory.Exists(r)) continue;
                    string direct = Path.Combine(r, "blender.exe");
                    if (File.Exists(direct)) return direct;
                    // Blender Foundation\Blender 3.6\blender.exe - take the newest version folder
                    string[] subs = Directory.GetDirectories(r);
                    Array.Sort(subs);
                    for (int i = subs.Length - 1; i >= 0; i--)
                    {
                        string c = Path.Combine(subs[i], "blender.exe");
                        if (File.Exists(c)) return c;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        public static void RememberBlender(string path)
        {
            try { File.WriteAllText(CfgPath, path); }
            catch (Exception) { }
        }

        private static string ScriptPath()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ReforgerTexturePacker");
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, "rz_bake.py");
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("rz_bake.py"))
            {
                if (s == null)
                    throw new Exception("rz_bake.py is not embedded in this build.");
                using (FileStream fs = new FileStream(p, FileMode.Create, FileAccess.Write))
                    s.CopyTo(fs);
            }
            return p;
        }

        // UDIM tile of a texture from its file name ("..._BaseColor.1002.png" -> 1002); 1001 when there is none.
        public static int TileFromName(string path)
        {
            if (string.IsNullOrEmpty(path)) return 1001;
            Match m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"[._](1\d{3})$");
            return m.Success ? int.Parse(m.Groups[1].Value) : 1001;
        }

        private static string Quote(string a)
        {
            return "\"" + a.Replace("\"", "\\\"") + "\"";
        }

        // Runs Blender and returns its exit code; each "RZBAKE ..." line goes to onLine (from a worker thread).
        private static int Run(string blender, string[] args, Action<string> onLine, StringBuilder errors)
        {
            StringBuilder sb = new StringBuilder("--background --factory-startup --python " + Quote(ScriptPath()) + " --");
            foreach (string a in args)
                sb.Append(' ').Append(Quote(a));
            ProcessStartInfo psi = new ProcessStartInfo(blender, sb.ToString());
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                DataReceivedEventHandler h = delegate(object s, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("RZBAKE ")) { if (onLine != null) onLine(e.Data.Substring(7)); }
                    else if (e.Data.Contains("Error") || e.Data.Contains("Traceback") || e.Data.StartsWith("  File") || e.Data.StartsWith("No ") || e.Data.StartsWith("Unsupported"))
                        lock (errors) errors.AppendLine(e.Data);
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                p.WaitForExit(); // flushes the async readers
                return p.ExitCode;
            }
        }

        public static List<MaterialInfo> Probe(string blender, string model)
        {
            string json = Path.Combine(Path.GetTempPath(), "ReforgerTexturePacker", "probe.json");
            if (File.Exists(json)) File.Delete(json);
            StringBuilder err = new StringBuilder();
            Run(blender, new string[] { "probe", model, json }, null, err);
            if (!File.Exists(json))
                throw new Exception("Blender could not read the model." + (err.Length > 0 ? "\n" + err.ToString().Trim() : ""));
            // tiny fixed-shape JSON: [{"name": "x", "faces": 1, "tiles": [1001, 1002]}, ...]
            List<MaterialInfo> list = new List<MaterialInfo>();
            string text = File.ReadAllText(json);
            foreach (Match m in Regex.Matches(text, "\\{\"name\": \"((?:[^\"\\\\]|\\\\.)*)\", \"faces\": (\\d+), \"tiles\": \\[([\\d, ]*)\\]\\}"))
            {
                MaterialInfo mi = new MaterialInfo();
                mi.Name = Regex.Unescape(m.Groups[1].Value);
                mi.Faces = int.Parse(m.Groups[2].Value);
                foreach (string t in m.Groups[3].Value.Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    mi.Tiles.Add(int.Parse(t));
                list.Add(mi);
            }
            return list;
        }

        // Preview mesh for the model, cached by path + modification time so reopening is instant.
        public static ModelMesh LoadMesh(string blender, string model)
        {
            FileInfo fi = new FileInfo(model);
            string key = (fi.FullName.ToLowerInvariant() + "|" + fi.LastWriteTimeUtc.Ticks);
            uint hsh = 2166136261;
            foreach (char c in key)
                hsh = unchecked((hsh ^ c) * 16777619);
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker", "meshcache");
            Directory.CreateDirectory(dir);
            string cache = Path.Combine(dir, Path.GetFileNameWithoutExtension(model) + "_" + hsh.ToString("x8") + ".rzm");
            if (!File.Exists(cache))
            {
                StringBuilder err = new StringBuilder();
                string tmp = cache + ".tmp";
                Run(blender, new string[] { "mesh", model, tmp }, null, err);
                if (!File.Exists(tmp))
                    throw new Exception("Blender could not read the model." + (err.Length > 0 ? "\n" + err.ToString().Trim() : ""));
                File.Move(tmp, cache);
            }
            return ModelMesh.Load(cache);
        }

        // Materials that belong to a texture set: names found in the texture's file name, else all used in the tile.
        public static List<string> GuessMaterials(ModelMesh m, int tile, string textureName)
        {
            string tex = (textureName ?? "").ToLowerInvariant();
            bool[] inTile = new bool[m.Materials.Count];
            for (int t = 0; t < m.TriCount; t++)
                if (m.TriTile[t] == tile && m.TriMat[t] < inTile.Length) inTile[m.TriMat[t]] = true;
            List<string> named = new List<string>(), all = new List<string>();
            for (int i = 0; i < m.Materials.Count; i++)
            {
                if (!inTile[i]) continue;
                all.Add(m.Materials[i]);
                if (m.Materials[i].Length > 1 && tex.Contains(m.Materials[i].ToLowerInvariant()))
                    named.Add(m.Materials[i]);
            }
            return named.Count > 0 ? named : all;
        }

        // Which model belongs to which texture folder, so the preview reloads it automatically.
        private static string ModelMapPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ReforgerTexturePacker", "models.txt"); }
        }

        public static string RememberedModel(string textureFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(textureFolder) || !File.Exists(ModelMapPath)) return null;
                string key = Path.GetFullPath(textureFolder).TrimEnd('\\').ToLowerInvariant() + "|";
                foreach (string line in File.ReadAllLines(ModelMapPath))
                    if (line.StartsWith(key))
                    {
                        string p = line.Substring(key.Length);
                        return File.Exists(p) ? p : null;
                    }
            }
            catch (Exception) { }
            return null;
        }

        public static void RememberModel(string textureFolder, string model)
        {
            try
            {
                string key = Path.GetFullPath(textureFolder).TrimEnd('\\').ToLowerInvariant() + "|";
                List<string> lines = new List<string>();
                if (File.Exists(ModelMapPath))
                    foreach (string l in File.ReadAllLines(ModelMapPath))
                        if (!l.StartsWith(key)) lines.Add(l);
                lines.Add(key + model);
                Directory.CreateDirectory(Path.GetDirectoryName(ModelMapPath));
                File.WriteAllLines(ModelMapPath, lines.ToArray());
            }
            catch (Exception) { }
        }

        // Writes ao.png / normal.png / height.png into outDir. Throws with Blender's error text on failure.
        public static void Bake(string blender, string model, string outDir, int w, int h, int tile, List<string> materials, Action<string> progress)
        {
            Directory.CreateDirectory(outDir);
            foreach (string f in new string[] { "ao.png", "normal.png", "height.png" })
                if (File.Exists(Path.Combine(outDir, f))) File.Delete(Path.Combine(outDir, f));
            StringBuilder err = new StringBuilder();
            bool done = false;
            Run(blender, new string[] { "bake", model, outDir, w.ToString(), h.ToString(), tile.ToString(), materials == null || materials.Count == 0 ? "*" : string.Join("|", materials.ToArray()) },
                delegate(string line)
                {
                    if (line == "DONE") done = true;
                    if (progress != null) progress(line);
                }, err);
            if (!done || !File.Exists(Path.Combine(outDir, "ao.png")))
                throw new Exception("Bake failed." + (err.Length > 0 ? "\n" + err.ToString().Trim() : ""));
        }
    }

    // Lets the user choose which of the model's materials belong to this texture set.
    public class MaterialPickDialog : Form
    {
        private CheckedListBox _list;
        private List<BlenderBaker.MaterialInfo> _mats;

        public MaterialPickDialog(List<BlenderBaker.MaterialInfo> mats, int tile, string textureName, List<string> previous)
        {
            _mats = mats;
            Text = "Bake from 3D model - pick materials";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(460, 330);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            Label l = new Label();
            l.Text = "Which materials use this texture set (UDIM tile " + tile + ")?\nEverything else still shades the AO, but isn't baked.";
            l.SetBounds(12, 10, 436, 36);
            Controls.Add(l);

            _list = new CheckedListBox();
            _list.SetBounds(12, 52, 436, 226);
            _list.CheckOnClick = true;
            _list.BackColor = Theme.Field;
            _list.ForeColor = Theme.Text;
            _list.BorderStyle = BorderStyle.FixedSingle;
            string tex = (textureName ?? "").ToLowerInvariant();
            bool anyGuess = false;
            List<bool> guess = new List<bool>();
            foreach (BlenderBaker.MaterialInfo m in mats)
            {
                bool inTile = m.Tiles.Contains(tile);
                bool named = m.Name.Length > 1 && tex.Contains(m.Name.ToLowerInvariant());
                bool g = previous != null && previous.Count > 0 ? previous.Contains(m.Name) : (inTile && named);
                guess.Add(g);
                anyGuess |= g;
                _list.Items.Add(string.Format("{0}    ({1:N0} faces, tiles {2}){3}", m.Name, m.Faces,
                    string.Join(",", m.Tiles.ConvertAll(delegate(int t) { return t.ToString(); }).ToArray()), inTile ? "" : "   - not in this tile"));
            }
            // nothing matched by name: default to every material that has faces in this tile
            for (int i = 0; i < mats.Count; i++)
                _list.SetItemChecked(i, anyGuess ? guess[i] : mats[i].Tiles.Contains(tile));
            Controls.Add(_list);

            Button ok = new Button();
            ok.Text = "Bake";
            ok.SetBounds(262, 290, 90, 30);
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(358, 290, 90, 30);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            Theme.Apply(this);
        }

        public List<string> Selected
        {
            get
            {
                List<string> s = new List<string>();
                for (int i = 0; i < _mats.Count; i++)
                    if (_list.GetItemChecked(i)) s.Add(_mats[i].Name);
                return s;
            }
        }
    }
}
