using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    public class MainForm : Form
    {
        private TextureSlot slotBase, slotRough, slotOpacity, slotNormal, slotMetal, slotAo;
        private ComboBox cmbRoughCh, cmbOpacityCh, cmbMetalCh, cmbAoCh, cmbMaxSize;
        private CheckBox chkRoughInvert, chkFlipGreen;
        private NumericUpDown numRoughDef, numMetalDef, numAoDef;
        private TextBox txtOutDir, txtBaseName, txtStatus;
        private Panel _header;
        private Label _lblAppTitle, _lblAppSub, _lblTheme, _lblCredit, _hint;
        private ComboBox _cmbTheme;
        private Button _btnAll;
        private string _startFile;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainForm() : this(null) { }

        public MainForm(string startFile)
        {
            _startFile = startFile;
            Text = "Reforger Texture Packer - by Modest23";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(984, 624);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { }

            // ---- header ----------------------------------------------------------
            _header = new Panel();
            _header.SetBounds(0, 0, 984, 44);
            _header.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Border))
                    e.Graphics.DrawLine(p, 0, _header.Height - 1, _header.Width, _header.Height - 1);
            };
            Controls.Add(_header);

            _lblAppTitle = new Label();
            _lblAppTitle.Text = "Reforger Texture Packer";
            _lblAppTitle.AutoSize = true;
            _lblAppTitle.Location = new Point(12, 9);
            _lblAppTitle.Font = new Font("Segoe UI", 11.5F, FontStyle.Bold);
            _header.Controls.Add(_lblAppTitle);

            _lblAppSub = new Label();
            _lblAppSub.Text = "PBR maps  ->  _BCR / _NMO / _BCA  (8-bit RGBA TIFF, LZW)";
            _lblAppSub.AutoSize = true;
            _lblAppSub.Location = new Point(232, 16);
            _header.Controls.Add(_lblAppSub);

            _lblCredit = new Label();
            _lblCredit.Text = "by Modest23";
            _lblCredit.SetBounds(742, 15, 80, 16);
            _lblCredit.TextAlign = ContentAlignment.MiddleRight;
            _lblCredit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _header.Controls.Add(_lblCredit);

            _lblTheme = new Label();
            _lblTheme.Text = "Theme:";
            _lblTheme.SetBounds(834, 15, 48, 16);
            _lblTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _header.Controls.Add(_lblTheme);

            _cmbTheme = new ComboBox();
            _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTheme.Items.AddRange(new object[] { "Dark", "Light" });
            _cmbTheme.SelectedIndex = Theme.Dark ? 0 : 1;
            _cmbTheme.SetBounds(886, 11, 88, 23);
            _cmbTheme.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _cmbTheme.SelectedIndexChanged += OnThemeChanged;
            _header.Controls.Add(_cmbTheme);

            Button btnAutoFill = new Button();
            btnAutoFill.Text = "Auto-Fill Set…";
            btnAutoFill.SetBounds(8, 52, 120, 28);
            btnAutoFill.Click += OnAutoFillClick;
            Controls.Add(btnAutoFill);

            _hint = new Label();
            _hint.Text = "Pick (or drop) any texture of a PBR set - the rest is matched by name (_BaseColor, _Roughness, _Normal, _Metallic, _AO, ORM). Drop on a slot to set only that slot.";
            _hint.SetBounds(136, 58, 844, 18);
            _hint.AutoEllipsis = true;
            Controls.Add(_hint);

            // ---- BCR / BCA group -------------------------------------------------
            DarkGroupBox grpBcr = new DarkGroupBox();
            grpBcr.Text = "_BCR / _BCA  -  Base Color (RGB) + Roughness / Opacity (A)";
            grpBcr.SetBounds(8, 86, 480, 330);
            Controls.Add(grpBcr);

            slotBase = new TextureSlot("Base Color / Albedo  ->  RGB");
            slotBase.Location = new Point(8, 20);
            grpBcr.Controls.Add(slotBase);

            slotRough = new TextureSlot("Roughness  ->  _BCR alpha");
            slotRough.Location = new Point(8, 122);
            grpBcr.Controls.Add(slotRough);
            cmbRoughCh = ChannelCombo();
            chkRoughInvert = FlowCheck("Invert (gloss)");
            numRoughDef = DefaultNum(0.50m);
            slotRough.Extras.Controls.Add(FlowLabel("Ch:"));
            slotRough.Extras.Controls.Add(cmbRoughCh);
            slotRough.Extras.Controls.Add(chkRoughInvert);
            slotRough.Extras.Controls.Add(FlowLabel("Default:"));
            slotRough.Extras.Controls.Add(numRoughDef);

            slotOpacity = new TextureSlot("Opacity  ->  _BCA alpha (optional)");
            slotOpacity.Location = new Point(8, 224);
            grpBcr.Controls.Add(slotOpacity);
            cmbOpacityCh = ChannelCombo();
            slotOpacity.Extras.Controls.Add(FlowLabel("Ch:"));
            slotOpacity.Extras.Controls.Add(cmbOpacityCh);

            // ---- NMO group -------------------------------------------------------
            DarkGroupBox grpNmo = new DarkGroupBox();
            grpNmo.Text = "_NMO  -  Normal (RG) + Metalness (B) + Occlusion (A)";
            grpNmo.SetBounds(496, 86, 480, 330);
            Controls.Add(grpNmo);

            slotNormal = new TextureSlot("Normal Map  ->  R = +X, G = -Y");
            slotNormal.Location = new Point(8, 20);
            grpNmo.Controls.Add(slotNormal);
            chkFlipGreen = FlowCheck("Flip green (OpenGL -> DirectX)");
            slotNormal.Extras.Controls.Add(chkFlipGreen);

            slotMetal = new TextureSlot("Metalness  ->  _NMO blue");
            slotMetal.Location = new Point(8, 122);
            grpNmo.Controls.Add(slotMetal);
            cmbMetalCh = ChannelCombo();
            numMetalDef = DefaultNum(0.00m);
            slotMetal.Extras.Controls.Add(FlowLabel("Ch:"));
            slotMetal.Extras.Controls.Add(cmbMetalCh);
            slotMetal.Extras.Controls.Add(FlowLabel("Default:"));
            slotMetal.Extras.Controls.Add(numMetalDef);

            slotAo = new TextureSlot("Ambient Occlusion  ->  _NMO alpha");
            slotAo.Location = new Point(8, 224);
            grpNmo.Controls.Add(slotAo);
            cmbAoCh = ChannelCombo();
            numAoDef = DefaultNum(1.00m);
            slotAo.Extras.Controls.Add(FlowLabel("Ch:"));
            slotAo.Extras.Controls.Add(cmbAoCh);
            slotAo.Extras.Controls.Add(FlowLabel("Default:"));
            slotAo.Extras.Controls.Add(numAoDef);

            // ---- Export group ----------------------------------------------------
            DarkGroupBox grpOut = new DarkGroupBox();
            grpOut.Text = "Export";
            grpOut.SetBounds(8, 424, 968, 128);
            Controls.Add(grpOut);

            Label lblDir = new Label();
            lblDir.Text = "Output folder:";
            lblDir.SetBounds(12, 27, 84, 16);
            grpOut.Controls.Add(lblDir);

            txtOutDir = new TextBox();
            txtOutDir.SetBounds(100, 23, 688, 23);
            grpOut.Controls.Add(txtOutDir);

            Button btnOutBrowse = new Button();
            btnOutBrowse.Text = "Browse…";
            btnOutBrowse.SetBounds(794, 22, 76, 25);
            btnOutBrowse.Click += OnOutBrowseClick;
            grpOut.Controls.Add(btnOutBrowse);

            Button btnOpenOut = new Button();
            btnOpenOut.Text = "Open Folder";
            btnOpenOut.SetBounds(874, 22, 86, 25);
            btnOpenOut.Click += OnOpenOutClick;
            grpOut.Controls.Add(btnOpenOut);

            Label lblBase = new Label();
            lblBase.Text = "Base name:";
            lblBase.SetBounds(12, 59, 84, 16);
            grpOut.Controls.Add(lblBase);

            txtBaseName = new TextBox();
            txtBaseName.SetBounds(100, 55, 240, 23);
            grpOut.Controls.Add(txtBaseName);

            Label lblSize = new Label();
            lblSize.Text = "Max size:";
            lblSize.SetBounds(360, 59, 60, 16);
            grpOut.Controls.Add(lblSize);

            cmbMaxSize = new ComboBox();
            cmbMaxSize.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbMaxSize.Items.AddRange(new object[] { "Auto", "8192", "4096", "2048", "1024", "512", "256" });
            cmbMaxSize.SelectedIndex = 0;
            cmbMaxSize.SetBounds(424, 55, 90, 23);
            grpOut.Controls.Add(cmbMaxSize);

            Button btnBcr = new Button();
            btnBcr.Text = "Export _BCR";
            btnBcr.SetBounds(100, 90, 110, 30);
            btnBcr.Click += delegate { RunExport(ExportBcr); };
            grpOut.Controls.Add(btnBcr);

            Button btnNmo = new Button();
            btnNmo.Text = "Export _NMO";
            btnNmo.SetBounds(216, 90, 110, 30);
            btnNmo.Click += delegate { RunExport(ExportNmo); };
            grpOut.Controls.Add(btnNmo);

            Button btnBca = new Button();
            btnBca.Text = "Export _BCA";
            btnBca.SetBounds(332, 90, 110, 30);
            btnBca.Click += delegate { RunExport(ExportBca); };
            grpOut.Controls.Add(btnBca);

            _btnAll = new Button();
            _btnAll.Text = "Export All";
            _btnAll.SetBounds(460, 90, 130, 30);
            _btnAll.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _btnAll.Click += delegate { RunExport(ExportAll); };
            grpOut.Controls.Add(_btnAll);

            Button btnMaskGen = new Button();
            btnMaskGen.Text = "Mask / VFX…";
            btnMaskGen.SetBounds(620, 90, 130, 30);
            btnMaskGen.Click += OnMaskGenClick;
            grpOut.Controls.Add(btnMaskGen);

            txtStatus = new TextBox();
            txtStatus.SetBounds(8, 558, 968, 58);
            txtStatus.Multiline = true;
            txtStatus.ReadOnly = true;
            txtStatus.ScrollBars = ScrollBars.Vertical;
            txtStatus.Text = "Ready. Load a PBR set (Auto-Fill or drag & drop), then Export. Output = 8-bit RGBA TIFF with LZW compression.";
            Controls.Add(txtStatus);

            slotBase.SlotChanged += OnPrimarySlotChanged;
            slotNormal.SlotChanged += OnPrimarySlotChanged;

            WireDnd(this);
            WireDnd(_header);
            WireDnd(grpBcr);
            WireDnd(grpNmo);
            WireDnd(grpOut);
            WireDnd(_hint);

            ReapplyTheme();
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            bool dark = _cmbTheme.SelectedIndex == 0;
            if (dark == Theme.Dark)
                return;
            Theme.SetMode(dark);
            Settings.SaveDark(dark);
            ReapplyTheme();
        }

        private void ReapplyTheme()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            _header.BackColor = Theme.HeaderBg;
            Theme.Apply(this);
            _lblAppTitle.ForeColor = Theme.Text;
            _lblAppSub.ForeColor = Theme.SubText;
            _lblTheme.ForeColor = Theme.SubText;
            _lblCredit.ForeColor = Theme.SubText;
            _hint.ForeColor = Theme.SubText;
            _btnAll.BackColor = Theme.Accent;
            _btnAll.ForeColor = Color.White;
            _btnAll.FlatAppearance.BorderColor = Theme.Accent;
            _btnAll.FlatAppearance.MouseOverBackColor = ControlPaint.Light(Theme.Accent);
            _btnAll.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(Theme.Accent);
            slotBase.ApplyTheme();
            slotRough.ApplyTheme();
            slotOpacity.ApplyTheme();
            slotNormal.ApplyTheme();
            slotMetal.ApplyTheme();
            slotAo.ApplyTheme();
            ApplyTitleBarTheme();
            Invalidate(true);
        }

        private void ApplyTitleBarTheme()
        {
            if (!IsHandleCreated)
                return;
            int v = Theme.Dark ? 1 : 0;
            try { DwmSetWindowAttribute(Handle, 20, ref v, 4); }
            catch (Exception) { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBarTheme();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(_startFile) && File.Exists(_startFile))
                AutoFillFrom(_startFile);
        }

        // ---- small control factories --------------------------------------------
        private Label FlowLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 9, 2, 0);
            return l;
        }

        private ComboBox ChannelCombo()
        {
            ComboBox c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.Items.AddRange(new object[] { "R", "G", "B", "A", "Luma" });
            c.SelectedIndex = 0;
            c.Width = 56;
            c.Margin = new Padding(0, 5, 8, 0);
            return c;
        }

        private NumericUpDown DefaultNum(decimal v)
        {
            NumericUpDown n = new NumericUpDown();
            n.DecimalPlaces = 2;
            n.Increment = 0.05m;
            n.Minimum = 0;
            n.Maximum = 1;
            n.Value = v;
            n.Width = 52;
            n.Margin = new Padding(0, 5, 0, 0);
            return n;
        }

        private CheckBox FlowCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 7, 8, 0);
            return c;
        }

        // ---- drag & drop = auto-fill --------------------------------------------
        private void WireDnd(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            c.DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                    AutoFillFrom(files[0]);
            };
        }

        private void OnAutoFillClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Images|*.png;*.tif;*.tiff;*.tga;*.jpg;*.jpeg;*.bmp|All files|*.*";
                dlg.Title = "Pick any texture of the set";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    AutoFillFrom(dlg.FileName);
            }
        }

        private void AutoFillFrom(string path)
        {
            TextureSetResult res;
            try { res = TextureSetMatcher.Match(path); }
            catch (Exception ex) { txtStatus.Text = "Auto-fill failed: " + ex.Message; return; }

            if (res.Matched == 0)
            {
                txtStatus.Text = "No PBR maps recognized next to '" + Path.GetFileName(path) + "'. Load the slots manually instead.";
                return;
            }

            List<string> log = new List<string>();
            if (res.BaseColor != null)
            {
                slotBase.LoadImage(res.BaseColor);
                log.Add("BaseColor: " + Path.GetFileName(res.BaseColor));
            }
            if (res.Roughness != null)
            {
                slotRough.LoadImage(res.Roughness);
                SelectItem(cmbRoughCh, res.RoughnessChannel);
                chkRoughInvert.Checked = res.RoughnessIsGloss;
                log.Add("Rough: " + Path.GetFileName(res.Roughness) + " [" + res.RoughnessChannel + (res.RoughnessIsGloss ? ", gloss->invert" : "") + "]");
            }
            if (res.Opacity != null)
            {
                slotOpacity.LoadImage(res.Opacity);
                log.Add("Opacity: " + Path.GetFileName(res.Opacity));
            }
            if (res.Normal != null)
            {
                slotNormal.LoadImage(res.Normal);
                chkFlipGreen.Checked = res.NormalIsOpenGL;
                log.Add("Normal: " + Path.GetFileName(res.Normal) + (res.NormalIsOpenGL ? " [GL->flip G]" : ""));
            }
            if (res.Metalness != null)
            {
                slotMetal.LoadImage(res.Metalness);
                SelectItem(cmbMetalCh, res.MetalnessChannel);
                log.Add("Metal: " + Path.GetFileName(res.Metalness) + " [" + res.MetalnessChannel + "]");
            }
            if (res.Ao != null)
            {
                slotAo.LoadImage(res.Ao);
                SelectItem(cmbAoCh, res.AoChannel);
                log.Add("AO: " + Path.GetFileName(res.Ao) + " [" + res.AoChannel + "]");
            }

            txtBaseName.Text = TrimSeps(res.BaseName);
            txtOutDir.Text = res.Folder;
            txtStatus.Text = "Auto-filled " + log.Count + " map(s):  " + string.Join("   |   ", log.ToArray());
        }

        private void OnPrimarySlotChanged(object sender, EventArgs e)
        {
            TextureSlot slot = (TextureSlot)sender;
            if (!slot.HasImage)
                return;
            if (txtOutDir.Text.Trim().Length == 0)
                txtOutDir.Text = Path.GetDirectoryName(slot.ImagePath);
            if (txtBaseName.Text.Trim().Length == 0)
                txtBaseName.Text = TrimSeps(TextureSetMatcher.DeriveBaseName(slot.ImagePath));
        }

        private static string TrimSeps(string s)
        {
            return s.TrimEnd('_', '-', ' ', '.');
        }

        private static void SelectItem(ComboBox cmb, string value)
        {
            int i = cmb.Items.IndexOf(value);
            if (i >= 0)
                cmb.SelectedIndex = i;
        }

        private void OnMaskGenClick(object sender, EventArgs e)
        {
            if (!slotNormal.HasImage)
            {
                txtStatus.Text = "Load a Normal map first - the Mask / VFX generator derives its masks from it.";
                return;
            }
            MaskGenContext ctx = new MaskGenContext();
            ctx.NormalPath = slotNormal.ImagePath;
            ctx.FlipGreen = chkFlipGreen.Checked;
            ctx.BasePath = slotBase.HasImage ? slotBase.ImagePath : null;
            ctx.RoughPath = slotRough.HasImage ? slotRough.ImagePath : null;
            ctx.RoughChannel = (string)cmbRoughCh.SelectedItem;
            ctx.RoughInvert = chkRoughInvert.Checked;
            ctx.OutDir = txtOutDir.Text.Trim();
            string bn = TrimSeps(txtBaseName.Text.Trim());
            ctx.BaseName = bn.Length > 0 ? bn : "Texture";
            using (MaskVfxDialog dlg = new MaskVfxDialog(ctx))
                dlg.ShowDialog(this);
        }

        private void OnOutBrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select output folder";
                if (Directory.Exists(txtOutDir.Text.Trim()))
                    dlg.SelectedPath = txtOutDir.Text.Trim();
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtOutDir.Text = dlg.SelectedPath;
            }
        }

        private void OnOpenOutClick(object sender, EventArgs e)
        {
            string dir = txtOutDir.Text.Trim();
            if (Directory.Exists(dir))
                Process.Start("explorer.exe", "\"" + dir + "\"");
            else
                txtStatus.Text = "Output folder does not exist yet.";
        }

        // ---- export --------------------------------------------------------------
        private void RunExport(Action<List<string>> action)
        {
            List<string> log = new List<string>();
            Cursor = Cursors.WaitCursor;
            try { action(log); }
            catch (Exception ex) { log.Add("ERROR: " + ex.Message); }
            finally { Cursor = Cursors.Default; }
            if (log.Count == 0)
                log.Add("Nothing to export - load maps first.");
            txtStatus.Text = string.Join(Environment.NewLine, log.ToArray());
        }

        private void ExportAll(List<string> log)
        {
            ExportBcr(log);
            ExportNmo(log);
            if (slotOpacity.HasImage)
                ExportBca(log);
        }

        private void ExportBcr(List<string> log)
        {
            if (!slotBase.HasImage)
            {
                log.Add("_BCR skipped - no Base Color loaded.");
                return;
            }
            Size target = GetTargetSize(slotBase.ImageSize);
            Bitmap bc = Packer.EnsureSize(Packer.LoadBitmap(slotBase.ImagePath), target.Width, target.Height);
            byte[] r = Packer.ExtractChannel(bc, "R");
            byte[] g = Packer.ExtractChannel(bc, "G");
            byte[] b = Packer.ExtractChannel(bc, "B");
            bc.Dispose();

            byte[] rough = GetChannelData(slotRough, cmbRoughCh, target);
            if (rough != null && chkRoughInvert.Checked)
                Packer.Invert(rough);

            using (Bitmap outBmp = Packer.Compose(target.Width, target.Height, r, 0, g, 0, b, 0, rough, ToByte(numRoughDef.Value)))
                log.Add(SaveOut(outBmp, "_BCR"));
            if (rough == null)
                log.Add(string.Format("    (no roughness map - used flat {0:0.00})", numRoughDef.Value));
        }

        private void ExportNmo(List<string> log)
        {
            if (!slotNormal.HasImage)
            {
                log.Add("_NMO skipped - no Normal map loaded.");
                return;
            }
            Size target = GetTargetSize(slotNormal.ImageSize);
            Bitmap nm = Packer.EnsureSize(Packer.LoadBitmap(slotNormal.ImagePath), target.Width, target.Height);
            byte[] r = Packer.ExtractChannel(nm, "R");
            byte[] g = Packer.ExtractChannel(nm, "G");
            nm.Dispose();
            if (chkFlipGreen.Checked)
                Packer.Invert(g);

            byte[] metal = GetChannelData(slotMetal, cmbMetalCh, target);
            byte[] ao = GetChannelData(slotAo, cmbAoCh, target);

            using (Bitmap outBmp = Packer.Compose(target.Width, target.Height, r, 0, g, 0, metal, ToByte(numMetalDef.Value), ao, ToByte(numAoDef.Value)))
                log.Add(SaveOut(outBmp, "_NMO"));
            if (metal == null)
                log.Add(string.Format("    (no metalness map - used flat {0:0.00})", numMetalDef.Value));
            if (ao == null)
                log.Add(string.Format("    (no AO map - used flat {0:0.00})", numAoDef.Value));
        }

        private void ExportBca(List<string> log)
        {
            if (!slotBase.HasImage)
            {
                log.Add("_BCA skipped - no Base Color loaded.");
                return;
            }
            if (!slotOpacity.HasImage)
            {
                log.Add("_BCA skipped - no Opacity map loaded.");
                return;
            }
            Size target = GetTargetSize(slotBase.ImageSize);
            Bitmap bc = Packer.EnsureSize(Packer.LoadBitmap(slotBase.ImagePath), target.Width, target.Height);
            byte[] r = Packer.ExtractChannel(bc, "R");
            byte[] g = Packer.ExtractChannel(bc, "G");
            byte[] b = Packer.ExtractChannel(bc, "B");
            bc.Dispose();
            byte[] a = GetChannelData(slotOpacity, cmbOpacityCh, target);

            using (Bitmap outBmp = Packer.Compose(target.Width, target.Height, r, 0, g, 0, b, 0, a, 255))
                log.Add(SaveOut(outBmp, "_BCA"));
        }

        private byte[] GetChannelData(TextureSlot slot, ComboBox cmb, Size target)
        {
            if (!slot.HasImage)
                return null;
            Bitmap bmp = Packer.EnsureSize(Packer.LoadBitmap(slot.ImagePath), target.Width, target.Height);
            byte[] v = Packer.ExtractChannel(bmp, (string)cmb.SelectedItem);
            bmp.Dispose();
            return v;
        }

        private Size GetTargetSize(Size primary)
        {
            string sel = (string)cmbMaxSize.SelectedItem;
            if (sel == "Auto")
                return primary;
            int cap = int.Parse(sel);
            int max = Math.Max(primary.Width, primary.Height);
            if (max <= cap)
                return primary;
            double s = (double)cap / max;
            return new Size(Math.Max(1, (int)Math.Round(primary.Width * s)),
                            Math.Max(1, (int)Math.Round(primary.Height * s)));
        }

        private string SaveOut(Bitmap bmp, string suffix)
        {
            string dir = txtOutDir.Text.Trim();
            if (dir.Length == 0)
                throw new Exception("Set an output folder first.");
            Directory.CreateDirectory(dir);
            string bn = TrimSeps(txtBaseName.Text.Trim());
            if (bn.Length == 0)
                bn = "Texture";
            string path = Path.Combine(dir, bn + suffix + ".tif");
            Packer.SaveTiffLzw(bmp, path);
            string warn = (!IsPot(bmp.Width) || !IsPot(bmp.Height)) ? "   [warning: not power-of-two]" : "";
            return string.Format("Saved {0}   ({1}x{2}, RGBA8, LZW){3}", path, bmp.Width, bmp.Height, warn);
        }

        private static byte ToByte(decimal v)
        {
            return (byte)Math.Round(v * 255m);
        }

        private static bool IsPot(int n)
        {
            return n > 0 && (n & (n - 1)) == 0;
        }
    }
}
