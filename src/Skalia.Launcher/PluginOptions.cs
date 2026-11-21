using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Skalia.Launcher
{
    /// <summary>
    /// Uma opcao do plugin que o launcher mostra e grava no config.yaml do SKALIA DEPS.
    /// Toggle grava true/false; Choice grava o valor escolhido em todos os caminhos.
    /// Titulos e dicas sao chaves de L.Tr (portugues).
    /// </summary>
    internal sealed class PluginOption
    {
        public string Id => Paths[0];
        public string[] Paths { get; set; }
        public string Group { get; set; }
        public string Title { get; set; }
        public string Hint { get; set; }
        public string Default { get; set; }
        /// <summary>Null = liga/desliga. Senao, pares (valor, rotulo).</summary>
        public KeyValuePair<string, string>[] Choices { get; set; }
        /// <summary>Id de um toggle: o campo fica desativado com ele desligado.</summary>
        public string[] DependsOn { get; set; }
        public bool IsToggle => Choices == null;
        /// <summary>Nao aparece na tela; o launcher grava sempre o padrao.</summary>
        public bool Fixed { get; set; }
    }

    internal static class PluginOptions
    {
        public const string Commands = "Comandos dos jogadores";
        public const string World = "Mundo";
        public const string Bots = "Bots";
        public const string BotBehavior = "Comportamento dos bots";
        public const string BotDifficulty = "Dificuldade dos bots";
        /// <summary>Perfil mostrado quando os modificadores nao batem com nenhum perfil pronto.</summary>
        public const string CustomProfile = "custom";

        public static readonly PluginOption[] All =
        {
            Toggle(Commands, "tpa:enabled", "Teleporte entre jogadores (/tpa)", "Pede para ir até outro jogador.", true),
            Toggle(Commands, "home:enabled", "Voltar para a cama (/home)", "Teleporta para uma cama sua.", true),
            Pick(Commands, new[] { "roleBenefits:default:homes" }, "Camas por jogador", null, "2",
                new[] { "home:enabled" }, C("1", "1"), C("2", "2"), C("3", "3"), C("5", "5")),
            Pick(Commands, new[] { "tpa:teleportDelaySeconds", "home:teleportDelaySeconds" }, "Espera do teleporte",
                "Levar dano de outro jogador durante a espera cancela.", "5",
                new[] { "tpa:enabled", "home:enabled" }, C("0", "Na hora"), C("3", "3 s"), C("5", "5 s"), C("10", "10 s")),
            Toggle(Commands, "vault:enabled", "Baú pessoal (/vault)", "Um baú que vai com você para qualquer lugar.", true),
            Pick(Commands, new[] { "roleBenefits:default:vaultWidth", "roleBenefits:default:vaultHeight" }, "Tamanho do baú", null, "6",
                new[] { "vault:enabled" }, C("4", "4 x 4"), C("6", "6 x 6"), C("8", "8 x 8")),
            Toggle(Commands, "inventorySort:commandEnabled", "Organizar inventário (/sort)", "Arruma os itens da mochila com um comando.", true),

            Toggle(World, "automaticReload:enabled", "Recarga automática", "Põe outro pente compatível quando a arma esvazia.", true),
            Toggle(World, "zombieSpawns:guaranteeMegas", "Mega Zombies garantidos", "Sempre um Mega por região; volta 10 min depois de morrer.", false),
            Toggle(World, "alicepackRewards:enabled", "Mochila dos Mega Zombies", "Todo Mega Zombie derruba Alicepacks ao morrer.", true),
            Toggle(World, "groundCleanup:enabled", "Limpar itens no chão", "Some com o que os jogadores largaram; o loot do mapa fica.", true),
            Pick(World, new[] { "groundCleanup:intervalSeconds" }, "Limpeza a cada", null, "600",
                new[] { "groundCleanup:enabled" }, C("300", "5 min"), C("600", "10 min"), C("1200", "20 min"), C("1800", "30 min")),

            Pick(Bots, new[] { "serverBot:respawnDelaySeconds" }, "Bots renascem em", null, "3",
                null, C("3", "3 s"), C("10", "10 s"), C("30", "30 s"), C("60", "60 s")),
            Toggle(Bots, "serverBot:movement:enabled", "Bots andam pelo mapa", "Desligado, os bots ficam parados.", true),

            // Humanizacao vinda das gravacoes: desligar so deixava o bot mais robotico. Sempre ligadas.
            Fixed(Toggle(BotBehavior, "serverBot:behavior:meleeAimVariation", null, null, true)),
            Fixed(Toggle(BotBehavior, "serverBot:behavior:hordeCaution", null, null, true)),
            Fixed(Toggle(BotBehavior, "serverBot:behavior:lootLook", null, null, true)),
            Fixed(Toggle(BotBehavior, "serverBot:behavior:humanTurning", null, null, true)),
            Fixed(Toggle(BotBehavior, "serverBot:behavior:routeHops", null, null, true)),
            Fixed(Toggle(BotBehavior, "serverBot:behavior:freshDeathAvoidance", null, null, true)),

            // Modificadores no estilo SPT: 1x = o comportamento tirado das gravacoes.
            Pick(BotDifficulty, new[] { "serverBot:modifiers:visionRange" }, "Alcance de visão", "Distância em que percebem jogadores e zumbis.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:fieldOfView" }, "Campo de visão", "Ângulo que enxergam, para os lados e para cima e baixo.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:hearing" }, "Audição", "Até onde ouvem tiros e balas passando perto.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:reactionTime" }, "Tempo de reação", "Abaixo de 1x viram e atiram mais rápido.", "1", null, Times(0.25, 0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:aimSpeed" }, "Velocidade da mira", "Acima de 1x travam no alvo mais rápido.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:accuracy" }, "Precisão", "Acima de 1x a mira fecha mais.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:courage" }, "Coragem", "Acima de 1x lutam com menos vida antes de fugir ou se curar.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:fightDistance" }, "Distância de luta", "Acima de 1x preferem lutar mais de longe.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:cover" }, "Uso de cobertura", "Acima de 1x se agacham e se escondem mais.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
            Pick(BotDifficulty, new[] { "serverBot:modifiers:looting" }, "Vontade de saquear", "Acima de 1x param mais para pegar loot antes de lutar.", "1", null, Times(0.5, 0.75, 1, 1.25, 1.5, 2)),
        };

        public static PluginOption Find(string id) => All.FirstOrDefault(o => o.Id == id);

        /// <summary>Perfis prontos de dificuldade: valor de cada modificador (o que faltar fica 1x).</summary>
        public static readonly KeyValuePair<string, string>[] BotProfiles =
        {
            C("default", "Padrão"), C("hard", "Difícil"), C("tryhard", "Tryhard"), C(CustomProfile, "Personalizado"),
        };

        public static readonly Dictionary<string, Dictionary<string, string>> BotProfileValues = new Dictionary<string, Dictionary<string, string>>
        {
            // Padrao e como os jogadores gravados jogam hoje.
            { "default", Profile(1, 1, 1, 1, 1, 1, 1, 1, 1, 1) },
            { "hard", Profile(1.25, 1, 1.25, 0.75, 1.25, 1.25, 1.25, 1, 1.25, 1) },
            { "tryhard", Profile(1.5, 1.25, 1.5, 0.5, 1.5, 1.5, 1.5, 1, 1.5, 0.75) },
        };

        private static Dictionary<string, string> Profile(double vision, double fov, double hearing, double reaction, double accuracy, double courage,
            double aimSpeed, double fightDistance, double cover, double looting) =>
            new Dictionary<string, string>
            {
                { "serverBot:modifiers:visionRange", N(vision) },
                { "serverBot:modifiers:fieldOfView", N(fov) },
                { "serverBot:modifiers:hearing", N(hearing) },
                { "serverBot:modifiers:reactionTime", N(reaction) },
                { "serverBot:modifiers:accuracy", N(accuracy) },
                { "serverBot:modifiers:courage", N(courage) },
                { "serverBot:modifiers:aimSpeed", N(aimSpeed) },
                { "serverBot:modifiers:fightDistance", N(fightDistance) },
                { "serverBot:modifiers:cover", N(cover) },
                { "serverBot:modifiers:looting", N(looting) },
            };

        private static string N(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

        private static KeyValuePair<string, string>[] Times(params double[] values) =>
            values.Select(v => C(N(v), N(v) + "x")).ToArray();

        private static PluginOption Fixed(PluginOption option)
        {
            option.Fixed = true;
            return option;
        }

        /// <summary>Valor salvo, ou o padrao quando nao ha valor valido.</summary>
        public static string Value(IDictionary<string, string> saved, PluginOption option)
        {
            if (option.Fixed)
            {
                return option.Default;
            }
            if (saved != null && saved.TryGetValue(option.Id, out var value) && value != null)
            {
                if (option.IsToggle ? value == "true" || value == "false" : option.Choices.Any(c => c.Key == value))
                {
                    return value;
                }
            }
            return option.Default;
        }

        /// <summary>Grava todas as opcoes no texto do config.yaml.</summary>
        public static string Apply(string yaml, IDictionary<string, string> saved)
        {
            foreach (var option in All)
            {
                var value = Value(saved, option);
                foreach (var path in option.Paths)
                {
                    yaml = SetPath(yaml, path, value);
                }
            }
            return yaml;
        }

        /// <summary>
        /// Troca o valor de uma chave aninhada ("a:b:c") num YAML indentado com 2 espacos,
        /// mantendo comentarios. Cria as secoes que faltarem no fim do bloco pai.
        /// </summary>
        public static string SetPath(string yaml, string path, string value)
        {
            var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
            var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
            var parts = path.Split(':');
            int start = 0, end = lines.Count;
            for (var depth = 0; depth < parts.Length; depth++)
            {
                var indent = new string(' ', depth * 2);
                var keyRegex = new Regex("^" + indent + Regex.Escape(parts[depth]) + @":(?=\s|$)");
                var found = -1;
                for (var i = start; i < end; i++)
                {
                    if (keyRegex.IsMatch(lines[i]))
                    {
                        found = i;
                        break;
                    }
                }
                if (found < 0)
                {
                    var insertAt = end;
                    // Fica dentro do bloco pai: recua sobre linhas em branco e comentarios da secao seguinte.
                    while (insertAt > start && IsBlankOrOuterComment(lines[insertAt - 1], indent.Length))
                    {
                        insertAt--;
                    }
                    var added = new List<string>();
                    for (var d = depth; d < parts.Length; d++)
                    {
                        var pad = new string(' ', d * 2);
                        added.Add(pad + parts[d] + ":" + (d == parts.Length - 1 ? " " + value : ""));
                    }
                    if (depth == 0)
                    {
                        while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                        {
                            lines.RemoveAt(lines.Count - 1);
                        }
                        lines.Add("");
                        lines.AddRange(added);
                        lines.Add("");
                    }
                    else
                    {
                        lines.InsertRange(insertAt, added);
                    }
                    return string.Join(newline, lines);
                }
                if (depth == parts.Length - 1)
                {
                    var m = Regex.Match(lines[found], "^(" + indent + Regex.Escape(parts[depth]) + @":)([ \t]*)([^#]*?)([ \t]*#.*)?$");
                    lines[found] = m.Groups[1].Value + " " + value + m.Groups[4].Value;
                    return string.Join(newline, lines);
                }
                // Bloco filho: ate a proxima linha com conteudo e indentacao <= a desta chave.
                var blockEnd = lines.Count;
                for (var i = found + 1; i < end; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                    {
                        continue;
                    }
                    if (line.Length - trimmed.Length <= indent.Length)
                    {
                        blockEnd = i;
                        break;
                    }
                }
                start = found + 1;
                end = Math.Min(blockEnd, end);
            }
            return yaml;
        }

        private static bool IsBlankOrOuterComment(string line, int childIndent)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0)
            {
                return true;
            }
            return trimmed.StartsWith("#") && line.Length - trimmed.Length < childIndent;
        }

        private static KeyValuePair<string, string> C(string value, string label) => new KeyValuePair<string, string>(value, label);

        private static PluginOption Toggle(string group, string path, string title, string hint, bool on) => new PluginOption
        {
            Group = group, Paths = new[] { path }, Title = title, Hint = hint, Default = on ? "true" : "false",
        };

        private static PluginOption Pick(string group, string[] paths, string title, string hint, string def, string[] dependsOn,
            params KeyValuePair<string, string>[] choices) => new PluginOption
        {
            Group = group, Paths = paths, Title = title, Hint = hint, Default = def, DependsOn = dependsOn, Choices = choices,
        };
    }

    /// <summary>
    /// Kit inicial por mapa (spawnLoadouts:maps:&lt;mapa&gt; do SKALIA DEPS), editado como texto:
    /// um item por linha, "ID x2" para quantidade, "ID/ID/ID" sorteia um, "#" comenta.
    /// Roupas sao vestidas e armas vao para as maos; o resto vai para a mochila.
    /// </summary>
    internal static class StartingKits
    {
        /// <summary>Kits que ja vinham no plugin; aparecem como exemplo ate o jogador mudar.</summary>
        public static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "California2", "4234/4235/4236/4237/4238/4239/4240/4241\n4301\n4400" },
            { "Arid Reborn", "19160\n19190\n19053 x2\n19633" },
            { "Washington", "183\n2\n13\n14" },
        };

        private static readonly Regex LineRegex = new Regex(@"^(\d+(?:\s*/\s*\d+)*)\s*(?:x\s*(\d+))?$", RegexOptions.IgnoreCase);

        /// <summary>Texto salvo para o mapa, ou o kit de exemplo, ou vazio.</summary>
        public static string TextFor(IDictionary<string, string> saved, string map)
        {
            if (string.IsNullOrEmpty(map))
            {
                return "";
            }
            if (saved != null)
            {
                foreach (var pair in saved)
                {
                    if (string.Equals(pair.Key, map, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value ?? "";
                    }
                }
            }
            return Defaults.TryGetValue(map, out var text) ? text.Replace("\n", Environment.NewLine) : "";
        }

        public sealed class Entry
        {
            public ushort[] Ids;
            public int Quantity;
        }

        /// <summary>Le o texto; linhas que nao entende vao para <paramref name="invalid"/>.</summary>
        public static List<Entry> Parse(string text, List<string> invalid)
        {
            var entries = new List<Entry>();
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw;
                var comment = line.IndexOf('#');
                if (comment >= 0)
                {
                    line = line.Substring(0, comment);
                }
                line = line.Trim();
                if (line.Length == 0)
                {
                    continue;
                }
                var m = LineRegex.Match(line);
                var ok = m.Success;
                ushort[] ids = null;
                var quantity = 1;
                if (ok)
                {
                    var parts = m.Groups[1].Value.Split('/').Select(part => part.Trim()).ToArray();
                    ids = new ushort[parts.Length];
                    for (var i = 0; i < parts.Length && ok; i++)
                    {
                        ok = ushort.TryParse(parts[i], out ids[i]) && ids[i] > 0;
                    }
                    if (ok && m.Groups[2].Success)
                    {
                        ok = int.TryParse(m.Groups[2].Value, out quantity) && quantity >= 1 && quantity <= 100;
                    }
                }
                if (ok)
                {
                    entries.Add(new Entry { Ids = ids, Quantity = quantity });
                }
                else
                {
                    invalid?.Add(raw.Trim());
                }
            }
            return entries;
        }

        /// <summary>Troca o bloco do mapa dentro de spawnLoadouts:maps (cria o que faltar).</summary>
        public static string Apply(string yaml, string map, string text, List<string> invalid)
        {
            var entries = Parse(text, invalid);
            var newline = yaml.Contains("\r\n") ? "\r\n" : "\n";
            var lines = yaml.Replace("\r\n", "\n").Split('\n').ToList();
            var block = new List<string> { "    " + Quote(map) + ":", "      # SKALIA: kit do launcher (Jogabilidade > Kit inicial)." };
            var fixedItems = entries.Where(e => e.Ids.Length == 1).ToList();
            var randomItems = entries.Where(e => e.Ids.Length > 1).ToList();
            if (fixedItems.Count == 0)
            {
                block.Add("      equipment: []");
            }
            else
            {
                block.Add("      equipment:");
                foreach (var entry in fixedItems)
                {
                    block.Add("        - id: " + entry.Ids[0]);
                    block.Add("          quantity: " + entry.Quantity);
                    block.Add("          quality: 100");
                }
            }
            if (randomItems.Count > 0)
            {
                block.Add("      randomEquipment:");
                foreach (var entry in randomItems)
                {
                    block.Add("        - choices:");
                    block.AddRange(entry.Ids.Select(id => "            - " + id));
                    block.Add("          quantity: " + entry.Quantity);
                    block.Add("          quality: 100");
                }
            }

            var section = FindKey(lines, 0, lines.Count, 0, "spawnLoadouts");
            if (section < 0)
            {
                while (lines.Count > 0 && lines[lines.Count - 1].Trim().Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }
                lines.Add("");
                lines.Add("spawnLoadouts:");
                lines.Add("  enabled: true");
                lines.Add("  maps:");
                lines.AddRange(block);
                lines.Add("");
                return string.Join(newline, lines);
            }
            var sectionEnd = BlockEnd(lines, section, 0);
            var maps = FindKey(lines, section + 1, sectionEnd, 2, "maps");
            if (maps < 0)
            {
                lines.InsertRange(TrimBack(lines, section + 1, sectionEnd), new[] { "  maps:" }.Concat(block));
                return string.Join(newline, lines);
            }
            var mapsEnd = BlockEnd(lines, maps, 2);
            var existing = FindKey(lines, maps + 1, mapsEnd, 4, map);
            if (existing < 0)
            {
                lines.InsertRange(TrimBack(lines, maps + 1, mapsEnd), block);
            }
            else
            {
                var end = TrimBack(lines, existing + 1, BlockEnd(lines, existing, 4));
                lines.RemoveRange(existing, end - existing);
                lines.InsertRange(existing, block);
            }
            return string.Join(newline, lines);
        }

        private static string Quote(string map) =>
            Regex.IsMatch(map, @"^[A-Za-z0-9_][A-Za-z0-9_ .\-]*$") ? map : "\"" + map.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        private static int FindKey(List<string> lines, int start, int end, int indent, string key)
        {
            var pad = new string(' ', indent);
            for (var i = start; i < end; i++)
            {
                var line = lines[i];
                if (!line.StartsWith(pad) || line.Length <= indent || line[indent] == ' ' || line[indent] == '#')
                {
                    continue;
                }
                var colon = line.IndexOf(':', indent);
                if (colon < 0)
                {
                    continue;
                }
                var name = line.Substring(indent, colon - indent).Trim().Trim('"', '\'');
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Primeira linha depois de <paramref name="keyLine"/> com conteudo e indentacao &lt;= a dela.</summary>
        private static int BlockEnd(List<string> lines, int keyLine, int indent)
        {
            for (var i = keyLine + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith("#"))
                {
                    continue;
                }
                if (lines[i].Length - trimmed.Length <= indent)
                {
                    return i;
                }
            }
            return lines.Count;
        }

        /// <summary>Recua sobre linhas em branco e comentarios do bloco seguinte.</summary>
        private static int TrimBack(List<string> lines, int start, int end)
        {
            while (end > start)
            {
                var trimmed = lines[end - 1].Trim();
                if (trimmed.Length == 0 || (trimmed.StartsWith("#") && lines[end - 1].Length - lines[end - 1].TrimStart().Length < 6))
                {
                    end--;
                    continue;
                }
                break;
            }
            return end;
        }
    }
}
