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
        private readonly Dictionary<string, Dropdown> m_RuleCombos = new Dictionary<string, Dropdown>();
        private readonly Dictionary<string, Control> m_PluginControls = new Dictionary<string, Control>();
        private Dropdown m_BotProfile;
        private bool m_ApplyingProfile;
        private TextBox m_Kit;
        private Label m_KitHint;
        private string m_KitMap;

        private Panel m_Content;
        private TextBox m_Name, m_Welcome, m_Password, m_BotNames, m_Workshop, m_Log;
        private Dropdown m_Map, m_Difficulty, m_Perspective;
        private NumberBox m_MaxPlayers, m_Port, m_BotCount;
        private ToggleSwitch m_PvP, m_Cheats, m_ShowConsole, m_AutoCallBots, m_BotLogs, m_FullServerLog;
        private int m_LastMeshStep = -1;
        private bool m_ShowingTrace;
        private Panel m_Dev;
        private Label m_DevHeader;
        private FlowLayoutPanel m_PluginList;
        private Panel m_Advanced;
        private Label m_AdvancedHeader;
        private FlatButton m_PlayButton, m_StopButton;
        private StatusLine m_Status;
        private Label m_StatusDetail;
        private Panel m_Footer, m_StopGap;
        private bool? m_FooterRunning;

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
            Theme.DarkTitleBar(this);
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
            AddPage(BuildGameplayPage());
            AddPage(BuildPluginsPage());
            AddPage(BuildConsolePage());
            WirePluginDependencies();
            ShowPage(0);
        }

        private Control BuildSidebar()
        {
            // Tudo na barra lateral comeca em x = 24 (texto do menu incluso).
            const int padX = 24;
            var sidebar = new Panel { Dock = DockStyle.Left, Width = 250, BackColor = Theme.Sidebar };

            var brand = new Panel { Dock = DockStyle.Top, Height = 156, BackColor = Theme.Sidebar };
            brand.Controls.Add(new PictureBox
            {
                Image = LoadLogo(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Location = new Point(padX, 28),
                Size = new Size(150, 46),
                BackColor = Theme.Sidebar,
            });
            brand.Controls.Add(new Label
            {
                UseMnemonic = false,
                Text = "SKALIA",
                Font = Theme.Bold(22f),
                ForeColor = Theme.Text,
                AutoSize = true,
                Location = new Point(padX - 2, 80),
            });
            brand.Controls.Add(new Label
            {
                UseMnemonic = false,
                Text = L.Tr("Unturned offline com bots"),
                Font = Theme.Regular(9.5f),
                ForeColor = Theme.Muted,
                AutoSize = true,
                Location = new Point(padX - 1, 120),
            });

            var nav = new Panel { Dock = DockStyle.Top, Height = 4 * 42 + 8, BackColor = Theme.Sidebar, Padding = new Padding(0, 8, 0, 0) };
            var labels = new[] { L.Tr("Servidor"), L.Tr("Jogabilidade"), L.Tr("Plugins e Workshop"), L.Tr("Console") };
            for (var i = 0; i < labels.Length; i++)
            {
                var index = i;
                var button = new NavButton(labels[i]) { Dock = DockStyle.Top };
                button.Click += (s, e) => ShowPage(index);
                m_Nav.Add(button);
                nav.Controls.Add(button);
                button.BringToFront();
            }

            m_Footer = new Panel { Dock = DockStyle.Bottom, BackColor = Theme.Sidebar, Padding = new Padding(padX, 0, padX, 24) };
            m_StopButton = new FlatButton(L.Tr("Salvar e parar servidor"), false) { Dock = DockStyle.Bottom };
            m_StopButton.Click += (s, e) => StopServer();
            m_StopGap = new Panel { Dock = DockStyle.Bottom, Height = 8, BackColor = Theme.Sidebar };
            m_PlayButton = new FlatButton(L.Tr("JOGAR"), true) { Dock = DockStyle.Bottom };
            m_PlayButton.Click += (s, e) => Play();
            m_StatusDetail = new Label { UseMnemonic = false, Dock = DockStyle.Bottom, Height = 36, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Padding = new Padding(16, 2, 0, 0) };
            m_Status = new StatusLine { Dock = DockStyle.Bottom, Height = 22 };
            // Dock Bottom: o ultimo adicionado fica mais embaixo.
            m_Footer.Controls.Add(m_Status);
            m_Footer.Controls.Add(m_StatusDetail);
            m_Footer.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 8, BackColor = Theme.Sidebar });
            m_Footer.Controls.Add(m_PlayButton);
            m_Footer.Controls.Add(m_StopGap);
            m_Footer.Controls.Add(m_StopButton);
            m_FooterRunning = null;
            UpdateFooter(false);

            var language = new Panel { Dock = DockStyle.Bottom, Height = 20 + 4 + Theme.InputHeight + 24, BackColor = Theme.Sidebar, Padding = new Padding(padX, 0, padX, 24) };
            var languageBox = new Dropdown { Dock = DockStyle.Bottom };
            languageBox.Items.Add(new Choice(L.English, "English"));
            languageBox.Items.Add(new Choice(L.Portuguese, "Português (Brasil)"));
            languageBox.SelectedIndex = L.IsPortuguese ? 1 : 0;
            languageBox.SelectedIndexChanged += (s, e) => ChangeLanguage(((Choice)languageBox.SelectedItem).Value);
            language.Controls.Add(new Label { UseMnemonic = false, Text = L.Tr("Idioma"), Dock = DockStyle.Bottom, Height = 24, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Padding = new Padding(0, 0, 0, 4), TextAlign = ContentAlignment.BottomLeft });
            language.Controls.Add(languageBox);

            sidebar.Controls.Add(language);
            sidebar.Controls.Add(m_Footer);
            sidebar.Controls.Add(nav);
            sidebar.Controls.Add(brand);
            nav.BringToFront();
            return sidebar;
        }

        private Control BuildServerPage()
        {
            var page = NewPage(L.Tr("Servidor"), L.Tr("Escolha o mapa e clique em JOGAR. O resto já vem pronto."));

            m_Name = new TextBox();
            m_Map = new Dropdown();
            m_Map.SelectedIndexChanged += (s, e) => SwitchKitMap();
            m_Difficulty = new Dropdown();
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

            Stack(page, Spacer(SectionGap));
            m_AdvancedHeader = CollapsibleHeader(() => SetAdvancedVisible(!m_Advanced.Visible));
            Stack(page, m_AdvancedHeader);

            m_Advanced = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window };

            m_BotCount = new NumberBox { Minimum = 0, Maximum = 48 };
            m_BotNames = new TextBox { Multiline = true, Height = 120, ScrollBars = ScrollBars.Vertical };
            m_AutoCallBots = new ToggleSwitch(L.Tr("Bots entram sozinhos"), L.Tr("Entram assim que o mapa termina de carregar."));
            var bots = NewCard();
            AddSection(bots, L.Tr("Bots"));
            AddField(bots, L.Tr("Quantidade"), m_BotCount);
            AddField(bots, L.Tr("Nomes"), m_BotNames, L.Tr("Um por linha. Vazio = nomes gerados automaticamente."));
            AddPluginOptions(bots, PluginOptions.Bots);
            AddWide(bots, m_AutoCallBots);
            var behavior = NewCard();
            AddSection(behavior, L.Tr(PluginOptions.BotDifficulty));
            AddWide(behavior, Hint(L.Tr("Padrão é como os jogadores gravados jogam hoje, tudo em 1x. Mexer em um modificador vira Personalizado.")));
            m_BotProfile = new Dropdown();
            foreach (var profile in PluginOptions.BotProfiles)
            {
                m_BotProfile.Items.Add(new Choice(profile.Key, L.Tr(profile.Value)));
            }
            AddField(behavior, L.Tr("Perfil"), m_BotProfile);
            AddPluginOptions(behavior, PluginOptions.BotDifficulty);
            WireBotProfile();

            m_MaxPlayers = new NumberBox { Minimum = 1, Maximum = 200 };
            m_Perspective = new Dropdown();
            m_Perspective.Items.AddRange(new object[]
            {
                new Choice("Both", L.Tr("Primeira e terceira pessoa")),
                new Choice("First", L.Tr("Somente primeira pessoa")),
                new Choice("Third", L.Tr("Somente terceira pessoa")),
                new Choice("Vehicle", L.Tr("Terceira pessoa só em veículos")),
            });
            m_Port = new NumberBox { Minimum = 1024, Maximum = 65000 };
            m_Password = new TextBox { UseSystemPasswordChar = true };
            m_Cheats = new ToggleSwitch(L.Tr("Comandos de administrador"), L.Tr("Libera /give, /tp, /vehicle e outros comandos para você."));
            m_ShowConsole = new ToggleSwitch(L.Tr("Mostrar a janela do servidor"), L.Tr("Abre o console do servidor junto com o jogo."));
            var server = NewCard();
            AddSection(server, L.Tr("Servidor"));
            AddField(server, L.Tr("Jogadores máximos"), m_MaxPlayers);
            AddField(server, L.Tr("Câmera"), m_Perspective);
            AddField(server, L.Tr("Porta"), m_Port, L.Tr("Mude só se outro programa já usa esta porta."));
            AddField(server, L.Tr("Senha"), m_Password, L.Tr("Opcional, para amigos na mesma rede."));
            m_Welcome = new TextBox();
            AddField(server, L.Tr("Boas-vindas"), m_Welcome, L.Tr("Mensagem no chat quando alguém entra. Opcional."));
            // Campos primeiro, interruptores depois, como nos outros cards.
            AddWide(server, m_Cheats);
            AddWide(server, m_ShowConsole);

            // Um assunto por card: bots, comportamento, cada grupo de regras e o servidor.
            Stack(m_Advanced, bots);
            Stack(m_Advanced, Spacer(CardGap));
            Stack(m_Advanced, behavior);
            foreach (var group in new[] { GameRules.World, GameRules.Survival, GameRules.Combat })
            {
                var rules = NewCard();
                AddSection(rules, L.Tr(group));
                foreach (var rule in GameRules.All.Where(r => r.Group == group))
                {
                    AddField(rules, L.Tr(rule.Title), RuleCombo(rule), L.Tr(rule.Hint));
                }
                Stack(m_Advanced, Spacer(CardGap));
                Stack(m_Advanced, rules);
            }
            Stack(m_Advanced, Spacer(CardGap));
            Stack(m_Advanced, server);
            // So aparece com a secao aberta: separa o ultimo card do titulo seguinte.
            Stack(m_Advanced, Spacer(SectionGap - CardGap));
            Stack(page, m_Advanced);

            Stack(page, Spacer(CardGap));
            m_DevHeader = CollapsibleHeader(() => SetDevVisible(!m_Dev.Visible));
            Stack(page, m_DevHeader);

            m_Dev = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window };
            m_BotLogs = new ToggleSwitch(L.Tr("Log dos bots"), L.Tr("Registra cada decisão dos bots. Pesado; só para investigar problemas."));
            var dev = NewCard();
            AddWide(dev, Hint(L.Tr("Só mexa aqui se você sabe o que está fazendo.")));
            AddWide(dev, m_BotLogs);
            m_FullServerLog = new ToggleSwitch(L.Tr("Log completo do servidor"), L.Tr("Mostra no Console tudo o que o servidor escreve. Desligado, só avisos e erros."));
            AddWide(dev, m_FullServerLog);
            m_FullServerLog.CheckedChanged += (s, e) => m_Settings.FullServerLog = m_FullServerLog.Checked;
            Stack(m_Dev, dev);
            Stack(page, m_Dev);
            Stack(page, Spacer(PageBottom));
            SetAdvancedVisible(false);
            SetDevVisible(false);
            return page.Parent;
        }

        private Control BuildGameplayPage()
        {
            var page = NewPage(L.Tr("Jogabilidade"), L.Tr("Comandos e regras extras que vêm com o SKALIA."));
            var first = true;
            foreach (var group in new[] { PluginOptions.Commands, PluginOptions.World })
            {
                if (!first)
                {
                    Stack(page, Spacer(CardGap));
                }
                first = false;
                var card = NewCard();
                AddSection(card, L.Tr(group));
                if (group == PluginOptions.World)
                {
                    m_KitMap = null;
                    m_Kit = new TextBox { Multiline = true, Height = 110, ScrollBars = ScrollBars.Vertical };
                    m_KitHint = AddField(card, L.Tr("Kit inicial"), m_Kit, " ");
                }
                AddPluginOptions(card, group);
                Stack(page, card);
            }
            Stack(page, Spacer(PageBottom));
            return page.Parent;
        }

        private void AddPluginOptions(TableLayoutPanel card, string group)
        {
            foreach (var option in PluginOptions.All.Where(o => o.Group == group && !o.Fixed))
            {
                if (option.IsToggle)
                {
                    var toggle = new ToggleSwitch(L.Tr(option.Title), option.Hint == null ? null : L.Tr(option.Hint));
                    m_PluginControls[option.Id] = toggle;
                    AddWide(card, toggle);
                    continue;
                }
                var combo = new Dropdown();
                foreach (var choice in option.Choices)
                {
                    combo.Items.Add(new Choice(choice.Key, L.Tr(choice.Value)));
                }
                combo.SelectedIndex = 0;
                m_PluginControls[option.Id] = combo;
                AddField(card, L.Tr(option.Title), combo, option.Hint == null ? null : L.Tr(option.Hint));
            }
        }

        /// <summary>
        /// Perfil de dificuldade dos bots: escolher um perfil preenche os modificadores; mexer num
        /// modificador sem bater com nenhum perfil vira Personalizado.
        /// </summary>
        private void WireBotProfile()
        {
            var modifiers = PluginOptions.All.Where(o => o.Group == PluginOptions.BotDifficulty)
                .Select(o => m_PluginControls[o.Id] as Dropdown).ToList();
            m_BotProfile.SelectedIndexChanged += (s, e) =>
            {
                var chosen = (m_BotProfile.SelectedItem as Choice)?.Value;
                if (m_ApplyingProfile || chosen == null || !PluginOptions.BotProfileValues.TryGetValue(chosen, out var values))
                {
                    return;
                }
                m_ApplyingProfile = true;
                foreach (var pair in values)
                {
                    if (m_PluginControls.TryGetValue(pair.Key, out var control) && control is Dropdown combo)
                    {
                        combo.SelectedItem = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Value == pair.Value) ?? combo.SelectedItem;
                    }
                }
                m_ApplyingProfile = false;
            };
            foreach (var combo in modifiers)
            {
                combo.SelectedIndexChanged += (s, e) =>
                {
                    if (!m_ApplyingProfile)
                    {
                        SelectProfileFromModifiers();
                    }
                };
            }
        }

        /// <summary>Marca o perfil que bate com os modificadores atuais, ou Personalizado.</summary>
        private void SelectProfileFromModifiers()
        {
            var current = PluginOptions.All.Where(o => o.Group == PluginOptions.BotDifficulty)
                .ToDictionary(o => o.Id, o => ((m_PluginControls[o.Id] as Dropdown)?.SelectedItem as Choice)?.Value);
            var match = PluginOptions.BotProfileValues.FirstOrDefault(p => p.Value.All(v => current.TryGetValue(v.Key, out var c) && c == v.Value)).Key
                ?? PluginOptions.CustomProfile;
            m_ApplyingProfile = true;
            m_BotProfile.SelectedItem = m_BotProfile.Items.Cast<Choice>().First(c => c.Value == match);
            m_ApplyingProfile = false;
        }

        /// <summary>Campo que so vale com um toggle ligado (ex.: espera do teleporte) fica desativado sem ele.</summary>
        private void WirePluginDependencies()
        {
            foreach (var option in PluginOptions.All.Where(o => o.DependsOn != null))
            {
                if (!m_PluginControls.TryGetValue(option.Id, out var target))
                {
                    continue;
                }
                var toggles = option.DependsOn.Select(id => m_PluginControls.TryGetValue(id, out var c) ? c as ToggleSwitch : null)
                    .Where(t => t != null).ToList();
                EventHandler update = (s, e) => target.Enabled = toggles.Any(t => t.Checked);
                foreach (var toggle in toggles)
                {
                    toggle.CheckedChanged += update;
                }
                update(null, EventArgs.Empty);
            }
        }

        private Control BuildPluginsPage()
        {
            var page = NewPage(L.Tr("Plugins e Workshop"), L.Tr("Adicione plugins OpenMod e itens da Workshop ao seu servidor."));

            var plugins = NewCard();
            AddSection(plugins, L.Tr("Plugins"));
            m_PluginList = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Card, Margin = new Padding(0) };
            AddWide(plugins, m_PluginList);
            AddWide(plugins, Hint(L.Tr("Coloque a DLL de um plugin OpenMod na pasta de plugins e abra o SKALIA de novo.")));
            var openPlugins = new FlatButton(L.Tr("Abrir pasta de plugins"), false) { Width = 220, Margin = new Padding(0, RowGap, 0, RowGap) };
            openPlugins.Click += (s, e) => OpenFolder(m_Paths.PluginsSourceDir);
            AddWide(plugins, openPlugins);
            Stack(page, plugins);

            Stack(page, Spacer(CardGap));
            m_Workshop = new TextBox { Multiline = true, Height = 110, ScrollBars = ScrollBars.Vertical };
            var workshop = NewCard();
            AddSection(workshop, L.Tr("Workshop"));
            AddField(workshop, L.Tr("IDs dos itens"), m_Workshop, L.Tr("Um por linha. Mapas da Workshop entram aqui sozinhos ao serem escolhidos."));
            Stack(page, workshop);
            Stack(page, Spacer(PageBottom));
            return page.Parent;
        }

        private Control BuildConsolePage()
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(PagePadX, PagePadTop, PagePadX, PageBottom) };
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
            Theme.DarkScrollbars(m_Log);
            var logHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar, Padding = new Padding(12) };
            logHost.Controls.Add(m_Log);
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = CardGap + 38, BackColor = Theme.Window, Padding = new Padding(0, CardGap, 0, 0) };
            var folder = new FlatButton(L.Tr("Abrir pasta do servidor"), false) { Dock = DockStyle.Left, Width = 220 };
            folder.Click += (s, e) => OpenFolder(m_Paths.ProfileDir);
            bottom.Controls.Add(folder);
            outer.Controls.Add(logHost);
            outer.Controls.Add(bottom);
            outer.Controls.Add(title);
            return outer;
        }

        // ------------------------------------------------------------ helpers

        // Grade de espacamento (DESIGN.md): 4, 8, 12, 24 e as margens da pagina.
        private const int PagePadX = 36;
        private const int PagePadTop = 28;
        private const int PageBottom = 32;
        private const int CardGap = 12;
        private const int SectionGap = 24;
        private const int RowGap = 6;
        private const int CardPadY = 14;
        /// <summary>Card = respiro + rotulo + campo + respiro; a pagina nunca fica mais larga que isso.</summary>
        private const int MaxContentWidth = Theme.CardPadX * 2 + Theme.LabelWidth + Theme.FieldWidth;
        private const int WideWidth = Theme.LabelWidth + Theme.FieldWidth;

        private Panel NewPage(string title, string subtitle)
        {
            var outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, AutoScroll = true, Padding = new Padding(PagePadX, PagePadTop, PagePadX, 0) };
            Theme.DarkScrollbars(outer);
            // Conteudo com largura maxima: em janela larga sobra espaco a direita, nao no meio dos cards.
            // Recalcula tambem quando a barra de rolagem aparece (ela come largura sem mudar o tamanho da janela).
            EventHandler fit = (s, e) =>
            {
                var client = outer.ClientSize.Width;
                if (outer.VerticalScroll.Visible && client == outer.Width)
                {
                    client -= SystemInformation.VerticalScrollBarWidth;
                }
                var right = Math.Max(PagePadX, client - PagePadX - MaxContentWidth);
                if (outer.Padding.Right != right)
                {
                    outer.Padding = new Padding(PagePadX, PagePadTop, right, 0);
                }
            };
            outer.Resize += fit;
            outer.ClientSizeChanged += fit;
            outer.Layout += (s, e) => fit(s, e);
            var stack = new Panel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, BackColor = Theme.Window };
            outer.Controls.Add(stack);
            Stack(stack, PageTitle(title, subtitle));
            return stack;
        }

        private static Control PageTitle(string title, string subtitle)
        {
            var titleFont = Theme.Bold(20f);
            var subtitleFont = Theme.Regular(10f);
            var titleHeight = TextRenderer.MeasureText("Ag", titleFont).Height;
            var subtitleHeight = TextRenderer.MeasureText("Ag", subtitleFont).Height;
            var header = new Panel { Dock = DockStyle.Top, Height = titleHeight + 4 + subtitleHeight + SectionGap, BackColor = Theme.Window };
            // -3/-1: tira o respiro lateral que o TextRenderer poe antes da primeira letra.
            header.Controls.Add(new Label { UseMnemonic = false, Text = title, Font = titleFont, ForeColor = Theme.Text, AutoSize = true, Location = new Point(-3, 0) });
            header.Controls.Add(new Label { UseMnemonic = false, Text = subtitle, Font = subtitleFont, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(-1, titleHeight + 4) });
            return header;
        }

        /// <summary>Titulo clicavel de secao recolhivel ("Advanced settings", "Developer").</summary>
        private static Label CollapsibleHeader(Action toggle)
        {
            var header = new Label
            {
                UseMnemonic = false,
                Dock = DockStyle.Top,
                Height = 32,
                Font = Theme.Semibold(10.5f),
                ForeColor = Theme.Muted,
                Cursor = Cursors.Hand,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 0, 0, 4),
            };
            header.Click += (s, e) => toggle();
            header.MouseEnter += (s, e) => header.ForeColor = Theme.Text;
            header.MouseLeave += (s, e) => header.ForeColor = Theme.Muted;
            return header;
        }

        private static void Stack(Control parent, Control child)
        {
            child.Dock = DockStyle.Top;
            parent.Controls.Add(child);
            child.BringToFront();
        }

        private static Panel Spacer(int height) => new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Window };

        /// <summary>
        /// Card de duas colunas (rotulo | campo). Cada linha tem RowGap em cima e embaixo, entao
        /// linhas ficam a 12px uma da outra e o conteudo a CardPadY + RowGap = 20px da borda.
        /// </summary>
        private static TableLayoutPanel NewCard()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Theme.Card,
                Padding = new Padding(Theme.CardPadX, CardPadY, Theme.CardPadX, CardPadY),
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.LabelWidth));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return grid;
        }

        private static void AddSection(TableLayoutPanel grid, string title)
        {
            var label = new Label { UseMnemonic = false, Text = title, Font = Theme.Semibold(10.5f), ForeColor = Theme.Text, AutoSize = true, Margin = new Padding(0, RowGap, 0, 8) };
            AddWide(grid, label);
        }

        /// <summary>Linha rotulo | campo. Devolve o rotulo da dica (ou null).</summary>
        private static Label AddField(TableLayoutPanel grid, string label, Control control, string hint = null)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var labelFont = Theme.Regular(10.5f);
            // Rotulo centrado na altura do campo; quebra a linha se for longo demais para a coluna.
            var labelTop = RowGap + Math.Max(0, (Theme.InputHeight - TextRenderer.MeasureText("Ag", labelFont).Height) / 2);
            grid.Controls.Add(new Label
            {
                UseMnemonic = false,
                Text = label,
                AutoSize = true,
                ForeColor = Theme.Muted,
                Font = labelFont,
                Margin = new Padding(0, labelTop, 12, RowGap),
                MaximumSize = new Size(Theme.LabelWidth - 12, 0),
            }, 0, row);
            Theme.StyleInput(control);
            if (control is TextBox box)
            {
                control = new InputFrame(box);
            }
            control.Width = Theme.FieldWidth;
            if (hint == null)
            {
                control.Margin = new Padding(0, RowGap, 0, RowGap);
                grid.Controls.Add(control, 1, row);
                return null;
            }
            control.Margin = new Padding(0);
            var cell = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Theme.Card, Margin = new Padding(0, RowGap, 0, RowGap) };
            cell.Controls.Add(control);
            var hintLabel = new Label { UseMnemonic = false, Text = hint, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Margin = new Padding(0, 4, 0, 0), MaximumSize = new Size(Theme.FieldWidth, 0) };
            cell.Controls.Add(hintLabel);
            grid.Controls.Add(cell, 1, row);
            return hintLabel;
        }

        /// <summary>O kit inicial e por mapa: guarda o texto do mapa anterior e mostra o do novo.</summary>
        private void SwitchKitMap()
        {
            if (m_Kit == null)
            {
                return;
            }
            StoreKit();
            m_KitMap = (m_Map.SelectedItem as MapEntry)?.Name;
            m_Kit.Text = StartingKits.TextFor(m_Settings.StartingKits, m_KitMap);
            m_KitHint.Text = m_KitMap == null
                ? L.Tr("Escolha um mapa na página Servidor.")
                : L.Tr("Ao nascer em ") + m_KitMap + L.Tr(". Um ID por linha; \"ID x2\" dá 2 e \"ID/ID\" sorteia um. Vazio = sem kit.");
        }

        private void StoreKit()
        {
            if (m_Kit == null || m_KitMap == null)
            {
                return;
            }
            var text = m_Kit.Text.Replace("\r\n", "\n").Trim().Replace("\n", Environment.NewLine);
            var saved = m_Settings.StartingKits.Keys.FirstOrDefault(k => string.Equals(k, m_KitMap, StringComparison.OrdinalIgnoreCase));
            if (saved != null)
            {
                m_Settings.StartingKits.Remove(saved);
            }
            // Igual ao exemplo: nao guarda, para o mapa seguir o kit de exemplo.
            if (text != StartingKits.TextFor(null, m_KitMap).Trim())
            {
                m_Settings.StartingKits[m_KitMap] = text;
            }
        }

        private static void AddWide(TableLayoutPanel grid, Control control)
        {
            var row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (control is ToggleSwitch)
            {
                control.Width = WideWidth;
                control.Margin = new Padding(0, RowGap, 0, RowGap);
            }
            else if (control is FlowLayoutPanel)
            {
                control.Margin = new Padding(0);
            }
            grid.Controls.Add(control, 0, row);
            grid.SetColumnSpan(control, 2);
        }

        private Dropdown RuleCombo(GameRule rule)
        {
            var combo = new Dropdown();
            combo.Items.AddRange(rule.Options.Cast<object>().ToArray());
            combo.SelectedIndex = 0;
            m_RuleCombos[rule.Id] = combo;
            return combo;
        }

        private static Label Hint(string text) => new Label { UseMnemonic = false, Text = text, AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Regular(9f), Margin = new Padding(0, RowGap, 0, RowGap), MaximumSize = new Size(WideWidth, 0) };

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
            if (index == m_Pages.Count - 1 && m_Log != null)
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
            var devOpen = m_Dev.Visible;
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
            m_PluginControls.Clear();
            BuildUi();
            if (m_Paths.U3DSDir != null)
            {
                LoadSettingsIntoUi();
            }
            m_Log.Text = log;
            SetAdvancedVisible(advanced);
            SetDevVisible(devOpen);
            ShowPage(Math.Max(0, page));
            ResumeLayout(true);
            Tick();
        }

        private void SetAdvancedVisible(bool visible)
        {
            m_Advanced.Visible = visible;
            m_AdvancedHeader.Text = (visible ? "▾  " : "▸  ") + L.Tr("Configurações avançadas");
        }

        private void SetDevVisible(bool visible)
        {
            m_Dev.Visible = visible;
            m_DevHeader.Text = (visible ? "▾  " : "▸  ") + L.Tr("Desenvolvedor");
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
            m_BotLogs.Checked = m_Settings.BotLogs;
            m_FullServerLog.Checked = m_Settings.FullServerLog;
            foreach (var option in PluginOptions.All)
            {
                if (!m_PluginControls.TryGetValue(option.Id, out var control))
                {
                    continue;
                }
                var value = PluginOptions.Value(m_Settings.PluginOptions, option);
                if (control is ToggleSwitch toggle)
                {
                    toggle.Checked = value == "true";
                }
                else if (control is Dropdown combo)
                {
                    var match = combo.Items.Cast<Choice>().FirstOrDefault(c => c.Value == value);
                    if (match != null)
                    {
                        combo.SelectedItem = match;
                    }
                }
            }
            SelectProfileFromModifiers();

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
                toggle.Width = WideWidth;
                toggle.Margin = new Padding(0, RowGap, 0, RowGap);
                m_PluginToggles.Add(toggle);
                m_PluginList.Controls.Add(toggle);
            }
            if (!plugins.Any(plugin => string.Equals(plugin, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase)))
            {
                AppendLog(L.Tr("AVISO: nenhum plugin encontrado em ") + m_Paths.PluginsSourceDir + ".");
            }
            m_Workshop.Text = string.Join(Environment.NewLine, m_Settings.WorkshopIds);
        }

        private static void SelectChoice(Dropdown combo, string value, string fallback)
        {
            var choices = combo.Items.Cast<Choice>().ToList();
            combo.SelectedItem = choices.FirstOrDefault(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase))
                ?? choices.First(c => c.Value == fallback);
        }

        private static string ChoiceValue(Dropdown combo, string fallback) => (combo.SelectedItem as Choice)?.Value ?? fallback;

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
            StoreKit();
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
            m_Settings.BotLogs = m_BotLogs.Checked;
            m_Settings.FullServerLog = m_FullServerLog.Checked;
            foreach (var pair in m_PluginControls)
            {
                if (pair.Value is ToggleSwitch toggle)
                {
                    m_Settings.PluginOptions[pair.Key] = toggle.Checked ? "true" : "false";
                }
                else if (pair.Value is Dropdown combo && combo.SelectedItem is Choice choice)
                {
                    m_Settings.PluginOptions[pair.Key] = choice.Value;
                }
            }

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

        private static decimal Clamp(int value, NumberBox box) => Math.Max(box.Minimum, Math.Min(box.Maximum, value));

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
                AppendLog(L.Tr("SKALIA é um projeto gratuito e de código aberto: github.com/Rafael7121/Skalia. Se você pagou por ele, foi enganado."));
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
            var mesh = running && !ready ? m_Server.BotMeshPercent : null;
            if (mesh.HasValue && mesh.Value != m_LastMeshStep)
            {
                // O plugin avanca por etapas (0, 5, 10, 15, 25...); cada uma vira uma linha no Console.
                m_LastMeshStep = mesh.Value;
                AppendLog(L.Tr("Preparando os bots: ") + mesh.Value + "%");
            }
            if (!running || ready)
            {
                m_LastMeshStep = -1;
            }
            if (ready && !m_WasReady)
            {
                AppendLog(L.Tr("Servidor pronto. Clique em ENTRAR NO JOGO."));
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
            else if (mesh.HasValue)
            {
                SetStatus(L.Tr("Preparando os bots... ") + mesh.Value + "%", Theme.Warn, L.Tr("Montar o caminho dos bots pode levar até 2 minutos; o número fica parado nesse tempo."));
            }
            else
            {
                SetStatus(L.Tr("Carregando mapa..."), Theme.Warn, L.Tr("Aguarde alguns instantes."));
            }

            m_PlayButton.Text = ready ? L.Tr("ENTRAR NO JOGO") : L.Tr("JOGAR");
            m_PlayButton.Enabled = !running || ready;
            UpdateFooter(running);
            m_StopButton.Text = stopping && (DateTime.UtcNow - m_StopRequestedAt).TotalSeconds > 45
                ? L.Tr("Encerrar à força") : L.Tr("Salvar e parar servidor");
            TailServerLog();
        }

        private void SetStatus(string text, Color color, string detail)
        {
            m_Status.Text = text;
            m_Status.DotColor = color;
            m_StatusDetail.Text = detail;
        }

        /// <summary>O botao de parar so existe com o servidor ligado; o rodape encolhe sem ele.</summary>
        private void UpdateFooter(bool running)
        {
            if (m_FooterRunning == running)
            {
                return;
            }
            m_FooterRunning = running;
            m_StopButton.Visible = running;
            m_StopGap.Visible = running;
            var height = m_Status.Height + m_StatusDetail.Height + 8 + m_PlayButton.Height + m_Footer.Padding.Bottom;
            if (running)
            {
                height += m_StopGap.Height + m_StopButton.Height;
            }
            m_Footer.Height = height;
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
                        if (ShowServerLine(trimmed))
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

        private static readonly System.Text.RegularExpressions.Regex ProblemLine =
            new System.Text.RegularExpressions.Regex(@"\b(WRN|ERR|FTL|Warning|Error|Exception)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// O Console e para quem joga: do log do servidor so passam avisos e erros (com o stack trace
        /// logo abaixo). "Log completo do servidor" em Desenvolvedor mostra tudo.
        /// </summary>
        private bool ShowServerLine(string line)
        {
            if (line.Length == 0)
            {
                return false;
            }
            if (m_Settings.FullServerLog)
            {
                return !line.Contains(" asset file contents into memory")
                    && !line.Contains("] Loaded ")
                    && !line.Contains("Discovered assembly")
                    && !line.Contains("] Searched ");
            }
            if (m_ShowingTrace && (line.StartsWith(" ") || line.StartsWith("\t") || line.StartsWith("---")))
            {
                return true;
            }
            m_ShowingTrace = ProblemLine.IsMatch(line);
            return m_ShowingTrace;
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
