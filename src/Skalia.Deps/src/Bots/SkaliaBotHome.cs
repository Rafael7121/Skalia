using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.1: a bed and /home, like the players. On Default and Skalia2 the
    // players used /home 370 times (median 4.9 min between two of the same
    // player), mostly to go between their own bases. A bot that finds a bed
    // and has none places it inside a house (roof, flat floor, room around
    // it, no other player's bed or building near), claims it the way a
    // player does (UseableBarricade/InteractableBed requests), is born on
    // it when it died far from it, and types /home in the chat (the same
    // ChatManager request a client sends, so the command runs through
    // OpenMod with the bot as the player) to heal at home with no medical
    // item left, to start a trip to an area closer to its bed than to it,
    // or when stuck for good. serverBot:behavior:botBeds / botHomeCommand.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float BedScanSeconds = 60f;
        private const float BedOtherOwnerMeters = 25f;
        private const float BedPlaceReach = 1.6f;
        private const float BedPhaseTimeoutSeconds = 6f;
        private const float BedRetrySeconds = 45f;
        private const float HomeFarMeters = 150f;
        private const float HomeTripMeters = 400f;
        private const float HomeCooldownSeconds = 300f;
        private const float HomeWaitSeconds = 8f;
        private const float HomeHealHealth = 60f;
        private const float ExternalTeleportMeters = 30f;

        private bool m_HasOwnedBed;
        private Vector3 m_OwnedBedPosition;
        private float m_NextBedScanAt;

        private int m_BedPhase;
        private float m_BedPhaseSince;
        private Vector3 m_BedPoint;
        private float m_BedYaw;
        private float m_NextBedTryAt;
        private int m_BedClaimTries;

        private float m_HomeRequestedAt = -1f;
        private float m_NextHomeAt;
        private string m_HomeReason = string.Empty;
        private Vector3 m_LastTickPosition;
        private bool m_HasLastTickPosition;

        private CSteamID BotSteamId => m_Player.channel.owner.playerID.steamID;

        private ServerInvocationContext BotInvocationContext()
        {
            return new ServerInvocationContext(
                ServerInvocationContext.EOrigin.Remote,
                m_Player.channel.owner,
                null!,
                null!);
        }

        private void RefreshOwnedBed(float now, bool force)
        {
            if (!force && now < m_NextBedScanAt)
            {
                return;
            }
            m_NextBedScanAt = now + BedScanSeconds;
            m_HasOwnedBed = false;
            var regions = BarricadeManager.regions;
            if (regions == null)
            {
                return;
            }
            var me = BotSteamId;
            for (var x = 0; x < regions.GetLength(0); x++)
            {
                for (var y = 0; y < regions.GetLength(1); y++)
                {
                    var region = regions[x, y];
                    if (region?.drops == null)
                    {
                        continue;
                    }
                    foreach (var drop in region.drops)
                    {
                        if (drop?.interactable is InteractableBed bed && bed.owner == me && drop.model != null)
                        {
                            m_HasOwnedBed = true;
                            m_OwnedBedPosition = drop.model.position;
                            return;
                        }
                    }
                }
            }
        }

        private static bool IsBedAsset(ItemAsset? asset)
        {
            return asset is ItemBarricadeAsset barricade && barricade.build == EBuild.BED;
        }

        private bool TryGetCarriedBed(out byte foundPage, out ItemJar foundJar)
        {
            foundPage = byte.MaxValue;
            foundJar = null!;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var count = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < count; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar != null && IsBedAsset(jar.GetAsset()))
                    {
                        foundPage = page;
                        foundJar = jar;
                        return true;
                    }
                }
            }
            return false;
        }

        // Loot: one bed, only with none placed and none in the bag.
        private bool WantsBedLoot(ItemAsset asset)
        {
            if (!s_Settings.BotBeds || !IsBedAsset(asset))
            {
                return false;
            }
            RefreshOwnedBed(Time.realtimeSinceStartup, false);
            return !m_HasOwnedBed
                && !TryGetCarriedBed(out _, out _)
                && HasInventorySpaceFor(asset);
        }

        // Called every tick from the main loop.
        private void UpdateBotHome(float now)
        {
            DetectExternalTeleport(now);
            if (!s_Settings.BotBeds || m_Player.life.isDead || m_Standby)
            {
                return;
            }
            RefreshOwnedBed(now, false);
            UpdateBedPlacement(now);
            UpdateHomeRequest(now);
            if (m_HomeRequestedAt < 0f && m_BedPhase == 0)
            {
                TryHomeToHeal(now);
            }
        }

        // ---- placing the bed ----
        private bool IsBedMomentFree(float now)
        {
            return (m_ObjectiveType == ObjectiveType.Explore
                    || m_ObjectiveType == ObjectiveType.Patrol
                    || m_ObjectiveType == ObjectiveType.Loot)
                && m_TreatmentPhase == 0
                && m_HomeRequestedAt < 0f
                && m_Player.life.health >= 50
                && m_Player.movement.isGrounded
                && !m_Player.equipment.isBusy
                && IsQuietAroundBot();
        }

        private void UpdateBedPlacement(float now)
        {
            if (m_BedPhase != 0)
            {
                TickBedPhase(now);
                return;
            }
            if (m_HasOwnedBed || now < m_NextBedTryAt || !IsBedMomentFree(now))
            {
                return;
            }
            if (!TryGetCarriedBed(out var page, out var jar))
            {
                return;
            }
            m_NextBedTryAt = now + 5f;
            var position = m_Player.transform.position;
            if (!HasRoofOverBot(position) || IsOtherOwnerBuildingNear(position))
            {
                return;
            }
            if (!TryFindBedSpot(jar.GetAsset() as ItemBarricadeAsset, position, out var point, out var yaw))
            {
                return;
            }
            if (!ItemTool.checkUseable(page, jar.item.id))
            {
                m_NextBedTryAt = now + BedRetrySeconds;
                return;
            }
            m_BedPoint = point;
            m_BedYaw = yaw;
            m_BedPhase = 1;
            m_BedPhaseSince = now;
            m_ThinkUntil = now + 1f;
            m_Player.equipment.ServerEquip(page, jar.x, jar.y);
            BotLog(LogLevel.Information,
                "Colocando a cama em {Point} (dentro de construcao, {Distance:0.0}m do bot).",
                FormatPosition(point),
                (point - position).magnitude);
        }

        private void TickBedPhase(float now)
        {
            m_ThinkUntil = Mathf.Max(m_ThinkUntil, now + 0.3f);
            var toPoint = m_BedPoint - m_Player.look.aim.position;
            var horizontal = new Vector2(toPoint.x, toPoint.z);
            // Looking at the spot only while the bed is in hand; once placed
            // a fight keeps its own aim.
            if (m_BedPhase <= 3 && horizontal.sqrMagnitude > 0.01f)
            {
                m_DesiredYaw = DirectionToYaw(new Vector3(toPoint.x, 0f, toPoint.z));
                m_DesiredPitch = 90f - Mathf.Atan2(toPoint.y, Mathf.Max(0.01f, horizontal.magnitude)) * Mathf.Rad2Deg;
            }
            if (m_BedPhase <= 3 && (!IsQuietAroundBot() || m_ObjectiveType == ObjectiveType.Combat))
            {
                AbortBedPlacement(now, "zumbi por perto");
                return;
            }
            if (now - m_BedPhaseSince > BedPhaseTimeoutSeconds)
            {
                AbortBedPlacement(now, "demorou demais na fase " + m_BedPhase);
                return;
            }
            var equipment = m_Player.equipment;
            switch (m_BedPhase)
            {
                case 1:
                    // In hand, the animation over: tell the server where, as
                    // the client's ReceiveBarricadeNone request does.
                    if (equipment.useable is UseableBarricade barricade
                        && IsBedAsset(equipment.asset)
                        && equipment.IsEquipAnimationFinished
                        && !equipment.isBusy)
                    {
                        var context = BotInvocationContext();
                        barricade.ReceiveBarricadeNone(in context, m_BedPoint, 0f, m_BedYaw, 0f);
                        if (!barricade.isValid)
                        {
                            AbortBedPlacement(now, "o servidor recusou o lugar (area protegida)");
                            return;
                        }
                        m_BedPhase = 2;
                        m_BedPhaseSince = now;
                    }
                    break;
                case 2:
                case 3:
                    // The click goes out in ConfigureBedInput.
                    break;
                case 4:
                    if (now - m_BedPhaseSince < 1.5f)
                    {
                        break;
                    }
                    if (!TryFindPlacedBed(m_BedPoint, out var bed))
                    {
                        AbortBedPlacement(now, "a cama nao apareceu");
                        return;
                    }
                    if (bed.owner == BotSteamId)
                    {
                        FinishBedPlacement(now);
                        return;
                    }
                    if (bed.owner == CSteamID.Nil && m_BedClaimTries < 3)
                    {
                        m_BedClaimTries++;
                        var context = BotInvocationContext();
                        bed.ReceiveClaimRequest(in context);
                        m_BedPhase = 5;
                        m_BedPhaseSince = now;
                        break;
                    }
                    AbortBedPlacement(now, "nao conseguiu reivindicar a cama");
                    m_NextBedTryAt = now + 600f;
                    return;
                case 5:
                    if (now - m_BedPhaseSince < 2f)
                    {
                        break;
                    }
                    if (TryFindPlacedBed(m_BedPoint, out var claimed) && claimed.owner == BotSteamId)
                    {
                        FinishBedPlacement(now);
                        return;
                    }
                    m_BedPhase = 4;
                    m_BedPhaseSince = now - 1.5f;
                    break;
            }
        }

        // The click on the bed, like a player's left button: Start, then Stop.
        private void ConfigureBedInput(WalkingPlayerInputPacket packet)
        {
            if (m_BedPhase == 2)
            {
                packet.primaryAttack = EAttackInputFlags.Start;
                m_BedPhase = 3;
            }
            else if (m_BedPhase == 3)
            {
                packet.primaryAttack = EAttackInputFlags.Stop;
                m_BedPhase = 4;
                m_BedPhaseSince = Time.realtimeSinceStartup;
                m_BedClaimTries = 0;
            }
        }

        private void FinishBedPlacement(float now)
        {
            m_BedPhase = 0;
            m_HasOwnedBed = true;
            m_OwnedBedPosition = m_BedPoint;
            m_NextBedScanAt = now + BedScanSeconds;
            m_RearmPending = true;
            BotLog(LogLevel.Information,
                "Cama colocada e reivindicada em {Point}; o bot renasce nela e pode usar /home.",
                FormatPosition(m_BedPoint));
        }

        private void AbortBedPlacement(float now, string reason)
        {
            var phase = m_BedPhase;
            m_BedPhase = 0;
            m_NextBedTryAt = Mathf.Max(m_NextBedTryAt, now + BedRetrySeconds);
            if (m_Player.equipment.useable is UseableBarricade && !m_Player.equipment.isBusy)
            {
                m_Player.equipment.dequip();
            }
            m_RearmPending = true;
            BotLog(LogLevel.Information,
                "Colocar a cama em {Point} falhou na fase {Phase}: {Reason}; nova tentativa em {Seconds:0}s.",
                FormatPosition(m_BedPoint),
                phase,
                reason,
                m_NextBedTryAt - now);
        }

        private static bool TryFindPlacedBed(Vector3 point, out InteractableBed bed)
        {
            bed = null!;
            var regions = new List<RegionCoordinate>();
            Regions.getRegionsInRadius(point, 4f, regions);
            var best = 2.5f;
            foreach (var coordinate in regions)
            {
                var region = BarricadeManager.regions?[coordinate.x, coordinate.y];
                if (region?.drops == null)
                {
                    continue;
                }
                foreach (var drop in region.drops)
                {
                    if (drop?.interactable is InteractableBed candidate && drop.model != null)
                    {
                        var gap = (drop.model.position - point).magnitude;
                        if (gap < best)
                        {
                            best = gap;
                            bed = candidate;
                        }
                    }
                }
            }
            return bed != null;
        }

        // A player's base, or someone's bed, within 25m: the bot sleeps
        // somewhere else.
        private bool IsOtherOwnerBuildingNear(Vector3 position)
        {
            var me = BotSteamId.m_SteamID;
            var regions = new List<RegionCoordinate>();
            Regions.getRegionsInRadius(position, BedOtherOwnerMeters, regions);
            foreach (var coordinate in regions)
            {
                var barricades = BarricadeManager.regions?[coordinate.x, coordinate.y];
                if (barricades?.drops != null)
                {
                    foreach (var drop in barricades.drops)
                    {
                        if (drop?.model == null)
                        {
                            continue;
                        }
                        var data = drop.GetServersideData();
                        var foreignOwner = data != null && data.owner != 0 && data.owner != me;
                        var foreignBed = drop.interactable is InteractableBed bed
                            && bed.owner != CSteamID.Nil
                            && bed.owner.m_SteamID != me;
                        if ((foreignOwner || foreignBed)
                            && (drop.model.position - position).GetHorizontalMagnitude() <= BedOtherOwnerMeters)
                        {
                            return true;
                        }
                    }
                }
                var structures = StructureManager.regions?[coordinate.x, coordinate.y];
                if (structures?.drops != null)
                {
                    foreach (var drop in structures.drops)
                    {
                        if (drop?.model == null)
                        {
                            continue;
                        }
                        var data = drop.GetServersideData();
                        if (data != null
                            && data.owner != 0
                            && data.owner != me
                            && (drop.model.position - position).GetHorizontalMagnitude() <= BedOtherOwnerMeters)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // The same checks the client makes before it asks (UseableBarricade
        // .checkSpace for a bed): a floor (normal up) in reach, inside the
        // map's bounds, nothing within ~1m of the bed's body, and a roof.
        private static readonly Collider[] s_BedOverlap = new Collider[4];

        private bool TryFindBedSpot(ItemBarricadeAsset? asset, Vector3 position, out Vector3 point, out float yaw)
        {
            point = Vector3.zero;
            yaw = 0f;
            if (asset == null)
            {
                return false;
            }
            var eye = m_Player.look.aim.position;
            var start = Random.Range(0, 8);
            for (var step = 0; step < 8; step++)
            {
                var angle = m_CurrentYaw + (start + step) * 45f;
                var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                var floorTarget = position + direction * BedPlaceReach;
                var toFloor = floorTarget - eye;
                if (!Physics.SphereCast(
                        eye,
                        0.1f,
                        toFloor.normalized,
                        out var hit,
                        Mathf.Min(asset.range, toFloor.magnitude + 0.5f),
                        RayMasks.BARRICADE_INTERACT)
                    || hit.normal.y <= 0.75f
                    || Mathf.Abs(hit.point.y - position.y) > 0.5f)
                {
                    continue;
                }
                var candidate = hit.point + hit.normal * asset.offset;
                if (!Level.checkSafeIncludingClipVolumes(candidate)
                    || Physics.OverlapSphereNonAlloc(
                        candidate + Vector3.up,
                        0.95f + asset.offset,
                        s_BedOverlap,
                        RayMasks.BLOCK_BARRICADE) > 0
                    || !Physics.Raycast(candidate + Vector3.up * 0.5f, Vector3.up, 6f, RayMasks.BLOCK_COLLISION))
                {
                    continue;
                }
                point = candidate;
                yaw = angle;
                return true;
            }
            return false;
        }

        // ---- born on the bed ----
        // When the bot died far (150m+) from its bed and few zombies (under 3
        // within 40m) stand by it, the respawn is at home, like the button.
        public bool ShouldRespawnAtHome()
        {
            if (!s_Settings.BotBeds)
            {
                return false;
            }
            RefreshOwnedBed(Time.realtimeSinceStartup, true);
            if (!m_HasOwnedBed)
            {
                return false;
            }
            var farFromDeath = !m_HasLastDeathPosition
                || (m_OwnedBedPosition - m_LastDeathPosition).GetHorizontalMagnitude() >= RespawnAvoidDeathMeters;
            var quiet = CountLivingZombiesNear(m_OwnedBedPosition, RespawnZombieCheckMeters) < RespawnMaxZombies;
            BotLog(LogLevel.Information,
                "Renascer na cama em {Bed}: {Decision} (longe da morte {Far}, zumbis perto da cama abaixo de {Max} {Quiet}).",
                FormatPosition(m_OwnedBedPosition),
                farFromDeath && quiet ? "sim" : "nao",
                farFromDeath,
                RespawnMaxZombies,
                quiet);
            return farFromDeath && quiet;
        }

        // ---- /home in the chat ----
        private bool CanUseHome(float now, Vector3 position)
        {
            return s_Settings.BotBeds
                && s_Settings.BotHomeCommand
                && m_HasOwnedBed
                && m_HomeRequestedAt < 0f
                && m_BedPhase == 0
                && now >= m_NextHomeAt
                && m_TreatmentPhase == 0
                && (m_OwnedBedPosition - position).GetHorizontalMagnitude() >= HomeFarMeters
                && GetNearestAggroedZombieDistance() >= HabitCalmAggroMeters
                // 0.2.2y: not mid-fight with a player.
                && m_PvpTarget == null;
        }

        private bool TryStartHome(float now, string reason)
        {
            var position = m_Player.transform.position;
            if (!CanUseHome(now, position))
            {
                return false;
            }
            m_HomeRequestedAt = now;
            m_NextHomeAt = now + HomeCooldownSeconds;
            m_HomeReason = reason;
            ClearObjective();
            m_ThinkUntil = now + HomeWaitSeconds;
            BotLog(LogLevel.Information,
                "Bot digitou /home ({Reason}); cama a {Distance:0}m.",
                reason,
                (m_OwnedBedPosition - position).GetHorizontalMagnitude());
            var context = BotInvocationContext();
            ChatManager.ReceiveChatRequest(in context, (byte)EChatMode.GLOBAL, "/home");
            return true;
        }

        private void UpdateHomeRequest(float now)
        {
            if (m_HomeRequestedAt < 0f)
            {
                return;
            }
            if (now - m_HomeRequestedAt > HomeWaitSeconds)
            {
                BotLog(LogLevel.Information,
                    "/home ({Reason}) nao levou o bot para casa em {Seconds:0}s; seguindo.",
                    m_HomeReason,
                    HomeWaitSeconds);
                m_HomeRequestedAt = -1f;
                m_NextExplorationSelectionTime = 0f;
                return;
            }
            // Standing still while the 5s run, unless a zombie comes.
            if (GetNearestAggroedZombieDistance() < HabitCalmAggroMeters)
            {
                m_ThinkUntil = 0f;
                return;
            }
            m_ThinkUntil = Mathf.Max(m_ThinkUntil, now + 0.3f);
        }

        // Hurt, no medical item and nothing after it: home to get well.
        private void TryHomeToHeal(float now)
        {
            if (!m_HasOwnedBed
                || now < m_NextHomeAt
                || m_Player.life.health >= HomeHealHealth
                || m_Player.life.isBleeding
                || CountInventoryItems(EItemType.MEDICAL) > 0
                || !IsQuietAroundBot())
            {
                return;
            }
            TryStartHome(now, "vida " + m_Player.life.health + " sem item de cura");
        }

        // The next area far away and much closer to the bed: /home first.
        private bool TryHomeBeforeTrip(Vector3 position, Vector3 area, float now)
        {
            if (!m_HasOwnedBed)
            {
                return false;
            }
            var fromBot = (area - position).GetHorizontalMagnitude();
            var fromBed = (area - m_OwnedBedPosition).GetHorizontalMagnitude();
            if (fromBot < HomeTripMeters || fromBed > fromBot * 0.5f)
            {
                return false;
            }
            return TryStartHome(now, "area " + m_InterestAreaName + " a " + fromBot.ToString("0")
                + "m do bot e " + fromBed.ToString("0") + "m da cama");
        }

        // ---- a teleport made from outside (/home, an admin's /tp) ----
        // The bot's queued packets still carry the old spot and its route
        // leads from there: point the packets at the new spot and pick the
        // next objective from it.
        private void DetectExternalTeleport(float now)
        {
            var position = m_Player.transform.position;
            if (!m_HasLastTickPosition || m_Player.life.isDead)
            {
                m_LastTickPosition = position;
                m_HasLastTickPosition = true;
                return;
            }
            var jump = (position - m_LastTickPosition).magnitude;
            m_LastTickPosition = position;
            if (jump < ExternalTeleportMeters)
            {
                return;
            }
            foreach (var queued in m_Player.input.serversidePackets)
            {
                if (queued is WalkingPlayerInputPacket walking)
                {
                    walking.clientPosition = position;
                }
            }
            var home = m_HomeRequestedAt >= 0f;
            m_HomeRequestedAt = -1f;
            m_ThinkUntil = 0f;
            m_LocalRecoveryPhase = LocalRecoveryPhase.None;
            m_TravelActive = false;
            m_TravelPhase = 0;
            m_HasInterestArea = false;
            m_PerceivedZombies.Clear();
            m_LastStuckCheckPosition = position;
            m_StallAnchor = position;
            m_NextStuckCheckTime = now + 2f;
            ClearObjective();
            ClearPath();
            m_NextExplorationSelectionTime = 0f;
            BotLog(LogLevel.Information,
                home
                    ? "Bot chegou em casa pelo /home ({Reason}): {Jump:0}m ate {Position}."
                    : "Bot foi teleportado de fora{Reason}: {Jump:0}m ate {Position}; objetivo refeito.",
                home ? m_HomeReason : string.Empty,
                jump,
                FormatPosition(position));
        }

        private void NoteOwnTeleport(Vector3 destination)
        {
            m_LastTickPosition = destination;
            m_HasLastTickPosition = true;
        }
    }
}
