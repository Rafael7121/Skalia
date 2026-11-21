using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Skalia.Launcher
{
    /// <summary>
    /// Abas de texto dentro de um card: a escolhida em texto claro com um traco dourado embaixo,
    /// as outras em muted, e uma linha fina embaixo de todas.
    /// </summary>
    internal sealed class TabStrip : Control
    {
        private const int Gap = 20;
        private readonly List<string> m_Tabs = new List<string>();
        private readonly List<Rectangle> m_Bounds = new List<Rectangle>();
        private int m_Index;
        private int m_Hover = -1;

        public event EventHandler SelectedIndexChanged;

        public TabStrip()
        {
            Font = Theme.Regular(10.5f);
            Height = 34;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Card;
        }

        public void Add(string tab)
        {
            m_Tabs.Add(tab);
            Invalidate();
        }

        public int Count => m_Tabs.Count;

        public int SelectedIndex
        {
            get => m_Index;
            set
            {
                var index = Math.Max(0, Math.Min(m_Tabs.Count - 1, value));
                if (index == m_Index)
                {
                    return;
                }
                m_Index = index;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void Measure()
        {
            m_Bounds.Clear();
            var x = 0;
            foreach (var tab in m_Tabs)
            {
                var width = TextRenderer.MeasureText(tab, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
                m_Bounds.Add(new Rectangle(x, 0, width, Height));
                x += width + Gap;
            }
        }

        private int HitTest(Point point)
        {
            Measure();
            for (var i = 0; i < m_Bounds.Count; i++)
            {
                var r = m_Bounds[i];
                if (point.X >= r.X - Gap / 2 && point.X < r.Right + Gap / 2)
                {
                    return i;
                }
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hover = HitTest(e.Location);
            if (hover != m_Hover)
            {
                m_Hover = hover;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { m_Hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var index = HitTest(e.Location);
            if (e.Button == MouseButtons.Left && index >= 0)
            {
                SelectedIndex = index;
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData) => keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) { SelectedIndex = m_Index - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SelectedIndex = m_Index + 1; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            Measure();
            using (var line = new SolidBrush(Theme.InputBorder))
            {
                g.FillRectangle(line, 0, Height - 1, Width, 1);
            }
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            for (var i = 0; i < m_Tabs.Count; i++)
            {
                var r = m_Bounds[i];
                var selected = i == m_Index;
                var color = selected || i == m_Hover ? Theme.Text : Theme.Muted;
                TextRenderer.DrawText(g, m_Tabs[i], Font, new Rectangle(r.X, 0, r.Width + 4, Height - 3), color, flags);
                if (selected)
                {
                    using (var brush = new SolidBrush(Focused ? Theme.AccentHover : Theme.Accent))
                    {
                        g.FillRectangle(brush, r.X, Height - 2, r.Width, 2);
                    }
                }
            }
        }
    }
}
