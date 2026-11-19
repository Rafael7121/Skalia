using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Skalia.Launcher
{
    /// <summary>
    /// Localiza a instalacao do Unturned/U3DS do proprio usuario. O SKALIA nunca
    /// copia nem distribui arquivos do jogo: ele so usa o que ja esta instalado.
    /// </summary>
    public sealed class GamePaths
    {
        public const string ProfileId = "Skalia";
        public const uint UnturnedAppId = 304930;

        public string SkaliaDir { get; private set; }
        public string U3DSDir { get; private set; }
        public string SteamDir { get; private set; }

        public string ServerExe => Path.Combine(U3DSDir, "Unturned.exe");
        public string ProfileDir => Path.Combine(U3DSDir, "Servers", ProfileId);
        public string DefaultProfileDir => Path.Combine(U3DSDir, "Servers", "Default");
        public string PluginsSourceDir => Path.Combine(SkaliaDir, "plugins");
        public string ProfilePluginsDir => Path.Combine(ProfileDir, "OpenMod", "plugins");
        public string SettingsFile => Path.Combine(SkaliaDir, "skalia.json");
        public string ServerLogFile => Path.Combine(U3DSDir, "Logs", "Server_" + ProfileId + ".log");
        public string ReadyFile => Path.Combine(ProfileDir, "skalia.ready");
        public string StopFile => Path.Combine(ProfileDir, "skalia.stop");
        public string PidFile => Path.Combine(SkaliaDir, "server.pid");
        public string SteamExe => SteamDir == null ? null : Path.Combine(SteamDir, "steam.exe");

        public static GamePaths Detect()
        {
            var paths = new GamePaths();
            paths.SkaliaDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            paths.SteamDir = FindSteamDir();

            var candidates = new List<string>();
            var parent = Directory.GetParent(paths.SkaliaDir)?.FullName;
            if (parent != null)
            {
                candidates.Add(parent);
            }
            foreach (var library in SteamLibraries(paths.SteamDir))
            {
                candidates.Add(Path.Combine(library, "steamapps", "common", "U3DS"));
            }
            paths.U3DSDir = candidates.FirstOrDefault(IsU3DS);

            if (paths.SteamDir == null && paths.U3DSDir != null)
            {
                // <Steam>\steamapps\common\U3DS
                var guess = Path.GetFullPath(Path.Combine(paths.U3DSDir, "..", "..", ".."));
                if (File.Exists(Path.Combine(guess, "steam.exe")))
                {
                    paths.SteamDir = guess;
                }
            }
            return paths;
        }

        public static bool IsU3DS(string dir) =>
            !string.IsNullOrEmpty(dir)
            && File.Exists(Path.Combine(dir, "Unturned.exe"))
            && Directory.Exists(Path.Combine(dir, "Unturned_Data"))
            && Directory.Exists(Path.Combine(dir, "Maps"));

        private static string FindSteamDir()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var value = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(value) && Directory.Exists(value))
                    {
                        return Path.GetFullPath(value);
                    }
                }
            }
            catch
            {
            }
            foreach (var guess in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            {
                if (File.Exists(Path.Combine(guess, "steam.exe")))
                {
                    return guess;
                }
            }
            return null;
        }

        private static IEnumerable<string> SteamLibraries(string steamDir)
        {
            if (steamDir == null)
            {
                yield break;
            }
            yield return steamDir;
            var vdf = Path.Combine(steamDir, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
            {
                yield break;
            }
            string text;
            try
            {
                text = File.ReadAllText(vdf);
            }
            catch
            {
                yield break;
            }
            foreach (Match match in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
            {
                yield return match.Groups[1].Value.Replace("\\\\", "\\");
            }
        }

        /// <summary>SteamID64 da conta logada mais recentemente na Steam deste PC.</summary>
        public ulong FindCurrentSteamId()
        {
            try
            {
                var file = SteamDir == null ? null : Path.Combine(SteamDir, "config", "loginusers.vdf");
                if (file == null || !File.Exists(file))
                {
                    return 0;
                }
                var text = File.ReadAllText(file);
                ulong first = 0;
                foreach (Match match in Regex.Matches(text, "\"(7656\\d{13})\"\\s*\\{([^}]*)\\}"))
                {
                    var id = ulong.Parse(match.Groups[1].Value);
                    if (first == 0)
                    {
                        first = id;
                    }
                    if (Regex.IsMatch(match.Groups[2].Value, "\"MostRecent\"\\s*\"1\""))
                    {
                        return id;
                    }
                }
                return first;
            }
            catch
            {
                return 0;
            }
        }
    }
}
