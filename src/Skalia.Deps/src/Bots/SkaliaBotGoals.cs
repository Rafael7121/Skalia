using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.1: the players play for a goal (doc "bots que pensam como
    // jogadores", 8): they know the gun, bag and ammo they still lack and
    // go where those spawn, farm the military zombies for their drop, camp
    // the Mega where it is born and, just born themselves, walk 130-400m in
    // the first minute straight to some place. The bots wandered: the next
    // area was weighed by distance and loot count only.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- loot tables, measured once per map ----
        // A table's chances, deep spawn assets and all, are read by drawing
        // from it like the server does when it spawns loot.
        private const int GoalTableSamples = 80;
        private static Dictionary<int, Dictionary<ushort, float>>? s_ItemTableProfiles;
        private static readonly Dictionary<byte, Dictionary<ushort, float>> s_ZombieLootProfiles =
            new Dictionary<byte, Dictionary<ushort, float>>();
        private static readonly Dictionary<string, Dictionary<int, int>> s_AreaTableCounts =
            new Dictionary<string, Dictionary<int, int>>();
        private static Dictionary<int, int>? s_MapTableCounts;
        private static int s_MapSpawnCount;

        private static void EnsureItemTableProfiles()
        {
            if (s_ItemTableProfiles != null)
            {
                return;
            }
            s_ItemTableProfiles = new Dictionary<int, Dictionary<ushort, float>>();
            s_MapTableCounts = new Dictionary<int, int>();
            s_MapSpawnCount = 0;
            var tables = LevelItems.tables;
            if (tables == null)
            {
                return;
            }
            for (var index = 0; index < tables.Count; index++)
            {
                var table = tables[index];
                var profile = new Dictionary<ushort, float>();
                if (table != null)
                {
                    for (var sample = 0; sample < GoalTableSamples; sample++)
                    {
                        ushort id;
                        try
                        {
                            id = table.getItem();
                        }
                        catch (System.Exception)
                        {
                            break;
                        }
                        if (id != 0)
                        {
                            profile.TryGetValue(id, out var count);
                            profile[id] = count + 1f / GoalTableSamples;
                        }
                        else if (sample == 9 && profile.Count == 0)
                        {
                            // An empty or broken table: no need for 70 more
                            // errors in the log.
                            break;
                        }
                    }
                }
                s_ItemTableProfiles[index] = profile;
            }
            if (LevelItems.spawns == null)
            {
                return;
            }
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
                        s_MapTableCounts.TryGetValue(spawn.type, out var count);
                        s_MapTableCounts[spawn.type] = count + 1;
                        s_MapSpawnCount++;
                    }
                }
            }
        }

        private static Dictionary<int, int> GetAreaTableCounts(string key, Vector3 center)
        {
            if (s_AreaTableCounts.TryGetValue(key, out var cached))
            {
                return cached;
            }
            var counts = new Dictionary<int, int>();
            if (LevelItems.spawns != null)
            {
                var regions = new List<RegionCoordinate>();
                Regions.getRegionsInRadius(center, InterestAreaRadiusMeters, regions);
                foreach (var coordinate in regions)
                {
                    var spawns = LevelItems.spawns[coordinate.x, coordinate.y];
                    if (spawns == null)
                    {
                        continue;
                    }
                    foreach (var spawn in spawns)
                    {
                        if ((spawn.point - center).GetHorizontalMagnitude() <= InterestAreaRadiusMeters)
                        {
                            counts.TryGetValue(spawn.type, out var count);
                            counts[spawn.type] = count + 1;
                        }
                    }
                }
            }
            s_AreaTableCounts[key] = counts;
            return counts;
        }

        private static Dictionary<ushort, float> GetZombieLootProfile(byte type)
        {
            if (s_ZombieLootProfiles.TryGetValue(type, out var cached))
            {
                return cached;
            }
            var profile = new Dictionary<ushort, float>();
            var tables = LevelZombies.tables;
            if (tables != null && type < tables.Count && tables[type] != null)
            {
                var table = tables[type];
                if (table.lootID != 0)
                {
                    for (var sample = 0; sample < GoalTableSamples; sample++)
                    {
                        ushort id;
                        try
                        {
                            id = SpawnTableTool.ResolveLegacyId(table.lootID, EAssetType.ITEM, () => "bot goal zombie loot");
                        }
                        catch (System.Exception)
                        {
                            break;
                        }
                        if (id != 0)
                        {
                            profile.TryGetValue(id, out var count);
                            profile[id] = count + 1f / GoalTableSamples;
                        }
                    }
                }
                else
                {
                    EnsureItemTableProfiles();
                    if (s_ItemTableProfiles!.TryGetValue(table.lootIndex, out var itemProfile))
                    {
                        profile = itemProfile;
                    }
                }
            }
            s_ZombieLootProfiles[type] = profile;
            return profile;
        }

        // ---- what the bot's gear lacks ----
        // Read once per decision; each item a table can give is worth what it
        // adds: a better gun 3, a bigger bag or vest 2, ammo for a carried gun
        // or a first/better melee 1-1.5, a medical item while under 4 and food
        // or water when low 1. A full bot has no goal (factor 1).
        private struct GoalNeeds
        {
            public bool HasGun;
            public float BestGun;
            public bool HasMelee;
            public float BestMelee;
            public int Medical;
            public bool Food;
            public bool Water;
            public ushort Backpack;
            public ushort Vest;
            public ushort Shirt;
            public ushort Pants;
        }

        private GoalNeeds m_GoalNeeds;
        private float m_GoalNeedsAt = -100f;
        private readonly Dictionary<ushort, float> m_GoalItemValues = new Dictionary<ushort, float>();
        private readonly Dictionary<int, float> m_GoalTableValues = new Dictionary<int, float>();
        private float m_GoalMapValue;

        private void RefreshGoalNeeds(float now)
        {
            if (now - m_GoalNeedsAt < 10f)
            {
                return;
            }
            m_GoalNeedsAt = now;
            var needs = new GoalNeeds
            {
                HasGun = HasWeaponRole(true),
                BestGun = BestCarriedUsableGunScore(),
                HasMelee = HasWeaponRole(false),
                BestMelee = BestCarriedMeleeDamage(),
                Medical = CountInventoryItems(EItemType.MEDICAL),
                Backpack = GetWornBagId(EItemType.BACKPACK),
                Vest = GetWornBagId(EItemType.VEST),
                Shirt = GetWornBagId(EItemType.SHIRT),
                Pants = GetWornBagId(EItemType.PANTS)
            };
            GetConsumableLootNeeds(out needs.Food, out needs.Water);
            m_GoalNeeds = needs;
            m_GoalItemValues.Clear();
            m_GoalTableValues.Clear();
            m_GoalMapValue = -1f;
        }

        private float GetGoalItemValue(ushort id)
        {
            if (m_GoalItemValues.TryGetValue(id, out var cached))
            {
                return cached;
            }
            var value = 0f;
            var needs = m_GoalNeeds;
            var asset = Assets.find(EAssetType.ITEM, id) as ItemAsset;
            switch (asset)
            {
                case null:
                    break;
                case ItemGunAsset gun:
                    if (!IsUsefulGun(gun))
                    {
                        break;
                    }
                    if (!needs.HasGun || needs.BestGun <= 0f || GunScore(gun) >= needs.BestGun * GunUpgradeRatio)
                    {
                        value = 3f;
                        if (s_Settings.TryhardGoals)
                        {
                            // 0.2.2 tryhard: the better the gun, the more it
                            // is worth going for, against the map's middle gun
                            // or, carrying one, against that one.
                            var against = needs.HasGun && needs.BestGun > 0f ? needs.BestGun : GetMapGunReference();
                            value = 3f * Mathf.Clamp(GunScore(gun) / Mathf.Max(1f, against), 0.5f, 4f);
                            // 0.2.2c: without a gun for the Mega, one is worth
                            // twice (the tester: "e 100% necessario achar um").
                            if (needs.BestGun < MegaGunScore && GunScore(gun) >= MegaGunScore)
                            {
                                value *= 2f;
                            }
                        }
                    }
                    break;
                case ItemMagazineAsset magazine:
                    if (needs.HasGun
                        && CountCompatibleMagazines(magazine, out var carried)
                        && carried < MaximumMagazinesPerGun)
                    {
                        value = 1.5f;
                    }
                    break;
                case ItemMeleeAsset melee:
                    if (!IsUsefulMelee(melee))
                    {
                        break;
                    }
                    if (!needs.HasMelee)
                    {
                        value = 1.5f;
                    }
                    else if (MeleeDamage(melee) >= needs.BestMelee * MeleeUpgradeRatio)
                    {
                        value = 1f;
                    }
                    break;
                case ItemMedicalAsset medical:
                    if (needs.Medical < MaximumMedicalItems && MedicalValue(medical) >= MedicalMinimumValue)
                    {
                        value = 1f;
                    }
                    break;
                default:
                    switch (asset.type)
                    {
                        case EItemType.BACKPACK:
                            value = needs.Backpack == 0 || IsBiggerBag(asset, needs.Backpack) ? 2f : 0f;
                            break;
                        case EItemType.VEST:
                            value = needs.Vest == 0 || IsBiggerBag(asset, needs.Vest) ? 2f : 0f;
                            break;
                        case EItemType.SHIRT:
                            value = needs.Shirt == 0 || IsBiggerBag(asset, needs.Shirt) ? 0.5f : 0f;
                            break;
                        case EItemType.PANTS:
                            value = needs.Pants == 0 || IsBiggerBag(asset, needs.Pants) ? 0.5f : 0f;
                            break;
                        case EItemType.FOOD:
                        case EItemType.WATER:
                            if ((needs.Food && IsUsableConsumable(asset, false))
                                || (needs.Water && IsUsableConsumable(asset, true)))
                            {
                                value = 1f;
                            }
                            break;
                    }
                    break;
            }
            m_GoalItemValues[id] = value;
            return value;
        }

        private float GetGoalProfileValue(Dictionary<ushort, float> profile, bool armsOnly)
        {
            var value = 0f;
            foreach (var entry in profile)
            {
                if (armsOnly)
                {
                    var asset = Assets.find(EAssetType.ITEM, entry.Key);
                    if (!(asset is ItemGunAsset) && !(asset is ItemMagazineAsset))
                    {
                        continue;
                    }
                }
                value += entry.Value * GetGoalItemValue(entry.Key);
            }
            return value;
        }

        private float GetGoalTableValue(int table)
        {
            if (m_GoalTableValues.TryGetValue(table, out var cached))
            {
                return cached;
            }
            var value = s_ItemTableProfiles != null && s_ItemTableProfiles.TryGetValue(table, out var profile)
                ? GetGoalProfileValue(profile, false)
                : 0f;
            m_GoalTableValues[table] = value;
            return value;
        }

        // Goal value per spawn point over the whole map: what an average
        // area gives this bot.
        private float GetGoalMapValue()
        {
            if (m_GoalMapValue >= 0f)
            {
                return m_GoalMapValue;
            }
            var total = 0f;
            if (s_MapTableCounts != null)
            {
                foreach (var entry in s_MapTableCounts)
                {
                    total += entry.Value * GetGoalTableValue(entry.Key);
                }
            }
            m_GoalMapValue = s_MapSpawnCount > 0 ? total / s_MapSpawnCount : 0f;
            return m_GoalMapValue;
        }

        // How much more of what the bot lacks this area gives than an average
        // one, per spawn point: 0.4 to 3. The weight of the area (loot per
        // distance squared, ConsiderExplorationCandidate) is multiplied by it.
        private const float GoalFactorMinimum = 0.4f;
        private const float GoalFactorMaximum = 3f;

        private float GetAreaGoalFactor(string key, Vector3 center)
        {
            if (!s_Settings.GoalLoot)
            {
                return 1f;
            }
            EnsureItemTableProfiles();
            RefreshGoalNeeds(m_BehaviorNow);
            var mapValue = GetGoalMapValue();
            if (mapValue < 0.005f)
            {
                return 1f;
            }
            var counts = GetAreaTableCounts(key, center);
            var spawns = 0;
            var total = 0f;
            foreach (var entry in counts)
            {
                spawns += entry.Value;
                total += entry.Value * GetGoalTableValue(entry.Key);
            }
            if (spawns == 0)
            {
                return s_Settings.TryhardGoals ? TryhardFactorMinimum : GoalFactorMinimum;
            }
            return s_Settings.TryhardGoals
                ? Mathf.Clamp(total / spawns / mapValue, TryhardFactorMinimum, TryhardFactorMaximum)
                : Mathf.Clamp(total / spawns / mapValue, GoalFactorMinimum, GoalFactorMaximum);
        }

        // 0.2.2 tryhard (the tester: "quero que ele rushe os melhores
        // equipamentos"): the goal decides, distance only breaks ties. The
        // area weight takes the factor squared (0.06-36): a military base 3x
        // farther than a town wins once it is worth 3x more for the gear.
        private const float TryhardFactorMinimum = 0.25f;
        private const float TryhardFactorMaximum = 6f;

        private float GetAreaGoalWeight(string key, Vector3 center)
        {
            var factor = GetAreaGoalFactor(key, center);
            return s_Settings.TryhardGoals ? factor * factor : factor;
        }

        // The middle useful gun the map's loot tables give, by GunScore.
        private static float s_MapGunReference = -1f;

        private static float GetMapGunReference()
        {
            if (s_MapGunReference > 0f)
            {
                return s_MapGunReference;
            }
            EnsureItemTableProfiles();
            var scores = new List<float>();
            var seen = new HashSet<ushort>();
            foreach (var profile in s_ItemTableProfiles!.Values)
            {
                foreach (var id in profile.Keys)
                {
                    if (seen.Add(id)
                        && Assets.find(EAssetType.ITEM, id) is ItemGunAsset gun
                        && IsUsefulGun(gun))
                    {
                        scores.Add(GunScore(gun));
                    }
                }
            }
            if (scores.Count == 0)
            {
                s_MapGunReference = 100f;
                return s_MapGunReference;
            }
            scores.Sort();
            s_MapGunReference = Mathf.Max(1f, scores[scores.Count / 2]);
            return s_MapGunReference;
        }

        private string DescribeGoalNeeds()
        {
            var needs = m_GoalNeeds;
            var text = new StringBuilder();
            void Add(string part)
            {
                if (text.Length > 0)
                {
                    text.Append(", ");
                }
                text.Append(part);
            }
            if (!needs.HasGun)
            {
                Add("arma de fogo");
            }
            else if (s_Settings.TryhardGoals && needs.BestGun < MegaGunScore)
            {
                Add("arma para o Mega");
            }
            if (!needs.HasMelee)
            {
                Add("arma branca");
            }
            if (needs.Backpack == 0)
            {
                Add("mochila");
            }
            if (needs.Vest == 0)
            {
                Add("colete");
            }
            if (needs.Medical < MaximumMedicalItems)
            {
                Add("cura (" + needs.Medical + "/" + MaximumMedicalItems + ")");
            }
            if (needs.Food)
            {
                Add("comida");
            }
            if (needs.Water)
            {
                Add("agua");
            }
            return text.Length == 0 ? "so melhorias" : text.ToString();
        }

        // ---- military zombies for their drop ----
        // Without a gun, or short of ammo for the one carried, a zombie whose
        // drop is worth it (a gun or ammo the bot uses in a quarter of the
        // draws or more) is worth walking up to within 30m.
        private const float GoalZombieMeters = 30f;
        private const float GoalZombieMinimumValue = 0.25f;
        private readonly Dictionary<byte, float> m_GoalZombieValues = new Dictionary<byte, float>();
        private float m_GoalZombieValuesAt = -100f;

        private bool IsGoalZombie(Zombie zombie, float horizontal)
        {
            if (!s_Settings.GoalLoot
                || horizontal > GoalZombieMeters
                || zombie.isMega
                || IsBossZombie(zombie)
                || IsFireZombie(zombie)
                || IsUnarmed()
                || IsLowHealth())
            {
                return false;
            }
            var now = m_BehaviorNow;
            if (now - m_GoalZombieValuesAt > 10f)
            {
                m_GoalZombieValuesAt = now;
                m_GoalZombieValues.Clear();
            }
            if (!m_GoalZombieValues.TryGetValue(zombie.type, out var value))
            {
                RefreshGoalNeeds(now);
                value = GetGoalProfileValue(GetZombieLootProfile(zombie.type), true);
                m_GoalZombieValues[zombie.type] = value;
            }
            return value >= GoalZombieMinimumValue;
        }

        // ---- straight to the goal after being born ----
        private float m_RespawnRushUntil;
        private const float RespawnRushSeconds = 60f;

        private void StartRespawnRush(float now)
        {
            m_RespawnRushUntil = s_Settings.RespawnRush ? now + RespawnRushSeconds : 0f;
            m_RespawnGraceUntil = now + 5f;
        }

        // ---- the Mega where it is born ----
        // The players killed 14 Megas in ~2h on 11-08 by waiting at its three
        // spawns (the military base, regions 0 and 20) and shooting it 16-83s
        // after it came back. A bot ready for the hunt (CanJoinBossHunt: gun,
        // rounds, health) with a live Mega within 600m goes there: to a spot
        // 45-60m from the spawn that sees it, higher if it can. The hunt itself
        // starts on its own once the Mega is in sight within 70m.
        private const float MegaTripMaxMeters = 600f;
        private const float MegaTripCheckSeconds = 30f;
        private const float MegaTripCooldownSeconds = 300f;
        private static List<Vector3>? s_MegaSpawns;
        private float m_NextMegaTripCheckAt;
        private float m_MegaTripUntil;

        // 0.2.2u: on Skalia3 the Megas are placed by SkaliaDeps (one a
        // region), not by a zombie table of the map, and the table search
        // found none. Without table spawns the anchors are where living
        // Megas were first seen standing (they stay round their spot and
        // come back there on respawn).
        private static readonly List<Vector3> s_MegaAnchors = new List<Vector3>();
        private static float s_NextMegaAnchorScanAt;

        private static List<Vector3> GetMegaSpawns()
        {
            var tableSpawns = GetMegaTableSpawns();
            if (tableSpawns.Count > 0)
            {
                return tableSpawns;
            }
            var realNow = Time.realtimeSinceStartup;
            if (realNow >= s_NextMegaAnchorScanAt && ZombieManager.regions != null)
            {
                s_NextMegaAnchorScanAt = realNow + 10f;
                foreach (var region in ZombieManager.regions)
                {
                    if (region?.zombies == null)
                    {
                        continue;
                    }
                    foreach (var zombie in region.zombies)
                    {
                        if (zombie == null
                            || zombie.isDead
                            || !zombie.isMega
                            || zombie.isHunting
                            || !zombie.gameObject.activeInHierarchy)
                        {
                            continue;
                        }
                        var point = zombie.transform.position;
                        var known = false;
                        foreach (var anchor in s_MegaAnchors)
                        {
                            if ((anchor - point).GetHorizontalMagnitude() < 80f)
                            {
                                known = true;
                                break;
                            }
                        }
                        if (!known)
                        {
                            s_MegaAnchors.Add(point);
                        }
                    }
                }
            }
            return s_MegaAnchors;
        }

        private static List<Vector3> GetMegaTableSpawns()
        {
            if (s_MegaSpawns != null)
            {
                return s_MegaSpawns;
            }
            s_MegaSpawns = new List<Vector3>();
            var tables = LevelZombies.tables;
            var spawns = LevelZombies.spawns;
            if (tables == null || spawns == null)
            {
                return s_MegaSpawns;
            }
            for (byte x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    var list = spawns[x, y];
                    if (list == null)
                    {
                        continue;
                    }
                    foreach (var spawn in list)
                    {
                        if (spawn.type < tables.Count && tables[spawn.type] != null && tables[spawn.type].isMega)
                        {
                            s_MegaSpawns.Add(spawn.point);
                        }
                    }
                }
            }
            return s_MegaSpawns;
        }

        private bool TryPickMegaTrip(Vector3 position, float now, out Vector3 spot, out string name)
        {
            spot = Vector3.zero;
            name = string.Empty;
            if (!s_Settings.MegaSpawnHunt
                || m_BossHuntActive
                || now < m_BossHuntBlockedUntil
                || now < m_NextMegaTripCheckAt
                || ZombieManager.regions == null)
            {
                return false;
            }
            m_NextMegaTripCheckAt = now + MegaTripCheckSeconds;
            Zombie? mega = null;
            // 0.2.2t: equipped, the tryhard crosses the map for it.
            var megaDistance = Tryhard ? TravelMaximumMeters : MegaTripMaxMeters;
            foreach (var region in ZombieManager.regions)
            {
                if (region?.zombies == null)
                {
                    continue;
                }
                foreach (var zombie in region.zombies)
                {
                    if (zombie == null
                        || zombie.isDead
                        || !zombie.isMega
                        || !zombie.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    var distance = (zombie.transform.position - position).GetHorizontalMagnitude();
                    if (distance < megaDistance)
                    {
                        megaDistance = distance;
                        mega = zombie;
                    }
                }
            }
            if (mega == null || megaDistance <= BossHuntStartMeters)
            {
                // Within 70m the hunt decides by itself.
                return false;
            }
            if (!CanJoinBossHunt(mega, now, out var damage, out var detail))
            {
                LogBossHuntRefusal(now, mega, "viagem ao ponto do Mega: " + detail);
                return false;
            }
            var team = GetBossHuntTeamDamage(mega, damage);
            if (damage < mega.health * BossHuntOwnShare || team < mega.health * GetBossHuntMargin())
            {
                return false;
            }
            // Its spawn: the Mega walks around it; without one near, where it
            // stands.
            var anchor = mega.transform.position;
            var nearestSpawn = 80f;
            foreach (var megaSpawn in GetMegaSpawns())
            {
                var gap = (megaSpawn - anchor).GetHorizontalMagnitude();
                if (gap < nearestSpawn)
                {
                    nearestSpawn = gap;
                    anchor = megaSpawn;
                }
            }
            if (!TryFindMegaLookout(anchor, position, out spot))
            {
                BotLog(LogLevel.Information,
                    "Pronto para o Mega a {Distance:0}m, mas nenhum ponto a 45-60m do nascimento dele em {Spawn} tem visao; seguindo a exploracao.",
                    megaDistance,
                    FormatPosition(anchor));
                return false;
            }
            name = "Mega (ponto de nascimento)";
            m_MegaTripUntil = now + MegaTripCooldownSeconds;
            m_NextMegaTripCheckAt = now + MegaTripCooldownSeconds;
            BotLog(LogLevel.Information,
                "Indo cacar o Mega no ponto de nascimento {Spawn}: Mega a {Distance:0}m (vida {BossHealth}), posicao de tiro {Spot} a {Gap:0}m dele, dano esperado {Damage:0}.",
                FormatPosition(anchor),
                megaDistance,
                mega.health,
                FormatPosition(spot),
                (spot - anchor).GetHorizontalMagnitude(),
                team);
            return true;
        }

        private static bool TryFindMegaLookout(Vector3 anchor, Vector3 botPosition, out Vector3 spot)
        {
            spot = Vector3.zero;
            var bestScore = float.NegativeInfinity;
            var target = anchor + Vector3.up * 1.5f;
            foreach (var radius in new[] { 52f, 45f, 60f })
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    var candidate = anchor + direction * radius;
                    Vector3 onGround;
                    if (SkaliaBotUnityNavmesh.MapFilled)
                    {
                        if (!SkaliaBotUnityNavmesh.TrySnap(candidate, 6f, out onGround))
                        {
                            continue;
                        }
                    }
                    else if (AstarPath.active != null)
                    {
                        var nearest = AstarPath.active.GetNearest(candidate, NNConstraint.Walkable);
                        if (nearest.node == null || (nearest.position - candidate).GetHorizontalMagnitude() > 6f)
                        {
                            continue;
                        }
                        onGround = nearest.position;
                    }
                    else
                    {
                        continue;
                    }
                    var gap = (onGround - anchor).GetHorizontalMagnitude();
                    if (gap < 40f
                        || gap > 65f
                        || WaterUtility.isPointUnderwater(onGround + Vector3.up * 0.5f)
                        || Physics.Linecast(
                            onGround + Vector3.up * 1.6f,
                            target,
                            RayMasks.BLOCK_COLLISION,
                            QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    // 0.2.2x1: a spot the prone eye still sees from counts more.
                    var proneLine = !Physics.Linecast(
                        onGround + Vector3.up * 0.55f,
                        target,
                        RayMasks.BLOCK_COLLISION,
                        QueryTriggerInteraction.Ignore);
                    var score = Mathf.Clamp(onGround.y - anchor.y, 0f, 8f) * 1.5f
                        + (proneLine ? 4f : 0f)
                        - (onGround - botPosition).GetHorizontalMagnitude() / 50f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        spot = onGround;
                    }
                }
            }
            return bestScore > float.NegativeInfinity;
        }
    }
}
