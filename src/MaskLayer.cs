using System;
using System.Threading.Tasks;

namespace ReforgerTexturePacker
{
    // One _VFX channel (dirt or mud). Public fields only - MaskPresetStore saves them by reflection.
    public class LayerSettings
    {
        // where it collects
        public double Base;
        public double Cavity, CavityScale = 0.45;
        public double Edges, EdgeScale = 0.15;
        public double Occlusion;
        public double Gradient; public int GradDir; public double GradStart = 0.4, GradEnd = 1.0;
        // on the 3D model (needs "Bake from 3D model"): world-facing and height, plus how strictly to keep to them
        public double FaceUp, FaceDown, Low, LowRange = 0.35, Confine;
        // pattern
        public double Noise, NoiseScale = 0.5;
        public double Scratches, ScratchDensity = 0.5, ScratchLength = 0.3, ScratchAngle = 0, ScratchSpread = 180, ScratchEdgeBias = 0.5;
        public double Grunge, GrungeTiling = 4; public string GrungePath = "";
        // breakup / streaks
        public double Breakup, BreakupScale = 0.45;
        public double Streaks, StreakLength = 0.3;
        public int Seed = 1;
        // surface weighting
        public double RoughInfl, DarkInfl;
        public double Mat1 = 1, Mat2 = 1, Mat3 = 1, Mat4 = 1;
        // output levels
        public double BlackPoint = 0, WhitePoint = 1, Gamma = 1, OutMax = 1, Softness = 0;

        public LayerSettings Clone()
        {
            return (LayerSettings)MemberwiseClone();
        }

        public static readonly string[] PresetNames =
        {
            "Grime (broad, in recesses)", "Mud patches", "Edge wear + scratches", "Dust (broad, soft)",
            "Rain streaks", "Scratches only", "Empty"
        };

        // has3D: a model bake is loaded, so presets use real up/down/height instead of UV guesses.
        public static LayerSettings Preset(int idx, bool has3D)
        {
            LayerSettings s = Preset(idx);
            if (!has3D)
                return s;
            switch (idx)
            {
                case 0: // grime also settles on top surfaces
                    s.FaceUp = 0.3;
                    break;
                case 1: // mud: underneath and low down, splashed in patches - no UV gradient needed
                    s.Gradient = 0;
                    s.FaceDown = 0.6; s.Low = 0.8; s.LowRange = 0.35; s.Confine = 0.85;
                    s.Base = 0.3;
                    break;
                case 3: // dust lies on top
                    s.FaceUp = 0.7; s.Confine = 0.6;
                    break;
            }
            return s;
        }

        public static LayerSettings Preset(int idx)
        {
            LayerSettings s = new LayerSettings();
            switch (idx)
            {
                case 0: // grime: broad mid-level coverage, heavier in recesses and occluded areas (like the vanilla red field)
                    s.Base = 0.3; s.Cavity = 0.6; s.CavityScale = 0.6; s.Occlusion = 0.9;
                    s.Noise = 0.15; s.NoiseScale = 0.7; s.Breakup = 0.25; s.BreakupScale = 0.7;
                    s.Streaks = 0.2; s.StreakLength = 0.2;
                    s.BlackPoint = 0.1; s.WhitePoint = 0.75; s.OutMax = 0.8; s.Softness = 3;
                    break;
                case 1: // mud patches: blotchy, heavier towards the bottom of the UVs
                    s.Base = 0.15; s.Cavity = 0.5; s.CavityScale = 0.65; s.Occlusion = 0.6;
                    s.Gradient = 0.4; s.GradDir = 0; s.GradStart = 0.3; s.GradEnd = 1.0;
                    s.Noise = 0.35; s.NoiseScale = 0.7;
                    s.Breakup = 0.65; s.BreakupScale = 0.7;
                    s.BlackPoint = 0.3; s.WhitePoint = 0.7; s.OutMax = 0.85; s.Softness = 3;
                    break;
                case 2: // edge wear: soft bands along raised edges, a few short scratches on them
                    s.Edges = 0.9; s.EdgeScale = 0.3;
                    s.Scratches = 0.35; s.ScratchDensity = 0.35; s.ScratchLength = 0.15; s.ScratchEdgeBias = 0.9;
                    s.Breakup = 0.4; s.BreakupScale = 0.35;
                    s.BlackPoint = 0.08; s.WhitePoint = 0.6; s.OutMax = 0.85; s.Softness = 1;
                    break;
                case 3: // dust: light, even, soft
                    s.Base = 0.6; s.Cavity = 0.3; s.CavityScale = 0.8; s.Occlusion = 0.3;
                    s.Noise = 0.4; s.NoiseScale = 0.75; s.Breakup = 0.3; s.BreakupScale = 0.75;
                    s.BlackPoint = 0.1; s.WhitePoint = 0.9; s.OutMax = 0.45; s.Softness = 3;
                    break;
                case 4: // rain streaks running down from recesses
                    s.Base = 0.1; s.Cavity = 0.7; s.CavityScale = 0.35; s.Occlusion = 0.3;
                    s.Streaks = 0.85; s.StreakLength = 0.5;
                    s.Breakup = 0.3; s.BreakupScale = 0.4;
                    s.BlackPoint = 0.1; s.WhitePoint = 0.7; s.OutMax = 0.75; s.Softness = 1;
                    break;
                case 5:
                    s.Scratches = 1.0; s.ScratchDensity = 0.45; s.ScratchLength = 0.2; s.ScratchEdgeBias = 0;
                    s.WhitePoint = 0.7; s.OutMax = 0.85;
                    break;
            }
            return s;
        }
    }

    public static class LayerEval
    {
        // matPresence: 4 soft maps (Mat1..Mat4) summing to ~1 per pixel, or null to ignore per-material weights.
        public static float[] Evaluate(MaskSources src, LayerSettings s, float[][] matPresence)
        {
            int w = src.W, h = src.H, n = w * h;
            float[] cav = s.Cavity > 0 ? src.Relief(s.CavityScale) : null;
            float[] edg = (s.Edges > 0 || (s.Scratches > 0 && s.ScratchEdgeBias > 0)) ? src.Relief(s.EdgeScale) : null;
            float[] occ = s.Occlusion > 0 ? src.Occlusion() : null;
            float[] noi = s.Noise > 0 ? src.Noise(s.NoiseScale, s.Seed) : null;
            float[] scr = s.Scratches > 0 ? src.Scratch(s.ScratchDensity, s.ScratchLength, s.ScratchAngle, s.ScratchSpread, s.Seed) : null;
            float[] gru = null;
            if (s.Grunge > 0 && !string.IsNullOrEmpty(s.GrungePath))
            {
                try { gru = src.Grunge(s.GrungePath, s.GrungeTiling); }
                catch (Exception) { gru = null; }
            }
            float[] brk = s.Breakup > 0 ? src.Noise(s.BreakupScale, s.Seed + 101) : null;
            float[] rough = src.Rough, luma = src.Luma;
            float[] bUp = src.HasBake ? src.BakeUp : null, bH = src.HasBake ? src.BakeHeight : null;
            float wUp = (float)s.FaceUp, wDown = (float)s.FaceDown, wLow = (float)s.Low, wConf = (float)s.Confine;
            float lowRange = (float)Math.Max(0.02, s.LowRange);
            bool use3D = bUp != null && (wUp > 0 || wDown > 0 || wLow > 0);

            float wBase = (float)s.Base, wCav = (float)s.Cavity, wEdg = (float)s.Edges, wOcc = (float)s.Occlusion, wGrad = (float)s.Gradient;
            float wNoi = (float)s.Noise, wScr = (float)s.Scratches, wGru = (float)s.Grunge, wBrk = (float)s.Breakup;
            float scrBias = (float)s.ScratchEdgeBias, rInf = (float)s.RoughInfl, dInf = (float)s.DarkInfl;
            float gs = (float)s.GradStart, ge = (float)s.GradEnd;
            int gdir = s.GradDir;

            float[] v = new float[n];
            Parallel.For(0, h, delegate(int y)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // "screen" combine: each source can bring the value up on its own, none can cancel another.
                    float keep = 1f - wBase;
                    if (cav != null) keep *= 1f - wCav * MaskOps.Clamp01(-cav[i]);
                    if (edg != null && wEdg > 0) keep *= 1f - wEdg * MaskOps.Clamp01(edg[i]);
                    if (occ != null) keep *= 1f - wOcc * occ[i];
                    if (wGrad > 0)
                    {
                        float t;
                        if (gdir == 0) t = (y + 0.5f) / h;
                        else if (gdir == 1) t = 1f - (y + 0.5f) / h;
                        else if (gdir == 2) t = (x + 0.5f) / w;
                        else t = 1f - (x + 0.5f) / w;
                        keep *= 1f - wGrad * MaskOps.SmoothStep(gs, ge, t);
                    }
                    if (noi != null) keep *= 1f - wNoi * noi[i];
                    if (scr != null)
                    {
                        float bias = edg != null ? 1f - scrBias + scrBias * MaskOps.Clamp01(edg[i] * 2f) : 1f;
                        keep *= 1f - wScr * scr[i] * bias;
                    }
                    if (gru != null) keep *= 1f - wGru * gru[i];
                    float val;
                    if (use3D)
                    {
                        float up = bUp[i];
                        float k3 = 1f;
                        if (wUp > 0) k3 *= 1f - wUp * MaskOps.SmoothStep(0.2f, 0.9f, up);
                        if (wDown > 0) k3 *= 1f - wDown * MaskOps.SmoothStep(0.1f, 0.8f, -up);
                        if (wLow > 0) k3 *= 1f - wLow * (1f - MaskOps.SmoothStep(lowRange * 0.25f, lowRange, bH[i]));
                        float model = 1f - k3;
                        val = 1f - keep * k3;
                        // Confine: everything else (noise, crevices...) only shows where the model terms allow it
                        val *= 1f - wConf + wConf * model;
                    }
                    else
                        val = 1f - keep;

                    if (brk != null)
                        val *= 1f - wBrk + wBrk * MaskOps.SmoothStep(0.15f, 0.85f, brk[i]);
                    if (rough != null && rInf > 0)
                        val *= 1f - rInf + rInf * rough[i];
                    if (luma != null && dInf > 0)
                        val *= 1f - dInf + dInf * (1f - luma[i]);
                    v[i] = val;
                }
            });

            if (s.Streaks > 0)
            {
                float[] st = MaskOps.StreakDown(v, w, h, (4.0 + s.StreakLength * 250.0) * src.ResFactor, s.Seed + 7);
                float a = (float)s.Streaks;
                for (int i = 0; i < n; i++)
                    v[i] = Math.Max(v[i], st[i] * a);
            }
            if (s.Softness > 0)
                v = MaskOps.Gauss(v, w, h, s.Softness * src.ResFactor);

            float bp = (float)s.BlackPoint, wp = (float)Math.Max(s.WhitePoint, s.BlackPoint + 0.01);
            float gamma = (float)Math.Max(0.05, s.Gamma), outMax = (float)s.OutMax;
            float m1 = (float)s.Mat1, m2 = (float)s.Mat2, m3 = (float)s.Mat3, m4 = (float)s.Mat4;
            bool useMat = matPresence != null && (m1 < 1f || m2 < 1f || m3 < 1f || m4 < 1f);
            Parallel.For(0, h, delegate(int y)
            {
                for (int i = y * w, e = i + w; i < e; i++)
                {
                    float t = MaskOps.Clamp01((v[i] - bp) / (wp - bp));
                    if (gamma != 1f && t > 0f)
                        t = (float)Math.Pow(t, gamma);
                    t *= outMax;
                    if (useMat)
                        t *= matPresence[0][i] * m1 + matPresence[1][i] * m2 + matPresence[2][i] * m3 + matPresence[3][i] * m4;
                    v[i] = t;
                }
            });
            return v;
        }
    }
}
