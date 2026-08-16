using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // One source-map slot: thumbnail, browse/clear, drag-drop, plus a host panel for per-slot options.
    public class TextureSlot : Panel
    {
        private PictureBox _thumb;
        private Label _title;
        private Label _info;
        private Button _browse;
        private Button _clear;
        private FlowLayoutPanel _extras;
        private ToolTip _tip;

        private string _imagePath;
        private Size _imageSize;
        private bool _dragOver;
        private int _state; // 0 = empty, 1 = loaded, 2 = error

        public event EventHandler SlotChanged;

        public string ImagePath { get { return _imagePath; } }
        public Size ImageSize { get { return _imageSize; } }
        public bool HasImage { get { return !string.IsNullOrEmpty(_imagePath); } }
        public FlowLayoutPanel Extras { get { return _extras; } }

        public TextureSlot(string title)
        {
            Size = new Size(464, 96);
            BackColor = Theme.PanelBg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            _tip = new ToolTip();

            _thumb = new PictureBox();
            _thumb.SetBounds(6, 6, 82, 82);
            _thumb.SizeMode = PictureBoxSizeMode.Zoom;
            _thumb.BackColor = Theme.ThumbBg;
            _thumb.Paint += OnThumbPaint;

            _title = new Label();
            _title.Text = title;
            _title.AutoSize = true;
            _title.Location = new Point(96, 8);
            _title.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            _title.ForeColor = Theme.Text;

            _browse = new Button();
            _browse.Text = "Browse…";
            _browse.SetBounds(326, 4, 70, 24);
            _browse.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _browse.Click += OnBrowseClick;

            _clear = new Button();
            _clear.Text = "Clear";
            _clear.SetBounds(400, 4, 56, 24);
            _clear.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _clear.Click += delegate { ClearImage(); };

            _info = new Label();
            _info.SetBounds(96, 32, 356, 16);
            _info.ForeColor = Theme.SubText;
            _info.AutoEllipsis = true;
            _info.Text = "(empty - browse or drop a file)";

            _extras = new FlowLayoutPanel();
            _extras.SetBounds(92, 54, 366, 36);
            _extras.FlowDirection = FlowDirection.LeftToRight;
            _extras.WrapContents = false;

            Controls.Add(_thumb);
            Controls.Add(_title);
            Controls.Add(_browse);
            Controls.Add(_clear);
            Controls.Add(_info);
            Controls.Add(_extras);

            WireDnd(this);
            WireDnd(_thumb);
            WireDnd(_title);
            WireDnd(_info);
            WireDnd(_extras);
        }

        public void ApplyTheme()
        {
            BackColor = Theme.PanelBg;
            _title.ForeColor = Theme.Text;
            _thumb.BackColor = Theme.ThumbBg;
            if (_state == 1) _info.ForeColor = Theme.Good;
            else if (_state == 2) _info.ForeColor = Theme.Bad;
            else _info.ForeColor = Theme.SubText;
            if (HasImage)
                RefreshThumb();
            Invalidate();
            _thumb.Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(_dragOver ? Theme.Accent : Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        private void OnThumbPaint(object sender, PaintEventArgs e)
        {
            if (_thumb.Image == null)
                TextRenderer.DrawText(e.Graphics, "drop\nimage", Font,
                    new Rectangle(0, 0, _thumb.Width, _thumb.Height),
                    _dragOver ? Theme.Accent : Theme.SubText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using (Pen p = new Pen(_dragOver ? Theme.Accent : Theme.Border))
                e.Graphics.DrawRectangle(p, 0, 0, _thumb.Width - 1, _thumb.Height - 1);
        }

        private void WireDnd(Control c)
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnterFile;
            c.DragLeave += OnDragLeaveFile;
            c.DragDrop += OnDragDropFile;
        }

        private void SetDragOver(bool on)
        {
            if (_dragOver == on)
                return;
            _dragOver = on;
            Invalidate();
            _thumb.Invalidate();
        }

        private void OnDragEnterFile(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                SetDragOver(true);
            }
        }

        private void OnDragLeaveFile(object sender, EventArgs e)
        {
            SetDragOver(false);
        }

        private void OnDragDropFile(object sender, DragEventArgs e)
        {
            SetDragOver(false);
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
                LoadImage(files[0]);
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Filter = "Images|*.png;*.tif;*.tiff;*.tga;*.jpg;*.jpeg;*.bmp|All files|*.*";
                dlg.Title = "Select " + _title.Text;
                if (HasImage)
                {
                    try { dlg.InitialDirectory = Path.GetDirectoryName(_imagePath); }
                    catch (Exception) { }
                }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    LoadImage(dlg.FileName);
            }
        }

        public void LoadImage(string path)
        {
            try
            {
                Bitmap full = Packer.LoadBitmap(path);
                _imageSize = full.Size;
                Bitmap thumb = MakeThumb(full, 80);
                full.Dispose();

                if (_thumb.Image != null)
                    _thumb.Image.Dispose();
                _thumb.Image = thumb;
                _imagePath = path;
                _state = 1;
                _info.Text = string.Format("{0}  ({1}x{2})", Path.GetFileName(path), _imageSize.Width, _imageSize.Height);
                _info.ForeColor = Theme.Good;
                _tip.SetToolTip(_info, path);
                _tip.SetToolTip(_thumb, path);
                if (SlotChanged != null)
                    SlotChanged(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _state = 2;
                _info.Text = "Load failed: " + ex.Message;
                _info.ForeColor = Theme.Bad;
            }
        }

        public void ClearImage()
        {
            _imagePath = null;
            _imageSize = Size.Empty;
            _state = 0;
            if (_thumb.Image != null)
            {
                _thumb.Image.Dispose();
                _thumb.Image = null;
            }
            _info.Text = "(empty - browse or drop a file)";
            _info.ForeColor = Theme.SubText;
            _thumb.Invalidate();
            if (SlotChanged != null)
                SlotChanged(this, EventArgs.Empty);
        }

        private void RefreshThumb()
        {
            try
            {
                Bitmap full = Packer.LoadBitmap(_imagePath);
                Bitmap thumb = MakeThumb(full, 80);
                full.Dispose();
                if (_thumb.Image != null)
                    _thumb.Image.Dispose();
                _thumb.Image = thumb;
            }
            catch (Exception) { }
        }

        private static Bitmap MakeThumb(Bitmap src, int box)
        {
            double s = Math.Min((double)box / src.Width, (double)box / src.Height);
            if (s > 1.0) s = 1.0;
            int tw = Math.Max(1, (int)(src.Width * s));
            int th = Math.Max(1, (int)(src.Height * s));
            Bitmap t = new Bitmap(tw, th, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(t))
            {
                g.Clear(Theme.ThumbBg);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(src, new Rectangle(0, 0, tw, th), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
            }
            return t;
        }
    }
}
