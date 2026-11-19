using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps.Patches
{
    [HarmonyPatch(typeof(UseableGun), "fire")]
    internal static class AutomaticReloadPatch
    {
        [HarmonyPostfix]
        private static void Postfix(UseableGun __instance)
        {
            AutomaticReloadService.QueueAfterShot(__instance);
        }
    }
}
