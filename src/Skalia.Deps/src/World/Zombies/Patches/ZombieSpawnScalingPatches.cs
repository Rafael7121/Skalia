using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    [HarmonyPatch(typeof(ZombieManager), nameof(ZombieManager.generateZombies))]
    internal static class GenerateZombiesScalingPatch
    {
        private static void Prefix()
        {
            ZombieSpawnScalingService.ApplyToLoadedLevel();
        }

        private static void Postfix(
            ZombieManager __instance,
            byte bound)
        {
            ZombieSpawnScalingService.EnsureConfiguredMegaCount(
                __instance,
                bound);
        }
    }

    [HarmonyPatch(typeof(ZombieManager), nameof(ZombieManager.respawnZombies))]
    internal static class GuaranteedMegaRespawnPatch
    {
        private static void Postfix()
        {
            ZombieSpawnScalingService.TryGuaranteeMega(
                ZombieManager.respawnZombiesBound);
        }
    }

    [HarmonyPatch(
        typeof(ZombieManager),
        "canRegionSpawnZombiesFromTable")]
    internal static class MultipleMegaRespawnPatch
    {
        private static void Postfix(
            ZombieRegion region,
            ZombieTable table,
            ref bool __result)
        {
            if (!__result
                && table.isMega
                && ZombieSpawnScalingService.CanAddAnotherMega(region))
            {
                __result = true;
            }
        }
    }
}
