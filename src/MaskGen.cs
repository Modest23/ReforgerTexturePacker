using System;

namespace ReforgerTexturePacker
{
    public class MaskChannelSettings
    {
        public double Strength = 1.2;
        public int Blur = 2;
        public double BaseLevel = 0.05;
        public double RoughWeight = 0.5;
        public double DarkWeight = 0.3;
        public bool EdgeMode; // false = pick up in crevices, true = on edges
    }

    // Derives dirt/mud style masks from normal-map curvature, weighted by roughness and albedo darkness.
    public static class MaskGen
    {
        // curvature = ddx(normal R) - ddy(normal G); expects DirectX-style green (-Y).
        public static float[] Curvature(byte[] r, byte[] g, int w, int h)
        {
            float[] outv = new float[w * h];
            for (int y = 0; y < h; y++)
            {
                int ym = Math.Max(y - 1, 0) * w;
                int yp = Math.Min(y + 1, h - 1) * w;
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    int xm = Math.Max(x - 1, 0);
                    int xp = Math.Min(x + 1, w - 1);
                    float dx = (r[row + xp] - r[row + xm]) * 0.5f;
                    float dy = (g[yp + x] - g[ym + x]) * 0.5f;
                    outv[row + x] = dx - dy;
                }
            }
            return outv;
        }

        public static float[] BoxBlur(float[] src, int w, int h, int radius)
        {
            if (radius <= 0)
                return src;
            float[] tmp = new float[src.Length];
            float[] dst = new float[src.Length];
            double[] pre = new double[Math.Max(w, h) + 1];
            // horizontal
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                pre[0] = 0;
                for (int x = 0; x < w; x++)
                    pre[x + 1] = pre[x] + src[row + x];
                for (int x = 0; x < w; x++)
                {
                    int a = Math.Max(x - radius, 0);
                    int b = Math.Min(x + radius, w - 1);
                    tmp[row + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
                }
            }
            // vertical
            for (int x = 0; x < w; x++)
            {
                pre[0] = 0;
                for (int y = 0; y < h; y++)
                    pre[y + 1] = pre[y] + tmp[y * w + x];
                for (int y = 0; y < h; y++)
                {
                    int a = Math.Max(y - radius, 0);
                    int b = Math.Min(y + radius, h - 1);
                    dst[y * w + x] = (float)((pre[b + 1] - pre[a]) / (b - a + 1));
                }
            }
            return dst;
        }

        // rough/luma may be null (then their influence sliders have no effect).
        // curvGain compensates curvature amplitude when settings tuned on a downscaled preview are applied at full resolution.
        public static byte[] Combine(float[] curv, byte[] rough, byte[] luma, int w, int h, MaskChannelSettings s, double curvGain = 1.0)
        {
            int n = w * h;
            float[] m = new float[n];
            for (int i = 0; i < n; i++)
            {
                double raw = s.EdgeMode ? curv[i] : -curv[i];
                m[i] = raw > 0 ? (float)(raw * curvGain / 48.0) : 0f;
            }
            m = BoxBlur(m, w, h, s.Blur);

            byte[] outv = new byte[n];
            for (int i = 0; i < n; i++)
            {
                double v = m[i] * s.Strength;
                if (v > 1.0) v = 1.0;
                if (rough != null && s.RoughWeight > 0)
                    v *= (1.0 - s.RoughWeight) + s.RoughWeight * (rough[i] / 255.0);
                if (luma != null && s.DarkWeight > 0)
                    v *= (1.0 - s.DarkWeight) + s.DarkWeight * (1.0 - luma[i] / 255.0);
                v = s.BaseLevel + (1.0 - s.BaseLevel) * v;
                outv[i] = (byte)Math.Round(v * 255.0);
            }
            return outv;
        }
    }
}
