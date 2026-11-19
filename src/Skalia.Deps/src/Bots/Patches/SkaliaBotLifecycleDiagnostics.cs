using System;
using System.Collections.Generic;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    internal static class SkaliaBotLifecycleDiagnostics
    {
        private static readonly HashSet<ulong> s_SteamIds = new HashSet<ulong>();
        private static ILogger? s_Logger;

        internal static void Register(ILogger logger)
        {
            s_SteamIds.Clear();
            s_Logger = logger;
        }

        internal static void RegisterBot(CSteamID steamId)
        {
            s_SteamIds.Add(steamId.m_SteamID);
        }

        internal static void Unregister()
        {
            s_SteamIds.Clear();
            s_Logger = null;
        }

        internal static bool IsBot(SteamPlayer? steamPlayer)
        {
            return steamPlayer != null
                && s_SteamIds.Contains(steamPlayer.playerID.steamID.m_SteamID);
        }

        internal static bool IsBot(Player? player)
        {
            return player?.channel?.owner != null && IsBot(player.channel.owner);
        }

        internal static void LogInformation(string message, params object[] arguments)
        {
            s_Logger?.LogInformation(message, arguments);
        }

        internal static void LogDebug(string message, params object[] arguments)
        {
            s_Logger?.LogDebug(message, arguments);
        }

        internal static void LogError(Exception exception, string message, params object[] arguments)
        {
            s_Logger?.LogError(exception, message, arguments);
        }
    }

    [HarmonyPatch(typeof(PlayerEquipment), "punch", new[] { typeof(EPlayerPunch) })]
    internal static class SkaliaBotPunchDiagnosticsPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            PlayerEquipment __instance,
            EPlayerPunch mode,
            out int __state)
        {
            __state = -1;
            if (!SkaliaBotLifecycleDiagnostics.IsBot(__instance.player))
            {
                return;
            }

            __state = __instance.player.input.getInputCount();
            SkaliaBotLifecycleDiagnostics.LogDebug(
                "Soco nativo iniciou no PlayerEquipment: punho {Hand}; simulacao {Simulation}; "
                + "entradas de hit antes {Inputs}; postura {Stance}; posicao {Position}.",
                mode,
                __instance.player.input.simulation,
                __state,
                __instance.player.stance.stance,
                FormatPosition(__instance.player.transform.position));
        }

        [HarmonyFinalizer]
        private static Exception? Finalizer(
            PlayerEquipment __instance,
            EPlayerPunch mode,
            int __state,
            Exception? __exception)
        {
            if (__state < 0 || !SkaliaBotLifecycleDiagnostics.IsBot(__instance.player))
            {
                return __exception;
            }

            if (__exception == null)
            {
                SkaliaBotLifecycleDiagnostics.LogDebug(
                    "Soco nativo terminou no PlayerEquipment: punho {Hand}; entradas de hit {Before}->{After}.",
                    mode,
                    __state,
                    __instance.player.input.getInputCount());
            }
            else
            {
                SkaliaBotLifecycleDiagnostics.LogError(
                    __exception,
                    "Soco nativo falhou no PlayerEquipment: punho {Hand}; entradas de hit antes {Inputs}.",
                    mode,
                    __state);
            }

            return __exception;
        }

        private static string FormatPosition(Vector3 position)
        {
            return $"({position.x:0.0}, {position.y:0.0}, {position.z:0.0})";
        }
    }

    [HarmonyPatch(typeof(DamageTool), nameof(DamageTool.damageZombie))]
    internal static class SkaliaBotZombieDamageDiagnosticsPatch
    {
        internal readonly struct DamageState
        {
            public DamageState(bool active, ushort health, bool wasAlive)
            {
                Active = active;
                Health = health;
                WasAlive = wasAlive;
            }

            public bool Active { get; }
            public ushort Health { get; }
            public bool WasAlive { get; }
        }

        [HarmonyPrefix]
        private static void Prefix(
            DamageZombieParameters parameters,
            out DamageState __state)
        {
            var player = parameters.instigator as Player;
            __state = SkaliaBotLifecycleDiagnostics.IsBot(player)
                && parameters.zombie != null
                ? new DamageState(
                    true,
                    parameters.zombie.health,
                    !parameters.zombie.isDead)
                : default;
        }

        [HarmonyPostfix]
        private static void Postfix(
            DamageZombieParameters parameters,
            EPlayerKill kill,
            DamageState __state)
        {
            if (!__state.Active || parameters.zombie == null)
            {
                return;
            }

            SkaliaBotLifecycleDiagnostics.LogDebug(
                "Dano do bot confirmado no zumbi: vida {Before}->{After}; dano base {Damage:0.##}; "
                + "multiplicador {Multiplier:0.##}; membro {Limb}; morto {Dead}; resultado {Kill}.",
                __state.Health,
                parameters.zombie.health,
                parameters.damage,
                parameters.times,
                parameters.limb,
                parameters.zombie.isDead,
                kill);
            // Only the hit that turned a living zombie into a dead one counts
            // as a kill; extra hits on the corpse in the same frame do not.
            if (__state.WasAlive
                && parameters.zombie.isDead
                && parameters.instigator is Player player)
            {
                SkaliaBotIdleSimulation.NotifyZombieKilled(
                    player,
                    parameters.zombie);
            }
        }
    }

    [HarmonyPatch(typeof(Provider), "RemoveClient")]
    internal static class SkaliaBotProviderRemovalDiagnosticsPatch
    {
        [HarmonyPrefix]
        private static void Prefix(SteamPlayer clientToRemove, out bool __state)
        {
            __state = SkaliaBotLifecycleDiagnostics.IsBot(clientToRemove);
            if (!__state)
            {
                return;
            }

            SkaliaBotLifecycleDiagnostics.LogInformation(
                "Provider.RemoveClient iniciou para o bot: presente {Present}; morto {Dead}; "
                + "ping {Ping:0.###}s; ultimo pacote ha {PacketAge:0.###}s. Stack trace:\n{StackTrace}",
                Provider.clients.Contains(clientToRemove),
                clientToRemove.player?.life?.isDead ?? false,
                clientToRemove.ping,
                Time.realtimeSinceStartup - clientToRemove.timeLastPacketWasReceivedFromClient,
                Environment.StackTrace);
        }

        [HarmonyPostfix]
        private static void Postfix(SteamPlayer clientToRemove, bool __state)
        {
            if (__state)
            {
                SkaliaBotLifecycleDiagnostics.LogInformation(
                    "Provider.RemoveClient terminou para o bot: ainda presente {Present}; clientes restantes {Count}.",
                    Provider.clients.Contains(clientToRemove),
                    Provider.clients.Count);
            }
        }
    }
}
