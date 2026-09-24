using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace ReforgerTexturePacker
{
    public class MaskGenContext
    {
        public string NormalPath;
        public bool FlipGreen;
        public string BasePath;
        public string RoughPath;
        public string RoughChannel = "R";
        public bool RoughInvert;
        public string MetalPath;
        public string MetalChannel = "R";
        public string AoPath;
        public string AoChannel = "R";
        public string OutDir;
        public string ModelPath;  // model loaded in the main window's 3D preview, if any
        public string BaseName = "Texture";
    }

    // Every source map resampled to one working resolution, plus the derived surface analysis
    // (multi-scale convexity/concavity) the mask layers are built from. Built once per dialog/resolution.
    public class MaskSources
    {
        public int W, H, FullW, FullH;
        public float[] ColR, ColG, ColB, Luma;   // null without a base color
        public float[] Rough, Metal, AoMap;       // null when not loaded
        public float[] Nx, Ny;                    // DirectX convention: +X right, +Y down the image

        // Blur radii of the curvature bands, in pixels at 1024 px; scaled with the working resolution.
        private static readonly double[] BandRadii = { 0, 1, 2, 4, 8, 16, 32 };
        private float[][] _bands;                 // each normalized to ~unit range
        private Dictionary<int, float[]> _relief = new Dictionary<int, float[]>();
        private Dictionary<string, float[]> _noise = new Dictionary<string, float[]>();
        private Dictionary<string, float[]> _scratch = new Dictionary<string, float[]>();
        private Dictionary<string, float[]> _grunge = new Dictionary<string, float[]>();
        private float[] _detail, _occEst;

        public double ResFactor { get { return Math.Max(W, H) / 1024.0; } }
        public bool HasColor { get { return ColR != null; } }

        public static MaskSources Load(MaskGenContext ctx, int workRes)
        {
            MaskSources s = new MaskSources();
            Bitmap nm = Packer.LoadBitmap(ctx.NormalPath);
            s.FullW = nm.Width;
            s.FullH = nm.Height;
            double sc = (double)workRes / Math.Max(s.FullW, s.FullH);
            if (sc > 1.0)
                sc = 1.0;
            s.W = Math.Max(8, (int)Math.Round(s.FullW * sc));
            s.H = Math.Max(8, (int)Math.Round(s.FullH * sc));
            nm = Packer.EnsureSize(nm, s.W, s.H);
            byte[] nr = Packer.ExtractChannel(nm, "R");
            byte[] ng = Packer.ExtractChannel(nm, "G");
            nm.Dispose();
            if (ctx.FlipGreen)
                Packer.Invert(ng);
            int n = s.W * s.H;
            s.Nx = new float[n];
            s.Ny = new float[n];
            for (int i = 0; i < n; i++)
            {
                s.Nx[i] = nr[i] / 127.5f - 1f;
                s.Ny[i] = ng[i] / 127.5f - 1f;
            }

            if (!string.IsNullOrEmpty(ctx.BasePath))
            {
                Bitmap bc = Packer.EnsureSize(Packer.LoadBitmap(ctx.BasePath), s.W, s.H);
                s.ColR = MaskOps.ToFloat(Packer.ExtractChannel(bc, "R"));
                s.ColG = MaskOps.ToFloat(Packer.ExtractChannel(bc, "G"));
                s.ColB = MaskOps.ToFloat(Packer.ExtractChannel(bc, "B"));
                s.Luma = MaskOps.ToFloat(Packer.ExtractChannel(bc, "Luma"));
                bc.Dispose();
            }
            s.Rough = LoadAux(ctx.RoughPath, ctx.RoughChannel, ctx.RoughInvert, s.W, s.H);
            s.Metal = LoadAux(ctx.MetalPath, ctx.MetalChannel, false, s.W, s.H);
            s.AoMap = LoadAux(ctx.AoPath, ctx.AoChannel, false, s.W, s.H);
            s.Analyze();
            return s;
        }

        private static float[] LoadAux(string path, string channel, bool invert, int w, int h)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            Bitmap bmp = Packer.EnsureSize(Packer.LoadBitmap(path), w, h);
            byte[] v = Packer.ExtractChannel(bmp, channel ?? "R");
            bmp.Dispose();
            if (invert)
                Packer.Invert(v);
            return MaskOps.ToFloat(v);
        }

        private void Analyze()
        {
            int w = W, h = H, n = w * h;
            // Convexity = divergence of the normal's XY. With DirectX (+Y down) a bump reads
            // dX/dx > 0 and dY/dy > 0, so convex = dX/dx + dY/dy (positive = edge, negative = crevice).
            float[] div = new float[n];
            Parallel.For(0, h, delegate(int y)
            {
                int ym = Math.Max(y - 1, 0) * w, yp = Math.Min(y + 1, h - 1) * w, row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int xm = Math.Max(x - 1, 0), xp = Math.Min(x + 1, w - 1);
                    div[row + x] = 0.5f * (Nx[row + xp] - Nx[row + xm]) + 0.5f * (Ny[yp + x] - Ny[ym + x]);
                }
            });
            // UV seams / island borders produce huge one-pixel spikes - clip them before they bleed into the blurred bands.
            float clip = Math.Max(1e-4f, MaskOps.Percentile(div, 0.995, true) * 1.5f);
            for (int i = 0; i < n; i++)
                div[i] = Math.Max(-clip, Math.Min(clip, div[i])) / clip;

            _bands = new float[BandRadii.Length][];
            for (int j = 0; j < BandRadii.Length; j++)
            {
                double r = BandRadii[j] * ResFactor;
                float[] b = r < 0.5 ? (float[])div.Clone() : MaskOps.Gauss(div, w, h, r);
                // integrating convexity over a wider window = relief relative to that neighbourhood;
                // normalizing each band keeps every scale on the same slider range.
                float p = Math.Max(1e-5f, MaskOps.Percentile(b, 0.99, true));
                for (int i = 0; i < n; i++)
                    b[i] /= p;
                _bands[j] = b;
            }

            float[] ad = new float[n];
            for (int i = 0; i < n; i++)
                ad[i] = Math.Abs(div[i]);
            _detail = MaskOps.Gauss(ad, w, h, 3.0 * ResFactor);
            MaskOps.NormalizeRange(_detail);
        }

        // Signed relief at a scale (0 = finest detail, 1 = broad forms): >0 raised / edges, <0 recessed / crevices.
        public float[] Relief(double scale)
        {
            int key = (int)Math.Round(Math.Max(0, Math.Min(1, scale)) * 40);
            float[] r;
            if (_relief.TryGetValue(key, out r))
                return r;
            int nb = _bands.Length, n = W * H;
            double center = key / 40.0 * (nb - 1);
            double[] wt = new double[nb];
            double sum = 0;
            for (int j = 0; j < nb; j++)
            {
                double d = j - center;
                wt[j] = Math.Exp(-d * d / (2 * 0.9 * 0.9));
                sum += wt[j];
            }
            r = new float[n];
            for (int j = 0; j < nb; j++)
            {
                float f = (float)(wt[j] / sum);
                if (f < 1e-3f)
                    continue;
                float[] b = _bands[j];
                for (int i = 0; i < n; i++)
                    r[i] += b[i] * f;
            }
            float p = Math.Max(1e-5f, MaskOps.Percentile(r, 0.985, true));
            for (int i = 0; i < n; i++)
                r[i] = Math.Max(-1f, Math.Min(1f, r[i] / p));
            Remember(_relief, key, r, 16);
            return r;
        }

        // Local amount of surface detail (bumpy/treaded vs smooth) - a clustering feature.
        public float[] Detail { get { return _detail; } }

        public bool HasAoMap { get { return AoMap != null || BakeAo != null; } }

        // ---- model bake (BlenderBaker) -------------------------------------------------------
        public float[] BakeAo;      // 0..1, 1 = open
        public float[] BakeUp;      // world normal Z: 1 = faces straight up, -1 = straight down
        public float[] BakeHeight;  // 0 = lowest point of the model, 1 = highest
        public bool HasBake { get { return BakeUp != null; } }

        // Loads ao/normal/height.png from a bake folder; false when there is no complete bake there.
        public bool LoadBake(string dir)
        {
            string ao = System.IO.Path.Combine(dir, "ao.png"), nm = System.IO.Path.Combine(dir, "normal.png"), ht = System.IO.Path.Combine(dir, "height.png");
            if (!System.IO.File.Exists(ao) || !System.IO.File.Exists(nm) || !System.IO.File.Exists(ht))
                return false;
            int n = W * H;
            float[] a = LoadAux(ao, "R", false, W, H), hh = LoadAux(ht, "R", false, W, H);
            Bitmap nb = Packer.EnsureSize(Packer.LoadBitmap(nm), W, H);
            byte[] nr = Packer.ExtractChannel(nb, "R"), ng = Packer.ExtractChannel(nb, "G"), nz = Packer.ExtractChannel(nb, "B");
            nb.Dispose();
            float[] up = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (nr[i] < 3 && ng[i] < 3 && nz[i] < 3)
                {
                    // outside every baked UV island (bake background is black): keep it neutral
                    up[i] = 0f; a[i] = 1f; hh[i] = 0.5f;
                }
                else
                    up[i] = nz[i] / 127.5f - 1f;
            }
            BakeAo = a;
            BakeUp = up;
            BakeHeight = hh;
            _occEst = null;
            return true;
        }

        public void ClearBake()
        {
            BakeAo = BakeUp = BakeHeight = null;
            _occEst = null;
        }

        // 1 = fully occluded. From the model bake / AO map when present, otherwise estimated from broad concavity.
        public float[] Occlusion()
        {
            if (_occEst != null)
                return _occEst;
            int n = W * H;
            _occEst = new float[n];
            if (BakeAo != null)
            {
                for (int i = 0; i < n; i++)
                    _occEst[i] = 1f - (AoMap != null ? Math.Min(BakeAo[i], AoMap[i]) : BakeAo[i]);
            }
            else if (AoMap != null)
            {
                for (int i = 0; i < n; i++)
                    _occEst[i] = 1f - AoMap[i];
            }
            else
            {
                float[] a = Relief(0.7), b = Relief(0.95);
                for (int i = 0; i < n; i++)
                    _occEst[i] = MaskOps.Clamp01(-(a[i] * 0.5f + b[i] * 0.5f));
            }
            return _occEst;
        }

        // scale 0..1 -> coarsest feature 4..320 px (at 1024).
        public float[] Noise(double scale, int seed)
        {
            double cell = (4.0 + 316.0 * scale * scale) * ResFactor;
            string key = Math.Round(cell, 1) + "_" + seed;
            float[] v;
            if (!_noise.TryGetValue(key, out v))
            {
                v = MaskOps.FbmNoise(W, H, cell, 5, seed);
                Remember(_noise, key, v, 8);
            }
            return v;
        }

        public float[] Scratch(double density, double length, double angle, double spread, int seed)
        {
            string key = string.Join("_", new string[] { density.ToString("0.00"), length.ToString("0.00"), angle.ToString("0"), spread.ToString("0"), seed.ToString() });
            float[] v;
            if (!_scratch.TryGetValue(key, out v))
            {
                v = MaskOps.Scratches(W, H, density, length, angle, spread, seed);
                Remember(_scratch, key, v, 6);
            }
            return v;
        }

        public float[] Grunge(string path, double tiling)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string key = path + "|" + tiling.ToString("0.00");
            float[] v;
            if (_grunge.TryGetValue(key, out v))
                return v;
            Bitmap bmp = Packer.LoadBitmap(path);
            int sw = bmp.Width, sh = bmp.Height;
            // cap the source so a 4K grunge sheet doesn't cost seconds per tiling change
            if (Math.Max(sw, sh) > 1024)
            {
                double f = 1024.0 / Math.Max(sw, sh);
                sw = Math.Max(1, (int)(sw * f));
                sh = Math.Max(1, (int)(sh * f));
                bmp = Packer.EnsureSize(bmp, sw, sh);
            }
            float[] src = MaskOps.ToFloat(Packer.ExtractChannel(bmp, "Luma"));
            bmp.Dispose();
            v = MaskOps.Tile(src, sw, sh, W, H, tiling);
            Remember(_grunge, key, v, 4);
            return v;
        }

        // Dragging a scale slider creates a new map per step - keep the caches bounded (4 MB each at 1024).
        private static void Remember<TKey>(Dictionary<TKey, float[]> cache, TKey key, float[] v, int max)
        {
            if (cache.Count >= max)
                cache.Clear();
            cache[key] = v;
        }
    }
}
