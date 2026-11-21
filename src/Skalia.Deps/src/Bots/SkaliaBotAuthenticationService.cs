using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class SkaliaBotAuthenticationService : ISkaliaBotService
    {
        private const string BotRegistryPath = "/OpenMod/plugins/Skalia.Deps/bots.json";

        private readonly ILogger<SkaliaBotAuthenticationService> m_Logger;
        private readonly IPAddress m_Address;
        private readonly ushort m_Port;
        private readonly float m_RespawnDelaySeconds;
        private readonly bool m_MovementEnabled;
        private readonly float m_AvoidPlayersWithinMeters;
        private readonly float m_TurnSpeedDegreesPerSecond;
        private readonly float m_ObstacleCheckDistanceMeters;
        private readonly float m_LootSearchRadiusMeters;
        private readonly float m_LootPickupDistanceMeters;
        private readonly float m_ObjectiveTimeoutSeconds;
        private readonly float m_ZombieDetectionRadiusMeters;
        private readonly float m_ZombieAttackDistanceMeters;

        private readonly Dictionary<ulong, BotSession> m_Bots = new Dictionary<ulong, BotSession>();
        private bool m_Running;
        private bool m_RegistryLoaded;

        private sealed class BotRegistryDocument
        {
            public List<BotRegistryEntry> Bots { get; set; } = new List<BotRegistryEntry>();
        }

        private sealed class BotRegistryEntry
        {
            public ulong SteamId { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        private sealed class BotSession
        {
            public BotSession(CSteamID steamId, string name)
            {
                SteamId = steamId;
                Name = name;
            }

            public CSteamID SteamId { get; }
            public string Name { get; }
            public SteamPlayer? SteamPlayer { get; set; }
            public SkaliaBotIdleSimulation? Simulation { get; set; }
            public bool AuthenticationScheduled { get; set; }
            public bool Standby { get; set; }
            public bool DesiredOnline { get; set; }
            public float NextDiagnosticTime { get; set; }
            public bool ReauthenticationScheduled { get; set; }
            public ESteamRejection? LastRejection { get; set; }
            public string LastRejectionExplanation { get; set; } = string.Empty;
        }

        public SkaliaBotAuthenticationService(
            IConfiguration configuration,
            ILogger<SkaliaBotAuthenticationService> logger)
        {
            // 0.1.0-y17.30: serverBot:botLogs (SkaliaBotLogFilter).
            m_Logger = configuration.GetValue("serverBot:botLogs", false)
                ? logger
                : new SkaliaBotLogFilter<SkaliaBotAuthenticationService>(logger, LogLevel.Warning);
            var configuredAddress = configuration.GetValue("serverBot:ipAddress", "127.0.0.2");
            if (!IPAddress.TryParse(configuredAddress, out var parsedAddress)
                || parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                parsedAddress = IPAddress.Parse("127.0.0.2");
                m_Logger.LogWarning(
                    "IP {ConfiguredAddress} invalido para o bot; usando {FallbackAddress}.",
                    configuredAddress,
                    parsedAddress);
            }

            m_Address = parsedAddress;
            m_Port = configuration.GetValue("serverBot:port", (ushort)27018);
            m_RespawnDelaySeconds = Math.Max(0f,
                configuration.GetValue("serverBot:respawnDelaySeconds", 3f));
            m_MovementEnabled = configuration.GetValue("serverBot:movement:enabled", true);
            m_AvoidPlayersWithinMeters = Math.Max(1f,
                configuration.GetValue("serverBot:movement:avoidPlayersWithinMeters", 30f));
            m_TurnSpeedDegreesPerSecond = Math.Max(10f,
                configuration.GetValue("serverBot:movement:turnSpeedDegreesPerSecond", 120f));
            m_ObstacleCheckDistanceMeters = Math.Max(0.75f,
                configuration.GetValue("serverBot:movement:obstacleCheckDistanceMeters", 1.75f));
            m_LootSearchRadiusMeters = Math.Max(10f,
                configuration.GetValue("serverBot:objectives:lootSearchRadiusMeters", 100f));
            m_LootPickupDistanceMeters = Math.Max(1.5f,
                configuration.GetValue("serverBot:objectives:lootPickupDistanceMeters", 3f));
            m_ObjectiveTimeoutSeconds = Math.Max(20f,
                configuration.GetValue("serverBot:objectives:timeoutSeconds", 120f));
            m_ZombieDetectionRadiusMeters = Math.Max(5f,
                configuration.GetValue("serverBot:combat:zombieDetectionRadiusMeters", 25f));
            m_ZombieAttackDistanceMeters = Math.Max(1f,
                Math.Min(1.65f,
                    configuration.GetValue("serverBot:combat:attackDistanceMeters", 1.45f)));
            SkaliaBotIdleSimulation.s_Settings = SkaliaBotBehaviorSettings.Load(configuration, m_Logger);
        }

        public void PrepareMaps()
        {
            SkaliaBotIdleSimulation.StartMaps(m_Logger);
        }

        public void Start()
        {
            if (m_Running)
            {
                return;
            }

            m_Running = true;
            SkaliaBotLifecycleDiagnostics.Register(m_Logger);
            Provider.onServerDisconnected += OnServerDisconnected;
            Provider.onRejectingPlayer += OnRejectingPlayer;
            PlayerLife.onPlayerDied += OnPlayerDied;
            SkaliaBotIdleSimulation.HookHearing();

            // Um reload global descarta o EventBus antes de descarregar os plugins.
            // A conexao antiga e mantida no unload para nao gerar eventos de
            // inventario contra o container descartado e removida somente agora,
            // quando o novo EventBus ja esta disponivel.
            KickConnectedBots();
            LoadRegisteredBotsWhenReadyAsync().Forget();
        }

        private async UniTaskVoid LoadRegisteredBotsWhenReadyAsync()
        {
            // OpenMod loads plugins before the level, when Provider.isServer is
            // still false. Loading then silently found nothing, and the next
            // /addbot overwrote bots.json with a single entry.
            while (m_Running && !m_RegistryLoaded && (!Provider.isServer || !Level.isLoaded))
            {
                await UniTask.Delay(TimeSpan.FromSeconds(1));
                await UniTask.SwitchToMainThread();
            }
            if (m_Running && !m_RegistryLoaded)
            {
                LoadRegisteredBots();
            }
        }

        public async UniTask StopAsync()
        {
            if (!m_Running)
            {
                return;
            }

            m_Running = false;
            Provider.onServerDisconnected -= OnServerDisconnected;
            Provider.onRejectingPlayer -= OnRejectingPlayer;
            PlayerLife.onPlayerDied -= OnPlayerDied;
            SkaliaBotIdleSimulation.UnhookHearing();

            await UniTask.SwitchToMainThread();
            foreach (var session in m_Bots.Values)
            {
                session.DesiredOnline = false;
                session.Simulation?.Stop();
                session.Simulation = null;
                session.SteamPlayer = null;
                CancelPendingAuthentication(session);
            }
            // Nao expulse bots durante o unload. Provider.kick processa crafting e
            // inventario, mas no `openmod reload` o LifetimeScope do EventBus ja foi
            // descartado. A proxima instancia remove estas conexoes em Start().
            SkaliaBotLifecycleDiagnostics.Unregister();
        }

        public async UniTask<int> KickAllAsync()
        {
            if (!m_Running)
            {
                return 0;
            }

            await UniTask.SwitchToMainThread();
            foreach (var session in m_Bots.Values)
            {
                session.DesiredOnline = false;
            }
            var count = KickConnectedBots();
            foreach (var session in m_Bots.Values)
            {
                CancelPendingAuthentication(session);
            }
            return count;
        }

        public async UniTask<int> CallAsync(string selector)
        {
            if (!m_Running || !Provider.isServer || !Level.isLoaded)
            {
                return 0;
            }

            await UniTask.SwitchToMainThread();
            var callAll = string.Equals(selector?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
            var selected = m_Bots.Values
                .Where(session => callAll || string.Equals(
                    session.Name,
                    selector?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            // Each Steam ticket can take up to the sidecar timeout; authenticate the
            // selected bots concurrently instead of one after another.
            var results = await UniTask.WhenAll(selected.Select(CallSessionAsync));
            return results.Count(called => called);
        }

        private async UniTask<bool> CallSessionAsync(BotSession session)
        {
            session.DesiredOnline = true;
            if (FindAuthenticatedBot(session) != null || session.AuthenticationScheduled)
            {
                return false;
            }

            session.AuthenticationScheduled = true;
            try
            {
                if (!await AuthenticateAsync(session))
                {
                    return false;
                }
                MaintainSessionAsync(session).Forget();
                return true;
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao chamar bot {BotName} ({SteamId}).",
                    session.Name,
                    session.SteamId.m_SteamID);
                return false;
            }
            finally
            {
                session.AuthenticationScheduled = false;
            }
        }

        public async UniTask<int> PlayAsync(string selector)
        {
            await UniTask.SwitchToMainThread();
            var playAll = string.Equals(selector?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
            var count = 0;
            var selected = m_Bots.Values.Where(session =>
                playAll || string.Equals(
                    session.Name,
                    selector?.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            foreach (var session in selected)
            {
                if (FindAuthenticatedBot(session) == null || session.Simulation == null)
                {
                    continue;
                }

                session.Standby = false;
                session.Simulation.SetStandby(false);
                count++;
            }
            return count;
        }

        public async UniTask<int> SetStandbyAllAsync()
        {
            return await SetStandbyAsync("all");
        }

        public async UniTask<int> SetStandbyAsync(string selector)
        {
            await UniTask.SwitchToMainThread();
            var callAll = string.Equals(selector?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
            var count = 0;
            var selected = m_Bots.Values.Where(session =>
                callAll || string.Equals(
                    session.Name,
                    selector?.Trim(),
                    StringComparison.OrdinalIgnoreCase));
            foreach (var session in selected)
            {
                session.Standby = true;
                if (session.Simulation != null)
                {
                    session.Simulation.SetStandby(true);
                    count++;
                }
            }
            return count;
        }

        public async UniTask<bool> AddBotAsync(ulong steamId, string name)
        {
            if (!m_Running || steamId == 0)
            {
                return false;
            }

            await UniTask.SwitchToMainThread();
            if (!m_RegistryLoaded)
            {
                LoadRegisteredBots();
            }
            if (m_Bots.ContainsKey(steamId)
                || m_Bots.Values.Any(session => string.Equals(
                    session.Name,
                    NormalizeName(name),
                    StringComparison.OrdinalIgnoreCase))
                || Provider.clients.Any(player => player.playerID.steamID.m_SteamID == steamId)
                || Provider.pending.Any(player => player.playerID.steamID.m_SteamID == steamId))
            {
                return false;
            }

            var session = new BotSession(new CSteamID(steamId), NormalizeName(name));
            session.DesiredOnline = true;
            m_Bots.Add(steamId, session);
            SkaliaBotLifecycleDiagnostics.RegisterBot(session.SteamId);
            try
            {
                if (!await AuthenticateAsync(session))
                {
                    m_Bots.Remove(steamId);
                    return false;
                }
                MaintainSessionAsync(session).Forget();
                SaveRegisteredBots();
                return true;
            }
            catch (Exception exception)
            {
                m_Bots.Remove(steamId);
                m_Logger.LogError(exception, "Falha ao adicionar bot {BotName} ({SteamId}).", session.Name, steamId);
                return false;
            }
        }

        public async UniTask<bool> SendChatAsync(string botName, string message)
        {
            if (!m_Running || !Provider.isServer || string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            await UniTask.SwitchToMainThread();
            var session = m_Bots.Values.FirstOrDefault(value =>
                string.Equals(value.Name, botName?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return false;
            }

            var steamPlayer = FindAuthenticatedBot(session);
            if (steamPlayer == null)
            {
                return false;
            }

            var normalizedMessage = message.Trim();
            var maximumMessageLength = Math.Max(1, 90 - session.Name.Length - 2);
            if (normalizedMessage.Length > maximumMessageLength)
            {
                normalizedMessage = normalizedMessage.Substring(0, maximumMessageLength);
            }

            ChatManager.serverSendMessage(
                $"{session.Name}: {normalizedMessage}",
                Color.white,
                steamPlayer,
                null,
                EChatMode.GLOBAL,
                null,
                false);
            m_Logger.LogInformation(
                "Mensagem enviada pelo bot {BotName} ({SteamId}) no chat global.",
                session.Name,
                session.SteamId.m_SteamID);
            return true;
        }

        public async UniTask<BotNavigationSnapshot?> GetNavigationSnapshotAsync(string? botName)
        {
            await UniTask.SwitchToMainThread();
            var simulation = m_Bots.Values
                .Where(session => string.IsNullOrWhiteSpace(botName)
                    || string.Equals(session.Name, botName.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(session => session.Simulation)
                .FirstOrDefault(value => value != null);
            return m_Running && simulation != null
                ? simulation.GetNavigationSnapshot()
                : null;
        }

        private int KickConnectedBots()
        {
            var bots = Provider.clients
                .Where(player => player.transportConnection is SkaliaBotTransportConnection)
                .ToArray();
            if (bots.Length == 0)
            {
                return 0;
            }

            // During complete server shutdown OpenMod can dispose plugins after
            // Unturned has already left server mode. Chat and kick APIs throw in
            // that state. A normal plugin reload remains in server mode and keeps
            // the same leave message as real players.
            if (!Provider.isServer)
            {
                return bots.Length;
            }

            foreach (var bot in bots)
            {
                ChatManager.serverSendMessage(
                    text: $"<color=#B8860B>{bot.playerID.characterName}</color> saiu do servidor.",
                    color: Color.yellow,
                    useRichTextFormatting: true,
                    iconURL: Provider.configData.Browser.Icon);
            }

            foreach (var bot in bots)
            {
                if (Provider.clients.Contains(bot))
                {
                    Provider.kick(bot.playerID.steamID, "Connection closed");
                }
            }

            return bots.Length;
        }

        private void OnServerDisconnected(CSteamID steamId)
        {
            if (m_Bots.TryGetValue(steamId.m_SteamID, out var session))
            {
                m_Logger.LogWarning(
                    "Provider.onServerDisconnected foi disparado para o bot; presente na lista {Present}; clientes {Count}.",
                    Provider.clients.Any(player => player.playerID.steamID == steamId),
                    Provider.clients.Count);
                session.Simulation?.Stop();
                session.Simulation = null;
                session.SteamPlayer = null;
            }
        }

        private void OnRejectingPlayer(
            CSteamID steamId,
            ESteamRejection rejection,
            string explanation)
        {
            if (!m_Bots.TryGetValue(steamId.m_SteamID, out var session))
            {
                return;
            }

            session.LastRejection = rejection;
            session.LastRejectionExplanation = explanation ?? string.Empty;
            m_Logger.LogWarning(
                "Steam/Provider rejeitou o bot {BotName} ({SteamId}): {Rejection}; {Explanation}.",
                session.Name,
                steamId.m_SteamID,
                rejection,
                string.IsNullOrWhiteSpace(explanation) ? "sem explicacao adicional" : explanation);
        }

        private void OnPlayerDied(
            PlayerLife sender,
            EDeathCause cause,
            ELimb limb,
            CSteamID instigator)
        {
            // 0.2.2y11: a man (or a bot) killed by a bot: the killer goes
            // through what he left (SkaliaBotPvpKillLootY).
            try
            {
                if (m_Running
                    && instigator != CSteamID.Nil
                    && sender.player != null
                    && sender.player.channel?.owner != null
                    && sender.player.channel.owner.playerID.steamID != instigator
                    && m_Bots.TryGetValue(instigator.m_SteamID, out var killer))
                {
                    killer.Simulation?.NotePvpKill(
                        sender.player.transform.position,
                        sender.player.channel.owner.playerID.characterName);
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogWarning("Falha ao avisar o bot do abate: {Error}", exception.Message);
            }
            if (!m_Running
                || sender.player == null
                || sender.player.channel == null
                || sender.player.channel.owner == null
                || !m_Bots.TryGetValue(sender.player.channel.owner.playerID.steamID.m_SteamID, out var session))
            {
                return;
            }

            m_Logger.LogInformation(
                "Bot {BotName} morreu: causa {Cause}; membro {Limb}; atacante {Instigator}; posicao {Position}; objetivo {Objective}.",
                session.Name,
                cause,
                limb,
                instigator == CSteamID.Nil ? "nenhum" : instigator.m_SteamID.ToString(),
                sender.transform.position,
                session.Simulation?.CurrentObjective ?? "None");
            session.Simulation?.PauseForDeath();
            RespawnAfterDeathAsync(session, sender).Forget();
        }

        private async UniTaskVoid RespawnAfterDeathAsync(BotSession session, PlayerLife life)
        {
            try
            {
                if (m_RespawnDelaySeconds > 0f)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(m_RespawnDelaySeconds));
                }

                await UniTask.SwitchToMainThread();
                var steamPlayer = FindAuthenticatedBot(session);
                if (!m_Running
                    || steamPlayer == null
                    || steamPlayer.player == null
                    || steamPlayer.player.life != life
                    || life.IsAlive)
                {
                    return;
                }

                // 0.2.1: on its bed when it died far from it (SkaliaBotHome).
                life.ServerRespawn(session.Simulation?.ShouldRespawnAtHome() ?? false);
                await UniTask.DelayFrame(1, PlayerLoopTiming.FixedUpdate);
                await UniTask.SwitchToMainThread();

                steamPlayer = FindAuthenticatedBot(session);
                if (!m_Running
                    || steamPlayer == null
                    || steamPlayer.player == null
                    || steamPlayer.player.life != life
                    || life.isDead)
                {
                    return;
                }

                if (session.Simulation != null)
                {
                    session.Simulation.ResumeAfterRespawn();
                    session.Simulation.SetStandby(session.Standby);
                }
                else
                {
                    RestartIdleSimulation(session, steamPlayer.player);
                }
                if (!session.Standby)
                {
                    VerifyMovementAfterRespawnAsync(
                        session,
                        steamPlayer.player,
                        steamPlayer.player.transform.position).Forget();
                }
                m_Logger.LogInformation(
                    "Bot {BotName} renasceu automaticamente apos {DelaySeconds:0.##}s e recebeu um novo objetivo.",
                    session.Name,
                    m_RespawnDelaySeconds);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Falha ao renascer automaticamente o bot {BotName}.",
                    session.Name);
            }
        }

        private async UniTaskVoid VerifyMovementAfterRespawnAsync(
            BotSession session,
            Player player,
            Vector3 respawnPosition)
        {
            try
            {
                await UniTask.Delay(TimeSpan.FromSeconds(5));
                await UniTask.SwitchToMainThread();

                if (!m_Running
                    || session.Standby
                    || player == null
                    || player.life == null
                    || player.life.isDead
                    || FindAuthenticatedBot(session)?.player != player)
                {
                    return;
                }

                var distance = (player.transform.position - respawnPosition)
                    .GetHorizontalMagnitude();
                var verticalDelta = Mathf.Abs(
                    player.transform.position.y - respawnPosition.y);
                var simulation = session.Simulation;
                var queueCount = player.input?.serversidePackets?.Count ?? -1;
                var controllerEnabled = player.movement?.controller != null
                    && player.movement.controller.enabled;
                var grounded = simulation?.IsGrounded ?? false;
                var inWater = simulation?.IsInWater ?? false;
                var stance = simulation?.CurrentStance ?? player.stance.stance;
                var nearNavigation = simulation?.HasNearbyNavigationNode(15f) ?? false;
                var respawnPhysicsConfirmed =
                    simulation?.HasConfirmedRespawnPhysics ?? false;
                var physicalStateValid = controllerEnabled
                    && ((grounded && nearNavigation)
                        || (inWater && stance == EPlayerStance.SWIM));

                if (physicalStateValid
                    && (distance >= 0.5f || respawnPhysicsConfirmed))
                {
                    m_Logger.LogInformation(
                        "Movimento apos renascimento confirmado: {Distance:0.##}m em 5s; "
                        + "variacao vertical {VerticalDelta:0.##}m; grounded {Grounded}; "
                        + "agua {InWater}; postura {Stance}; navmesh proximo {NearNavigation}; "
                        + "tick fisico confirmado {PhysicsConfirmed}; objetivo {Objective}; "
                        + "andando {Walking}; correndo {Sprinting}.",
                        distance,
                        verticalDelta,
                        grounded,
                        inWater,
                        stance,
                        nearNavigation,
                        respawnPhysicsConfirmed,
                        simulation?.CurrentObjective ?? "None",
                        simulation?.IsWalking ?? false,
                        simulation?.IsSprinting ?? false);
                    return;
                }

                m_Logger.LogWarning(
                    "Estado pos-renascimento invalido apos 5s: deslocamento {Distance:0.##}m; "
                    + "variacao vertical {VerticalDelta:0.##}m; grounded {Grounded}; "
                    + "agua {InWater}; postura {Stance}; navmesh proximo {NearNavigation}; "
                    + "controller {ControllerEnabled}; tick fisico confirmado {PhysicsConfirmed}; "
                    + "fila {QueueCount}. Aplicando nova "
                    + "sincronizacao fisica; objetivo {Objective}; andando {Walking}; correndo {Sprinting}.",
                    distance,
                    verticalDelta,
                    grounded,
                    inWater,
                    stance,
                    nearNavigation,
                    controllerEnabled,
                    respawnPhysicsConfirmed,
                    queueCount,
                    simulation?.CurrentObjective ?? "None",
                    simulation?.IsWalking ?? false,
                    simulation?.IsSprinting ?? false);
                if (session.Simulation != null)
                {
                    session.Simulation.ResumeAfterRespawn();
                    session.Simulation.SetStandby(session.Standby);
                }
                else
                {
                    RestartIdleSimulation(session, player);
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Falha ao verificar movimento do bot apos renascimento.");
            }
        }

        private async UniTask<bool> AuthenticateAsync(BotSession session)
        {
            await UniTask.SwitchToMainThread();
            if (!m_Running || !session.DesiredOnline || !Provider.isServer || !Level.isLoaded)
            {
                return false;
            }

            if (Provider.pending.Any(pending => pending.playerID.steamID == session.SteamId))
            {
                throw new InvalidOperationException(
                    $"Steam ID {session.SteamId.m_SteamID} ja esta na fila de autenticacao.");
            }

            var hwid = CreateDeterministicHwid(session);
            var playerId = new SteamPlayerID(
                session.SteamId,
                0,
                session.Name,
                session.Name,
                session.Name,
                CSteamID.Nil,
                hwid);

            var transport = new SkaliaBotTransportConnection(
                m_Address,
                m_Port,
                session.SteamId.m_SteamID);
            // Free palette entries only, deterministically varied by SteamID.
            var appearance = CreateDeterministicAppearance(session.SteamId.m_SteamID);
            var face = (byte)(appearance[0] % Customization.FACES_FREE);
            var hair = (byte)(appearance[1] % Customization.HAIRS_FREE);
            var beard = (byte)(appearance[2] % Customization.BEARDS_FREE);
            var skinIndex = appearance[3] % Customization.SKINS.Length;
            var colorIndex = appearance[4] % Customization.COLORS.Length;
            var markerIndex = appearance[5] % Customization.MARKER_COLORS.Length;
            var skinColor = Customization.SKINS[skinIndex];
            var hairColor = Customization.COLORS[colorIndex];
            var markerColor = Customization.MARKER_COLORS[markerIndex];
            var isLeftHanded = (appearance[6] & 1) != 0;
            var pending = new SteamPending(
                transport,
                playerId,
                false,
                face,
                hair,
                beard,
                skinColor,
                hairColor,
                markerColor,
                hairColor,
                isLeftHanded,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<ulong>(),
                EPlayerSkillset.NONE,
                "English",
                CSteamID.Nil,
                EClientPlatform.Windows)
            {
                hasAuthentication = true,
                hasGroup = true,
                hasProof = true,
                shirtItem = 0,
                pantsItem = 0,
                hatItem = 0,
                backpackItem = 0,
                vestItem = 0,
                maskItem = 0,
                glassesItem = 0,
                skinItems = Array.Empty<int>(),
                skinTags = Array.Empty<string>(),
                skinDynamicProps = Array.Empty<string>()
            };

            Provider.pending.Add(pending);
            session.LastRejection = null;
            session.LastRejectionExplanation = string.Empty;
            try
            {
                Provider.accept(
                    playerId,
                    pending.assignedPro,
                    pending.assignedAdmin,
                    pending.face,
                    pending.hair,
                    pending.beard,
                    pending.skin,
                    pending.color,
                    pending.markerColor,
                    pending.BeardColor,
                    pending.hand,
                    pending.shirtItem,
                    pending.pantsItem,
                    pending.hatItem,
                    pending.backpackItem,
                    pending.vestItem,
                    pending.maskItem,
                    pending.glassesItem,
                    pending.skinItems,
                    pending.skinTags,
                    pending.skinDynamicProps,
                    pending.skillset,
                    pending.language,
                    pending.lobbyID,
                    EClientPlatform.Windows);
            }
            catch
            {
                Provider.pending.Remove(pending);
                throw;
            }

            session.SteamPlayer = FindAuthenticatedBot(session);
            if (session.SteamPlayer == null)
            {
                Provider.pending.Remove(pending);
                throw new InvalidOperationException("O servidor rejeitou o bot durante Provider.accept.");
            }

            RestartIdleSimulation(session, session.SteamPlayer.player);

            m_Logger.LogInformation(
                "Bot {BotName} autenticado no mapa {Map} com Steam ID {SteamId}; aparencia free rosto {Face}, cabelo {Hair}, barba {Beard}, pele {Skin}, cor {Color}. Caminhada autonoma: {MovementEnabled}; prioridades combate, loot e exploracao ativas; evasao de jogadores temporariamente desativada.",
                session.Name,
                Provider.map,
                session.SteamId.m_SteamID,
                face,
                hair,
                beard,
                skinIndex,
                colorIndex,
                m_MovementEnabled);
            return true;
        }

        private void RestartIdleSimulation(BotSession session, Player player)
        {
            session.Simulation?.Stop();
            session.Simulation = new SkaliaBotIdleSimulation(
                player,
                m_Logger,
                m_MovementEnabled,
                m_AvoidPlayersWithinMeters,
                m_TurnSpeedDegreesPerSecond,
                m_ObstacleCheckDistanceMeters,
                m_LootSearchRadiusMeters,
                m_LootPickupDistanceMeters,
                m_ObjectiveTimeoutSeconds,
                m_ZombieDetectionRadiusMeters,
                m_ZombieAttackDistanceMeters);
            session.Simulation.Start();
            session.Simulation.SetStandby(session.Standby);
        }

        private void CancelPendingAuthentication(BotSession session)
        {
            var pending = Provider.pending.FirstOrDefault(value =>
                value.playerID.steamID == session.SteamId);
            if (pending != null)
            {
                Provider.pending.Remove(pending);
            }
        }

        private void ScheduleReauthentication(BotSession session)
        {
            if (session.ReauthenticationScheduled
                || session.AuthenticationScheduled
                || !session.DesiredOnline
                || !m_Running)
            {
                return;
            }

            session.ReauthenticationScheduled = true;
            ReauthenticateUntilConnectedAsync(session).Forget();
        }

        private async UniTaskVoid ReauthenticateUntilConnectedAsync(BotSession session)
        {
            try
            {
                while (m_Running && session.DesiredOnline)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(5));
                    await UniTask.SwitchToMainThread();
                    if (!m_Running
                        || !session.DesiredOnline
                        || FindAuthenticatedBot(session) != null)
                    {
                        return;
                    }

                    session.AuthenticationScheduled = true;
                    try
                    {
                        await AuthenticateAsync(session);
                        if (FindAuthenticatedBot(session) != null)
                        {
                            MaintainSessionAsync(session).Forget();
                            m_Logger.LogInformation(
                                "Bot {BotName} reconectado com um novo ticket Steam.",
                                session.Name);
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        m_Logger.LogWarning(
                            exception,
                            "Falha renovando sessao Steam do bot {BotName}; nova tentativa em 5s.",
                            session.Name);
                    }
                    finally
                    {
                        session.AuthenticationScheduled = false;
                    }
                }
            }
            finally
            {
                session.ReauthenticationScheduled = false;
            }
        }

        private async UniTaskVoid MaintainSessionAsync(BotSession session)
        {
            try
            {
                while (m_Running)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(5));
                    await UniTask.SwitchToMainThread();

                    var steamPlayer = FindAuthenticatedBot(session);
                    if (steamPlayer == null)
                    {
                        m_Logger.LogWarning(
                            "Keepalive perdeu o bot {BotName} sem evento de desconexao: clientes {Count}; "
                            + "simulacao existente {HasSimulation}. Agendando reconexao.",
                            session.Name,
                            Provider.clients.Count,
                            session.Simulation != null);
                        session.Simulation?.Stop();
                        session.Simulation = null;
                        session.SteamPlayer = null;
                        if (m_Running && session.DesiredOnline)
                        {
                            ScheduleReauthentication(session);
                        }
                        return;
                    }

                    session.SteamPlayer = steamPlayer;
                    steamPlayer.timeLastPacketWasReceivedFromClient = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup >= session.NextDiagnosticTime)
                    {
                        session.NextDiagnosticTime = Time.realtimeSinceStartup + 30f;
                        m_Logger.LogDebug(
                            "Keepalive do bot confirmado: presente {Present}; ping {Ping:0.###}s; "
                            + "morto {Dead}; clientes {Count}; simulacao {Simulation}.",
                            Provider.clients.Contains(steamPlayer),
                            steamPlayer.ping,
                            steamPlayer.player?.life?.isDead,
                            Provider.clients.Count,
                            steamPlayer.player?.input?.simulation);
                    }
                }
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Keepalive do bot foi interrompido por uma excecao.");
            }
        }

        private SteamPlayer? FindAuthenticatedBot(BotSession session)
        {
            if (session.SteamPlayer != null
                && session.SteamPlayer.playerID.steamID == session.SteamId
                && Provider.clients.Contains(session.SteamPlayer))
            {
                return session.SteamPlayer;
            }

            return Provider.clients.FirstOrDefault(player => player.playerID.steamID == session.SteamId);
        }

        private static byte[] CreateDeterministicHwid(BotSession session)
        {
            using var sha1 = SHA1.Create();
            return sha1.ComputeHash(Encoding.UTF8.GetBytes($"SkaliaBot:{session.SteamId.m_SteamID}:{session.Name}"));
        }

        private void LoadRegisteredBots()
        {
            try
            {
                if (!Provider.isServer)
                {
                    return;
                }
                m_RegistryLoaded = true;
                if (!ServerSavedata.fileExists(BotRegistryPath))
                {
                    return;
                }

                var document = ServerSavedata.deserializeJSON<BotRegistryDocument>(BotRegistryPath);
                if (document?.Bots == null)
                {
                    return;
                }

                foreach (var entry in document.Bots)
                {
                    var name = NormalizeName(entry.Name);
                    if (entry.SteamId == 0
                        || m_Bots.ContainsKey(entry.SteamId)
                        || m_Bots.Values.Any(session => string.Equals(
                            session.Name,
                            name,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var session = new BotSession(new CSteamID(entry.SteamId), name);
                    m_Bots.Add(entry.SteamId, session);
                    SkaliaBotLifecycleDiagnostics.RegisterBot(session.SteamId);
                }

                m_Logger.LogInformation(
                    "Registro persistente de bots carregado: {Count} bot(s).",
                    m_Bots.Count);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha carregando registro persistente de bots.");
            }
        }

        private void SaveRegisteredBots()
        {
            if (!m_RegistryLoaded)
            {
                m_Logger.LogWarning("Registro de bots ainda nao foi carregado; bots.json nao sera sobrescrito.");
                return;
            }
            try
            {
                var document = new BotRegistryDocument
                {
                    Bots = m_Bots.Values
                        .OrderBy(session => session.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(session => new BotRegistryEntry
                        {
                            SteamId = session.SteamId.m_SteamID,
                            Name = session.Name
                        })
                        .ToList()
                };
                ServerSavedata.serializeJSON(BotRegistryPath, document);
                m_Logger.LogInformation(
                    "Registro persistente de bots salvo: {Count} bot(s).",
                    document.Bots.Count);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "Falha salvando registro persistente de bots.");
            }
        }

        private static byte[] CreateDeterministicAppearance(ulong steamId)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(Encoding.UTF8.GetBytes($"SkaliaAppearance:{steamId}"));
        }

        private static string NormalizeName(string? value)
        {
            var name = string.IsNullOrWhiteSpace(value) ? "Skalia Bot" : value.Trim();
            return name.Length <= 32 ? name : name.Substring(0, 32);
        }
    }
}
