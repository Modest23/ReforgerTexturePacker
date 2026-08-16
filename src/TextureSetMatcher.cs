using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReforgerTexturePacker
{
    public enum MapType { BaseColor, Roughness, Gloss, Opacity, Normal, Metalness, Ao, Orm, Packed }

    public class TextureSetResult
    {
        public string Folder;
        public string BaseName = "";
        public string BaseColor, Roughness, Opacity, Normal, Metalness, Ao;
        public bool RoughnessIsGloss, NormalIsOpenGL;
        public string RoughnessChannel = "R", MetalnessChannel = "R", AoChannel = "R";
        public int Matched;
    }

    // Finds the rest of a PBR texture set from one file, by filename suffix.
    public static class TextureSetMatcher
    {
        private static readonly char[] Seps = { '_', '-', ' ', '.' };
        private static readonly List<KeyValuePair<string, MapType>> Tokens = BuildTokens();

        private static List<KeyValuePair<string, MapType>> BuildTokens()
        {
            List<KeyValuePair<string, MapType>> t = new List<KeyValuePair<string, MapType>>();
            Add(t, MapType.BaseColor, "basecolor", "base_color", "albedo", "diffuse", "diff", "color", "col", "bc", "d");
            Add(t, MapType.Roughness, "roughness", "rough", "rgh", "r");
            Add(t, MapType.Gloss, "glossiness", "gloss", "smoothness", "gls");
            Add(t, MapType.Opacity, "opacity", "transparency", "opac", "alpha");
            Add(t, MapType.Normal, "normal_opengl", "normalopengl", "normal_gl", "normalgl", "normal_directx", "normaldx", "normal_dx", "normal", "nrm", "norm", "nor", "n");
            Add(t, MapType.Metalness, "metallic", "metalness", "metal", "mtl", "met", "m");
            Add(t, MapType.Ao, "ambientocclusion", "ambient_occlusion", "mixed_ao", "mixedao", "occlusion", "ao", "o");
            Add(t, MapType.Orm, "occlusionroughnessmetallic", "orm", "arm");
            Add(t, MapType.Packed, "bcr", "bca", "nmo", "mcr", "nho");
            // Longest tokens first so "normal" wins over "n", "mixed_ao" over "ao", etc.
            return t.OrderByDescending(delegate(KeyValuePair<string, MapType> kv) { return kv.Key.Length; }).ToList();
        }

        private static void Add(List<KeyValuePair<string, MapType>> list, MapType type, params string[] tokens)
        {
            foreach (string tok in tokens)
                list.Add(new KeyValuePair<string, MapType>(tok, type));
        }

        public static bool TryStripSuffix(string lowerName, out string baseLower, out MapType type, out string token)
        {
            foreach (KeyValuePair<string, MapType> kv in Tokens)
            {
                foreach (char sep in Seps)
                {
                    string suffix = sep + kv.Key;
                    if (lowerName.Length > suffix.Length && lowerName.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        baseLower = lowerName.Substring(0, lowerName.Length - suffix.Length);
                        type = kv.Value;
                        token = kv.Key;
                        return true;
                    }
                }
            }
            baseLower = lowerName;
            type = MapType.BaseColor;
            token = null;
            return false;
        }

        public static string DeriveBaseName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string bl; MapType t; string tok;
            if (TryStripSuffix(name.ToLowerInvariant(), out bl, out t, out tok))
                return name.Substring(0, bl.Length);
            return name;
        }

        public static TextureSetResult Match(string droppedPath)
        {
            TextureSetResult res = new TextureSetResult();
            res.Folder = Path.GetDirectoryName(droppedPath);
            string name = Path.GetFileNameWithoutExtension(droppedPath);
            string baseLower; MapType dt; string dtok;
            TryStripSuffix(name.ToLowerInvariant(), out baseLower, out dt, out dtok);
            res.BaseName = name.Substring(0, baseLower.Length);

            HashSet<string> exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".png", ".tif", ".tiff", ".tga", ".jpg", ".jpeg", ".bmp" };

            string[] files;
            try { files = Directory.GetFiles(res.Folder); }
            catch (Exception) { files = new string[0]; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            bool baseExplicit = false, roughFromOrm = false, metalFromOrm = false, aoFromOrm = false;

            foreach (string f in files)
            {
                if (!exts.Contains(Path.GetExtension(f)))
                    continue;
                string fl = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                string fb; MapType ft; string ftok;
                if (!TryStripSuffix(fl, out fb, out ft, out ftok))
                {
                    // Bare "gun.png" next to "gun_normal.png" counts as the base color.
                    if (fl == baseLower && res.BaseColor == null) { res.BaseColor = f; res.Matched++; }
                    continue;
                }
                if (fb != baseLower)
                    continue;

                switch (ft)
                {
                    case MapType.BaseColor:
                        if (res.BaseColor == null) { res.BaseColor = f; baseExplicit = true; res.Matched++; }
                        else if (!baseExplicit) { res.BaseColor = f; baseExplicit = true; }
                        break;
                    case MapType.Roughness:
                        if (res.Roughness == null) { res.Roughness = f; res.Matched++; }
                        else if (res.RoughnessIsGloss || roughFromOrm) res.Roughness = f;
                        else break;
                        res.RoughnessIsGloss = false; res.RoughnessChannel = "R"; roughFromOrm = false;
                        break;
                    case MapType.Gloss:
                        if (res.Roughness == null) { res.Roughness = f; res.RoughnessIsGloss = true; res.RoughnessChannel = "R"; res.Matched++; }
                        break;
                    case MapType.Opacity:
                        if (res.Opacity == null) { res.Opacity = f; res.Matched++; }
                        break;
                    case MapType.Normal:
                        if (res.Normal == null)
                        {
                            res.Normal = f;
                            res.NormalIsOpenGL = ftok != null && ftok.Contains("gl");
                            res.Matched++;
                        }
                        break;
                    case MapType.Metalness:
                        if (res.Metalness == null) { res.Metalness = f; res.Matched++; }
                        else if (metalFromOrm) res.Metalness = f;
                        else break;
                        res.MetalnessChannel = "R"; metalFromOrm = false;
                        break;
                    case MapType.Ao:
                        if (res.Ao == null) { res.Ao = f; res.Matched++; }
                        else if (aoFromOrm) res.Ao = f;
                        else break;
                        res.AoChannel = "R"; aoFromOrm = false;
                        break;
                    case MapType.Orm:
                        // ORM/ARM combined map: R = AO, G = roughness, B = metalness.
                        if (res.Ao == null) { res.Ao = f; res.AoChannel = "R"; aoFromOrm = true; res.Matched++; }
                        if (res.Roughness == null) { res.Roughness = f; res.RoughnessChannel = "G"; res.RoughnessIsGloss = false; roughFromOrm = true; res.Matched++; }
                        if (res.Metalness == null) { res.Metalness = f; res.MetalnessChannel = "B"; metalFromOrm = true; res.Matched++; }
                        break;
                }
            }
            return res;
        }
    }
}
