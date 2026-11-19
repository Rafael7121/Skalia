using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class CallVoteService : ICallVoteService, IDisposable
    {
        private const int VoteTimeoutSeconds = 300;
        private const int WeatherClearConfirmationSeconds = 60;
        private readonly Dictionary<CallVoteType, CallVoteSession> m_ActiveVotes =
            new Dictionary<CallVoteType, CallVoteSession>();
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<CallVoteService> m_Logger;
        private readonly IPlayerActivityService m_PlayerActivityService;
        private CancellationTokenSource? m_MonitorCancellation;
        private bool m_SkipNightVoteOfferedForCurrentNight;
        private bool m_WeatherVoteOfferedForCurrentEvent;
        private DateTimeOffset? m_WeatherClearSince;
        private bool m_Started;

        public CallVoteService(
            IStringLocalizer stringLocalizer,
            ILogger<CallVoteService> logger,
            IPlayerActivityService playerActivityService)
        {
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_PlayerActivityService = playerActivityService;
        }

        public void Start()
        {
            Stop();
            m_Started = true;
            m_PlayerActivityService.AwayStateChanged += HandleAwayStateChanged;
            m_MonitorCancellation = new CancellationTokenSource();
            MonitorVoteWindowsAsync(m_MonitorCancellation.Token).Forget();
        }

        public void Stop()
        {
            if (m_Started)
            {
                m_PlayerActivityService.AwayStateChanged -= HandleAwayStateChanged;
                m_Started = false;
            }

            m_MonitorCancellation?.Cancel();
            m_MonitorCancellation?.Dispose();
            m_MonitorCancellation = null;
            m_SkipNightVoteOfferedForCurrentNight = false;
            m_WeatherVoteOfferedForCurrentEvent = false;
            m_WeatherClearSince = null;

            foreach (var session in m_ActiveVotes.Values.ToArray())
            {
                session.Cancel();
            }

            m_ActiveVotes.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        public async Task CastVoteAsync(UnturnedPlayer player, CallVoteType type)
        {
            await UniTask.SwitchToMainThread();

            if (!IsVoteEnvironmentActive(type) ||
                !m_ActiveVotes.TryGetValue(type, out var session))
            {
                await TellAsync(player, GetMessageKey(type, "unavailable"));
                return;
            }

            var playerId = player.SteamId.m_SteamID;
            var eligiblePlayers = GetEligiblePlayerIds(type);
            if (!eligiblePlayers.Contains(playerId))
            {
                await TellAsync(player, type == CallVoteType.WeatherNone
                    ? "messages:call_vote:weather_outside_region"
                    : GetMessageKey(type, "unavailable"));
                return;
            }

            if (!session.Voters.Add(playerId))
            {
                await TellAsync(player, GetMessageKey(type, "already_voted"));
                return;
            }

            var votes = CountActiveVotes(session, eligiblePlayers);
            var required = GetRequiredVotes(eligiblePlayers.Count);
            SendToPlayers(eligiblePlayers, GetMessageKey(type, "vote_registered"), new
            {
                Player = GetPlayerName(player),
                Votes = votes,
                Required = required,
                PercentageDisplay = GetVotePercentageDisplay(votes, required),
                Players = eligiblePlayers.Count
            });

            TryCompleteVote(session, eligiblePlayers);
        }

        public async Task HandleConnectedAsync(UnturnedPlayer player)
        {
            await UniTask.SwitchToMainThread();
            foreach (var session in m_ActiveVotes.Values.ToArray())
            {
                if (!IsVoteEnvironmentActive(session.Type))
                {
                    continue;
                }

                var activePlayers = GetEligiblePlayerIds(session.Type);
                if (!activePlayers.Contains(player.SteamId.m_SteamID))
                {
                    continue;
                }
                if (!session.AnnouncedPlayers.Add(player.SteamId.m_SteamID))
                {
                    continue;
                }
                await TellAsync(player, GetMessageKey(session.Type, "prompt"), new
                {
                    Seconds = GetSecondsRemaining(session),
                    Required = GetRequiredVotes(activePlayers.Count),
                    PercentageDisplay = GetVotePercentageDisplay(
                        CountActiveVotes(session, activePlayers),
                        GetRequiredVotes(activePlayers.Count)),
                    Players = activePlayers.Count,
                    Votes = CountActiveVotes(session, activePlayers)
                });
            }
        }

        public async Task HandleDisconnectedAsync(UnturnedPlayer player)
        {
            await UniTask.SwitchToMainThread();
            foreach (var session in m_ActiveVotes.Values)
            {
                session.Voters.Remove(player.SteamId.m_SteamID);
                session.AnnouncedPlayers.Remove(player.SteamId.m_SteamID);
            }

            var activePlayers = GetActivePlayerIds();
            activePlayers.Remove(player.SteamId.m_SteamID);
            if (activePlayers.Count == 0)
            {
                var closedSkipNightVote = m_ActiveVotes.ContainsKey(CallVoteType.SkipNight);
                var closedWeatherVote = m_ActiveVotes.ContainsKey(CallVoteType.WeatherNone);
                foreach (var type in m_ActiveVotes.Keys.ToArray())
                {
                    CloseVote(type);
                }

                if (closedSkipNightVote)
                {
                    m_SkipNightVoteOfferedForCurrentNight = false;
                }

                if (closedWeatherVote)
                {
                    m_WeatherVoteOfferedForCurrentEvent = false;
                    m_WeatherClearSince = null;
                }

                return;
            }

            foreach (var session in m_ActiveVotes.Values.ToArray())
            {
                TryCompleteVote(session, GetEligiblePlayerIds(session.Type));
            }
        }

        private async UniTaskVoid MonitorVoteWindowsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await UniTask.SwitchToMainThread(cancellationToken);
                    UpdateSkipNightWindow();
                    UpdateWeatherWindow();
                    RefreshActiveVotes();
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken: cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do monitor ou do plugin.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "O monitor das votacoes automaticas foi interrompido.");
            }
        }

        private void UpdateSkipNightWindow()
        {
            if (!IsSkipNightEnvironment())
            {
                m_SkipNightVoteOfferedForCurrentNight = false;
                CloseVote(CallVoteType.SkipNight);
                return;
            }

            if (m_SkipNightVoteOfferedForCurrentNight)
            {
                return;
            }

            // Nao consome o periodo de votacao enquanto nao ha ninguem para
            // receber o anuncio. O primeiro jogador online inicia os 300s.
            if (GetActivePlayerIds().Count == 0)
            {
                return;
            }

            m_SkipNightVoteOfferedForCurrentNight = true;
            OpenVote(CallVoteType.SkipNight);
        }

        private void UpdateWeatherWindow()
        {
            if (Level.info == null || Level.info.type != ELevelType.SURVIVAL)
            {
                m_WeatherVoteOfferedForCurrentEvent = false;
                m_WeatherClearSince = null;
                CloseVote(CallVoteType.WeatherNone);
                return;
            }

            if (!HasActiveWeather())
            {
                CloseVote(CallVoteType.WeatherNone);
                m_WeatherClearSince ??= DateTimeOffset.UtcNow;
                if ((DateTimeOffset.UtcNow - m_WeatherClearSince.Value).TotalSeconds >=
                    WeatherClearConfirmationSeconds)
                {
                    m_WeatherVoteOfferedForCurrentEvent = false;
                }
                return;
            }

            m_WeatherClearSince = null;
            if (m_WeatherVoteOfferedForCurrentEvent)
            {
                return;
            }

            var affectedPlayers = GetWeatherAffectedPlayerIds();
            if (affectedPlayers.Count == 0)
            {
                return;
            }

            m_WeatherVoteOfferedForCurrentEvent = true;
            OpenVote(CallVoteType.WeatherNone, affectedPlayers);
        }

        private void RefreshActiveVotes()
        {
            var activePlayers = GetActivePlayerIds();
            if (activePlayers.Count == 0)
            {
                var closedSkipNightVote = m_ActiveVotes.ContainsKey(CallVoteType.SkipNight);
                var closedWeatherVote = m_ActiveVotes.ContainsKey(CallVoteType.WeatherNone);
                foreach (var type in m_ActiveVotes.Keys.ToArray())
                {
                    CloseVote(type);
                }

                if (closedSkipNightVote)
                {
                    m_SkipNightVoteOfferedForCurrentNight = false;
                }

                if (closedWeatherVote)
                {
                    m_WeatherVoteOfferedForCurrentEvent = false;
                    m_WeatherClearSince = null;
                }

                return;
            }

            foreach (var session in m_ActiveVotes.Values.ToArray())
            {
                var eligiblePlayers = GetEligiblePlayerIds(session.Type);
                NotifyNewEligiblePlayers(session, eligiblePlayers);
                TryCompleteVote(session, eligiblePlayers);
            }
        }

        private void HandleAwayStateChanged(
            object? sender,
            PlayerAwayStateChangedEventArgs eventArgs)
        {
            HandleAwayStateChangedAsync(eventArgs).Forget();
        }

        private async UniTaskVoid HandleAwayStateChangedAsync(
            PlayerAwayStateChangedEventArgs eventArgs)
        {
            await UniTask.SwitchToMainThread();
            if (!m_Started)
            {
                return;
            }

            RefreshActiveVotes();
            if (eventArgs.IsAway)
            {
                return;
            }

            var target = Provider.clients.FirstOrDefault(client =>
                client.playerID.steamID.m_SteamID == eventArgs.PlayerId);
            if (target == null)
            {
                return;
            }

            foreach (var session in m_ActiveVotes.Values.ToArray())
            {
                if (!IsVoteEnvironmentActive(session.Type))
                {
                    continue;
                }

                var activePlayers = GetEligiblePlayerIds(session.Type);
                if (!activePlayers.Contains(eventArgs.PlayerId))
                {
                    continue;
                }
                if (!session.AnnouncedPlayers.Add(eventArgs.PlayerId))
                {
                    continue;
                }
                SendToPlayer(target, GetMessageKey(session.Type, "prompt"), new
                {
                    Seconds = GetSecondsRemaining(session),
                    Required = GetRequiredVotes(activePlayers.Count),
                    PercentageDisplay = GetVotePercentageDisplay(
                        CountActiveVotes(session, activePlayers),
                        GetRequiredVotes(activePlayers.Count)),
                    Players = activePlayers.Count,
                    Votes = CountActiveVotes(session, activePlayers)
                });
            }
        }

        private void OpenVote(CallVoteType type, HashSet<ulong>? eligiblePlayers = null)
        {
            if (m_ActiveVotes.ContainsKey(type))
            {
                return;
            }

            var session = new CallVoteSession(
                type,
                TimeSpan.FromSeconds(VoteTimeoutSeconds));
            m_ActiveVotes.Add(session.Type, session);
            var activePlayers = eligiblePlayers ?? GetEligiblePlayerIds(type);
            session.AnnouncedPlayers.UnionWith(activePlayers);
            SendToPlayers(activePlayers, GetMessageKey(type, "prompt"), new
            {
                Seconds = VoteTimeoutSeconds,
                Required = GetRequiredVotes(activePlayers.Count),
                PercentageDisplay = GetVotePercentageDisplay(
                    0,
                    GetRequiredVotes(activePlayers.Count)),
                Players = activePlayers.Count,
                Votes = 0
            });
            ExpireVoteAsync(session, session.Cancellation.Token).Forget();
            m_Logger.LogInformation(
                "Votacao {VoteType} iniciada por {Seconds}s com {Players} jogador(es) online.",
                type,
                VoteTimeoutSeconds,
                activePlayers.Count);
        }

        private async UniTaskVoid ExpireVoteAsync(
            CallVoteSession session,
            CancellationToken cancellationToken)
        {
            try
            {
                await UniTask.Delay(
                    TimeSpan.FromSeconds(VoteTimeoutSeconds),
                    cancellationToken: cancellationToken);
                await UniTask.SwitchToMainThread(cancellationToken);

                if (!m_ActiveVotes.TryGetValue(session.Type, out var activeSession) ||
                    !ReferenceEquals(activeSession, session))
                {
                    return;
                }

                var activePlayers = GetEligiblePlayerIds(session.Type);
                var votes = CountActiveVotes(session, activePlayers);
                var required = GetRequiredVotes(activePlayers.Count);
                m_ActiveVotes.Remove(session.Type);
                session.Cancel();
                SendToPlayers(activePlayers, GetMessageKey(session.Type, "expired"), new
                {
                    Votes = votes,
                    Required = required,
                    PercentageDisplay = GetVotePercentageDisplay(votes, required),
                    Players = activePlayers.Count
                });
                m_Logger.LogInformation(
                    "Votacao {VoteType} expirou com {Votes}/{Required} votos.",
                    session.Type,
                    votes,
                    required);
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal da votacao ou do plugin.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "A votacao {VoteType} foi interrompida.", session.Type);
            }
        }

        private void TryCompleteVote(
            CallVoteSession session,
            HashSet<ulong> activePlayers)
        {
            if (!m_ActiveVotes.TryGetValue(session.Type, out var activeSession) ||
                !ReferenceEquals(activeSession, session))
            {
                return;
            }

            var votes = CountActiveVotes(session, activePlayers);
            var required = GetRequiredVotes(activePlayers.Count);
            if (votes < required)
            {
                return;
            }

            m_ActiveVotes.Remove(session.Type);
            session.Cancel();
            SendToPlayers(activePlayers, GetMessageKey(session.Type, "approved"), new
            {
                Votes = votes,
                Required = required,
                PercentageDisplay = GetVotePercentageDisplay(votes, required),
                Players = activePlayers.Count
            });
            if (session.Type == CallVoteType.SkipNight)
            {
                LightingManager.time = (uint)(LightingManager.cycle * LevelLighting.transition);
            }
            else if (session.Type == CallVoteType.WeatherNone)
            {
                LightingManager.ResetScheduledWeather();
            }

            m_Logger.LogInformation(
                "Votacao {VoteType} aprovada com {Votes}/{Players} votos.",
                session.Type,
                votes,
                activePlayers.Count);
        }

        private void CloseVote(CallVoteType type)
        {
            if (!m_ActiveVotes.TryGetValue(type, out var session))
            {
                return;
            }

            m_ActiveVotes.Remove(type);
            session.Cancel();
        }

        private static bool IsSkipNightEnvironment()
        {
            if (Level.info == null || Level.info.type != ELevelType.SURVIVAL)
            {
                return false;
            }

            // LevelLighting.bias e o instante vanilla de pôr do sol (DUSK).
            // Antes dele ainda ocorre a transicao visual de tarde para crepusculo.
            var sunsetStart = Mathf.Clamp01(LevelLighting.bias);
            var dawnStart = Mathf.Clamp01(1f - LevelLighting.transition);
            var currentTime = LightingManager.day;
            return currentTime >= sunsetStart && currentTime < dawnStart;
        }

        private static bool IsVoteEnvironmentActive(CallVoteType type)
        {
            switch (type)
            {
                case CallVoteType.SkipNight:
                    return IsSkipNightEnvironment();
                case CallVoteType.WeatherNone:
                    return Level.info != null &&
                           Level.info.type == ELevelType.SURVIVAL &&
                           HasActiveWeather();
                default:
                    return false;
            }
        }

        private static string GetMessageKey(CallVoteType type, string message)
        {
            return type == CallVoteType.WeatherNone
                ? $"messages:call_vote:weather_{message}"
                : $"messages:call_vote:{message}";
        }

        private HashSet<ulong> GetActivePlayerIds()
        {
            return Provider.clients
                .Select(client => client.playerID.steamID.m_SteamID)
                .Where(playerId => !m_PlayerActivityService.IsAway(playerId))
                .ToHashSet();
        }

        private HashSet<ulong> GetEligiblePlayerIds(CallVoteType type)
        {
            return type == CallVoteType.WeatherNone
                ? GetWeatherAffectedPlayerIds()
                : GetActivePlayerIds();
        }

        private HashSet<ulong> GetWeatherAffectedPlayerIds()
        {
            var affected = new HashSet<ulong>();
            if (Level.info == null || Level.info.type != ELevelType.SURVIVAL)
            {
                return affected;
            }

            var foundActiveComponent = false;
            foreach (var instance in LevelLighting.customWeatherInstances)
            {
                var component = instance.component;
                if (component == null || !component.isWeatherActive)
                {
                    continue;
                }

                foundActiveComponent = true;

                foreach (var player in component.EnumerateMaskedPlayers())
                {
                    if (player?.channel?.owner == null)
                    {
                        continue;
                    }

                    var playerId = player.channel.owner.playerID.steamID.m_SteamID;
                    if (!m_PlayerActivityService.IsAway(playerId))
                    {
                        affected.Add(playerId);
                    }
                }
            }

            // Mapas sem mascaras regionais usam o clima global tradicional;
            // nesse caso todos os jogadores realmente estao sob o mesmo clima.
            if (!foundActiveComponent && LevelLighting.GetActiveWeatherAsset() != null)
            {
                return GetActivePlayerIds();
            }

            return affected;
        }

        private static bool HasActiveWeather()
        {
            return LevelLighting.GetActiveWeatherAsset() != null ||
                   LevelLighting.customWeatherInstances.Any(instance =>
                       instance.component != null && instance.component.isWeatherActive);
        }

        private void NotifyNewEligiblePlayers(
            CallVoteSession session,
            HashSet<ulong> eligiblePlayers)
        {
            foreach (var playerId in eligiblePlayers)
            {
                if (!session.AnnouncedPlayers.Add(playerId))
                {
                    continue;
                }

                var target = Provider.clients.FirstOrDefault(client =>
                    client.playerID.steamID.m_SteamID == playerId);
                if (target == null)
                {
                    continue;
                }

                var votes = CountActiveVotes(session, eligiblePlayers);
                var required = GetRequiredVotes(eligiblePlayers.Count);
                SendToPlayer(target, GetMessageKey(session.Type, "prompt"), new
                {
                    Seconds = GetSecondsRemaining(session),
                    Votes = votes,
                    Required = required,
                    PercentageDisplay = GetVotePercentageDisplay(votes, required),
                    Players = eligiblePlayers.Count
                });
            }
        }

        private static int CountActiveVotes(
            CallVoteSession session,
            HashSet<ulong> activePlayers)
        {
            return session.Voters.Count(activePlayers.Contains);
        }

        private static int GetRequiredVotes(int activePlayerCount)
        {
            // Maioria estrita: 1/1, 2/2, 2/3, 3/4, etc.
            return activePlayerCount / 2 + 1;
        }

        private static int GetSecondsRemaining(CallVoteSession session)
        {
            return Math.Max(0, (int)Math.Ceiling(
                (session.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        }

        private static string GetVotePercentageDisplay(int votes, int required)
        {
            if (required <= 0)
            {
                return "0%";
            }

            string percentage = (votes * 100d / required).ToString(
                "0.##",
                CultureInfo.InvariantCulture);
            return votes > 0
                ? $"<color=#FFD54F>{percentage}%</color>"
                : "0%";
        }

        private static string GetPlayerName(UnturnedPlayer player)
        {
            return ChatNamePrefixService.StripPrefix(
                player.SteamPlayer.playerID.characterName);
        }

        private async Task TellAsync(
            UnturnedPlayer player,
            string key,
            object? arguments = null)
        {
            await player.PrintMessageAsync(Format(key, arguments));
        }

        private void Broadcast(string key, object? arguments = null)
        {
            foreach (var client in Provider.clients)
            {
                SendToPlayer(client, key, arguments);
            }
        }

        private void SendToPlayers(
            HashSet<ulong> playerIds,
            string key,
            object? arguments = null)
        {
            foreach (var client in Provider.clients.Where(client =>
                         playerIds.Contains(client.playerID.steamID.m_SteamID)))
            {
                SendToPlayer(client, key, arguments);
            }
        }

        private void SendToPlayer(SteamPlayer player, string key, object? arguments = null)
        {
            ChatManager.serverSendMessage(
                Format(key, arguments),
                ServerChatPalette.BroadcastText,
                toPlayer: player,
                useRichTextFormatting: true,
                iconURL: Provider.configData.Browser.Icon);
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return $"<color=#58C7FA>[Votação]</color> {message}";
        }
    }
}
