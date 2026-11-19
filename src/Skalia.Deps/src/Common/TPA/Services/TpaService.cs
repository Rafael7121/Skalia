using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;
using SDG.Unturned;
using Steamworks;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class TpaService : ITpaService, IDisposable
    {
        private readonly object m_Sync = new object();
        private readonly Dictionary<Guid, TpaRequest> m_RequestsById = new Dictionary<Guid, TpaRequest>();
        private readonly Dictionary<ulong, TpaRequest> m_OutgoingBySender = new Dictionary<ulong, TpaRequest>();
        private readonly Dictionary<ulong, List<TpaRequest>> m_IncomingByReceiver =
            new Dictionary<ulong, List<TpaRequest>>();
        private readonly Dictionary<ulong, TpaRequest> m_WarmingByReceiver = new Dictionary<ulong, TpaRequest>();
        private readonly Dictionary<ulong, DateTimeOffset> m_CombatUntil = new Dictionary<ulong, DateTimeOffset>();
        private readonly Dictionary<ulong, Dictionary<ulong, UnturnedPlayer>> m_AfkReturnWatchers =
            new Dictionary<ulong, Dictionary<ulong, UnturnedPlayer>>();
        private readonly CancellationTokenSource m_Shutdown = new CancellationTokenSource();
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<TpaService> m_Logger;
        private readonly IPlayerActivityService m_PlayerActivityService;
        private readonly IPlayerStatsFileStore m_StatsStore;
        private readonly string m_ServerId;
        private readonly int m_RequestTimeoutSeconds;
        private readonly int m_TeleportDelaySeconds;
        private readonly int m_CombatDurationSeconds;
        private bool m_Disposed;

        public TpaService(
            IConfiguration configuration,
            IStringLocalizer stringLocalizer,
            ILogger<TpaService> logger,
            IPlayerActivityService playerActivityService,
            IPlayerStatsFileStore statsStore)
        {
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_PlayerActivityService = playerActivityService;
            m_StatsStore = statsStore;
            m_ServerId = configuration.GetValue<string>("serverId") ?? "default";
            m_PlayerActivityService.AwayStateChanged += HandleAwayStateChanged;
            m_RequestTimeoutSeconds = Clamp(configuration.GetValue("tpa:requestTimeoutSeconds", 60), 10, 600);
            m_TeleportDelaySeconds = Clamp(configuration.GetValue("tpa:teleportDelaySeconds", 5), 0, 60);
            m_CombatDurationSeconds = Clamp(configuration.GetValue("tpa:combatDurationSeconds", 15), 1, 300);
        }

        public async Task SendAsync(UnturnedPlayer sender, UnturnedPlayer receiver)
        {
            var senderId = sender.SteamId.m_SteamID;
            var receiverId = receiver.SteamId.m_SteamID;
            string? failureKey = null;
            object? failureArguments = null;
            TpaRequest? request = null;
            var receiverWasAway = false;

            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                if (senderId == receiverId)
                {
                    failureKey = "messages:tpa:self_request";
                }
                else if (!sender.IsAlive)
                {
                    failureKey = "messages:tpa:must_be_alive";
                }
                else if (!receiver.IsAlive)
                {
                    failureKey = "messages:tpa:target_dead";
                    failureArguments = new { Player = GetName(receiver) };
                }
                else if (GetCombatSecondsRemainingUnsafe(senderId) > 0)
                {
                    failureKey = "messages:tpa:sender_in_combat";
                    failureArguments = new { Seconds = GetCombatSecondsRemainingUnsafe(senderId) };
                }
                else if (GetCombatSecondsRemainingUnsafe(receiverId) > 0)
                {
                    failureKey = "messages:tpa:target_in_combat";
                    failureArguments = new { Player = GetName(receiver) };
                }
                else if (m_OutgoingBySender.ContainsKey(senderId) || m_WarmingByReceiver.ContainsKey(senderId))
                {
                    failureKey = "messages:tpa:already_in_request";
                }
                else
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(m_Shutdown.Token);
                    request = new TpaRequest(sender, receiver, cancellation);
                    m_RequestsById.Add(request.Id, request);
                    m_OutgoingBySender.Add(senderId, request);
                    if (!m_IncomingByReceiver.TryGetValue(receiverId, out var incoming))
                    {
                        incoming = new List<TpaRequest>();
                        m_IncomingByReceiver.Add(receiverId, incoming);
                    }

                    incoming.Add(request);
                    receiverWasAway = m_PlayerActivityService.IsAway(receiverId);
                    if (receiverWasAway)
                    {
                        RegisterAfkReturnWatcherUnsafe(receiverId, senderId, sender);
                    }
                }
            }

            if (failureKey != null)
            {
                await TellAsync(sender, failureKey, failureArguments);
                return;
            }

            if (request == null)
            {
                return;
            }

            await RecordTpaSentAsync(sender);

            var receiverVehicle = VehiclePassengerTeleporter.GetVehicle(receiver);
            if (receiverWasAway)
            {
                await TellAsync(sender, "messages:tpa:request_sent_afk", new
                {
                    Player = GetName(receiver),
                    Seconds = m_RequestTimeoutSeconds
                });
            }
            else if (receiverVehicle != null)
            {
                if (VehiclePassengerTeleporter.TryGetAvailableSeat(receiverVehicle, out _))
                {
                    await TellAsync(sender, "messages:tpa:request_sent_vehicle", new
                    {
                        Player = GetName(receiver),
                        Seconds = m_RequestTimeoutSeconds
                    });
                }
                else
                {
                    await TellAsync(sender, "messages:tpa:request_sent_vehicle_no_seat", new
                    {
                        Player = GetName(receiver),
                        Seconds = m_RequestTimeoutSeconds
                    });
                }
            }
            else
            {
                await TellAsync(sender, "messages:tpa:request_sent", new
                {
                    Player = GetName(receiver),
                    Seconds = m_RequestTimeoutSeconds
                });
            }

            if (receiverVehicle != null)
            {
                var receiverKey = VehiclePassengerTeleporter.TryGetAvailableSeat(receiverVehicle, out _)
                    ? "messages:tpa:request_received_vehicle"
                    : "messages:tpa:request_received_vehicle_no_seat";
                await TellAsync(receiver, receiverKey, new
                {
                    Player = GetName(sender),
                    Seconds = m_RequestTimeoutSeconds
                });
            }
            else
            {
                await TellAsync(receiver, "messages:tpa:request_received", new
                {
                    Player = GetName(sender),
                    Seconds = m_RequestTimeoutSeconds
                });
            }

            await TellPendingRequestCountAsync(receiver);
            _ = ExpireRequestAsync(request);
        }

        public async Task AcceptAsync(UnturnedPlayer receiver, UnturnedPlayer? requestedSender)
        {
            var receiverId = receiver.SteamId.m_SteamID;
            TpaRequest? request = null;
            string? failureKey = null;
            object? failureArguments = null;
            var cancelledForCombat = false;
            var blockedByFullVehicle = false;
            var acceptedInVehicle = false;
            var requestWasRemoved = false;

            lock (m_Sync)
            {
                request = SelectPendingIncomingUnsafe(
                    receiverId,
                    requestedSender,
                    out failureKey,
                    out failureArguments);

                if (request != null && m_WarmingByReceiver.ContainsKey(receiverId))
                {
                    request = null;
                    failureKey = "messages:tpa:already_accepting_request";
                }
                else if (request != null && m_OutgoingBySender.ContainsKey(receiverId))
                {
                    request = null;
                    failureKey = "messages:tpa:outgoing_blocks_accept";
                }
                else if (request != null)
                {
                    var senderCombat = GetCombatSecondsRemainingUnsafe(request.SenderId);
                    var receiverCombat = GetCombatSecondsRemainingUnsafe(request.ReceiverId);

                    if (senderCombat > 0 || receiverCombat > 0)
                    {
                        cancelledForCombat = true;
                        RemoveRequestUnsafe(request);
                        requestWasRemoved = true;
                        failureKey = "messages:tpa:combat_cancelled";
                    }
                    else if (!request.Sender.IsAlive || !request.Receiver.IsAlive)
                    {
                        RemoveRequestUnsafe(request);
                        requestWasRemoved = true;
                        failureKey = "messages:tpa:player_not_alive_cancelled";
                    }
                    else
                    {
                        var receiverVehicle = VehiclePassengerTeleporter.GetVehicle(request.Receiver);
                        if (receiverVehicle != null &&
                            !VehiclePassengerTeleporter.TryGetAvailableSeat(receiverVehicle, out _))
                        {
                            // O pedido continua pendente: o destinatário pode sair do
                            // veículo e aceitar novamente antes do prazo expirar.
                            blockedByFullVehicle = true;
                        }
                        else
                        {
                            acceptedInVehicle = receiverVehicle != null;
                            request.State = TpaRequestState.WarmingUp;
                            m_WarmingByReceiver.Add(receiverId, request);
                            ReplaceDelayCancellationUnsafe(request);
                        }
                    }
                }
            }

            if (blockedByFullVehicle && request != null)
            {
                var (pendingCount, pendingPlayers) = GetPendingRequestSummary(receiver);
                await TellAsync(receiver, "messages:tpa:accept_vehicle_no_seat_receiver", new
                {
                    Player = GetName(request.Sender),
                    Count = pendingCount,
                    Players = pendingPlayers
                });
                await TellAsync(request.Sender, "messages:tpa:accept_vehicle_no_seat_sender", new
                {
                    Player = GetName(receiver)
                });
                return;
            }

            if (failureKey != null)
            {
                await TellAsync(receiver, failureKey, failureArguments);
                if (cancelledForCombat && request != null)
                {
                    await TellAsync(request.Sender, "messages:tpa:combat_cancelled");
                }

                if (requestWasRemoved)
                {
                    await TellPendingRequestCountAsync(receiver);
                }

                return;
            }

            if (request == null)
            {
                return;
            }

            await RecordTpaAcceptedAsync(receiver);

            var (remainingCount, remainingPlayers) = GetPendingRequestSummary(receiver);
            if (acceptedInVehicle)
            {
                var receiverKey = remainingCount == 0
                    ? "messages:tpa:request_accepted_receiver_vehicle_empty"
                    : "messages:tpa:request_accepted_receiver_vehicle_remaining";
                await TellAsync(receiver, receiverKey, new
                {
                    Player = GetName(request.Sender),
                    Count = remainingCount,
                    Players = remainingPlayers
                });
                await TellAsync(request.Sender, "messages:tpa:request_accepted_sender_vehicle", new
                {
                    Player = GetName(receiver),
                    Seconds = m_TeleportDelaySeconds
                });
            }
            else
            {
                var receiverKey = remainingCount == 0
                    ? "messages:tpa:request_accepted_receiver_empty"
                    : "messages:tpa:request_accepted_receiver_remaining";
                await TellAsync(receiver, receiverKey, new
                {
                    Player = GetName(request.Sender),
                    Count = remainingCount,
                    Players = remainingPlayers
                });
                await TellAsync(request.Sender, "messages:tpa:request_accepted_sender", new
                {
                    Player = GetName(receiver),
                    Seconds = m_TeleportDelaySeconds
                });
            }

            await CompleteTeleportAsync(request);
        }

        private async Task RecordTpaSentAsync(UnturnedPlayer player)
        {
            try
            {
                await m_StatsStore.ApplyDeltaAsync(
                    player.SteamId.m_SteamID,
                    GetName(player),
                    m_ServerId,
                    tpaRequestsSent: 1);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Nao foi possivel registrar o TPA enviado por {SteamId}.",
                    player.SteamId.m_SteamID);
            }
        }

        private async Task RecordTpaAcceptedAsync(UnturnedPlayer player)
        {
            try
            {
                await m_StatsStore.ApplyDeltaAsync(
                    player.SteamId.m_SteamID,
                    GetName(player),
                    m_ServerId,
                    tpaRequestsAccepted: 1);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Nao foi possivel registrar o TPA aceito por {SteamId}.",
                    player.SteamId.m_SteamID);
            }
        }

        public async Task DeclineAsync(UnturnedPlayer receiver, UnturnedPlayer? requestedSender)
        {
            var receiverId = receiver.SteamId.m_SteamID;
            TpaRequest? request;
            string? failureKey;
            object? failureArguments;

            lock (m_Sync)
            {
                request = SelectPendingIncomingUnsafe(
                    receiverId,
                    requestedSender,
                    out failureKey,
                    out failureArguments);
                if (request != null)
                {
                    RemoveRequestUnsafe(request);
                }
            }

            if (request == null)
            {
                await TellAsync(receiver, failureKey ?? "messages:tpa:no_incoming_request", failureArguments);
                return;
            }

            await TellAsync(receiver, "messages:tpa:request_declined_receiver", new { Player = GetName(request.Sender) });
            await TellAsync(request.Sender, "messages:tpa:request_declined_sender", new { Player = GetName(receiver) });
            await TellPendingRequestCountAsync(receiver);
        }

        public async Task CancelAsync(UnturnedPlayer player)
        {
            var playerId = player.SteamId.m_SteamID;
            TpaRequest? request;

            lock (m_Sync)
            {
                if (!m_OutgoingBySender.TryGetValue(playerId, out request))
                {
                    m_WarmingByReceiver.TryGetValue(playerId, out request);
                }

                if (request != null)
                {
                    RemoveRequestUnsafe(request);
                }
            }

            if (request == null)
            {
                await TellAsync(player, "messages:tpa:no_active_request");
                return;
            }

            var other = request.SenderId == playerId ? request.Receiver : request.Sender;
            await TellAsync(player, "messages:tpa:request_cancelled_self");
            await TellAsync(other, "messages:tpa:request_cancelled_other", new { Player = GetName(player) });
            await TellPendingRequestCountAsync(request.Receiver);
        }

        public async Task RegisterCombatAsync(UnturnedPlayer victim, CSteamID attacker)
        {
            var victimId = victim.SteamId.m_SteamID;
            var attackerId = attacker.m_SteamID;
            if (attackerId == 0 || attackerId == victimId)
            {
                return;
            }

            // No golpe fatal, o Unturned publica o evento de morte antes do evento
            // de dano. Revalidar a vida aqui impede que o dano atrasado reinsira o
            // jogador morto no combate depois de HandleDeathAsync tê-lo removido.
            var victimIsAlive = victim.IsAlive;
            var attackerPlayer = PlayerTool.getPlayer(attacker);
            var attackerIsAlive = attackerPlayer != null && !attackerPlayer.life.isDead;

            List<TpaRequest> cancelledRequests;
            lock (m_Sync)
            {
                var combatEndsAt = DateTimeOffset.UtcNow.AddSeconds(m_CombatDurationSeconds);
                if (victimIsAlive)
                {
                    m_CombatUntil[victimId] = combatEndsAt;
                }
                else
                {
                    m_CombatUntil.Remove(victimId);
                }

                if (attackerIsAlive)
                {
                    m_CombatUntil[attackerId] = combatEndsAt;
                }
                else
                {
                    m_CombatUntil.Remove(attackerId);
                }

                cancelledRequests = m_RequestsById.Values
                    .Where(request =>
                        request.SenderId == victimId ||
                        request.ReceiverId == victimId ||
                        request.SenderId == attackerId ||
                        request.ReceiverId == attackerId)
                    .ToList();

                foreach (var request in cancelledRequests)
                {
                    RemoveRequestUnsafe(request);
                }
            }

            foreach (var participant in cancelledRequests
                         .SelectMany(request => new[] { request.Sender, request.Receiver })
                         .GroupBy(player => player.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellAsync(participant, "messages:tpa:combat_cancelled");
            }

            foreach (var receiver in cancelledRequests
                         .Select(request => request.Receiver)
                         .GroupBy(player => player.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellPendingRequestCountAsync(receiver);
            }
        }

        public int GetCombatSecondsRemaining(UnturnedPlayer player)
        {
            lock (m_Sync)
            {
                return GetCombatSecondsRemainingUnsafe(player.SteamId.m_SteamID);
            }
        }

        public async Task HandleDeathAsync(UnturnedPlayer player)
        {
            var playerId = player.SteamId.m_SteamID;
            List<TpaRequest> cancelledRequests;

            lock (m_Sync)
            {
                m_CombatUntil.Remove(playerId);
                cancelledRequests = m_RequestsById.Values
                    .Where(request => request.SenderId == playerId || request.ReceiverId == playerId)
                    .ToList();
                foreach (var request in cancelledRequests)
                {
                    RemoveRequestUnsafe(request);
                }
            }

            if (cancelledRequests.Count == 0)
            {
                return;
            }

            foreach (var participant in cancelledRequests
                         .SelectMany(request => new[] { request.Sender, request.Receiver })
                         .GroupBy(current => current.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellAsync(participant, "messages:tpa:player_not_alive_cancelled");
            }

            foreach (var receiver in cancelledRequests
                         .Select(request => request.Receiver)
                         .GroupBy(current => current.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellPendingRequestCountAsync(receiver);
            }
        }

        public async Task HandleDisconnectAsync(UnturnedPlayer player)
        {
            var playerId = player.SteamId.m_SteamID;
            List<TpaRequest> cancelledRequests;

            lock (m_Sync)
            {
                m_CombatUntil.Remove(playerId);
                RemoveAfkReturnWatchersUnsafe(playerId);
                cancelledRequests = m_RequestsById.Values
                    .Where(request => request.SenderId == playerId || request.ReceiverId == playerId)
                    .ToList();
                foreach (var request in cancelledRequests)
                {
                    RemoveRequestUnsafe(request);
                }
            }

            if (cancelledRequests.Count == 0)
            {
                return;
            }

            foreach (var other in cancelledRequests
                         .Select(request => request.SenderId == playerId ? request.Receiver : request.Sender)
                         .Where(current => current.SteamId.m_SteamID != playerId)
                         .GroupBy(current => current.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellAsync(other, "messages:tpa:disconnect_cancelled", new { Player = GetName(player) });
            }

            foreach (var receiver in cancelledRequests
                         .Select(request => request.Receiver)
                         .Where(current => current.SteamId.m_SteamID != playerId)
                         .GroupBy(current => current.SteamId.m_SteamID)
                         .Select(group => group.First()))
            {
                await TellPendingRequestCountAsync(receiver);
            }
        }

        public void Dispose()
        {
            lock (m_Sync)
            {
                if (m_Disposed)
                {
                    return;
                }

                m_Disposed = true;
                m_PlayerActivityService.AwayStateChanged -= HandleAwayStateChanged;
                m_Shutdown.Cancel();
                foreach (var request in m_RequestsById.Values)
                {
                    CancelAndDispose(request.DelayCancellation);
                }

                m_RequestsById.Clear();
                m_OutgoingBySender.Clear();
                m_IncomingByReceiver.Clear();
                m_WarmingByReceiver.Clear();
                m_CombatUntil.Clear();
                m_AfkReturnWatchers.Clear();
            }

            m_Shutdown.Dispose();
        }

        private void HandleAwayStateChanged(
            object? sender,
            PlayerAwayStateChangedEventArgs eventArgs)
        {
            if (!eventArgs.IsAway)
            {
                NotifyAfkReturnWatchersAsync(eventArgs).Forget();
            }
        }

        private async UniTaskVoid NotifyAfkReturnWatchersAsync(
            PlayerAwayStateChangedEventArgs eventArgs)
        {
            await UniTask.SwitchToMainThread();
            List<UnturnedPlayer> senders;
            lock (m_Sync)
            {
                if (m_Disposed ||
                    !m_AfkReturnWatchers.TryGetValue(eventArgs.PlayerId, out var watchers))
                {
                    return;
                }

                senders = watchers.Values.ToList();
                m_AfkReturnWatchers.Remove(eventArgs.PlayerId);
            }

            foreach (var sender in senders)
            {
                var senderId = sender.SteamId.m_SteamID;
                if (Provider.clients.All(client =>
                        client.playerID.steamID.m_SteamID != senderId))
                {
                    continue;
                }

                await TellAsync(sender, "messages:tpa:target_returned_from_afk", new
                {
                    Player = ChatNamePrefixService.StripPrefix(eventArgs.PlayerName)
                });
            }
        }

        private void RegisterAfkReturnWatcherUnsafe(
            ulong receiverId,
            ulong senderId,
            UnturnedPlayer sender)
        {
            if (!m_AfkReturnWatchers.TryGetValue(receiverId, out var watchers))
            {
                watchers = new Dictionary<ulong, UnturnedPlayer>();
                m_AfkReturnWatchers.Add(receiverId, watchers);
            }

            watchers[senderId] = sender;
        }

        private void RemoveAfkReturnWatchersUnsafe(ulong playerId)
        {
            m_AfkReturnWatchers.Remove(playerId);
            foreach (var pair in m_AfkReturnWatchers.ToArray())
            {
                pair.Value.Remove(playerId);
                if (pair.Value.Count == 0)
                {
                    m_AfkReturnWatchers.Remove(pair.Key);
                }
            }
        }

        private async Task ExpireRequestAsync(TpaRequest request)
        {
            var token = request.DelayCancellation.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(m_RequestTimeoutSeconds), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var expired = false;
            lock (m_Sync)
            {
                if (request.State == TpaRequestState.Pending && IsActiveUnsafe(request))
                {
                    expired = true;
                    RemoveRequestUnsafe(request);
                }
            }

            if (expired)
            {
                await TellAsync(request.Sender, "messages:tpa:request_expired");
                await TellAsync(request.Receiver, "messages:tpa:request_expired");
                await TellPendingRequestCountAsync(request.Receiver);
            }
        }

        private async Task CompleteTeleportAsync(TpaRequest request)
        {
            var token = request.DelayCancellation.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(m_TeleportDelaySeconds), token);

                // Task.Delay resumes on a thread-pool thread. From this point onward the
                // request reads Unity player/vehicle state and may broadcast a vehicle RPC,
                // all of which must run on Unity's game thread.
                await UniTask.SwitchToMainThread(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            string? cancellationKey = null;
            var cancelledForMissingPassengerSeat = false;
            InteractableVehicle? destinationVehicle = null;
            byte destinationSeat = byte.MaxValue;
            lock (m_Sync)
            {
                if (!IsActiveUnsafe(request) || request.State != TpaRequestState.WarmingUp)
                {
                    return;
                }

                if (GetCombatSecondsRemainingUnsafe(request.SenderId) > 0 ||
                    GetCombatSecondsRemainingUnsafe(request.ReceiverId) > 0)
                {
                    cancellationKey = "messages:tpa:combat_cancelled";
                }
                else if (!request.Sender.IsAlive || !request.Receiver.IsAlive)
                {
                    cancellationKey = "messages:tpa:player_not_alive_cancelled";
                }
                else
                {
                    destinationVehicle = VehiclePassengerTeleporter.GetVehicle(request.Receiver);
                    if (destinationVehicle != null &&
                        !VehiclePassengerTeleporter.TryGetAvailableSeat(
                            destinationVehicle,
                            out destinationSeat))
                    {
                        cancelledForMissingPassengerSeat = true;
                    }
                }

                RemoveRequestUnsafe(request);
            }

            if (cancellationKey != null)
            {
                await TellAsync(request.Sender, cancellationKey);
                await TellAsync(request.Receiver, cancellationKey);
                return;
            }

            if (cancelledForMissingPassengerSeat)
            {
                await TellAsync(request.Sender, "messages:tpa:vehicle_no_seat_cancelled_sender", new
                {
                    Player = GetName(request.Receiver)
                });
                await TellAsync(request.Receiver, "messages:tpa:vehicle_no_seat_cancelled_receiver", new
                {
                    Player = GetName(request.Sender)
                });
                return;
            }

            var teleported = false;
            try
            {
                if (destinationVehicle != null)
                {
                    teleported = VehiclePassengerTeleporter.TryPlaceInAvailableSeat(
                        request.Sender,
                        destinationVehicle,
                        destinationSeat);
                }
                else
                {
                    teleported = await request.Sender.SetPositionAsync(
                        request.Receiver.Transform.Position,
                        request.Receiver.Transform.Rotation);
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha ao teleportar {Sender} para {Receiver}.",
                    request.SenderId, request.ReceiverId);
            }

            if (teleported)
            {
                if (destinationVehicle != null)
                {
                    await TellAsync(request.Sender, "messages:tpa:vehicle_teleport_success", new
                    {
                        Player = GetName(request.Receiver)
                    });
                    await TellAsync(request.Receiver, "messages:tpa:vehicle_teleport_arrived", new
                    {
                        Player = GetName(request.Sender)
                    });
                }
                else
                {
                    await TellAsync(request.Sender, "messages:tpa:teleport_success", new
                    {
                        Player = GetName(request.Receiver)
                    });
                    await TellAsync(request.Receiver, "messages:tpa:teleport_arrived", new
                    {
                        Player = GetName(request.Sender)
                    });
                }
            }
            else
            {
                await TellAsync(request.Sender, "messages:tpa:teleport_failed");
                await TellAsync(request.Receiver, "messages:tpa:teleport_failed");
            }
        }

        private void ReplaceDelayCancellationUnsafe(TpaRequest request)
        {
            CancelAndDispose(request.DelayCancellation);
            request.DelayCancellation = CancellationTokenSource.CreateLinkedTokenSource(m_Shutdown.Token);
        }

        private void RemoveRequestUnsafe(TpaRequest request)
        {
            m_RequestsById.Remove(request.Id);

            if (m_OutgoingBySender.TryGetValue(request.SenderId, out var senderRequest) &&
                ReferenceEquals(senderRequest, request))
            {
                m_OutgoingBySender.Remove(request.SenderId);
            }

            if (m_IncomingByReceiver.TryGetValue(request.ReceiverId, out var incoming))
            {
                incoming.Remove(request);
                if (incoming.Count == 0)
                {
                    m_IncomingByReceiver.Remove(request.ReceiverId);
                }
            }

            if (m_WarmingByReceiver.TryGetValue(request.ReceiverId, out var warmingRequest) &&
                ReferenceEquals(warmingRequest, request))
            {
                m_WarmingByReceiver.Remove(request.ReceiverId);
            }

            CancelAndDispose(request.DelayCancellation);
        }

        private bool IsActiveUnsafe(TpaRequest request)
        {
            return m_RequestsById.TryGetValue(request.Id, out var active) &&
                   ReferenceEquals(active, request);
        }

        private TpaRequest? SelectPendingIncomingUnsafe(
            ulong receiverId,
            UnturnedPlayer? requestedSender,
            out string? failureKey,
            out object? failureArguments)
        {
            failureKey = null;
            failureArguments = null;

            if (!m_IncomingByReceiver.TryGetValue(receiverId, out var incoming))
            {
                failureKey = "messages:tpa:no_incoming_request";
                return null;
            }

            var pending = incoming
                .Where(request => request.State == TpaRequestState.Pending)
                .ToList();
            if (pending.Count == 0)
            {
                failureKey = "messages:tpa:no_incoming_request";
                return null;
            }

            if (requestedSender != null)
            {
                var senderId = requestedSender.SteamId.m_SteamID;
                var selected = pending.FirstOrDefault(request => request.SenderId == senderId);
                if (selected == null)
                {
                    failureKey = "messages:tpa:no_request_from_player";
                    failureArguments = new { Player = GetName(requestedSender) };
                }

                return selected;
            }

            if (pending.Count > 1)
            {
                failureKey = "messages:tpa:choose_incoming_request";
                failureArguments = new
                {
                    Count = pending.Count,
                    Players = FormatPendingSenders(pending)
                };
                return null;
            }

            return pending[0];
        }

        private async Task TellPendingRequestCountAsync(UnturnedPlayer receiver)
        {
            var (count, players) = GetPendingRequestSummary(receiver);
            if (count == 0)
            {
                await TellAsync(receiver, "messages:tpa:pending_request_count_empty");
                return;
            }

            await TellAsync(receiver, "messages:tpa:pending_request_count", new
            {
                Count = count,
                Players = players
            });
        }

        private (int Count, string Players) GetPendingRequestSummary(UnturnedPlayer receiver)
        {
            lock (m_Sync)
            {
                var pending = m_IncomingByReceiver.TryGetValue(receiver.SteamId.m_SteamID, out var incoming)
                    ? incoming.Where(request => request.State == TpaRequestState.Pending).ToList()
                    : new List<TpaRequest>();
                return (pending.Count, FormatPendingSenders(pending));
            }
        }

        private static string FormatPendingSenders(IReadOnlyCollection<TpaRequest> pending)
        {
            var duplicateNames = pending
                .GroupBy(request => GetName(request.Sender), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return string.Join(", ", pending.Select(request =>
            {
                var name = GetName(request.Sender);
                return duplicateNames.Contains(name)
                    ? $"{name} [{request.SenderId}]"
                    : name;
            }));
        }

        private int GetCombatSecondsRemainingUnsafe(ulong playerId)
        {
            if (!m_CombatUntil.TryGetValue(playerId, out var combatUntil))
            {
                return 0;
            }

            var remaining = combatUntil - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                m_CombatUntil.Remove(playerId);
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }

        private async Task TellAsync(UnturnedPlayer player, string key, object? arguments = null)
        {
            try
            {
                var message = arguments == null
                    ? m_StringLocalizer[key].Value
                    : m_StringLocalizer[key, arguments].Value;
                var prefix = m_StringLocalizer["prefix"].Value;
                await player.PrintMessageAsync($"{prefix} {message}");
            }
            catch (Exception exception)
            {
                m_Logger.LogDebug(exception, "Não foi possível enviar uma mensagem TPA ao jogador {PlayerId}.",
                    player.SteamId.m_SteamID);
            }
        }

        private static string GetName(UnturnedPlayer player)
        {
            return ChatNamePrefixService.StripPrefix(
                player.SteamPlayer.playerID.characterName);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static void CancelAndDispose(CancellationTokenSource cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            cancellation.Dispose();
        }
    }
}
