using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    internal static class GuaranteedAlicepackRewardService
    {
        internal const ushort DefaultAlicepackId = 253;

        private static MicrosoftLogger? s_Logger;
        private static bool s_Enabled;
        private static bool s_MegaZombieEnabled;
        private static bool s_HordeBeaconEnabled;
        private static ushort s_ItemId;
        private static int s_MegaZombieQuantity;
        private static int s_HordeBeaconQuantity;
        private static bool s_HasReportedInvalidAsset;

        internal static void Configure(
            IConfiguration configuration,
            MicrosoftLogger logger)
        {
            s_Logger = logger;
            s_Enabled = configuration.GetValue(
                "alicepackRewards:enabled", true);
            s_MegaZombieEnabled = configuration.GetValue(
                "alicepackRewards:megaZombie:enabled", true);
            s_HordeBeaconEnabled = configuration.GetValue(
                "alicepackRewards:hordeBeacon:enabled", true);

            int configuredItemId = configuration.GetValue(
                "alicepackRewards:itemId", (int) DefaultAlicepackId);
            s_ItemId = (ushort) Mathf.Clamp(
                configuredItemId,
                1,
                ushort.MaxValue);

            s_MegaZombieQuantity = Mathf.Clamp(
                configuration.GetValue(
                    "alicepackRewards:megaZombie:quantity", 1),
                1,
                10);
            s_HordeBeaconQuantity = Mathf.Clamp(
                configuration.GetValue(
                    "alicepackRewards:hordeBeacon:quantity", 1),
                1,
                10);
            s_HasReportedInvalidAsset = false;

            if (s_Enabled)
            {
                logger.LogInformation(
                    "Recompensas garantidas de Alicepack configuradas: item {ItemId}, Mega x{MegaQuantity}, Horde Beacon x{HordeQuantity}.",
                    s_ItemId,
                    s_MegaZombieEnabled ? s_MegaZombieQuantity : 0,
                    s_HordeBeaconEnabled ? s_HordeBeaconQuantity : 0);
            }
        }

        internal static void Clear()
        {
            s_Logger = null;
            s_Enabled = false;
            s_MegaZombieEnabled = false;
            s_HordeBeaconEnabled = false;
            s_HasReportedInvalidAsset = false;
        }

        internal static void DropForMegaZombie(Vector3 position)
        {
            if (s_Enabled && s_MegaZombieEnabled)
            {
                TryDrop(position, s_MegaZombieQuantity, "Mega Zombie");
            }
        }

        internal static void DropForCompletedHordeBeacon(Vector3 position)
        {
            if (s_Enabled && s_HordeBeaconEnabled)
            {
                TryDrop(position, s_HordeBeaconQuantity, "Horde Beacon");
            }
        }

        private static void TryDrop(Vector3 position, int quantity, string source)
        {
            try
            {
                Drop(position, quantity, source);
            }
            catch (Exception exception)
            {
                // Reward failure must never escape into Zombie.askDamage because
                // that can interrupt the native death/respawn flow for the Mega.
                s_Logger?.LogError(
                    exception,
                    "Falha ao gerar recompensa de Alicepack para {Source}; o fluxo nativo foi preservado.",
                    source);
            }
        }

        private static void Drop(
            Vector3 position,
            int quantity,
            string source)
        {
            ItemAsset? asset = Assets.find(EAssetType.ITEM, s_ItemId)
                as ItemAsset;
            if (asset == null || asset.type != EItemType.BACKPACK)
            {
                if (!s_HasReportedInvalidAsset)
                {
                    s_Logger?.LogError(
                        "A recompensa garantida de Alicepack foi ignorada: o ID {ItemId} nao existe ou nao e uma mochila.",
                        s_ItemId);
                    s_HasReportedInvalidAsset = true;
                }

                return;
            }

            for (int index = 0; index < quantity; index++)
            {
                ItemManager.dropItem(
                    new Item(s_ItemId, EItemOrigin.WORLD),
                    position,
                    false,
                    true,
                    true);
            }

            s_Logger?.LogDebug(
                "Recompensa garantida gerada por {Source}: item {ItemId} x{Quantity}.",
                source,
                s_ItemId,
                quantity);
        }
    }
}
