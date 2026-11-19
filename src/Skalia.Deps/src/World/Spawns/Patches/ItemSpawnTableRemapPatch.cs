using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Remaps legacy map item tables used by zombie loot. This overload is
    /// separate from the ItemSpawnpoint overload used by natural ground loot.
    /// </summary>
    [HarmonyPatch(
        typeof(LevelItems),
        nameof(LevelItems.getItem),
        new[] { typeof(byte) })]
    internal static class LegacyMapItemTableRemapPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref ushort __result)
        {
            __result = NaturalItemSpawnRemapPatch.RemapLoadedItemId(__result);
        }
    }

    /// <summary>
    /// Remaps item results from spawn assets and legacy spawn tables. These
    /// resolvers are used by zombie drops, Horde Beacon rewards and airdrop
    /// carepackages. Non-item spawn table resolutions are left untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class ItemSpawnTableRemapPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (MethodInfo method in typeof(SpawnTableTool).GetMethods(
                         BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.Static))
            {
                if (method.Name != nameof(SpawnTableTool.ResolveLegacyId)
                    || method.ReturnType != typeof(ushort))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 3
                    && parameters[1].ParameterType == typeof(EAssetType))
                {
                    yield return method;
                }
            }
        }

        [HarmonyPostfix]
        private static void Postfix(
            EAssetType legacyTargetAssetType,
            ref ushort __result)
        {
            if (legacyTargetAssetType == EAssetType.ITEM)
            {
                __result =
                    NaturalItemSpawnRemapPatch.RemapLoadedItemId(__result);
            }
        }
    }
}
