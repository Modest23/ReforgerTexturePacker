using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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
        public string OutDir;
        public string BaseName = "Texture";
    }

    // Three independent mask generators (A/B/C) from normal-map curvature + BCR inputs.
    // A/B feed _VFX (R=dirt, G=mud); any of A/B/C (or inverted) route into _GLOBAL_MASK channels.
    public class MaskVfxDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private MaskGenContext _ctx;
        private PictureBox _preview;
        private ComboBox _cmbView;
        private ComboBox _cmbMaskR, _cmbMaskG, _cmbMaskB;
        private Label _status;
        private MaskChannelSettings _a = new MaskChannelSettings();
        private MaskChannelSettings _b = new MaskChannelSettings();
        private MaskChannelSettings _c = new MaskChannelSettings();

        private int _pw, _ph, _fullW, _fullH;
        private float[] _curv;
        private byte[] _rough, _luma;
        private byte[] _maskA, _maskB, _maskC;
        private bool _ready;

        public MaskVfxDialog(MaskGenContext ctx)
        {
            _ctx = ctx;
            // A = dirt-in-crevices, B = broad mud, C = edge wear.
            _b.Strength = 1.0; _b.Blur = 8; _b.BaseLevel = 0; _b.RoughWeight = 0.3; _b.DarkWeight = 0.5;
            _c.Strength = 1.0; _c.Blur = 1; _c.BaseLevel = 0; _c.RoughWeight = 0.3; _c.DarkWeight = 0.2; _c.Mode = 1;

            Text = "Mask / VFX Generator";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1280, 586);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            _preview = new PictureBox();
            _preview.SetBounds(12, 12, 440, 480);
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _preview.BackColor = Theme.ThumbBg;
            _preview.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(_preview);

            Label lv = new Label();
            lv.Text = "Preview:";
            lv.SetBounds(12, 504, 56, 16);
            Controls.Add(lv);

            _cmbView = new ComboBox();
            _cmbView.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbView.Items.AddRange(new object[] { "Mask A", "Mask B", "Mask C", "VFX composite", "Global Mask (RGB)" });
            _cmbView.SelectedIndex = 3;
            _cmbView.SetBounds(72, 500, 150, 23);
            _cmbView.SelectedIndexChanged += delegate { RenderPreview(); };
            Controls.Add(_cmbView);

            Label lsrc = new Label();
            lsrc.Text = "Uses: Normal" + (_ctx.RoughPath != null ? " + Rough" : "") + (_ctx.BasePath != null ? " + Color" : "");
            lsrc.SetBounds(240, 504, 212, 16);
            lsrc.ForeColor = Theme.SubText;
            Controls.Add(lsrc);

            BuildChannelGroup("Mask A  ->  _VFX red (dirt)", 460, 12, _a);
            BuildChannelGroup("Mask B  ->  _VFX green (mud)", 460, 236, _b);
            BuildChannelGroup("Mask C  ->  extra (Global Mask)", 868, 12, _c);

            // PBRMulti global mask: black = Material 1, R/G/B = Materials 2/3/4.
            DarkGroupBox grpLayout = new DarkGroupBox();
            grpLayout.Text = "_GLOBAL_MASK layout  (PBRMulti: black = Mat 1)";
            grpLayout.SetBounds(868, 236, 400, 56);
            Controls.Add(grpLayout);
            _cmbMaskR = MaskSourceCombo(grpLayout, 8, "R = Mat 2:", 1);
            _cmbMaskG = MaskSourceCombo(grpLayout, 139, "G = Mat 3:", 2);
            _cmbMaskB = MaskSourceCombo(grpLayout, 270, "B = Mat 4:", 3);

            Button btnVfx = new Button();
            btnVfx.Text = "Export _VFX";
            btnVfx.SetBounds(868, 300, 130, 30);
            btnVfx.Click += delegate { Export(false); };
            Controls.Add(btnVfx);

            Button btnMask = new Button();
            btnMask.Text = "Export _GLOBAL_MASK";
            btnMask.SetBounds(1004, 300, 164, 30);
            btnMask.Click += delegate { Export(true); };
            Controls.Add(btnMask);

            Button btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.SetBounds(1174, 300, 94, 30);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            Label lnote = new Label();
            lnote.Text = "_VFX always exports Mask A into red + Mask B into green.";
            lnote.SetBounds(868, 340, 400, 16);
            lnote.ForeColor = Theme.SubText;
            Controls.Add(lnote);

            _status = new Label();
            _status.SetBounds(12, 560, 1256, 20);
            _status.AutoEllipsis = true;
            _status.ForeColor = Theme.SubText;
            _status.Text = "Masks are picked up from normal-map crevices/edges, weighted by roughness and dark albedo.";
            Controls.Add(_status);

            Theme.Apply(this);
        }

        private void BuildChannelGroup(string title, int x, int y, MaskChannelSettings s)
        {
            DarkGroupBox g = new DarkGroupBox();
            g.Text = title;
            g.SetBounds(x, y, 400, 218);
            Controls.Add(g);
            AddSlider(g, 22, "Strength", 0, 300, (int)Math.Round(s.Strength * 100), delegate(int v) { s.Strength = v / 100.0; });
            AddSlider(g, 54, "Blur", 0, 20, s.Blur, delegate(int v) { s.Blur = v; });
            AddSlider(g, 86, "Base level", 0, 100, (int)Math.Round(s.BaseLevel * 100), delegate(int v) { s.BaseLevel = v / 100.0; });
            AddSlider(g, 118, "Rough infl.", 0, 100, (int)Math.Round(s.RoughWeight * 100), delegate(int v) { s.RoughWeight = v / 100.0; });
            AddSlider(g, 150, "Dark infl.", 0, 100, (int)Math.Round(s.DarkWeight * 100), delegate(int v) { s.DarkWeight = v / 100.0; });

            Label lm = new Label();
            lm.Text = "Pick up in:";
            lm.SetBounds(10, 188, 70, 16);
            g.Controls.Add(lm);
            ComboBox cm = new ComboBox();
            cm.DropDownStyle = ComboBoxStyle.DropDownList;
            cm.Items.AddRange(new object[] { "Crevices", "Edges", "Both", "Flat areas" });
            cm.SelectedIndex = s.Mode;
            cm.SetBounds(84, 184, 110, 23);
            cm.SelectedIndexChanged += delegate { s.Mode = cm.SelectedIndex; UpdateMasks(); };
            g.Controls.Add(cm);
        }

        private ComboBox MaskSourceCombo(Control parent, int x, string caption, int defIdx)
        {
            Label l = new Label();
            l.Text = caption;
            l.SetBounds(x, 26, 64, 16);
            parent.Controls.Add(l);
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(new object[] { "None", "Mask A", "Mask B", "Mask C", "Inv A", "Inv B", "Inv C", "White" });
            c.SelectedIndex = defIdx;
            c.SetBounds(x + 64, 22, 66, 23);
            c.SelectedIndexChanged += delegate { RenderPreview(); };
            parent.Controls.Add(c);
            return c;
        }

        private static void ResolveMask(int sel, byte[] a, byte[] b, byte[] c, out byte[] arr, out byte def)
        {
            arr = null;
            def = 0;
            if (sel == 1) arr = a;
            else if (sel == 2) arr = b;
            else if (sel == 3) arr = c;
            else if (sel == 4) arr = InvertCopy(a);
            else if (sel == 5) arr = InvertCopy(b);
            else if (sel == 6) arr = InvertCopy(c);
            else if (sel == 7) def = 255;
        }

        private static byte[] InvertCopy(byte[] src)
        {
            byte[] c = (byte[])src.Clone();
            Packer.Invert(c);
            return c;
        }

        private void AddSlider(Control parent, int y, string caption, int min, int max, int val, Action<int> onChange)
        {
            Label l = new Label();
            l.Text = caption;
            l.SetBounds(10, y + 4, 70, 16);
            parent.Controls.Add(l);

            TrackBar tb = new TrackBar();
            tb.AutoSize = false;
            tb.SetBounds(82, y, 240, 28);
            tb.Minimum = min;
            tb.Maximum = max;
            tb.Value = val;
            tb.TickStyle = TickStyle.None;
            tb.BackColor = Theme.Bg;
            parent.Controls.Add(tb);

            Label lval = new Label();
            lval.SetBounds(328, y + 4, 60, 16);
            lval.Text = val.ToString();
            parent.Controls.Add(lval);

            tb.ValueChanged += delegate
            {
                lval.Text = tb.Value.ToString();
                onChange(tb.Value);
                UpdateMasks();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int v = Theme.Dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref v, 4); }
            catch (Exception) { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                PreparePreviewData();
                _ready = true;
                UpdateMasks();
            }
            catch (Exception ex)
            {
                _status.Text = "Failed to load source maps: " + ex.Message;
            }
        }

        private void PreparePreviewData()
        {
            Bitmap nm = Packer.LoadBitmap(_ctx.NormalPath);
            _fullW = nm.Width;
            _fullH = nm.Height;
            double sc = 512.0 / Math.Max(_fullW, _fullH);
            if (sc > 1.0) sc = 1.0;
            _pw = Math.Max(1, (int)(_fullW * sc));
            _ph = Math.Max(1, (int)(_fullH * sc));
            nm = Packer.EnsureSize(nm, _pw, _ph);
            byte[] nr = Packer.ExtractChannel(nm, "R");
            byte[] ng = Packer.ExtractChannel(nm, "G");
            nm.Dispose();
            if (_ctx.FlipGreen)
                Packer.Invert(ng);
            _curv = MaskGen.Curvature(nr, ng, _pw, _ph);
            _rough = LoadAux(_ctx.RoughPath, _ctx.RoughChannel, _ctx.RoughInvert, _pw, _ph);
            _luma = LoadAux(_ctx.BasePath, "Luma", false, _pw, _ph);
        }

        private static byte[] LoadAux(string path, string channel, bool invert, int w, int h)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            Bitmap bmp = Packer.EnsureSize(Packer.LoadBitmap(path), w, h);
            byte[] v = Packer.ExtractChannel(bmp, channel);
            bmp.Dispose();
            if (invert)
                Packer.Invert(v);
            return v;
        }

        private void UpdateMasks()
        {
            if (!_ready)
                return;
            _maskA = MaskGen.Combine(_curv, _rough, _luma, _pw, _ph, _a);
            _maskB = MaskGen.Combine(_curv, _rough, _luma, _pw, _ph, _b);
            _maskC = MaskGen.Combine(_curv, _rough, _luma, _pw, _ph, _c);
            RenderPreview();
        }

        private void RenderPreview()
        {
            if (_maskA == null)
                return;
            Bitmap bmp;
            int view = _cmbView.SelectedIndex;
            if (view == 0)
                bmp = Packer.Compose(_pw, _ph, _maskA, 0, _maskA, 0, _maskA, 0, null, 255);
            else if (view == 1)
                bmp = Packer.Compose(_pw, _ph, _maskB, 0, _maskB, 0, _maskB, 0, null, 255);
            else if (view == 2)
                bmp = Packer.Compose(_pw, _ph, _maskC, 0, _maskC, 0, _maskC, 0, null, 255);
            else if (view == 4)
            {
                byte[] mr, mg, mb; byte dr, dg, db;
                ResolveMask(_cmbMaskR.SelectedIndex, _maskA, _maskB, _maskC, out mr, out dr);
                ResolveMask(_cmbMaskG.SelectedIndex, _maskA, _maskB, _maskC, out mg, out dg);
                ResolveMask(_cmbMaskB.SelectedIndex, _maskA, _maskB, _maskC, out mb, out db);
                bmp = Packer.Compose(_pw, _ph, mr, dr, mg, dg, mb, db, null, 255);
            }
            else
                bmp = Packer.Compose(_pw, _ph, _maskA, 0, _maskB, 0, null, 0, null, 255);
            if (_preview.Image != null)
                _preview.Image.Dispose();
            _preview.Image = bmp;
        }

        private void Export(bool asGlobalMask)
        {
            if (!_ready)
            {
                _status.Text = "Sources not loaded yet.";
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                // recompute at full resolution; scale blur + curvature gain so it matches the preview look
                Bitmap nm = Packer.EnsureSize(Packer.LoadBitmap(_ctx.NormalPath), _fullW, _fullH);
                byte[] nr = Packer.ExtractChannel(nm, "R");
                byte[] ng = Packer.ExtractChannel(nm, "G");
                nm.Dispose();
                if (_ctx.FlipGreen)
                    Packer.Invert(ng);
                float[] curv = MaskGen.Curvature(nr, ng, _fullW, _fullH);
                byte[] rough = LoadAux(_ctx.RoughPath, _ctx.RoughChannel, _ctx.RoughInvert, _fullW, _fullH);
                byte[] luma = LoadAux(_ctx.BasePath, "Luma", false, _fullW, _fullH);

                double resScale = (double)Math.Max(_fullW, _fullH) / Math.Max(_pw, _ph);
                byte[] a = MaskGen.Combine(curv, rough, luma, _fullW, _fullH, ScaledForExport(_a, resScale), resScale);
                byte[] b = MaskGen.Combine(curv, rough, luma, _fullW, _fullH, ScaledForExport(_b, resScale), resScale);
                byte[] c = MaskGen.Combine(curv, rough, luma, _fullW, _fullH, ScaledForExport(_c, resScale), resScale);

                string dir = string.IsNullOrEmpty(_ctx.OutDir) ? Path.GetDirectoryName(_ctx.NormalPath) : _ctx.OutDir;
                Directory.CreateDirectory(dir);
                string bn = string.IsNullOrEmpty(_ctx.BaseName) ? "Texture" : _ctx.BaseName;

                if (asGlobalMask)
                {
                    byte[] mr, mg, mb; byte dr, dg, db;
                    ResolveMask(_cmbMaskR.SelectedIndex, a, b, c, out mr, out dr);
                    ResolveMask(_cmbMaskG.SelectedIndex, a, b, c, out mg, out dg);
                    ResolveMask(_cmbMaskB.SelectedIndex, a, b, c, out mb, out db);
                    string path = Path.Combine(dir, bn + "_GLOBAL_MASK.tif");
                    using (Bitmap outBmp = Packer.Compose(_fullW, _fullH, mr, dr, mg, dg, mb, db, null, 255))
                        Packer.SaveTiffLzw(outBmp, path);
                    _status.Text = "Saved " + path + "   (R=Mat2 G=Mat3 B=Mat4, black=Mat1; set compression manually: R-only=RedHQ, RGB=ColorHQ)";
                }
                else
                {
                    string path = Path.Combine(dir, bn + "_VFX.tif");
                    using (Bitmap outBmp = Packer.Compose(_fullW, _fullH, a, 0, b, 0, null, 0, null, 255))
                        Packer.SaveTiffLzw(outBmp, path);
                    _status.Text = string.Format("Saved {0}   ({1}x{2}, R=dirt G=mud, LZW)", path, _fullW, _fullH);
                }
            }
            catch (Exception ex)
            {
                _status.Text = "ERROR: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private static MaskChannelSettings ScaledForExport(MaskChannelSettings s, double resScale)
        {
            MaskChannelSettings c = new MaskChannelSettings();
            c.Strength = s.Strength;
            c.Blur = (int)Math.Round(s.Blur * resScale);
            c.BaseLevel = s.BaseLevel;
            c.RoughWeight = s.RoughWeight;
            c.DarkWeight = s.DarkWeight;
            c.Mode = s.Mode;
            return c;
        }
    }
}
