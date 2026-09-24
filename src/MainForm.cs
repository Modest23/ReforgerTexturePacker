using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        private ModelPreview _model3d;
        private ComboBox _cmbShow;
        private Label _lblModel;
        private Button _btnModel;
        private ModelMesh _mesh;
        private string _modelPath;
        private bool _modelLoading;
        private Timer _texTimer = new Timer();
        private ListBox _lstSets;
        private Button _btnMatch, _btnExportSets;
        private List<MaterialSet> _sets;
        private MaterialSet _cur;
        private bool _switching;
        private int _mapLoads;
        private Dictionary<string, int> _setJobs = new Dictionary<string, int>();
        private ToolTip _tip3d = new ToolTip();
        private Form _fsForm;
        private SplitContainer _splitMain, _splitLeft, _splitPrev;
        private ComboBox _cmbSetsSize;
        private string _projectPath;
        private string[] _pendingProject;
        private Control _fsPrevParent;
        private Rectangle _fsPrevBounds;
        private DockStyle _fsPrevDock;

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
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
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

            Button btnOpenProj = new Button();
            btnOpenProj.Text = "Open project\u2026";
            btnOpenProj.SetBounds(590, 9, 104, 26);
            btnOpenProj.Click += delegate { OnOpenProjectClick(); };
            _header.Controls.Add(btnOpenProj);
            Button btnSaveProj = new Button();
            btnSaveProj.Text = "Save project";
            btnSaveProj.SetBounds(698, 9, 96, 26);
            btnSaveProj.Click += delegate { SaveProject(false); };
            _header.Controls.Add(btnSaveProj);
            ToolTip ttp = new ToolTip();
            ttp.SetToolTip(btnOpenProj, "Open a saved .rtp project (Ctrl+O). You can also drop a .rtp on the window.");
            ttp.SetToolTip(btnSaveProj, "Save everything - model, every texture set, channels, export size - to a .rtp file (Ctrl+S, Ctrl+Shift+S = save as).");

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
            cmbMaxSize.SelectedItem = Prefs.Get("maxsize", "2048");
            if (cmbMaxSize.SelectedIndex < 0) cmbMaxSize.SelectedIndex = 0;
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

            // ---- 3D preview ------------------------------------------------------
            const int extra = 416;
            ClientSize = new Size(984 + extra, 624);
            _header.Width = 984 + extra;
            DarkGroupBox grpPrev = new DarkGroupBox();
            grpPrev.Text = "3D preview";
            grpPrev.SetBounds(984, 86, 408, 530);
            // resizing / maximizing the window gives the extra room to the 3D preview
            grpPrev.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(grpPrev);
            // texture-set list above, 3D view below - drag the bar between them
            _splitPrev = new SplitContainer();
            _splitPrev.Orientation = Orientation.Horizontal;
            _splitPrev.SetBounds(8, 20, 392, 414);
            _splitPrev.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _splitPrev.SplitterWidth = 6;
            _splitPrev.FixedPanel = FixedPanel.Panel1; // the list keeps its height, the 3D view takes any growth
            grpPrev.Controls.Add(_splitPrev);
            _splitPrev.Panel1MinSize = 40;
            _splitPrev.Panel2MinSize = 100;
            _splitPrev.SplitterDistance = 104;
            Label lsets = new Label();
            lsets.Text = "Texture sets on this model - click one to edit it:";
            lsets.Dock = DockStyle.Top;
            lsets.Height = 18;
            _lstSets = new ListBox();
            _lstSets.Dock = DockStyle.Fill;
            _lstSets.IntegralHeight = false;
            _lstSets.BorderStyle = BorderStyle.FixedSingle;
            _lstSets.SelectedIndexChanged += delegate { OnSetSelected(); };
            _splitPrev.Panel1.Controls.Add(_lstSets);   // Fill first, then the docked label
            _splitPrev.Panel1.Controls.Add(lsets);
            _model3d = new ModelPreview();
            _model3d.Dock = DockStyle.Fill;
            _splitPrev.Panel2.Controls.Add(_model3d);
            _tip3d.SetToolTip(_model3d, "Drag = rotate   right-drag = pan   wheel = zoom   double-click = reset");
            _btnModel = new Button();
            _btnModel.Text = "Load model\u2026";
            _btnModel.SetBounds(8, 440, 96, 28);
            _btnModel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnModel.Click += OnLoadModelClick;
            grpPrev.Controls.Add(_btnModel);
            _btnMatch = new Button();
            _btnMatch.Text = "Match textures";
            _btnMatch.SetBounds(108, 440, 104, 28);
            _btnMatch.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnMatch.Click += OnMatchClick;
            grpPrev.Controls.Add(_btnMatch);
            _tip3d.SetToolTip(_btnMatch, "Fills every set that has no textures from the texture folder - by material name and UDIM tile.");
            _btnExportSets = new Button();
            _btnExportSets.Text = "Export all sets";
            _btnExportSets.SetBounds(216, 440, 116, 28);
            _btnExportSets.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _btnExportSets.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _btnExportSets.Enabled = false;
            _btnExportSets.Click += OnExportSetsClick;
            grpPrev.Controls.Add(_btnExportSets);
            _tip3d.SetToolTip(_btnExportSets, "Exports _BCR / _NMO (/ _BCA) for every texture set that has textures, each to its own output folder + base name, at the size on the right.");
            _cmbSetsSize = new ComboBox();
            _cmbSetsSize.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (object o in cmbMaxSize.Items) _cmbSetsSize.Items.Add(o);
            _cmbSetsSize.SelectedItem = cmbMaxSize.SelectedItem;
            _cmbSetsSize.SetBounds(336, 443, 64, 23);
            _cmbSetsSize.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            grpPrev.Controls.Add(_cmbSetsSize);
            _tip3d.SetToolTip(_cmbSetsSize, "Max export size (same as Max size in the Export box). Auto = keep the source size.\n" +
                "The 3D preview shows the textures at this size too - switch between Auto and 2048 to compare.");
            // one setting, two places: keep both boxes in step and remember it
            _cmbSetsSize.SelectedIndexChanged += delegate
            {
                if (!Equals(cmbMaxSize.SelectedItem, _cmbSetsSize.SelectedItem)) cmbMaxSize.SelectedItem = _cmbSetsSize.SelectedItem;
            };
            cmbMaxSize.SelectedIndexChanged += delegate
            {
                if (!Equals(_cmbSetsSize.SelectedItem, cmbMaxSize.SelectedItem)) _cmbSetsSize.SelectedItem = cmbMaxSize.SelectedItem;
                Prefs.Set("maxsize", (string)cmbMaxSize.SelectedItem);
                // the preview follows the export size, so flipping it compares 4K vs 2K on the model
                if (_mesh != null && _sets != null)
                {
                    LoadAllSetMaps();
                    txtStatus.Text = "3D preview now shows the textures at " + ((string)cmbMaxSize.SelectedItem == "Auto" ? "their full source size" : cmbMaxSize.SelectedItem + " px") +
                        " - exactly what Export writes. Zoom in (wheel) or go full screen (F11) to judge the detail.";
                }
            };
            Label lshow = new Label();
            lshow.Text = "Show:";
            lshow.SetBounds(8, 480, 40, 16);
            lshow.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            grpPrev.Controls.Add(lshow);
            _cmbShow = new ComboBox();
            _cmbShow.DropDownStyle = ComboBoxStyle.DropDownList;
            // order matches ModelPreview.Mode* constants
            _cmbShow.Items.AddRange(new object[] { "Full material (PBR)", "Base color only", "Roughness", "Metalness", "Ambient occlusion", "Normal map", "Clay + normal detail" });
            _cmbShow.SelectedIndex = 0;
            _cmbShow.SetBounds(50, 476, 174, 23);
            _cmbShow.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cmbShow.SelectedIndexChanged += delegate { _model3d.SetMode(_cmbShow.SelectedIndex); };
            grpPrev.Controls.Add(_cmbShow);
            Button btnFull = new Button();
            btnFull.Text = "Full screen  (F11)";
            btnFull.SetBounds(232, 474, 168, 27);
            btnFull.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnFull.Click += delegate { ToggleFullscreen(); };
            grpPrev.Controls.Add(btnFull);
            _lblModel = new Label();
            _lblModel.SetBounds(8, 506, 392, 16);
            _lblModel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _lblModel.AutoEllipsis = true;
            _lblModel.Text = "Load the .fbx / .blend / .obj these textures are for (uses Blender).";
            grpPrev.Controls.Add(_lblModel);
            _header.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            txtStatus.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
            _model3d.MapsLost += delegate { LoadAllSetMaps(); };
            _texTimer.Interval = 150;
            _texTimer.Tick += delegate { _texTimer.Stop(); RefreshPreviewTexture(); };

            slotBase.SlotChanged += OnPrimarySlotChanged;
            slotNormal.SlotChanged += OnPrimarySlotChanged;
            foreach (TextureSlot sl in new TextureSlot[] { slotBase, slotRough, slotNormal, slotMetal, slotAo })
                sl.SlotChanged += delegate { OnTexturesChanged(); };
            slotOpacity.SlotChanged += delegate { OnSetEdited(); };
            foreach (ComboBox cb in new ComboBox[] { cmbRoughCh, cmbMetalCh, cmbAoCh, cmbOpacityCh })
                cb.SelectedIndexChanged += delegate { OnSetEdited(); };
            chkRoughInvert.CheckedChanged += delegate { OnSetEdited(); };
            chkFlipGreen.CheckedChanged += delegate { OnSetEdited(); };
            foreach (NumericUpDown nd in new NumericUpDown[] { numRoughDef, numMetalDef, numAoDef })
                nd.ValueChanged += delegate { OnSetEdited(); };

            _splitMain = new SplitContainer();
            _splitMain.Orientation = Orientation.Vertical;
            _splitMain.SetBounds(0, 44, ClientSize.Width, ClientSize.Height - 44);
            _splitMain.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _splitMain.SplitterWidth = 6;
            _splitMain.FixedPanel = FixedPanel.Panel1; // a bigger window grows the preview side
            Controls.Add(_splitMain);
            _splitMain.Panel1MinSize = 480;
            _splitMain.Panel2MinSize = 300;
            _splitMain.SplitterDistance = 980;
            _splitLeft = new SplitContainer();
            _splitLeft.Orientation = Orientation.Horizontal;
            _splitLeft.Dock = DockStyle.Fill;
            _splitLeft.SplitterWidth = 6;
            _splitLeft.FixedPanel = FixedPanel.Panel1;
            _splitMain.Panel1.Controls.Add(_splitLeft);
            _splitLeft.Panel1MinSize = 120;
            _splitLeft.Panel2MinSize = 36;
            _splitLeft.SplitterDistance = 508;
            _splitLeft.Panel1.AutoScroll = true;
            foreach (Control c in new Control[] { btnAutoFill, _hint, grpBcr, grpNmo, grpOut })
                MoveInto(c, _splitLeft.Panel1, 0, -44);
            Controls.Remove(txtStatus);
            txtStatus.Anchor = AnchorStyles.None;
            txtStatus.Dock = DockStyle.Fill;
            _splitLeft.Panel2.Padding = new Padding(8, 0, 0, 8);
            _splitLeft.Panel2.Controls.Add(txtStatus);
            Controls.Remove(grpPrev);
            grpPrev.Anchor = AnchorStyles.None;
            grpPrev.Dock = DockStyle.Fill;
            _splitMain.Panel2.Padding = new Padding(0, 42, 8, 8);
            _splitMain.Panel2.Controls.Add(grpPrev);
            RestoreSplits();

            WireDnd(this);
            WireDnd(_splitLeft.Panel1);
            WireDnd(_header);
            WireDnd(grpBcr);
            WireDnd(grpNmo);
            WireDnd(grpOut);
            WireDnd(_hint);

            ReapplyTheme();
            MinimumSize = Size; // can grow (or maximize), never shrink below the designed layout
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
            if (_model3d != null)
            {
                _model3d.ApplyTheme();
                _lblModel.ForeColor = Theme.SubText;
                _lstSets.BackColor = Theme.Field;
                foreach (SplitContainer sc in new SplitContainer[] { _splitMain, _splitLeft, _splitPrev })
                {
                    if (sc == null) continue;
                    sc.BackColor = Theme.Border;          // the draggable bar
                    sc.Panel1.BackColor = Theme.Bg;
                    sc.Panel2.BackColor = Theme.Bg;
                }
                _lstSets.ForeColor = Theme.Text;
                _btnExportSets.BackColor = Theme.Accent;
                _btnExportSets.ForeColor = Color.White;
                _btnExportSets.FlatAppearance.BorderColor = Theme.Accent;
            }
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

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S)) { SaveProject(false); return true; }
            if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { SaveProject(true); return true; }
            if (keyData == (Keys.Control | Keys.O)) { OnOpenProjectClick(); return true; }
            if (keyData == Keys.F12) { SaveScreenshot(); return true; }
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // Moves the 3D view into a borderless window covering this monitor; Esc / F11 / the button put it back.
        private void ToggleFullscreen()
        {
            if (_fsForm != null)
            {
                _fsForm.Close();
                return;
            }
            Form f = new Form();
            f.Text = "3D preview";
            f.FormBorderStyle = FormBorderStyle.None;
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = Screen.FromControl(this).Bounds;
            f.BackColor = Theme.ThumbBg;
            f.ForeColor = Theme.Text;
            f.Font = Font;
            f.KeyPreview = true;
            f.ShowInTaskbar = false;
            f.Icon = Icon;

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = 38;
            bar.BackColor = Theme.HeaderBg;
            Label ls = new Label();
            ls.Text = "Show:";
            ls.SetBounds(12, 11, 40, 16);
            bar.Controls.Add(ls);
            ComboBox show = new ComboBox();
            show.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (object o in _cmbShow.Items) show.Items.Add(o);
            show.SelectedIndex = _cmbShow.SelectedIndex;
            show.SetBounds(54, 7, 190, 23);
            show.SelectedIndexChanged += delegate { _cmbShow.SelectedIndex = show.SelectedIndex; };
            bar.Controls.Add(show);
            Label info = new Label();
            info.Text = "Drag = rotate    right-drag = pan    wheel = zoom    double-click = reset view    Esc / F11 = exit";
            info.ForeColor = Theme.SubText;
            info.SetBounds(262, 11, 700, 16);
            bar.Controls.Add(info);
            Button shot = new Button();
            shot.Text = "Save image\u2026  (F12)";
            shot.SetBounds(f.Width - 300, 5, 144, 28);
            shot.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            shot.Click += delegate { SaveScreenshot(); };
            bar.Controls.Add(shot);
            Button exit = new Button();
            exit.Text = "Exit full screen";
            exit.SetBounds(f.Width - 150, 5, 138, 28);
            exit.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            exit.Click += delegate { f.Close(); };
            bar.Controls.Add(exit);
            Theme.Apply(bar);

            _fsPrevParent = _model3d.Parent;
            _fsPrevBounds = _model3d.Bounds;
            _fsPrevDock = _model3d.Dock;
            _fsPrevParent.Controls.Remove(_model3d);
            _model3d.Dock = DockStyle.Fill;
            f.Controls.Add(_model3d);   // the Fill control goes in first, the docked bar after it
            f.Controls.Add(bar);
            f.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F12)
                {
                    e.Handled = true;
                    SaveScreenshot();
                }
                else if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.F11)
                {
                    e.Handled = true;
                    f.Close();
                }
            };
            f.FormClosed += delegate
            {
                f.Controls.Remove(_model3d);
                _fsPrevParent.Controls.Add(_model3d);
                _model3d.Dock = _fsPrevDock;
                if (_fsPrevDock == DockStyle.None) _model3d.Bounds = _fsPrevBounds;
                _model3d.Invalidate();
                _fsForm = null;
                Activate();
            };
            _fsForm = f;
            f.Show(this);
            _model3d.Focus();
        }

        private static void MoveInto(Control c, Control parent, int dx, int dy)
        {
            Rectangle b = c.Bounds;
            b.Offset(dx, dy);
            c.Parent.Controls.Remove(c);
            parent.Controls.Add(c);
            c.Bounds = b;
        }

        private void RestoreSplits()
        {
            int v;
            try
            {
                if (int.TryParse(Prefs.Get("split.main", ""), out v)) _splitMain.SplitterDistance = v;
                if (int.TryParse(Prefs.Get("split.left", ""), out v)) _splitLeft.SplitterDistance = v;
                if (int.TryParse(Prefs.Get("split.prev", ""), out v)) _splitPrev.SplitterDistance = v;
            }
            catch (Exception) { } // out of range for the current window size - keep the defaults
        }

        private void SaveSplits()
        {
            if (WindowState == FormWindowState.Minimized) return;
            Prefs.Set("split.main", _splitMain.SplitterDistance.ToString());
            Prefs.Set("split.left", _splitLeft.SplitterDistance.ToString());
            Prefs.Set("split.prev", _splitPrev.SplitterDistance.ToString());
        }

        // Saves the 3D view as a PNG, rendered at twice the on-screen size.
        private void SaveScreenshot()
        {
            if (_mesh == null)
            {
                txtStatus.Text = "Load a model first - Save image captures the 3D preview.";
                return;
            }
            Bitmap img = _model3d.RenderImage(Math.Max(64, _model3d.Width * 2), Math.Max(64, _model3d.Height * 2));
            if (img == null)
            {
                txtStatus.Text = "Save image isn't supported by this graphics driver.";
                return;
            }
            using (img)
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "Save 3D preview image";
                dlg.Filter = "PNG image (*.png)|*.png";
                dlg.DefaultExt = "png";
                if (_modelPath != null)
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(_modelPath);
                    dlg.FileName = Path.GetFileNameWithoutExtension(_modelPath) + "_preview.png";
                }
                if (dlg.ShowDialog(_fsForm != null ? (IWin32Window)_fsForm : this) != DialogResult.OK)
                    return;
                img.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                txtStatus.Text = string.Format("Saved {0}  ({1}x{2})", dlg.FileName, img.Width, img.Height);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSplits();
            if (_cur != null) SaveUiToSet(_cur);
            SaveSets();
            base.OnFormClosing(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(_startFile) && File.Exists(_startFile))
            {
                if (Path.GetExtension(_startFile).Equals(".rtp", StringComparison.OrdinalIgnoreCase)) OpenProject(_startFile);
                else AutoFillFrom(_startFile);
            }
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
                {
                    if (Path.GetExtension(files[0]).Equals(".rtp", StringComparison.OrdinalIgnoreCase)) OpenProject(files[0]);
                    else AutoFillFrom(files[0]);
                }
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
            ctx.MetalPath = slotMetal.HasImage ? slotMetal.ImagePath : null;
            ctx.MetalChannel = (string)cmbMetalCh.SelectedItem;
            ctx.AoPath = slotAo.HasImage ? slotAo.ImagePath : null;
            ctx.AoChannel = (string)cmbAoCh.SelectedItem;
            ctx.OutDir = txtOutDir.Text.Trim();
            ctx.ModelPath = _modelPath;
            string bn = TrimSeps(txtBaseName.Text.Trim());
            ctx.BaseName = bn.Length > 0 ? bn : "Texture";
            using (MaskVfxDialog dlg = new MaskVfxDialog(ctx))
                dlg.ShowDialog(this);
        }

        // ---- 3D preview + per-material texture sets -----------------------------------------

        private string PreviewTexturePath()
        {
            if (slotBase.HasImage) return slotBase.ImagePath;
            if (slotNormal.HasImage) return slotNormal.ImagePath;
            return null;
        }

        private void OnTexturesChanged()
        {
            if (_switching)
                return;
            // first texture of a set: bring back the model used with this folder last time
            string tex = PreviewTexturePath();
            if (_mesh == null && !_modelLoading && tex != null)
            {
                string remembered = BlenderBaker.RememberedModel(Path.GetDirectoryName(tex));
                if (remembered != null)
                    LoadModel(remembered);
            }
            OnSetEdited();
        }

        // Any edit in the slots: keep it in the current set and refresh that set on the model.
        private void OnSetEdited()
        {
            if (_switching)
                return;
            CheckChannels();
            if (_cur == null)
                return;
            SaveUiToSet(_cur);
            RefreshSetRow(_cur);
            QueuePreviewTexture();
        }

        // The classic ORM mistake: two of AO / rough / metal reading the same channel of the same file.
        private void CheckChannels()
        {
            string[] names = { "AO", "Roughness", "Metalness" };
            TextureSlot[] slots = { slotAo, slotRough, slotMetal };
            ComboBox[] chs = { cmbAoCh, cmbRoughCh, cmbMetalCh };
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    if (slots[i].HasImage && slots[j].HasImage && string.Equals(slots[i].ImagePath, slots[j].ImagePath, StringComparison.OrdinalIgnoreCase)
                        && (string)chs[i].SelectedItem == (string)chs[j].SelectedItem)
                    {
                        txtStatus.Text = string.Format("Check the channels: {0} and {1} both read channel {2} of the same file. " +
                            "For an ORM / packed map use AO = R, Roughness = G, Metalness = B.", names[i], names[j], chs[i].SelectedItem);
                        return;
                    }
        }

        private void SaveUiToSet(MaterialSet s)
        {
            if (s == null) return;
            s.Base = slotBase.HasImage ? slotBase.ImagePath : "";
            s.Rough = slotRough.HasImage ? slotRough.ImagePath : "";
            s.Opacity = slotOpacity.HasImage ? slotOpacity.ImagePath : "";
            s.Normal = slotNormal.HasImage ? slotNormal.ImagePath : "";
            s.Metal = slotMetal.HasImage ? slotMetal.ImagePath : "";
            s.Ao = slotAo.HasImage ? slotAo.ImagePath : "";
            s.RoughCh = (string)cmbRoughCh.SelectedItem;
            s.MetalCh = (string)cmbMetalCh.SelectedItem;
            s.AoCh = (string)cmbAoCh.SelectedItem;
            s.OpacityCh = (string)cmbOpacityCh.SelectedItem;
            s.RoughInvert = chkRoughInvert.Checked;
            s.FlipGreen = chkFlipGreen.Checked;
            s.RoughDef = (double)numRoughDef.Value;
            s.MetalDef = (double)numMetalDef.Value;
            s.AoDef = (double)numAoDef.Value;
            s.OutDir = txtOutDir.Text.Trim();
            s.BaseName = txtBaseName.Text.Trim();
        }

        private void LoadSetToUi(MaterialSet s)
        {
            _switching = true;
            try
            {
                SetSlot(slotBase, s.Base);
                SetSlot(slotRough, s.Rough);
                SetSlot(slotOpacity, s.Opacity);
                SetSlot(slotNormal, s.Normal);
                SetSlot(slotMetal, s.Metal);
                SetSlot(slotAo, s.Ao);
                SelectItem(cmbRoughCh, s.RoughCh);
                SelectItem(cmbMetalCh, s.MetalCh);
                SelectItem(cmbAoCh, s.AoCh);
                SelectItem(cmbOpacityCh, s.OpacityCh);
                chkRoughInvert.Checked = s.RoughInvert;
                chkFlipGreen.Checked = s.FlipGreen;
                numRoughDef.Value = (decimal)Math.Max(0, Math.Min(1, s.RoughDef));
                numMetalDef.Value = (decimal)Math.Max(0, Math.Min(1, s.MetalDef));
                numAoDef.Value = (decimal)Math.Max(0, Math.Min(1, s.AoDef));
                // a set without its own output settings exports next to its textures
                string tex = s.Base.Length > 0 ? s.Base : s.Normal;
                txtOutDir.Text = s.OutDir.Length > 0 ? s.OutDir : (tex.Length > 0 ? Path.GetDirectoryName(tex) : txtOutDir.Text);
                txtBaseName.Text = s.BaseName.Length > 0 ? s.BaseName : (tex.Length > 0 ? TrimSeps(TextureSetMatcher.DeriveBaseName(tex)) : "");
            }
            finally
            {
                _switching = false;
            }
            CheckChannels();
        }

        private static void SetSlot(TextureSlot slot, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                if (slot.HasImage) slot.ClearImage();
            }
            else if (!string.Equals(slot.ImagePath, path, StringComparison.OrdinalIgnoreCase))
                slot.LoadImage(path);
        }

        private void OnLoadModelClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Pick the 3D model these textures belong to";
                dlg.Filter = "3D models|*.fbx;*.blend;*.obj|All files|*.*";
                string tex = PreviewTexturePath();
                if (!string.IsNullOrEmpty(_modelPath) && File.Exists(_modelPath))
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(_modelPath);
                    dlg.FileName = Path.GetFileName(_modelPath);
                }
                else if (tex != null)
                {
                    DirectoryInfo up = Directory.GetParent(Path.GetDirectoryName(tex));
                    dlg.InitialDirectory = up != null ? up.FullName : Path.GetDirectoryName(tex);
                }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadModel(dlg.FileName);
            }
        }

        private void LoadModel(string model)
        {
            string blender = BlenderBaker.FindBlender();
            if (blender == null)
            {
                using (OpenFileDialog dlg = new OpenFileDialog())
                {
                    dlg.Title = "Blender wasn't found - where is blender.exe?";
                    dlg.Filter = "blender.exe|blender.exe|Programs|*.exe";
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    blender = dlg.FileName;
                }
                BlenderBaker.RememberBlender(blender);
            }
            // keep the edits of the model we're leaving
            if (_cur != null && _modelPath != null)
            {
                SaveUiToSet(_cur);
                MaterialSets.Save(_modelPath, _sets, _cur);
            }
            _modelLoading = true;
            _btnModel.Enabled = false;
            _lblModel.Text = "Loading " + Path.GetFileName(model) + " in Blender...";
            _model3d.SetHint("Loading model...");
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                ModelMesh mesh = null;
                string err = null;
                try { mesh = BlenderBaker.LoadMesh(blender, model); }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)delegate
                {
                    _modelLoading = false;
                    _btnModel.Enabled = true;
                    if (mesh == null)
                    {
                        _lblModel.Text = "Could not load the model - see the status box.";
                        _model3d.SetHint(_mesh == null ? "No model loaded" : null);
                        txtStatus.Text = "3D preview: " + err;
                        return;
                    }
                    _mesh = mesh;
                    _modelPath = model;
                    string tex = PreviewTexturePath();
                    if (tex != null)
                        BlenderBaker.RememberModel(Path.GetDirectoryName(tex), model);
                    _model3d.SetMesh(mesh);
                    BuildSets();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // One texture set per material (and UDIM tile) of the model: restore what was saved, put what's in
        // the slots into the set it belongs to, and fill the rest from the texture folder by material name.
        private void BuildSets()
        {
            List<MaterialSet> sets = MaterialSets.FromMesh(_mesh);
            string savedCur = MaterialSets.Restore(_modelPath, sets);
            string[] project = _pendingProject;
            _pendingProject = null;
            if (project != null)
                savedCur = MaterialSets.RestoreLines(project, sets) ?? savedCur;
            MaterialSet cur = null;
            bool fromUi = false;
            string tex = project != null ? null : PreviewTexturePath();
            if (tex != null)
            {
                int tile = BlenderBaker.TileFromName(tex);
                List<string> guess = BlenderBaker.GuessMaterials(_mesh, tile, Path.GetFileName(tex));
                foreach (MaterialSet s in sets)
                    if (s.Tile == tile && guess.Contains(s.Material)) { cur = s; break; }
                if (cur != null)
                {
                    SaveUiToSet(cur);
                    fromUi = true;
                }
            }
            if (cur == null && savedCur != null)
                cur = sets.Find(delegate(MaterialSet s) { return s.Key == savedCur; });
            if (cur == null)
                cur = sets.Find(delegate(MaterialSet s) { return s.HasTextures; }) ?? (sets.Count > 0 ? sets[0] : null);
            _sets = sets;
            _cur = cur;

            string folder = tex != null ? Path.GetDirectoryName(tex) : FirstTextureFolder();
            int filled = project != null ? 0 : MaterialSets.AutoMatch(_sets, folder);
            RebuildSetList();
            if (_cur != null && !fromUi)
                LoadSetToUi(_cur);
            LoadAllSetMaps();
            SaveSets();
            int withTex = _sets.FindAll(delegate(MaterialSet s) { return s.HasTextures; }).Count;
            _lblModel.Text = string.Format("{0}  -  {1:N0} tris, {2} texture sets ({3} with textures)", Path.GetFileName(_modelPath), _mesh.TriCount, _sets.Count, withTex);
            if (project != null)
            {
                txtStatus.Text = string.Format("Opened project {0}: {1} texture sets, {2} with textures.", Path.GetFileName(_projectPath ?? ""), _sets.Count, withTex);
                return;
            }
            txtStatus.Text = string.Format("Model loaded: {0} texture sets (one per material{1}). {2}Click a set on the right to edit its textures; Export all sets writes every set that has textures.",
                _sets.Count, _sets.Exists(delegate(MaterialSet s) { return s.Tile != 1001; }) ? " and UDIM tile" : "",
                filled > 0 ? filled + " matched automatically from the texture folder by material name. " : "");
        }

        private string FirstTextureFolder()
        {
            if (_sets != null)
                foreach (MaterialSet s in _sets)
                    if (s.HasTextures) return Path.GetDirectoryName(s.Base.Length > 0 ? s.Base : s.Normal);
            return null;
        }

        private void RebuildSetList()
        {
            _switching = true;
            try
            {
                _lstSets.BeginUpdate();
                _lstSets.Items.Clear();
                if (_sets != null)
                    foreach (MaterialSet s in _sets)
                        _lstSets.Items.Add(s);
                _lstSets.SelectedItem = _cur;
                _lstSets.EndUpdate();
            }
            finally
            {
                _switching = false;
            }
            _btnExportSets.Enabled = _sets != null && _sets.Count > 0;
        }

        private void RefreshSetRow(MaterialSet s)
        {
            int i = _lstSets.Items.IndexOf(s);
            if (i < 0) return;
            _switching = true;
            try
            {
                _lstSets.Items[i] = s; // re-evaluates ToString (and can drop the selection)
                _lstSets.SelectedIndex = _cur != null ? _lstSets.Items.IndexOf(_cur) : -1;
            }
            finally
            {
                _switching = false;
            }
        }

        private void OnSetSelected()
        {
            if (_switching)
                return;
            MaterialSet s = _lstSets.SelectedItem as MaterialSet;
            if (s == null || s == _cur)
                return;
            MaterialSet prev = _cur;
            SaveUiToSet(prev);
            _cur = s;
            if (prev != null) RefreshSetRow(prev);
            Cursor = Cursors.WaitCursor;
            try { LoadSetToUi(s); }
            finally { Cursor = Cursors.Default; }
            SaveSets();
            txtStatus.Text = s.HasTextures
                ? "Editing " + s.Material + (s.Tile != 1001 ? " (tile " + s.Tile + ")" : "") + " - changes apply to this set."
                : "Editing " + s.Material + (s.Tile != 1001 ? " (tile " + s.Tile + ")" : "") + " - it has no textures yet: Auto-Fill or drop them in.";
        }

        private void OnMatchClick(object sender, EventArgs e)
        {
            if (_sets == null) { txtStatus.Text = "Load a model first."; return; }
            SaveUiToSet(_cur);
            string folder = PreviewTexturePath() != null ? Path.GetDirectoryName(PreviewTexturePath()) : FirstTextureFolder();
            if (folder == null)
            {
                folder = FolderPicker.Pick(this, "Folder with this model's textures", Path.GetDirectoryName(_modelPath));
                if (folder == null) return;
            }
            bool curWasEmpty = _cur != null && !_cur.HasTextures;
            int n = MaterialSets.AutoMatch(_sets, folder);
            if (curWasEmpty && _cur.HasTextures)
                LoadSetToUi(_cur);
            RebuildSetList();
            LoadAllSetMaps();
            SaveSets();
            int missing = _sets.FindAll(delegate(MaterialSet s) { return !s.HasTextures; }).Count;
            txtStatus.Text = string.Format("Matched {0} set(s) from {1}.{2}", n, folder,
                missing > 0 ? " " + missing + " still without textures - select one and Auto-Fill / drop its textures." : " Every set has textures.");
        }

        private void OnExportSetsClick(object sender, EventArgs e)
        {
            if (_sets == null) return;
            SaveUiToSet(_cur);
            MaterialSet keep = _cur;
            List<string> log = new List<string>();
            Cursor = Cursors.WaitCursor;
            try
            {
                foreach (MaterialSet s in _sets)
                {
                    if (!s.HasTextures) continue;
                    _cur = s;
                    LoadSetToUi(s);
                    log.Add("== " + s.Material + (s.Tile != 1001 ? " (tile " + s.Tile + ")" : ""));
                    try { ExportAll(log); }
                    catch (Exception ex) { log.Add("ERROR: " + ex.Message); }
                }
            }
            finally
            {
                _cur = keep;
                if (keep != null) LoadSetToUi(keep);
                Cursor = Cursors.Default;
            }
            if (log.Count == 0)
                log.Add("No texture sets with textures to export.");
            txtStatus.Text = string.Join(Environment.NewLine, log.ToArray());
            SaveSets();
        }

        private void SaveSets()
        {
            if (_sets != null && _modelPath != null)
                MaterialSets.Save(_modelPath, _sets, _cur);
        }

        private void QueuePreviewTexture()
        {
            _texTimer.Stop();
            _texTimer.Start();
        }

        // debounced: the current set changed - reload its maps onto the model and save the sets
        private void RefreshPreviewTexture()
        {
            if (_mesh == null || _cur == null)
                return;
            LoadSetMaps(new MaterialSet[] { _cur });
            SaveSets();
        }

        private void LoadAllSetMaps()
        {
            if (_sets == null) return;
            List<MaterialSet> list = new List<MaterialSet>();
            foreach (MaterialSet s in _sets)
            {
                if (s.MapCount > 0) list.Add(s);
                else _model3d.ClearGroupMaps(s.Key);
            }
            LoadSetMaps(list.ToArray());
        }

        // Builds each set's albedo / normal / packed AO-rough-metal (same channels + defaults as the export)
        // on a worker thread, one set after another, and puts them on the model.
        private void LoadSetMaps(MaterialSet[] sets)
        {
            if (sets.Length == 0) return;
            List<KeyValuePair<MaterialSet, int>> jobs = new List<KeyValuePair<MaterialSet, int>>();
            foreach (MaterialSet s in sets)
            {
                int j;
                _setJobs.TryGetValue(s.Key, out j);
                _setJobs[s.Key] = ++j;
                // snapshot: the set may be edited while we load
                MaterialSet copy = (MaterialSet)s.GetType().GetMethod("MemberwiseClone", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(s, null);
                jobs.Add(new KeyValuePair<MaterialSet, int>(copy, j));
            }
            _mapLoads++;
            UpdateLoadingLabel();
            int maxSize = PreviewMaxSize();
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                foreach (KeyValuePair<MaterialSet, int> job in jobs)
                {
                    MaterialSet s = job.Key;
                    GLImage alb = null, nrm = null, orm = null;
                    string err = null;
                    try { BuildPreviewMaps(s, maxSize, out alb, out nrm, out orm); }
                    catch (Exception ex) { err = ex.Message; }
                    int jobId = job.Value;
                    BeginInvoke((Action)delegate
                    {
                        int latest;
                        _setJobs.TryGetValue(s.Key, out latest);
                        if (latest != jobId) return;
                        if (err != null) txtStatus.Text = "3D preview: could not load maps of " + s.Material + " - " + err;
                        _model3d.SetGroupMaps(s.Key, alb, nrm, orm, s.FlipGreen);
                    });
                }
                BeginInvoke((Action)delegate { _mapLoads--; UpdateLoadingLabel(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void UpdateLoadingLabel()
        {
            string txt = _lblModel.Text.Replace("  (loading maps...)", "");
            _lblModel.Text = _mapLoads > 0 ? txt + "  (loading maps...)" : txt;
        }

        // The preview shows textures at the export size (same bicubic downscale), so what you see is what you export.
        private int PreviewMaxSize()
        {
            int v;
            return int.TryParse((string)cmbMaxSize.SelectedItem, out v) ? v : 16384; // Auto = the source's own size
        }

        private static void BuildPreviewMaps(MaterialSet s, int maxSize, out GLImage alb, out GLImage nrm, out GLImage orm)
        {
            string basePath = s.Base.Length > 0 ? s.Base : null, nrmPath = s.Normal.Length > 0 ? s.Normal : null;
            string roughPath = s.Rough.Length > 0 ? s.Rough : null, metalPath = s.Metal.Length > 0 ? s.Metal : null, aoPath = s.Ao.Length > 0 ? s.Ao : null;
            byte roughDef = (byte)Math.Round(s.RoughDef * 255), metalDef = (byte)Math.Round(s.MetalDef * 255), aoDef = (byte)Math.Round(s.AoDef * 255);
            // the maps decode in parallel - 4K PNGs are slow to decode one after another
            GLImage pa = null, pn = null;
            System.Threading.Tasks.Task ta = System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                if (basePath != null) using (Bitmap b = Packer.LoadBitmap(basePath)) pa = GLImage.FromBitmap(b, maxSize);
            });
            System.Threading.Tasks.Task tn = System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                if (nrmPath != null) using (Bitmap b = Packer.LoadBitmap(nrmPath)) pn = GLImage.FromBitmap(b, maxSize);
            });
            if (roughPath != null || metalPath != null || aoPath != null)
            {
                // an ORM file sits in all three slots - decode each file once, at the size of the first one
                Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    int w = 0, h = 0;
                    foreach (string p in new string[] { aoPath, roughPath, metalPath })
                    {
                        if (p == null || cache.ContainsKey(p)) continue;
                        Bitmap b = Packer.LoadBitmap(p);
                        if (w == 0)
                        {
                            double sc = Math.Min(1.0, (double)maxSize / Math.Max(b.Width, b.Height));
                            w = Math.Max(1, (int)(b.Width * sc));
                            h = Math.Max(1, (int)(b.Height * sc));
                        }
                        cache[p] = Packer.EnsureSize(b, w, h);
                    }
                    byte[] ao = PreviewChannel(cache, aoPath, s.AoCh, false);
                    byte[] ro = PreviewChannel(cache, roughPath, s.RoughCh, s.RoughInvert);
                    byte[] me = PreviewChannel(cache, metalPath, s.MetalCh, false);
                    using (Bitmap packed = Packer.Compose(w, h, ao, aoDef, ro, roughDef, me, metalDef, null, 255))
                        orm = GLImage.FromBitmap(packed, maxSize);
                }
                finally
                {
                    foreach (Bitmap b in cache.Values) b.Dispose();
                }
            }
            else
            {
                using (Bitmap flat = Packer.Compose(1, 1, null, aoDef, null, roughDef, null, metalDef, null, 255))
                    orm = GLImage.FromBitmap(flat, 1);
            }
            System.Threading.Tasks.Task.WaitAll(ta, tn);
            alb = pa;
            nrm = pn;
        }

        private static byte[] PreviewChannel(Dictionary<string, Bitmap> cache, string path, string channel, bool invert)
        {
            if (path == null)
                return null;
            byte[] v = Packer.ExtractChannel(cache[path], channel ?? "R");
            if (invert) Packer.Invert(v);
            return v;
        }

        // ---- project files (.rtp) --------------------------------------------------------

        private void UpdateTitle()
        {
            Text = "Reforger Texture Packer - by Modest23" + (_projectPath != null ? "   -   " + Path.GetFileName(_projectPath) : "");
        }

        private void SaveProject(bool saveAs)
        {
            string path = _projectPath;
            if (path == null || saveAs)
            {
                using (SaveFileDialog dlg = new SaveFileDialog())
                {
                    dlg.Title = "Save texture packer project";
                    dlg.Filter = "Texture Packer project (*.rtp)|*.rtp";
                    dlg.DefaultExt = "rtp";
                    string near = _modelPath ?? PreviewTexturePath();
                    if (near != null) dlg.InitialDirectory = Path.GetDirectoryName(near);
                    dlg.FileName = _modelPath != null ? Path.GetFileNameWithoutExtension(_modelPath)
                        : (txtBaseName.Text.Trim().Length > 0 ? txtBaseName.Text.Trim() : "Textures");
                    if (dlg.ShowDialog(this) != DialogResult.OK)
                        return;
                    path = dlg.FileName;
                }
            }
            try
            {
                List<MaterialSet> sets = _sets;
                MaterialSet cur = _cur;
                if (cur != null)
                    SaveUiToSet(cur);
                if (sets == null)
                {
                    // no model: the project is just what's in the slots
                    cur = new MaterialSet();
                    SaveUiToSet(cur);
                    sets = new List<MaterialSet>();
                    sets.Add(cur);
                }
                StringBuilder head = new StringBuilder();
                head.AppendLine("# Reforger Texture Packer project");
                head.AppendLine("rtp=1");
                head.AppendLine("model=" + (_modelPath ?? ""));
                head.AppendLine("maxsize=" + cmbMaxSize.SelectedItem);
                head.AppendLine("show=" + _cmbShow.SelectedIndex);
                File.WriteAllText(path, MaterialSets.Serialize(sets, cur, head.ToString()));
                _projectPath = path;
                Prefs.Set("lastproject", path);
                UpdateTitle();
                int withTex = sets.FindAll(delegate(MaterialSet s) { return s.HasTextures; }).Count;
                txtStatus.Text = string.Format("Saved project {0}  ({1} texture set(s), {2} with textures{3}).", path, sets.Count, withTex,
                    _modelPath != null ? ", model " + Path.GetFileName(_modelPath) : "");
            }
            catch (Exception ex)
            {
                txtStatus.Text = "Could not save the project: " + ex.Message;
            }
        }

        private void OnOpenProjectClick()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Open texture packer project";
                dlg.Filter = "Texture Packer project (*.rtp)|*.rtp|All files|*.*";
                string last = Prefs.Get("lastproject", "");
                if (last.Length > 0 && File.Exists(last))
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(last);
                    dlg.FileName = Path.GetFileName(last);
                }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    OpenProject(dlg.FileName);
            }
        }

        private void OpenProject(string path)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex) { txtStatus.Text = "Could not open the project: " + ex.Message; return; }
            string model = "", maxsize = null;
            int show = -1;
            foreach (string l in lines)
            {
                if (l.StartsWith("[")) break;
                if (l.StartsWith("model=")) model = l.Substring(6);
                else if (l.StartsWith("maxsize=")) maxsize = l.Substring(8);
                else if (l.StartsWith("show=")) int.TryParse(l.Substring(5), out show);
            }
            // keep what's open now in its per-model autosave before switching
            if (_cur != null && _modelPath != null)
            {
                SaveUiToSet(_cur);
                SaveSets();
            }
            _projectPath = path;
            Prefs.Set("lastproject", path);
            UpdateTitle();
            if (maxsize != null) cmbMaxSize.SelectedItem = maxsize;
            if (show >= 0 && show < _cmbShow.Items.Count) _cmbShow.SelectedIndex = show;

            if (model.Length > 0 && File.Exists(model))
            {
                // BuildSets applies these once the model has loaded
                _pendingProject = lines;
                LoadModel(model);
                txtStatus.Text = "Opening project " + Path.GetFileName(path) + " - loading " + Path.GetFileName(model) + "...";
                return;
            }
            // no model (or it has moved): bring back the sets as they were, just without the 3D view
            List<MaterialSet> sets = MaterialSets.FromLines(lines);
            string curKey = MaterialSets.RestoreLines(lines, sets);
            MaterialSet cur = sets.Find(delegate(MaterialSet s) { return s.Key == curKey; }) ?? (sets.Count > 0 ? sets[0] : null);
            if (model.Length > 0)
            {
                // several sets but no model: still let the user switch between them in the list
                _sets = sets.Count > 1 ? sets : null;
                _cur = sets.Count > 1 ? cur : null;
                RebuildSetList();
            }
            if (cur != null) LoadSetToUi(cur);
            txtStatus.Text = model.Length > 0
                ? "Opened " + Path.GetFileName(path) + ", but its model wasn't found (" + model + ") - textures restored; use Load model... to point at it again."
                : "Opened project " + Path.GetFileName(path) + ".";
        }

        private void OnOutBrowseClick(object sender, EventArgs e)
        {
            string picked = FolderPicker.Pick(this, "Select output folder", txtOutDir.Text.Trim());
            if (picked != null)
                txtOutDir.Text = picked;
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
