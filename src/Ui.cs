using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexScheduler
{
    // ============================================================ 主题

    public enum BtnKind { Primary, Secondary, Danger, Ghost, Chip }

    public static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(243, 244, 246);
        public static readonly Color Card = Color.White;
        public static readonly Color Header = Color.FromArgb(249, 250, 251);
        public static readonly Color Border = Color.FromArgb(229, 231, 235);
        public static readonly Color BorderStrong = Color.FromArgb(209, 213, 219);
        public static readonly Color Text = Color.FromArgb(17, 24, 39);
        public static readonly Color Muted = Color.FromArgb(107, 114, 128);
        public static readonly Color Subtle = Color.FromArgb(156, 163, 175);
        public static readonly Color Hover = Color.FromArgb(243, 244, 246);
        public static readonly Color Pressed = Color.FromArgb(229, 231, 235);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
        public static readonly Color AccentPressed = Color.FromArgb(30, 64, 175);
        public static readonly Color AccentSoft = Color.FromArgb(239, 246, 255);
        public static readonly Color AccentSoft2 = Color.FromArgb(219, 234, 254);
        public static readonly Color Ok = Color.FromArgb(22, 163, 74);
        public static readonly Color OkSoft = Color.FromArgb(240, 253, 244);
        public static readonly Color Warn = Color.FromArgb(217, 119, 6);
        public static readonly Color WarnSoft = Color.FromArgb(255, 251, 235);
        public static readonly Color Bad = Color.FromArgb(220, 38, 38);
        public static readonly Color BadSoft = Color.FromArgb(254, 242, 242);

        public const string FontName = "Microsoft YaHei UI";
        public static readonly Font Base = new Font(FontName, 9F);
        public static readonly Font Bold = new Font(FontName, 9F, FontStyle.Bold);
        public static readonly Font Small = new Font(FontName, 8.25F);
        public static readonly Font H2 = new Font(FontName, 10.5F, FontStyle.Bold);
        public static readonly Font Title = new Font(FontName, 12F, FontStyle.Bold);
        public static readonly Font Mono = new Font("Consolas", 9F);

        /// <summary>系统 DPI 缩放系数。字体按“磅”自动随 DPI 变大，像素尺寸统一用 S() 换算，避免高 DPI 下文字被截断</summary>
        public static float Scale = 1f;

        public static void InitScale()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = Math.Max(1f, g.DpiX / 96f);
            }
            catch { Scale = 1f; }
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
    }

    // ============================================================ 控件工厂

    public static class Ui
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string app, string idList);

        const int EM_SETCUEBANNER = 0x1501;

        static int S(int v) { return Theme.S(v); }

        public static Button Btn(string text, BtnKind kind)
        {
            var b = new Button();
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.FlatStyle = FlatStyle.Flat;
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.Font = Theme.Base;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.Anchor = AnchorStyles.Left;
            if (kind == BtnKind.Chip)
            {
                b.MinimumSize = new Size(S(40), S(26));
                b.Padding = new Padding(S(4), 0, S(4), 0);
                b.Margin = new Padding(0, S(2), S(6), S(2));
            }
            else
            {
                b.MinimumSize = new Size(S(64), S(32));
                b.Padding = new Padding(S(8), 0, S(8), 0);
                b.Margin = new Padding(0, 0, S(8), 0);
            }
            Style(b, kind);
            return b;
        }

        public static void Style(Button b, BtnKind kind)
        {
            var fa = b.FlatAppearance;
            fa.BorderSize = 1;
            b.EnabledChanged -= PrimaryEnabledChanged;
            b.Font = kind == BtnKind.Chip ? Theme.Small : (kind == BtnKind.Primary ? Theme.Bold : Theme.Base);
            switch (kind)
            {
                case BtnKind.Primary:
                    b.BackColor = Theme.Accent; b.ForeColor = Color.White;
                    fa.BorderColor = Theme.Accent; fa.MouseOverBackColor = Theme.AccentHover; fa.MouseDownBackColor = Theme.AccentPressed;
                    break;
                case BtnKind.Danger:
                    b.BackColor = Theme.Card; b.ForeColor = Theme.Bad;
                    fa.BorderColor = Theme.BorderStrong; fa.MouseOverBackColor = Theme.BadSoft; fa.MouseDownBackColor = Color.FromArgb(254, 226, 226);
                    break;
                case BtnKind.Ghost:
                    b.BackColor = Theme.Card; b.ForeColor = Theme.Accent;
                    fa.BorderSize = 0; fa.BorderColor = Theme.Card; fa.MouseOverBackColor = Theme.AccentSoft; fa.MouseDownBackColor = Theme.AccentSoft2;
                    break;
                case BtnKind.Chip:
                    b.BackColor = Theme.AccentSoft; b.ForeColor = Theme.Accent;
                    fa.BorderColor = Theme.AccentSoft2; fa.MouseOverBackColor = Theme.AccentSoft2; fa.MouseDownBackColor = Theme.AccentSoft2;
                    break;
                default:
                    b.BackColor = Theme.Card; b.ForeColor = Theme.Text;
                    fa.BorderColor = Theme.BorderStrong; fa.MouseOverBackColor = Theme.Hover; fa.MouseDownBackColor = Theme.Pressed;
                    break;
            }
            if (kind == BtnKind.Primary)
            {
                // 禁用态的主按钮不要继续显示成深蓝底灰字
                b.EnabledChanged += PrimaryEnabledChanged;
                PrimaryEnabledChanged(b, EventArgs.Empty);
            }
        }

        static void PrimaryEnabledChanged(object sender, EventArgs e)
        {
            var b = (Button)sender;
            b.BackColor = b.Enabled ? Theme.Accent : Theme.AccentSoft2;
            b.FlatAppearance.BorderColor = b.BackColor;
        }

        public static Label Lbl(string text, Color color, Font font)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = color;
            l.Font = font ?? Theme.Base;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 0, S(6), 0);
            return l;
        }

        /// <summary>表单左侧的字段名</summary>
        public static Label FieldLabel(string text)
        {
            var l = Lbl(text, Theme.Muted, Theme.Base);
            l.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            l.Margin = new Padding(0, S(7), S(10), S(14));
            return l;
        }

        public static FlowLayoutPanel Flow()
        {
            var f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = false;
            f.FlowDirection = FlowDirection.LeftToRight;
            f.Margin = new Padding(0);
            f.Padding = new Padding(0);
            f.BackColor = Color.Transparent;
            f.Anchor = AnchorStyles.Left;
            return f;
        }

        public static TableLayoutPanel Table(int cols, int rows)
        {
            var t = new TableLayoutPanel();
            t.ColumnCount = cols;
            t.RowCount = rows;
            t.Margin = new Padding(0);
            t.Padding = new Padding(0);
            t.BackColor = Color.Transparent;
            return t;
        }

        public static Panel Separator()
        {
            var p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Theme.Border;
            p.Margin = new Padding(0);
            return p;
        }

        public static void Cue(TextBox t, string text)
        {
            EventHandler apply = delegate { try { SendMessage(t.Handle, EM_SETCUEBANNER, (IntPtr)1, text); } catch { } };
            if (t.IsHandleCreated) apply(t, EventArgs.Empty);
            t.HandleCreated += apply;
        }

        /// <summary>ListView 用资源管理器风格（悬停高亮、现代复选框）</summary>
        public static void ExplorerTheme(Control c)
        {
            EventHandler apply = delegate { try { SetWindowTheme(c.Handle, "Explorer", null); } catch { } };
            if (c.IsHandleCreated) apply(c, EventArgs.Empty);
            c.HandleCreated += apply;
        }

        public static void DoubleBuffer(Control c)
        {
            try
            {
                var pi = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(c, true, null);
            }
            catch { }
        }

        public static DateTimePicker Picker(string format, int width)
        {
            var d = new DateTimePicker();
            d.Format = DateTimePickerFormat.Custom;
            d.CustomFormat = format;
            d.Font = Theme.Base;
            d.Width = S(width);
            d.Anchor = AnchorStyles.Left;
            d.Margin = new Padding(0, 0, S(8), 0);
            return d;
        }

        public static NumericUpDown Num(int min, int max, int value, int width)
        {
            var n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = S(width);
            n.Anchor = AnchorStyles.Left;
            n.Margin = new Padding(0, 0, S(6), 0);
            n.TextAlign = HorizontalAlignment.Center;
            return n;
        }

        public static void SetValueSafe(NumericUpDown n, decimal v)
        {
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v));
        }

        public static void SetValueSafe(DateTimePicker d, DateTime v)
        {
            if (v < d.MinDate) v = d.MinDate;
            if (v > d.MaxDate) v = d.MaxDate;
            d.Value = v;
        }

        /// <summary>分段按钮（单选），选中时蓝色描边+浅蓝底</summary>
        public static RadioButton Segment(string text)
        {
            var r = new RadioButton();
            r.Text = text;
            r.Appearance = Appearance.Button;
            r.AutoSize = true;
            r.MinimumSize = new Size(S(64), S(32));
            r.Padding = new Padding(S(6), 0, S(6), 0);
            r.TextAlign = ContentAlignment.MiddleCenter;
            r.FlatStyle = FlatStyle.Flat;
            r.UseVisualStyleBackColor = false;
            r.BackColor = Theme.Card;
            r.ForeColor = Theme.Text;
            r.Font = Theme.Base;
            r.Cursor = Cursors.Hand;
            r.Anchor = AnchorStyles.Left;
            r.Margin = new Padding(0, 0, S(6), 0);
            r.FlatAppearance.BorderColor = Theme.BorderStrong;
            r.FlatAppearance.CheckedBackColor = Theme.AccentSoft;
            r.FlatAppearance.MouseOverBackColor = Theme.Hover;
            r.FlatAppearance.MouseDownBackColor = Theme.Pressed;
            r.CheckedChanged += delegate
            {
                r.ForeColor = r.Checked ? Theme.Accent : Theme.Text;
                r.FlatAppearance.BorderColor = r.Checked ? Theme.Accent : Theme.BorderStrong;
                r.Font = r.Checked ? Theme.Bold : Theme.Base;
            };
            return r;
        }

        /// <summary>可多选的小方块（星期）</summary>
        public static CheckBox Toggle(string text)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Appearance = Appearance.Button;
            c.AutoSize = false;
            c.Size = new Size(S(36), S(32));
            c.TextAlign = ContentAlignment.MiddleCenter;
            c.FlatStyle = FlatStyle.Flat;
            c.UseVisualStyleBackColor = false;
            c.BackColor = Theme.Card;
            c.ForeColor = Theme.Text;
            c.Font = Theme.Base;
            c.Cursor = Cursors.Hand;
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 0, S(4), 0);
            c.FlatAppearance.BorderColor = Theme.BorderStrong;
            c.FlatAppearance.CheckedBackColor = Theme.Accent;
            c.FlatAppearance.MouseOverBackColor = Theme.Hover;
            c.CheckedChanged += delegate
            {
                c.ForeColor = c.Checked ? Color.White : Theme.Text;
                c.FlatAppearance.BorderColor = c.Checked ? Theme.Accent : Theme.BorderStrong;
                c.FlatAppearance.MouseOverBackColor = c.Checked ? Theme.AccentHover : Theme.Hover;
            };
            return c;
        }

        public static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }

    // ============================================================ 自绘控件

    /// <summary>白底 + 1px 描边的卡片</summary>
    public class Card : Panel
    {
        public Card()
        {
            BackColor = Theme.Card;
            DoubleBuffered = true;
            ResizeRedraw = true;
            Padding = new Padding(1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>顶部导航页签：文字 + 选中下划线</summary>
    public class NavTab : Control
    {
        bool _selected, _hover;

        public NavTab(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = Theme.Base;
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            Margin = new Padding(0, 0, Theme.S(2), 0);
            Height = Theme.S(44);
            Fit();
        }

        public bool Selected
        {
            get { return _selected; }
            set { _selected = value; Invalidate(); }
        }

        public void SetText(string text)
        {
            if (Text == text) return;
            Text = text;
            Fit();
            Invalidate();
        }

        void Fit()
        {
            var sz = TextRenderer.MeasureText(Text ?? "", Theme.Bold);
            Width = sz.Width + Theme.S(28);
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Color fc = _selected ? Theme.Accent : (_hover ? Theme.Text : Theme.Muted);
            TextRenderer.DrawText(g, Text, _selected ? Theme.Bold : Theme.Base, ClientRectangle, fc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (_selected)
            {
                using (var br = new SolidBrush(Theme.Accent))
                    g.FillRectangle(br, Theme.S(10), Height - Theme.S(3), Math.Max(0, Width - Theme.S(20)), Theme.S(3));
            }
        }
    }

    /// <summary>左上角的圆形 logo</summary>
    public class Logo : Control
    {
        public Logo()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Size = new Size(Theme.S(34), Theme.S(34));
            Anchor = AnchorStyles.Left;
            Margin = new Padding(0, 0, Theme.S(10), 0);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var br = new LinearGradientBrush(rc, Color.FromArgb(37, 99, 235), Color.FromArgb(20, 184, 166), 45f))
            using (var path = Rounded(rc, Theme.S(9)))
                g.FillPath(br, path);
            using (var f = new Font("Segoe UI", 12F, FontStyle.Bold))
                TextRenderer.DrawText(g, "C", f, rc, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
