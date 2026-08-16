using System;
using System.Drawing;
using System.Windows.Forms;

namespace ReforgerTexturePacker
{
    public static class Theme
    {
        public static bool Dark = true;
        public static Color Bg, HeaderBg, PanelBg, Field, FieldHover, Border, Text, SubText, Accent, Good, Bad, ThumbBg;

        static Theme() { SetMode(true); }

        public static void SetMode(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Bg = Color.FromArgb(30, 30, 30);
                HeaderBg = Color.FromArgb(38, 38, 40);
                PanelBg = Color.FromArgb(40, 40, 42);
                Field = Color.FromArgb(56, 56, 60);
                FieldHover = Color.FromArgb(72, 72, 78);
                Border = Color.FromArgb(82, 82, 88);
                Text = Color.FromArgb(232, 232, 232);
                SubText = Color.FromArgb(150, 150, 156);
                Accent = Color.FromArgb(0, 140, 230);
                Good = Color.FromArgb(110, 220, 140);
                Bad = Color.FromArgb(245, 110, 100);
                ThumbBg = Color.FromArgb(22, 22, 22);
            }
            else
            {
                Bg = Color.FromArgb(244, 244, 246);
                HeaderBg = Color.FromArgb(252, 252, 253);
                PanelBg = Color.FromArgb(252, 252, 253);
                Field = Color.FromArgb(255, 255, 255);
                FieldHover = Color.FromArgb(234, 238, 243);
                Border = Color.FromArgb(196, 198, 204);
                Text = Color.FromArgb(28, 30, 33);
                SubText = Color.FromArgb(108, 112, 120);
                Accent = Color.FromArgb(0, 110, 200);
                Good = Color.FromArgb(22, 130, 60);
                Bad = Color.FromArgb(198, 52, 42);
                ThumbBg = Color.FromArgb(228, 229, 232);
            }
        }

        // Recursively restyles buttons/textboxes/combos/numerics; labels inherit ForeColor from the form.
        public static void Apply(Control root)
        {
            foreach (Control c in root.Controls)
            {
                Button b = c as Button;
                TextBox t = c as TextBox;
                ComboBox cb = c as ComboBox;
                NumericUpDown n = c as NumericUpDown;
                GroupBox gb = c as GroupBox;
                if (b != null)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Field;
                    b.ForeColor = Text;
                    b.FlatAppearance.BorderColor = Border;
                    b.FlatAppearance.MouseOverBackColor = FieldHover;
                    b.FlatAppearance.MouseDownBackColor = Bg;
                }
                else if (t != null)
                {
                    t.BackColor = Field;
                    t.ForeColor = Text;
                    t.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (cb != null)
                {
                    cb.FlatStyle = FlatStyle.Flat;
                    cb.BackColor = Field;
                    cb.ForeColor = Text;
                }
                else if (n != null)
                {
                    n.BackColor = Field;
                    n.ForeColor = Text;
                }
                else if (gb != null)
                {
                    gb.BackColor = Bg;
                    gb.ForeColor = Text;
                }
                Apply(c);
            }
        }
    }

    // GroupBox with a flat themed border and title - the default rendering is light-theme only.
    public class DarkGroupBox : GroupBox
    {
        public DarkGroupBox()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            Size ts = TextRenderer.MeasureText(g, Text, Font);
            int half = ts.Height / 2;
            using (Pen p = new Pen(Theme.Border))
                g.DrawRectangle(p, 0, half, Width - 1, Height - 1 - half);
            using (SolidBrush b = new SolidBrush(BackColor))
                g.FillRectangle(b, 8, 0, ts.Width + 4, ts.Height);
            TextRenderer.DrawText(g, Text, Font, new Point(10, 0), ForeColor);
        }
    }
}
