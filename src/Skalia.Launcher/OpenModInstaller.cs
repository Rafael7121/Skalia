using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Skalia.Launcher
{
    /// <summary>
    /// Instala o modulo OpenMod.Unturned no U3DS quando ele ainda nao existe.
    /// Baixa o zip do modulo da ultima release do OpenMod no GitHub; nada e instalado
    /// se o modulo ja estiver la.
    /// </summary>
    internal static class OpenModInstaller
    {
        public const string ModuleName = "OpenMod.Unturned";
        public const string ManualUrl = "https://openmod.github.io/openmod-docs/userdoc/installation/unturned.html";
        private const string ReleasesApi = "https://api.github.com/repos/openmod/openmod/releases?per_page=30";

        public static string ModuleDir(GamePaths paths) => Path.Combine(paths.U3DSDir, "Modules", ModuleName);

        public static bool IsInstalled(GamePaths paths) =>
            File.Exists(Path.Combine(ModuleDir(paths), ModuleName + ".module"));

        /// <summary>Baixa e instala o modulo. Lanca excecao se nao conseguir.</summary>
        public static void Install(GamePaths paths, Action<string> log)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SKALIA-Launcher");

                var releases = http.GetStringAsync(ReleasesApi).GetAwaiter().GetResult();
                // A lista vem da mais nova para a mais antiga; nem toda release do
                // repositorio traz o modulo do Unturned, entao pega a primeira que traz.
                var match = Regex.Matches(releases, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"")
                    .Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .FirstOrDefault(url =>
                    {
                        var file = url.Substring(url.LastIndexOf('/') + 1);
                        return file.StartsWith("OpenMod.Unturned.Module", StringComparison.OrdinalIgnoreCase)
                            && file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
                    });
                if (match == null)
                {
                    throw new InvalidOperationException(L.Tr("nenhuma release do OpenMod traz o módulo do Unturned."));
                }

                log(L.Tr("Baixando o OpenMod: ") + match.Substring(match.LastIndexOf('/') + 1));
                var zipPath = Path.Combine(Path.GetTempPath(), "skalia-openmod-" + Guid.NewGuid().ToString("N") + ".zip");
                try
                {
                    using (var response = http.GetAsync(match).GetAwaiter().GetResult())
                    {
                        response.EnsureSuccessStatusCode();
                        using (var file = File.Create(zipPath))
                        {
                            response.Content.CopyToAsync(file).GetAwaiter().GetResult();
                        }
                    }
                    ExtractModule(zipPath, ModuleDir(paths));
                }
                finally
                {
                    try { File.Delete(zipPath); } catch { }
                }
            }

            if (!IsInstalled(paths))
            {
                throw new InvalidOperationException(L.Tr("o zip baixado não tinha o arquivo OpenMod.Unturned.module."));
            }
            log(L.Tr("OpenMod instalado em ") + ModuleDir(paths) + ".");
        }

        /// <summary>
        /// Extrai a pasta que contem OpenMod.Unturned.module para Modules\OpenMod.Unturned,
        /// esteja ela na raiz do zip ou dentro de uma subpasta.
        /// </summary>
        private static void ExtractModule(string zipPath, string targetDir)
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var moduleEntry = zip.Entries.FirstOrDefault(entry =>
                    string.Equals(entry.Name, ModuleName + ".module", StringComparison.OrdinalIgnoreCase));
                if (moduleEntry == null)
                {
                    throw new InvalidOperationException(L.Tr("o zip baixado não tinha o arquivo OpenMod.Unturned.module."));
                }
                var prefix = moduleEntry.FullName.Substring(0, moduleEntry.FullName.Length - moduleEntry.Name.Length);

                var staging = targetDir + ".skalia-tmp";
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, true);
                }
                Directory.CreateDirectory(staging);
                var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.Length == prefix.Length)
                    {
                        continue;
                    }
                    var relative = entry.FullName.Substring(prefix.Length).Replace('/', Path.DirectorySeparatorChar);
                    var destination = Path.GetFullPath(Path.Combine(staging, relative));
                    if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue; // caminho fora da pasta (zip malformado)
                    }
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    entry.ExtractToFile(destination, true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetDir));
                if (Directory.Exists(targetDir))
                {
                    // So chega aqui se a pasta existe sem o .module (instalacao quebrada).
                    Directory.Delete(targetDir, true);
                }
                Directory.Move(staging, targetDir);
            }
        }
    }
}
