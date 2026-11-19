using System;
using Microsoft.Extensions.Configuration;

namespace SkaliaDeps
{
    internal static class ChatNamePrefixService
    {
        private static string s_NamePrefix = string.Empty;

        public static void Configure(IConfiguration configuration)
        {
            s_NamePrefix = configuration["chat:namePrefix"] ?? string.Empty;
        }

        public static void Clear()
        {
            s_NamePrefix = string.Empty;
        }

        public static string FormatName(string displayName)
        {
            if (string.IsNullOrEmpty(s_NamePrefix) ||
                displayName.StartsWith(s_NamePrefix, StringComparison.Ordinal))
            {
                return displayName;
            }

            return s_NamePrefix + displayName;
        }

        public static string StripPrefix(string displayName)
        {
            if (string.IsNullOrEmpty(s_NamePrefix) ||
                !displayName.StartsWith(s_NamePrefix, StringComparison.Ordinal))
            {
                return displayName;
            }

            return displayName.Substring(s_NamePrefix.Length);
        }
    }
}
