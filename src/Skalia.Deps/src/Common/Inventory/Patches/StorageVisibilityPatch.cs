using System;
using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Observa o RPC de storage no servidor. Diferente do gesto de inventario,
    /// este caminho sempre acontece ao abrir bau, trunk ou storage virtual,
    /// inclusive deitado ou com um item equipado.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.sendStorage))]
    internal static class StorageVisibilityPatch
    {
        internal static event Action<PlayerInventory, bool>? StorageVisibilityChanged;

        [HarmonyPostfix]
        private static void Postfix(PlayerInventory __instance)
        {
            try
            {
                StorageVisibilityChanged?.Invoke(__instance, __instance.isStoring);
            }
            catch (Exception exception)
            {
                UnturnedLog.exception(
                    exception,
                    "Skalia Addon failed to observe storage visibility.");
            }
        }
    }
}
