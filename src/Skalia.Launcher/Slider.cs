using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Skalia.Launcher
{
    /// <summary>Campo de escolha entre valores prontos: a lista (Dropdown) ou o slider.</summary>
    internal interface IChoicePicker
    {
        IList<object> Choices { get; }
        object SelectedItem { get; set; }
        event EventHandler SelectedIndexChanged;
    }

    internal sealed partial class Dropdown : IChoicePicker
    {
        IList<object> IChoicePicker.Choices => Items;
    }

    /// <summary>
    /// Slider com passos fixos (os itens), na altura de um campo. O trilho ocupa a largura menos a
    /// coluna do valor, que mostra o rotulo do item escolhido (ex.: "1.25x"). MarkerIndex marca o
    /// ponto de referencia (1x): a parte entre ele e a bolinha fica dourada.
    /// </summary>
    internal sealed class Slider : Control, IChoicePicker
    {
        private const int ValueWidth = 56;
        private const int Knob = 16;

        private readonly List<object> m_Items = new List<object>();
        private int m_Index = -1;
        private bool m_Hover;
        private bool m_Dragging;

        public event EventHandler SelectedIndexChanged;

        public Slider()
        {
            Height = Theme.InputHeight;
            Font = Theme.Regular(10.5f);
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public IList<object> Choices => m_Items;

        /// <summary>Item de referencia (1x); -1 sem marca.</summary>
        public int MarkerIndex { get; set; } = -1;

        public int SelectedIndex
        {
            get => m_Index;
            set
            {
                var index = m_Items.Count == 0 ? -1 : Math.Max(0, Math.Min(m_Items.Count - 1, value));
                if (index == m_Index)
                {
                    return;
                }
                m_Index = index;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get => m_Index >= 0 && m_Index < m_Items.Count ? m_Items[m_Index] : null;
            set
            {
                var index = value == null ? -1 : m_Items.IndexOf(value);
                if (index >= 0)
                {
                    SelectedIndex = index;
                }
            }
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            base.SetBoundsCore(x, y, width, Theme.InputHeight, specified);
        }

        private Rectangle Track => new Rectangle(Knob / 2, 0, Math.Max(1, Width - ValueWidth - Knob), Height);

        private int XFor(int index)
        {
            var track = Track;
            return m_Items.Count <= 1 ? track.X : track.X + (int)Math.Round(track.Width * (double)index / (m_Items.Count - 1));
        }

        private int IndexAt(int x)
        {
            var track = Track;
            if (m_Items.Count <= 1)
            {
                return 0;
            }
            var ratio = (x - track.X) / (double)track.Width;
            return (int)Math.Round(Math.Max(0, Math.Min(1, ratio)) * (m_Items.Count - 1));
        }

        protected override void OnMouseEnter(EventArgs e) { m_Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { m_Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.X < Width - ValueWidth + Knob / 2)
            {
                Focus();
                m_Dragging = true;
                Capture = true;
                SelectedIndex = IndexAt(e.X);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (m_Dragging)
            {
                SelectedIndex = IndexAt(e.X);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            m_Dragging = false;
            Capture = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: SelectedIndex = m_Index - 1; e.Handled = true; break;
                case Keys.Right: case Keys.Up: SelectedIndex = m_Index + 1; e.Handled = true; break;
                case Keys.Home: SelectedIndex = 0; e.Handled = true; break;
                case Keys.End: SelectedIndex = m_Items.Count - 1; e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = Track;
            var mid = Height / 2;
            var bar = new Rectangle(track.X, mid - 2, track.Width, 4);
            using (var path = Theme.Rounded(bar, 2))
            using (var brush = new SolidBrush(Theme.InputBorder))
            {
                g.FillPath(brush, path);
            }

            var accent = Enabled ? Theme.Accent : Theme.Muted;
            if (m_Index >= 0)
            {
                var knobX = XFor(m_Index);
                var from = MarkerIndex >= 0 ? XFor(MarkerIndex) : track.X;
                if (knobX != from)
                {
                    using (var brush = new SolidBrush(accent))
                    {
                        g.FillRectangle(brush, Math.Min(from, knobX), mid - 2, Math.Abs(knobX - from), 4);
                    }
                }
                if (MarkerIndex >= 0)
                {
                    // Tracinho do 1x, por cima do trilho.
                    using (var brush = new SolidBrush(Theme.Muted))
                    {
                        g.FillRectangle(brush, XFor(MarkerIndex) - 1, mid - 6, 2, 12);
                    }
                }
                var knob = new Rectangle(knobX - Knob / 2, mid - Knob / 2, Knob, Knob);
                if (Focused)
                {
                    using (var pen = new Pen(Color.FromArgb(90, Theme.Accent), 4f))
                    {
                        g.DrawEllipse(pen, knob);
                    }
                }
                using (var brush = new SolidBrush(m_Hover || m_Dragging ? Theme.AccentHover : accent))
                {
                    g.FillEllipse(brush, knob);
                }

                const TextFormatFlags flags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                TextRenderer.DrawText(g, SelectedItem?.ToString() ?? "", Font,
                    new Rectangle(Width - ValueWidth, 0, ValueWidth, Height), Enabled ? Theme.Text : Theme.Muted, flags);
            }
        }
    }
}
