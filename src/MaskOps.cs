using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ReforgerTexturePacker
{
    // Float image helpers for the mask generator. All maps are row-major float[w*h], usually 0..1.
    public static class MaskOps
    {
        public static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        public static float SmoothStep(float e0, float e1, float x)
        {
            if (e1 <= e0)
                return x < e0 ? 0f : 1f;
            float t = Clamp01((x - e0) / (e1 - e0));
            return t * t * (3f - 2f * t);
        }

        public static float[] ToFloat(byte[] b)
        {
            float[] f = new float[b.Length];
            for (int i = 0; i < b.Length; i++)
                f[i] = b[i] / 255f;
            return f;
        }

        public static byte[] ToByte(float[] f)
        {
            byte[] b = new byte[f.Length];
            for (int i = 0; i < f.Length; i++)
            {
                float v = f[i];
                b[i] = (byte)(v <= 0f ? 0 : (v >= 1f ? 255 : (int)(v * 255f + 0.5f)));
            }
            return b;
        }

        // Separable box blur with running sums (edge-clamped window), parallel over rows / columns.
        public static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            if (r <= 0)
                return (float[])src.Clone();
            float[] tmp = new float[src.Length];
            float[] dst = new float[src.Length];
            Parallel.For(0, h, delegate(int y)
            {
                int row = y * w;
                double[] pre = new double[w + 1];
                for (int x = 0; x < w; x++)
                    pre[x + 1] = pre[x] + src[row + x];
                for (int x = 0; x < w; x++)
                {
                    int a = Math.Max(x - r, 0);
                    int b = Math.Min(x + r, w - 1);
                    tmp[row + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
                }
            });
            Parallel.For(0, w, delegate(int x)
            {
                double[] pre = new double[h + 1];
                for (int y = 0; y < h; y++)
                    pre[y + 1] = pre[y] + tmp[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    int a = Math.Max(y - r, 0);
                    int b = Math.Min(y + r, h - 1);
                    dst[y * w + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
                }
            });
            return dst;
        }

        // Gaussian approximation: three box passes with matching variance.
        public static float[] Gauss(float[] src, int w, int h, double sigma)
        {
            if (sigma < 0.35)
                return (float[])src.Clone();
            int r = (int)Math.Round((Math.Sqrt(4.0 * sigma * sigma + 1.0) - 1.0) / 2.0);
            if (r < 1)
                r = 1;
            float[] a = BoxBlur(src, w, h, r);
            a = BoxBlur(a, w, h, r);
            return BoxBlur(a, w, h, r);
        }

        // p in 0..1; strided sample so it stays fast on big maps.
        public static float Percentile(float[] v, double p, bool abs)
        {
            int n = v.Length;
            int step = Math.Max(1, n / 250000);
            List<float> s = new List<float>(n / step + 1);
            for (int i = 0; i < n; i += step)
                s.Add(abs ? Math.Abs(v[i]) : v[i]);
            s.Sort();
            int k = (int)Math.Round(p * (s.Count - 1));
            return s[Math.Max(0, Math.Min(s.Count - 1, k))];
        }

        // Bilinear resample; box-prefilters when shrinking so thin features don't alias.
        public static float[] Resize(float[] src, int w, int h, int tw, int th)
        {
            if (w == tw && h == th)
                return (float[])src.Clone();
            float[] s = src;
            double fx = (double)w / tw, fy = (double)h / th;
            if (fx > 1.5 || fy > 1.5)
                s = BoxBlur(src, w, h, (int)Math.Floor(Math.Max(fx, fy) / 2.0));
            float[] d = new float[tw * th];
            Parallel.For(0, th, delegate(int y)
            {
                double sy = (y + 0.5) * fy - 0.5;
                int y0 = (int)Math.Floor(sy);
                float ty = (float)(sy - y0);
                int ya = Math.Max(0, Math.Min(h - 1, y0)), yb = Math.Max(0, Math.Min(h - 1, y0 + 1));
                for (int x = 0; x < tw; x++)
                {
                    double sx = (x + 0.5) * fx - 0.5;
                    int x0 = (int)Math.Floor(sx);
                    float tx = (float)(sx - x0);
                    int xa = Math.Max(0, Math.Min(w - 1, x0)), xb = Math.Max(0, Math.Min(w - 1, x0 + 1));
                    float top = s[ya * w + xa] + (s[ya * w + xb] - s[ya * w + xa]) * tx;
                    float bot = s[yb * w + xa] + (s[yb * w + xb] - s[yb * w + xa]) * tx;
                    d[y * tw + x] = top + (bot - top) * ty;
                }
            });
            return d;
        }

        public static sbyte[] ResizeNearest(sbyte[] src, int w, int h, int tw, int th)
        {
            sbyte[] d = new sbyte[tw * th];
            for (int y = 0; y < th; y++)
            {
                int sy = Math.Min(h - 1, (int)((y + 0.5) * h / th));
                for (int x = 0; x < tw; x++)
                    d[y * tw + x] = src[sy * w + Math.Min(w - 1, (int)((x + 0.5) * w / tw))];
            }
            return d;
        }

        // Remaps so p2..p98 spans 0..1 (makes generated patterns resolution/seed independent).
        public static void NormalizeRange(float[] v)
        {
            float lo = Percentile(v, 0.02, false), hi = Percentile(v, 0.98, false);
            float d = hi - lo;
            if (d < 1e-6f)
                d = 1e-6f;
            for (int i = 0; i < v.Length; i++)
                v[i] = Clamp01((v[i] - lo) / d);
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint hsh = (uint)(x * 374761393 + y * 668265263 + seed * 144665);
                hsh = (hsh ^ (hsh >> 13)) * 1274126177u;
                hsh ^= hsh >> 16;
                return (hsh & 0xFFFFFF) / 16777215f;
            }
        }

        // Fractal gradient (Perlin) noise, normalized to 0..1. cellPx = size of the coarsest feature in pixels.
        // Each octave is rotated so the lattice never lines up into visible blocks.
        public static float[] FbmNoise(int w, int h, double cellPx, int octaves, int seed)
        {
            float[] outv = new float[w * h];
            if (cellPx < 1.0)
                cellPx = 1.0;
            Parallel.For(0, h, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    double sum = 0, amp = 1, norm = 0, cell = cellPx;
                    for (int o = 0; o < octaves; o++)
                    {
                        double ang = 0.61 + o * 1.13;
                        double ca = Math.Cos(ang), sa = Math.Sin(ang);
                        double fx = (x * ca - y * sa) / cell + o * 17.31;
                        double fy = (x * sa + y * ca) / cell + o * 5.77;
                        sum += Perlin(fx, fy, seed + o * 1013) * amp;
                        norm += amp;
                        amp *= 0.45; // a little below 0.5: softer, less busy blotches
                        cell *= 0.5;
                        if (cell < 1.5)
                            break;
                    }
                    outv[y * w + x] = (float)(sum / norm);
                }
            });
            NormalizeRange(outv);
            return outv;
        }

        private static double Perlin(double fx, double fy, int seed)
        {
            int ix = (int)Math.Floor(fx), iy = (int)Math.Floor(fy);
            double tx = fx - ix, ty = fy - iy;
            double u = tx * tx * tx * (tx * (tx * 6 - 15) + 10);
            double v = ty * ty * ty * (ty * (ty * 6 - 15) + 10);
            double a = Grad(ix, iy, seed, tx, ty), b = Grad(ix + 1, iy, seed, tx - 1, ty);
            double c = Grad(ix, iy + 1, seed, tx, ty - 1), d = Grad(ix + 1, iy + 1, seed, tx - 1, ty - 1);
            double top = a + (b - a) * u, bot = c + (d - c) * u;
            return top + (bot - top) * v;
        }

        private static double Grad(int ix, int iy, int seed, double dx, double dy)
        {
            double ang = Hash(ix, iy, seed) * Math.PI * 2.0;
            return Math.Cos(ang) * dx + Math.Sin(ang) * dy;
        }

        // Random thin anti-aliased line segments, tapered at both ends. Values 0..1.
        public static float[] Scratches(int w, int h, double density, double length, double angleDeg, double spreadDeg, int seed)
        {
            float[] m = new float[w * h];
            double size = Math.Max(w, h);
            int count = (int)(density * density * 3000.0 * (w * (double)h) / (1024.0 * 1024.0));
            double maxLen = (0.005 + length * 0.12) * size;
            Random rnd = new Random(seed * 7919 + 17);
            for (int i = 0; i < count; i++)
            {
                double cx = rnd.NextDouble() * w, cy = rnd.NextDouble() * h;
                double ang = (angleDeg + (rnd.NextDouble() - 0.5) * spreadDeg) * Math.PI / 180.0;
                double len = maxLen * (0.3 + 0.7 * rnd.NextDouble());
                float inten = (float)(0.35 + 0.65 * rnd.NextDouble());
                double dx = Math.Cos(ang), dy = Math.Sin(ang);
                // slight bow so long scratches don't look ruler-straight
                double bow = (rnd.NextDouble() - 0.5) * 0.15 * len;
                int steps = Math.Max(2, (int)(len * 2.0));
                for (int s = 0; s <= steps; s++)
                {
                    double t = (double)s / steps;
                    double off = (t - 0.5) * len;
                    double b = bow * (1.0 - 4.0 * (t - 0.5) * (t - 0.5));
                    double px = cx + dx * off - dy * b, py = cy + dy * off + dx * b;
                    float taper = (float)Math.Sin(t * Math.PI);
                    Splat(m, w, h, px, py, inten * (0.4f + 0.6f * taper));
                }
            }
            return m;
        }

        private static void Splat(float[] m, int w, int h, double px, double py, float v)
        {
            int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
            float tx = (float)(px - x0), ty = (float)(py - y0);
            SplatPix(m, w, h, x0, y0, v * (1 - tx) * (1 - ty));
            SplatPix(m, w, h, x0 + 1, y0, v * tx * (1 - ty));
            SplatPix(m, w, h, x0, y0 + 1, v * (1 - tx) * ty);
            SplatPix(m, w, h, x0 + 1, y0 + 1, v * tx * ty);
        }

        private static void SplatPix(float[] m, int w, int h, int x, int y, float v)
        {
            if (x < 0 || y < 0 || x >= w || y >= h)
                return;
            int i = y * w + x;
            // 1.6x so the pixel nearest the line reaches full value despite the bilinear split
            float nv = Math.Min(1f, v * 1.6f);
            if (nv > m[i])
                m[i] = nv;
        }

        // Tiles a grayscale source over w x h with wrap-around bilinear sampling.
        public static float[] Tile(float[] src, int sw, int sh, int w, int h, double tiling)
        {
            float[] d = new float[w * h];
            if (tiling <= 0)
                tiling = 1;
            Parallel.For(0, h, delegate(int y)
            {
                double v = (y + 0.5) / h * tiling * sh - 0.5;
                int y0 = (int)Math.Floor(v);
                float ty = (float)(v - y0);
                int ya = Mod(y0, sh), yb = Mod(y0 + 1, sh);
                for (int x = 0; x < w; x++)
                {
                    double u = (x + 0.5) / w * tiling * sw - 0.5;
                    int x0 = (int)Math.Floor(u);
                    float tx = (float)(u - x0);
                    int xa = Mod(x0, sw), xb = Mod(x0 + 1, sw);
                    float top = src[ya * sw + xa] + (src[ya * sw + xb] - src[ya * sw + xa]) * tx;
                    float bot = src[yb * sw + xa] + (src[yb * sw + xb] - src[yb * sw + xa]) * tx;
                    d[y * w + x] = top + (bot - top) * ty;
                }
            });
            return d;
        }

        private static int Mod(int a, int n)
        {
            int r = a % n;
            return r < 0 ? r + n : r;
        }

        // Drips: each value leaks along +V (down the image) with exponential falloff.
        // Per-column noise varies the drip lengths so they read as streaks, not a smear.
        public static float[] StreakDown(float[] v, int w, int h, double lengthPx, int seed)
        {
            float[] d = new float[v.Length];
            Parallel.For(0, w, delegate(int x)
            {
                float colVar = 0.35f + 0.65f * Hash(x, 7, seed) * (0.5f + 0.5f * Hash(x / 3, 11, seed));
                double len = Math.Max(1.0, lengthPx * colVar);
                float decay = (float)Math.Exp(-1.0 / len);
                float run = 0f;
                for (int y = 0; y < h; y++)
                {
                    int i = y * w + x;
                    run = Math.Max(v[i], run * decay);
                    d[i] = run;
                }
            });
            return d;
        }
    }
}
