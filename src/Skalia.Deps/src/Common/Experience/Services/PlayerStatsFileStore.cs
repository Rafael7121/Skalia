using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class PlayerStatsFileStore : IPlayerStatsFileStore
    {
        private const int CurrentSchemaVersion = 2;
        private const string MutexName = "SkaliaDeps.PlayerStats.v1";
        private readonly ILogger<PlayerStatsFileStore> m_Logger;
        private readonly string m_DataDirectory;
        private readonly JsonSerializerOptions m_JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public PlayerStatsFileStore(
            IConfiguration configuration,
            ILogger<PlayerStatsFileStore> logger)
        {
            m_Logger = logger;
            string configuredPath = configuration.GetValue<string>(
                "experienceBar:dataDirectory",
                Path.Combine("Skalia", "data", "player-stats"))
                ?? Path.Combine("Skalia", "data", "player-stats");
            m_DataDirectory = Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(Directory.GetCurrentDirectory(), configuredPath));
        }

        public Task<RankProgressData> LoadAllAsync()
        {
            return RunLockedAsync(LoadAllUnsafe);
        }

        public Task<RankProgressData> EnsurePlayerAsync(
            ulong steamId,
            string playerName,
            string serverId)
        {
            return MutateAsync(steamId, playerName, serverId, _ => { });
        }

        public Task<RankProgressData> ApplyDeltaAsync(
            ulong steamId,
            string playerName,
            string serverId,
            long rankPoints = 0,
            long zombieKills = 0,
            long megaZombieKills = 0,
            long playerKills = 0,
            long deaths = 0,
            long questsCompleted = 0,
            long tpaRequestsSent = 0,
            long tpaRequestsAccepted = 0,
            long totalPlaytimeSeconds = 0,
            long longestLifeCandidateSeconds = 0)
        {
            return MutateAsync(steamId, playerName, serverId, data =>
            {
                data.RankPoints = AddNonNegative(data.RankPoints, rankPoints);
                data.ZombieKills = AddNonNegative(data.ZombieKills, zombieKills);
                data.MegaZombieKills = AddNonNegative(data.MegaZombieKills, megaZombieKills);
                data.PlayerKills = AddNonNegative(data.PlayerKills, playerKills);
                data.Deaths = AddNonNegative(data.Deaths, deaths);
                data.QuestsCompleted = AddNonNegative(data.QuestsCompleted, questsCompleted);
                data.TpaRequestsSent = AddNonNegative(data.TpaRequestsSent, tpaRequestsSent);
                data.TpaRequestsAccepted = AddNonNegative(
                    data.TpaRequestsAccepted,
                    tpaRequestsAccepted);
                data.TotalPlaytimeSeconds = AddNonNegative(
                    data.TotalPlaytimeSeconds,
                    totalPlaytimeSeconds);
                data.LongestLifeSeconds = Math.Max(
                    Math.Max(0, data.LongestLifeSeconds),
                    Math.Max(0, longestLifeCandidateSeconds));
            });
        }

        public Task<RankProgressData> SetUiVisibleAsync(
            ulong steamId,
            string playerName,
            string serverId,
            bool visible)
        {
            return MutateAsync(steamId, playerName, serverId, data =>
            {
                data.UiVisible = visible;
            });
        }

        private Task<RankProgressData> MutateAsync(
            ulong steamId,
            string playerName,
            string serverId,
            Action<RankPlayerData> mutation)
        {
            return RunLockedAsync(() =>
            {
                Directory.CreateDirectory(m_DataDirectory);
                string path = GetPlayerPath(steamId);
                RankPlayerData data = ReadPlayerUnsafe(path) ?? new RankPlayerData();
                data.SchemaVersion = CurrentSchemaVersion;
                data.SteamId = steamId;
                string sanitizedPlayerName = Sanitize(playerName, 80);
                if (!string.IsNullOrEmpty(sanitizedPlayerName))
                {
                    data.PlayerName = sanitizedPlayerName;
                }

                string sanitizedServerId = Sanitize(serverId, 80);
                if (!string.IsNullOrEmpty(sanitizedServerId))
                {
                    data.LastServerId = sanitizedServerId;
                }
                mutation(data);
                data.UpdatedAtUtc = DateTime.UtcNow;
                WritePlayerUnsafe(path, data);
                return LoadAllUnsafe();
            });
        }

        private Task<RankProgressData> RunLockedAsync(Func<RankProgressData> operation)
        {
            return Task.Run(() =>
            {
                using var mutex = new Mutex(false, MutexName);
                bool ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(15));
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        throw new TimeoutException(
                            "Tempo esgotado aguardando o armazenamento compartilhado de status.");
                    }

                    return operation();
                }
                finally
                {
                    if (ownsMutex)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            });
        }

        private RankProgressData LoadAllUnsafe()
        {
            Directory.CreateDirectory(m_DataDirectory);
            var snapshot = new RankProgressData();
            foreach (string path in Directory.GetFiles(m_DataDirectory, "*.json"))
            {
                RankPlayerData? player = ReadPlayerUnsafe(path);
                if (player == null || player.SteamId == 0)
                {
                    continue;
                }

                if (player.SchemaVersion < CurrentSchemaVersion)
                {
                    player.SchemaVersion = CurrentSchemaVersion;
                    WritePlayerUnsafe(path, player);
                }

                snapshot.Players[player.SteamId] = player;
            }

            return snapshot;
        }

        private RankPlayerData? ReadPlayerUnsafe(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return DeserializePlayerUnsafe(path);
            }
            catch (Exception primaryException)
            {
                string backupPath = GetBackupPath(path);
                if (File.Exists(backupPath))
                {
                    try
                    {
                        RankPlayerData backup = DeserializePlayerUnsafe(backupPath);
                        m_Logger.LogWarning(
                            primaryException,
                            "JSON de status invalido em {Path}. Restaurando o backup {BackupPath}.",
                            path,
                            backupPath);
                        try
                        {
                            WritePlayerFileUnsafe(path, backup);
                        }
                        catch (Exception restoreException)
                        {
                            m_Logger.LogWarning(
                                restoreException,
                                "Nao foi possivel restaurar {Path}, mas o backup valido sera usado nesta leitura.",
                                path);
                        }

                        return backup;
                    }
                    catch (Exception backupException)
                    {
                        m_Logger.LogError(
                            backupException,
                            "Backup de status invalido em {BackupPath}.",
                            backupPath);
                    }
                }

                m_Logger.LogError(primaryException, "JSON de status invalido em {Path}.", path);
                return null;
            }
        }

        private void WritePlayerUnsafe(string path, RankPlayerData data)
        {
            WritePlayerFileUnsafe(path, data);

            string backupPath = GetBackupPath(path);
            try
            {
                WritePlayerFileUnsafe(backupPath, data);
            }
            catch (Exception exception)
            {
                // A gravacao principal ja foi concluida. Nao propague esta falha para
                // evitar que o chamador repita a mesma mutacao (por exemplo, XP ou kills).
                m_Logger.LogWarning(
                    exception,
                    "Nao foi possivel atualizar o backup de status em {BackupPath}.",
                    backupPath);
            }
        }

        private RankPlayerData DeserializePlayerUnsafe(string path)
        {
            RankPlayerData? data = JsonSerializer.Deserialize<RankPlayerData>(
                File.ReadAllText(path),
                m_JsonOptions);
            return data ?? throw new JsonException("O arquivo de status contem JSON nulo.");
        }

        private void WritePlayerFileUnsafe(string path, RankPlayerData data)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(data, m_JsonOptions);
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(contents, 0, contents.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static string GetBackupPath(string path)
        {
            return path + ".bak";
        }

        private string GetPlayerPath(ulong steamId)
        {
            return Path.Combine(m_DataDirectory, steamId + ".json");
        }

        private static long AddNonNegative(long current, long delta)
        {
            if (delta <= 0)
            {
                return Math.Max(0, current);
            }

            return current > long.MaxValue - delta ? long.MaxValue : current + delta;
        }

        private static string Sanitize(string value, int maxLength)
        {
            string result = (value ?? string.Empty).Trim();
            return result.Length <= maxLength ? result : result.Substring(0, maxLength);
        }
    }
}
