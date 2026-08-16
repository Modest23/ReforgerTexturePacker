using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ReforgerTexturePacker
{
    // Minimal TGA loader: uncompressed/RLE truecolor (24/32) and grayscale (8).
    public static class TgaReader
    {
        public static Bitmap Load(string path)
        {
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 18)
                throw new Exception("Not a valid TGA file.");

            int idLen = d[0];
            int cmapType = d[1];
            int imgType = d[2];
            int cmapLen = d[5] | (d[6] << 8);
            int cmapDepth = d[7];
            int w = d[12] | (d[13] << 8);
            int h = d[14] | (d[15] << 8);
            int depth = d[16];
            int desc = d[17];
            bool topOrigin = (desc & 0x20) != 0;
            bool rle = imgType == 10 || imgType == 11;
            bool gray = imgType == 3 || imgType == 11;

            if (imgType != 2 && imgType != 3 && imgType != 10 && imgType != 11)
                throw new Exception("Unsupported TGA image type " + imgType + " (palettes not supported).");
            if (gray && depth != 8)
                throw new Exception("Unsupported grayscale TGA depth " + depth + ".");
            if (!gray && depth != 24 && depth != 32)
                throw new Exception("Unsupported TGA depth " + depth + " (need 24 or 32).");
            if (w <= 0 || h <= 0)
                throw new Exception("Invalid TGA dimensions.");

            int bpp = depth / 8;
            int pos = 18 + idLen + (cmapType == 1 ? cmapLen * ((cmapDepth + 7) / 8) : 0);
            int n = w * h;
            byte[] px = new byte[n * bpp];

            if (!rle)
            {
                if (pos + px.Length > d.Length)
                    throw new Exception("TGA file truncated.");
                Buffer.BlockCopy(d, pos, px, 0, px.Length);
            }
            else
            {
                int pi = 0;
                while (pi < px.Length)
                {
                    if (pos >= d.Length)
                        throw new Exception("TGA RLE data truncated.");
                    byte hdr = d[pos++];
                    int count = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0)
                    {
                        for (int k = 0; k < count && pi < px.Length; k++)
                        {
                            Buffer.BlockCopy(d, pos, px, pi, bpp);
                            pi += bpp;
                        }
                        pos += bpp;
                    }
                    else
                    {
                        int bytes = Math.Min(count * bpp, px.Length - pi);
                        Buffer.BlockCopy(d, pos, px, pi, bytes);
                        pi += bytes;
                        pos += count * bpp;
                    }
                }
            }

            byte[] bgra = new byte[n * 4];
            bool anyAlpha = false;
            for (int i = 0; i < n; i++)
            {
                int s = i * bpp;
                int t = i * 4;
                if (gray)
                {
                    byte v = px[s];
                    bgra[t] = v; bgra[t + 1] = v; bgra[t + 2] = v; bgra[t + 3] = 255;
                }
                else if (bpp == 3)
                {
                    bgra[t] = px[s]; bgra[t + 1] = px[s + 1]; bgra[t + 2] = px[s + 2]; bgra[t + 3] = 255;
                }
                else
                {
                    bgra[t] = px[s]; bgra[t + 1] = px[s + 1]; bgra[t + 2] = px[s + 2]; bgra[t + 3] = px[s + 3];
                    if (px[s + 3] != 0) anyAlpha = true;
                }
            }
            // 32-bit files with an all-zero alpha channel are treated as opaque.
            if (bpp == 4 && !anyAlpha)
                for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            {
                int srcRow = topOrigin ? y : (h - 1 - y);
                Marshal.Copy(bgra, srcRow * w * 4, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), w * 4);
            }
            bmp.UnlockBits(data);
            return bmp;
        }
    }
}
