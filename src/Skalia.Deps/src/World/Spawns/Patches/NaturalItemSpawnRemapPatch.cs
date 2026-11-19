using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Remaps only loot resolved from map item spawnpoints. Player drops,
    /// inventories, commands, crafting, NPC rewards and airdrops do not call
    /// this overload and therefore remain untouched.
    /// </summary>
    [HarmonyPatch(
        typeof(LevelItems),
        nameof(LevelItems.getItem),
        new[] { typeof(ItemSpawnpoint) })]
    internal static class NaturalItemSpawnRemapPatch
    {
        internal const ushort VanillaFragmentationGrenadeId = 254;
        internal const ushort StackableFragmentationGrenadeId = 59122;
        internal const ushort VanillaRawExplosivesId = 73;
        internal const ushort StackableRawExplosivesId = 59123;

        [HarmonyPostfix]
        private static void Postfix(ref ushort __result)
        {
            __result = RemapLoadedItemId(__result);
        }

        internal static ushort RemapLoadedItemId(ushort originalId)
        {
            switch (originalId)
            {
                case VanillaFragmentationGrenadeId:
                    return GetLoadedReplacementOrOriginal(
                        StackableFragmentationGrenadeId,
                        VanillaFragmentationGrenadeId);

                case VanillaRawExplosivesId:
                    return GetLoadedReplacementOrOriginal(
                        StackableRawExplosivesId,
                        VanillaRawExplosivesId);

                default:
                    return originalId;
            }
        }

        internal static ItemAsset RemapLoadedItemAsset(ItemAsset originalAsset)
        {
            ushort replacementId = RemapLoadedItemId(originalAsset.id);
            if (replacementId == originalAsset.id)
            {
                return originalAsset;
            }

            return Assets.find(EAssetType.ITEM, replacementId) as ItemAsset
                ?? originalAsset;
        }

        private static ushort GetLoadedReplacementOrOriginal(
            ushort replacementId,
            ushort originalId)
        {
            return Assets.find(EAssetType.ITEM, replacementId) is ItemAsset
                ? replacementId
                : originalId;
        }
    }
}
