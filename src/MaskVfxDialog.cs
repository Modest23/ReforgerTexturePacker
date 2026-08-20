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

    // Generates _VFX (R=dirt, G=mud) and _GLOBAL_MASK textures from normal-map curvature + BCR inputs.
    public class MaskVfxDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private MaskGenContext _ctx;
        private PictureBox _preview;
        private ComboBox _cmbView;
        private ComboBox _cmbMaskR, _cmbMaskG, _cmbMaskB;
        private Label _status;
        private MaskChannelSettings _dirt = new MaskChannelSettings();
        private MaskChannelSettings _mud = new MaskChannelSettings();

        private int _pw, _ph, _fullW, _fullH;
        private float[] _curv;
        private byte[] _rough, _luma;
        private byte[] _dirtMask, _mudMask;
        private bool _ready;

        public MaskVfxDialog(MaskGenContext ctx)
        {
            _ctx = ctx;
            _mud.Strength = 1.0; _mud.Blur = 8; _mud.BaseLevel = 0; _mud.RoughWeight = 0.3; _mud.DarkWeight = 0.5;

            Text = "Mask / VFX Generator";
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(932, 586);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;

            _preview = new PictureBox();
            _preview.SetBounds(12, 12, 480, 480);
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
            _cmbView.Items.AddRange(new object[] { "Dirt (R)", "Mud (G)", "VFX composite", "Global Mask (RGB)" });
            _cmbView.SelectedIndex = 2;
            _cmbView.SetBounds(72, 500, 150, 23);
            _cmbView.SelectedIndexChanged += delegate { RenderPreview(); };
            Controls.Add(_cmbView);

            Label lsrc = new Label();
            lsrc.Text = "Sources: Normal" + (_ctx.RoughPath != null ? " + Roughness" : "") + (_ctx.BasePath != null ? " + Base Color" : "");
            lsrc.SetBounds(240, 504, 250, 16);
            lsrc.ForeColor = Theme.SubText;
            Controls.Add(lsrc);

            BuildChannelGroup("Dirt mask  ->  _VFX red", 500, 12, _dirt);
            BuildChannelGroup("Mud mask  ->  _VFX green", 500, 236, _mud);

            // PBRMulti global mask: black = Material 1, R/G/B = Materials 2/3/4.
            DarkGroupBox grpLayout = new DarkGroupBox();
            grpLayout.Text = "_GLOBAL_MASK layout  (PBRMulti: black = Mat 1)";
            grpLayout.SetBounds(500, 458, 420, 56);
            Controls.Add(grpLayout);
            _cmbMaskR = MaskSourceCombo(grpLayout, 10, "R = Mat 2:", 1);
            _cmbMaskG = MaskSourceCombo(grpLayout, 150, "G = Mat 3:", 0);
            _cmbMaskB = MaskSourceCombo(grpLayout, 290, "B = Mat 4:", 0);

            Button btnVfx = new Button();
            btnVfx.Text = "Export _VFX";
            btnVfx.SetBounds(500, 522, 150, 30);
            btnVfx.Click += delegate { Export(false); };
            Controls.Add(btnVfx);

            Button btnMask = new Button();
            btnMask.Text = "Export _GLOBAL_MASK";
            btnMask.SetBounds(656, 522, 170, 30);
            btnMask.Click += delegate { Export(true); };
            Controls.Add(btnMask);

            Button btnClose = new Button();
            btnClose.Text = "Close";
            btnClose.SetBounds(836, 522, 84, 30);
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            _status = new Label();
            _status.SetBounds(12, 560, 908, 20);
            _status.AutoEllipsis = true;
            _status.ForeColor = Theme.SubText;
            _status.Text = "Dirt/mud are picked up from normal-map crevices, weighted by roughness and dark albedo.";
            Controls.Add(_status);

            Theme.Apply(this);
        }

        private void BuildChannelGroup(string title, int x, int y, MaskChannelSettings s)
        {
            DarkGroupBox g = new DarkGroupBox();
            g.Text = title;
            g.SetBounds(x, y, 420, 218);
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
            l.SetBounds(x, 26, 62, 16);
            parent.Controls.Add(l);
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(new object[] { "None", "Dirt", "Mud", "Dirt inv", "Mud inv", "White" });
            c.SelectedIndex = defIdx;
            c.SetBounds(x + 62, 22, 66, 23);
            c.SelectedIndexChanged += delegate { RenderPreview(); };
            parent.Controls.Add(c);
            return c;
        }

        private static void ResolveMask(int sel, byte[] dirt, byte[] mud, out byte[] arr, out byte def)
        {
            arr = null;
            def = 0;
            if (sel == 1) arr = dirt;
            else if (sel == 2) arr = mud;
            else if (sel == 3) arr = InvertCopy(dirt);
            else if (sel == 4) arr = InvertCopy(mud);
            else if (sel == 5) def = 255;
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
            tb.SetBounds(82, y, 260, 28);
            tb.Minimum = min;
            tb.Maximum = max;
            tb.Value = val;
            tb.TickStyle = TickStyle.None;
            tb.BackColor = Theme.Bg;
            parent.Controls.Add(tb);

            Label lval = new Label();
            lval.SetBounds(348, y + 4, 62, 16);
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
            _dirtMask = MaskGen.Combine(_curv, _rough, _luma, _pw, _ph, _dirt);
            _mudMask = MaskGen.Combine(_curv, _rough, _luma, _pw, _ph, _mud);
            RenderPreview();
        }

        private void RenderPreview()
        {
            if (_dirtMask == null)
                return;
            Bitmap bmp;
            int view = _cmbView.SelectedIndex;
            if (view == 0)
                bmp = Packer.Compose(_pw, _ph, _dirtMask, 0, _dirtMask, 0, _dirtMask, 0, null, 255);
            else if (view == 1)
                bmp = Packer.Compose(_pw, _ph, _mudMask, 0, _mudMask, 0, _mudMask, 0, null, 255);
            else if (view == 3)
            {
                byte[] mr, mg, mb; byte dr, dg, db;
                ResolveMask(_cmbMaskR.SelectedIndex, _dirtMask, _mudMask, out mr, out dr);
                ResolveMask(_cmbMaskG.SelectedIndex, _dirtMask, _mudMask, out mg, out dg);
                ResolveMask(_cmbMaskB.SelectedIndex, _dirtMask, _mudMask, out mb, out db);
                bmp = Packer.Compose(_pw, _ph, mr, dr, mg, dg, mb, db, null, 255);
            }
            else
                bmp = Packer.Compose(_pw, _ph, _dirtMask, 0, _mudMask, 0, null, 0, null, 255);
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
                MaskChannelSettings d = ScaledForExport(_dirt, resScale);
                MaskChannelSettings m = ScaledForExport(_mud, resScale);
                byte[] dirt = MaskGen.Combine(curv, rough, luma, _fullW, _fullH, d, resScale);
                byte[] mud = MaskGen.Combine(curv, rough, luma, _fullW, _fullH, m, resScale);

                string dir = string.IsNullOrEmpty(_ctx.OutDir) ? Path.GetDirectoryName(_ctx.NormalPath) : _ctx.OutDir;
                Directory.CreateDirectory(dir);
                string bn = string.IsNullOrEmpty(_ctx.BaseName) ? "Texture" : _ctx.BaseName;

                if (asGlobalMask)
                {
                    byte[] mr, mg, mb; byte dr, dg, db;
                    ResolveMask(_cmbMaskR.SelectedIndex, dirt, mud, out mr, out dr);
                    ResolveMask(_cmbMaskG.SelectedIndex, dirt, mud, out mg, out dg);
                    ResolveMask(_cmbMaskB.SelectedIndex, dirt, mud, out mb, out db);
                    string path = Path.Combine(dir, bn + "_GLOBAL_MASK.tif");
                    using (Bitmap outBmp = Packer.Compose(_fullW, _fullH, mr, dr, mg, dg, mb, db, null, 255))
                        Packer.SaveTiffLzw(outBmp, path);
                    _status.Text = "Saved " + path + "   (R=Mat2 G=Mat3 B=Mat4, black=Mat1; set compression manually: R-only=RedHQ, RGB=ColorHQ)";
                }
                else
                {
                    string path = Path.Combine(dir, bn + "_VFX.tif");
                    using (Bitmap outBmp = Packer.Compose(_fullW, _fullH, dirt, 0, mud, 0, null, 0, null, 255))
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
