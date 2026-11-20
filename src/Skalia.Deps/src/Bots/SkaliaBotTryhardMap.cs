using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Framework.Devkit;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t (doc 11.1, 11.2, 11.15): the tryhard knows the map. The areas
    // used to come only from the map's 16 name labels, 85m each: most of the
    // military loot of Washington lies outside those circles. The strong
    // spots are measured from the spawn tables (what a table gives, not what
    // it is called), grouped by 70m, and the tryhard goes: strong spot not
    // cleaned yet -> the Mega when equipped -> the hunting ground (the
    // biggest town), swept along its roads up to the Mega's spawn.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private sealed class StrongSpot
        {
            public Vector3 Center;
            public int Points;
            public float Value;
            public bool HasMega;
            public string Name = string.Empty;
        }

        private const float StrongTableValue = 0.3f;
        private const float StrongGunShare = 0.75f;
        private const float StrongClusterMeters = 70f;
        private const int StrongClusterMinimumPoints = 4;
        private const float StrongSpotMegaMeters = 150f;
        private const float HuntSweepRadiusMeters = 150f;
        private const float HuntCorridorMeters = 90f;
        private const float HuntMegaSpawnMaxMeters = 450f;
        private const float HuntPointSpacingMeters = 18f;
        private const int HuntMaximumPoints = 70;
        private const float HuntLootNearMeters = 25f;
        private const float HuntLootPassMeters = 8f;

        private static List<StrongSpot>? s_StrongSpots;
        private static Dictionary<int, float>? s_StrongTableValues;
        private static bool s_StrongSpotsLogged;
        private static bool s_HuntGroundResolved;
        private static bool s_HasHuntGround;
        private static Vector3 s_HuntCenter;
        private static string s_HuntName = string.Empty;
        private static string s_HuntKey = string.Empty;
        private float m_NextTryhardNeedsLogAt;

        private bool IsHuntSweep => Tryhard
            && m_HasInterestArea
            && s_HasHuntGround
            && m_InterestAreaKey == s_HuntKey;

        private static float GetStrongItemValue(ItemAsset asset, HashSet<ushort> strongCalibers)
        {
            switch (asset)
            {
                case ItemGunAsset gun:
                    if (!IsUsefulGun(gun))
                    {
                        return 0f;
                    }
                    var share = GunScore(gun) / MegaGunScore;
                    return share >= StrongGunShare ? 3f * Mathf.Min(share, 2f) : 0.2f * share;
                case ItemMagazineAsset magazine:
                    if (magazine.calibers != null)
                    {
                        foreach (var caliber in magazine.calibers)
                        {
                            if (strongCalibers.Contains(caliber))
                            {
                                return 1f;
                            }
                        }
                    }
                    return 0f;
                case ItemClothingAsset clothing:
                    return clothing.armor < 0.9f ? 1.5f : clothing.armor < 0.96f ? 0.6f : 0f;
                case ItemMedicalAsset _:
                    return 0.2f;
            }
            switch (asset.type)
            {
                case EItemType.SIGHT:
                case EItemType.TACTICAL:
                case EItemType.GRIP:
                case EItemType.BARREL:
                    return 0.3f;
                case EItemType.SUPPLY:
                    return IsStrongAmmoBox(asset.id) ? 1f : 0f;
                default:
                    return 0f;
            }
        }

        private static void EnsureStrongSpots()
        {
            if (s_StrongSpots != null)
            {
                return;
            }
            s_StrongSpots = new List<StrongSpot>();
            s_StrongTableValues = new Dictionary<int, float>();
            EnsureItemTableProfiles();
            if (s_ItemTableProfiles == null || LevelItems.spawns == null)
            {
                return;
            }
            var strongCalibers = new HashSet<ushort>();
            foreach (var profile in s_ItemTableProfiles.Values)
            {
                foreach (var id in profile.Keys)
                {
                    if (Assets.find(EAssetType.ITEM, id) is ItemGunAsset gun
                        && IsUsefulGun(gun)
                        && GunScore(gun) >= MegaGunScore * StrongGunShare
                        && gun.magazineCalibers != null)
                    {
                        foreach (var caliber in gun.magazineCalibers)
                        {
                            strongCalibers.Add(caliber);
                        }
                    }
                }
            }
            foreach (var entry in s_ItemTableProfiles)
            {
                var value = 0f;
                foreach (var item in entry.Value)
                {
                    if (Assets.find(EAssetType.ITEM, item.Key) is ItemAsset asset)
                    {
                        value += item.Value * GetStrongItemValue(asset, strongCalibers);
                    }
                }
                s_StrongTableValues[entry.Key] = value;
            }
            // Group the spawn points of the strong tables.
            var centers = new List<Vector3>();
            var sums = new List<Vector3>();
            var counts = new List<int>();
            var values = new List<float>();
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
                        if (!s_StrongTableValues.TryGetValue(spawn.type, out var tableValue)
                            || tableValue < StrongTableValue
                            || IsInRadiation(spawn.point, 10f))
                        {
                            continue;
                        }
                        var group = -1;
                        var nearest = StrongClusterMeters;
                        for (var index = 0; index < centers.Count; index++)
                        {
                            var gap = (centers[index] - spawn.point).GetHorizontalMagnitude();
                            if (gap < nearest)
                            {
                                nearest = gap;
                                group = index;
                            }
                        }
                        if (group < 0)
                        {
                            centers.Add(spawn.point);
                            sums.Add(spawn.point);
                            counts.Add(1);
                            values.Add(tableValue);
                        }
                        else
                        {
                            sums[group] += spawn.point;
                            counts[group]++;
                            values[group] += tableValue;
                            centers[group] = sums[group] / counts[group];
                        }
                    }
                }
            }
            // Two groups that drifted next to each other are one place.
            for (var a = 0; a < centers.Count; a++)
            {
                for (var b = centers.Count - 1; b > a; b--)
                {
                    if ((centers[a] - centers[b]).GetHorizontalMagnitude() >= StrongClusterMeters)
                    {
                        continue;
                    }
                    sums[a] += sums[b];
                    counts[a] += counts[b];
                    values[a] += values[b];
                    centers[a] = sums[a] / counts[a];
                    centers.RemoveAt(b);
                    sums.RemoveAt(b);
                    counts.RemoveAt(b);
                    values.RemoveAt(b);
                }
            }
            var megaSpawns = GetMegaSpawns();
            var locations = LocationDevkitNodeSystem.Get()?.GetAllNodes();
            for (var index = 0; index < centers.Count; index++)
            {
                if (counts[index] < StrongClusterMinimumPoints)
                {
                    continue;
                }
                var spot = new StrongSpot
                {
                    Center = centers[index],
                    Points = counts[index],
                    Value = values[index]
                };
                foreach (var megaSpawn in megaSpawns)
                {
                    if ((megaSpawn - spot.Center).GetHorizontalMagnitude() <= StrongSpotMegaMeters)
                    {
                        spot.HasMega = true;
                        break;
                    }
                }
                var nearName = string.Empty;
                var nearMeters = 500f;
                if (locations != null)
                {
                    foreach (var location in locations)
                    {
                        var gap = (location.transform.position - spot.Center).GetHorizontalMagnitude();
                        if (gap < nearMeters && !string.IsNullOrWhiteSpace(location.locationName))
                        {
                            nearMeters = gap;
                            nearName = location.locationName;
                        }
                    }
                }
                spot.Name = nearName.Length == 0
                    ? "ponto forte"
                    : nearMeters <= InterestAreaRadiusMeters
                        ? "ponto forte de " + nearName
                        : "ponto forte a " + Mathf.RoundToInt(nearMeters) + "m de " + nearName;
                s_StrongSpots.Add(spot);
            }
            s_StrongSpots.Sort((left, right) => right.Value.CompareTo(left.Value));
        }

        private void LogStrongSpotsOnce()
        {
            if (s_StrongSpotsLogged)
            {
                return;
            }
            s_StrongSpotsLogged = true;
            var tables = new StringBuilder();
            if (s_StrongTableValues != null)
            {
                foreach (var entry in s_StrongTableValues)
                {
                    var spawnCount = 0;
                    s_MapTableCounts?.TryGetValue(entry.Key, out spawnCount);
                    if (spawnCount == 0)
                    {
                        continue;
                    }
                    if (tables.Length > 0)
                    {
                        tables.Append("; ");
                    }
                    tables.Append('#').Append(entry.Key)
                        .Append(' ').Append(entry.Value.ToString("0.00"))
                        .Append(" (").Append(spawnCount).Append(" pontos")
                        .Append(entry.Value >= StrongTableValue ? ", forte)" : ")");
                }
            }
            BotLog(LogLevel.Information,
                "Mapa do tryhard: nota de cada tabela de loot (forte a partir de {Threshold:0.00}): {Tables}.",
                StrongTableValue,
                tables.ToString());
            var spots = s_StrongSpots ?? new List<StrongSpot>();
            BotLog(LogLevel.Information,
                "Mapa do tryhard: {Count} ponto(s) forte(s), {Megas} nascimento(s) de Mega, ponto de caca {Hunt}.",
                spots.Count,
                GetMegaSpawns().Count,
                s_HasHuntGround ? s_HuntName + " em " + FormatPosition(s_HuntCenter) : "nenhum");
            foreach (var spot in spots)
            {
                BotLog(LogLevel.Information,
                    "Mapa do tryhard: {Name} em {Center}: {Points} pontos de loot, nota {Value:0.0}, Mega {Mega}.",
                    spot.Name,
                    FormatPosition(spot.Center),
                    spot.Points,
                    spot.Value,
                    spot.HasMega);
            }
        }

        // The hunting ground: the place named in the config, or the place
        // with the most loot points (the biggest town).
        private static void EnsureHuntGround()
        {
            if (s_HuntGroundResolved)
            {
                return;
            }
            s_HuntGroundResolved = true;
            var locations = LocationDevkitNodeSystem.Get()?.GetAllNodes();
            if (locations == null)
            {
                return;
            }
            // 0.2.2u: without a name in the config, Seattle (the tester: the
            // kill point); the count of loot points picked Heritage Valley.
            // A name no place of the map has falls back to the count.
            var wanted = (s_Settings.HuntArea ?? string.Empty).Trim();
            if (wanted.Length == 0)
            {
                wanted = "Seattle";
            }
            ResolveHuntGround(locations, wanted);
            if (!s_HasHuntGround)
            {
                ResolveHuntGround(locations, string.Empty);
            }
        }

        private static void ResolveHuntGround(IEnumerable<LocationDevkitNode> locations, string wanted)
        {
            var best = -1;
            foreach (var location in locations)
            {
                if (string.IsNullOrWhiteSpace(location.locationName))
                {
                    continue;
                }
                var point = location.transform.position;
                var key = BuildExplorationDestinationKey(location.locationName, point);
                int score;
                if (wanted.Length > 0)
                {
                    if (location.locationName.IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    score = int.MaxValue;
                }
                else
                {
                    score = GetAreaSpawnCount(key, point);
                }
                if (score > best && !IsInRadiation(point, 15f))
                {
                    best = score;
                    s_HasHuntGround = true;
                    s_HuntCenter = point;
                    s_HuntName = location.locationName;
                    s_HuntKey = key;
                }
            }
        }

        // Strong gun that works, two magazines' worth of rounds, bandages.
        private const float TryhardMegaHealthGuess = 2999f;

        private bool IsTryhardEquipped(out string lacks)
        {
            lacks = string.Empty;
            // 0.1.0-y17.24 (the tester, 20/11: "no modo arena ele so tem que
            // pegar qualquer arma que aparecer e matar").
            if (IsArenaMap)
            {
                if (TryGetKeeperGun(out _, out _, out _))
                {
                    return true;
                }
                lacks = "qualquer arma (arena)";
                return false;
            }
            if (!TryGetKeeperGun(out _, out _, out var gun)
                || GunScore(gun) < MegaGunScore * StrongGunShare)
            {
                lacks = "arma forte";
                return false;
            }
            // 0.2.2v (doc 13.3, item 1): ready = enough rounds to kill the
            // Mega by the hunt's own count. thigas (Sabertooth, 4 magazines)
            // was "equipped" with 1267 to 2376 of the 4499 the hunt asked.
            var megaHealth = TryFindNearestLivingMega(m_BehaviorPosition, float.MaxValue, out var mega, out _)
                ? (float)mega.health
                : TryhardMegaHealthGuess;
            var huntDamage = GetBossHuntDamageFor(megaHealth, out _, out var huntGun, out _, out _);
            var wanted = megaHealth * GetBossHuntMargin();
            if (huntGun == null)
            {
                lacks = "automatica de calibre Military ou Ranger que tire " + (megaHealth * BossHuntMagazineShare).ToString("0") + " do Mega num pente";
                return false;
            }
            if (huntDamage < wanted)
            {
                lacks = "municao para o Mega (" + huntGun.itemName + ": dano " + huntDamage.ToString("0")
                    + " de " + wanted.ToString("0") + ")";
                return false;
            }
            if (GetBandageStock() < 2f)
            {
                lacks = "curativo";
                return false;
            }
            // 0.1.0-y17.24: and the top of the map, gun and worn gear
            // (SkaliaBotFullLootY).
            return HasMapTopGear(gun, out lacks);
        }

        // 0.2.2v (doc 13.3, item 7): when nothing of the tryhard is reachable
        // from here (Tadeu born on the Scorpion-7 island, 18:49:49) the old
        // rule picks the area near by, and the log says why; the trip out of
        // there then leads toward the goal (TryGetTryhardGoal).
        private float m_NextTryhardFallbackLogAt;

        private bool TryPickTryhardDestination(
            Vector3 position,
            float now,
            GraphNode originNode,
            ref Vector3 chosen,
            ref string chosenKey,
            ref int candidateCount,
            ref int blacklistedCount,
            ref int missingNodeCount,
            ref int disconnectedCount)
        {
            if (TryPickTryhardDestinationCore(position, now, originNode, ref chosen, ref chosenKey,
                    ref candidateCount, ref blacklistedCount, ref missingNodeCount, ref disconnectedCount))
            {
                return true;
            }
            GetConsumableLootNeeds(out var needFood, out var needWater);
            if (!needFood && !needWater && now >= m_NextTryhardFallbackLogAt)
            {
                m_NextTryhardFallbackLogAt = now + 60f;
                BotLog(LogLevel.Information,
                    "Tryhard: nenhum ponto forte nem {Hunt} alcancavel pela malha daqui ({Blacklisted} na lista de ignorados, {Missing} sem no, {Disconnected} desconectados); area proxima pela regra comum e depois viagem rumo ao objetivo.",
                    s_HasHuntGround ? s_HuntName : "ponto de caca",
                    blacklistedCount,
                    missingNodeCount,
                    disconnectedCount);
            }
            return false;
        }

        // The pick of the tryhard, in order. True when it chose.
        private bool TryPickTryhardDestinationCore(
            Vector3 position,
            float now,
            GraphNode originNode,
            ref Vector3 chosen,
            ref string chosenKey,
            ref int candidateCount,
            ref int blacklistedCount,
            ref int missingNodeCount,
            ref int disconnectedCount)
        {
            EnsureStrongSpots();
            EnsureHuntGround();
            LogStrongSpotsOnce();
            GetConsumableLootNeeds(out var needFood, out var needWater);
            if (needFood || needWater)
            {
                if (now >= m_NextTryhardNeedsLogAt)
                {
                    m_NextTryhardNeedsLogAt = now + 60f;
                    BotLog(LogLevel.Information,
                        "Tryhard: sem {What} na mochila; escolhendo area pela regra comum ate achar.",
                        needFood && needWater ? "comida e agua" : needFood ? "comida" : "agua");
                }
                return false;
            }
            var equipped = IsTryhardEquipped(out var lacks);
            if (equipped && now < m_HuntBoredUntil)
            {
                // 0.2.2x1 (doc 16.5, item 14): the sweep gave nothing for 4 min.
                equipped = false;
                lacks = "variar (a varredura de " + s_HuntName + " ficou 4 min sem nada)";
            }
            if (!equipped && s_StrongSpots!.Count > 0)
            {
                var ranked = new List<KeyValuePair<float, StrongSpot>>();
                // 0.1.0-y17.25: and the ones in a deadzone it can go into
                // (SkaliaBotRadiationY).
                foreach (var spot in s_StrongSpots.Concat(GetEnterableRadiationSpots()))
                {
                    var distance = (spot.Center - position).GetHorizontalMagnitude();
                    var rank = spot.Value / (distance + TryhardSpotDistanceWeight);
                    if (IsAreaClaimedByOtherBot(BuildExplorationDestinationKey(spot.Name, spot.Center), now))
                    {
                        rank *= 0.2f;
                    }
                    // 0.2.2x2 (doc 16.7, item 5): 4 of the 7 deaths were at
                    // the Olympia Mega; a bot with no automatic to take it on
                    // looks for one where there is no Mega first.
                    if (spot.HasMega && !HasAutoGunCached())
                    {
                        rank *= 0.1f;
                    }
                    ranked.Add(new KeyValuePair<float, StrongSpot>(rank, spot));
                }
                ranked.Sort((left, right) => right.Key.CompareTo(left.Key));
                foreach (var entry in ranked)
                {
                    var spot = entry.Value;
                    var before = candidateCount;
                    m_ExplorationWeightTotal = 0f;
                    ConsiderExplorationCandidate(
                        position,
                        spot.Center,
                        spot.Name,
                        0f,
                        TravelMaximumMeters,
                        now,
                        originNode,
                        ref chosen,
                        ref m_InterestAreaName,
                        ref chosenKey,
                        ref candidateCount,
                        ref blacklistedCount,
                        ref missingNodeCount,
                        ref disconnectedCount,
                        false);
                    if (candidateCount > before)
                    {
                        m_StrongAreaKey = chosenKey;
                        BotLog(LogLevel.Information,
                            "Tryhard: falta {Lacks}; indo ao {Name} a {Distance:0}m ({Points} pontos de loot, nota {Value:0.0}, Mega {Mega}).",
                            lacks,
                            spot.Name,
                            (spot.Center - position).GetHorizontalMagnitude(),
                            spot.Points,
                            spot.Value,
                            spot.HasMega);
                        return true;
                    }
                }
            }
            if (!s_HasHuntGround)
            {
                return false;
            }
            // A sweep that ended does not close the hunting ground.
            m_ExplorationDestinationBlacklist.Remove(s_HuntKey);
            var huntDistance = (s_HuntCenter - position).GetHorizontalMagnitude();
            if (huntDistance <= TravelMaximumMeters - 20f)
            {
                var before = candidateCount;
                m_ExplorationWeightTotal = 0f;
                ConsiderExplorationCandidate(
                    position,
                    s_HuntCenter,
                    s_HuntName,
                    0f,
                    TravelMaximumMeters,
                    now,
                    originNode,
                    ref chosen,
                    ref m_InterestAreaName,
                    ref chosenKey,
                    ref candidateCount,
                    ref blacklistedCount,
                    ref missingNodeCount,
                    ref disconnectedCount,
                    false,
                    true);
                if (candidateCount > before)
                {
                    BotLog(LogLevel.Information,
                        "Tryhard: {Why}; indo cacar em {Name} a {Distance:0}m, varrendo as ruas.",
                        equipped ? "equipado" : "falta " + lacks + " e nao ha ponto forte por limpar ao alcance",
                        s_HuntName,
                        huntDistance);
                    return true;
                }
                return false;
            }
            // Too far for one trip: the place on the way that gets closest.
            var locations = LocationDevkitNodeSystem.Get()?.GetAllNodes();
            if (locations == null)
            {
                return false;
            }
            var steps = new List<KeyValuePair<float, LocationDevkitNode>>();
            foreach (var location in locations)
            {
                var point = location.transform.position;
                var left = (point - s_HuntCenter).GetHorizontalMagnitude();
                if (string.IsNullOrWhiteSpace(location.locationName)
                    || (point - position).GetHorizontalMagnitude() > TravelMaximumMeters - 50f
                    || left > huntDistance - 150f)
                {
                    continue;
                }
                steps.Add(new KeyValuePair<float, LocationDevkitNode>(left, location));
            }
            steps.Sort((left, right) => left.Key.CompareTo(right.Key));
            foreach (var step in steps)
            {
                var before = candidateCount;
                m_ExplorationWeightTotal = 0f;
                ConsiderExplorationCandidate(
                    position,
                    step.Value.transform.position,
                    step.Value.locationName,
                    35f,
                    TravelMaximumMeters,
                    now,
                    originNode,
                    ref chosen,
                    ref m_InterestAreaName,
                    ref chosenKey,
                    ref candidateCount,
                    ref blacklistedCount,
                    ref missingNodeCount,
                    ref disconnectedCount,
                    false,
                    true);
                if (candidateCount > before)
                {
                    BotLog(LogLevel.Information,
                        "Tryhard: {Hunt} fica a {Distance:0}m, longe para uma viagem; passando por {Step} ({Left:0}m de la).",
                        s_HuntName,
                        huntDistance,
                        step.Value.locationName,
                        step.Key);
                    return true;
                }
            }
            return false;
        }

        // 0.2.2v (doc 13.3, item 6): the hunting ground is never "done". A
        // sweep that ends starts over (the bot was sent to farms after the
        // Seattle houses were visited). True when it restarted.
        private bool TryRestartHuntSweep(string chosenKey, bool noLootHere)
        {
            if (!Tryhard || noLootHere || !s_HasHuntGround || chosenKey != s_HuntKey)
            {
                return false;
            }
            foreach (var point in m_AreaLootPoints)
            {
                m_VisitedPatrolPoints.Remove(point);
            }
            BotLog(LogLevel.Information,
                "Ponto de caca {Name}: varredura completa ({Count} pontos); recomecando em vez de marcar a area como vasculhada.",
                s_HuntName,
                m_AreaLootPoints.Count);
            return true;
        }

        // Where the tryhard wants to end up: the strong spot it would pick,
        // or the hunting ground once equipped (or with no strong spot left).
        private bool TryGetTryhardGoal(Vector3 position, float now, out Vector3 goal, out string name)
        {
            goal = Vector3.zero;
            name = string.Empty;
            if (!Tryhard)
            {
                return false;
            }
            EnsureStrongSpots();
            EnsureHuntGround();
            if ((!IsTryhardEquipped(out _) || now < m_HuntBoredUntil) && s_StrongSpots != null)
            {
                var best = 0f;
                foreach (var spot in s_StrongSpots)
                {
                    var key = BuildExplorationDestinationKey(spot.Name, spot.Center);
                    if (m_ExplorationDestinationBlacklist.TryGetValue(key, out var until) && now < until)
                    {
                        continue;
                    }
                    var rank = spot.Value / ((spot.Center - position).GetHorizontalMagnitude() + TryhardSpotDistanceWeight);
                    if (rank > best)
                    {
                        best = rank;
                        goal = spot.Center;
                        name = spot.Name;
                    }
                }
                if (best > 0f)
                {
                    return true;
                }
            }
            if (!s_HasHuntGround)
            {
                return false;
            }
            goal = s_HuntCenter;
            name = s_HuntName;
            return true;
        }

        // ---- the sweep of the hunting ground ----
        // Road points of the town, and of the strip between it and the
        // nearest Mega spawn, instead of the loot points inside the houses.
        private bool BuildHuntSweepPoints(ref Vector3 center, GraphNode originNode, float now)
        {
            m_AreaLootPoints.Clear();
            if (AstarPath.active == null)
            {
                return false;
            }
            var hasMega = false;
            var megaSpawn = Vector3.zero;
            var megaGap = HuntMegaSpawnMaxMeters;
            foreach (var spawn in GetMegaSpawns())
            {
                var gap = (spawn - center).GetHorizontalMagnitude();
                if (gap < megaGap)
                {
                    megaGap = gap;
                    megaSpawn = spawn;
                    hasMega = true;
                }
            }
            var raw = new List<Vector3>();
            for (var roadIndex = 0; roadIndex < 4096; roadIndex++)
            {
                var road = LevelRoads.getRoad(roadIndex);
                if (road == null)
                {
                    break;
                }
                var joints = road.joints;
                if (joints == null)
                {
                    continue;
                }
                for (var joint = 0; joint < joints.Count; joint++)
                {
                    ConsiderHuntPoint(joints[joint].vertex, center, hasMega, megaSpawn, raw);
                    if (joint + 1 >= joints.Count)
                    {
                        continue;
                    }
                    var gap = (joints[joint + 1].vertex - joints[joint].vertex).GetHorizontalMagnitude();
                    var pieces = Mathf.Min(12, Mathf.FloorToInt(gap / 25f));
                    for (var piece = 1; piece <= pieces; piece++)
                    {
                        Vector3 between;
                        try
                        {
                            between = road.getPosition(joint, piece / (pieces + 1f));
                        }
                        catch (System.Exception)
                        {
                            between = Vector3.Lerp(joints[joint].vertex, joints[joint + 1].vertex, piece / (pieces + 1f));
                        }
                        ConsiderHuntPoint(between, center, hasMega, megaSpawn, raw);
                    }
                }
            }
            // 0.2.2v (doc 13.3, item 6): every filter counts what it threw
            // away ("so 2 ponto(s) de rua" 11 times in the 0.2.2u test, cause
            // unknown), and with fewer than 4 road points an outdoor grid of
            // the same strip is used: a point on the mesh with open sky.
            var stats = new int[5];
            foreach (var candidate in raw)
            {
                if (m_AreaLootPoints.Count >= HuntMaximumPoints)
                {
                    break;
                }
                TryAddHuntPoint(candidate, originNode, false, stats);
            }
            var roadPoints = m_AreaLootPoints.Count;
            var roadStats = "rua: " + raw.Count + " na faixa, " + stats[0] + " fora da malha, "
                + stats[1] + " sem caminho, " + stats[2] + " agua/radiacao, " + stats[4] + " colados";
            var gridStats = string.Empty;
            if (roadPoints < 4)
            {
                var grid = new List<Vector3>();
                var reach = HuntSweepRadiusMeters + (hasMega ? megaGap : 0f);
                for (var dx = -reach; dx <= reach; dx += HuntPointSpacingMeters)
                {
                    for (var dz = -reach; dz <= reach; dz += HuntPointSpacingMeters)
                    {
                        var probe = new Vector3(center.x + dx, center.y, center.z + dz);
                        probe.y = LevelGround.getHeight(probe);
                        ConsiderHuntPoint(probe, center, hasMega, megaSpawn, grid);
                    }
                }
                // Shuffled, so the 70 kept points spread over the whole strip
                // instead of filling its west side first.
                for (var index = grid.Count - 1; index > 0; index--)
                {
                    var swap = Random.Range(0, index + 1);
                    var held = grid[index];
                    grid[index] = grid[swap];
                    grid[swap] = held;
                }
                System.Array.Clear(stats, 0, stats.Length);
                foreach (var candidate in grid)
                {
                    if (m_AreaLootPoints.Count >= HuntMaximumPoints)
                    {
                        break;
                    }
                    TryAddHuntPoint(candidate, originNode, true, stats);
                }
                gridStats = "; grade: " + grid.Count + " pontos, " + stats[0] + " fora da malha, "
                    + stats[1] + " sem caminho, " + stats[2] + " agua/radiacao, " + stats[3] + " com teto, "
                    + stats[4] + " colados, " + (m_AreaLootPoints.Count - roadPoints) + " usados";
            }
            BotLog(LogLevel.Information,
                "Ponto de caca {Name}: {Road}{Grid}.",
                s_HuntName,
                roadStats,
                gridStats);
            if (m_AreaLootPoints.Count < 4)
            {
                BotLog(LogLevel.Information,
                    "Ponto de caca {Name}: so {Count} ponto(s) ao ar livre alcancaveis; usando os pontos de loot da area.",
                    s_HuntName,
                    m_AreaLootPoints.Count);
                m_AreaLootPoints.Clear();
                return false;
            }
            // A second sweep starts over: the streets are walked again.
            if (CountUnvisitedAreaPoints(now) == 0)
            {
                foreach (var point in m_AreaLootPoints)
                {
                    m_VisitedPatrolPoints.Remove(point);
                }
            }
            BotLog(LogLevel.Information,
                "Ponto de caca {Name}: varrendo {Count} pontos de rua em {Radius:0}m{Mega}; loot de casa so se for valioso ou faltar comida/agua.",
                s_HuntName,
                m_AreaLootPoints.Count,
                HuntSweepRadiusMeters,
                hasMega ? " e ate o nascimento do Mega em " + FormatPosition(megaSpawn) + " (a " + Mathf.RoundToInt(megaGap) + "m)" : string.Empty);
            return true;
        }

        // stats: 0 off the mesh, 1 no path, 2 water or radiation, 3 under a
        // roof, 4 too close to a kept point.
        private void TryAddHuntPoint(Vector3 candidate, GraphNode originNode, bool needSky, int[] stats)
        {
            var nearest = AstarPath.active.GetNearest(candidate, NNConstraint.Walkable);
            var point = nearest.position;
            var onGraph = nearest.node != null
                && (nearest.position - candidate).GetHorizontalMagnitude() <= 6f
                && Mathf.Abs(nearest.position.y - candidate.y) <= 3f;
            var graphOk = onGraph && PathUtilities.IsPathPossible(originNode, nearest.node);
            if (!graphOk)
            {
                // The bot walks the new mesh too: a point on it is enough.
                if (!SkaliaBotUnityNavmesh.TrySnap(candidate, 6f, out point)
                    || Mathf.Abs(point.y - candidate.y) > 3f)
                {
                    stats[onGraph ? 1 : 0]++;
                    return;
                }
            }
            if (WaterUtility.isPointUnderwater(point + Vector3.up * 0.5f)
                || IsInRadiation(point, 10f))
            {
                stats[2]++;
                return;
            }
            if (needSky
                && Physics.Raycast(
                    point + Vector3.up * 1.5f,
                    Vector3.up,
                    40f,
                    RayMasks.BLOCK_COLLISION,
                    QueryTriggerInteraction.Ignore))
            {
                stats[3]++;
                return;
            }
            foreach (var kept in m_AreaLootPoints)
            {
                if ((kept - point).GetHorizontalMagnitude() < HuntPointSpacingMeters)
                {
                    stats[4]++;
                    return;
                }
            }
            m_AreaLootPoints.Add(point);
        }

        private static void ConsiderHuntPoint(Vector3 point, Vector3 center, bool hasMega, Vector3 megaSpawn, List<Vector3> into)
        {
            if (into.Count >= 600)
            {
                return;
            }
            var flat = point - center;
            flat.y = 0f;
            if (flat.magnitude <= HuntSweepRadiusMeters)
            {
                into.Add(point);
                return;
            }
            if (!hasMega)
            {
                return;
            }
            // Inside the strip between the town and the Mega's spawn, but
            // not on top of the spawn: the bot shoots from 45m out.
            var axis = megaSpawn - center;
            axis.y = 0f;
            var length = axis.magnitude;
            if (length < 1f)
            {
                return;
            }
            axis /= length;
            var along = Vector3.Dot(flat, axis);
            if (along < 0f || along > length - 45f)
            {
                return;
            }
            var side = (flat - axis * along).magnitude;
            if (side <= HuntCorridorMeters)
            {
                into.Add(point);
            }
        }

        // ---- 0.2.2v (doc 13.3, item 5): the strong spot ----
        private string m_StrongAreaKey = string.Empty;
        // 0.2.2w (doc 14.3, item 5): distance weighs less (it was +150m:
        // the nearest strong spot always won) and a spot done rests 30 min.
        private const float TryhardSpotDistanceWeight = 600f;
        private const float TryhardSpotRestSeconds = 1800f;

        private bool IsStrongSweep => Tryhard
            && m_HasInterestArea
            && m_StrongAreaKey.Length > 0
            && m_InterestAreaKey == m_StrongAreaKey;

        private static bool IsStrongTable(byte table)
        {
            return s_StrongTableValues != null
                && s_StrongTableValues.TryGetValue(table, out var value)
                && value >= StrongTableValue;
        }

        // At a strong spot what is worth it is taken anywhere in sight; the
        // rest only when the bot walks past it (8m).
        private bool IsStrongLootSkipped(ItemAsset asset, Vector3 itemPoint, Vector3 position, bool needFood, bool needWater)
        {
            if ((needFood && asset.type == EItemType.FOOD) || (needWater && asset.type == EItemType.WATER))
            {
                return false;
            }
            var worth = IsValuableLoot(asset) || asset is ItemMeleeAsset || IsWearUpgrade(asset);
            return !worth && (itemPoint - position).GetHorizontalMagnitude() > HuntLootPassMeters;
        }

        // In the sweep: only what is worth stopping for, and only near.
        private bool IsHuntLootSkipped(ItemAsset asset, Vector3 itemPoint, Vector3 position, bool needFood, bool needWater)
        {
            if ((needFood && asset.type == EItemType.FOOD) || (needWater && asset.type == EItemType.WATER))
            {
                return false;
            }
            // Worth a detour: up to 25m. Anything else the bot wants: only
            // when it is on the way (8m).
            var worth = IsValuableLoot(asset) || asset is ItemMeleeAsset || IsWearUpgrade(asset);
            return (itemPoint - position).GetHorizontalMagnitude() > (worth ? HuntLootNearMeters : HuntLootPassMeters);
        }
    }
}
