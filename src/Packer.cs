using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ReforgerTexturePacker
{
    // Image loading, channel extraction, packing and LZW TIFF output.
    public static class Packer
    {
        public static Bitmap LoadBitmap(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".tga")
                return TgaReader.Load(path);

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Image img = Image.FromStream(fs))
            {
                int w = img.Width, h = img.Height;
                Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                Bitmap src = img as Bitmap;
                if (src == null)
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
                    }
                    return bmp;
                }
                // LockBits conversion copies channels 1:1 - DrawImage would premultiply RGB by alpha.
                Rectangle rect = new Rectangle(0, 0, w, h);
                BitmapData sd = src.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                BitmapData dd = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                byte[] row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(new IntPtr(sd.Scan0.ToInt64() + (long)y * sd.Stride), row, 0, row.Length);
                    Marshal.Copy(row, 0, new IntPtr(dd.Scan0.ToInt64() + (long)y * dd.Stride), row.Length);
                }
                src.UnlockBits(sd);
                bmp.UnlockBits(dd);
                return bmp;
            }
        }

        // Returns a bitmap of exactly w x h; disposes the input if a resize was needed.
        public static Bitmap EnsureSize(Bitmap bmp, int w, int h)
        {
            if (bmp.Width == w && bmp.Height == h)
                return bmp;
            Bitmap r = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(r))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(bmp, new Rectangle(0, 0, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
            }
            bmp.Dispose();
            return r;
        }

        // channel: "R", "G", "B", "A" or "Luma".
        public static byte[] ExtractChannel(Bitmap bmp, string channel)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] buf = new byte[stride * h];
            Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            bmp.UnlockBits(data);

            bool luma = false;
            int off = 2;
            if (channel == "R") off = 2;
            else if (channel == "G") off = 1;
            else if (channel == "B") off = 0;
            else if (channel == "A") off = 3;
            else luma = true;

            byte[] outv = new byte[w * h];
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++, i++)
                {
                    int p = row + x * 4;
                    if (luma)
                        outv[i] = (byte)((buf[p + 2] * 299 + buf[p + 1] * 587 + buf[p] * 114) / 1000);
                    else
                        outv[i] = buf[p + off];
                }
            }
            return outv;
        }

        public static void Invert(byte[] v)
        {
            for (int i = 0; i < v.Length; i++)
                v[i] = (byte)(255 - v[i]);
        }

        // Builds a 32-bit bitmap from per-channel arrays; null array = flat default value.
        public static Bitmap Compose(int w, int h, byte[] r, byte defR, byte[] g, byte defG, byte[] b, byte defB, byte[] a, byte defA)
        {
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            byte[] buf = new byte[stride * h];
            int i = 0;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++, i++)
                {
                    int p = row + x * 4;
                    buf[p] = (b != null) ? b[i] : defB;
                    buf[p + 1] = (g != null) ? g[i] : defG;
                    buf[p + 2] = (r != null) ? r[i] : defR;
                    buf[p + 3] = (a != null) ? a[i] : defA;
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            bmp.UnlockBits(data);
            return bmp;
        }

        public static void SaveTiffLzw(Bitmap bmp, string path)
        {
            ImageCodecInfo codec = null;
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == "image/tiff")
                    codec = c;
            if (codec == null)
                throw new Exception("No TIFF encoder available on this system.");

            using (EncoderParameters ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)EncoderValue.CompressionLZW);
                if (File.Exists(path))
                    File.Delete(path);
                bmp.Save(path, codec, ep);
            }
        }
    }
}
