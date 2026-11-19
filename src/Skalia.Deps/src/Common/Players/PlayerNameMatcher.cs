using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenMod.Unturned.Users;

namespace SkaliaDeps
{
    internal static class PlayerNameMatcher
    {
        public static PlayerNameMatch Find(string search, ICollection<UnturnedUser> onlineUsers)
        {
            var trimmedSearch = search.Trim();
            if (trimmedSearch.Length == 0)
            {
                return PlayerNameMatch.NotFound();
            }

            // A colecao do OpenMod pode conter temporariamente uma entrada nula ou
            // um usuario sem Player completo enquanto ele conecta/desconecta.
            var players = onlineUsers
                .Select(TryCreateCandidate)
                .Where(candidate => candidate != null)
                .Select(candidate => candidate!)
                .ToList();

            if (ulong.TryParse(trimmedSearch, out var steamId))
            {
                var bySteamId = players
                    .Where(candidate => candidate.User.SteamId.m_SteamID == steamId)
                    .ToList();
                if (bySteamId.Count == 1)
                {
                    return PlayerNameMatch.Found(bySteamId[0].User);
                }
            }

            var literalMatches = players
                .Where(candidate => string.Equals(candidate.Name, trimmedSearch,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (literalMatches.Count > 0)
            {
                return ResultFrom(literalMatches);
            }

            var normalizedSearch = Normalize(trimmedSearch);
            if (normalizedSearch.Length == 0)
            {
                return PlayerNameMatch.NotFound();
            }

            var normalizedMatches = players
                .Where(candidate => candidate.NormalizedName == normalizedSearch)
                .ToList();
            if (normalizedMatches.Count > 0)
            {
                return ResultFrom(normalizedMatches);
            }

            var prefixMatches = players
                .Where(candidate => candidate.NormalizedName.StartsWith(
                    normalizedSearch, StringComparison.Ordinal))
                .ToList();
            if (prefixMatches.Count > 0)
            {
                return ResultFrom(prefixMatches);
            }

            var containsMatches = players
                .Where(candidate => candidate.NormalizedName.Contains(normalizedSearch))
                .ToList();
            if (containsMatches.Count > 0)
            {
                return ResultFrom(containsMatches);
            }

            if (normalizedSearch.Length < 3)
            {
                return PlayerNameMatch.NotFound();
            }

            var maximumDistance = normalizedSearch.Length <= 4
                ? 1
                : normalizedSearch.Length <= 8 ? 2 : 3;
            var fuzzyMatches = players
                .Where(candidate => Math.Abs(candidate.NormalizedName.Length - normalizedSearch.Length) <= maximumDistance)
                .Where(candidate => LevenshteinDistance(candidate.NormalizedName, normalizedSearch) <= maximumDistance)
                .ToList();

            return fuzzyMatches.Count > 0
                ? ResultFrom(fuzzyMatches)
                : PlayerNameMatch.NotFound();
        }

        public static string FormatConflicts(IReadOnlyCollection<UnturnedUser> users)
        {
            var duplicateNames = users
                .GroupBy(GetName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return string.Join(", ", users.Select(user =>
            {
                var name = GetName(user);
                return duplicateNames.Contains(name)
                    ? $"{name} [{user.SteamId.m_SteamID}]"
                    : name;
            }));
        }

        private static PlayerNameMatch ResultFrom(IReadOnlyList<Candidate> candidates)
        {
            if (candidates.Count == 1)
            {
                return PlayerNameMatch.Found(candidates[0].User);
            }

            return PlayerNameMatch.Ambiguous(candidates.Select(candidate => candidate.User).ToList());
        }

        private static Candidate? TryCreateCandidate(UnturnedUser? user)
        {
            if (user == null)
            {
                return null;
            }

            var characterName = user.Player?.SteamPlayer?.playerID?.characterName;
            if (string.IsNullOrWhiteSpace(characterName))
            {
                return null;
            }

            var name = ChatNamePrefixService.StripPrefix(characterName);
            return string.IsNullOrWhiteSpace(name)
                ? null
                : new Candidate(user, name);
        }

        private static string Normalize(string value)
        {
            var decomposed = value.Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(character);
                if (category == UnicodeCategory.NonSpacingMark ||
                    category == UnicodeCategory.SpacingCombiningMark ||
                    category == UnicodeCategory.EnclosingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    result.Append(char.ToLowerInvariant(character));
                }
            }

            return result.ToString().Normalize(NormalizationForm.FormC);
        }

        private static int LevenshteinDistance(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (var column = 0; column <= right.Length; column++)
            {
                previous[column] = column;
            }

            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + substitutionCost);
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[right.Length];
        }

        private static string GetName(UnturnedUser user)
        {
            var characterName = user.Player?.SteamPlayer?.playerID?.characterName;
            return string.IsNullOrWhiteSpace(characterName)
                ? user.SteamId.m_SteamID.ToString()
                : ChatNamePrefixService.StripPrefix(characterName);
        }

        private sealed class Candidate
        {
            public Candidate(UnturnedUser user, string name)
            {
                User = user;
                Name = name;
                NormalizedName = Normalize(name);
            }

            public UnturnedUser User { get; }

            public string Name { get; }

            public string NormalizedName { get; }
        }
    }

    internal sealed class PlayerNameMatch
    {
        private PlayerNameMatch(UnturnedUser? user, IReadOnlyCollection<UnturnedUser> conflicts)
        {
            User = user;
            Conflicts = conflicts;
        }

        public UnturnedUser? User { get; }

        public IReadOnlyCollection<UnturnedUser> Conflicts { get; }

        public bool IsAmbiguous => Conflicts.Count > 0;

        public static PlayerNameMatch Found(UnturnedUser user)
        {
            return new PlayerNameMatch(user, Array.Empty<UnturnedUser>());
        }

        public static PlayerNameMatch Ambiguous(IReadOnlyCollection<UnturnedUser> users)
        {
            return new PlayerNameMatch(null, users);
        }

        public static PlayerNameMatch NotFound()
        {
            return new PlayerNameMatch(null, Array.Empty<UnturnedUser>());
        }
    }
}
