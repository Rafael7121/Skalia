using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.23 (the tester, 20/11: "adicione o full loot para os bots para
    // eles se equiparem de acordo com os requisitos ja feitos", "ja pensando
    // em loots de todos os mapas e ... compatibilidade com mods"; his pick:
    // the bot loots until complete, and equipped also asks for clothes, a
    // backpack, a vest and a helmet). Equipped was a strong gun, rounds for
    // the Mega and 2 bandages (IsTryhardEquipped). It now also asks for the
    // worn gear, measured against what the loaded map spawns: the loot
    // tables are read by drawing from them (EnsureItemTableProfiles), so a
    // map's own or a mod's items count as they come, and a piece the map
    // never spawns is not asked for.
    // - Shirt and pants: worn.
    // - Backpack: room at least the middle (by spawn count) of the map's
    //   backpacks, so the smallest bag does not count.
    // - Vest: one that protects (armor under 0.96, as IsValuableLoot).
    // - Helmet: a hat that protects (armor 0.95 or less, IsUsefulHeadgear).
    // Speed-cutting pieces (movementSpeedMultiplier under 0.99) are left out
    // of the map's list, as the wear code leaves them out.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float FullLootVestArmor = 0.96f;
        private const float FullLootHelmetArmor = 0.95f;

        private static bool s_MapGearResolved;
        private static bool s_MapHasShirt;
        private static bool s_MapHasPants;
        private static bool s_MapHasVest;
        private static bool s_MapHasHelmet;
        private static int s_MapBackpackRoom;

        private static void EnsureMapGear()
        {
            if (s_MapGearResolved)
            {
                return;
            }
            EnsureItemTableProfiles();
            if (s_ItemTableProfiles == null || s_MapTableCounts == null)
            {
                return;
            }
            s_MapGearResolved = true;
            var backpacks = new List<KeyValuePair<int, float>>();
            var total = 0f;
            foreach (var entry in s_ItemTableProfiles)
            {
                if (!s_MapTableCounts.TryGetValue(entry.Key, out var points) || points == 0)
                {
                    continue;
                }
                foreach (var item in entry.Value)
                {
                    if (!(Assets.find(EAssetType.ITEM, item.Key) is ItemClothingAsset gear)
                        || gear.isPro
                        || gear.movementSpeedMultiplier < 0.99f)
                    {
                        continue;
                    }
                    switch (gear.type)
                    {
                        case EItemType.SHIRT:
                            s_MapHasShirt = true;
                            break;
                        case EItemType.PANTS:
                            s_MapHasPants = true;
                            break;
                        case EItemType.VEST:
                            s_MapHasVest |= gear.armor < FullLootVestArmor;
                            break;
                        case EItemType.HAT:
                            s_MapHasHelmet |= gear.armor <= FullLootHelmetArmor;
                            break;
                        case EItemType.BACKPACK:
                            if (gear is ItemBagAsset bag && bag.width * bag.height > 0)
                            {
                                var weight = item.Value * points;
                                backpacks.Add(new KeyValuePair<int, float>(bag.width * bag.height, weight));
                                total += weight;
                            }
                            break;
                    }
                }
            }
            s_MapBackpackRoom = 0;
            if (backpacks.Count > 0)
            {
                backpacks.Sort((a, b) => a.Key.CompareTo(b.Key));
                var walked = 0f;
                foreach (var entry in backpacks)
                {
                    walked += entry.Value;
                    if (walked >= total * 0.5f)
                    {
                        s_MapBackpackRoom = entry.Key;
                        break;
                    }
                }
            }
        }

        private bool m_MapGearLogged;

        // The worn part of "equipped"; lacks names the first piece missing.
        private bool HasFullLootGear(out string lacks)
        {
            lacks = string.Empty;
            EnsureMapGear();
            if (!s_MapGearResolved)
            {
                return true;
            }
            if (!m_MapGearLogged)
            {
                m_MapGearLogged = true;
                BotLog(LogLevel.Information,
                    "Full loot neste mapa: camisa {Shirt}, calca {Pants}, mochila de {Room}+ espacos, colete que protege {Vest}, capacete {Helmet} (pelas tabelas de loot do mapa).",
                    s_MapHasShirt ? "sim" : "nao existe",
                    s_MapHasPants ? "sim" : "nao existe",
                    s_MapBackpackRoom > 0 ? s_MapBackpackRoom.ToString() : "-",
                    s_MapHasVest ? "sim" : "nao existe",
                    s_MapHasHelmet ? "sim" : "nao existe");
            }
            var clothing = m_Player.clothing;
            if (s_MapHasShirt && clothing.shirt == 0)
            {
                lacks = "camisa";
                return false;
            }
            if (s_MapHasPants && clothing.pants == 0)
            {
                lacks = "calca";
                return false;
            }
            if (s_MapBackpackRoom > 0
                && !(clothing.backpackAsset is ItemBagAsset backpack && backpack.width * backpack.height >= s_MapBackpackRoom))
            {
                lacks = "mochila de " + s_MapBackpackRoom + " espacos ou mais";
                return false;
            }
            if (s_MapHasVest && !(clothing.vestAsset is ItemClothingAsset vest && vest.armor < FullLootVestArmor))
            {
                lacks = "colete que protege";
                return false;
            }
            if (s_MapHasHelmet && !(clothing.hatAsset is ItemClothingAsset hat && hat.armor <= FullLootHelmetArmor))
            {
                lacks = "capacete";
                return false;
            }
            return true;
        }
    }
}
