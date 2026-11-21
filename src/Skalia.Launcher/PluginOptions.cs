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
    }

    internal static class PluginOptions
    {
        public const string Commands = "Comandos dos jogadores";
        public const string World = "Mundo";
        public const string Bots = "Bots";
        public const string BotBehavior = "Comportamento dos bots";

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

            Toggle(World, "spawnLoadouts:enabled", "Kit inicial", "Roupas e itens ao nascer, nos mapas que têm kit.", true),
            Toggle(World, "automaticReload:enabled", "Recarga automática", "Põe outro pente compatível quando a arma esvazia.", true),
            Toggle(World, "zombieSpawns:guaranteeMegas", "Mega Zombies garantidos", "Sempre um Mega por região; volta 10 min depois de morrer.", false),
            Toggle(World, "alicepackRewards:enabled", "Mochila dos Mega Zombies", "Todo Mega Zombie derruba Alicepacks ao morrer.", true),
            Toggle(World, "groundCleanup:enabled", "Limpar itens no chão", "Some com o que os jogadores largaram; o loot do mapa fica.", true),
            Pick(World, new[] { "groundCleanup:intervalSeconds" }, "Limpeza a cada", null, "600",
                new[] { "groundCleanup:enabled" }, C("300", "5 min"), C("600", "10 min"), C("1200", "20 min"), C("1800", "30 min")),

            Pick(Bots, new[] { "serverBot:respawnDelaySeconds" }, "Bots renascem em", null, "3",
                null, C("3", "3 s"), C("10", "10 s"), C("30", "30 s"), C("60", "60 s")),
            Toggle(Bots, "serverBot:movement:enabled", "Bots andam pelo mapa", "Desligado, os bots ficam parados.", true),

            Toggle(BotBehavior, "serverBot:behavior:meleeAimVariation", "Golpes variam a mira", "Desligado, todo golpe vai na cabeça.", true),
            Toggle(BotBehavior, "serverBot:behavior:hordeCaution", "Cautela na horda", "Ferido no meio da horda, agacha e luta só com o que está perto.", true),
            Toggle(BotBehavior, "serverBot:behavior:lootLook", "Olhar antes de pegar", "Para meio segundo no item antes de pegar.", true),
            Toggle(BotBehavior, "serverBot:behavior:humanTurning", "Giro natural", "Gira mais rápido nas viradas grandes, como um jogador.", true),
            Toggle(BotBehavior, "serverBot:behavior:routeHops", "Atalhos ao ar livre", "Corta em linha reta quando a rota faz zigue-zague.", true),
            Toggle(BotBehavior, "serverBot:behavior:freshDeathAvoidance", "Evitar onde um bot morreu", "Não pega loot perto de onde outro bot morreu há pouco.", true),
        };

        public static PluginOption Find(string id) => All.FirstOrDefault(o => o.Id == id);

        /// <summary>Valor salvo, ou o padrao quando nao ha valor valido.</summary>
        public static string Value(IDictionary<string, string> saved, PluginOption option)
        {
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
}
