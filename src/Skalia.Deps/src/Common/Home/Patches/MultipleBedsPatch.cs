using System;
using HarmonyLib;
using SDG.Unturned;
using Steamworks;

namespace SkaliaDeps.Patches
{
    internal static class MultipleBedClaimContext
    {
        [ThreadStatic]
        internal static bool IsClaimingBed;
    }

    [HarmonyPatch(typeof(BarricadeManager), nameof(BarricadeManager.ServerClaimBedForPlayer))]
    internal static class BedClaimContextPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            MultipleBedClaimContext.IsClaimingBed = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            MultipleBedClaimContext.IsClaimingBed = false;
        }
    }

    /// <summary>
    /// A interacao normal do jogador nao usa ServerClaimBedForPlayer. Ela entra
    /// diretamente em InteractableBed.ReceiveClaimRequest e chama unclaimBeds.
    /// Mantemos o mesmo contexto para esse caminho nativo.
    /// </summary>
    [HarmonyPatch(typeof(InteractableBed), nameof(InteractableBed.ReceiveClaimRequest))]
    internal static class InteractiveBedClaimContextPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            MultipleBedClaimContext.IsClaimingBed = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            MultipleBedClaimContext.IsClaimingBed = false;
        }
    }

    [HarmonyPatch(typeof(BarricadeManager), nameof(BarricadeManager.unclaimBeds),
        new[] { typeof(CSteamID) })]
    internal static class PreserveExistingBedsPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            // Mantém o comportamento normal em qualquer outro uso de unclaimBeds.
            // Apenas a limpeza automática feita ao reivindicar uma nova cama é ignorada.
            return !MultipleBedClaimContext.IsClaimingBed;
        }
    }
}
