using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    // Zoomable preview: wheel = zoom at cursor, right-drag = pan, double-click = fit.
    // Left button events are reported in image pixel coordinates; a right click without drag raises PickClick.
    public class MaskPreview : Control
    {
        private Bitmap _img;
        private float _zoom = 1f;
        private PointF _pan;            // image-space point shown at the control centre
        private bool _fit = true;
        private Point _dragStart, _lastMouse;
        private bool _rightDown, _rightMoved;
        private Point _cursor = new Point(-1, -1);

        public int BrushRadius;         // image px; 0 = no brush cursor
        public event Action<int, int, MouseButtons, int> ImageMouse; // x, y, button, phase (0 down, 1 move, 2 up)
        public event Action<int, int> PickClick;
        public event Action<int, int> HoverPixel;

        public MaskPreview()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.ThumbBg;
        }

        public Bitmap Image
        {
            get { return _img; }
            set
            {
                bool sizeChanged = _img == null || value == null || _img.Width != value.Width || _img.Height != value.Height;
                if (_img != null && _img != value)
                    _img.Dispose();
                _img = value;
                if (sizeChanged)
                    _fit = true;
                Invalidate();
            }
        }

        public void FitToView()
        {
            _fit = true;
            Invalidate();
        }

        private void ApplyFit()
        {
            if (_img == null || Width < 2 || Height < 2)
                return;
            _zoom = Math.Min((float)Width / _img.Width, (float)Height / _img.Height);
            _pan = new PointF(_img.Width / 2f, _img.Height / 2f);
        }

        private PointF ToImage(Point p)
        {
            return new PointF(_pan.X + (p.X - Width / 2f) / _zoom, _pan.Y + (p.Y - Height / 2f) / _zoom);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_img == null)
                return;
            if (_fit)
                ApplyFit();
            float dw = _img.Width * _zoom, dh = _img.Height * _zoom;
            float ox = Width / 2f - _pan.X * _zoom, oy = Height / 2f - _pan.Y * _zoom;
            g.InterpolationMode = _zoom >= 1.5f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_img, new RectangleF(ox, oy, dw, dh));
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            if (BrushRadius > 0 && _cursor.X >= 0)
            {
                float r = BrushRadius * _zoom;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen p = new Pen(Color.FromArgb(220, 255, 255, 255), 1.5f))
                    g.DrawEllipse(p, _cursor.X - r, _cursor.Y - r, 2 * r, 2 * r);
                using (Pen p = new Pen(Color.FromArgb(200, 0, 0, 0), 1f))
                    g.DrawEllipse(p, _cursor.X - r - 1.5f, _cursor.Y - r - 1.5f, 2 * r + 3, 2 * r + 3);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_img == null)
                return;
            if (_fit)
                ApplyFit();
            _fit = false;
            PointF before = ToImage(e.Location);
            float f = e.Delta > 0 ? 1.25f : 0.8f;
            float minZ = Math.Min((float)Width / _img.Width, (float)Height / _img.Height) * 0.5f;
            _zoom = Math.Max(minZ, Math.Min(32f, _zoom * f));
            // keep the pixel under the cursor fixed
            _pan = new PointF(before.X - (e.X - Width / 2f) / _zoom, before.Y - (e.Y - Height / 2f) / _zoom);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            Focus(); // so the wheel reaches us without a click first
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _rightDown = true;
                _rightMoved = false;
                _dragStart = e.Location;
                if (_fit) ApplyFit();
                _fit = false;
            }
            else if (e.Button == MouseButtons.Left)
                RaiseImage(e.Location, MouseButtons.Left, 0);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _cursor = e.Location;
            if (_rightDown)
            {
                if (Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) > 3)
                    _rightMoved = true;
                _pan = new PointF(_pan.X - (e.X - _lastMouse.X) / _zoom, _pan.Y - (e.Y - _lastMouse.Y) / _zoom);
            }
            else if (e.Button == MouseButtons.Left)
                RaiseImage(e.Location, MouseButtons.Left, 1);
            _lastMouse = e.Location;
            if (HoverPixel != null && _img != null)
            {
                PointF ip = ToImage(e.Location);
                HoverPixel((int)Math.Floor(ip.X), (int)Math.Floor(ip.Y));
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_rightDown && (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle))
            {
                _rightDown = false;
                if (!_rightMoved && e.Button == MouseButtons.Right && PickClick != null && _img != null)
                {
                    PointF ip = ToImage(e.Location);
                    PickClick((int)Math.Floor(ip.X), (int)Math.Floor(ip.Y));
                }
            }
            else if (e.Button == MouseButtons.Left)
                RaiseImage(e.Location, MouseButtons.Left, 2);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _cursor = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            if (MouseButtons == MouseButtons.None && ModifierKeys == Keys.None && BrushRadius == 0)
                FitToView();
        }

        private void RaiseImage(Point p, MouseButtons b, int phase)
        {
            if (_img == null || ImageMouse == null)
                return;
            PointF ip = ToImage(p);
            ImageMouse((int)Math.Floor(ip.X), (int)Math.Floor(ip.Y), b, phase);
        }
    }

    // TrackBar that passes the mouse wheel to its scrolling parent instead of changing value.
    public class WheelSafeTrackBar : TrackBar
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A)
            {
                Control p = Parent;
                while (p != null && !(p is ScrollableControl && ((ScrollableControl)p).AutoScroll))
                    p = p.Parent;
                if (p != null)
                {
                    ScrollableControl sc = (ScrollableControl)p;
                    int delta = (short)((long)m.WParam >> 16);
                    int y = -sc.AutoScrollPosition.Y - delta;
                    sc.AutoScrollPosition = new Point(0, Math.Max(0, y));
                }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
