using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Players.Connections.Events;
using OpenMod.Unturned.Players.Life.Events;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    public sealed class ServerActivityAnnouncementListener :
        IEventListener<UnturnedPlayerConnectedEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>,
        IEventListener<UnturnedPlayerDeathEvent>
    {
        private readonly IStringLocalizer m_StringLocalizer;

        public ServerActivityAnnouncementListener(IStringLocalizer stringLocalizer)
        {
            m_StringLocalizer = stringLocalizer;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            return BroadcastAsync("messages:activity:player_joined", new { Player = GetTaggedName(@event.Player) }, Color.yellow);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            // As conexoes locais sao anunciadas diretamente pelo servico antes
            // da remocao, inclusive enquanto o OpenMod esta recarregando.
            if (@event.Player.SteamPlayer.transportConnection?.GetType().FullName ==
                "SkaliaDeps.SkaliaBotTransportConnection")
            {
                return Task.CompletedTask;
            }

            return BroadcastAsync("messages:activity:player_left", new { Player = GetTaggedName(@event.Player) }, Color.yellow);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDeathEvent @event)
        {
            var victimId = @event.Player.SteamId.m_SteamID;
            var killerId = @event.Instigator.m_SteamID;
            if (killerId == 0 || killerId == victimId)
            {
                return Task.CompletedTask;
            }

            var killer = PlayerTool.getSteamPlayer(@event.Instigator);
            if (killer == null)
            {
                return Task.CompletedTask;
            }

            return BroadcastAsync(GetKillMessageKey(@event.DeathCause, @event.Limb), new
            {
                Killer = killer.playerID.characterName,
                Victim = GetName(@event.Player),
                Distance = GetDistanceMeters(@event.Player, killer)
            }, Color.red);
        }

        private async Task BroadcastAsync(string key, object arguments, Color color)
        {
            var message = m_StringLocalizer[key, arguments].Value;
            await UniTask.SwitchToMainThread();
            ChatManager.serverSendMessage(
                text: message,
                color: color,
                useRichTextFormatting: true,
                iconURL: Provider.configData.Browser.Icon);
        }

        private static string GetName(UnturnedPlayer player)
        {
            return player.SteamPlayer.playerID.characterName;
        }

        private static string GetTaggedName(UnturnedPlayer player)
        {
            return ChatNamePrefixService.FormatName(GetName(player));
        }

        private static int GetDistanceMeters(UnturnedPlayer victim, SteamPlayer killer)
        {
            var victimPosition = victim.Transform.Position;
            var killerUnityPosition = killer.player.transform.position;
            var killerPosition = new System.Numerics.Vector3(
                killerUnityPosition.x,
                killerUnityPosition.y,
                killerUnityPosition.z);

            return (int)System.Math.Round(
                System.Numerics.Vector3.Distance(victimPosition, killerPosition),
                System.MidpointRounding.AwayFromZero);
        }

        private static string GetKillMessageKey(EDeathCause cause, ELimb limb)
        {
            switch (cause)
            {
                case EDeathCause.GUN:
                    if (limb == ELimb.SKULL)
                    {
                        return "messages:activity:kill_gun_head";
                    }

                    if (IsArm(limb))
                    {
                        return "messages:activity:kill_gun_arm";
                    }

                    if (IsLeg(limb))
                    {
                        return "messages:activity:kill_gun_leg";
                    }

                    return "messages:activity:kill_gun_torso";

                case EDeathCause.MELEE:
                    if (limb == ELimb.SKULL)
                    {
                        return "messages:activity:kill_melee_head";
                    }

                    return IsTorso(limb)
                        ? "messages:activity:kill_melee_torso"
                        : "messages:activity:kill_melee_limb";

                case EDeathCause.PUNCH:
                    return "messages:activity:kill_punch";

                case EDeathCause.ROADKILL:
                    return "messages:activity:kill_roadkill";

                case EDeathCause.VEHICLE:
                    return "messages:activity:kill_vehicle";

                case EDeathCause.GRENADE:
                case EDeathCause.LANDMINE:
                case EDeathCause.MISSILE:
                case EDeathCause.CHARGE:
                case EDeathCause.SPLASH:
                    return "messages:activity:kill_explosion";

                case EDeathCause.SENTRY:
                    return "messages:activity:kill_sentry";

                case EDeathCause.BURNING:
                case EDeathCause.BURNER:
                    return "messages:activity:kill_fire";

                case EDeathCause.ACID:
                case EDeathCause.SPIT:
                    return "messages:activity:kill_acid";

                case EDeathCause.SHRED:
                    return "messages:activity:kill_shred";

                default:
                    return "messages:activity:player_killed";
            }
        }

        private static bool IsArm(ELimb limb)
        {
            return limb == ELimb.LEFT_ARM || limb == ELimb.RIGHT_ARM ||
                   limb == ELimb.LEFT_HAND || limb == ELimb.RIGHT_HAND;
        }

        private static bool IsLeg(ELimb limb)
        {
            return limb == ELimb.LEFT_LEG || limb == ELimb.RIGHT_LEG ||
                   limb == ELimb.LEFT_FOOT || limb == ELimb.RIGHT_FOOT;
        }

        private static bool IsTorso(ELimb limb)
        {
            return limb == ELimb.SPINE || limb == ELimb.LEFT_BACK ||
                   limb == ELimb.RIGHT_BACK || limb == ELimb.LEFT_FRONT ||
                   limb == ELimb.RIGHT_FRONT;
        }
    }
}
