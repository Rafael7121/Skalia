using System;
using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Observa o pedido de abrir/fechar o inventario depois que o metodo nativo
    /// termina. O Postfix tambem acontece quando o Unturned descarta a animacao
    /// por postura, permitindo que o inventario dentro de veiculos mantenha a UI.
    /// </summary>
    [HarmonyPatch(typeof(PlayerAnimator), nameof(PlayerAnimator.ReceiveGestureRequest))]
    internal static class InventoryGestureRequestPatch
    {
        internal static event Action<PlayerAnimator, bool>? InventoryVisibilityRequested;

        [HarmonyPostfix]
        private static void Postfix(PlayerAnimator __instance, EPlayerGesture newGesture)
        {
            bool? isInventoryOpen = null;
            if (newGesture == EPlayerGesture.INVENTORY_START)
            {
                isInventoryOpen = true;
            }
            else if (newGesture == EPlayerGesture.INVENTORY_STOP)
            {
                isInventoryOpen = false;
            }

            if (!isInventoryOpen.HasValue)
            {
                return;
            }

            try
            {
                InventoryVisibilityRequested?.Invoke(__instance, isInventoryOpen.Value);
            }
            catch (Exception exception)
            {
                // Uma falha no Addon nunca deve interromper o RPC original do jogo.
                UnturnedLog.exception(
                    exception,
                    "Skalia Addon failed to observe an inventory gesture request.");
            }
        }
    }
}
