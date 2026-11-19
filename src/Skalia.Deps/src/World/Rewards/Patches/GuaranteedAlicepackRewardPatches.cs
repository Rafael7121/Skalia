using HarmonyLib;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    [HarmonyPatch(typeof(Zombie), nameof(Zombie.askDamage))]
    internal static class MegaZombieAlicepackRewardPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Zombie __instance, out bool __state)
        {
            __state = Provider.isServer
                && __instance.isMega
                && !__instance.isDead;
        }

        [HarmonyPostfix]
        private static void Postfix(Zombie __instance, bool __state)
        {
            if (__state && __instance.isDead)
            {
                // Service contains its own exception boundary so optional reward
                // code cannot break Zombie.askDamage or Mega respawning.
                GuaranteedAlicepackRewardService.DropForMegaZombie(
                    __instance.transform.position);
            }
        }
    }

    [HarmonyPatch(
        typeof(InteractableBeacon),
        nameof(InteractableBeacon.ManualOnDestroy))]
    internal static class HordeBeaconAlicepackRewardPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            InteractableBeacon __instance,
            bool ___isRegistered,
            bool ___wasInit,
            int ___remaining,
            int ___alive,
            out Vector3 __state)
        {
            bool wasSuccessfullyCompleted = Provider.isServer
                && ___isRegistered
                && ___wasInit
                && ___remaining == 0
                && ___alive == 0;

            __state = wasSuccessfullyCompleted
                ? __instance.transform.position
                : new Vector3(float.NaN, float.NaN, float.NaN);
        }

        [HarmonyPostfix]
        private static void Postfix(Vector3 __state)
        {
            if (!float.IsNaN(__state.x))
            {
                GuaranteedAlicepackRewardService
                    .DropForCompletedHordeBeacon(__state);
            }
        }
    }
}
