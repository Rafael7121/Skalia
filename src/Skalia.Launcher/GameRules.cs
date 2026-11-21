using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Skalia.Launcher
{
    /// <summary>Uma escolha de regra. Sem valores = padrao da dificuldade.</summary>
    public sealed class RuleOption
    {
        public RuleOption(string id, string label, params string[] pairs)
        {
            Id = id;
            Label = label;
            for (var i = 0; i + 1 < pairs.Length; i += 2)
            {
                Values[pairs[i]] = pairs[i + 1];
            }
        }

        public string Id { get; }
        public string Label { get; }

        /// <summary>Multiplicador sobre o valor da dificuldade (regras escaladas).</summary>
        public double Multiplier { get; private set; } = 1;

        public static RuleOption Times(double multiplier)
        {
            var label = multiplier.ToString("0.##", CultureInfo.InvariantCulture) + "x";
            return new RuleOption(label, label) { Multiplier = multiplier };
        }

        /// <summary>"Secao/Chave" do Config.txt, ou "Cmd/Comando" do Commands.dat.</summary>
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>();

        public override string ToString() => L.Tr(Label);
    }

    /// <summary>Chave escalada: valor padrao por dificuldade (Easy, Normal, Hard).</summary>
    public sealed class ScaledKey
    {
        public ScaledKey(string key, double easy, double normal, double hard, double max = double.MaxValue, bool inverse = false, bool integer = false, int rounding = 0)
        {
            Rounding = rounding;
            Key = key;
            Easy = easy;
            Normal = normal;
            Hard = hard;
            Max = max;
            Inverse = inverse;
            Integer = integer;
        }

        public string Key { get; }
        public double Easy { get; }
        public double Normal { get; }
        public double Hard { get; }
        public double Max { get; }

        /// <summary>Valor e um intervalo (ticks): multiplicar a velocidade divide o valor.</summary>
        public bool Inverse { get; }
        public bool Integer { get; }

        /// <summary>So para Integer: 0 arredonda, -1 para baixo, +1 para cima. Um par Min (-1) / Max (+1)
        /// sorteia entre os dois e da em media o multiplicador (1,5x de 1 item = 1 ou 2).</summary>
        public int Rounding { get; }

        public string Format(string difficulty, double multiplier)
        {
            var baseValue = difficulty == "Easy" ? Easy : difficulty == "Hard" ? Hard : Normal;
            var value = Inverse ? baseValue / multiplier : baseValue * multiplier;
            value = Math.Min(value, Max);
            return Integer
                ? Math.Max(1, (int)(Rounding < 0 ? Math.Floor(value + 1e-9) : Rounding > 0 ? Math.Ceiling(value - 1e-9) : Math.Round(value))).ToString(CultureInfo.InvariantCulture)
                : Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    public sealed class GameRule
    {
        public GameRule(string id, string group, string title, string hint, params RuleOption[] options)
        {
            Id = id;
            Group = group;
            Title = title;
            Hint = hint;
            Options = new[] { new RuleOption("default", "Padrão da dificuldade") }.Concat(options).ToArray();
        }

        /// <summary>Regra em multiplicadores sobre o padrao da dificuldade; 1x = padrao.</summary>
        public GameRule(string id, string group, string title, string hint, double[] multipliers, params ScaledKey[] keys)
        {
            Id = id;
            Group = group;
            Title = title;
            Hint = hint;
            Scaled = keys;
            Options = multipliers.Select(m => m == 1 ? new RuleOption("default", "1x") : RuleOption.Times(m)).ToArray();
        }

        public ScaledKey[] Scaled { get; }

        public string Id { get; }
        public string Group { get; }
        public string Title { get; }
        public string Hint { get; }
        public RuleOption[] Options { get; }

        public RuleOption Find(string optionId) =>
            Options.FirstOrDefault(option => option.Id == optionId)
            ?? Options.FirstOrDefault(option => option.Id == "default")
            ?? Options[0];
    }

    public static class GameRules
    {
        public const string Basic = "basic";
        public const string World = "Mundo";
        public const string Survival = "Sobrevivência";
        public const string Combat = "Interface e combate";

        public static readonly GameRule[] All =
        {
            new GameRule("loot", Basic, "Quantidade de loot", null,
                new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5, 10, 25, 50, 100 },
                new ScaledKey("Items/Spawn_Chance", 0.35, 0.35, 0.15, max: 1),
                // Drop dos zumbis segue o mesmo multiplicador (10x = 10 itens por zumbi, 50 por Mega, 80-100 por chefe;
                // 100x = 100 por zumbi). Os campos de drop do jogo sao uint, sem teto.
                new ScaledKey("Zombies/Loot_Chance", 0.55, 0.5, 0.3, max: 1),
                new ScaledKey("Zombies/Min_Drops", 1, 1, 1, integer: true, rounding: -1),
                new ScaledKey("Zombies/Max_Drops", 1, 1, 1, integer: true, rounding: 1),
                new ScaledKey("Zombies/Min_Mega_Drops", 5, 5, 5, integer: true, rounding: -1),
                new ScaledKey("Zombies/Max_Mega_Drops", 5, 5, 5, integer: true, rounding: 1),
                new ScaledKey("Zombies/Min_Boss_Drops", 8, 8, 8, integer: true, rounding: -1),
                new ScaledKey("Zombies/Max_Boss_Drops", 10, 10, 10, integer: true, rounding: 1)),
            new GameRule("zombies", Basic, "Quantidade de zumbis", null,
                new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4 },
                new ScaledKey("Zombies/Spawn_Chance", 0.2, 0.25, 0.3, max: 1)),

            new GameRule("zombieDamage", World, "Dano dos zumbis", null,
                new[] { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 5, 10 },
                new ScaledKey("Zombies/Damage_Multiplier", 0.75, 1, 1.5)),
            new GameRule("zombieSpeed", World, "Zumbis mais lentos", null,
                new RuleOption("yes", "Sim", "Zombies/Slow_Movement", "True"),
                new RuleOption("no", "Não", "Zombies/Slow_Movement", "False")),
            new GameRule("vehicles", World, "Veículos encontrados", null,
                new RuleOption("ready", "Sempre com bateria e pneus",
                    "Vehicles/Has_Battery_Chance", "1", "Vehicles/Min_Battery_Charge", "0.8",
                    "Vehicles/Max_Battery_Charge", "1", "Vehicles/Has_Tire_Chance", "1")),
            new GameRule("airdrops", World, "Airdrops", null,
                new RuleOption("on", "Ligados", "Events/Use_Airdrops", "True"),
                new RuleOption("off", "Desligados", "Events/Use_Airdrops", "False")),
            new GameRule("dayLength", World, "Duração do dia", "Um dia completo (dia e noite).",
                new RuleOption("30", "30 minutos", "Cmd/Cycle", "1800"),
                new RuleOption("120", "2 horas", "Cmd/Cycle", "7200"),
                new RuleOption("240", "4 horas", "Cmd/Cycle", "14400")),

            new GameRule("experience", Survival, "Ganho de experiência", null,
                new[] { 0.5, 0.75, 1, 2, 3, 5, 10 },
                new ScaledKey("Players/Experience_Multiplier", 1.5, 1, 1.5)),
            new GameRule("hunger", Survival, "Velocidade da fome e sede", "Acima de 1x a fome e a sede chegam mais rápido.",
                new[] { 0.25, 0.5, 0.75, 1, 1.5, 2 },
                new ScaledKey("Players/Food_Use_Ticks", 350, 300, 250, inverse: true, integer: true),
                new ScaledKey("Players/Water_Use_Ticks", 320, 270, 220, inverse: true, integer: true)),
            new GameRule("deathItems", Survival, "Ao morrer", null,
                new RuleOption("keepClothes", "Mantém as roupas",
                    "Players/Lose_Clothes_PvE", "False", "Players/Lose_Clothes_PvP", "False"),
                new RuleOption("keepAll", "Mantém tudo",
                    "Players/Lose_Items_PvE", "0", "Players/Lose_Items_PvP", "0",
                    "Players/Lose_Clothes_PvE", "False", "Players/Lose_Clothes_PvP", "False",
                    "Players/Lose_Weapons_PvE", "False", "Players/Lose_Weapons_PvP", "False")),
            new GameRule("deathSkills", Survival, "Habilidades ao morrer", null,
                new RuleOption("keep", "Mantém níveis e experiência",
                    "Players/Lose_Skill_Levels_PvE", "0", "Players/Lose_Skill_Levels_PvP", "0",
                    "Players/Lose_Experience_PvE", "1", "Players/Lose_Experience_PvP", "1")),
            new GameRule("startSkills", Survival, "Habilidades iniciais", null,
                new RuleOption("stamina", "Fôlego e corrida no máximo", "Players/Spawn_With_Stamina_Skills", "True"),
                new RuleOption("all", "Todas no máximo", "Players/Spawn_With_Max_Skills", "True")),
            new GameRule("injuries", Survival, "Sangramento e pernas quebradas", null,
                new RuleOption("on", "Ligados", "Players/Can_Start_Bleeding", "True", "Players/Can_Break_Legs", "True"),
                new RuleOption("off", "Desligados", "Players/Can_Start_Bleeding", "False", "Players/Can_Break_Legs", "False")),

            new GameRule("maps", Combat, "Mapa e GPS", "Sem precisar achar o item no jogo.",
                new RuleOption("chart", "Mapa sempre disponível", "Gameplay/Chart", "True"),
                new RuleOption("all", "Mapa, GPS e bússola sempre", "Gameplay/Chart", "True", "Gameplay/Satellite", "True", "Gameplay/Compass", "True")),
            new GameRule("crosshair", Combat, "Mira e marcador de acerto", null,
                new RuleOption("on", "Ligados", "Gameplay/Crosshair", "True", "Gameplay/Hitmarkers", "True"),
                new RuleOption("off", "Desligados", "Gameplay/Crosshair", "False", "Gameplay/Hitmarkers", "False")),
            new GameRule("ballistics", Combat, "Queda de bala", null,
                new RuleOption("on", "Realista", "Gameplay/Ballistics", "True"),
                new RuleOption("off", "Desligada", "Gameplay/Ballistics", "False")),
            new GameRule("friendlyFire", Combat, "Fogo amigo no grupo", null,
                new RuleOption("on", "Ligado", "Gameplay/Friendly_Fire", "True"),
                new RuleOption("off", "Desligado", "Gameplay/Friendly_Fire", "False")),
            new GameRule("timers", Combat, "Sair e renascer", null,
                new RuleOption("fast", "Rápido", "Gameplay/Timer_Exit", "0", "Gameplay/Timer_Respawn", "3", "Gameplay/Timer_Home", "10")),
        };

        /// <summary>
        /// Itens por ponto de loot sem o teto do jogo (Spawn_Chance para em 1): o SKALIA DEPS
        /// poe o que passar disso (lootAmount:spawnFactor). Ex.: 10x no Normal = 3,5.
        /// </summary>
        public static double LootSpawnFactor(IDictionary<string, string> choices, string difficulty)
        {
            var rule = All.First(r => r.Id == "loot");
            choices.TryGetValue(rule.Id, out var chosen);
            var option = rule.Find(chosen);
            var key = rule.Scaled[0];
            var baseValue = difficulty == "Easy" ? key.Easy : difficulty == "Hard" ? key.Hard : key.Normal;
            // Loot do chao fica no limite normal do mapa (todos os pontos de loot, 1 item cada):
            // nada de itens extras do SKALIA DEPS. O multiplicador acima disso vai so para o drop dos zumbis.
            return Math.Round(Math.Min(baseValue * option.Multiplier, key.Max), 3);
        }

        /// <summary>Valores escolhidos (Secao/Chave -> valor) e todas as chaves controladas pelo launcher.</summary>
        public static Dictionary<string, string> Resolve(IDictionary<string, string> choices, string difficulty, out HashSet<string> managedKeys)
        {
            managedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in All)
            {
                choices.TryGetValue(rule.Id, out var chosen);
                var option = rule.Find(chosen);
                if (rule.Scaled != null)
                {
                    foreach (var scaled in rule.Scaled)
                    {
                        managedKeys.Add(scaled.Key);
                        if (option.Id != "default")
                        {
                            values[scaled.Key] = scaled.Format(difficulty, option.Multiplier);
                        }
                    }
                    continue;
                }
                foreach (var other in rule.Options)
                {
                    foreach (var key in other.Values.Keys)
                    {
                        managedKeys.Add(key);
                    }
                }
                foreach (var pair in option.Values)
                {
                    values[pair.Key] = pair.Value;
                }
            }
            return values;
        }

        /// <summary>
        /// Atualiza o Config.txt do perfil mantendo o resto do arquivo. Chaves sem valor
        /// voltam ao padrao da dificuldade. Os numeros usam ponto (o Unturned le invariante).
        /// </summary>
        public static void ApplyToConfig(string path, Dictionary<string, string> values, HashSet<string> managedKeys)
        {
            var configKeys = managedKeys.Where(key => !key.StartsWith("Cmd/", StringComparison.OrdinalIgnoreCase)).ToList();
            var lines = File.Exists(path)
                ? File.ReadAllLines(path, Encoding.UTF8).ToList()
                : new List<string> { "Version 1" };
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string section = null;
            for (var i = 0; i < lines.Count; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("//"))
                {
                    continue;
                }
                if (!raw.StartsWith("\t") && !raw.StartsWith(" ") && trimmed != "{" && trimmed != "}")
                {
                    section = trimmed.Split(' ')[0];
                    continue;
                }
                if (trimmed == "}" && !raw.StartsWith("\t"))
                {
                    // Fim da secao: insere as chaves que faltaram.
                    var missing = configKeys.Where(key => SectionOf(key) == section && !done.Contains(key) && values.ContainsKey(key)).ToList();
                    foreach (var key in missing)
                    {
                        lines.Insert(i, "\t" + NameOf(key) + " " + values[key]);
                        done.Add(key);
                        i++;
                    }
                    section = null;
                    continue;
                }
                if (section == null || !raw.StartsWith("\t") || raw.StartsWith("\t\t"))
                {
                    continue;
                }
                var name = trimmed.Split(' ')[0];
                var fullKey = section + "/" + name;
                if (!configKeys.Contains(fullKey, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                lines[i] = values.TryGetValue(fullKey, out var value) ? "\t" + name + " " + value : "\t" + name;
                done.Add(fullKey);
            }

            foreach (var group in configKeys.Where(key => !done.Contains(key) && values.ContainsKey(key)).GroupBy(SectionOf))
            {
                lines.Add("");
                lines.Add(group.Key);
                lines.Add("{");
                foreach (var key in group)
                {
                    lines.Add("\t" + NameOf(key) + " " + values[key]);
                }
                lines.Add("}");
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        private static string SectionOf(string key) => key.Substring(0, key.IndexOf('/'));

        private static string NameOf(string key) => key.Substring(key.IndexOf('/') + 1);
    }
}
