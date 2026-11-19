using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Skalia.Launcher
{
    internal static class Theme
    {
        public static readonly Color Window = Color.FromArgb(19, 21, 26);
        public static readonly Color Sidebar = Color.FromArgb(13, 14, 18);
        public static readonly Color Card = Color.FromArgb(28, 31, 38);
        public static readonly Color Input = Color.FromArgb(38, 42, 51);
        public static readonly Color InputBorder = Color.FromArgb(56, 61, 73);
        public static readonly Color Hover = Color.FromArgb(34, 37, 45);
        public static readonly Color Accent = Color.FromArgb(214, 192, 140);
        public static readonly Color AccentHover = Color.FromArgb(230, 210, 162);
        public static readonly Color AccentText = Color.FromArgb(24, 22, 16);
        public static readonly Color Text = Color.FromArgb(235, 236, 240);
        public static readonly Color Muted = Color.FromArgb(146, 152, 166);
        public static readonly Color Good = Color.FromArgb(110, 207, 140);
        public static readonly Color Warn = Color.FromArgb(232, 184, 92);

        public static Font Regular(float size = 10f) => new Font("Segoe UI", size, FontStyle.Regular);
        public static Font Semibold(float size = 10f) => new Font("Segoe UI Semibold", size, FontStyle.Regular);
        public static Font Bold(float size = 10f) => new Font("Segoe UI", size, FontStyle.Bold);

        public static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void StyleInput(Control control)
        {
            control.BackColor = Input;
            control.ForeColor = Text;
            control.Font = Regular(10.5f);
            if (control is TextBox box)
            {
                box.BorderStyle = BorderStyle.FixedSingle;
            }
            if (control is NumericUpDown number)
            {
                number.BorderStyle = BorderStyle.FixedSingle;
            }
            if (control is ComboBox combo)
            {
                combo.FlatStyle = FlatStyle.Flat;
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.ItemHeight = 24;
                combo.DrawItem += (s, e) =>
                {
                    if (e.Index < 0)
                    {
                        return;
                    }
                    var selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                    using (var back = new SolidBrush(selected ? Hover : Input))
                    {
                        e.Graphics.FillRectangle(back, e.Bounds);
                    }
                    TextRenderer.DrawText(e.Graphics, combo.GetItemText(combo.Items[e.Index]), combo.Font,
                        new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                };
            }
        }
    }

    /// <summary>Botao arredondado (primario dourado ou secundario escuro).</summary>
    internal sealed class FlatButton : Control
    {
        private bool m_Hover;
        private bool m_Primary;

        public FlatButton(string text, bool primary)
        {
            Text = text;
            m_Primary = primary;
            Cursor = Cursors.Hand;
            Font = primary ? Theme.Bold(12f) : Theme.Semibold(10f);
            Height = primary ? 52 : 38;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public bool Primary
        {
            get => m_Primary;
            set { m_Primary = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { m_Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { m_Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var fill = !Enabled ? Color.FromArgb(44, 47, 55)
                : m_Primary ? (m_Hover ? Theme.AccentHover : Theme.Accent)
                : (m_Hover ? Color.FromArgb(52, 57, 68) : Theme.Input);
            var fore = !Enabled ? Theme.Muted : m_Primary ? Theme.AccentText : Theme.Text;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (var brush = new SolidBrush(fill))
            {
                e.Graphics.FillPath(brush, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Item de navegacao da barra lateral.</summary>
    internal sealed class NavButton : Control
    {
        private bool m_Hover;
        private bool m_Selected;

        public NavButton(string text)
        {
            Text = text;
            Height = 42;
            Cursor = Cursors.Hand;
            Font = Theme.Semibold(10.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
        }

        public bool Selected
        {
            get => m_Selected;
            set { m_Selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { m_Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { m_Hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Sidebar);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(10, 2, Width - 21, Height - 5);
            if (m_Selected || m_Hover)
            {
                using (var path = Theme.Rounded(rect, 7))
                using (var brush = new SolidBrush(m_Selected ? Theme.Card : Theme.Hover))
                {
                    e.Graphics.FillPath(brush, path);
                }
            }
            if (m_Selected)
            {
                using (var brush = new SolidBrush(Theme.Accent))
                {
                    e.Graphics.FillRectangle(brush, 10, rect.Y + 10, 3, rect.Height - 20);
                }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(rect.X + 16, rect.Y, rect.Width - 16, rect.Height),
                m_Selected ? Theme.Text : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Interruptor liga/desliga com descricao a direita.</summary>
    internal sealed class ToggleSwitch : Control
    {
        private bool m_Checked;
        private bool m_Locked;

        public event EventHandler CheckedChanged;

        public ToggleSwitch(string text, string hint = null)
        {
            Text = text;
            Hint = hint;
            Height = hint == null ? 32 : 48;
            Cursor = Cursors.Hand;
            Font = Theme.Regular(10.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public string Hint { get; set; }

        public bool Checked
        {
            get => m_Checked;
            set
            {
                if (m_Checked == value)
                {
                    return;
                }
                m_Checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Travado: sempre ligado e nao pode ser desmarcado.</summary>
        public bool Locked
        {
            get => m_Locked;
            set { m_Locked = value; Cursor = value ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            if (!m_Locked)
            {
                Checked = !Checked;
            }
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new Rectangle(0, 6, 40, 22);
            var on = m_Checked || m_Locked;
            var trackColor = m_Locked ? Color.FromArgb(120, Theme.Accent) : on ? Theme.Accent : Color.FromArgb(62, 67, 79);
            using (var path = Theme.Rounded(track, 11))
            using (var brush = new SolidBrush(trackColor))
            {
                e.Graphics.FillPath(brush, path);
            }
            var knobX = on ? track.Right - 19 : track.X + 3;
            using (var brush = new SolidBrush(on ? Theme.AccentText : Theme.Text))
            {
                e.Graphics.FillEllipse(brush, knobX, track.Y + 3, 16, 16);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Point(52, 7), Theme.Text);
            if (!string.IsNullOrEmpty(Hint))
            {
                using (var small = Theme.Regular(9f))
                {
                    TextRenderer.DrawText(e.Graphics, Hint, small, new Point(52, 29), Theme.Muted);
                }
            }
        }
    }
}
