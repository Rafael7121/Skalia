using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;
using SDG.Unturned;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class ExperienceBarService : IExperienceBarService
    {
        public const ushort DefaultEffectId = 59113;
        public const short DefaultEffectKey = 5913;
        private const int ProgressSegmentCount = 100;
        private const int TimingFlushSeconds = 60;
        private const string KillsValueName = "BifrostRankKillsValue";
        private const string KdValueName = "BifrostRankKdValue";
        private const string PositionValueName = "BifrostRankPositionValue";
        private const string KillsTooltipTextName = "BifrostKillsTooltipText";
        private const string KdTooltipTextName = "BifrostKdTooltipText";
        private const string PositionTooltipTextName = "BifrostPositionTooltipText";
        private const string ProgressTooltipTextName = "BifrostRankProgressTooltipText";
        private const string GameplayHudRootName = "BifrostRankGameplayHud";

        private readonly ILogger<ExperienceBarService> m_Logger;
        private readonly IPlayerStatsFileStore m_StatsStore;
        private readonly Dictionary<PlayerQuests, ulong> m_QuestOwners =
            new Dictionary<PlayerQuests, ulong>();
        private readonly Dictionary<ulong, int> m_LastProgressSegments =
            new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, PlayerTiming> m_PlayerTimings =
            new Dictionary<ulong, PlayerTiming>();
        private readonly bool m_Enabled;
        private readonly ushort m_EffectId;
        private readonly short m_EffectKey;
        private readonly int m_ZombiePoints;
        private readonly int m_MegaZombiePoints;
        private readonly int m_PlayerKillPoints;
        private readonly int m_QuestPoints;
        private readonly int m_BaseLevelRequirement;
        private readonly int m_LevelRequirementGrowth;
        private readonly string m_ServerId;
        private RankProgressData m_Data = new RankProgressData();
        private bool m_Loaded;
        private bool m_MissingEffectLogged;
        private bool m_Started;
        private CancellationTokenSource? m_SharedSyncCancellation;

        public ExperienceBarService(
            IConfiguration configuration,
            IPlayerStatsFileStore statsStore,
            ILogger<ExperienceBarService> logger)
        {
            m_StatsStore = statsStore;
            m_Logger = logger;
            m_Enabled = configuration.GetValue("experienceBar:enabled", true);
            m_EffectId = (ushort)Math.Max(1, Math.Min(ushort.MaxValue,
                configuration.GetValue("experienceBar:effectId", (int)DefaultEffectId)));
            m_EffectKey = (short)Math.Max(short.MinValue + 1, Math.Min(short.MaxValue,
                configuration.GetValue("experienceBar:effectKey", (int)DefaultEffectKey)));
            m_ZombiePoints = Math.Max(0,
                configuration.GetValue("experienceBar:points:zombie", 10));
            m_MegaZombiePoints = Math.Max(m_ZombiePoints,
                configuration.GetValue("experienceBar:points:megaZombie", 100));
            m_PlayerKillPoints = Math.Max(0,
                configuration.GetValue("experienceBar:points:playerKill", 250));
            m_QuestPoints = Math.Max(0,
                configuration.GetValue("experienceBar:points:quest", 200));
            m_BaseLevelRequirement = Math.Max(100,
                configuration.GetValue("experienceBar:progression:baseRequirement", 1000));
            m_LevelRequirementGrowth = Math.Max(0,
                configuration.GetValue("experienceBar:progression:requirementGrowth", 250));
            m_ServerId = configuration.GetValue<string>("serverId") ?? "default";
        }

        public void Start()
        {
            if (!m_Enabled || m_Started)
            {
                return;
            }

            InventoryGestureRequestPatch.InventoryVisibilityRequested +=
                OnInventoryVisibilityRequested;
            StorageVisibilityPatch.StorageVisibilityChanged +=
                OnStorageVisibilityChanged;
            m_SharedSyncCancellation = new CancellationTokenSource();
            RefreshSharedStatsLoopAsync(m_SharedSyncCancellation.Token).Forget();
            m_Started = true;
        }

        public async UniTask RegisterAsync(UnturnedPlayer player)
        {
            if (!m_Enabled || IsSyntheticPlayer(player))
            {
                return;
            }

            await EnsureLoadedAsync();
            m_Data = await m_StatsStore.EnsurePlayerAsync(
                player.SteamId.m_SteamID,
                GetPlayerName(player.SteamPlayer),
                m_ServerId);
            await UniTask.SwitchToMainThread();
            RegisterTiming(player.SteamId.m_SteamID);
            RegisterQuestListener(player);
            Show(player.Player);
            UpdateAllRankPositions();
        }

        public async UniTask UnregisterAsync(UnturnedPlayer player)
        {
            ulong steamId = player.SteamId.m_SteamID;
            string playerName = GetPlayerName(player.SteamPlayer);
            TimingDelta timing = CaptureTiming(
                steamId,
                includeLifeCandidate: true,
                finishSession: true);
            if (timing.HasValue)
            {
                m_Data = await m_StatsStore.ApplyDeltaAsync(
                    steamId,
                    playerName,
                    m_ServerId,
                    totalPlaytimeSeconds: timing.PlaytimeSeconds,
                    longestLifeCandidateSeconds: timing.LifeSeconds);
            }

            await UniTask.SwitchToMainThread();
            PlayerQuests quests = player.Player.quests;
            if (quests != null && m_QuestOwners.Remove(quests))
            {
                quests.questCompleted -= OnQuestCompleted;
            }

            m_LastProgressSegments.Remove(steamId);
        }

        public void RecordPlayerSpawned(UnturnedPlayer player)
        {
            if (!m_Enabled || IsSyntheticPlayer(player))
            {
                return;
            }

            ulong steamId = player.SteamId.m_SteamID;
            RegisterTiming(steamId);
            m_PlayerTimings[steamId].LifeStartedUtc = DateTime.UtcNow;
        }

        public async UniTask RecordZombieKillAsync(UnturnedPlayer player, bool isMega)
        {
            if (!m_Enabled || IsSyntheticPlayer(player))
            {
                return;
            }

            ulong steamId = player.SteamId.m_SteamID;
            m_Data = await m_StatsStore.ApplyDeltaAsync(
                steamId,
                GetPlayerName(player.SteamPlayer),
                m_ServerId,
                rankPoints: isMega ? m_MegaZombiePoints : m_ZombiePoints,
                zombieKills: isMega ? 0 : 1,
                megaZombieKills: isMega ? 1 : 0);
            m_Loaded = true;
            await UniTask.SwitchToMainThread();
            RefreshAfterProgressChange(player.Player);
        }

        public async UniTask RecordPlayerDeathAsync(UnturnedPlayer victim, ulong killerId)
        {
            if (!m_Enabled || IsSyntheticPlayer(victim))
            {
                return;
            }

            ulong victimId = victim.SteamId.m_SteamID;
            TimingDelta timing = CaptureTiming(
                victimId,
                includeLifeCandidate: true,
                finishSession: false);
            m_Data = await m_StatsStore.ApplyDeltaAsync(
                victimId,
                GetPlayerName(victim.SteamPlayer),
                m_ServerId,
                deaths: 1,
                totalPlaytimeSeconds: timing.PlaytimeSeconds,
                longestLifeCandidateSeconds: timing.LifeSeconds);
            m_Loaded = true;
            await UniTask.SwitchToMainThread();
            UpdatePlayer(victim.Player, updateSegments: false);

            if (killerId != 0 && killerId != victimId)
            {
                SteamPlayer killer = PlayerTool.getSteamPlayer(new Steamworks.CSteamID(killerId));
                if (killer?.player != null && !IsSyntheticPlayer(killer))
                {
                    m_Data = await m_StatsStore.ApplyDeltaAsync(
                        killerId,
                        GetPlayerName(killer),
                        m_ServerId,
                        rankPoints: m_PlayerKillPoints,
                        playerKills: 1);
                    await UniTask.SwitchToMainThread();
                    RefreshAfterProgressChange(killer.player);
                    return;
                }
            }

            UpdateAllRankPositions();
        }

        public async UniTask SyncOnlinePlayersAsync()
        {
            if (!m_Enabled)
            {
                return;
            }

            foreach (SteamPlayer client in Provider.clients)
            {
                if (client.player == null || IsSyntheticPlayer(client))
                {
                    continue;
                }

                m_Data = await m_StatsStore.EnsurePlayerAsync(
                    client.playerID.steamID.m_SteamID,
                    GetPlayerName(client),
                    m_ServerId);
                m_Loaded = true;
                await UniTask.SwitchToMainThread();
                RegisterTiming(client.playerID.steamID.m_SteamID);
                RegisterQuestListener(client.player);
                Show(client.player);
            }

            UpdateAllRankPositions();
        }

        public async UniTask StopAsync()
        {
            m_SharedSyncCancellation?.Cancel();
            m_SharedSyncCancellation?.Dispose();
            m_SharedSyncCancellation = null;
            await UniTask.SwitchToMainThread();
            await FlushAllTimingsAsync();
            await UniTask.SwitchToMainThread();
            if (m_Started)
            {
                InventoryGestureRequestPatch.InventoryVisibilityRequested -=
                    OnInventoryVisibilityRequested;
                StorageVisibilityPatch.StorageVisibilityChanged -=
                    OnStorageVisibilityChanged;
                m_Started = false;
            }

            foreach (PlayerQuests quests in m_QuestOwners.Keys.ToArray())
            {
                quests.questCompleted -= OnQuestCompleted;
            }

            m_QuestOwners.Clear();
            foreach (SteamPlayer client in Provider.clients)
            {
                if (client.player != null)
                {
                    EffectManager.askEffectClearByID(m_EffectId, client.transportConnection);
                }
            }

            m_LastProgressSegments.Clear();
            m_PlayerTimings.Clear();
        }

        private async UniTaskVoid RefreshSharedStatsLoopAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(10),
                        cancellationToken: cancellationToken);
                    await UniTask.SwitchToMainThread(cancellationToken);
                    await FlushDueTimingsAsync();
                    RankProgressData snapshot = await m_StatsStore.LoadAllAsync();
                    await UniTask.SwitchToMainThread(cancellationToken);
                    m_Data = snapshot;
                    m_Loaded = true;
                    UpdateAllRankPositions();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Nao foi possivel sincronizar os status compartilhados entre servidores.");
            }
        }

        public async UniTask<bool> ToggleUiAsync(UnturnedPlayer player)
        {
            if (!m_Enabled || IsSyntheticPlayer(player))
            {
                return false;
            }

            await EnsureLoadedAsync();
            ulong steamId = player.SteamId.m_SteamID;
            bool visible = !GetOrCreate(steamId).UiVisible;
            m_Data = await m_StatsStore.SetUiVisibleAsync(
                steamId,
                GetPlayerName(player.SteamPlayer),
                m_ServerId,
                visible);
            await UniTask.SwitchToMainThread();
            if (visible)
            {
                Show(player.Player);
            }
            else
            {
                EffectManager.askEffectClearByID(
                    m_EffectId,
                    player.SteamPlayer.transportConnection);
                m_LastProgressSegments.Remove(steamId);
            }

            return visible;
        }

        private void OnInventoryVisibilityRequested(
            PlayerAnimator animator,
            bool isInventoryOpen)
        {
            if (!m_Enabled || animator?.player == null)
            {
                return;
            }

            SetGameplayHudVisible(animator.player, !isInventoryOpen);
        }

        private void OnStorageVisibilityChanged(
            PlayerInventory inventory,
            bool isStorageOpen)
        {
            if (!m_Enabled || !isStorageOpen)
            {
                return;
            }

            Player player = Provider.clients
                .Select(client => client.player)
                .FirstOrDefault(candidate =>
                    candidate != null && ReferenceEquals(candidate.inventory, inventory));
            if (player != null)
            {
                SetGameplayHudVisible(player, false);
            }
        }

        private void SetGameplayHudVisible(Player player, bool visible)
        {
            EffectManager.sendUIEffectVisibility(
                m_EffectKey,
                player.channel.owner.transportConnection,
                true,
                GameplayHudRootName,
                visible && IsUiVisible(GetSteamId(player)));
        }

        private void RegisterQuestListener(UnturnedPlayer player)
        {
            RegisterQuestListener(player.Player);
        }

        private void RegisterQuestListener(Player player)
        {
            PlayerQuests quests = player.quests;
            if (quests == null || m_QuestOwners.ContainsKey(quests))
            {
                return;
            }

            m_QuestOwners[quests] = player.channel.owner.playerID.steamID.m_SteamID;
            quests.questCompleted += OnQuestCompleted;
        }

        private void OnQuestCompleted(PlayerQuests sender, QuestAsset asset)
        {
            if (m_QuestOwners.TryGetValue(sender, out ulong steamId))
            {
                RecordQuestCompletedAsync(steamId).Forget();
            }
        }

        private async UniTaskVoid RecordQuestCompletedAsync(ulong steamId)
        {
            try
            {
                SteamPlayer client = PlayerTool.getSteamPlayer(new Steamworks.CSteamID(steamId));
                string playerName = client == null ? string.Empty : GetPlayerName(client);
                m_Data = await m_StatsStore.ApplyDeltaAsync(
                    steamId,
                    playerName,
                    m_ServerId,
                    rankPoints: m_QuestPoints,
                    questsCompleted: 1);
                m_Loaded = true;
                await UniTask.SwitchToMainThread();
                if (client?.player != null)
                {
                    RefreshAfterProgressChange(client.player);
                }
                else
                {
                    UpdateAllRankPositions();
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Nao foi possivel registrar a quest concluida no rank de {SteamId}.",
                    steamId);
            }
        }

        private void RefreshAfterProgressChange(Player player)
        {
            UpdatePlayer(player, updateSegments: true);
            UpdateAllRankPositions();
        }

        private void Show(Player player)
        {
            ulong steamId = GetSteamId(player);
            if (!IsUiVisible(steamId))
            {
                EffectManager.askEffectClearByID(
                    m_EffectId,
                    player.channel.owner.transportConnection);
                return;
            }

            var asset = Assets.find(EAssetType.EFFECT, m_EffectId) as EffectAsset;
            if (asset == null)
            {
                if (!m_MissingEffectLogged)
                {
                    m_MissingEffectLogged = true;
                    m_Logger.LogWarning(
                        "Effect UI {EffectId} do HUD de rank nao foi encontrado. Atualize o pacote de interface da Workshop (3765696023) e reinicie o servidor.",
                        m_EffectId);
                }

                return;
            }

#pragma warning disable CS0612
            EffectManager.sendUIEffect(
                m_EffectId,
                m_EffectKey,
                player.channel.owner.playerID.steamID,
                true);
#pragma warning restore CS0612
            m_LastProgressSegments.Remove(steamId);
            UpdatePlayer(player, updateSegments: true);
        }

        private void UpdatePlayer(Player player, bool updateSegments)
        {
            ulong steamId = GetSteamId(player);
            RankPlayerData data = GetOrCreate(steamId);
            if (!data.UiVisible)
            {
                return;
            }
            string kills = data.PlayerKills.ToString(CultureInfo.InvariantCulture);
            string kd = CalculateKd(data).ToString("0.00", CultureInfo.InvariantCulture);
            int position = GetRankPosition(steamId);
            RankLevelProgress progress = CalculateProgress(data.RankPoints);
            var connection = player.channel.owner.transportConnection;

            SetText(connection, KillsValueName, kills);
            SetText(connection, KdValueName, kd);
            SetText(connection, PositionValueName, "#" + position.ToString(CultureInfo.InvariantCulture));
            SetText(connection, KillsTooltipTextName, "Abates PvP: " + kills);
            SetText(connection, KdTooltipTextName, "K/D Ratio: " + kd);
            SetText(connection, PositionTooltipTextName,
                BuildPositionTooltip(steamId));
            SetText(connection, ProgressTooltipTextName,
                "Próximo rank: Bóndi\nProgresso: " +
                progress.Current.ToString(CultureInfo.InvariantCulture) +
                "/" + progress.Required.ToString(CultureInfo.InvariantCulture));

            if (updateSegments)
            {
                UpdateProgressSegments(player, progress);
            }
        }

        private void UpdateAllRankPositions()
        {
            foreach (SteamPlayer client in Provider.clients)
            {
                if (client.player == null || IsSyntheticPlayer(client))
                {
                    continue;
                }

                ulong steamId = client.playerID.steamID.m_SteamID;
                if (!IsUiVisible(steamId))
                {
                    continue;
                }

                int position = GetRankPosition(steamId);
                SetText(
                    client.transportConnection,
                    PositionValueName,
                    "#" + position.ToString(CultureInfo.InvariantCulture));
                SetText(
                    client.transportConnection,
                    PositionTooltipTextName,
                    BuildPositionTooltip(steamId));
            }
        }

        private string BuildPositionTooltip(ulong steamId)
        {
            int position = GetRankPosition(steamId);
            RankPlayerData current = GetOrCreate(steamId);
            var text = new StringBuilder();
            text.AppendLine("Top 5 global:");

            int rank = 1;
            foreach (var pair in GetOrderedRanking().Take(5))
            {
                text.Append('#')
                    .Append(rank.ToString(CultureInfo.InvariantCulture))
                    .Append(' ')
                    .Append(GetDisplayName(pair.Value))
                    .Append(" — ")
                    .Append(pair.Value.RankPoints.ToString(CultureInfo.InvariantCulture))
                    .Append(" pts");
                text.AppendLine();
                rank++;
            }

            text.AppendLine();
            text.Append("Você: #")
                .Append(position.ToString(CultureInfo.InvariantCulture))
                .Append(" — ")
                .Append(GetDisplayName(current));

            return text.ToString();
        }

        private void UpdateProgressSegments(Player player, RankLevelProgress progress)
        {
            ulong steamId = GetSteamId(player);
            int visibleSegments = progress.Required <= 0
                ? 0
                : (int)Math.Floor(progress.Current * 100d / progress.Required);
            visibleSegments = Math.Max(0, Math.Min(ProgressSegmentCount, visibleSegments));
            int previous = m_LastProgressSegments.TryGetValue(steamId, out int last) ? last : -1;
            int start = previous < 0 ? 1 : Math.Min(previous, visibleSegments) + 1;
            int end = previous < 0 ? ProgressSegmentCount : Math.Max(previous, visibleSegments);
            for (int segment = start; segment <= end; segment++)
            {
                EffectManager.sendUIEffectVisibility(
                    m_EffectKey,
                    player.channel.owner.transportConnection,
                    true,
                    GetSegmentName(segment),
                    segment <= visibleSegments);
            }

            m_LastProgressSegments[steamId] = visibleSegments;
        }

        private void SetText(
            SDG.NetTransport.ITransportConnection connection,
            string childName,
            string value)
        {
            EffectManager.sendUIEffectText(m_EffectKey, connection, true, childName, value);
        }

        private RankPlayerData GetOrCreate(ulong steamId)
        {
            if (!m_Data.Players.TryGetValue(steamId, out RankPlayerData data))
            {
                data = new RankPlayerData { SteamId = steamId };
                m_Data.Players[steamId] = data;
            }

            return data;
        }

        private int GetRankPosition(ulong steamId)
        {
            int position = 1;
            foreach (var pair in GetOrderedRanking())
            {
                if (pair.Key == steamId)
                {
                    return position;
                }

                position++;
            }

            return position;
        }

        private IOrderedEnumerable<KeyValuePair<ulong, RankPlayerData>> GetOrderedRanking()
        {
            return m_Data.Players
                .Where(entry => HasDisplayName(entry.Value))
                .OrderByDescending(entry => entry.Value.RankPoints)
                .ThenByDescending(entry => entry.Value.PlayerKills)
                .ThenBy(entry => entry.Key);
        }

        private static string GetDisplayName(RankPlayerData data)
        {
            string name = ChatNamePrefixService.StripPrefix(data.PlayerName).Trim();
            return name.Length <= 24 ? name : name.Substring(0, 21) + "...";
        }

        private static bool HasDisplayName(RankPlayerData data)
        {
            return !string.IsNullOrWhiteSpace(
                ChatNamePrefixService.StripPrefix(data.PlayerName));
        }

        private RankLevelProgress CalculateProgress(long totalPoints)
        {
            long current = Math.Max(0, totalPoints);
            long required = m_BaseLevelRequirement;
            while (current >= required)
            {
                current -= required;
                required = Math.Min(int.MaxValue, required + m_LevelRequirementGrowth);
            }

            return new RankLevelProgress(current, required);
        }

        private static double CalculateKd(RankPlayerData data)
        {
            if (data.Deaths <= 0)
            {
                return data.PlayerKills;
            }

            return data.PlayerKills / (double)data.Deaths;
        }

        private void RegisterTiming(ulong steamId)
        {
            if (!m_PlayerTimings.ContainsKey(steamId))
            {
                DateTime now = DateTime.UtcNow;
                m_PlayerTimings[steamId] = new PlayerTiming(now, now);
            }
        }

        private TimingDelta CaptureTiming(
            ulong steamId,
            bool includeLifeCandidate,
            bool finishSession)
        {
            if (!m_PlayerTimings.TryGetValue(steamId, out PlayerTiming timing))
            {
                return default;
            }

            DateTime now = DateTime.UtcNow;
            long playtimeSeconds = ElapsedWholeSeconds(timing.PlaytimeCheckpointUtc, now);
            if (playtimeSeconds > 0)
            {
                timing.PlaytimeCheckpointUtc = timing.PlaytimeCheckpointUtc.AddSeconds(
                    playtimeSeconds);
            }

            long lifeSeconds = 0;
            if (includeLifeCandidate && timing.LifeStartedUtc.HasValue)
            {
                lifeSeconds = ElapsedWholeSeconds(timing.LifeStartedUtc.Value, now);
                timing.LifeStartedUtc = null;
            }

            if (finishSession)
            {
                m_PlayerTimings.Remove(steamId);
            }

            return new TimingDelta(playtimeSeconds, lifeSeconds);
        }

        private async UniTask FlushDueTimingsAsync()
        {
            DateTime now = DateTime.UtcNow;
            ulong[] due = m_PlayerTimings
                .Where(pair =>
                    ElapsedWholeSeconds(pair.Value.PlaytimeCheckpointUtc, now) >=
                    TimingFlushSeconds)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (ulong steamId in due)
            {
                TimingDelta timing = CaptureTiming(
                    steamId,
                    includeLifeCandidate: false,
                    finishSession: false);
                if (!timing.HasValue)
                {
                    continue;
                }

                SteamPlayer client = PlayerTool.getSteamPlayer(
                    new Steamworks.CSteamID(steamId));
                m_Data = await m_StatsStore.ApplyDeltaAsync(
                    steamId,
                    client == null ? string.Empty : GetPlayerName(client),
                    m_ServerId,
                    totalPlaytimeSeconds: timing.PlaytimeSeconds);
                await UniTask.SwitchToMainThread();
            }
        }

        private async UniTask FlushAllTimingsAsync()
        {
            foreach (ulong steamId in m_PlayerTimings.Keys.ToArray())
            {
                SteamPlayer client = PlayerTool.getSteamPlayer(
                    new Steamworks.CSteamID(steamId));
                TimingDelta timing = CaptureTiming(
                    steamId,
                    includeLifeCandidate: true,
                    finishSession: true);
                if (!timing.HasValue)
                {
                    continue;
                }

                m_Data = await m_StatsStore.ApplyDeltaAsync(
                    steamId,
                    client == null ? string.Empty : GetPlayerName(client),
                    m_ServerId,
                    totalPlaytimeSeconds: timing.PlaytimeSeconds,
                    longestLifeCandidateSeconds: timing.LifeSeconds);
                await UniTask.SwitchToMainThread();
            }
        }

        private static long ElapsedWholeSeconds(DateTime startedUtc, DateTime nowUtc)
        {
            return Math.Max(0, (long)Math.Floor((nowUtc - startedUtc).TotalSeconds));
        }

        private async UniTask EnsureLoadedAsync()
        {
            if (m_Loaded)
            {
                return;
            }

            m_Data = await m_StatsStore.LoadAllAsync();
            m_Data.Players ??= new Dictionary<ulong, RankPlayerData>();
            m_Loaded = true;
        }

        private bool IsUiVisible(ulong steamId)
        {
            return !m_Data.Players.TryGetValue(steamId, out RankPlayerData data)
                || data.UiVisible;
        }

        private static ulong GetSteamId(Player player)
        {
            return player.channel.owner.playerID.steamID.m_SteamID;
        }

        private static string GetPlayerName(SteamPlayer player)
        {
            string name = player.playerID.characterName ??
                player.playerID.playerName ??
                string.Empty;
            return ChatNamePrefixService.StripPrefix(name);
        }

        private static string GetSegmentName(int number)
        {
            return "BifrostRankProgressSegment" + number.ToString("D3", CultureInfo.InvariantCulture);
        }

        private static bool IsSyntheticPlayer(UnturnedPlayer player)
        {
            return IsSyntheticPlayer(player.SteamPlayer);
        }

        private static bool IsSyntheticPlayer(SteamPlayer player)
        {
            string typeName = player.transportConnection?.GetType().FullName ?? string.Empty;
            return typeName.IndexOf("BotTransportConnection", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private readonly struct RankLevelProgress
        {
            public RankLevelProgress(long current, long required)
            {
                Current = current;
                Required = required;
            }

            public long Current { get; }
            public long Required { get; }
        }

        private sealed class PlayerTiming
        {
            public PlayerTiming(DateTime playtimeCheckpointUtc, DateTime lifeStartedUtc)
            {
                PlaytimeCheckpointUtc = playtimeCheckpointUtc;
                LifeStartedUtc = lifeStartedUtc;
            }

            public DateTime PlaytimeCheckpointUtc { get; set; }
            public DateTime? LifeStartedUtc { get; set; }
        }

        private readonly struct TimingDelta
        {
            public TimingDelta(long playtimeSeconds, long lifeSeconds)
            {
                PlaytimeSeconds = playtimeSeconds;
                LifeSeconds = lifeSeconds;
            }

            public long PlaytimeSeconds { get; }
            public long LifeSeconds { get; }
            public bool HasValue => PlaytimeSeconds > 0 || LifeSeconds > 0;
        }
    }
}
