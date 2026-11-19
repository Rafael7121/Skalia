using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SDG.Unturned;

namespace SkaliaDeps
{
    internal static class AutomaticReloadService
    {
        private const int AmmoStateIndex = 10;
        private const int MaximumWaitFrames = 180;

        private static readonly object Sync = new object();
        private static readonly HashSet<int> PendingGuns = new HashSet<int>();
        private static readonly MethodInfo? ReceiveAttachMagazineMethod = typeof(UseableGun)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "ReceiveAttachMagazine")
            .Where(method => method.GetParameters().Length == 4 || method.GetParameters().Length == 5)
            .OrderByDescending(method => method.GetParameters().Length)
            .FirstOrDefault();
        private static CancellationTokenSource? s_Shutdown;
        private static ILogger? s_Logger;

        public static void Start(ILogger logger)
        {
            lock (Sync)
            {
                s_Shutdown?.Cancel();
                s_Shutdown?.Dispose();
                s_Shutdown = new CancellationTokenSource();
                s_Logger = logger;
                PendingGuns.Clear();
            }
        }

        public static void Stop()
        {
            lock (Sync)
            {
                s_Shutdown?.Cancel();
                s_Shutdown?.Dispose();
                s_Shutdown = null;
                s_Logger = null;
                PendingGuns.Clear();
            }
        }

        public static void QueueAfterShot(UseableGun gun)
        {
            if (!Provider.isServer || !IsSameEquippedEmptyGun(gun))
            {
                return;
            }

            // Evita iniciar uma espera por pellet/projétil ou por chamadas repetidas
            // do mesmo disparo enquanto a arma ainda está finalizando o frame.
            var gunId = gun.GetInstanceID();
            CancellationToken token;
            lock (Sync)
            {
                if (s_Shutdown == null || !PendingGuns.Add(gunId))
                {
                    return;
                }

                token = s_Shutdown.Token;
            }

            ReloadWhenReadyAsync(gun, gunId, token).Forget();
        }

        private static async UniTaskVoid ReloadWhenReadyAsync(
            UseableGun gun,
            int gunId,
            CancellationToken cancellationToken)
        {
            try
            {
                // ReceiveAttachMagazine rejeita a solicitação enquanto o último tiro
                // ainda está marcado como ativo. Tentamos novamente por até três
                // segundos, mas paramos imediatamente se a arma for trocada, receber
                // munição por outro meio ou ficar sem carregador compatível.
                for (var frame = 0; frame < MaximumWaitFrames; frame++)
                {
                    await UniTask.NextFrame(cancellationToken);

                    if (!IsSameEquippedEmptyGun(gun))
                    {
                        return;
                    }

                    if (!TryFindBestMagazine(gun, out var magazine))
                    {
                        return;
                    }

                    InvokeAttachMagazine(gun, magazine);

                    if (!IsEmpty(gun))
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Esperado durante descarregamento ou reinício do plugin.
            }
            catch (Exception exception)
            {
                s_Logger?.LogError(exception, "Falha ao iniciar a recarga automática.");
            }
            finally
            {
                lock (Sync)
                {
                    PendingGuns.Remove(gunId);
                }
            }
        }

        private static bool TryFindBestMagazine(
            UseableGun gun,
            out InventorySearch bestMagazine)
        {
            var gunAsset = gun.equippedGunAsset;
            var results = gun.player.inventory.search(
                EItemType.MAGAZINE,
                gunAsset.magazineCalibers,
                !gunAsset.requiresNonZeroAttachmentCaliber);

            var bestIndex = -1;
            byte bestAmmo = 0;
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                var asset = result.GetAsset();
                var jar = result.jar;
                if (asset == null || jar == null || jar.item == null || jar.item.amount <= bestAmmo)
                {
                    continue;
                }

                bestAmmo = jar.item.amount;
                bestIndex = index;
            }

            if (bestIndex < 0)
            {
                bestMagazine = null!;
                return false;
            }

            bestMagazine = results[bestIndex];
            return true;
        }

        private static void InvokeAttachMagazine(UseableGun gun, InventorySearch magazine)
        {
            if (ReceiveAttachMagazineMethod == null)
            {
                throw new MissingMethodException(typeof(UseableGun).FullName, "ReceiveAttachMagazine");
            }

            var jar = magazine.jar;
            var asset = magazine.GetAsset();
            var parameters = ReceiveAttachMagazineMethod.GetParameters();
            if (parameters.Length == 5)
            {
                var contextType = parameters[0].ParameterType.GetElementType() ?? parameters[0].ParameterType;
                ReceiveAttachMagazineMethod.Invoke(gun, new[]
                {
                    Activator.CreateInstance(contextType),
                    (object)magazine.page,
                    jar.x,
                    jar.y,
                    asset.hash
                });
                return;
            }

            ReceiveAttachMagazineMethod.Invoke(gun, new object[]
            {
                magazine.page,
                jar.x,
                jar.y,
                asset.hash
            });
        }

        private static bool IsSameEquippedEmptyGun(UseableGun gun)
        {
            if (gun == null || gun.player == null || gun.player.life.isDead)
            {
                return false;
            }

            var equipment = gun.player.equipment;
            return equipment != null &&
                   equipment.useable == gun &&
                   equipment.IsEquipAnimationFinished &&
                   IsEmpty(gun);
        }

        private static bool IsEmpty(UseableGun gun)
        {
            var state = gun.player.equipment.state;
            return state != null && state.Length > AmmoStateIndex && state[AmmoStateIndex] == 0;
        }
    }
}
