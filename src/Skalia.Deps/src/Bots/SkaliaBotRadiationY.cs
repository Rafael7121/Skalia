using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SDG.Framework.Devkit;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.25 (the tester, 20/11: "faca ele entrar protegido entao",
    // "tem como ele usar o filtro para repor a mascara"). The bot kept out
    // of every deadzone. It now goes in protected, by the game's own rules
    // (PlayerLife.simulate, UseableFilter):
    // - Any deadzone: a mask with proofRadiation and filter quality above 0.
    //   FullSuitRadiation: also a shirt and pants with proofRadiation.
    // - Inside, the filter loses MaskFilterDamagePerSecond (times the mask's
    //   FilterDegradationRateMultiplier) quality per second and the player
    //   takes ProtectedDamagePerSecond. Unprotected: UnprotectedDamagePerSecond
    //   and the virus climbs.
    // - A filter is equipped and used like a bandage; the game puts the mask
    //   back to 100 (only when it is under 100).
    // What the bot does:
    // - Picks up a gas mask (and the suit on a map with full-suit zones) and
    //   up to RadiationFilterCap filters, and wears them.
    // - Loot points, items and strong spots in a deadzone open only while it
    //   is protected for that zone, has filter for it and the health.
    // - Inside, it changes the filter when the mask has under
    //   RadiationFilterUseSeconds left, and walks back to where it came in
    //   when the filter or the health left will not last the way out, or
    //   when it is in a zone without the protection for it.
    // The time margins below are this code's choice; there is no recording of
    // a player in a deadzone to take them from.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const int RadiationFilterCap = 3;
        private const float RadiationFilterUseSeconds = 45f;
        private const byte RadiationEnterQuality = 50;
        private const byte RadiationEnterHealth = 60;
        private const byte RadiationKeepHealth = 25;
        private const float RadiationExitSpeed = 4f;
        private const float RadiationExitPathFactor = 1.5f;
        private const float RadiationExitMarginSeconds = 15f;
        private const float RadiationSpotClusterMeters = 70f;

        private static bool s_DeadzonesRead;
        private static bool s_MapHasDeadzone;
        private static bool s_MapHasFullSuitZone;
        private static List<StrongSpot>? s_RadiationSpots;

        private Vector3 m_LastOutsideRadiation;
        private bool m_HasLastOutsideRadiation;
        private bool m_RadiationExitActive;
        private string m_RadiationExitWhy = string.Empty;
        private float m_NextRadiationLogAt;
        private bool m_TreatmentIsFilter;
        private byte m_TreatmentFilterBefore;

        private static void EnsureDeadzones()
        {
            if (s_DeadzonesRead || !Level.isLoaded)
            {
                return;
            }
            s_DeadzonesRead = true;
            try
            {
                var volumes = VolumeManager<DeadzoneVolume, DeadzoneVolumeManager>.Get()?.GetAllVolumes();
                if (volumes == null)
                {
                    return;
                }
                foreach (var volume in volumes)
                {
                    s_MapHasDeadzone = true;
                    s_MapHasFullSuitZone |= volume.DeadzoneType == EDeadzoneType.FullSuitRadiation;
                }
            }
            catch (System.Exception)
            {
                // A map without the devkit volumes.
            }
        }

        private static bool MapHasDeadzone
        {
            get
            {
                EnsureDeadzones();
                return s_MapHasDeadzone;
            }
        }

        private static DeadzoneVolume? GetDeadzoneAt(Vector3 point)
        {
            try
            {
                return VolumeManager<DeadzoneVolume, DeadzoneVolumeManager>.Get()?.GetMostDangerousOverlappingVolume(point);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        private static bool IsRadiationMask(ItemClothingAsset? gear)
        {
            return gear is ItemMaskAsset && gear.proofRadiation;
        }

        // Worn protection for a zone of this type (filter left not counted).
        private bool IsSuitedFor(EDeadzoneType type)
        {
            var clothing = m_Player.clothing;
            if (!IsRadiationMask(clothing.maskAsset))
            {
                return false;
            }
            return type != EDeadzoneType.FullSuitRadiation
                || (clothing.shirtAsset != null && clothing.shirtAsset.proofRadiation
                    && clothing.pantsAsset != null && clothing.pantsAsset.proofRadiation);
        }

        private int CountFilters()
        {
            return CountInventoryItems(EItemType.FILTER);
        }

        // A deadzone point the bot may go to now. Outside any zone: true.
        private bool CanEnterRadiationAt(Vector3 point)
        {
            EnsureDeadzones();
            if (!s_MapHasDeadzone || m_RadiationExitActive)
            {
                return false;
            }
            var zone = GetDeadzoneAt(point);
            var type = zone != null
                ? zone.DeadzoneType
                : s_MapHasFullSuitZone ? EDeadzoneType.FullSuitRadiation : EDeadzoneType.DefaultRadiation;
            return IsSuitedFor(type)
                && (m_Player.clothing.maskQuality >= RadiationEnterQuality || CountFilters() > 0)
                && m_Player.life.health >= RadiationEnterHealth;
        }

        // Replaces "in radiation" where the bot picks loot and areas.
        private bool IsRadiationClosedFor(Vector3 point, float margin = 0f)
        {
            return IsInRadiation(point, margin) && !CanEnterRadiationAt(point);
        }

        // ---- the strong spots inside deadzones (EnsureStrongSpots leaves
        // them out) ----
        private static List<StrongSpot> GetRadiationSpots()
        {
            if (s_RadiationSpots != null)
            {
                return s_RadiationSpots;
            }
            s_RadiationSpots = new List<StrongSpot>();
            EnsureStrongSpots();
            if (!MapHasDeadzone || s_StrongTableValues == null || LevelItems.spawns == null)
            {
                return s_RadiationSpots;
            }
            var sums = new List<Vector3>();
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
                        if (!s_StrongTableValues.TryGetValue(spawn.type, out var value)
                            || value < StrongTableValue
                            || !IsInRadiation(spawn.point))
                        {
                            continue;
                        }
                        var group = -1;
                        for (var index = 0; index < s_RadiationSpots.Count; index++)
                        {
                            if ((s_RadiationSpots[index].Center - spawn.point).GetHorizontalMagnitude() < RadiationSpotClusterMeters)
                            {
                                group = index;
                                break;
                            }
                        }
                        if (group < 0)
                        {
                            s_RadiationSpots.Add(new StrongSpot { Center = spawn.point, Points = 1, Value = value });
                            sums.Add(spawn.point);
                            continue;
                        }
                        var spot = s_RadiationSpots[group];
                        sums[group] += spawn.point;
                        spot.Points++;
                        spot.Value += value;
                        spot.Center = sums[group] / spot.Points;
                    }
                }
            }
            s_RadiationSpots.RemoveAll(spot => spot.Points < StrongClusterMinimumPoints);
            var megaSpawns = GetMegaSpawns();
            foreach (var spot in s_RadiationSpots)
            {
                spot.HasMega = megaSpawns.Any(mega => (mega - spot.Center).GetHorizontalMagnitude() <= StrongSpotMegaMeters);
                spot.Name = "ponto forte na radiacao em " + FormatPosition(spot.Center);
            }
            return s_RadiationSpots;
        }

        private IEnumerable<StrongSpot> GetEnterableRadiationSpots()
        {
            if (!MapHasDeadzone)
            {
                return Enumerable.Empty<StrongSpot>();
            }
            return GetRadiationSpots().Where(spot => CanEnterRadiationAt(spot.Center));
        }

        // ---- inside: change the filter, walk out in time ----
        private float GetRadiationSecondsLeft(IDeadzoneNode zone, out string limit)
        {
            var clothing = m_Player.clothing;
            var filterRate = zone.MaskFilterDamagePerSecond
                * (clothing.maskAsset is ItemMaskAsset mask ? mask.FilterDegradationRateMultiplier : 1f);
            var filterSeconds = filterRate > 0f ? clothing.maskQuality / filterRate : float.PositiveInfinity;
            var hurt = zone.ProtectedDamagePerSecond;
            var healthSeconds = hurt > 0f
                ? Mathf.Max(0f, m_Player.life.health - RadiationKeepHealth) / hurt
                : float.PositiveInfinity;
            limit = filterSeconds <= healthSeconds ? "filtro" : "vida";
            return Mathf.Min(filterSeconds, healthSeconds);
        }

        private float GetRadiationExitSeconds(Vector3 position)
        {
            var distance = m_HasLastOutsideRadiation
                ? (m_LastOutsideRadiation - position).GetHorizontalMagnitude()
                : 60f;
            return distance * RadiationExitPathFactor / RadiationExitSpeed + RadiationExitMarginSeconds;
        }

        private bool IsRadiationExitRequired()
        {
            var position = m_BehaviorPosition;
            var now = m_BehaviorNow;
            var movement = m_Player.movement;
            var zone = movement.isRadiated ? movement.ActiveDeadzone : null;
            if (zone == null)
            {
                if (!IsInRadiation(position))
                {
                    m_LastOutsideRadiation = position;
                    m_HasLastOutsideRadiation = true;
                    if (m_RadiationExitActive)
                    {
                        m_RadiationExitActive = false;
                        BotLog(LogLevel.Information,
                            "Radiacao: fora da zona em {Position} (vida {Health}, filtro {Quality}%, {Filters} filtro(s)).",
                            FormatPosition(position),
                            m_Player.life.health,
                            m_Player.clothing.maskQuality,
                            CountFilters());
                        if (m_ObjectiveType == ObjectiveType.EscapeHazard && !m_HazardEscapeActive)
                        {
                            ClearObjective();
                            m_NextExplorationSelectionTime = now;
                        }
                    }
                }
                return false;
            }
            if (m_RadiationExitActive)
            {
                return true;
            }
            string why;
            if (!IsSuitedFor(zone.DeadzoneType))
            {
                // Crossing a zone on the way somewhere else stays as it was
                // before y17.25 while the health holds.
                if (!IsInRadiation(m_ObjectivePosition) && m_Player.life.health >= RadiationEnterHealth)
                {
                    return false;
                }
                why = zone.DeadzoneType == EDeadzoneType.FullSuitRadiation
                    ? "sem mascara e traje antirradiacao"
                    : "sem mascara antirradiacao";
            }
            else
            {
                var left = GetRadiationSecondsLeft(zone, out var limit);
                var exit = GetRadiationExitSeconds(position);
                if (limit == "filtro" && CountFilters() > 0)
                {
                    // TryStartFilterUse changes it before it runs out.
                    return false;
                }
                if (left >= exit)
                {
                    return false;
                }
                why = limit + " para " + left.ToString("0") + "s e a saida leva " + exit.ToString("0") + "s";
            }
            m_RadiationExitActive = true;
            m_RadiationExitWhy = why;
            BotLog(LogLevel.Information,
                "Radiacao: saindo da zona ({Why}); voltando por {Exit}.",
                why,
                m_HasLastOutsideRadiation ? FormatPosition(m_LastOutsideRadiation) : "o ponto seco mais perto");
            return true;
        }

        private BotBehaviorStatus TickRadiationExitBehavior()
        {
            var position = m_BehaviorPosition;
            var now = m_BehaviorNow;
            if (m_HasLastOutsideRadiation
                && (m_LastOutsideRadiation - position).GetHorizontalMagnitude() <= 2.5f)
            {
                // At the way-in point and still radiated: the zone moved or the
                // point was on its edge; look farther out.
                m_HasLastOutsideRadiation = TryFindRadiationExit(position, out m_LastOutsideRadiation);
            }
            var target = m_HasLastOutsideRadiation
                ? m_LastOutsideRadiation
                : (TryFindRadiationExit(position, out var found) ? found : position);
            if (m_ObjectiveType != ObjectiveType.EscapeHazard
                || (m_ObjectivePosition - target).GetHorizontalMagnitude() > 4f
                || now >= m_ObjectiveExpiresAt)
            {
                SetObjective(ObjectiveType.EscapeHazard, target, now + 60f);
            }
            if (now >= m_NextRadiationLogAt)
            {
                m_NextRadiationLogAt = now + 5f;
                BotLog(LogLevel.Debug,
                    "Radiacao: saindo ({Why}), a {Distance:0}m de {Target}; vida {Health}, filtro {Quality}%.",
                    m_RadiationExitWhy,
                    (target - position).GetHorizontalMagnitude(),
                    FormatPosition(target),
                    m_Player.life.health,
                    m_Player.clothing.maskQuality);
            }
            return BotBehaviorStatus.Running;
        }

        private static bool TryFindRadiationExit(Vector3 origin, out Vector3 exit)
        {
            for (var radius = 20f; radius <= 300f; radius += 20f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    var candidate = FindNearestNavigablePoint(origin + direction * radius);
                    if (!IsInRadiation(candidate, 5f)
                        && !WaterUtility.isPointUnderwater(candidate + Vector3.up * 0.5f))
                    {
                        exit = candidate;
                        return true;
                    }
                }
            }
            exit = origin;
            return false;
        }

        // A filter into the mask, like a bandage (UpdateTreatment): when the
        // mask has under RadiationFilterUseSeconds left in this zone, or is
        // under RadiationEnterQuality outside one.
        private bool TryStartFilterUse(float now)
        {
            var clothing = m_Player.clothing;
            if (!IsRadiationMask(clothing.maskAsset)
                || clothing.maskQuality >= 100
                || m_Player.equipment.isBusy
                || !TryFindFilter(out var page, out var jar))
            {
                return false;
            }
            var movement = m_Player.movement;
            var zone = movement.isRadiated ? movement.ActiveDeadzone : null;
            if (zone != null)
            {
                var filterRate = zone.MaskFilterDamagePerSecond
                    * (clothing.maskAsset is ItemMaskAsset mask ? mask.FilterDegradationRateMultiplier : 1f);
                if (filterRate <= 0f || clothing.maskQuality / filterRate >= RadiationFilterUseSeconds)
                {
                    return false;
                }
            }
            else if (clothing.maskQuality >= RadiationEnterQuality)
            {
                return false;
            }
            var equipment = m_Player.equipment;
            if (!(equipment.equippedPage == page && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y))
            {
                equipment.ServerEquip(page, jar.x, jar.y);
            }
            var asset = jar.GetAsset();
            m_TreatmentEquipIssuedAt = now;
            m_TreatmentIsMeal = false;
            m_TreatmentIsFilter = true;
            m_TreatmentFilterBefore = clothing.maskQuality;
            m_TreatmentPhase = 1;
            m_TreatmentItemId = asset?.id ?? jar.item.id;
            m_TreatmentItemName = asset?.itemName ?? "filtro";
            m_TreatmentPhaseSince = now;
            m_TreatmentHealthBefore = m_Player.life.health;
            m_TreatmentBleedingBefore = m_Player.life.isBleeding;
            m_TreatmentVirusBefore = m_Player.life.virus;
            BotLog(LogLevel.Information,
                "Radiacao: trocando o filtro da {Mask} ({Quality}%, {Where}).",
                clothing.maskAsset!.itemName,
                clothing.maskQuality,
                zone != null ? "dentro da zona" : "fora da zona");
            return true;
        }

        private bool TryFindFilter(out byte page, out ItemJar jar)
        {
            page = byte.MaxValue;
            jar = null!;
            for (byte candidatePage = 0; candidatePage < PlayerInventory.STORAGE; candidatePage++)
            {
                var itemCount = m_Player.inventory.getItemCount(candidatePage);
                for (byte index = 0; index < itemCount; index++)
                {
                    var candidate = m_Player.inventory.getItem(candidatePage, index);
                    if (candidate?.GetAsset() is ItemFilterAsset)
                    {
                        page = candidatePage;
                        jar = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        // ---- what to pick up and wear ----
        private bool WantsFilterLoot(ItemAsset asset)
        {
            return asset is ItemFilterAsset
                && MapHasDeadzone
                && CountFilters() < RadiationFilterCap
                && HasInventorySpaceFor(asset);
        }

        // A radiation mask over any other mask on a map with deadzones; a
        // plain mask never over it.
        private static int RadiationMaskOrder(ItemClothingAsset gear, ItemClothingAsset? worn)
        {
            if (gear.type != EItemType.MASK || !MapHasDeadzone)
            {
                return 0;
            }
            var gearProof = IsRadiationMask(gear);
            var wornProof = IsRadiationMask(worn);
            return gearProof == wornProof ? 0 : gearProof ? 1 : -1;
        }

        // The suit on a map with full-suit zones: a proof shirt or pants over
        // a plain one; a plain one never over it.
        private static int RadiationSuitOrder(ItemClothingAsset gear, ItemClothingAsset? worn)
        {
            if ((gear.type != EItemType.SHIRT && gear.type != EItemType.PANTS) || !MapHasDeadzone || !s_MapHasFullSuitZone)
            {
                return 0;
            }
            var wornProof = worn != null && worn.proofRadiation;
            return gear.proofRadiation == wornProof ? 0 : gear.proofRadiation ? 1 : -1;
        }
    }
}
