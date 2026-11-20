using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SDG.Framework.Devkit;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.24: full loot is the top of the loaded map (the tester, 20/11:
    // "tem que sempre ser os equipamentos mais tops do mapa"; Washington:
    // military alicepack and the AK, Heartbreaker or Honey Badger; PEI: AK or
    // Maple and the military gear; "o topo e a protecao"; the drum is not
    // required; in an arena any gun will do). y17.23 asked only for worn
    // pieces that protect at all and a middle-sized backpack.
    //
    // The top is read from the map that is loaded, so official, curated and
    // modded maps work the same way:
    // - Every loot table is walked exactly (legacy tiers and spawn assets,
    //   nested), so a rare piece like an Alicepack or a drum is seen; the
    //   80 draws the rest of the bot uses can miss it.
    // - Each item gets where it comes from: a building spawn point outside
    //   radiation (the bot loots these today), a spawn point in radiation,
    //   the Mega's drop or other zombies' drop. Expected count = the sum of
    //   its share over the spawn points of each table.
    // - Ranked as the tester said: a gun by GunScore (any gun within
    //   TopGunBand of the best counts, as the three of Washington), a backpack
    //   by room, a vest and a helmet by armor (protection first).
    // - Required now: the best of each kind that spawns in buildings outside
    //   radiation, at least TopMinimumExpected on the map. What is better but
    //   only comes from the Mega or radiation is logged and left for the
    //   steps that teach the bot those places.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float TopGunBand = 0.65f;
        private const float TopMinimumExpected = 1f;
        private const float TopArmorMatch = 0.005f;
        private const int TopSpawnDepth = 12;

        [System.Flags]
        private enum TopSource
        {
            None = 0,
            Building = 1,
            Radiation = 2,
            Mega = 4,
            Zombie = 8,
        }

        private sealed class TopItem
        {
            public ushort Id;
            public string Name = string.Empty;
            public float Building;
            public float Radiation;
            public TopSource Sources;
        }

        private static bool s_TopResolved;
        private static readonly Dictionary<ushort, TopItem> s_TopItems = new Dictionary<ushort, TopItem>();
        private static bool s_MapHasShirt;
        private static bool s_MapHasPants;
        private static float s_TopGunScore;
        private static int s_TopBagRoom;
        private static float s_TopVestArmor = 1f;
        private static float s_TopHatArmor = 1f;
        private static string s_TopSummary = string.Empty;
        private static bool s_TopLogged;

        private static bool IsArenaMap => Level.info != null && Level.info.type == ELevelType.ARENA;

        // ---- exact shares of a table ----
        private static void AddItemTableShares(Dictionary<ushort, float> shares, ItemTable table)
        {
            if (table.tableID != 0)
            {
                if (Assets.find(EAssetType.SPAWN, table.tableID) is SpawnAsset spawn)
                {
                    AddSpawnShares(shares, spawn, 1f, 0);
                }
                return;
            }
            var tiers = table.tiers;
            if (tiers == null || tiers.Count == 0)
            {
                return;
            }
            // getItem: tier i when the roll is under its (cumulative) chance;
            // past the last chance, a tier at random.
            var previous = 0f;
            var last = Mathf.Clamp01(tiers[tiers.Count - 1].chance);
            var rest = 1f - last;
            foreach (var tier in tiers)
            {
                var chance = Mathf.Clamp01(tier.chance);
                var share = Mathf.Max(0f, chance - previous) + rest / tiers.Count;
                previous = Mathf.Max(previous, chance);
                if (tier.table == null || tier.table.Count == 0 || share <= 0f)
                {
                    continue;
                }
                var each = share / tier.table.Count;
                foreach (var entry in tier.table)
                {
                    if (entry.item != 0)
                    {
                        shares.TryGetValue(entry.item, out var sum);
                        shares[entry.item] = sum + each;
                    }
                }
            }
        }

        private static void AddSpawnShares(Dictionary<ushort, float> shares, SpawnAsset spawn, float share, int depth)
        {
            if (depth > TopSpawnDepth || spawn.tables == null || spawn.tables.Count == 0)
            {
                return;
            }
            var total = 0f;
            foreach (var entry in spawn.tables)
            {
                total += Mathf.Max(0, entry.weight);
            }
            if (total <= 0f)
            {
                return;
            }
            foreach (var entry in spawn.tables)
            {
                if (entry.weight <= 0)
                {
                    continue;
                }
                var part = share * entry.weight / total;
                switch (entry.FindAsset(EAssetType.ITEM))
                {
                    case ItemAsset item:
                        shares.TryGetValue(item.id, out var sum);
                        shares[item.id] = sum + part;
                        break;
                    case SpawnAsset child:
                        AddSpawnShares(shares, child, part, depth + 1);
                        break;
                }
            }
        }

        private static TopItem GetTopItem(ushort id)
        {
            if (!s_TopItems.TryGetValue(id, out var item))
            {
                item = new TopItem { Id = id };
                if (Assets.find(EAssetType.ITEM, id) is ItemAsset asset)
                {
                    item.Name = asset.itemName;
                }
                s_TopItems[id] = item;
            }
            return item;
        }

        private static void EnsureMapTop()
        {
            if (s_TopResolved || !Level.isLoaded || LevelItems.tables == null)
            {
                return;
            }
            s_TopResolved = true;
            var tables = LevelItems.tables;
            var tableShares = new Dictionary<ushort, float>[tables.Count];
            for (var index = 0; index < tables.Count; index++)
            {
                tableShares[index] = new Dictionary<ushort, float>();
                if (tables[index] != null)
                {
                    try
                    {
                        AddItemTableShares(tableShares[index], tables[index]);
                    }
                    catch (System.Exception)
                    {
                        // A broken table of a map or mod: left out.
                    }
                }
            }
            if (LevelItems.spawns != null)
            {
                for (byte x = 0; x < Regions.WORLD_SIZE; x++)
                {
                    for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                    {
                        var spawns = LevelItems.spawns[x, y];
                        if (spawns == null)
                        {
                            continue;
                        }
                        foreach (var spawn in spawns)
                        {
                            if (spawn.type >= tableShares.Length)
                            {
                                continue;
                            }
                            var radiation = IsInRadiation(spawn.point);
                            foreach (var entry in tableShares[spawn.type])
                            {
                                var item = GetTopItem(entry.Key);
                                if (radiation)
                                {
                                    item.Radiation += entry.Value;
                                    item.Sources |= TopSource.Radiation;
                                }
                                else
                                {
                                    item.Building += entry.Value;
                                    item.Sources |= TopSource.Building;
                                }
                            }
                        }
                    }
                }
            }
            var zombies = LevelZombies.tables;
            if (zombies != null)
            {
                foreach (var zombie in zombies)
                {
                    if (zombie == null)
                    {
                        continue;
                    }
                    var shares = new Dictionary<ushort, float>();
                    try
                    {
                        if (zombie.lootID != 0)
                        {
                            if (Assets.find(EAssetType.SPAWN, zombie.lootID) is SpawnAsset spawn)
                            {
                                AddSpawnShares(shares, spawn, 1f, 0);
                            }
                        }
                        else if (zombie.lootIndex < tables.Count && tables[zombie.lootIndex] != null)
                        {
                            AddItemTableShares(shares, tables[zombie.lootIndex]);
                        }
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                    foreach (var id in shares.Keys)
                    {
                        GetTopItem(id).Sources |= zombie.isMega ? TopSource.Mega : TopSource.Zombie;
                    }
                }
            }
            RankMapTop();
        }

        private static bool IsTopWearable(ItemClothingAsset gear)
        {
            return !gear.isPro && gear.movementSpeedMultiplier >= 0.99f;
        }

        private static void RankMapTop()
        {
            // The best of each kind from any source, and from buildings only.
            float bestGun = 0f, bestBuildingGun = 0f;
            int bestRoom = 0, bestBuildingRoom = 0;
            float bestVest = 1f, bestBuildingVest = 1f, bestHat = 1f, bestBuildingHat = 1f;
            foreach (var item in s_TopItems.Values)
            {
                var asset = Assets.find(EAssetType.ITEM, item.Id) as ItemAsset;
                var building = item.Building >= TopMinimumExpected;
                switch (asset)
                {
                    case ItemGunAsset gun when IsUsefulGun(gun):
                        var score = GunScore(gun);
                        bestGun = Mathf.Max(bestGun, score);
                        if (building)
                        {
                            bestBuildingGun = Mathf.Max(bestBuildingGun, score);
                        }
                        break;
                    case ItemClothingAsset gear when IsTopWearable(gear):
                        switch (gear.type)
                        {
                            case EItemType.SHIRT:
                                s_MapHasShirt |= building;
                                break;
                            case EItemType.PANTS:
                                s_MapHasPants |= building;
                                break;
                            case EItemType.BACKPACK:
                                if (gear is ItemBagAsset bag)
                                {
                                    var room = bag.width * bag.height;
                                    bestRoom = Mathf.Max(bestRoom, room);
                                    if (building)
                                    {
                                        bestBuildingRoom = Mathf.Max(bestBuildingRoom, room);
                                    }
                                }
                                break;
                            case EItemType.VEST:
                                bestVest = Mathf.Min(bestVest, gear.armor);
                                if (building)
                                {
                                    bestBuildingVest = Mathf.Min(bestBuildingVest, gear.armor);
                                }
                                break;
                            case EItemType.HAT:
                                bestHat = Mathf.Min(bestHat, gear.armor);
                                if (building)
                                {
                                    bestBuildingHat = Mathf.Min(bestBuildingHat, gear.armor);
                                }
                                break;
                        }
                        break;
                }
            }
            s_TopGunScore = Mathf.Max(MegaGunScore * StrongGunShare, bestBuildingGun * TopGunBand);
            s_TopBagRoom = bestBuildingRoom;
            s_TopVestArmor = bestBuildingVest;
            s_TopHatArmor = bestBuildingHat;

            // The log: the top pieces by name, where they come from.
            var guns = new List<string>();
            var bags = new List<string>();
            var vests = new List<string>();
            var hats = new List<string>();
            var magazines = new List<string>();
            var later = new List<string>();
            var topGunCalibers = new HashSet<ushort>();
            foreach (var item in s_TopItems.Values.OrderByDescending(entry => entry.Building))
            {
                var asset = Assets.find(EAssetType.ITEM, item.Id) as ItemAsset;
                string? label = null;
                var isTop = false;
                switch (asset)
                {
                    case ItemGunAsset gun when IsUsefulGun(gun) && GunScore(gun) >= bestGun * TopGunBand:
                        label = item.Name + " (" + GunScore(gun).ToString("0") + ", " + DescribeTopSources(item) + ")";
                        guns.Add(label);
                        isTop = true;
                        if (gun.magazineCalibers != null)
                        {
                            topGunCalibers.UnionWith(gun.magazineCalibers);
                        }
                        break;
                    case ItemBagAsset bag when IsTopWearable(bag) && bag.type == EItemType.BACKPACK
                        && bag.width * bag.height >= bestRoom:
                        label = item.Name + " (" + bag.width + "x" + bag.height + ", " + DescribeTopSources(item) + ")";
                        bags.Add(label);
                        isTop = true;
                        break;
                    case ItemClothingAsset vest when IsTopWearable(vest) && vest.type == EItemType.VEST
                        && vest.armor <= bestVest + TopArmorMatch && bestVest < 1f:
                        label = item.Name + " (x" + vest.armor.ToString("0.00") + ", " + DescribeTopSources(item) + ")";
                        vests.Add(label);
                        isTop = true;
                        break;
                    case ItemClothingAsset hat when IsTopWearable(hat) && hat.type == EItemType.HAT
                        && hat.armor <= bestHat + TopArmorMatch && bestHat < 1f:
                        label = item.Name + " (x" + hat.armor.ToString("0.00") + ", " + DescribeTopSources(item) + ")";
                        hats.Add(label);
                        isTop = true;
                        break;
                }
                if (isTop && item.Building < TopMinimumExpected)
                {
                    later.Add(item.Name);
                }
            }
            foreach (var item in s_TopItems.Values)
            {
                if (Assets.find(EAssetType.ITEM, item.Id) is ItemMagazineAsset magazine
                    && magazine.calibers != null
                    && magazine.calibers.Any(topGunCalibers.Contains)
                    && magazine.amount > 30)
                {
                    magazines.Add(item.Name + " (" + magazine.amount + ", " + DescribeTopSources(item) + ")");
                }
            }
            s_TopSummary = "armas " + JoinTop(guns)
                + "; mochila " + JoinTop(bags)
                + "; colete " + JoinTop(vests)
                + "; capacete " + JoinTop(hats)
                + "; pentes grandes (nao exigidos) " + JoinTop(magazines)
                + ". Exigido agora: arma " + s_TopGunScore.ToString("0") + "+, mochila " + s_TopBagRoom + "+ espacos, colete x"
                + s_TopVestArmor.ToString("0.00") + ", capacete x" + s_TopHatArmor.ToString("0.00")
                + (later.Count > 0 ? ". Fica para depois (so no Mega, zumbi ou radiacao): " + string.Join(", ", later) : string.Empty)
                + ". " + DescribeDeadzones() + " " + DescribeKeyDoors();
        }

        private static string JoinTop(List<string> names)
        {
            return names.Count == 0 ? "nenhum" : string.Join(", ", names.Take(6));
        }

        private static string DescribeTopSources(TopItem item)
        {
            var parts = new List<string>();
            if (item.Building > 0f)
            {
                parts.Add("predio " + item.Building.ToString("0.0"));
            }
            if (item.Radiation > 0f)
            {
                parts.Add("radiacao " + item.Radiation.ToString("0.0"));
            }
            if ((item.Sources & TopSource.Mega) != 0)
            {
                parts.Add("Mega");
            }
            if ((item.Sources & TopSource.Zombie) != 0)
            {
                parts.Add("zumbi");
            }
            return string.Join(" ", parts);
        }

        private static string DescribeDeadzones()
        {
            try
            {
                var manager = VolumeManager<DeadzoneVolume, DeadzoneVolumeManager>.Get();
                var volumes = manager?.GetAllVolumes();
                if (volumes == null || volumes.Count == 0)
                {
                    return "Deadzones: nenhuma.";
                }
                var parts = new List<string>();
                foreach (var volume in volumes.Take(8))
                {
                    var at = volume.transform.position;
                    parts.Add(volume.DeadzoneType + " em (" + at.x.ToString("0") + ", " + at.z.ToString("0") + ")");
                }
                return "Deadzones: " + volumes.Count + " (" + string.Join(", ", parts) + ").";
            }
            catch (System.Exception)
            {
                return "Deadzones: nao lidas.";
            }
        }

        // Map objects that open only with an item in the bag (the keys of
        // Arid, Elver and others). Only listed here; going for them is a
        // later step.
        private static string DescribeKeyDoors()
        {
            var keys = new Dictionary<string, int>();
            try
            {
                if (LevelObjects.objects == null)
                {
                    return "Portas com chave: nao lidas.";
                }
                for (byte x = 0; x < Regions.WORLD_SIZE; x++)
                {
                    for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                    {
                        var objects = LevelObjects.objects[x, y];
                        if (objects == null)
                        {
                            continue;
                        }
                        foreach (var levelObject in objects)
                        {
                            var conditions = levelObject?.asset?.interactabilityConditions;
                            if (conditions == null)
                            {
                                continue;
                            }
                            foreach (var condition in conditions)
                            {
                                if (condition is NPCItemCondition needsItem)
                                {
                                    var name = needsItem.GetItemAsset()?.itemName ?? needsItem.id.ToString();
                                    keys.TryGetValue(name, out var count);
                                    keys[name] = count + 1;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                return "Portas com chave: nao lidas.";
            }
            if (keys.Count == 0)
            {
                return "Portas com chave: nenhuma.";
            }
            return "Portas com chave: " + string.Join(", ", keys.Take(12).Select(entry => entry.Key + " x" + entry.Value)) + ".";
        }

        // The worn part and the gun of "equipped"; lacks names the first
        // piece missing.
        private bool HasMapTopGear(ItemGunAsset gun, out string lacks)
        {
            lacks = string.Empty;
            EnsureMapTop();
            if (!s_TopResolved)
            {
                return true;
            }
            if (!s_TopLogged)
            {
                s_TopLogged = true;
                BotLog(LogLevel.Information, "Topo deste mapa (tabelas de loot): {Summary}", s_TopSummary);
            }
            if (GunScore(gun) < s_TopGunScore)
            {
                lacks = "arma do topo (" + gun.itemName + " " + GunScore(gun).ToString("0") + " de " + s_TopGunScore.ToString("0") + ")";
                return false;
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
            if (s_TopBagRoom > 0
                && !(clothing.backpackAsset is ItemBagAsset backpack && backpack.width * backpack.height >= s_TopBagRoom))
            {
                lacks = "mochila do topo (" + s_TopBagRoom + " espacos)";
                return false;
            }
            if (s_TopVestArmor < 1f
                && !(clothing.vestAsset is ItemClothingAsset vest && vest.armor <= s_TopVestArmor + TopArmorMatch))
            {
                lacks = "colete do topo (x" + s_TopVestArmor.ToString("0.00") + ")";
                return false;
            }
            if (s_TopHatArmor < 1f
                && !(clothing.hatAsset is ItemClothingAsset hat && hat.armor <= s_TopHatArmor + TopArmorMatch))
            {
                lacks = "capacete do topo (x" + s_TopHatArmor.ToString("0.00") + ")";
                return false;
            }
            return true;
        }
    }
}
