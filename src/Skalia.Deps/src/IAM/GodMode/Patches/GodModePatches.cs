using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps.Patches
{
    [HarmonyPatch(typeof(PlayerLife), "doDamage")]
    internal static class GodModeDamagePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            PlayerLife __instance,
            byte amount,
            EDeathCause newCause)
        {
            return !GodModeState.IsEnabled(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerLife), "onLanded")]
    internal static class GodModeFallPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerLife __instance)
        {
            return !GodModeState.IsEnabled(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerLife), nameof(PlayerLife.askStarve))]
    internal static class GodModeFoodPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerLife __instance)
        {
            return !GodModeState.IsEnabled(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerLife), nameof(PlayerLife.askDehydrate))]
    internal static class GodModeWaterPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerLife __instance)
        {
            return !GodModeState.IsEnabled(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerLife), nameof(PlayerLife.askTire))]
    internal static class GodModeStaminaPatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerLife __instance, out byte __state)
        {
            __state = __instance.stamina;
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerLife __instance, byte __state)
        {
            if (GodModeState.IsEnabled(__instance) && __instance.stamina < __state)
            {
                __instance.serverModifyStamina(__state - __instance.stamina);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerLife), nameof(PlayerLife.askInfect))]
    internal static class GodModeInfectionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PlayerLife __instance)
        {
            return !GodModeState.IsEnabled(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerLife), nameof(PlayerLife.askRadiate))]
    internal static class GodModeRadiationPatch
    {
        [HarmonyPrefix]
        private static void Prefix(PlayerLife __instance, out byte __state)
        {
            __state = __instance.virus;
        }

        [HarmonyPostfix]
        private static void Postfix(PlayerLife __instance, byte __state)
        {
            if (GodModeState.IsEnabled(__instance) && __instance.virus < __state)
            {
                __instance.serverModifyVirus(__state - __instance.virus);
            }
        }
    }
}
