using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Skalia.Launcher
{
    public sealed class MapEntry
    {
        public string Name { get; set; }
        public ulong WorkshopId { get; set; }
        public override string ToString() => WorkshopId == 0 ? Name : Name + "  (Workshop " + WorkshopId + ")";
    }

    /// <summary>Monta o perfil Servers\Skalia e controla o servidor LAN local.</summary>
    public sealed class ServerManager
    {
        private static readonly string[] BrokenWorkshopDefinitions =
        {
            // Definicoes quebradas do California 2 Assets que causam kick por asset ausente.
            @"3711646503\Resources\Desert\CA_Cactus_1_Alt\CA_Cactus_1_Alt.dat",
            @"3711646503\Resources\Desert\CA_Cactus_1\CA_Cactus_1.dat",
            @"3711646503\Resources\Desert\CA_Joshua_0\CA_Joshua_0.dat",
        };

        private readonly GamePaths m_Paths;
        private readonly Action<string> m_Log;
        private Process m_Process;

        public ServerManager(GamePaths paths, Action<string> log)
        {
            m_Paths = paths;
            m_Log = log;
        }

        public bool IsRunning
        {
            get
            {
                var process = FindProcess();
                return process != null && !process.HasExited;
            }
        }

        public bool IsReady => IsRunning && File.Exists(m_Paths.ReadyFile);

        // ---------------------------------------------------------------- mapas

        public List<MapEntry> DiscoverMaps(IEnumerable<ulong> workshopIds)
        {
            var maps = new List<MapEntry>();
            var mapsDir = Path.Combine(m_Paths.U3DSDir, "Maps");
            if (Directory.Exists(mapsDir))
            {
                foreach (var dir in Directory.GetDirectories(mapsDir))
                {
                    var name = Path.GetFileName(dir);
                    if (!string.Equals(name, "Tutorial", StringComparison.OrdinalIgnoreCase))
                    {
                        maps.Add(new MapEntry { Name = name });
                    }
                }
            }

            // Mapas da Workshop ja baixados por qualquer perfil do U3DS.
            var serversDir = Path.Combine(m_Paths.U3DSDir, "Servers");
            if (Directory.Exists(serversDir))
            {
                foreach (var profile in Directory.GetDirectories(serversDir))
                {
                    var content = Path.Combine(profile, "Workshop", "Steam", "content", GamePaths.UnturnedAppId.ToString());
                    if (!Directory.Exists(content))
                    {
                        continue;
                    }
                    foreach (var item in Directory.GetDirectories(content))
                    {
                        if (!ulong.TryParse(Path.GetFileName(item), out var id) || !File.Exists(Path.Combine(item, "Map.meta")))
                        {
                            continue;
                        }
                        foreach (var dir in Directory.GetDirectories(item))
                        {
                            var name = Path.GetFileName(dir);
                            if (!maps.Any(map => string.Equals(map.Name, name, StringComparison.OrdinalIgnoreCase)))
                            {
                                maps.Add(new MapEntry { Name = name, WorkshopId = id });
                            }
                        }
                    }
                }
            }
            return maps.OrderBy(map => map.WorkshopId != 0).ThenBy(map => map.Name).ToList();
        }

        public List<ulong> ReadDefaultWorkshopIds()
        {
            var ids = new List<ulong>();
            try
            {
                var file = Path.Combine(m_Paths.DefaultProfileDir, "WorkshopDownloadConfig.json");
                if (File.Exists(file))
                {
                    var match = Regex.Match(File.ReadAllText(file), "\"File_IDs\"\\s*:\\s*\\[([^\\]]*)\\]");
                    foreach (Match number in Regex.Matches(match.Groups[1].Value, "\\d+"))
                    {
                        ids.Add(ulong.Parse(number.Value));
                    }
                }
            }
            catch
            {
            }
            return ids;
        }

        // -------------------------------------------------------------- plugins

        public List<string> AvailablePlugins()
        {
            if (!Directory.Exists(m_Paths.PluginsSourceDir))
            {
                return new List<string>();
            }
            return Directory.GetFiles(m_Paths.PluginsSourceDir, "*.dll")
                .Select(Path.GetFileName)
                .OrderBy(name => !string.Equals(name, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase))
                .ThenBy(name => name)
                .ToList();
        }

        // ---------------------------------------------------------------- perfil

        public void PrepareProfile(SkaliaSettings settings, ulong ownerSteamId)
        {
            Directory.CreateDirectory(Path.Combine(m_Paths.ProfileDir, "Server"));
            Directory.CreateDirectory(m_Paths.ProfilePluginsDir);

            var rules = GameRules.Resolve(settings.GameRules, settings.Difficulty, out var managedKeys);
            WriteCommands(settings, ownerSteamId, rules);
            GameRules.ApplyToConfig(Path.Combine(m_Paths.ProfileDir, "Config.txt"), rules, managedKeys);
            WriteWorkshopConfig(settings);
            SeedWorkshopFromDefaultProfile();
            EnsureOpenMod();
            CopyOpenModPackages();
            InstallPlugins(settings);
            WriteBots(settings);
            ConfigureDeps(settings);
            DisableBrokenWorkshopDefinitions();
            ApplyStartSkillsToSaves(settings);
            TryDelete(m_Paths.ReadyFile);
            TryDelete(m_Paths.StopFile);
        }

        private void WriteCommands(SkaliaSettings settings, ulong ownerSteamId, Dictionary<string, string> rules)
        {
            var lines = new List<string>
            {
                "Name " + Sanitize(settings.ServerName),
                "Map " + settings.Map,
                "MaxPlayers " + Math.Max(1, Math.Min(200, settings.MaxPlayers)).ToString(CultureInfo.InvariantCulture),
                "Port " + settings.Port.ToString(CultureInfo.InvariantCulture),
                "Perspective " + settings.Perspective,
                "Mode " + settings.Difficulty,
            };
            if (!settings.PvP)
            {
                lines.Add("PvE");
            }
            if (settings.Cheats)
            {
                lines.Add("Cheats");
            }
            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                lines.Add("Password " + Sanitize(settings.Password));
            }
            if (rules.TryGetValue("Cmd/Cycle", out var cycle))
            {
                lines.Add("Cycle " + cycle);
            }
            if (!string.IsNullOrWhiteSpace(settings.WelcomeMessage))
            {
                lines.Add("Welcome " + Sanitize(settings.WelcomeMessage));
            }
            if (ownerSteamId != 0)
            {
                lines.Add("Owner " + ownerSteamId.ToString(CultureInfo.InvariantCulture));
            }
            File.WriteAllLines(Path.Combine(m_Paths.ProfileDir, "Server", "Commands.dat"), lines, new UTF8Encoding(false));
            m_Log(L.Tr("Commands.dat gravado (mapa ") + settings.Map + L.Tr(", porta ") + settings.Port + ").");
        }

        private void WriteWorkshopConfig(SkaliaSettings settings)
        {
            var ids = string.Join(",\n    ", settings.WorkshopIds.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));
            var json = "{\n  \"File_IDs\": [" + (ids.Length > 0 ? "\n    " + ids + "\n  " : "") + "],\n"
                + "  \"Ignore_Children_File_IDs\": [],\n"
                + "  \"Query_Cache_Max_Age_Seconds\": 600,\n"
                + "  \"Max_Query_Retries\": 2,\n"
                + "  \"Use_Cached_Downloads\": true,\n"
                + "  \"Should_Monitor_Updates\": false\n"
                + "}\n";
            File.WriteAllText(Path.Combine(m_Paths.ProfileDir, "WorkshopDownloadConfig.json"), json, new UTF8Encoding(false));
        }

        private void SeedWorkshopFromDefaultProfile()
        {
            // Reaproveita os itens da Workshop ja baixados pelo perfil Default do U3DS
            // (so leitura). Evita baixar tudo de novo e downloads que travam na Steam.
            var source = Path.Combine(m_Paths.DefaultProfileDir, "Workshop", "Steam");
            var target = Path.Combine(m_Paths.ProfileDir, "Workshop", "Steam");
            var sourceContent = Path.Combine(source, "content", GamePaths.UnturnedAppId.ToString());
            var targetContent = Path.Combine(target, "content", GamePaths.UnturnedAppId.ToString());
            var manifestName = "appworkshop_" + GamePaths.UnturnedAppId + ".acf";
            var sourceManifest = Path.Combine(source, manifestName);
            var targetManifest = Path.Combine(target, manifestName);
            if (!Directory.Exists(sourceContent) || !File.Exists(sourceManifest))
            {
                return;
            }
            Directory.CreateDirectory(targetContent);

            // Itens ja completos no SKALIA: baixados pela propria Steam (estao no .acf
            // do perfil) ou copiados por inteiro antes (lista skalia-seeded.txt).
            var seededList = Path.Combine(target, "skalia-seeded.txt");
            var complete = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(seededList))
            {
                foreach (var line in File.ReadAllLines(seededList))
                {
                    complete.Add(line.Trim());
                }
            }
            var ownManifest = File.Exists(targetManifest) ? File.ReadAllText(targetManifest) : "";

            var copied = 0;
            foreach (var item in Directory.GetDirectories(sourceContent))
            {
                var id = Path.GetFileName(item);
                var destination = Path.Combine(targetContent, id);
                if (complete.Contains(id) || (Directory.Exists(destination) && ownManifest.Contains("\"" + id + "\"")))
                {
                    continue;
                }
                m_Log(L.Tr("Preparando item da Workshop ") + id + "...");
                CopyDirectory(item, destination, overwrite: true);
                File.AppendAllText(seededList, id + Environment.NewLine);
                copied++;
            }
            if (copied == 0 && File.Exists(targetManifest))
            {
                return;
            }
            File.Copy(sourceManifest, targetManifest, overwrite: true);
            foreach (var leftover in new[] { "downloads", "temp" })
            {
                try
                {
                    var dir = Path.Combine(target, leftover);
                    if (Directory.Exists(dir))
                    {
                        Directory.Delete(LongPath(dir), recursive: true);
                    }
                }
                catch
                {
                }
            }
            m_Log(copied + L.Tr(" item(ns) da Workshop reaproveitados de downloads anteriores."));
        }

        private void EnsureOpenMod()
        {
            if (OpenModInstaller.IsInstalled(m_Paths))
            {
                return;
            }
            m_Log(L.Tr("OpenMod não encontrado no Unturned Dedicated Server. Instalando (só na primeira vez)..."));
            try
            {
                OpenModInstaller.Install(m_Paths, m_Log);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    L.Tr("não foi possível instalar o OpenMod (") + ex.Message + L.Tr("). Instale manualmente: ") + OpenModInstaller.ManualUrl, ex);
            }
        }

        private void CopyOpenModPackages()
        {
            // O OpenMod baixa pacotes NuGet no primeiro uso; reaproveita os do perfil
            // Default, se existirem, para abrir mesmo sem internet. Somente leitura.
            var source = Path.Combine(m_Paths.DefaultProfileDir, "OpenMod", "packages");
            var target = Path.Combine(m_Paths.ProfileDir, "OpenMod", "packages");
            if (!Directory.Exists(source) || Directory.Exists(target))
            {
                return;
            }
            CopyDirectory(source, target);
            m_Log(L.Tr("Pacotes do OpenMod reaproveitados."));
        }

        private void InstallPlugins(SkaliaSettings settings)
        {
            foreach (var name in AvailablePlugins())
            {
                var source = Path.Combine(m_Paths.PluginsSourceDir, name);
                var target = Path.Combine(m_Paths.ProfilePluginsDir, name);
                var enabled = string.Equals(name, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase)
                    || !settings.DisabledPlugins.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (enabled)
                {
                    if (!File.Exists(target) || File.GetLastWriteTimeUtc(source) != File.GetLastWriteTimeUtc(target)
                        || new FileInfo(source).Length != new FileInfo(target).Length)
                    {
                        File.Copy(source, target, overwrite: true);
                        m_Log(L.Tr("Plugin instalado: ") + name);
                    }
                }
                else if (File.Exists(target))
                {
                    File.Delete(target);
                    m_Log(L.Tr("Plugin desativado: ") + name);
                }
            }
        }

        private void WriteBots(SkaliaSettings settings)
        {
            var dir = Path.Combine(m_Paths.ProfilePluginsDir, "Skalia.Deps");
            Directory.CreateDirectory(dir);
            var entries = new List<string>();
            for (var i = 0; i < settings.BotCount; i++)
            {
                var name = i < settings.BotNames.Count && !string.IsNullOrWhiteSpace(settings.BotNames[i])
                    ? settings.BotNames[i].Trim()
                    : BotNameGenerator.Generate(1, settings.BotNames, i + 1).FirstOrDefault() ?? "Sobrevivente";
                // SteamIDs individuais validos, fora da faixa de contas reais: o
                // servidor e local e os bots nao usam contas Steam.
                var steamId = 76561197960265728UL + 4000000000UL + (ulong)i;
                entries.Add("    { \"SteamId\": " + steamId.ToString(CultureInfo.InvariantCulture) + ", \"Name\": \"" + JsonEscape(name) + "\" }");
            }
            var json = "{\n  \"Bots\": [\n" + string.Join(",\n", entries) + "\n  ]\n}\n";
            File.WriteAllText(Path.Combine(dir, "bots.json"), json, new UTF8Encoding(false));
            m_Log(settings.BotCount + L.Tr(" bot(s) registrados."));
        }

        private void ConfigureDeps(SkaliaSettings settings)
        {
            var dir = Path.Combine(m_Paths.ProfilePluginsDir, "Skalia.Deps");
            var config = Path.Combine(dir, "config.yaml");
            var defaults = Path.Combine(m_Paths.PluginsSourceDir, "Skalia.Deps.config.yaml");
            if (!File.Exists(config) && File.Exists(defaults))
            {
                File.Copy(defaults, config);
            }
            if (!File.Exists(config))
            {
                return;
            }
            ApplyDepsLanguage(settings, dir);
            var text = File.ReadAllText(config);
            var value = settings.AutoCallBots ? "true" : "false";
            text = Regex.IsMatch(text, @"(?m)^  autoCallOnStart:")
                ? Regex.Replace(text, @"(?m)^(  autoCallOnStart:)\s*\S+", "$1 " + value)
                : Regex.Replace(text, @"(?m)^serverBot:\r?\n", "serverBot:\n  autoCallOnStart: " + value + "\n");
            text = SetYamlKey(text, "serverBot", "botLogs", settings.BotLogs ? "true" : "false",
                "# SKALIA: log detalhado dos bots (opcao Desenvolvedor do launcher). false = so avisos e erros.");
            text = PluginOptions.Apply(text, settings.PluginOptions);
            // Kit inicial do mapa escolhido (vazio = sem kit); a chave geral fica sempre ligada.
            text = PluginOptions.SetPath(text, "spawnLoadouts:enabled", "true");
            var kitInvalid = new List<string>();
            text = StartingKits.Apply(text, settings.Map, StartingKits.TextFor(settings.StartingKits, settings.Map), kitInvalid);
            foreach (var line in kitInvalid)
            {
                m_Log(L.Tr("Kit inicial: linha ignorada, não é um ID de item: ") + line);
            }
            // Dicas automaticas do chat eram de outro servidor (California 2, em portugues).
            text = PluginOptions.SetPath(text, "automaticMessages:enabled", "false");
            var factor = GameRules.LootSpawnFactor(settings.GameRules, settings.Difficulty)
                .ToString("0.###", CultureInfo.InvariantCulture);
            text = Regex.IsMatch(text, @"(?m)^  spawnFactor:")
                ? Regex.Replace(text, @"(?m)^(  spawnFactor:)\s*\S+", "$1 " + factor)
                : text.TrimEnd('\r', '\n') + "\n\nlootAmount:\n  # SKALIA: itens por ponto de loot (regra Quantidade de loot); acima de 1 o SKALIA DEPS poe os extras.\n  spawnFactor: " + factor + "\n";
            File.WriteAllText(config, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// Mensagens do plugin no idioma do launcher. Ingles: copia o translations.en.yaml que vem
        /// em plugins. Portugues: apaga o arquivo para o plugin extrair o dele (embutido, sempre atual).
        /// </summary>
        private void ApplyDepsLanguage(SkaliaSettings settings, string dir)
        {
            var target = Path.Combine(dir, "translations.yaml");
            var english = Path.Combine(m_Paths.PluginsSourceDir, "Skalia.Deps.translations.en.yaml");
            try
            {
                if (settings.Language != "pt-BR" && File.Exists(english))
                {
                    File.Copy(english, target, true);
                }
                else
                {
                    TryDelete(target);
                }
            }
            catch (Exception ex)
            {
                m_Log(L.Tr("Não foi possível trocar o idioma das mensagens do jogo: ") + ex.Message);
            }
        }

        // Maximo de cada habilidade (PlayerSkills.InitializePlayer), na ordem gravada em Skills.dat.
        private static readonly byte[][] SkillMax =
        {
            new byte[] { 7, 7, 5, 5, 5, 5, 5 },
            new byte[] { 7, 5, 5, 5, 5, 5, 5 },
            new byte[] { 7, 3, 5, 3, 5, 7, 5, 3 },
        };

        /// <summary>
        /// O Unturned so aplica Spawn_With_Stamina_Skills/Max_Skills em personagens sem save.
        /// Para a opcao valer tambem para quem ja jogou no mapa, sobe essas habilidades no Skills.dat
        /// (nunca abaixa nenhuma).
        /// </summary>
        private void ApplyStartSkillsToSaves(SkaliaSettings settings)
        {
            settings.GameRules.TryGetValue("startSkills", out var mode);
            if (mode != "stamina" && mode != "all")
            {
                return;
            }
            var playersDir = Path.Combine(m_Paths.ProfileDir, "Players");
            if (!Directory.Exists(playersDir))
            {
                return;
            }
            var updated = 0;
            foreach (var characterDir in Directory.GetDirectories(playersDir))
            {
                var file = Path.Combine(characterDir, settings.Map, "Player", "Skills.dat");
                try
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }
                    var data = File.ReadAllBytes(file);
                    if (data.Length == 0 || data[0] < 6)
                    {
                        continue;
                    }
                    var offset = 1 + 4 + (data[0] >= 7 ? 4 : 0) + 1;
                    var changed = false;
                    for (var speciality = 0; speciality < SkillMax.Length; speciality++)
                    {
                        for (var skill = 0; skill < SkillMax[speciality].Length; skill++, offset++)
                        {
                            if (offset >= data.Length)
                            {
                                break;
                            }
                            var wanted = mode == "all" || (speciality == 0 && skill >= 3 && skill <= 6);
                            if (wanted && data[offset] < SkillMax[speciality][skill])
                            {
                                data[offset] = SkillMax[speciality][skill];
                                changed = true;
                            }
                        }
                    }
                    if (changed)
                    {
                        File.WriteAllBytes(file, data);
                        updated++;
                    }
                }
                catch (Exception ex)
                {
                    m_Log(L.Tr("AVISO: não consegui ajustar as habilidades em ") + file + ": " + ex.Message);
                }
            }
            if (updated > 0)
            {
                m_Log(L.Tr("Habilidades iniciais aplicadas a personagens existentes: ") + updated + ".");
            }
        }

        /// <summary>Grava "  chave: valor" dentro de uma secao de topo do YAML (cria se faltar).</summary>
        private static string SetYamlKey(string text, string section, string key, string value, string comment)
        {
            var keyPattern = @"(?m)^(  " + Regex.Escape(key) + @":)[ \t]*[^\s#]*";
            if (Regex.IsMatch(text, keyPattern))
            {
                return Regex.Replace(text, keyPattern, "$1 " + value);
            }
            var sectionPattern = @"(?m)^" + Regex.Escape(section) + @":[ \t]*\r?\n";
            var entry = "  " + comment + "\n  " + key + ": " + value + "\n";
            return Regex.IsMatch(text, sectionPattern)
                ? Regex.Replace(text, sectionPattern, m => m.Value + entry, RegexOptions.None)
                : text.TrimEnd('\r', '\n') + "\n\n" + section + ":\n" + entry;
        }

        private void DisableBrokenWorkshopDefinitions()
        {
            var content = Path.Combine(m_Paths.ProfileDir, "Workshop", "Steam", "content", GamePaths.UnturnedAppId.ToString());
            foreach (var relative in BrokenWorkshopDefinitions)
            {
                var file = Path.Combine(content, relative);
                if (File.Exists(file))
                {
                    File.Copy(file, file + ".disabled", overwrite: true);
                    File.Delete(file);
                }
            }
        }

        // ------------------------------------------------------------- processo

        public void Start(SkaliaSettings settings)
        {
            if (IsRunning)
            {
                m_Log(L.Tr("O servidor já está rodando."));
                return;
            }
            var info = new ProcessStartInfo
            {
                FileName = m_Paths.ServerExe,
                Arguments = "-batchmode -nographics +LanServer/" + GamePaths.ProfileId,
                WorkingDirectory = m_Paths.U3DSDir,
                UseShellExecute = true,
                WindowStyle = settings.ShowServerConsole ? ProcessWindowStyle.Normal : ProcessWindowStyle.Minimized,
            };
            m_Process = Process.Start(info);
            File.WriteAllText(m_Paths.PidFile, m_Process.Id.ToString(CultureInfo.InvariantCulture));
            m_Log(L.Tr("Servidor LAN iniciado (processo ") + m_Process.Id + L.Tr("). Carregando o mapa..."));
        }

        public void RequestStop()
        {
            if (!IsRunning)
            {
                return;
            }
            File.WriteAllText(m_Paths.StopFile, DateTime.UtcNow.ToString("o"));
            m_Log(L.Tr("Pedido para salvar e desligar enviado ao servidor."));
        }

        public void Kill()
        {
            var process = FindProcess();
            if (process != null && !process.HasExited)
            {
                process.Kill();
                m_Log(L.Tr("Servidor encerrado à força (o progresso desde o último salvamento pode ser perdido)."));
            }
            TryDelete(m_Paths.ReadyFile);
        }

        private Process FindProcess()
        {
            if (m_Process != null && !m_Process.HasExited)
            {
                return m_Process;
            }
            try
            {
                if (File.Exists(m_Paths.PidFile)
                    && int.TryParse(File.ReadAllText(m_Paths.PidFile).Trim(), out var pid))
                {
                    var process = Process.GetProcessById(pid);
                    var path = process.MainModule?.FileName;
                    if (path != null && string.Equals(Path.GetFullPath(path), Path.GetFullPath(m_Paths.ServerExe), StringComparison.OrdinalIgnoreCase))
                    {
                        m_Process = process;
                        return process;
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        /// <summary>O cliente do Unturned esta aberto? (ignora o servidor, que tambem se chama Unturned.exe)</summary>
        private bool IsClientRunning()
        {
            var serverDir = Path.GetFullPath(m_Paths.U3DSDir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            foreach (var process in Process.GetProcessesByName("Unturned"))
            {
                try
                {
                    var file = process.MainModule?.FileName;
                    if (file != null && !file.StartsWith(serverDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Sem acesso ao processo: na duvida, nao e o servidor do SKALIA.
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        public void LaunchClient(SkaliaSettings settings)
        {
            var args = "+connect 127.0.0.1:" + settings.Port.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(settings.Password))
            {
                args += " +password " + settings.Password.Trim();
            }
            if (IsClientRunning())
            {
                // Com o jogo ja aberto, o -applaunch so traz a janela pra frente e ignora os argumentos.
                // A URL steam://run chega no jogo aberto (NewUrlLaunchParameters) e abre a tela do servidor.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://run/" + GamePaths.UnturnedAppId + "//" + Uri.EscapeDataString(args) + "/",
                    UseShellExecute = true,
                });
                m_Log(L.Tr("O Unturned já está aberto: enviando a conexão para ele. Se você estiver em outro servidor, saia dele antes."));
                return;
            }
            if (m_Paths.SteamExe != null && File.Exists(m_Paths.SteamExe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = m_Paths.SteamExe,
                    Arguments = "-applaunch " + GamePaths.UnturnedAppId + " " + args,
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "steam://run/" + GamePaths.UnturnedAppId + "//" + Uri.EscapeDataString(args) + "/",
                    UseShellExecute = true,
                });
            }
            m_Log(L.Tr("Abrindo o Unturned pela Steam e conectando em 127.0.0.1:") + settings.Port + L.Tr(" (se a Steam perguntar, escolha jogar sem BattlEye)."));
        }

        // ------------------------------------------------------------- utilidades

        private static string Sanitize(string value) => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string JsonEscape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static void TryDelete(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }

        // Mapas da Workshop tem caminhos com mais de 260 caracteres; o prefixo \\?\
        // libera caminhos longos no .NET Framework sem mexer no registro do Windows.
        private static string LongPath(string path)
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                return full;
            }
            return full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full.Substring(2)
                : @"\\?\" + full;
        }

        private static void CopyDirectory(string source, string target, bool overwrite = false)
        {
            CopyDirectoryLong(LongPath(source), LongPath(target), overwrite);
        }

        private static void CopyDirectoryLong(string source, string target, bool overwrite)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
            {
                var destination = target + "\\" + Path.GetFileName(file);
                if (overwrite || !File.Exists(destination))
                {
                    File.Copy(file, destination, overwrite: true);
                }
            }
            foreach (var dir in Directory.GetDirectories(source))
            {
                CopyDirectoryLong(dir, target + "\\" + Path.GetFileName(dir), overwrite);
            }
        }
    }
}
