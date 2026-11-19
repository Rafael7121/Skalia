using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class PlayerActivityService : IPlayerActivityService, IDisposable
    {
        private const byte NeutralWalkingAnalog = 0x11;
        private const float LookActivityThresholdDegrees = 0.05f;
        private const float DrivingInputThreshold = 0.01f;
        private readonly ConcurrentDictionary<ulong, PlayerActivityState> m_States =
            new ConcurrentDictionary<ulong, PlayerActivityState>();
        private readonly ILogger<PlayerActivityService> m_Logger;
        private readonly int m_AwayAfterSeconds;
        private CancellationTokenSource? m_MonitorCancellation;
        private bool m_Started;

        public PlayerActivityService(
            IConfiguration configuration,
            ILogger<PlayerActivityService> logger)
        {
            m_Logger = logger;
            m_AwayAfterSeconds = Clamp(
                configuration.GetValue("playerActivity:awayAfterSeconds", 180),
                30,
                3600);
        }

        public event EventHandler<PlayerAwayStateChangedEventArgs>? AwayStateChanged;

        public void Start()
        {
            Stop();
            try
            {
                m_Started = true;
                PlayerActivityInputBridge.BaseInputRead += HandleBaseInput;
                PlayerActivityInputBridge.WalkingInputRead += HandleWalkingInput;
                PlayerActivityInputBridge.DrivingInputRead += HandleDrivingInput;
                InventoryGestureRequestPatch.InventoryVisibilityRequested +=
                    HandleInventoryVisibilityRequested;

                foreach (var client in Provider.clients)
                {
                    if (client?.player != null)
                    {
                        RegisterPlayer(client.player);
                    }
                }

                m_MonitorCancellation = new CancellationTokenSource();
                MonitorAwayPlayersAsync(m_MonitorCancellation.Token).Forget();
                m_Logger.LogInformation(
                    "Detector AFK iniciado com limite de {Seconds} segundos.",
                    m_AwayAfterSeconds);
            }
            catch
            {
                // Evita handlers estaticos pendurados se o carregamento falhar.
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            if (m_Started)
            {
                PlayerActivityInputBridge.BaseInputRead -= HandleBaseInput;
                PlayerActivityInputBridge.WalkingInputRead -= HandleWalkingInput;
                PlayerActivityInputBridge.DrivingInputRead -= HandleDrivingInput;
                InventoryGestureRequestPatch.InventoryVisibilityRequested -=
                    HandleInventoryVisibilityRequested;
                m_Started = false;
            }

            m_MonitorCancellation?.Cancel();
            m_MonitorCancellation?.Dispose();
            m_MonitorCancellation = null;
            m_States.Clear();
        }

        public void Dispose()
        {
            Stop();
        }

        public void RegisterPlayer(Player player)
        {
            if (player?.channel?.owner == null)
            {
                return;
            }

            var owner = player.channel.owner;
            var playerId = owner.playerID.steamID.m_SteamID;
            var playerName = owner.playerID.characterName;
            m_States.TryAdd(
                playerId,
                new PlayerActivityState(player, playerName, GetMonotonicSeconds()));
        }

        public void UnregisterPlayer(ulong playerId)
        {
            m_States.TryRemove(playerId, out _);
        }

        public void MarkActivity(ulong playerId)
        {
            if (!TryGetOrCreateState(playerId, out var state))
            {
                return;
            }

            PlayerAwayStateChangedEventArgs? stateChange;
            lock (state.SyncRoot)
            {
                stateChange = MarkActivityStateUnsafe(playerId, state);
            }
            PublishStateChange(stateChange);
        }

        public bool IsAway(ulong playerId)
        {
            if (!m_States.TryGetValue(playerId, out var state))
            {
                return false;
            }

            lock (state.SyncRoot)
            {
                return state.IsAway;
            }
        }

        private void HandleBaseInput(
            Player player,
            ushort keys,
            EAttackInputFlags primaryAttack,
            EAttackInputFlags secondaryAttack,
            float yaw,
            float pitch)
        {
            var playerId = player.channel.owner.playerID.steamID.m_SteamID;
            if (!TryGetOrCreateState(player, out var state))
            {
                return;
            }

            PlayerAwayStateChangedEventArgs? stateChange;
            lock (state.SyncRoot)
            {
                var hasActivity = false;
                if (state.HasBaseInputSnapshot)
                {
                    hasActivity = keys != state.LastKeys ||
                                  primaryAttack != EAttackInputFlags.None ||
                                  secondaryAttack != EAttackInputFlags.None ||
                                  Mathf.Abs(Mathf.DeltaAngle(state.LastYaw, yaw)) >=
                                  LookActivityThresholdDegrees ||
                                  Mathf.Abs(state.LastPitch - pitch) >=
                                  LookActivityThresholdDegrees;
                }

                state.HasBaseInputSnapshot = true;
                state.LastKeys = keys;
                state.LastYaw = yaw;
                state.LastPitch = pitch;
                stateChange = hasActivity
                    ? MarkActivityStateUnsafe(playerId, state)
                    : null;
            }
            PublishStateChange(stateChange);
        }

        private void HandleWalkingInput(Player player, byte analog)
        {
            var playerId = player.channel.owner.playerID.steamID.m_SteamID;
            if (!TryGetOrCreateState(player, out var state))
            {
                return;
            }

            PlayerAwayStateChangedEventArgs? stateChange;
            lock (state.SyncRoot)
            {
                var hasActivity = analog != NeutralWalkingAnalog ||
                                  (state.HasWalkingInputSnapshot && analog != state.LastWalkingAnalog);
                state.HasWalkingInputSnapshot = true;
                state.LastWalkingAnalog = analog;
                stateChange = hasActivity
                    ? MarkActivityStateUnsafe(playerId, state)
                    : null;
            }
            PublishStateChange(stateChange);
        }

        private void HandleDrivingInput(Player player, float steeringInput, float velocityInput)
        {
            if (Mathf.Abs(steeringInput) < DrivingInputThreshold &&
                Mathf.Abs(velocityInput) < DrivingInputThreshold)
            {
                return;
            }

            MarkActivity(player.channel.owner.playerID.steamID.m_SteamID);
        }

        private void HandleInventoryVisibilityRequested(PlayerAnimator animator, bool isInventoryOpen)
        {
            if (animator?.player?.channel?.owner != null)
            {
                MarkActivity(animator.player.channel.owner.playerID.steamID.m_SteamID);
            }
        }

        private async UniTaskVoid MonitorAwayPlayersAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(1),
                        cancellationToken: cancellationToken);

                    List<PlayerAwayStateChangedEventArgs> stateChanges =
                        new List<PlayerAwayStateChangedEventArgs>();
                    var now = GetMonotonicSeconds();
                    foreach (var pair in m_States)
                    {
                        var state = pair.Value;
                        lock (state.SyncRoot)
                        {
                            if (state.IsAway ||
                                now - state.LastActivitySeconds < m_AwayAfterSeconds)
                            {
                                continue;
                            }

                            state.IsAway = true;
                            stateChanges.Add(new PlayerAwayStateChangedEventArgs(
                                pair.Key,
                                state.PlayerName,
                                true));
                        }
                    }

                    foreach (var stateChange in stateChanges)
                    {
                        PublishStateChange(stateChange);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do monitor ou do plugin.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "O detector AFK foi interrompido.");
            }
        }

        private static PlayerAwayStateChangedEventArgs? MarkActivityStateUnsafe(
            ulong playerId,
            PlayerActivityState state)
        {
            state.LastActivitySeconds = GetMonotonicSeconds();
            if (!state.IsAway)
            {
                return null;
            }

            state.IsAway = false;
            return new PlayerAwayStateChangedEventArgs(playerId, state.PlayerName, false);
        }

        private bool TryGetOrCreateState(ulong playerId, out PlayerActivityState state)
        {
            if (m_States.TryGetValue(playerId, out state!))
            {
                return true;
            }

            var player = PlayerTool.getPlayer(new Steamworks.CSteamID(playerId));
            return TryGetOrCreateState(player, out state!);
        }

        private bool TryGetOrCreateState(Player? player, out PlayerActivityState state)
        {
            if (player?.channel?.owner == null)
            {
                state = null!;
                return false;
            }

            var owner = player.channel.owner;
            var playerId = owner.playerID.steamID.m_SteamID;
            if (!m_States.TryGetValue(playerId, out state!))
            {
                state = new PlayerActivityState(
                    player,
                    owner.playerID.characterName,
                    GetMonotonicSeconds());
                state = m_States.GetOrAdd(
                    playerId,
                    _ => new PlayerActivityState(
                        player,
                        owner.playerID.characterName,
                        GetMonotonicSeconds()));
            }

            return true;
        }

        private void PublishStateChange(PlayerAwayStateChangedEventArgs? stateChange)
        {
            if (stateChange == null)
            {
                return;
            }

            if (stateChange.IsAway)
            {
                m_Logger.LogInformation(
                    "Jogador {Player} [{PlayerId}] ficou AFK apos {Seconds} segundos sem atividade.",
                    stateChange.PlayerName,
                    stateChange.PlayerId,
                    m_AwayAfterSeconds);
            }
            else
            {
                m_Logger.LogInformation(
                    "Jogador {Player} [{PlayerId}] retornou do AFK.",
                    stateChange.PlayerName,
                    stateChange.PlayerId);
            }

            try
            {
                AwayStateChanged?.Invoke(this, stateChange);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao publicar a mudanca de estado AFK de {PlayerId}.",
                    stateChange.PlayerId);
            }
        }

        private static double GetMonotonicSeconds()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private sealed class PlayerActivityState
        {
            public PlayerActivityState(Player player, string playerName, double lastActivitySeconds)
            {
                Player = player;
                PlayerName = playerName;
                LastActivitySeconds = lastActivitySeconds;
                LastWalkingAnalog = NeutralWalkingAnalog;
            }

            public Player Player { get; }

            public object SyncRoot { get; } = new object();

            public string PlayerName { get; }

            public double LastActivitySeconds { get; set; }

            public bool IsAway { get; set; }

            public bool HasBaseInputSnapshot { get; set; }

            public ushort LastKeys { get; set; }

            public float LastYaw { get; set; }

            public float LastPitch { get; set; }

            public bool HasWalkingInputSnapshot { get; set; }

            public byte LastWalkingAnalog { get; set; }
        }
    }
}
