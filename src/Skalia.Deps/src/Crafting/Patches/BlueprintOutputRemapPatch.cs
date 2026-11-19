using System;
using System.Reflection;
using HarmonyLib;
using SDG.Unturned;

namespace SkaliaDeps
{
    internal static class CraftingOutputRemapContext
    {
        [ThreadStatic]
        private static int s_Depth;

        internal static bool IsCrafting => s_Depth > 0;

        internal static void Enter()
        {
            s_Depth++;
        }

        internal static void Exit()
        {
            if (s_Depth > 0)
            {
                s_Depth--;
            }
        }
    }

    /// <summary>
    /// Restricts output remapping to server-side blueprint execution. Asset
    /// inspection and integrity hashing continue to see the original vanilla
    /// blueprint references.
    /// </summary>
    [HarmonyPatch]
    internal static class CraftRequestOutputRemapScopePatch
    {
        private const string CraftMethodName = "HandleCraftRequestInternal";

        private static MethodBase TargetMethod()
        {
            foreach (MethodInfo method in typeof(PlayerCrafting).GetMethods(
                         BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.Instance))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.Name == CraftMethodName
                    && method.ReturnType == typeof(bool)
                    && parameters.Length == 5
                    && parameters[1].ParameterType == typeof(Blueprint))
                {
                    return method;
                }
            }

            throw new MissingMethodException(
                typeof(PlayerCrafting).FullName,
                CraftMethodName);
        }

        [HarmonyPrefix]
        private static void Prefix()
        {
            CraftingOutputRemapContext.Enter();
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            CraftingOutputRemapContext.Exit();
        }
    }

    /// <summary>
    /// Redirects any vanilla crafting or salvage output for Fragmentation
    /// Grenades and Raw Explosives to their stackable replacements while the
    /// server is executing a craft request.
    /// </summary>
    [HarmonyPatch]
    internal static class BlueprintOutputRemapPatch
    {
        private const string FindItemAssetMethodName = "FindItemAsset";

        private static MethodBase TargetMethod()
        {
            foreach (MethodInfo method in typeof(BlueprintOutput).GetMethods(
                         BindingFlags.Public
                         | BindingFlags.NonPublic
                         | BindingFlags.Instance))
            {
                if (method.Name == FindItemAssetMethodName
                    && !method.IsGenericMethod
                    && method.GetParameters().Length == 0
                    && method.ReturnType == typeof(ItemAsset))
                {
                    return method;
                }
            }

            throw new MissingMethodException(
                typeof(BlueprintOutput).FullName,
                FindItemAssetMethodName);
        }

        [HarmonyPostfix]
        private static void Postfix(ref ItemAsset __result)
        {
            if (CraftingOutputRemapContext.IsCrafting && __result != null)
            {
                __result =
                    NaturalItemSpawnRemapPatch.RemapLoadedItemAsset(__result);
            }
        }
    }
}
