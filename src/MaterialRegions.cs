using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace ReforgerTexturePacker
{
    public class RegionSettings
    {
        public int Source;              // 0 = auto clusters, 1 = ID map
        public string IdMapPath = "";
        public int Clusters = 6;
        public double ColorW = 1.0, RoughW = 0.6, MetalW = 1.0, DetailW = 0.3;
        public int Cleanup = 2;
        public double EdgeSoft = 0;
    }

    // How one _GLOBAL_MASK channel (Mat 2/3/4) is filled inside its region. The shader thresholds the
    // channel against the material's tiling Mask_N, so a gradient here becomes peeling/scratches in-game.
    public class MatChannelSettings
    {
        public double Level = 1;
        public int Modulate;            // index into ModulateNames
        public double Amount = 0.5, Scale = 0.3;
        public bool Invert;
        public int Seed = 3;

        public static readonly string[] ModulateNames =
        {
            "None (solid)", "Edges", "Crevices", "Occlusion", "Noise", "Scratches", "UV gradient down", "UV gradient up"
        };

        public MatChannelSettings Clone()
        {
            return (MatChannelSettings)MemberwiseClone();
        }
    }

    // A per-pixel region label map (clusters or ID-map colours) plus each region's material assignment.
    public class Regions
    {
        public const int MaxRegions = 24;
        public int W, H, Count;
        public byte[] Labels;
        public int[] Assign;            // region -> material 0..3 (0 = Mat 1 / black)
        public float[] Area;            // fraction of the texture
        public Color[] MeanColor;       // average albedo (or the ID colour)
        public double[][] Centroid;     // feature-space centre, for auto-assign merging
        public bool IsIdMap;

        public static readonly Color[] Palette = BuildPalette();

        private static Color[] BuildPalette()
        {
            Color[] p = new Color[MaxRegions];
            for (int i = 0; i < MaxRegions; i++)
            {
                double hue = (i * 0.618034) % 1.0;
                double val = (i % 3 == 2) ? 0.7 : 0.95;
                double sat = (i % 2 == 0) ? 0.75 : 0.55;
                p[i] = FromHsv(hue * 360.0, sat, val);
            }
            return p;
        }

        private static Color FromHsv(double hDeg, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((hDeg / 60.0) % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (hDeg < 60) { r = c; g = x; }
            else if (hDeg < 120) { r = x; g = c; }
            else if (hDeg < 180) { g = c; b = x; }
            else if (hDeg < 240) { g = x; b = c; }
            else if (hDeg < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        public int LabelAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= W || y >= H)
                return -1;
            return Labels[y * W + x];
        }

        // paint: per-pixel override (-1 = none, 0..3 = material) or null.
        public byte[] MaterialLabels(sbyte[] paint)
        {
            int n = W * H;
            byte[] m = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int p = paint != null ? paint[i] : -1;
                m[i] = (byte)(p >= 0 ? p : Assign[Labels[i]]);
            }
            return m;
        }

        // Pixels 4-connected to (x,y) that share its region label.
        public bool[] ConnectedArea(int x, int y)
        {
            int n = W * H;
            bool[] hit = new bool[n];
            int lab = LabelAt(x, y);
            if (lab < 0)
                return hit;
            int[] queue = new int[n];
            int qh = 0, qt = 0;
            int start = y * W + x;
            hit[start] = true;
            queue[qt++] = start;
            while (qh < qt)
            {
                int i = queue[qh++];
                int px = i % W, py = i / W;
                if (px > 0) Visit(i - 1, lab, hit, queue, ref qt);
                if (px < W - 1) Visit(i + 1, lab, hit, queue, ref qt);
                if (py > 0) Visit(i - W, lab, hit, queue, ref qt);
                if (py < H - 1) Visit(i + W, lab, hit, queue, ref qt);
            }
            return hit;
        }

        private void Visit(int j, int lab, bool[] hit, int[] queue, ref int qt)
        {
            if (!hit[j] && Labels[j] == lab)
            {
                hit[j] = true;
                queue[qt++] = j;
            }
        }

        // ---- auto clustering --------------------------------------------------------------

        public static Regions Cluster(MaskSources s, RegionSettings rs)
        {
            int w = s.W, h = s.H, n = w * h;
            List<float[]> feats = new List<float[]>();
            if (s.HasColor && rs.ColorW > 0)
            {
                float[] L = new float[n], A = new float[n], B = new float[n];
                Parallel.For(0, n, delegate(int i)
                {
                    double l, a, b;
                    ToLab(s.ColR[i], s.ColG[i], s.ColB[i], out l, out a, out b);
                    L[i] = (float)(l / 100.0 * rs.ColorW);
                    // chroma counts a bit more than lightness: shading varies lightness within one material
                    A[i] = (float)(a / 100.0 * rs.ColorW * 1.4);
                    B[i] = (float)(b / 100.0 * rs.ColorW * 1.4);
                });
                feats.Add(L); feats.Add(A); feats.Add(B);
            }
            if (s.Rough != null && rs.RoughW > 0)
                feats.Add(Scaled(s.Rough, rs.RoughW));
            if (s.Metal != null && rs.MetalW > 0)
                feats.Add(Scaled(s.Metal, rs.MetalW));
            if (rs.DetailW > 0 || feats.Count == 0)
                feats.Add(Scaled(s.Detail, Math.Max(0.3, rs.DetailW)));

            // light pre-blur: clusters should follow material areas, not per-pixel texture noise
            double pre = 1.0 * s.ResFactor;
            for (int f = 0; f < feats.Count; f++)
                feats[f] = MaskOps.Gauss(feats[f], w, h, pre);

            int d = feats.Count;
            int k = Math.Max(2, Math.Min(MaxRegions, rs.Clusters));

            // k-means++ on a sample, deterministic seed so the same settings give the same regions
            Random rnd = new Random(1234);
            int sampleN = Math.Min(n, 60000);
            int[] sample = new int[sampleN];
            for (int i = 0; i < sampleN; i++)
                sample[i] = sampleN == n ? i : rnd.Next(n);
            double[][] cen = new double[k][];
            cen[0] = Feature(feats, sample[rnd.Next(sampleN)]);
            double[] dist = new double[sampleN];
            for (int c = 1; c < k; c++)
            {
                double total = 0;
                for (int i = 0; i < sampleN; i++)
                {
                    double best = double.MaxValue;
                    for (int j = 0; j < c; j++)
                        best = Math.Min(best, Dist2(feats, sample[i], cen[j]));
                    dist[i] = best;
                    total += best;
                }
                double pick = rnd.NextDouble() * total;
                int chosen = sampleN - 1;
                for (int i = 0; i < sampleN; i++)
                {
                    pick -= dist[i];
                    if (pick <= 0) { chosen = i; break; }
                }
                cen[c] = Feature(feats, sample[chosen]);
            }
            int[] sl = new int[sampleN];
            for (int it = 0; it < 25; it++)
            {
                double[][] acc = new double[k][];
                int[] cnt = new int[k];
                for (int c = 0; c < k; c++)
                    acc[c] = new double[d];
                bool changed = false;
                for (int i = 0; i < sampleN; i++)
                {
                    int best = Nearest(feats, sample[i], cen);
                    if (best != sl[i] || it == 0) changed = true;
                    sl[i] = best;
                    cnt[best]++;
                    for (int f = 0; f < d; f++)
                        acc[best][f] += feats[f][sample[i]];
                }
                for (int c = 0; c < k; c++)
                    if (cnt[c] > 0)
                        for (int f = 0; f < d; f++)
                            cen[c][f] = acc[c][f] / cnt[c];
                if (!changed)
                    break;
            }

            byte[] labels = new byte[n];
            Parallel.For(0, n, delegate(int i) { labels[i] = (byte)Nearest(feats, i, cen); });

            Regions r = new Regions();
            r.W = w; r.H = h;
            r.Labels = labels;
            r.Count = k;
            int rad = (int)Math.Round(rs.Cleanup * s.ResFactor);
            if (rad > 0)
                r.MajorityFilter(rad);
            r.Compact();
            r.ComputeStats(s, feats);
            r.AutoAssign();
            return r;
        }

        private static float[] Scaled(float[] v, double k)
        {
            float[] o = new float[v.Length];
            float f = (float)k;
            for (int i = 0; i < v.Length; i++)
                o[i] = v[i] * f;
            return o;
        }

        private static double[] Feature(List<float[]> feats, int i)
        {
            double[] v = new double[feats.Count];
            for (int f = 0; f < feats.Count; f++)
                v[f] = feats[f][i];
            return v;
        }

        private static double Dist2(List<float[]> feats, int i, double[] c)
        {
            double s = 0;
            for (int f = 0; f < c.Length; f++)
            {
                double dd = feats[f][i] - c[f];
                s += dd * dd;
            }
            return s;
        }

        private static int Nearest(List<float[]> feats, int i, double[][] cen)
        {
            int best = 0;
            double bd = double.MaxValue;
            for (int c = 0; c < cen.Length; c++)
            {
                double dd = Dist2(feats, i, cen[c]);
                if (dd < bd) { bd = dd; best = c; }
            }
            return best;
        }

        private static void ToLab(float r, float g, float b, out double L, out double A, out double B)
        {
            double lr = Lin(r), lg = Lin(g), lb = Lin(b);
            double x = (lr * 0.4124 + lg * 0.3576 + lb * 0.1805) / 0.95047;
            double y = lr * 0.2126 + lg * 0.7152 + lb * 0.0722;
            double z = (lr * 0.0193 + lg * 0.1192 + lb * 0.9505) / 1.08883;
            x = LabF(x); y = LabF(y); z = LabF(z);
            L = 116 * y - 16;
            A = 500 * (x - y);
            B = 200 * (y - z);
        }

        private static double Lin(double c)
        {
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static double LabF(double t)
        {
            return t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : 7.787 * t + 16.0 / 116.0;
        }

        // Replaces each label with the most common label in its neighbourhood (removes speckle).
        private void MajorityFilter(int radius)
        {
            int n = W * H;
            float[] bestVal = new float[n];
            byte[] best = new byte[n];
            float[] onehot = new float[n];
            for (int c = 0; c < Count; c++)
            {
                for (int i = 0; i < n; i++)
                    onehot[i] = Labels[i] == c ? 1f : 0f;
                float[] bl = MaskOps.BoxBlur(onehot, W, H, radius);
                for (int i = 0; i < n; i++)
                    if (bl[i] > bestVal[i]) { bestVal[i] = bl[i]; best[i] = (byte)c; }
            }
            Labels = best;
        }

        // Drops empty labels and renumbers largest-first so the list reads biggest region at the top.
        private void Compact()
        {
            int[] cnt = new int[256];
            for (int i = 0; i < Labels.Length; i++)
                cnt[Labels[i]]++;
            List<int> used = new List<int>();
            for (int c = 0; c < 256; c++)
                if (cnt[c] > 0) used.Add(c);
            used.Sort(delegate(int a, int b) { return cnt[b].CompareTo(cnt[a]); });
            byte[] map = new byte[256];
            for (int j = 0; j < used.Count; j++)
                map[used[j]] = (byte)j;
            for (int i = 0; i < Labels.Length; i++)
                Labels[i] = map[Labels[i]];
            Count = used.Count;
        }

        private void ComputeStats(MaskSources s, List<float[]> feats)
        {
            int n = W * H, d = feats != null ? feats.Count : 3;
            double[] cr = new double[Count], cg = new double[Count], cb = new double[Count];
            int[] cnt = new int[Count];
            Centroid = new double[Count][];
            for (int c = 0; c < Count; c++)
                Centroid[c] = new double[d];
            for (int i = 0; i < n; i++)
            {
                int l = Labels[i];
                cnt[l]++;
                if (s.HasColor) { cr[l] += s.ColR[i]; cg[l] += s.ColG[i]; cb[l] += s.ColB[i]; }
                if (feats != null)
                    for (int f = 0; f < d; f++)
                        Centroid[l][f] += feats[f][i];
            }
            Area = new float[Count];
            MeanColor = new Color[Count];
            for (int c = 0; c < Count; c++)
            {
                Area[c] = cnt[c] / (float)n;
                if (cnt[c] > 0 && feats != null)
                    for (int f = 0; f < d; f++)
                        Centroid[c][f] /= cnt[c];
                MeanColor[c] = s.HasColor && cnt[c] > 0
                    ? Color.FromArgb((int)(cr[c] / cnt[c] * 255), (int)(cg[c] / cnt[c] * 255), (int)(cb[c] / cnt[c] * 255))
                    : Palette[c % MaxRegions];
            }
        }

        // Starting guess: merge the most similar regions until 4 groups remain; biggest group = Mat 1.
        public void AutoAssign()
        {
            Assign = new int[Count];
            List<List<int>> groups = new List<List<int>>();
            List<double[]> gc = new List<double[]>();
            List<double> ga = new List<double>();
            for (int c = 0; c < Count; c++)
            {
                List<int> g = new List<int>();
                g.Add(c);
                groups.Add(g);
                gc.Add((double[])Centroid[c].Clone());
                ga.Add(Area[c]);
            }
            while (groups.Count > 4)
            {
                int bi = 0, bj = 1;
                double bd = double.MaxValue;
                for (int i = 0; i < groups.Count; i++)
                    for (int j = i + 1; j < groups.Count; j++)
                    {
                        double dd = 0;
                        for (int f = 0; f < gc[i].Length; f++)
                        {
                            double t = gc[i][f] - gc[j][f];
                            dd += t * t;
                        }
                        if (dd < bd) { bd = dd; bi = i; bj = j; }
                    }
                double wa = ga[bi], wb = ga[bj], wt = Math.Max(1e-9, wa + wb);
                for (int f = 0; f < gc[bi].Length; f++)
                    gc[bi][f] = (gc[bi][f] * wa + gc[bj][f] * wb) / wt;
                ga[bi] = wa + wb;
                groups[bi].AddRange(groups[bj]);
                groups.RemoveAt(bj); gc.RemoveAt(bj); ga.RemoveAt(bj);
            }
            List<int> order = new List<int>();
            for (int i = 0; i < groups.Count; i++)
                order.Add(i);
            order.Sort(delegate(int a, int b) { return ga[b].CompareTo(ga[a]); });
            for (int m = 0; m < order.Count; m++)
                foreach (int c in groups[order[m]])
                    Assign[c] = m;
        }

        // ---- ID map ------------------------------------------------------------------------

        // Flat-colour material ID bake (e.g. Blender/Substance). Anti-aliased edge pixels snap to the nearest kept colour.
        public static Regions FromIdMap(MaskSources s, string path)
        {
            int w = s.W, h = s.H, n = w * h;
            Bitmap bmp = Packer.LoadBitmap(path);
            int sw = bmp.Width, sh = bmp.Height;
            byte[] br = Packer.ExtractChannel(bmp, "R"), bg = Packer.ExtractChannel(bmp, "G"), bb = Packer.ExtractChannel(bmp, "B");
            bmp.Dispose();
            // nearest sampling - filtering would invent in-between colours
            int[] rgb = new int[n];
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(sh - 1, (int)((y + 0.5) * sh / h));
                for (int x = 0; x < w; x++)
                {
                    int si = sy * sw + Math.Min(sw - 1, (int)((x + 0.5) * sw / w));
                    rgb[y * w + x] = (br[si] << 16) | (bg[si] << 8) | bb[si];
                }
            }
            int[] cnt = new int[32768];
            for (int i = 0; i < n; i++)
                cnt[Key15(rgb[i])]++;
            List<int> keys = new List<int>();
            for (int k = 0; k < 32768; k++)
                if (cnt[k] >= Math.Max(1, n / 1000)) keys.Add(k);
            keys.Sort(delegate(int a, int b) { return cnt[b].CompareTo(cnt[a]); });
            if (keys.Count > MaxRegions)
                keys.RemoveRange(MaxRegions, keys.Count - MaxRegions);
            if (keys.Count == 0)
                throw new Exception("ID map has no dominant colours.");

            int kc = keys.Count;
            double[] mr = new double[kc], mg = new double[kc], mb = new double[kc];
            int[] mc = new int[kc];
            int[] keyToLabel = new int[32768];
            for (int k = 0; k < 32768; k++)
                keyToLabel[k] = -1;
            for (int j = 0; j < kc; j++)
                keyToLabel[keys[j]] = j;
            for (int i = 0; i < n; i++)
            {
                int l = keyToLabel[Key15(rgb[i])];
                if (l < 0) continue;
                mr[l] += (rgb[i] >> 16) & 255; mg[l] += (rgb[i] >> 8) & 255; mb[l] += rgb[i] & 255;
                mc[l]++;
            }
            for (int j = 0; j < kc; j++)
            {
                mr[j] /= mc[j]; mg[j] /= mc[j]; mb[j] /= mc[j];
            }
            // remaining (edge / rare) colours -> nearest kept colour
            for (int k = 0; k < 32768; k++)
            {
                if (keyToLabel[k] >= 0) continue;
                double r = ((k >> 10) & 31) * 8 + 4, g = ((k >> 5) & 31) * 8 + 4, b = (k & 31) * 8 + 4;
                int best = 0;
                double bd = double.MaxValue;
                for (int j = 0; j < kc; j++)
                {
                    double dd = (r - mr[j]) * (r - mr[j]) + (g - mg[j]) * (g - mg[j]) + (b - mb[j]) * (b - mb[j]);
                    if (dd < bd) { bd = dd; best = j; }
                }
                keyToLabel[k] = best;
            }
            Regions reg = new Regions();
            reg.W = w; reg.H = h;
            reg.IsIdMap = true;
            reg.Labels = new byte[n];
            for (int i = 0; i < n; i++)
                reg.Labels[i] = (byte)keyToLabel[Key15(rgb[i])];
            reg.Count = kc;
            reg.Compact();
            reg.Area = new float[reg.Count];
            reg.MeanColor = new Color[reg.Count];
            reg.Centroid = new double[reg.Count][];
            double[][] acc = new double[reg.Count][];
            int[] cc = new int[reg.Count];
            for (int c = 0; c < reg.Count; c++)
                acc[c] = new double[3];
            for (int i = 0; i < n; i++)
            {
                int l = reg.Labels[i];
                cc[l]++;
                acc[l][0] += (rgb[i] >> 16) & 255; acc[l][1] += (rgb[i] >> 8) & 255; acc[l][2] += rgb[i] & 255;
            }
            for (int c = 0; c < reg.Count; c++)
            {
                reg.Area[c] = cc[c] / (float)n;
                double[] m = new double[3];
                for (int f = 0; f < 3; f++)
                    m[f] = cc[c] > 0 ? acc[c][f] / cc[c] : 0;
                reg.MeanColor[c] = Color.FromArgb((int)m[0], (int)m[1], (int)m[2]);
                reg.Centroid[c] = new double[] { m[0] / 255.0, m[1] / 255.0, m[2] / 255.0 };
            }
            reg.AutoAssign();
            return reg;
        }

        private static int Key15(int rgb)
        {
            return (((rgb >> 19) & 31) << 10) | (((rgb >> 11) & 31) << 5) | ((rgb >> 3) & 31);
        }
    }

    public static class GlobalMaskComposer
    {
        // Soft presence of each material (0..3) from the hard label map. sigma 0 = hard edges.
        public static float[][] Presence(byte[] mat, int w, int h, double sigma)
        {
            float[][] p = new float[4][];
            int n = w * h;
            for (int k = 0; k < 4; k++)
            {
                float[] a = new float[n];
                for (int i = 0; i < n; i++)
                    a[i] = mat[i] == k ? 1f : 0f;
                p[k] = sigma > 0.3 ? MaskOps.Gauss(a, w, h, sigma) : a;
            }
            return p;
        }

        // Returns the R,G,B channels (Mat 2, 3, 4).
        public static float[][] Compose(MaskSources src, float[][] presence, MatChannelSettings[] ch)
        {
            int w = src.W, h = src.H, n = w * h;
            float[][] outc = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                MatChannelSettings s = ch[c];
                float[] pres = presence[c + 1];
                float[] mod = Modulator(src, s);
                float lvl = (float)s.Level, amt = (float)s.Amount;
                float[] o = new float[n];
                for (int i = 0; i < n; i++)
                {
                    float v = pres[i] * lvl;
                    if (mod != null)
                    {
                        float m = s.Invert ? 1f - mod[i] : mod[i];
                        v *= 1f - amt + amt * m;
                    }
                    o[i] = MaskOps.Clamp01(v);
                }
                outc[c] = o;
            }
            return outc;
        }

        private static float[] Modulator(MaskSources src, MatChannelSettings s)
        {
            int w = src.W, h = src.H, n = w * h;
            switch (s.Modulate)
            {
                case 1:
                case 2:
                {
                    float[] r = src.Relief(s.Scale);
                    float[] m = new float[n];
                    float sign = s.Modulate == 1 ? 1f : -1f;
                    for (int i = 0; i < n; i++)
                        m[i] = MaskOps.Clamp01(r[i] * sign * 1.5f);
                    return m;
                }
                case 3: return src.Occlusion();
                case 4: return src.Noise(s.Scale, s.Seed);
                case 5: return src.Scratch(0.5, s.Scale, 0, 180, s.Seed);
                case 6:
                case 7:
                {
                    float[] m = new float[n];
                    for (int y = 0; y < h; y++)
                    {
                        float t = (y + 0.5f) / h;
                        if (s.Modulate == 7) t = 1f - t;
                        for (int x = 0; x < w; x++)
                            m[y * w + x] = t;
                    }
                    return m;
                }
            }
            return null;
        }
    }
}
