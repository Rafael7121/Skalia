using System;
using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    internal static class PlayerActivityInputBridge
    {
        internal static event Action<Player, ushort, EAttackInputFlags, EAttackInputFlags, float, float>?
            BaseInputRead;

        internal static event Action<Player, byte>? WalkingInputRead;

        internal static event Action<Player, float, float>? DrivingInputRead;

        internal static void ReportBase(PlayerInputPacket packet, SteamChannel channel)
        {
            var player = channel.owner?.player;
            if (!Provider.isServer || player == null)
            {
                return;
            }

            BaseInputRead?.Invoke(
                player,
                packet.keys,
                packet.primaryAttack,
                packet.secondaryAttack,
                packet.yaw,
                packet.pitch);
        }

        internal static void ReportWalking(WalkingPlayerInputPacket packet, SteamChannel channel)
        {
            var player = channel.owner?.player;
            if (Provider.isServer && player != null)
            {
                WalkingInputRead?.Invoke(player, packet.analog);
            }
        }

        internal static void ReportDriving(DrivingPlayerInputPacket packet, SteamChannel channel)
        {
            var player = channel.owner?.player;
            if (Provider.isServer && player != null)
            {
                DrivingInputRead?.Invoke(
                    player,
                    packet.steeringInput,
                    packet.velocityInput);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerInputPacket), nameof(PlayerInputPacket.read))]
    internal static class PlayerActivityBaseInputPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerInputPacket __instance, SteamChannel channel)
        {
            PlayerActivityInputBridge.ReportBase(__instance, channel);
        }
    }

    [HarmonyPatch(typeof(WalkingPlayerInputPacket), nameof(WalkingPlayerInputPacket.read))]
    internal static class PlayerActivityWalkingInputPatch
    {
        [HarmonyPostfix]
        private static void Postfix(WalkingPlayerInputPacket __instance, SteamChannel channel)
        {
            PlayerActivityInputBridge.ReportWalking(__instance, channel);
        }
    }

    [HarmonyPatch(typeof(DrivingPlayerInputPacket), nameof(DrivingPlayerInputPacket.read))]
    internal static class PlayerActivityDrivingInputPatch
    {
        [HarmonyPostfix]
        private static void Postfix(DrivingPlayerInputPacket __instance, SteamChannel channel)
        {
            PlayerActivityInputBridge.ReportDriving(__instance, channel);
        }
    }
}
