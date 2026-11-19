using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Skalia.Launcher
{
    public sealed class MainForm : Form
    {

        private sealed class Choice
        {
            public Choice(string value, string label) { Value = value; Label = label; }
            public string Value { get; }
            public string Label { get; }
            public override string ToString() => Label;
        }

        private readonly GamePaths m_Paths;
        private readonly SkaliaSettings m_Settings;
        private readonly ServerManager m_Server;
        private readonly Timer m_Timer = new Timer { Interval = 1000 };

        private readonly List<NavButton> m_Nav = new List<NavButton>();
        private readonly List<Control> m_Pages = new List<Control>();
        private readonly List<ToggleSwitch> m_PluginToggles = new List<ToggleSwitch>();
        private readonly Dictionary<string, ComboBox> m_RuleCombos = new Dictionary<string, ComboBox>();

        private Panel m_Content;
        private TextBox m_Name, m_Welcome, m_Password, m_BotNames, m_Workshop, m_Log;
        private ComboBox m_Map, m_Difficulty, m_Perspective;
        private NumericUpDown m_MaxPlayers, m_Port, m_BotCount;
        private ToggleSwitch m_PvP, m_Cheats, m_ShowConsole, m_AutoCallBots;
        private FlowLayoutPanel m_PluginList;
        private Panel m_Advanced;
        private Label m_AdvancedHeader;
        private FlatButton m_PlayButton, m_StopButton;
        private Label m_Status, m_StatusDetail;

        private long m_LogPosition;
        private bool m_WasReady;
        private bool m_PlayWhenReady;
        private bool m_Preparing;
        private DateTime m_StopRequestedAt = DateTime.MinValue;

        public MainForm()
        {
            m_Paths = GamePaths.Detect();
            m_Settings = SkaliaSettings.Load(m_Paths.SettingsFile);
            L.Language = m_Settings.Language;
            m_Server = new ServerManager(m_Paths, line =>
            {
                if (InvokeRequired)
                {
                    BeginInvoke(new Action(() => AppendLog(line)));
                }
                else
                {
                    AppendLog(line);
                }
            });

            Text = "SKALIA";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1080, 720);
            MinimumSize = new Size(920, 640);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            Font = Theme.Regular(10f);
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            BuildUi();

            if (m_Paths.U3DSDir == null)
            {
                MessageBox.Show(this,
                    L.Tr("Não encontrei o Unturned Dedicated Server (U3DS).\n\nInstale o \"Unturned Dedicated Server\" pela Steam e coloque a pasta do SKALIA dentro dele (U3DS\\Skalia)."),
                    "SKALIA", MessageBoxButtons.OK, MessageBoxIcon.Error);
                m_PlayButton.Enabled = false;
                return;
            }

            if (!m_Settings.WorkshopImported)
            {
                m_Settings.WorkshopIds = new List<ulong>();
                m_Settings.WorkshopImported = true;
            }
            LoadSettingsIntoUi();
            Shown += (s, e) => ActiveControl = null;
            m_LogPosition = File.Exists(m_Paths.ServerLogFile) ? new FileInfo(m_Paths.ServerLogFile).Length : 0;
            m_Timer.Tick += (s, e) => Tick();
            m_Timer.Start();
            Tick();
        }

        // ================================================================== layout

        private void BuildUi()
        {
            m_Content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
            Controls.Add(m_Content);
            Controls.Add(BuildSidebar());

            AddPage(BuildServerPage());
            AddPage(BuildPluginsPage());
            AddPage(BuildConsolePage());
            ShowPage(0);
        }

        private Control BuildSidebar()
        {
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Theme.Sidebar };

            var brand = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = Theme.Sidebar };
            var logo = new PictureBox
            {
                Image = LoadLogo(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(24, 26),
                Size = new Size(150, 46),
                BackColor = Theme.Sidebar,
            };
            brand.Controls.Add(logo);
            brand.Controls.Add(new Label
            {
                Text = "SKALIA",
                Font = Theme.Bold(22f),
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(20, 78),
            });
            brand.Controls.Add(new Label
            {
                Text = L.Tr("Unturned offline com bots"),
                Font = Theme.Regular(9.5f),
                ForeColor = Theme.Muted,
                AutoSize = true,
                Location = new Point(24, 118),
            });

            var nav = new Panel { Dock = DockStyle.Top, Height = 160, BackColor = Theme.Sidebar, Padding = new Padding(0, 8, 0, 0) };
            var labels = new[] { L.Tr("Servidor"), L.Tr("Plugins e Workshop"), L.Tr("Console") };
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var button = new NavButton(labels[i]) { Dock = DockStyle.Top };
                button.Click += (s, e) => ShowPage(index);
                m_Nav.Add(button);
                nav.Controls.Add(button);
                button.BringToFront();
            }

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 196, BackColor = Theme.Sidebar, Padding = new Padding(20, 0, 20, 22) };
            m_StopButton = new FlatButton(L.Tr("Salvar e parar servidor"), false) { Dock = DockStyle.Bottom };
            m_StopButton.Click += (s, e) => StopServer();
            m_PlayButton = new FlatButton(L.Tr("JOGAR"), true) { Dock = DockStyle.Bottom };
            m_PlayButton.Click += (s, e) => Play();
            m_StatusDetail = new Label { Dock = DockStyle.Bottom, Height = 34, ForeColor = Theme.Muted, Font = Theme.Regular(9f) };
            m_Status = new Label { Dock = DockStyle.Bottom, Height = 26, Font = Theme.Semibold(10.5f), ForeColor = Theme.Muted };
            // Dock Bottom: o ultimo adicionado fica mais embaixo.
            footer.Controls.Add(m_Status);
            footer.Controls.Add(m_StatusDetail);
            footer.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Theme.Sidebar });
            footer.Controls.Add(m_PlayButton);
            footer.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 10, BackColor = Theme.Sidebar });
            footer.Controls.Add(m_StopButton);

            var language = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = Theme.Sidebar, Padding = new Padding(20, 0, 20, 12) };
            var languageBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Bottom };
            Theme.StyleInput(languageBox);
            languageBox.Items.Add(new Choice(L.English, "English"));
            languageBox.Items.Add(new Choice(L.Portuguese, "Português (Brasil)"));
            languageBox.SelectedIndex = L.IsPortuguese ? 1 : 0;
            languageBox.SelectedIndexChanged += (s, e) => ChangeLanguage(((Choice)languageBox.SelectedItem).Value);
            language.Controls.Add(new Label { UseMnemonic = false, Text = L.Tr("Idioma"), Dock = DockStyle.Bottom, Height = 22, ForeColor = Theme.Muted, Font = Theme.Regular(9f) });
            language.Controls.Add(languageBox);

            sidebar.Controls.Add(language);
            sidebar.Controls.Add(footer);
            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(brand);
            nav.BringToFront();
            return sidebar;
        }

        private Control BuildServerPage()
        {
            var page = NewPage(L.Tr("Servidor"), L.Tr("Escolha o mapa e clique em JOGAR. O resto já vem pronto."));

            m_Name = new TextBox();
            m_Map = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            m_Difficulty = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            m_Difficulty.Items.AddRange(new object[]
            {
                new Choice("Easy", L.Tr("Fácil")),
                new Choice("Normal", "Normal"),
                new Choice("Hard", L.Tr("Difícil")),
            });
            m_PvP = new ToggleSwitch("PvP", L.Tr("Jogadores e bots podem se atacar."));

            var basic = NewCard();
            AddField(basic, L.Tr("Nome do servidor"), m_Name);
            AddField(basic, L.Tr("Mapa"), m_Map);
            AddField(basic, L.Tr("Dificuldade"), m_Difficulty, L.Tr("Os multiplicadores (1x) partem dela."));
            foreach (var rule in GameRules.All.Where(r => r.Group == GameRules.Basic))
            {
                AddField(basic, L.Tr(rule.Title), RuleCombo(rule), L.Tr(rule.Hint));
            }
            AddWide(basic, m_PvP);
            Stack(page, basic);

            Stack(page, Spacer(18));
            m_AdvancedHeader = new Label
            {
                Dock = DockStyle.Top,
                Height = 36,
                Font = Theme.Semibold(11f),
                ForeColor = Theme.Muted,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft,
            };
            m_AdvancedHeader.Click += (s, e) => SetAdvancedVisible(!m_Advanced.Visible);
            m_AdvancedHeader.MouseEnter += (s, e) => m_AdvancedHeader.ForeColor = Theme.Text;
            m_AdvancedHeader.MouseLeave += (s, e) => m_AdvancedHeader.ForeColor = Theme.Muted;
            Stack(page, m_AdvancedHeader);

            m_Advanced = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window };

            m_BotCount = new NumericUpDown { Minimum = 0, Maximum = 48 };
            m_BotNames = new TextBox { Multiline = true, Height = 120, ScrollBars = ScrollBars.Vertical };
            m_AutoCallBots = new ToggleSwitch(L.Tr("Bots entram sozinhos"), L.Tr("Entram assim que o mapa termina de carregar."));
            var bots = NewCard();
            AddSection(bots, L.Tr("Bots"));
            AddField(bots, L.Tr("Quantidade"), m_BotCount);
            AddField(bots, L.Tr("Nomes"), m_BotNames, L.Tr("Um por linha. Vazio = nomes gerados automaticamente."));
            AddWide(bots, m_AutoCallBots);

            m_MaxPlayers = new NumericUpDown { Minimum = 1, Maximum = 200 };
            m_Perspective = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            m_Perspective.Items.AddRange(new object[]
            {
                new Choice("Both", L.Tr("Primeira e terceira pessoa")),
                new Choice("First", L.Tr("Somente primeira pessoa")),
                new Choice("Third", L.Tr("Somente terceira pessoa")),
                new Choice("Vehicle", L.Tr("Terceira pessoa só em veículos")),
            });
            m_Port = new NumericUpDown { Minimum = 1024, Maximum = 65000 };
            m_Password = new TextBox { UseSystemPasswordChar = true };
            m_Cheats = new ToggleSwitch(L.Tr("Comandos de administrador"), L.Tr("Libera /give, /tp, /vehicle e outros comandos para você."));
            m_ShowConsole = new ToggleSwitch(L.Tr("Mostrar a janela do servidor"), L.Tr("Abre o console do servidor junto com o jogo."));
            var server = NewCard();
            AddSection(server, L.Tr("Servidor"));
            AddField(server, L.Tr("Jogadores máximos"), m_MaxPlayers);
            AddField(server, L.Tr("Câmera"), m_Perspective);
            AddField(server, L.Tr("Porta"), m_Port, L.Tr("Mude só se outro programa já usa esta porta."));
            AddField(server, L.Tr("Senha"), m_Password, L.Tr("Opcional, para amigos na mesma rede."));
            AddWide(server, m_Cheats);
            AddWide(server, m_ShowConsole);

            m_Welcome = new TextBox();
            AddField(server, L.Tr("Boas-vindas"), m_Welcome, L.Tr("Mensagem no chat quando alguém entra. Opcional."));

            var rules = NewCard();
            var first = true;
            foreach (var group in new[] { GameRules.World, GameRules.Survival, GameRules.Combat })
            {
                if (!first)
                {
                    AddWide(rules, new Panel { Height = 14, Width = 10, BackColor = Theme.Card, Margin = new Padding(0) });
                }
                first = false;
                AddSection(rules, L.Tr(group));
                foreach (var rule in GameRules.All.Where(r => r.Group == group))
                {
                    AddField(rules, L.Tr(rule.Title), RuleCombo(rule), L.Tr(rule.Hint));
                }
            }

            Stack(m_Advanced, bots);
            Stack(m_Advanced, Spacer(14));
            Stack(m_Advanced, rules);
            Stack(m_Advanced, Spacer(14));
            Stack(m_Advanced, server);
            Stack(page, m_Advanced);
            Stack(page, Spacer(28));
            SetAdvancedVisible(false);
            return page.Parent;
        }

        private Control BuildPluginsPage()
        {
            var page = NewPage(L.Tr("Plugins e Workshop"), L.Tr("Adicione plugins OpenMod e itens da Workshop ao seu servidor."));

            var plugins = NewCard();
            AddSection(plugins, L.Tr("Plugins"));
            m_PluginList = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Card, Margin = new Padding(0) };
            AddWide(plugins, m_PluginList);
            var openPlugins = new FlatButton(L.Tr("Abrir pasta de plugins"), false) { Width = 220 };
            openPlugins.Click += (s, e) => OpenFolder(m_Paths.PluginsSourceDir);
            AddWide(plugins, openPlugins);
            AddWide(plugins, Hint(L.Tr("Coloque a DLL de um plugin OpenMod nessa pasta e abra o SKALIA de novo.")));
            Stack(page, plugins);

            Stack(page, Spacer(18));
            m_Workshop = new TextBox { Multiline = true, Height = 110, ScrollBars = ScrollBars.Vertical };
            var workshop = NewCard();
            AddSection(workshop, L.Tr("Workshop"));
            AddField(workshop, L.Tr("IDs dos itens"), m_Workshop, L.Tr("Um por linha. Mapas da Workshop entram aqui sozinhos ao serem escolhidos."));
            Stack(page, workshop);
            Stack(page, Spacer(28));
            return page.Parent;
        }

        private Control BuildConsolePage()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(36, 28, 36, 28) };
            var title = PageTitle(L.Tr("Console"), L.Tr("O que o servidor está fazendo agora."));
            m_Log = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.Sidebar,
                ForeColor = Color.FromArgb(196, 201, 212),
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9.5f),
                WordWrap = false,
            };
            var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(12) };
            logHost.Controls.Add(m_Log);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 54, BackColor = Theme.Window, Padding = new Padding(0, 14, 0, 0) };
            var folder = new FlatButton(L.Tr("Abrir pasta do servidor"), false) { Dock = DockStyle.Left, Width = 220 };
            folder.Click += (s, e) => OpenFolder(m_Paths.ProfileDir);
            bottom.Controls.Add(folder);
            outer.Controls.Add(logHost);
            outer.Controls.Add(bottom);
            outer.Controls.Add(title);
            return outer;
        }

        // ------------------------------------------------------------ helpers

        private Panel NewPage(string title, string subtitle)
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, AutoScroll = true, Padding = new Padding(36, 28, 36, 0) };
            var stack = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window };
            outer.Controls.Add(stack);
            Stack(stack, PageTitle(title, subtitle));
            return stack;
        }

        private static Control PageTitle(string title, string subtitle)
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Theme.Window };
            header.Controls.Add(new Label { UseMnemonic = false, Text = title, Font = Theme.Bold(20f), ForeColor = Theme.Text, AutoSize = true, Location = new Point(-2, 0) });
            header.Controls.Add(new Label { UseMnemonic = false, Text = subtitle, Font = Theme.Regular(10f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(0, 42) });
            return header;
        }

        private static void Stack(Control parent, Control child)
        {
            child.Dock = DockStyle.Top;
            parent.Controls.Add(child);
            child.BringToFront();
        }

        private static Panel Spacer(int height) => new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Window };

        private static TableLayoutPanel NewCard()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Theme.Card,
                Padding = new Padding(24, 18, 24, 18),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return grid;
        }

        private static void AddSection(TableLayoutPanel grid, string title)
        {
            var label = new Label { UseMnemonic = false, Text = title, Font = Theme.Semibold(10.5f), ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
            AddWide(grid, label);
        }

        private static void AddField(TableLayoutPanel grid, string label, Control control, string hint = null)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(new Label { UseMnemonic = false, Text = label, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Regular(10.5f), Margin = new Padding(0, 10, 12, 6) }, 0, row);
            Theme.StyleInput(control);
            control.Width = control is NumericUpDown ? 120 : 380;
            control.Margin = new Padding(0, 6, 0, 6);
            if (hint == null)
            {
                grid.Controls.Add(control, 1, row);
                return;
            }
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0) };
            cell.Controls.Add(control);
            cell.Controls.Add(new Label { UseMnemonic = false, Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Margin = new Padding(0, 0, 0, 6), MaximumSize = new Size(420, 0) });
            grid.Controls.Add(cell, 1, row);
        }

        private static void AddWide(TableLayoutPanel grid, Control control)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (control is ToggleSwitch)
            {
                control.Width = 520;
                control.Margin = new Padding(0, 6, 0, 4);
            }
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        private ComboBox RuleCombo(GameRule rule)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(rule.Options.Cast<object>().ToArray());
            combo.SelectedIndex = 0;
            m_RuleCombos[rule.Id] = combo;
            return combo;
        }

        private static Label Hint(string text) => new Label { UseMnemonic = false, Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Margin = new Padding(0, 8, 0, 0) };

        private void AddPage(Control page)
        {
            page.Visible = false;
            m_Pages.Add(page);
            m_Content.Controls.Add(page);
        }

        private void ShowPage(int index)
        {
            for (var i = 0; i < m_Pages.Count; i++)
            {
                m_Pages[i].Visible = i == index;
                m_Nav[i].Selected = i == index;
            }
            if (index == 2 && m_Log != null)
            {
                m_Log.SelectionStart = m_Log.TextLength;
                m_Log.ScrollToCaret();
            }
        }

        private void ChangeLanguage(string language)
        {
            if (language == L.Language)
            {
                return;
            }
            var advanced = m_Advanced.Visible;
            var page = m_Pages.FindIndex(p => p.Visible);
            var log = m_Log.Text;
            if (m_Paths.U3DSDir != null)
            {
                ReadUiIntoSettings();
            }
            m_Settings.Language = language;
            L.Language = language;
            try
            {
                m_Settings.Save(m_Paths.SettingsFile);
            }
            catch
            {
            }

            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var control in old)
            {
                BeginInvoke(new Action(control.Dispose));
            }
            m_Nav.Clear();
            m_Pages.Clear();
            m_PluginToggles.Clear();
            m_RuleCombos.Clear();
            BuildUi();
            if (m_Paths.U3DSDir != null)
            {
                LoadSettingsIntoUi();
            }
            m_Log.Text = log;
            SetAdvancedVisible(advanced);
            ShowPage(Math.Max(0, page));
            ResumeLayout(true);
            Tick();
        }

        private void SetAdvancedVisible(bool visible)
        {
            m_Advanced.Visible = visible;
            m_AdvancedHeader.Text = (visible ? "▾  " : "▸  ") + L.Tr("Configurações avançadas");
        }

        private static Image LoadLogo()
        {
            try
            {
                using (var stream = typeof(MainForm).Assembly.GetManifestResourceStream("logo.png"))
                {
                    return stream == null ? null : new Bitmap(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        // =============================================================== settings

        private void LoadSettingsIntoUi()
        {
            m_Name.Text = m_Settings.ServerName;
            ReloadMaps();
            SelectChoice(m_Difficulty, m_Settings.Difficulty, "Normal");
            m_PvP.Checked = m_Settings.PvP;

            m_BotCount.Value = Clamp(m_Settings.BotCount, m_BotCount);
            m_BotNames.Text = string.Join(Environment.NewLine, m_Settings.BotNames);
            m_AutoCallBots.Checked = m_Settings.AutoCallBots;

            m_MaxPlayers.Value = Clamp(m_Settings.MaxPlayers, m_MaxPlayers);
            SelectChoice(m_Perspective, m_Settings.Perspective, "Both");
            m_Port.Value = Clamp(m_Settings.Port, m_Port);
            m_Password.Text = m_Settings.Password;
            m_Welcome.Text = m_Settings.WelcomeMessage;
            foreach (var rule in GameRules.All)
            {
                m_Settings.GameRules.TryGetValue(rule.Id, out var chosen);
                m_RuleCombos[rule.Id].SelectedItem = rule.Find(chosen);
            }
            m_Cheats.Checked = m_Settings.Cheats;
            m_ShowConsole.Checked = m_Settings.ShowServerConsole;

            m_PluginList.Controls.Clear();
            m_PluginToggles.Clear();
            var plugins = m_Server.AvailablePlugins();
            foreach (var plugin in plugins)
            {
                if (string.Equals(plugin, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var toggle = new ToggleSwitch(Path.GetFileNameWithoutExtension(plugin), L.Tr("Plugin OpenMod adicional."))
                {
                    Checked = !m_Settings.DisabledPlugins.Contains(plugin, StringComparer.OrdinalIgnoreCase),
                };
                toggle.Tag = plugin;
                toggle.Width = 520;
                toggle.Margin = new Padding(0, 4, 0, 4);
                m_PluginToggles.Add(toggle);
                m_PluginList.Controls.Add(toggle);
            }
            if (!plugins.Any(plugin => string.Equals(plugin, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase)))
            {
                AppendLog(L.Tr("AVISO: nenhum plugin encontrado em ") + m_Paths.PluginsSourceDir + ".");
            }
            m_Workshop.Text = string.Join(Environment.NewLine, m_Settings.WorkshopIds);
        }

        private static void SelectChoice(ComboBox combo, string value, string fallback)
        {
            var choices = combo.Items.Cast<Choice>().ToList();
            combo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))
                ?? choices.First(c => c.Value == fallback);
        }

        private static string ChoiceValue(ComboBox combo, string fallback) => (combo.SelectedItem as Choice)?.Value ?? fallback;

        private void ReloadMaps()
        {
            m_Map.Items.Clear();
            var maps = m_Server.DiscoverMaps(m_Settings.WorkshopIds);
            foreach (var map in maps)
            {
                m_Map.Items.Add(map);
            }
            var selected = maps.FirstOrDefault(map => string.Equals(map.Name, m_Settings.Map, StringComparison.OrdinalIgnoreCase));
            if (selected == null && !string.IsNullOrWhiteSpace(m_Settings.Map))
            {
                selected = new MapEntry { Name = m_Settings.Map };
                m_Map.Items.Add(selected);
            }
            m_Map.SelectedItem = selected ?? maps.FirstOrDefault();
        }

        private void ReadUiIntoSettings()
        {
            m_Settings.ServerName = string.IsNullOrWhiteSpace(m_Name.Text) ? "Server" : m_Name.Text.Trim();
            var map = m_Map.SelectedItem as MapEntry;
            m_Settings.Map = map?.Name ?? m_Settings.Map;
            m_Settings.Difficulty = ChoiceValue(m_Difficulty, "Normal");
            m_Settings.PvP = m_PvP.Checked;

            m_Settings.BotCount = (int)m_BotCount.Value;
            m_Settings.BotNames = m_BotNames.Lines.Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
            if (m_Settings.BotNames.Count < m_Settings.BotCount)
            {
                // Sem nome preenchido: gera apelidos e guarda, para cada bot manter o mesmo nome.
                var missing = m_Settings.BotCount - m_Settings.BotNames.Count;
                var generated = BotNameGenerator.Generate(missing, m_Settings.BotNames, Environment.TickCount);
                m_Settings.BotNames.AddRange(generated);
                m_BotNames.Text = string.Join(Environment.NewLine, m_Settings.BotNames);
                AppendLog(L.Tr("Nomes gerados para ") + generated.Count + L.Tr(" bot(s): ") + string.Join(", ", generated));
            }
            m_Settings.AutoCallBots = m_AutoCallBots.Checked;

            m_Settings.MaxPlayers = (int)m_MaxPlayers.Value;
            m_Settings.Perspective = ChoiceValue(m_Perspective, "Both");
            m_Settings.Port = (int)m_Port.Value;
            m_Settings.Password = m_Password.Text.Trim();
            m_Settings.WelcomeMessage = m_Welcome.Text.Trim();
            m_Settings.GameRules = m_RuleCombos
                .Where(pair => pair.Value.SelectedItem is RuleOption option && option.Id != "default")
                .ToDictionary(pair => pair.Key, pair => ((RuleOption)pair.Value.SelectedItem).Id);
            m_Settings.Cheats = m_Cheats.Checked;
            m_Settings.ShowServerConsole = m_ShowConsole.Checked;

            m_Settings.DisabledPlugins = m_PluginToggles
                .Where(toggle => !toggle.Locked && !toggle.Checked)
                .Select(toggle => (string)toggle.Tag)
                .ToList();

            var ids = new List<ulong>();
            foreach (var token in m_Workshop.Text.Split(new[] { '\r', '\n', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (ulong.TryParse(token.Trim(), out var id) && !ids.Contains(id))
                {
                    ids.Add(id);
                }
            }
            if (map != null && map.WorkshopId != 0 && !ids.Contains(map.WorkshopId))
            {
                ids.Add(map.WorkshopId);
                AppendLog(L.Tr("Mapa da Workshop adicionado: ") + map.WorkshopId);
            }
            m_Settings.WorkshopIds = ids;
            m_Workshop.Text = string.Join(Environment.NewLine, ids);
            m_Settings.Save(m_Paths.SettingsFile);
        }

        private static decimal Clamp(int value, NumericUpDown box) => Math.Max(box.Minimum, Math.Min(box.Maximum, value));

        // ================================================================ actions

        private async void StartServer(bool playWhenReady)
        {
            if (m_Preparing)
            {
                return;
            }
            if (m_Server.IsRunning)
            {
                m_PlayWhenReady |= playWhenReady;
                return;
            }
            m_Preparing = true;
            m_PlayButton.Enabled = false;
            SetStatus(L.Tr("Preparando..."), Theme.Warn, L.Tr("Organizando os arquivos do servidor."));
            try
            {
                ReadUiIntoSettings();
                var owner = m_Paths.FindCurrentSteamId();
                await System.Threading.Tasks.Task.Run(() => m_Server.PrepareProfile(m_Settings, owner));
                m_LogPosition = File.Exists(m_Paths.ServerLogFile) ? new FileInfo(m_Paths.ServerLogFile).Length : 0;
                m_WasReady = false;
                m_PlayWhenReady = playWhenReady;
                m_StopRequestedAt = DateTime.MinValue;
                m_Server.Start(m_Settings);
            }
            catch (Exception exception)
            {
                AppendLog(L.Tr("ERRO ao iniciar: ") + exception.Message);
                MessageBox.Show(this, exception.Message, "SKALIA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                m_Preparing = false;
                m_PlayButton.Enabled = true;
                Tick();
            }
        }

        private void Play()
        {
            if (m_Server.IsReady)
            {
                m_Server.LaunchClient(m_Settings);
                return;
            }
            if (!m_Server.IsRunning)
            {
                StartServer(playWhenReady: false);
                AppendLog(L.Tr("Quando o servidor terminar de carregar, clique em ENTRAR NO JOGO."));
            }
        }

        private void StopServer()
        {
            m_PlayWhenReady = false;
            if (!m_Server.IsRunning)
            {
                return;
            }
            if (m_StopRequestedAt != DateTime.MinValue && (DateTime.UtcNow - m_StopRequestedAt).TotalSeconds > 45)
            {
                var answer = MessageBox.Show(this,
                    L.Tr("O servidor não desligou depois do pedido. Encerrar à força?\n(O progresso desde o último salvamento pode ser perdido.)"),
                    "SKALIA", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer == DialogResult.Yes)
                {
                    m_Server.Kill();
                }
                return;
            }
            m_StopRequestedAt = DateTime.UtcNow;
            m_Server.RequestStop();
        }

        private void Tick()
        {
            if (m_Preparing)
            {
                return;
            }
            var running = m_Server.IsRunning;
            var ready = running && m_Server.IsReady;
            if (ready && !m_WasReady)
            {
                AppendLog(L.Tr("Servidor pronto."));
            }
            m_WasReady = ready;
            if (!running)
            {
                m_StopRequestedAt = DateTime.MinValue;
            }

            var stopping = m_StopRequestedAt != DateTime.MinValue;
            if (!running)
            {
                SetStatus(L.Tr("Pronto para jogar"), Theme.Muted, L.Tr("O servidor sobe quando você clicar em JOGAR."));
            }
            else if (stopping)
            {
                SetStatus(L.Tr("Salvando..."), Theme.Warn, L.Tr("Desligando o servidor."));
            }
            else if (ready)
            {
                SetStatus(L.Tr("Servidor online"), Theme.Good, L.Tr("Clique em ENTRAR NO JOGO."));
            }
            else
            {
                SetStatus(L.Tr("Carregando mapa..."), Theme.Warn, L.Tr("Aguarde alguns instantes."));
            }

            m_PlayButton.Text = ready ? L.Tr("ENTRAR NO JOGO") : L.Tr("JOGAR");
            m_PlayButton.Enabled = !running || ready;
            m_StopButton.Visible = running;
            m_StopButton.Text = stopping && (DateTime.UtcNow - m_StopRequestedAt).TotalSeconds > 45
                ? L.Tr("Encerrar à força") : L.Tr("Salvar e parar servidor");
            TailServerLog();
        }

        private void SetStatus(string text, Color color, string detail)
        {
            m_Status.Text = "●  " + text;
            m_Status.ForeColor = color;
            m_StatusDetail.Text = detail;
        }

        private void TailServerLog()
        {
            try
            {
                if (!File.Exists(m_Paths.ServerLogFile))
                {
                    return;
                }
                using (var stream = new FileStream(m_Paths.ServerLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length < m_LogPosition)
                    {
                        m_LogPosition = 0;
                    }
                    if (stream.Length == m_LogPosition)
                    {
                        return;
                    }
                    stream.Seek(m_LogPosition, SeekOrigin.Begin);
                    var buffer = new byte[Math.Min(256 * 1024, stream.Length - m_LogPosition)];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    m_LogPosition += read;
                    var text = Encoding.UTF8.GetString(buffer, 0, read);
                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.TrimEnd('\r');
                        if (trimmed.Length > 0
                            && !trimmed.Contains(" asset file contents into memory")
                            && !trimmed.Contains("] Loaded ")
                            && !trimmed.Contains("Discovered assembly")
                            && !trimmed.Contains("] Searched "))
                        {
                            AppendLog(trimmed);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private void AppendLog(string line)
        {
            if (m_Log == null)
            {
                return;
            }
            if (m_Log.TextLength > 200000)
            {
                m_Log.Text = m_Log.Text.Substring(m_Log.TextLength - 100000);
            }
            m_Log.AppendText(line + Environment.NewLine);
        }

        private static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (m_Server.IsRunning)
            {
                var answer = MessageBox.Show(this,
                    L.Tr("O servidor continua rodando. Salvar e desligar agora?"),
                    "SKALIA", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (answer == DialogResult.Yes)
                {
                    m_Server.RequestStop();
                }
            }
            try
            {
                if (m_Paths.U3DSDir != null)
                {
                    ReadUiIntoSettings();
                }
            }
            catch
            {
            }
            base.OnFormClosing(e);
        }
    }
}
