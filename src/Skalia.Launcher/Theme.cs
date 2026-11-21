using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
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

        /// <summary>Altura de todo campo de uma linha (texto, numero, lista).</summary>
        public const int InputHeight = 28;
        /// <summary>Coluna do rotulo e coluna do campo dentro do card.</summary>
        public const int LabelWidth = 200;
        public const int FieldWidth = 370;
        /// <summary>Respiro interno do card (laterais e topo/base).</summary>
        public const int CardPadX = 24;
        public const int CardPadY = 20;

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

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Barras de rolagem escuras do Windows 10/11 (sem efeito em sistemas sem o tema).</summary>
        public static void DarkScrollbars(Control control)
        {
            EventHandler apply = (s, e) =>
            {
                try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); } catch { }
            };
            if (control.IsHandleCreated) apply(null, EventArgs.Empty);
            control.HandleCreated += apply;
        }

        /// <summary>Barra de titulo escura (Windows 10 20H1+ e 11).</summary>
        public static void DarkTitleBar(Form form)
        {
            EventHandler apply = (s, e) =>
            {
                try
                {
                    var on = 1;
                    if (DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)) != 0)
                    {
                        DwmSetWindowAttribute(form.Handle, 19, ref on, sizeof(int));
                    }
                }
                catch { }
            };
            if (form.IsHandleCreated) apply(null, EventArgs.Empty);
            form.HandleCreated += apply;
        }

        public static void StyleInput(Control control)
        {
            control.BackColor = Input;
            control.ForeColor = Text;
            control.Font = Regular(10.5f);
            if (control is TextBox box)
            {
                // A borda vem do InputFrame; a do Windows e clara demais no tema escuro.
                box.BorderStyle = BorderStyle.None;
                if (box.Multiline)
                {
                    DarkScrollbars(box);
                }
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
            var rect = new Rectangle(12, 2, Width - 24, Height - 4);
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
                    e.Graphics.FillRectangle(brush, rect.X, rect.Y + 10, 3, rect.Height - 20);
                }
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(rect.X + 10, rect.Y, rect.Width - 10, rect.Height),
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
            Cursor = Cursors.Hand;
            Font = Theme.Regular(10.5f);
            // Linha do titulo com a altura de um campo (o trilho fica no meio dela); a dica vem logo abaixo.
            Height = Theme.InputHeight + (hint == null ? 0 : TextRenderer.MeasureText("Ag", s_HintFont).Height);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        private static readonly Font s_HintFont = Theme.Regular(9f);

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
            var hintHeight = string.IsNullOrEmpty(Hint) ? 0 : TextRenderer.MeasureText("Ag", s_HintFont).Height;
            var line = Math.Max(22, Height - hintHeight);
            var track = new Rectangle(0, (line - 22) / 2, 40, 22);
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
            const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(52, 0, Width - 52, line), Theme.Text, flags | TextFormatFlags.VerticalCenter);
            if (hintHeight > 0)
            {
                TextRenderer.DrawText(e.Graphics, Hint, s_HintFont, new Rectangle(52, line - 3, Width - 52, hintHeight), Theme.Muted, flags);
            }
        }
    }

    /// <summary>Status do servidor na barra lateral: bolinha na cor do estado e texto normal.</summary>
    internal sealed class StatusLine : Control
    {
        private Color m_DotColor = Theme.Muted;

        public StatusLine()
        {
            Font = Theme.Semibold(10.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Sidebar;
        }

        public Color DotColor
        {
            get => m_DotColor;
            set { m_DotColor = value; Invalidate(); }
        }

        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(m_DotColor))
            {
                e.Graphics.FillEllipse(brush, 0, Height / 2f - 4, 8, 8);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(14, 0, Width - 14, Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>
    /// Moldura de campo de texto: fundo input, borda de 1px (clareia no foco) e respiro interno,
    /// no lugar da borda nativa do TextBox.
    /// </summary>
    internal sealed class InputFrame : Panel
    {
        private readonly TextBox m_Box;
        private bool m_Focused;

        public InputFrame(TextBox box)
        {
            m_Box = box;
            var multilineHeight = Math.Max(box.Height, 60);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Input;
            Padding = box.Multiline ? new Padding(8, 6, 2, 6) : new Padding(8, 0, 8, 0);
            box.Dock = box.Multiline ? DockStyle.Fill : DockStyle.None;
            Controls.Add(box);
            Height = box.Multiline ? multilineHeight : Theme.InputHeight;
            box.GotFocus += (s, e) => { m_Focused = true; Invalidate(); };
            box.LostFocus += (s, e) => { m_Focused = false; Invalidate(); };
            Click += (s, e) => box.Focus();
            Cursor = Cursors.IBeam;
        }

        public TextBox Box => m_Box;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (!m_Box.Multiline)
            {
                m_Box.SetBounds(Padding.Left, (Height - m_Box.Height) / 2, Width - Padding.Horizontal, m_Box.Height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Input);
            using (var pen = new Pen(m_Focused ? Theme.Muted : Theme.InputBorder))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    /// <summary>Campo numerico simples (so digitos), sem as setinhas nativas do NumericUpDown.</summary>
    internal sealed class NumberBox : TextBox
    {
        public decimal Minimum { get; set; }
        public decimal Maximum { get; set; } = 100;

        public decimal Value
        {
            get
            {
                decimal.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v);
                return Math.Max(Minimum, Math.Min(Maximum, v));
            }
            set { Text = Math.Max(Minimum, Math.Min(Maximum, value)).ToString("0", CultureInfo.InvariantCulture); }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar))
            {
                e.Handled = true;
            }
            base.OnKeyPress(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Setas mudam o valor, como no NumericUpDown.
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                Value += e.KeyCode == Keys.Up ? 1 : -1;
                SelectionStart = Text.Length;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnValidating(System.ComponentModel.CancelEventArgs e)
        {
            Value = Value;
            base.OnValidating(e);
        }
    }

    /// <summary>
    /// Lista de escolha desenhada inteira no tema (substitui o ComboBox, cuja altura e seta
    /// mudam conforme o Windows). Fechada: fundo input, borda de 1px e chevron. Aberta: lista
    /// escura logo abaixo, ate 10 linhas, com rolagem escura.
    /// </summary>
    internal sealed partial class Dropdown : Control
    {
        public sealed class ItemCollection : Collection<object>
        {
            private readonly Dropdown m_Owner;

            internal ItemCollection(Dropdown owner) { m_Owner = owner; }

            public void AddRange(IEnumerable<object> items)
            {
                foreach (var item in items)
                {
                    Add(item);
                }
            }

            protected override void InsertItem(int index, object item) { base.InsertItem(index, item); m_Owner.Invalidate(); }
            protected override void SetItem(int index, object item) { base.SetItem(index, item); m_Owner.Invalidate(); }

            protected override void RemoveItem(int index)
            {
                base.RemoveItem(index);
                if (m_Owner.m_Index >= Count)
                {
                    m_Owner.SelectedIndex = Count - 1;
                }
                m_Owner.Invalidate();
            }

            protected override void ClearItems()
            {
                base.ClearItems();
                m_Owner.SelectedIndex = -1;
            }
        }

        private const int RowHeight = 28;
        private const int MaxRows = 10;

        private int m_Index = -1;
        private bool m_Hover;
        // Uma lista por controle, reaproveitada. Nao e descartada a mao: no Mono, descartar um
        // ToolStripDropDown recem-fechado derruba o loop de mensagens.
        private ToolStripDropDown m_Popup;
        private bool m_Open;
        private int m_ClosedAt;

        public event EventHandler SelectedIndexChanged;

        public Dropdown()
        {
            Items = new ItemCollection(this);
            Height = Theme.InputHeight;
            Font = Theme.Regular(10.5f);
            BackColor = Theme.Input;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }

        public ItemCollection Items { get; }

        public bool DroppedDown => m_Open;

        public int SelectedIndex
        {
            get => m_Index;
            set
            {
                var index = value < -1 || value >= Items.Count ? -1 : value;
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
            get => m_Index >= 0 && m_Index < Items.Count ? Items[m_Index] : null;
            set => SelectedIndex = value == null ? -1 : Items.IndexOf(value);
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            // Altura fixa, igual a dos campos de texto.
            base.SetBoundsCore(x, y, width, Theme.InputHeight, specified);
        }

        protected override void OnMouseEnter(EventArgs e) { m_Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { m_Hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }
            Focus();
            // O clique que fecha a lista tambem chega aqui; nao reabre na mesma hora.
            if (unchecked(Environment.TickCount - m_ClosedAt) > 200)
            {
                Open();
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up:
                case Keys.Down:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.F4 || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                Open();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down && m_Index < Items.Count - 1)
            {
                SelectedIndex = m_Index + 1;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up && m_Index > 0)
            {
                SelectedIndex = m_Index - 1;
                e.Handled = true;
            }
        }

        private void Open()
        {
            if (m_Open || Items.Count == 0 || !Enabled)
            {
                return;
            }
            var hover = -1;
            var list = new ListBox
            {
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = RowHeight,
                IntegralHeight = false,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Font = Font,
                Size = new Size(Width - 2, Math.Min(Items.Count, MaxRows) * RowHeight),
            };
            foreach (var item in Items)
            {
                list.Items.Add(item);
            }
            list.SelectedIndex = m_Index;
            Theme.DarkScrollbars(list);
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0)
                {
                    return;
                }
                var current = e.Index == m_Index;
                var highlighted = e.Index == hover || (hover < 0 && (e.State & DrawItemState.Selected) != 0);
                using (var back = new SolidBrush(highlighted ? Theme.Hover : Theme.Input))
                {
                    e.Graphics.FillRectangle(back, e.Bounds);
                }
                if (current)
                {
                    using (var mark = new SolidBrush(Theme.Muted))
                    {
                        e.Graphics.FillRectangle(mark, e.Bounds.X, e.Bounds.Y + 8, 2, e.Bounds.Height - 16);
                    }
                }
                TextRenderer.DrawText(e.Graphics, Convert.ToString(list.Items[e.Index]), Font,
                    new Rectangle(e.Bounds.X + 9, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
                    current ? Theme.Text : Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            list.MouseMove += (s, e) =>
            {
                var index = list.IndexFromPoint(e.Location);
                if (index != hover)
                {
                    hover = index;
                    list.Invalidate();
                }
            };
            list.MouseClick += (s, e) => Choose(list.IndexFromPoint(e.Location));
            list.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    Choose(list.SelectedIndex);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    m_Popup.Close();
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                {
                    hover = -1;
                }
            };

            var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
            if (m_Popup == null)
            {
                m_Popup = new ToolStripDropDown
                {
                    Padding = new Padding(1),
                    Renderer = new PopupRenderer(),
                    DropShadowEnabled = false,
                };
                m_Popup.Closed += (s, e) =>
                {
                    m_Open = false;
                    m_ClosedAt = Environment.TickCount;
                    if (!IsDisposed)
                    {
                        Invalidate();
                    }
                };
            }
            var previous = m_Popup.Items.Cast<ToolStripItem>().ToList();
            m_Popup.Items.Clear();
            foreach (var item in previous)
            {
                item.Dispose(); // a lista da abertura anterior (o host descarta o ListBox junto)
            }
            m_Popup.Items.Add(host);
            m_Open = true;
            m_Popup.Show(this, new Point(0, Height));
            if (m_Index >= 0)
            {
                list.TopIndex = Math.Max(0, m_Index - MaxRows / 2);
            }
            list.Focus();
            Invalidate();
        }

        private void Choose(int index)
        {
            m_Popup.Close();
            if (IsDisposed)
            {
                return;
            }
            Focus();
            if (index >= 0 && index < Items.Count && index != m_Index)
            {
                // A troca pode reconstruir a tela (idioma); deixa a lista terminar de fechar antes.
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                    {
                        SelectedIndex = index;
                    }
                }));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = !Enabled ? Theme.Card : m_Hover || DroppedDown ? Theme.Hover : Theme.Input;
            g.Clear(back);
            using (var pen = new Pen(Enabled && (Focused || DroppedDown) ? Theme.Muted : Theme.InputBorder))
            {
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
            var fore = Enabled ? Theme.Text : Theme.Muted;
            var text = Convert.ToString(SelectedItem) ?? "";
            TextRenderer.DrawText(g, text, Font, new Rectangle(9, 0, Width - 36, Height), fore,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var cx = Width - 16f;
            var cy = Height / 2f;
            using (var pen = new Pen(Enabled ? Theme.Muted : Theme.InputBorder, 1.6f))
            {
                if (DroppedDown)
                {
                    g.DrawLines(pen, new[] { new PointF(cx - 4, cy + 2), new PointF(cx, cy - 2), new PointF(cx + 4, cy + 2) });
                }
                else
                {
                    g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && m_Open)
            {
                m_Popup.Close();
            }
            base.Dispose(disposing);
        }

        private sealed class PopupRenderer : ToolStripRenderer
        {
            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(Theme.Muted);
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }
        }
    }
}
