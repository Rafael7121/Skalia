using System;
using System.Collections.Generic;
using System.Linq;

namespace Skalia.Launcher
{
    /// <summary>Gera apelidos com cara de jogador para os bots sem nome.</summary>
    public static class BotNameGenerator
    {
        private static readonly string[] Bases =
        {
            "Lobo", "Corvo", "Zeca", "Tigrao", "Faisca", "Kaze", "Ryu", "Vulto", "Sombra", "Fumaca",
            "Raio", "Bala", "Neblina", "Trovao", "Pirata", "Viking", "Coyote", "Falcao", "Jaguar", "Caveira",
            "Ghost", "Reaper", "Toxic", "Frost", "Blaze", "Rex", "Duke", "Nomad", "Ranger", "Hunter",
            "Kiko", "Dudu", "Biel", "Caio", "Leleo", "Juninho", "Pedrin", "Gui", "Rafinha", "Tuts",
            "Zumbizeiro", "Saqueador", "Matheus", "Vini", "Breno", "Lucao", "Nando", "Teteu", "Japa", "Alemao",
        };

        private static readonly string[] Suffixes =
        {
            "", "", "", "BR", "_BR", "007", "zin", "_gg", "TTV", "x", "99", "22", "_PvP", "Sniper",
            "_Real", "Gamer", "OP", "_YT", "13", "69", "777", "Jr", "_Pro", "Kill",
        };

        private static readonly string[] Prefixes = { "", "", "", "", "", "o", "Xx", "Mr", "Dark", "Lil", "The" };

        public static List<string> Generate(int count, IEnumerable<string> taken, int seed)
        {
            var random = new Random(seed);
            var used = new HashSet<string>(taken ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            var guard = 0;
            while (names.Count < count && guard++ < count * 200)
            {
                var prefix = Prefixes[random.Next(Prefixes.Length)];
                var name = Bases[random.Next(Bases.Length)];
                var suffix = Suffixes[random.Next(Suffixes.Length)];
                var candidate = prefix == "Xx"
                    ? "Xx" + name + "xX"
                    : (prefix.Length > 0 ? prefix + (random.Next(2) == 0 ? "_" : "") : "") + name + suffix;
                if (random.Next(5) == 0)
                {
                    candidate = candidate.ToLowerInvariant();
                }
                if (candidate.Length > 24 || !used.Add(candidate))
                {
                    continue;
                }
                names.Add(candidate);
            }
            return names;
        }
    }
}
