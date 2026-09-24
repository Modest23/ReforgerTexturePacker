using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // Mask / VFX generator.
    //  _GLOBAL_MASK: the texture is split into material regions (auto-clustered from color/rough/metal,
    //  or read from an ID map), each region is assigned Mat 1-4, with paint/fill overrides on top.
    //  _VFX: dirt (R) and mud (G) layers built from surface relief, occlusion, gradients, noise,
    //  scratches, grunge, breakup and streaks, optionally weighted per material.
    public class MaskVfxDialog : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int V_MATOVER = 0, V_REGIONS = 1, V_GM = 2, V_DIRTOVER = 3, V_MUDOVER = 4, V_VFXOVER = 5,
                          V_VFX = 6, V_DIRT = 7, V_MUD = 8, V_RELIEF = 9, V_OCC = 10, V_COLOR = 11, V_NORMAL = 12;
        private static readonly string[] ViewNames =
        {
            "Materials over texture", "Material regions (clusters)", "_GLOBAL_MASK file (exact)",
            "Dirt over texture (red)", "Mud over texture (green)", "_VFX over texture (both)", "_VFX file (exact)",
            "Dirt only (red)", "Mud only (green)", "Surface relief (edges light, crevices dark)",
            "Occlusion", "Base color", "Normal map"
        };
        private static readonly string[] MatNames = { "Mat 1  (black)", "Mat 2  (R)", "Mat 3  (G)", "Mat 4  (B)" };
        private static readonly Color[] MatColors =
        {
            Color.FromArgb(70, 70, 70), Color.FromArgb(225, 60, 50), Color.FromArgb(60, 190, 80), Color.FromArgb(60, 110, 235)
        };
        private static readonly string[] ToolNames = { "Assign whole region", "Fill connected area", "Paint brush", "Erase paint" };
        private const int T_ASSIGN = 0, T_FILL = 1, T_BRUSH = 2, T_ERASE = 3;

        // ---- state -------------------------------------------------------------------------
        private MaskGenContext _ctx;
        private MaskSources _src;
        private RegionSettings _rs;
        private MatChannelSettings[] _ch;
        private LayerSettings _dirt, _mud;
        private Regions _reg;
        private sbyte[] _paint;
        private byte[] _mat;
        private float[][] _presence, _presenceSoft;
        private float[][] _gm;
        private float[] _dirtMap, _mudMap;
        private int _workRes = 1024;
        private int[] _pendingAssign;   // saved assignment waiting for the first clustering
        private MaskPresetStore.State _loaded;

        private bool _dRegions, _dMask, _dDirt, _dMud, _dragging, _ready;
        private int _suppress;
        private int _tool = T_ASSIGN, _paintMat = 1, _brush = 12, _selRegion = -1, _strokeLabel = -1;
        private bool _brushInside = true;
        private Point _lastStamp;
        private Stack<KeyValuePair<int[], sbyte[]>> _undo = new Stack<KeyValuePair<int[], sbyte[]>>();

        // ---- UI ------------------------------------------------------------------------------
        private MaskPreview _preview;
        private ComboBox _cmbView, _cmbWorkRes, _cmbSize, _cmbTool;
        private Label _status, _hover, _lblIdMap, _lblBake;
        private Button _btnBake;
        private string _bakeModel = "", _bakeMats = "";
        private bool _baking;
        private Panel[] _tabs = new Panel[3];
        private Button[] _tabBtns = new Button[3];
        private Button[] _matBtns = new Button[4];
        private Panel _regionList;
        private List<ComboBox> _regionCombos = new List<ComboBox>();
        private List<Panel> _regionRows = new List<Panel>();
        private List<Action> _refreshers = new List<Action>();
        private Dictionary<LayerSettings, Label> _grungeLabels = new Dictionary<LayerSettings, Label>();
        private ToolTip _tip = new ToolTip();
        private Timer _timer = new Timer();
        private int _activeTab;

        public MaskVfxDialog(MaskGenContext ctx)
        {
            _ctx = ctx;
            _loaded = MaskPresetStore.Load(ctx.NormalPath);
            MaskPresetStore.State st = _loaded ?? new MaskPresetStore.State();
            _rs = st.Regions;
            _ch = st.Channels;
            _dirt = st.Dirt;
            _mud = st.Mud;
            _workRes = st.WorkRes == 512 || st.WorkRes == 2048 ? st.WorkRes : 1024;
            if (_loaded != null && _loaded.Assign != null)
                _pendingAssign = _loaded.Assign;
            if (_loaded != null)
            {
                _bakeModel = _loaded.BakeModel ?? "";
                _bakeMats = _loaded.BakeMaterials ?? "";
            }

            Text = "Mask / VFX Generator  -  " + ctx.BaseName;
            Font = new Font("Segoe UI", 9F);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(1440, wa.Width - 40), Math.Min(880, wa.Height - 60));
            MinimumSize = new Size(1100, 680);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            _tip.AutoPopDelay = 20000;

            int cw = ClientSize.Width, chh = ClientSize.Height;
            int rightX = cw - 470 - 12;

            _preview = new MaskPreview();
            _preview.SetBounds(12, 12, rightX - 24, chh - 12 - 70);
            _preview.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _preview.ImageMouse += OnPreviewMouse;
            _preview.PickClick += OnPreviewPick;
            _preview.HoverPixel += OnPreviewHover;
            Controls.Add(_preview);

            Label lv = new Label();
            lv.Text = "View:";
            lv.SetBounds(12, chh - 50, 40, 16);
            lv.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            Controls.Add(lv);

            _cmbView = new ComboBox();
            _cmbView.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbView.Items.AddRange(ViewNames);
            _cmbView.SelectedIndex = V_MATOVER;
            _cmbView.SetBounds(54, chh - 54, 270, 23);
            _cmbView.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _cmbView.SelectedIndexChanged += delegate { RenderPreview(); };
            Controls.Add(_cmbView);

            Button btnFit = new Button();
            btnFit.Text = "Fit";
            btnFit.SetBounds(330, chh - 55, 50, 25);
            btnFit.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnFit.Click += delegate { _preview.FitToView(); };
            Controls.Add(btnFit);

            _hover = new Label();
            _hover.SetBounds(390, chh - 50, rightX - 400, 16);
            _hover.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _hover.ForeColor = Theme.SubText;
            _hover.AutoEllipsis = true;
            _hover.Text = "Wheel = zoom, right-drag = pan, right-click = pick region, double-click = fit";
            Controls.Add(_hover);

            _status = new Label();
            _status.SetBounds(12, chh - 24, cw - 24, 18);
            _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _status.AutoEllipsis = true;
            _status.ForeColor = Theme.SubText;
            _status.Text = "Analyzing source maps...";
            Controls.Add(_status);

            // tab strip
            string[] tabNames = { "Materials  (MASK)", "Dirt  (VFX red)", "Mud  (VFX green)" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                Button b = new Button();
                b.Text = tabNames[i];
                b.SetBounds(rightX + i * 157, 12, 155, 30);
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                b.Click += delegate { SelectTab(idx, true); };
                Controls.Add(b);
                _tabBtns[i] = b;

                Panel p = new Panel();
                p.SetBounds(rightX, 48, 470, chh - 48 - 180);
                p.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right;
                p.AutoScroll = true;
                p.BackColor = Theme.Bg;
                p.Visible = i == 0;
                Controls.Add(p);
                _tabs[i] = p;
            }
            BuildMaterialsTab(_tabs[0]);
            BuildLayerTab(_tabs[1], _dirt, true);
            BuildLayerTab(_tabs[2], _mud, false);
            BuildExportBox(rightX, chh - 172);

            Theme.Apply(this);
            SelectTab(0, false);

            _timer.Interval = 30;
            _timer.Tick += delegate { _timer.Stop(); Recompute(); };
        }

        // =====================================================================================
        //  UI construction
        // =====================================================================================

        private class Builder
        {
            private MaskVfxDialog _d;
            public Panel P;
            public int Y = 4;

            public Builder(MaskVfxDialog d, Panel p) { _d = d; P = p; }

            public void Header(string text)
            {
                Y += 8;
                Label l = new Label();
                l.Text = text;
                l.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
                l.SetBounds(6, Y, 430, 20);
                P.Controls.Add(l);
                Panel line = new Panel();
                line.BackColor = Theme.Border;
                line.SetBounds(6, Y + 21, 432, 1);
                P.Controls.Add(line);
                Y += 28;
            }

            public Label Note(string text, int lines)
            {
                Label l = new Label();
                l.Text = text;
                l.ForeColor = Theme.SubText;
                l.SetBounds(8, Y, 430, 15 * lines + 2);
                P.Controls.Add(l);
                Y += 15 * lines + 6;
                return l;
            }

            public void Slider(string cap, double min, double max, double step, Func<double> get, Action<double> set, string fmt, string tip, Action changed)
            {
                Label l = new Label();
                l.Text = cap;
                l.SetBounds(8, Y + 4, 118, 16);
                P.Controls.Add(l);

                WheelSafeTrackBar tb = new WheelSafeTrackBar();
                tb.AutoSize = false;
                tb.SetBounds(126, Y, 246, 26);
                tb.Minimum = 0;
                tb.Maximum = Math.Max(1, (int)Math.Round((max - min) / step));
                tb.TickStyle = TickStyle.None;
                tb.BackColor = Theme.Bg;
                P.Controls.Add(tb);

                Label val = new Label();
                val.SetBounds(376, Y + 4, 62, 16);
                P.Controls.Add(val);

                if (!string.IsNullOrEmpty(tip))
                {
                    _d._tip.SetToolTip(l, tip);
                    _d._tip.SetToolTip(tb, tip);
                }

                MaskVfxDialog d = _d;
                Action refresh = delegate
                {
                    d._suppress++;
                    int v = (int)Math.Round((get() - min) / step);
                    tb.Value = Math.Max(tb.Minimum, Math.Min(tb.Maximum, v));
                    val.Text = Fmt(get(), fmt);
                    d._suppress--;
                };
                tb.ValueChanged += delegate
                {
                    if (d._suppress > 0)
                        return;
                    double v = min + tb.Value * step;
                    set(v);
                    val.Text = Fmt(v, fmt);
                    changed();
                };
                _d._refreshers.Add(refresh);
                refresh();
                Y += 28;
            }

            public ComboBox Combo(string cap, string[] items, Func<int> get, Action<int> set, string tip, Action changed)
            {
                Label l = new Label();
                l.Text = cap;
                l.SetBounds(8, Y + 4, 118, 16);
                P.Controls.Add(l);
                WheelSafeComboBox c = new WheelSafeComboBox();
                c.DropDownStyle = ComboBoxStyle.DropDownList;
                c.Items.AddRange(items);
                c.SetBounds(128, Y, 244, 23);
                P.Controls.Add(c);
                if (!string.IsNullOrEmpty(tip))
                {
                    _d._tip.SetToolTip(l, tip);
                    _d._tip.SetToolTip(c, tip);
                }
                MaskVfxDialog d = _d;
                Action refresh = delegate
                {
                    d._suppress++;
                    c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, get()));
                    d._suppress--;
                };
                c.SelectedIndexChanged += delegate
                {
                    if (d._suppress > 0)
                        return;
                    set(c.SelectedIndex);
                    changed();
                };
                _d._refreshers.Add(refresh);
                refresh();
                Y += 30;
                return c;
            }

            public CheckBox Check(string text, Func<bool> get, Action<bool> set, Action changed)
            {
                CheckBox c = new CheckBox();
                c.Text = text;
                c.SetBounds(128, Y, 300, 22);
                P.Controls.Add(c);
                MaskVfxDialog d = _d;
                Action refresh = delegate
                {
                    d._suppress++;
                    c.Checked = get();
                    d._suppress--;
                };
                c.CheckedChanged += delegate
                {
                    if (d._suppress > 0)
                        return;
                    set(c.Checked);
                    changed();
                };
                _d._refreshers.Add(refresh);
                refresh();
                Y += 26;
                return c;
            }

            public Button Button(string text, int x, int w, EventHandler click)
            {
                Button b = new Button();
                b.Text = text;
                b.SetBounds(x, Y, w, 26);
                b.Click += click;
                P.Controls.Add(b);
                return b;
            }

            public void End()
            {
                // spacer so the last row isn't flush against the scroll edge
                Panel sp = new Panel();
                sp.SetBounds(0, Y + 10, 10, 10);
                P.Controls.Add(sp);
            }
        }

        private static string Fmt(double v, string fmt)
        {
            switch (fmt)
            {
                case "pct": return (v * 100).ToString("0") + "%";
                case "px": return v.ToString("0.#") + " px";
                case "deg": return v.ToString("0") + "°";
                case "x": return v.ToString("0.0") + "x";
                case "int": return v.ToString("0");
                default: return v.ToString("0.00");
            }
        }

        private void BuildMaterialsTab(Panel p)
        {
            Builder b = new Builder(this, p);
            Action reg = delegate { MarkDirty(true, true, false, false); };
            Action mask = delegate { MarkDirty(false, true, false, false); };

            b.Header("1.  Split the texture into regions");
            b.Combo("Regions from:", new string[] { "Auto (from color / rough / metal)", "ID map image" },
                delegate { return _rs.Source; }, delegate(int v) { _rs.Source = v; },
                "Auto clusters similar-looking pixels. An ID map (flat colour per material, baked from Blender/Substance) is exact.", reg);
            _lblIdMap = new Label();
            _lblIdMap.SetBounds(128, b.Y + 4, 218, 16);
            _lblIdMap.AutoEllipsis = true;
            _lblIdMap.ForeColor = Theme.SubText;
            _lblIdMap.Text = string.IsNullOrEmpty(_rs.IdMapPath) ? "(no ID map loaded)" : Path.GetFileName(_rs.IdMapPath);
            p.Controls.Add(_lblIdMap);
            b.Button("Load ID map…", 350, 88, OnLoadIdMap);
            b.Y += 32;
            b.Slider("Regions", 2, 16, 1, delegate { return _rs.Clusters; }, delegate(double v) { _rs.Clusters = (int)v; }, "int",
                "How many regions auto-clustering splits the texture into. More = finer control, each assignable to a material.", reg);
            b.Slider("Color weight", 0, 2, 0.05, delegate { return _rs.ColorW; }, delegate(double v) { _rs.ColorW = v; }, "f2", "How much albedo colour separates regions.", reg);
            b.Slider("Roughness weight", 0, 2, 0.05, delegate { return _rs.RoughW; }, delegate(double v) { _rs.RoughW = v; }, "f2", "Rubber vs paint vs bare metal differ strongly in roughness.", reg);
            b.Slider("Metal weight", 0, 2, 0.05, delegate { return _rs.MetalW; }, delegate(double v) { _rs.MetalW = v; }, "f2", "Separates metal from non-metal (needs a metalness map).", reg);
            b.Slider("Surface detail wt.", 0, 2, 0.05, delegate { return _rs.DetailW; }, delegate(double v) { _rs.DetailW = v; }, "f2", "Bumpy/treaded vs smooth surfaces, from the normal map.", reg);
            b.Slider("Cleanup", 0, 8, 1, delegate { return _rs.Cleanup; }, delegate(double v) { _rs.Cleanup = (int)v; }, "px", "Removes speckle - each pixel takes the majority region around it.", reg);
            b.Note(MapsInUse(), 1);

            b.Header("2.  Assign regions to materials");
            b.Note("Pick a material, then click in the preview. Right-click picks a region.", 1);
            for (int m = 0; m < 4; m++)
            {
                int mi = m;
                Button mb = new Button();
                mb.Text = MatNames[m];
                mb.SetBounds(8 + m * 108, b.Y, 104, 28);
                mb.Click += delegate { SetPaintMat(mi); };
                p.Controls.Add(mb);
                _matBtns[m] = mb;
            }
            b.Y += 36;
            _cmbTool = b.Combo("Left click:", ToolNames, delegate { return _tool; }, delegate(int v) { _tool = v; },
                "Whole region: the region's material in the list below.  Fill: only the connected patch you click (per UV island).  Brush/Erase: paint overrides.",
                delegate { OnToolChanged(); });
            b.Slider("Brush size", 1, 150, 1, delegate { return _brush; }, delegate(double v) { _brush = (int)v; }, "px", "Brush radius in working-resolution pixels.  [ and ] also resize.", delegate { OnToolChanged(); });
            b.Check("Brush stays inside the region it starts in", delegate { return _brushInside; }, delegate(bool v) { _brushInside = v; }, delegate { });
            b.Button("Undo (Ctrl+Z)", 8, 104, delegate { Undo(); });
            b.Button("Clear paint", 116, 104, delegate { PushUndo(); ClearPaint(); MarkDirty(false, true, false, false); });
            b.Button("Auto-assign", 224, 104, delegate
            {
                if (_reg == null) return;
                PushUndo();
                _reg.AutoAssign();
                RefreshRegionCombos();
                MarkDirty(false, true, false, false);
            });
            b.Y += 34;
            _regionList = new Panel();
            _regionList.SetBounds(8, b.Y, 430, 216);
            _regionList.AutoScroll = true;
            _regionList.BackColor = Theme.PanelBg;
            p.Controls.Add(_regionList);
            b.Y += 222;

            b.Header("3.  How each material channel is filled");
            b.Note("In-game each channel is cut by the material's tiling Mask_N texture\n(MaskSharpness / MaskOffset in the .emat): solid = full coverage, a\ngradient becomes wear / peeling / scratches along it.", 3);
            for (int c = 0; c < 3; c++)
            {
                MatChannelSettings s = _ch[c];
                b.Header("Mat " + (c + 2) + "  ->  " + "RGB"[c] + " channel");
                b.Slider("Level", 0, 1, 0.01, delegate { return s.Level; }, delegate(double v) { s.Level = v; }, "pct", "Channel value inside the region.", mask);
                b.Combo("Modulate by", MatChannelSettings.ModulateNames, delegate { return s.Modulate; }, delegate(int v) { s.Modulate = v; },
                    "Varies the channel inside the region, e.g. Edges = this material only shows on worn edges.", mask);
                b.Slider("Amount", 0, 1, 0.01, delegate { return s.Amount; }, delegate(double v) { s.Amount = v; }, "pct", null, mask);
                b.Slider("Scale", 0, 1, 0.01, delegate { return s.Scale; }, delegate(double v) { s.Scale = v; }, "pct", "Detail size of the modulation (fine -> broad).", mask);
                b.Check("Invert modulation", delegate { return s.Invert; }, delegate(bool v) { s.Invert = v; }, mask);
            }
            b.Header("Edges");
            b.Slider("Edge softness", 0, 8, 0.5, delegate { return _rs.EdgeSoft; }, delegate(double v) { _rs.EdgeSoft = v; }, "px",
                "Blurs region borders. Vanilla masks are usually hard (0) - the shader's Mask_N does the breakup.", mask);
            b.End();
        }

        private string MapsInUse()
        {
            List<string> u = new List<string>();
            u.Add("Normal");
            if (!string.IsNullOrEmpty(_ctx.BasePath)) u.Add("Color");
            if (!string.IsNullOrEmpty(_ctx.RoughPath)) u.Add("Rough");
            if (!string.IsNullOrEmpty(_ctx.MetalPath)) u.Add("Metal");
            if (!string.IsNullOrEmpty(_ctx.AoPath)) u.Add("AO");
            return "Using: " + string.Join(" + ", u.ToArray()) + "   (more maps loaded = better regions)";
        }

        private void BuildLayerTab(Panel p, LayerSettings s, bool isDirt)
        {
            Builder b = new Builder(this, p);
            Action ch = delegate { MarkDirty(false, false, isDirt, !isDirt); };

            b.Note(isDirt
                ? "_VFX red drives the material's Dirt layer (DirtBCRMap / DirtMaskMap,\nDirtOpacity, DirtLayerSharpness). Keep it a soft gradient - the shader\nthresholds it against the tiling DirtMaskMap for the fine pattern."
                : "_VFX green drives the material's Mud layer (MudBCRMap / MudMaskMap,\nMudOpacity). Typically heavier, lower down and around wheels.\nUV 'down' is only the model's bottom if the UVs are laid out upright.", 3);
            List<string> presets = new List<string>();
            presets.Add("- apply a preset -");
            presets.AddRange(LayerSettings.PresetNames);
            b.Combo("Preset", presets.ToArray(), delegate { return 0; }, delegate(int v)
            {
                if (v <= 0) return;
                LayerSettings pr = LayerSettings.Preset(v - 1, _src != null && _src.HasBake);
                pr.GrungePath = s.GrungePath;
                pr.Seed = s.Seed;
                CopyLayer(pr, s);
                BeginInvoke((Action)delegate { RefreshAll(); });
            }, "Starting points - every slider stays editable afterwards.", ch);

            b.Header("Quick controls");
            b.Slider("Amount", 0, 1, 0.01, delegate { return s.OutMax; }, delegate(double v) { s.OutMax = v; }, "pct",
                "How strong it gets at its strongest. Vanilla VFX maps sit around 60-90%.", ch);
            b.Slider("Coverage", 0, 1, 0.01, delegate { return Math.Max(0, Math.Min(1, 1 - s.BlackPoint / 0.6)); }, delegate(double v)
            {
                s.BlackPoint = (1 - v) * 0.6;
                s.WhitePoint = Math.Min(1, s.BlackPoint + 0.3 + 0.3 * v);
                BeginInvoke((Action)RefreshControls);
            }, "pct", "How much of the surface it spreads over - low = only the strongest spots, high = most of it.", ch);
            b.Slider("Patch size", 0, 1, 0.01, delegate { return s.BreakupScale; }, delegate(double v)
            {
                s.BreakupScale = v;
                s.NoiseScale = v;
                BeginInvoke((Action)RefreshControls);
            }, "pct", "Size of the patches / blotches on the model: small speckles (0) -> big patches (100).", ch);

            b.Header("Where it collects");
            b.Slider("Everywhere", 0, 1, 0.01, delegate { return s.Base; }, delegate(double v) { s.Base = v; }, "pct", "Base coverage over the whole texture - breakup, weighting and levels still shape it.", ch);
            b.Slider("Crevices", 0, 1, 0.01, delegate { return s.Cavity; }, delegate(double v) { s.Cavity = v; }, "pct", "Concave areas: seams, recesses, panel gaps.", ch);
            b.Slider("  scale", 0, 1, 0.01, delegate { return s.CavityScale; }, delegate(double v) { s.CavityScale = v; }, "pct", "Fine creases (0) -> broad recessed areas (100).", ch);
            b.Slider("Edges", 0, 1, 0.01, delegate { return s.Edges; }, delegate(double v) { s.Edges = v; }, "pct", "Convex / raised edges - wear.", ch);
            b.Slider("  scale", 0, 1, 0.01, delegate { return s.EdgeScale; }, delegate(double v) { s.EdgeScale = v; }, "pct", "Sharp thin edges (0) -> broad raised forms (100).", ch);
            b.Slider(_ctx.AoPath != null ? "Occlusion (AO)" : "Occlusion (est.)", 0, 1, 0.01, delegate { return s.Occlusion; }, delegate(double v) { s.Occlusion = v; }, "pct",
                _ctx.AoPath != null ? "From the loaded AO map." : "No AO map loaded - estimated from broad concavity. Load an AO map for much better results.", ch);
            b.Slider("UV gradient", 0, 1, 0.01, delegate { return s.Gradient; }, delegate(double v) { s.Gradient = v; }, "pct", "Builds up towards one side of the UV layout.", ch);
            b.Combo("  direction", new string[] { "More at bottom (V down)", "More at top", "More at right", "More at left" },
                delegate { return s.GradDir; }, delegate(int v) { s.GradDir = v; }, null, ch);
            b.Slider("  start", 0, 1, 0.01, delegate { return s.GradStart; }, delegate(double v) { s.GradStart = v; }, "pct", null, ch);
            b.Slider("  end", 0, 1, 0.01, delegate { return s.GradEnd; }, delegate(double v) { s.GradEnd = v; }, "pct", null, ch);

            b.Header("On the 3D model  (needs Bake from 3D model)");
            b.Slider("Faces up", 0, 1, 0.01, delegate { return s.FaceUp; }, delegate(double v) { s.FaceUp = v; }, "pct", "Tops of surfaces on the real model - dust, grime settling.", ch);
            b.Slider("Faces down", 0, 1, 0.01, delegate { return s.FaceDown; }, delegate(double v) { s.FaceDown = v; }, "pct", "Undersides on the real model - mud, road spray.", ch);
            b.Slider("Low on model", 0, 1, 0.01, delegate { return s.Low; }, delegate(double v) { s.Low = v; }, "pct", "Near the ground on the real model - mud builds up from the bottom.", ch);
            b.Slider("  low zone height", 0.05, 1, 0.01, delegate { return s.LowRange; }, delegate(double v) { s.LowRange = v; }, "pct", "How far up the model 'low' reaches (fraction of the model's height).", ch);
            b.Slider("Keep to these", 0, 1, 0.01, delegate { return s.Confine; }, delegate(double v) { s.Confine = v; }, "pct",
                "Restricts everything else (noise, crevices, patches) to the areas above - e.g. mud patches only underneath and low down.", ch);

            b.Header("Pattern");
            b.Slider("Noise", 0, 1, 0.01, delegate { return s.Noise; }, delegate(double v) { s.Noise = v; }, "pct", "Adds cloudy fractal noise.", ch);
            b.Slider("  scale", 0, 1, 0.01, delegate { return s.NoiseScale; }, delegate(double v) { s.NoiseScale = v; }, "pct", null, ch);
            b.Slider("Scratches", 0, 1, 0.01, delegate { return s.Scratches; }, delegate(double v) { s.Scratches = v; }, "pct", "Procedural thin scratch lines.", ch);
            b.Slider("  density", 0, 1, 0.01, delegate { return s.ScratchDensity; }, delegate(double v) { s.ScratchDensity = v; }, "pct", null, ch);
            b.Slider("  length", 0, 1, 0.01, delegate { return s.ScratchLength; }, delegate(double v) { s.ScratchLength = v; }, "pct", null, ch);
            b.Slider("  angle", 0, 180, 1, delegate { return s.ScratchAngle; }, delegate(double v) { s.ScratchAngle = v; }, "deg", null, ch);
            b.Slider("  angle spread", 0, 180, 1, delegate { return s.ScratchSpread; }, delegate(double v) { s.ScratchSpread = v; }, "deg", "0 = all parallel, 180 = any direction.", ch);
            b.Slider("  on edges only", 0, 1, 0.01, delegate { return s.ScratchEdgeBias; }, delegate(double v) { s.ScratchEdgeBias = v; }, "pct", "Keeps scratches to raised edges (uses the Edges scale).", ch);
            b.Slider("Grunge texture", 0, 1, 0.01, delegate { return s.Grunge; }, delegate(double v) { s.Grunge = v; }, "pct", "Your own tileable grunge / scratch image (luma), tiled over the UVs.", ch);
            b.Slider("  tiling", 0.5, 16, 0.5, delegate { return s.GrungeTiling; }, delegate(double v) { s.GrungeTiling = v; }, "x", null, ch);
            Label lg = new Label();
            lg.SetBounds(128, b.Y + 4, 190, 16);
            lg.AutoEllipsis = true;
            lg.ForeColor = Theme.SubText;
            lg.Text = string.IsNullOrEmpty(s.GrungePath) ? "(none)" : Path.GetFileName(s.GrungePath);
            p.Controls.Add(lg);
            _grungeLabels[s] = lg;
            b.Button("Load…", 322, 58, delegate { LoadGrunge(s, isDirt); });
            b.Button("Clear", 384, 54, delegate { s.GrungePath = ""; lg.Text = "(none)"; ch(); });
            b.Y += 32;

            b.Header("Breakup & streaks");
            b.Slider("Breakup", 0, 1, 0.01, delegate { return s.Breakup; }, delegate(double v) { s.Breakup = v; }, "pct", "Knocks the result out in patches so it isn't uniform.", ch);
            b.Slider("  scale", 0, 1, 0.01, delegate { return s.BreakupScale; }, delegate(double v) { s.BreakupScale = v; }, "pct", null, ch);
            b.Slider("Streaks", 0, 1, 0.01, delegate { return s.Streaks; }, delegate(double v) { s.Streaks = v; }, "pct", "Drips / rain streaks running down (V+) from what's already there.", ch);
            b.Slider("  length", 0, 1, 0.01, delegate { return s.StreakLength; }, delegate(double v) { s.StreakLength = v; }, "pct", null, ch);
            b.Slider("Random seed", 1, 99, 1, delegate { return s.Seed; }, delegate(double v) { s.Seed = (int)v; }, "int", "New random noise / scratch / streak layout.", ch);

            b.Header("Surface weighting");
            b.Slider("Rough surfaces", 0, 1, 0.01, delegate { return s.RoughInfl; }, delegate(double v) { s.RoughInfl = v; }, "pct", "Reduce on glossy surfaces (needs roughness).", ch);
            b.Slider("Dark albedo", 0, 1, 0.01, delegate { return s.DarkInfl; }, delegate(double v) { s.DarkInfl = v; }, "pct", "Reduce on bright albedo (needs base color).", ch);
            b.Slider("On Mat 1", 0, 1, 0.01, delegate { return s.Mat1; }, delegate(double v) { s.Mat1 = v; }, "pct", "Per-material multiplier, from the Materials tab.", ch);
            b.Slider("On Mat 2 (R)", 0, 1, 0.01, delegate { return s.Mat2; }, delegate(double v) { s.Mat2 = v; }, "pct", null, ch);
            b.Slider("On Mat 3 (G)", 0, 1, 0.01, delegate { return s.Mat3; }, delegate(double v) { s.Mat3 = v; }, "pct", null, ch);
            b.Slider("On Mat 4 (B)", 0, 1, 0.01, delegate { return s.Mat4; }, delegate(double v) { s.Mat4 = v; }, "pct", null, ch);

            b.Header("Output levels");
            b.Slider("Black point", 0, 1, 0.01, delegate { return s.BlackPoint; }, delegate(double v) { s.BlackPoint = v; }, "pct", "Everything below this becomes 0 - cleans faint haze.", ch);
            b.Slider("White point", 0, 1, 0.01, delegate { return s.WhitePoint; }, delegate(double v) { s.WhitePoint = v; }, "pct", "Everything above this reaches Output max.", ch);
            b.Slider("Curve", 0.2, 3, 0.05, delegate { return s.Gamma; }, delegate(double v) { s.Gamma = v; }, "f2", "<1 spreads it out, >1 keeps it to the strongest spots.", ch);
            b.Slider("Output max", 0, 1, 0.01, delegate { return s.OutMax; }, delegate(double v) { s.OutMax = v; }, "pct", "Vanilla VFX maps rarely go to full white.", ch);
            b.Slider("Softness", 0, 8, 0.5, delegate { return s.Softness; }, delegate(double v) { s.Softness = v; }, "px", null, ch);
            b.End();
        }

        private static void CopyLayer(LayerSettings from, LayerSettings to)
        {
            foreach (System.Reflection.FieldInfo f in typeof(LayerSettings).GetFields())
                f.SetValue(to, f.GetValue(from));
        }

        private void BuildExportBox(int x, int y)
        {
            DarkGroupBox g = new DarkGroupBox();
            g.Text = "Export";
            g.SetBounds(x, y, 470, 162);
            g.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            Controls.Add(g);

            Label lw = new Label();
            lw.Text = "Work res:";
            lw.SetBounds(10, 28, 62, 16);
            g.Controls.Add(lw);
            _cmbWorkRes = new ComboBox();
            _cmbWorkRes.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbWorkRes.Items.AddRange(new object[] { "512", "1024", "2048" });
            _cmbWorkRes.SelectedItem = _workRes.ToString();
            _cmbWorkRes.SetBounds(74, 24, 70, 23);
            _cmbWorkRes.SelectedIndexChanged += delegate { OnWorkResChanged(); };
            g.Controls.Add(_cmbWorkRes);
            _tip.SetToolTip(_cmbWorkRes, "Resolution the masks are generated at. 1024 is plenty for most; 2048 is slower to edit.");

            Label ls = new Label();
            ls.Text = "Export size:";
            ls.SetBounds(160, 28, 72, 16);
            g.Controls.Add(ls);
            _cmbSize = new ComboBox();
            _cmbSize.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbSize.Items.AddRange(new object[] { "Auto", "2048", "1024", "512", "256" });
            int es = _loaded != null ? _loaded.ExportSize : 1024;
            _cmbSize.SelectedItem = es == 0 ? "Auto" : es.ToString();
            if (_cmbSize.SelectedIndex < 0) _cmbSize.SelectedIndex = 2;
            _cmbSize.SetBounds(236, 24, 70, 23);
            g.Controls.Add(_cmbSize);

            Label lh = new Label();
            lh.Text = "(masks rarely need full res)";
            lh.SetBounds(312, 28, 154, 16);
            lh.ForeColor = Theme.SubText;
            g.Controls.Add(lh);

            Button b1 = new Button();
            b1.Text = "Export _GLOBAL_MASK";
            b1.SetBounds(10, 58, 150, 30);
            b1.Click += delegate { Export(true, false); };
            g.Controls.Add(b1);
            Button b2 = new Button();
            b2.Text = "Export _VFX";
            b2.SetBounds(166, 58, 110, 30);
            b2.Click += delegate { Export(false, true); };
            g.Controls.Add(b2);
            Button b3 = new Button();
            b3.Text = "Export both";
            b3.SetBounds(282, 58, 100, 30);
            b3.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            b3.Click += delegate { Export(true, true); };
            g.Controls.Add(b3);
            Button bc = new Button();
            bc.Text = "Close";
            bc.SetBounds(388, 58, 72, 30);
            bc.Click += delegate { Close(); };
            g.Controls.Add(bc);

            _btnBake = new Button();
            _btnBake.Text = "Bake from 3D model…";
            _btnBake.SetBounds(10, 96, 150, 28);
            _btnBake.Click += OnBakeClick;
            g.Controls.Add(_btnBake);
            _tip.SetToolTip(_btnBake, "Pick the .fbx / .blend / .obj. Blender bakes real AO, which way every pixel faces and how high it sits\n" +
                "on the model - dirt and mud can then use 'faces up / faces down / low on the model' like Substance.");
            _lblBake = new Label();
            _lblBake.SetBounds(166, 102, 296, 16);
            _lblBake.AutoEllipsis = true;
            _lblBake.ForeColor = Theme.SubText;
            _lblBake.Text = "No model baked - dirt/mud use the 2D maps only";
            g.Controls.Add(_lblBake);

            Label ln = new Label();
            ln.Text = "_GLOBAL_MASK compression: set manually in Workbench (RGB -> ColorHQ).";
            ln.SetBounds(10, 134, 452, 20);
            ln.ForeColor = Theme.SubText;
            g.Controls.Add(ln);
        }

        private void SelectTab(int idx, bool switchView)
        {
            _activeTab = idx;
            for (int i = 0; i < 3; i++)
            {
                _tabs[i].Visible = i == idx;
                bool on = i == idx;
                _tabBtns[i].BackColor = on ? Theme.Accent : Theme.Field;
                _tabBtns[i].ForeColor = on ? Color.White : Theme.Text;
                _tabBtns[i].FlatAppearance.BorderColor = on ? Theme.Accent : Theme.Border;
            }
            if (switchView)
            {
                int v = idx == 0 ? V_MATOVER : (idx == 1 ? V_DIRTOVER : V_MUDOVER);
                if (_cmbView.SelectedIndex != v)
                    _cmbView.SelectedIndex = v;
            }
            OnToolChanged();
        }

        private void SetPaintMat(int m)
        {
            _paintMat = m;
            for (int i = 0; i < 4; i++)
            {
                Button b = _matBtns[i];
                bool on = i == m;
                b.BackColor = on ? MatColors[i] : Theme.Field;
                b.ForeColor = on ? Color.White : Theme.Text;
                b.FlatAppearance.BorderColor = on ? Color.White : MatColors[i];
                b.FlatAppearance.BorderSize = on ? 2 : 1;
            }
        }

        private void OnToolChanged()
        {
            bool brush = _activeTab == 0 && (_tool == T_BRUSH || _tool == T_ERASE);
            _preview.BrushRadius = brush ? _brush : 0;
            _preview.Cursor = _activeTab == 0 ? Cursors.Cross : Cursors.Default;
            if (brush && _cmbView.SelectedIndex == V_REGIONS)
                _cmbView.SelectedIndex = V_MATOVER; // paint isn't visible on the cluster view
            _preview.Invalidate();
        }

        private void RefreshControls()
        {
            foreach (Action a in _refreshers)
                a();
        }

        private void RefreshAll()
        {
            foreach (Action a in _refreshers)
                a();
            foreach (KeyValuePair<LayerSettings, Label> kv in _grungeLabels)
                kv.Value.Text = string.IsNullOrEmpty(kv.Key.GrungePath) ? "(none)" : Path.GetFileName(kv.Key.GrungePath);
            MarkDirty(false, true, true, true);
        }

        // =====================================================================================
        //  lifecycle / recompute
        // =====================================================================================

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
            SetPaintMat(1);
            _status.Refresh();
            LoadSources(true);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveState();
            _timer.Stop();
            base.OnFormClosing(e);
        }

        private void LoadSources(bool first)
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                sbyte[] oldPaint = _paint;
                int ow = _src != null ? _src.W : 0, oh = _src != null ? _src.H : 0;
                if (_reg != null)
                    _pendingAssign = (int[])_reg.Assign.Clone();
                _src = MaskSources.Load(_ctx, _workRes);
                if (_src.LoadBake(MaskPresetStore.BakeDirFor(_ctx.NormalPath)))
                    _lblBake.Text = "Baked: " + Path.GetFileName(_bakeModel.Length > 0 ? _bakeModel : "model") + (_bakeMats.Length > 0 ? "  [" + _bakeMats.Replace("|", ", ") + "]" : "");
                if (first && _loaded != null && _loaded.Paint != null)
                {
                    oldPaint = _loaded.Paint;
                    ow = _loaded.PaintW;
                    oh = _loaded.PaintH;
                }
                if (oldPaint != null && ow > 0)
                    _paint = (ow == _src.W && oh == _src.H) ? oldPaint : MaskOps.ResizeNearest(oldPaint, ow, oh, _src.W, _src.H);
                else
                {
                    _paint = new sbyte[_src.W * _src.H];
                    ClearPaint();
                }
                _undo.Clear();
                _ready = true;
                MarkDirty(true, true, true, true);
                Recompute();
                _status.Text = string.Format("Source {0}x{1}, working at {2}x{3}. {4}", _src.FullW, _src.FullH, _src.W, _src.H,
                    _loaded != null && first ? "Restored your previous settings for this texture set." : "");
            }
            catch (Exception ex)
            {
                _status.Text = "Failed to load source maps: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void ClearPaint()
        {
            if (_paint == null) return;
            for (int i = 0; i < _paint.Length; i++)
                _paint[i] = -1;
        }

        private void MarkDirty(bool regions, bool mask, bool dirt, bool mud)
        {
            _dRegions |= regions;
            _dMask |= mask;
            _dDirt |= dirt;
            _dMud |= mud;
            if (_ready)
            {
                _timer.Stop();
                _timer.Start();
            }
        }

        private static bool UsesMats(LayerSettings s)
        {
            return s.Mat1 < 1 || s.Mat2 < 1 || s.Mat3 < 1 || s.Mat4 < 1;
        }

        private void Recompute()
        {
            if (!_ready || _src == null)
                return;
            try
            {
                if (_dRegions)
                {
                    _dRegions = false;
                    Regions r = null;
                    if (_rs.Source == 1)
                    {
                        if (string.IsNullOrEmpty(_rs.IdMapPath) || !File.Exists(_rs.IdMapPath))
                            _status.Text = "No ID map loaded - showing auto clusters. Use 'Load ID map…'.";
                        else
                        {
                            try { r = Regions.FromIdMap(_src, _rs.IdMapPath); }
                            catch (Exception ex) { _status.Text = "ID map failed: " + ex.Message; }
                        }
                    }
                    if (r == null)
                        r = Regions.Cluster(_src, _rs);
                    if (_pendingAssign != null && _pendingAssign.Length == r.Count)
                        for (int i = 0; i < r.Count; i++)
                            r.Assign[i] = Math.Max(0, Math.Min(3, _pendingAssign[i]));
                    _pendingAssign = null;
                    _reg = r;
                    if (_selRegion >= r.Count) _selRegion = -1;
                    RebuildRegionList();
                    _dMask = true;
                }
                if (_dMask)
                {
                    _dMask = false;
                    _mat = _reg.MaterialLabels(_paint);
                    _presenceSoft = GlobalMaskComposer.Presence(_mat, _src.W, _src.H, _rs.EdgeSoft * _src.ResFactor);
                    _gm = GlobalMaskComposer.Compose(_src, _presenceSoft, _ch);
                    _presence = GlobalMaskComposer.Presence(_mat, _src.W, _src.H, 1.0 * _src.ResFactor);
                    if (UsesMats(_dirt)) _dDirt = true;
                    if (UsesMats(_mud)) _dMud = true;
                    if (_dirtMap == null) _dDirt = true;
                    if (_mudMap == null) _dMud = true;
                }
                if (!_dragging)
                {
                    if (_dDirt) { _dDirt = false; _dirtMap = LayerEval.Evaluate(_src, _dirt, _presence); }
                    if (_dMud) { _dMud = false; _mudMap = LayerEval.Evaluate(_src, _mud, _presence); }
                }
            }
            catch (Exception ex)
            {
                _status.Text = "ERROR: " + ex.Message;
            }
            RenderPreview();
        }

        // =====================================================================================
        //  region list
        // =====================================================================================

        private void RebuildRegionList()
        {
            _regionList.SuspendLayout();
            foreach (Control c in _regionList.Controls)
                c.Dispose();
            _regionList.Controls.Clear();
            _regionCombos.Clear();
            _regionRows.Clear();
            for (int c = 0; c < _reg.Count; c++)
            {
                int ci = c;
                Panel row = new Panel();
                row.SetBounds(0, c * 30, 410, 28);
                row.BackColor = Theme.PanelBg;
                _regionList.Controls.Add(row);

                Panel sw1 = new Panel();
                sw1.SetBounds(6, 7, 14, 14);
                sw1.BackColor = Regions.Palette[c % Regions.MaxRegions];
                row.Controls.Add(sw1);
                Panel sw2 = new Panel();
                sw2.SetBounds(24, 7, 14, 14);
                sw2.BackColor = _reg.MeanColor[c];
                sw2.BorderStyle = BorderStyle.FixedSingle;
                row.Controls.Add(sw2);

                Label l = new Label();
                l.Text = string.Format("{0} {1}   {2:0.0}%", _reg.IsIdMap ? "ID" : "Region", c + 1, _reg.Area[c] * 100);
                l.SetBounds(44, 6, 170, 16);
                l.Cursor = Cursors.Hand;
                l.Click += delegate { SelectRegion(ci == _selRegion ? -1 : ci); };
                row.Controls.Add(l);
                row.Click += delegate { SelectRegion(ci == _selRegion ? -1 : ci); };

                WheelSafeComboBox cb = new WheelSafeComboBox();
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.Items.AddRange(MatNames);
                cb.SelectedIndex = _reg.Assign[c];
                cb.SetBounds(236, 3, 150, 23);
                cb.SelectedIndexChanged += delegate
                {
                    if (_suppress > 0 || _reg == null || ci >= _reg.Count) return;
                    PushUndo();
                    _reg.Assign[ci] = cb.SelectedIndex;
                    MarkDirty(false, true, false, false);
                };
                row.Controls.Add(cb);
                _regionCombos.Add(cb);
                _regionRows.Add(row);
            }
            Theme.Apply(_regionList);
            foreach (ComboBox cb in _regionCombos)
                cb.BackColor = Theme.Field;
            _regionList.ResumeLayout();
            HighlightRows();
        }

        private void RefreshRegionCombos()
        {
            _suppress++;
            for (int c = 0; c < _regionCombos.Count && c < _reg.Count; c++)
                _regionCombos[c].SelectedIndex = _reg.Assign[c];
            _suppress--;
        }

        private void SelectRegion(int r)
        {
            _selRegion = r;
            HighlightRows();
            if (r >= 0 && r < _regionRows.Count)
                _regionList.ScrollControlIntoView(_regionRows[r]);
            RenderPreview();
        }

        private void HighlightRows()
        {
            for (int c = 0; c < _regionRows.Count; c++)
                _regionRows[c].BackColor = c == _selRegion ? Theme.FieldHover : Theme.PanelBg;
        }

        // =====================================================================================
        //  preview interaction
        // =====================================================================================

        private void OnPreviewMouse(int x, int y, MouseButtons b, int phase)
        {
            if (_reg == null || _activeTab != 0)
                return;
            bool inside = x >= 0 && y >= 0 && x < _src.W && y < _src.H;
            if (_tool == T_ASSIGN || _tool == T_FILL)
            {
                if (phase != 0 || !inside)
                    return;
                int lab = _reg.LabelAt(x, y);
                PushUndo();
                if (_tool == T_ASSIGN)
                {
                    _reg.Assign[lab] = _paintMat;
                    // a whole-region assign wins over old paint inside that region
                    for (int i = 0; i < _paint.Length; i++)
                        if (_reg.Labels[i] == lab) _paint[i] = -1;
                    RefreshRegionCombos();
                    _status.Text = string.Format("Region {0} -> {1}", lab + 1, MatNames[_paintMat].Trim());
                }
                else
                {
                    bool[] hit = _reg.ConnectedArea(x, y);
                    int cnt = 0;
                    for (int i = 0; i < hit.Length; i++)
                        if (hit[i]) { _paint[i] = (sbyte)_paintMat; cnt++; }
                    _status.Text = string.Format("Filled {0} px connected to region {1} -> {2}", cnt, lab + 1, MatNames[_paintMat].Trim());
                }
                SelectRegion(lab);
                MarkDirty(false, true, false, false);
                return;
            }
            // brush / erase
            if (phase == 0)
            {
                if (!inside) return;
                PushUndo();
                _dragging = true;
                _strokeLabel = _brushInside ? _reg.LabelAt(x, y) : -1;
                _lastStamp = new Point(x, y);
                Stamp(x, y);
            }
            else if (phase == 1 && _dragging)
            {
                double dx = x - _lastStamp.X, dy = y - _lastStamp.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                int steps = Math.Max(1, (int)(len / Math.Max(1.0, _brush * 0.4)));
                for (int s = 1; s <= steps; s++)
                    Stamp((int)(_lastStamp.X + dx * s / steps), (int)(_lastStamp.Y + dy * s / steps));
                _lastStamp = new Point(x, y);
            }
            else if (phase == 2 && _dragging)
            {
                _dragging = false;
                if (UsesMats(_dirt)) _dDirt = true;
                if (UsesMats(_mud)) _dMud = true;
            }
            MarkDirty(false, true, false, false);
        }

        private void Stamp(int cx, int cy)
        {
            int r = _brush, w = _src.W, h = _src.H;
            sbyte val = _tool == T_ERASE ? (sbyte)-1 : (sbyte)_paintMat;
            int r2 = r * r;
            for (int y = Math.Max(0, cy - r); y <= Math.Min(h - 1, cy + r); y++)
                for (int x = Math.Max(0, cx - r); x <= Math.Min(w - 1, cx + r); x++)
                {
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy > r2) continue;
                    int i = y * w + x;
                    if (_strokeLabel >= 0 && _reg.Labels[i] != _strokeLabel) continue;
                    _paint[i] = val;
                }
        }

        private void OnPreviewPick(int x, int y)
        {
            if (_reg == null) return;
            int lab = _reg.LabelAt(x, y);
            if (lab < 0) return;
            if (_activeTab != 0) SelectTab(0, false);
            SelectRegion(lab);
            _status.Text = string.Format("Picked region {0} ({1:0.0}% of texture) - currently {2}", lab + 1, _reg.Area[lab] * 100, MatNames[_reg.Assign[lab]].Trim());
        }

        private void OnPreviewHover(int x, int y)
        {
            if (_reg == null || x < 0 || y < 0 || x >= _src.W || y >= _src.H)
                return;
            int i = y * _src.W + x;
            string s = string.Format("{0}, {1}   region {2}  ->  {3}{4}", x, y, _reg.Labels[i] + 1, MatNames[_mat != null ? _mat[i] : 0].Trim(), _paint[i] >= 0 ? " (painted)" : "");
            if (_dirtMap != null && _mudMap != null)
                s += string.Format("   |   dirt {0:0}%  mud {1:0}%", _dirtMap[i] * 100, _mudMap[i] * 100);
            _hover.Text = s;
        }

        private void PushUndo()
        {
            if (_reg == null) return;
            _undo.Push(new KeyValuePair<int[], sbyte[]>((int[])_reg.Assign.Clone(), (sbyte[])_paint.Clone()));
            if (_undo.Count > 40)
            {
                // drop the oldest
                KeyValuePair<int[], sbyte[]>[] arr = _undo.ToArray();
                _undo.Clear();
                for (int i = arr.Length - 2; i >= 0; i--)
                    _undo.Push(arr[i]);
            }
        }

        private void Undo()
        {
            if (_undo.Count == 0 || _reg == null)
            {
                _status.Text = "Nothing to undo.";
                return;
            }
            KeyValuePair<int[], sbyte[]> e = _undo.Pop();
            if (e.Key.Length == _reg.Count)
                _reg.Assign = e.Key;
            if (e.Value.Length == _paint.Length)
                _paint = e.Value;
            RefreshRegionCombos();
            MarkDirty(false, true, true, true);
            _status.Text = "Undone.";
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z))
            {
                Undo();
                return true;
            }
            if (!(ActiveControl is ComboBox) && _activeTab == 0)
            {
                if (keyData == Keys.OemOpenBrackets) { _brush = Math.Max(1, _brush - Math.Max(1, _brush / 6)); RefreshAll(); OnToolChanged(); return true; }
                if (keyData == Keys.OemCloseBrackets) { _brush = Math.Min(150, _brush + Math.Max(1, _brush / 6)); RefreshAll(); OnToolChanged(); return true; }
                if (keyData >= Keys.D1 && keyData <= Keys.D4) { SetPaintMat(keyData - Keys.D1); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void OnLoadIdMap(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Images|*.png;*.tif;*.tiff;*.tga;*.jpg;*.jpeg;*.bmp|All files|*.*";
                dlg.Title = "Pick a material ID map (flat colour per material)";
                if (!string.IsNullOrEmpty(_ctx.NormalPath))
                    dlg.InitialDirectory = Path.GetDirectoryName(_ctx.NormalPath);
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                _rs.IdMapPath = dlg.FileName;
                _rs.Source = 1;
                _lblIdMap.Text = Path.GetFileName(dlg.FileName);
                ClearPaint();
                _undo.Clear();
                RefreshAll();
                MarkDirty(true, true, false, false);
            }
        }

        private void LoadGrunge(LayerSettings s, bool isDirt)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Images|*.png;*.tif;*.tiff;*.tga;*.jpg;*.jpeg;*.bmp|All files|*.*";
                dlg.Title = "Pick a tileable grunge / scratch texture";
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                s.GrungePath = dlg.FileName;
                if (s.Grunge <= 0) s.Grunge = 0.6;
                RefreshAll();
                MarkDirty(false, false, isDirt, !isDirt);
            }
        }

        private void OnWorkResChanged()
        {
            int v;
            if (!int.TryParse((string)_cmbWorkRes.SelectedItem, out v) || v == _workRes)
                return;
            _workRes = v;
            _dirtMap = _mudMap = null;
            LoadSources(false);
        }

        // =====================================================================================
        //  rendering
        // =====================================================================================

        private void RenderPreview()
        {
            if (_src == null || _reg == null || _gm == null)
                return;
            int w = _src.W, h = _src.H, n = w * h;
            byte[] r = new byte[n], g = new byte[n], bl = new byte[n];
            int view = _cmbView.SelectedIndex;
            float[] cr = _src.ColR, cg = _src.ColG, cb = _src.ColB;

            switch (view)
            {
                case V_REGIONS:
                    for (int i = 0; i < n; i++)
                    {
                        int lab = _reg.Labels[i];
                        Color c = Regions.Palette[lab % Regions.MaxRegions];
                        float k = (_selRegion >= 0 && lab != _selRegion) ? 0.25f : 1f;
                        r[i] = (byte)(c.R * k); g[i] = (byte)(c.G * k); bl[i] = (byte)(c.B * k);
                    }
                    break;
                case V_MATOVER:
                    for (int i = 0; i < n; i++)
                    {
                        float br, bg, bb;
                        Albedo(i, out br, out bg, out bb);
                        float mr = _gm[0][i], mg = _gm[1][i], mb = _gm[2][i];
                        float sum = Math.Min(1f, mr + mg + mb);
                        // Mat 1 = the texture as-is; Mat 2-4 tinted over it, shaded by its brightness so detail stays visible
                        float shade = 0.45f + 0.8f * (br * 0.3f + bg * 0.59f + bb * 0.11f);
                        float a = 0.6f * sum, inv = sum > 1e-4f ? 1f / sum : 0f;
                        float tr = (mr * 1.0f + mg * 0.15f + mb * 0.15f) * inv * shade;
                        float tg = (mr * 0.2f + mg * 0.9f + mb * 0.45f) * inv * shade;
                        float tb = (mr * 0.15f + mg * 0.2f + mb * 1.0f) * inv * shade;
                        r[i] = B8(br + (tr - br) * a); g[i] = B8(bg + (tg - bg) * a); bl[i] = B8(bb + (tb - bb) * a);
                    }
                    break;
                case V_GM:
                    r = MaskOps.ToByte(_gm[0]); g = MaskOps.ToByte(_gm[1]); bl = MaskOps.ToByte(_gm[2]);
                    break;
                case V_VFX:
                    if (_dirtMap != null) r = MaskOps.ToByte(_dirtMap);
                    if (_mudMap != null) g = MaskOps.ToByte(_mudMap);
                    break;
                case V_DIRTOVER:
                case V_MUDOVER:
                case V_VFXOVER:
                    for (int i = 0; i < n; i++)
                    {
                        float br, bg, bb;
                        Albedo(i, out br, out bg, out bb);
                        float d = (view != V_MUDOVER && _dirtMap != null) ? _dirtMap[i] * 0.85f : 0f;
                        float m = (view != V_DIRTOVER && _mudMap != null) ? _mudMap[i] * 0.85f : 0f;
                        // same colours as the file: dirt = red, mud = green, over the texture
                        br += (1f - br) * d; bg -= bg * d * 0.8f; bb -= bb * d * 0.8f;
                        br -= br * m * 0.8f; bg += (1f - bg) * m; bb -= bb * m * 0.8f;
                        r[i] = B8(br); g[i] = B8(bg); bl[i] = B8(bb);
                    }
                    break;
                case V_DIRT:
                    if (_dirtMap != null) r = MaskOps.ToByte(_dirtMap);
                    break;
                case V_MUD:
                    if (_mudMap != null) g = MaskOps.ToByte(_mudMap);
                    break;
                case V_RELIEF:
                {
                    LayerSettings ls = _activeTab == 2 ? _mud : _dirt;
                    float[] rel = _src.Relief(ls.Edges > ls.Cavity ? ls.EdgeScale : ls.CavityScale);
                    for (int i = 0; i < n; i++)
                        r[i] = B8(0.5f + rel[i] * 0.5f);
                    g = r; bl = r;
                    break;
                }
                case V_OCC:
                {
                    float[] o = _src.Occlusion();
                    for (int i = 0; i < n; i++)
                        r[i] = B8(1f - o[i]);
                    g = r; bl = r;
                    break;
                }
                case V_COLOR:
                    if (cr != null) { r = MaskOps.ToByte(cr); g = MaskOps.ToByte(cg); bl = MaskOps.ToByte(cb); }
                    break;
                case V_NORMAL:
                    for (int i = 0; i < n; i++)
                    {
                        float nx = _src.Nx[i], ny = _src.Ny[i];
                        r[i] = B8(nx * 0.5f + 0.5f);
                        g[i] = B8(ny * 0.5f + 0.5f);
                        bl[i] = B8((float)Math.Sqrt(Math.Max(0, 1 - nx * nx - ny * ny)) * 0.5f + 0.5f);
                    }
                    break;
            }
            _preview.Image = Packer.Compose(w, h, r, 0, g, 0, bl, 0, null, 255);
        }

        // Base colour, slightly darkened so the tints read; mid grey without a base colour map.
        private void Albedo(int i, out float r, out float g, out float b)
        {
            if (_src.ColR == null) { r = g = b = 0.45f; return; }
            r = _src.ColR[i] * 0.85f; g = _src.ColG[i] * 0.85f; b = _src.ColB[i] * 0.85f;
        }

        private static byte B8(float v)
        {
            return (byte)(v <= 0f ? 0 : (v >= 1f ? 255 : (int)(v * 255f + 0.5f)));
        }

        // =====================================================================================
        //  export / persistence
        // =====================================================================================

        private Size GetExportSize()
        {
            string sel = (string)_cmbSize.SelectedItem;
            if (sel == "Auto")
                return new Size(_src.FullW, _src.FullH);
            int cap = int.Parse(sel);
            int max = Math.Max(_src.FullW, _src.FullH);
            if (max <= cap)
                return new Size(_src.FullW, _src.FullH);
            double s = (double)cap / max;
            return new Size(Math.Max(1, (int)Math.Round(_src.FullW * s)), Math.Max(1, (int)Math.Round(_src.FullH * s)));
        }

        private void Export(bool globalMask, bool vfx)
        {
            if (_src == null || _gm == null)
            {
                _status.Text = "Sources not loaded yet.";
                return;
            }
            Cursor = Cursors.WaitCursor;
            try
            {
                _dragging = false;
                Recompute(); // flush anything still pending on the debounce timer
                Size t = GetExportSize();
                int tw = t.Width, th = t.Height, w = _src.W, h = _src.H;
                string dir = string.IsNullOrEmpty(_ctx.OutDir) ? Path.GetDirectoryName(_ctx.NormalPath) : _ctx.OutDir;
                Directory.CreateDirectory(dir);
                string bn = string.IsNullOrEmpty(_ctx.BaseName) ? "Texture" : _ctx.BaseName;
                List<string> saved = new List<string>();
                if (globalMask)
                {
                    byte[] mr = MaskOps.ToByte(MaskOps.Resize(_gm[0], w, h, tw, th));
                    byte[] mg = MaskOps.ToByte(MaskOps.Resize(_gm[1], w, h, tw, th));
                    byte[] mb = MaskOps.ToByte(MaskOps.Resize(_gm[2], w, h, tw, th));
                    string path = Path.Combine(dir, bn + "_GLOBAL_MASK.tif");
                    using (Bitmap outBmp = Packer.Compose(tw, th, mr, 0, mg, 0, mb, 0, null, 255))
                        Packer.SaveTiffLzw(outBmp, path);
                    saved.Add(Path.GetFileName(path));
                }
                if (vfx)
                {
                    byte[] dr = MaskOps.ToByte(MaskOps.Resize(_dirtMap, w, h, tw, th));
                    byte[] dg = MaskOps.ToByte(MaskOps.Resize(_mudMap, w, h, tw, th));
                    string path = Path.Combine(dir, bn + "_VFX.tif");
                    using (Bitmap outBmp = Packer.Compose(tw, th, dr, 0, dg, 0, null, 0, null, 255))
                        Packer.SaveTiffLzw(outBmp, path);
                    saved.Add(Path.GetFileName(path));
                }
                SaveState();
                _status.Text = string.Format("Saved {0}  ({1}x{2}, LZW) to {3}", string.Join(" + ", saved.ToArray()), tw, th, dir);
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

        // =====================================================================================
        //  3D model bake
        // =====================================================================================

        private void OnBakeClick(object sender, EventArgs e)
        {
            if (_baking || _src == null)
                return;
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
            }
            BlenderBaker.RememberBlender(blender);

            string model;
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "Pick the 3D model these textures belong to";
                dlg.Filter = "3D models|*.fbx;*.blend;*.obj|All files|*.*";
                string known = _bakeModel.Length > 0 && File.Exists(_bakeModel) ? _bakeModel
                    : (!string.IsNullOrEmpty(_ctx.ModelPath) && File.Exists(_ctx.ModelPath) ? _ctx.ModelPath : null);
                if (known != null)
                {
                    dlg.InitialDirectory = Path.GetDirectoryName(known);
                    dlg.FileName = Path.GetFileName(known);
                }
                else
                {
                    // textures usually sit a folder or two below the model
                    string d = Path.GetDirectoryName(_ctx.NormalPath);
                    DirectoryInfo up = Directory.GetParent(d);
                    dlg.InitialDirectory = up != null ? up.FullName : d;
                }
                if (dlg.ShowDialog(this) != DialogResult.OK)
                    return;
                model = dlg.FileName;
            }

            int tile = BlenderBaker.TileFromName(!string.IsNullOrEmpty(_ctx.BasePath) ? _ctx.BasePath : _ctx.NormalPath);
            SetBaking(true, "Reading the model in Blender...");
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                List<BlenderBaker.MaterialInfo> mats = null;
                string err = null;
                try { mats = BlenderBaker.Probe(blender, model); }
                catch (Exception ex) { err = ex.Message; }
                BeginInvoke((Action)delegate { AfterProbe(blender, model, tile, mats, err); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AfterProbe(string blender, string model, int tile, List<BlenderBaker.MaterialInfo> mats, string err)
        {
            if (err != null || mats == null || mats.Count == 0)
            {
                SetBaking(false, "Bake failed: " + (err ?? "no UV-mapped meshes in the model."));
                MessageBox.Show(this, err ?? "No UV-mapped meshes found in the model.", "Bake from 3D model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            List<string> previous = model == _bakeModel && _bakeMats.Length > 0 ? new List<string>(_bakeMats.Split('|')) : null;
            List<string> chosen;
            using (MaterialPickDialog pick = new MaterialPickDialog(mats, tile, Path.GetFileName(_ctx.BasePath ?? _ctx.NormalPath), previous))
            {
                if (pick.ShowDialog(this) != DialogResult.OK)
                {
                    SetBaking(false, "Bake cancelled.");
                    return;
                }
                chosen = pick.Selected;
            }
            if (chosen.Count == 0)
            {
                SetBaking(false, "Bake cancelled - no materials picked.");
                return;
            }
            string dir = MaskPresetStore.BakeDirFor(_ctx.NormalPath);
            int w = _src.W, h = _src.H;
            SetBaking(true, "Baking in Blender (tile " + tile + ")...");
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                string berr = null;
                try
                {
                    BlenderBaker.Bake(blender, model, dir, w, h, tile, chosen, delegate(string line)
                    {
                        string msg = null;
                        if (line.StartsWith("STEP height")) msg = "Baking in Blender: height...";
                        else if (line.StartsWith("STEP normal")) msg = "Baking in Blender: which way surfaces face...";
                        else if (line.StartsWith("STEP ao")) msg = "Baking in Blender: ambient occlusion (the slow one)...";
                        if (msg != null)
                            BeginInvoke((Action)delegate { _status.Text = msg; });
                    });
                }
                catch (Exception ex) { berr = ex.Message; }
                BeginInvoke((Action)delegate { AfterBake(model, chosen, dir, berr); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void AfterBake(string model, List<string> chosen, string dir, string err)
        {
            if (err != null)
            {
                SetBaking(false, "Bake failed.");
                MessageBox.Show(this, err, "Bake from 3D model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool first = !_src.HasBake;
            if (!_src.LoadBake(dir))
            {
                SetBaking(false, "Bake produced no maps.");
                return;
            }
            _bakeModel = model;
            _bakeMats = string.Join("|", chosen.ToArray());
            _lblBake.Text = "Baked: " + Path.GetFileName(model) + "  [" + string.Join(", ", chosen.ToArray()) + "]";
            // first bake: give the layers their model-aware terms, unless the user already set some
            if (first)
            {
                if (_dirt.FaceUp == 0 && _dirt.FaceDown == 0 && _dirt.Low == 0)
                    _dirt.FaceUp = 0.3;
                if (_mud.FaceUp == 0 && _mud.FaceDown == 0 && _mud.Low == 0)
                {
                    _mud.FaceDown = 0.6; _mud.Low = 0.8; _mud.LowRange = 0.35; _mud.Confine = 0.85; _mud.Gradient = 0;
                }
            }
            SaveState();
            RefreshAll();
            SetBaking(false, "Baked from " + Path.GetFileName(model) + " - dirt and mud now use the real model ('On the 3D model' in the Dirt / Mud tabs).");
        }

        private void SetBaking(bool on, string status)
        {
            _baking = on;
            _btnBake.Enabled = !on;
            _btnBake.Text = on ? "Baking…" : "Bake from 3D model…";
            Cursor = on ? Cursors.AppStarting : Cursors.Default;
            _status.Text = status;
        }

        private void SaveState()
        {
            if (_src == null)
                return;
            MaskPresetStore.State st = new MaskPresetStore.State();
            st.Regions = _rs;
            st.Channels = _ch;
            st.Dirt = _dirt;
            st.Mud = _mud;
            st.WorkRes = _workRes;
            string sel = (string)_cmbSize.SelectedItem;
            st.ExportSize = sel == "Auto" ? 0 : int.Parse(sel);
            if (_reg != null)
                st.Assign = _reg.Assign;
            st.Paint = _paint;
            st.PaintW = _src.W;
            st.PaintH = _src.H;
            st.BakeModel = _bakeModel;
            st.BakeMaterials = _bakeMats;
            MaskPresetStore.Save(_ctx.NormalPath, st);
        }
    }

    // ComboBox that passes the mouse wheel to its scrolling parent unless the list is open.
    public class WheelSafeComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A && !DroppedDown)
            {
                Control p = Parent;
                while (p != null && !(p is ScrollableControl && ((ScrollableControl)p).AutoScroll))
                    p = p.Parent;
                if (p != null)
                {
                    ScrollableControl sc = (ScrollableControl)p;
                    int delta = (short)((long)m.WParam >> 16);
                    sc.AutoScrollPosition = new Point(0, Math.Max(0, -sc.AutoScrollPosition.Y - delta));
                }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
