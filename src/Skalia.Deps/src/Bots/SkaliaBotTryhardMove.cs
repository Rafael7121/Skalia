using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t (doc 11.3 and 11.4): never standing in the open. Cover is a
    // spot where walls, rocks or vehicles close most directions at crouch
    // height; a bandage is finished there, crouched. Anywhere else the bot
    // keeps walking, and 2s on one spot without a reason is logged and
    // broken up.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const string CoverFleeName = "cobertura";
        private const float CoverSearchMeters = 12f;
        private const float CoverRayMeters = 5f;
        private const int CoverMinimumBlocked = 5;
        private const float HopCoverMeters = 10f;
        private const float IdleLimitSeconds = 2f;
        private const float IdleMoveMeters = 0.6f;

        private float m_CoverCheckedAt = float.NegativeInfinity;
        private Vector3 m_CoverCheckedPosition;
        private bool m_CoverCheckedResult;
        private bool m_CoverSearchedThisTreatment;
        private Vector3 m_IdleAnchor;
        private float m_IdleSince;
        private int m_IdleStrikes;
        private float m_EmptyHandsSince = -1f;
        private float m_RespawnGraceUntil;

        private static int CountBlockedDirections(Vector3 point, float meters)
        {
            var origin = point + Vector3.up * 0.9f;
            var blocked = 0;
            for (var index = 0; index < 8; index++)
            {
                var direction = Quaternion.Euler(0f, index * 45f, 0f) * Vector3.forward;
                if (Physics.Raycast(origin, direction, meters, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    blocked++;
                }
            }
            return blocked;
        }

        private bool IsBehindCover(Vector3 position)
        {
            var now = Time.realtimeSinceStartup;
            if (now - m_CoverCheckedAt < 0.3f
                && (position - m_CoverCheckedPosition).sqrMagnitude < 0.25f)
            {
                return m_CoverCheckedResult;
            }
            m_CoverCheckedAt = now;
            m_CoverCheckedPosition = position;
            m_CoverCheckedResult = CountBlockedDirections(position, CoverRayMeters) >= CoverMinimumBlocked;
            return m_CoverCheckedResult;
        }

        // Anything solid at chest height within 10m, in any direction.
        private static bool HasCoverWithin(Vector3 position, float meters)
        {
            return CountBlockedDirections(position, meters) > 0;
        }

        private bool TryFindCoverSpot(Vector3 position, float now, out Vector3 spot)
        {
            spot = position;
            var bestDistance = float.PositiveInfinity;
            for (var ring = 4f; ring <= CoverSearchMeters + 0.1f; ring += 4f)
            {
                for (var index = 0; index < 8; index++)
                {
                    var direction = Quaternion.Euler(0f, index * 45f + (ring > 6f && ring < 10f ? 22.5f : 0f), 0f) * Vector3.forward;
                    var desired = position + direction * ring;
                    if (!TryGetWalkableGround(desired, out var grounded)
                        || Mathf.Abs(grounded.y - position.y) > 1.5f
                        || WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                        || IsHazardousPosition(grounded, now, 1.5f)
                        || IsInRadiation(grounded)
                        || IsWithinMegaReach(grounded)
                        || CountBlockedDirections(grounded, CoverRayMeters) < CoverMinimumBlocked
                        // 0.2.2y2: not in sight of the shooter it found.
                        || IsSeenFromPvpShooter(grounded))
                    {
                        continue;
                    }
                    // A spot behind a wall with no door on this side is not
                    // 12m away on foot: the way there must be on the mesh.
                    // 0.2.2x9: the new mesh, the one the route is made on.
                    if (!SkaliaBotUnityNavmesh.TrySnap(grounded, 1.5f, out var onMesh)
                        || (onMesh - grounded).GetHorizontalMagnitude() > 1.5f
                        || Mathf.Abs(onMesh.y - grounded.y) > 1.5f)
                    {
                        continue;
                    }
                    // The straightest of the ring: the one that leaves
                    // the fewest open sides.
                    var open = 8 - CountBlockedDirections(grounded, CoverRayMeters);
                    if (open < bestDistance)
                    {
                        bestDistance = open;
                        spot = grounded;
                    }
                }
                if (bestDistance < float.PositiveInfinity)
                {
                    return true;
                }
            }
            return false;
        }

        // Called before the behavior tree: once per bandage, pick where it
        // is finished. Behind cover already: stay. Cover within 12m: go
        // there (the bandage is started on the way). None: keep walking.
        private void UpdateTreatmentCover(Vector3 position, float now)
        {
            if (!Tryhard)
            {
                return;
            }
            if (m_TreatmentPhase == 0 || m_TreatmentIsMeal)
            {
                m_CoverSearchedThisTreatment = false;
                return;
            }
            if (m_CoverSearchedThisTreatment
                || m_CachedNearestZombieDistance <= 8f
                || m_CachedNearestBossDistance <= BossTreatmentRunMeters
                || !IsTreatmentStopSafe()
                || IsFightObjective()
                || m_ObjectiveType == ObjectiveType.Unstuck
                || m_ObjectiveType == ObjectiveType.ExitWater
                || m_ObjectiveType == ObjectiveType.Ladder
                || IsLadderInProgress()
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return;
            }
            m_CoverSearchedThisTreatment = true;
            if (IsBehindCover(position) && !IsSeenFromPvpShooter(position))
            {
                BotLog(LogLevel.Information,
                    "Curativo: ja atras de cobertura em {Position}; cura parado e agachado.",
                    FormatPosition(position));
                return;
            }
            if (TryFindCoverSpot(position, now, out var spot))
            {
                m_FleeingFromPlayerName = CoverFleeName;
                SetObjective(ObjectiveType.Flee, spot, now + 4f);
                BotLog(LogLevel.Information,
                    "Curativo: indo para a cobertura a {Distance:0.#}m em {Spot} (sangrando {Bleeding}); cura no caminho e termina la.",
                    (spot - position).GetHorizontalMagnitude(),
                    FormatPosition(spot),
                    m_Player.life.isBleeding);
                return;
            }
            if (TryStartPvpHealRun(position, now))
            {
                return;
            }
            BotLog(LogLevel.Information,
                "Curativo: sem cobertura a {Meters:0}m de {Position}; cura andando.",
                CoverSearchMeters,
                FormatPosition(position));
        }

        // The old rule stopped the bot wherever it was. In tryhard it only
        // stands still for a bandage behind cover.
        private bool ShouldStopForTreatment(Vector3 position)
        {
            return !Tryhard || IsBehindCover(position);
        }

        // ---- 2s on one spot ----

        private bool IsIdleJustified(Vector3 position, float now)
        {
            if (m_Player.life.isDead
                || !m_MovementEnabled
                || m_Player.stance.stance == EPlayerStance.SITTING
                || m_Player.stance.stance == EPlayerStance.DRIVING
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || IsLadderInProgress()
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_BoulderDodgePhase != 0
                || m_RampPhase != 0
                || m_HomeRequestedAt >= 0f
                || now < m_RespawnGraceUntil
                || m_ObjectiveType == ObjectiveType.OpenDoor
                || m_ObjectiveType == ObjectiveType.Unstuck
                || IsLootPileActive(now)
                || now < m_LootLingerUntil
                // 0.2.2x1: the watch from high ground stands still on purpose.
                || IsHighWatching(now)
                // 0.2.2x10: waiting for the Mega or a ladder retry.
                || IsDeliberateHold(now)
                // 0.2.2y: shooting back at a player, or looking where he was.
                || IsPvpPlanted())
            {
                return true;
            }
            if (m_TreatmentPhase != 0 && !m_TreatmentIsMeal && IsBehindCover(position))
            {
                return true;
            }
            if ((m_ObjectiveType == ObjectiveType.Combat || m_ObjectiveType == ObjectiveType.EscapeSwarm)
                && IsTargetZombieAlive())
            {
                // Aiming a gun, or a zombie in reach of the blade.
                return m_Player.equipment.asset is ItemGunAsset
                    || (m_TargetZombie!.transform.position - position).GetHorizontalMagnitude() <= 4f;
            }
            if (m_ObjectiveType == ObjectiveType.Loot
                && (m_ObjectivePosition - position).GetHorizontalMagnitude()
                   <= Mathf.Max(m_LootPickupDistanceMeters, LootSightReachMeters) + 1.5f)
            {
                return true;
            }
            return false;
        }

        private void UpdateIdleWatchdog(Vector3 position, float now)
        {
            if (!Tryhard)
            {
                return;
            }
            if ((position - m_IdleAnchor).sqrMagnitude > IdleMoveMeters * IdleMoveMeters
                || IsIdleJustified(position, now))
            {
                m_IdleAnchor = position;
                m_IdleSince = now;
                m_IdleStrikes = 0;
                return;
            }
            if (now - m_IdleSince < IdleLimitSeconds)
            {
                return;
            }
            m_IdleSince = now;
            m_IdleStrikes++;
            var action = m_ObjectiveType == ObjectiveType.None
                ? "escolhendo destino agora"
                : m_IdleStrikes >= 3 || (m_IdleStrikes == 2 && m_IsWalking)
                    ? "manobra de destravar"
                    : m_IdleStrikes == 2
                        ? "rota refeita"
                        : "pausas canceladas";
            BotLog(LogLevel.Information,
                "Parado {Seconds:0}s em {Position} (vez {Strike}): objetivo {Objective} a {Distance:0.#}m, andando {Walking}, pensando {Think}, pausa de habito {Habit}, rota {Index}/{Count} pendente {Pending}, parede {Wall}, curativo fase {Treatment}, na mao {Held}, zumbi mais perto {Zombie:0.#}m. Acao: {Action}.",
                IdleLimitSeconds * m_IdleStrikes,
                FormatPosition(position),
                m_IdleStrikes,
                m_ObjectiveType,
                m_ObjectiveType == ObjectiveType.None ? 0f : (m_ObjectivePosition - position).GetHorizontalMagnitude(),
                m_IsWalking,
                now < m_ThinkUntil,
                IsHabitPaused(now),
                m_PathIndex,
                m_PathPoints.Count,
                m_PathPending,
                m_WallMovementBlocked,
                m_TreatmentPhase,
                DescribeEquipped(),
                m_CachedNearestZombieDistance,
                action);
            m_ThinkUntil = 0f;
            ResetHabits();
            if (m_ObjectiveType == ObjectiveType.None)
            {
                m_NextExplorationSelectionTime = 0f;
                return;
            }
            if (m_IdleStrikes >= 3 || (m_IdleStrikes == 2 && m_IsWalking))
            {
                m_IdleStrikes = 0;
                EnterUnstuckState(position, now);
            }
            else if (m_IdleStrikes == 2)
            {
                ClearPath();
            }
        }

        // ---- 0.2.2w (doc 14.3, item 7): fluid movement ----
        private const float TryhardCoastSeconds = 0.5f;
        private float m_CoastUntil;

        // A turn of 270 degrees or more in 2s while the bot moved under
        // 1.5m is logged with what it was doing, so a spin that is left
        // shows its cause in the next test.
        private float m_SpinWindowStart;
        private float m_SpinAccumulated;
        private float m_SpinLastYaw;
        private Vector3 m_SpinStartPosition;
        private float m_NextSpinLogAt;

        private void UpdateSpinWatch(Vector3 position, float now)
        {
            if (!Tryhard)
            {
                return;
            }
            var yaw = m_Player.look.yaw;
            if (now - m_SpinWindowStart > 2f)
            {
                m_SpinWindowStart = now;
                m_SpinAccumulated = 0f;
                m_SpinStartPosition = position;
                m_SpinLastYaw = yaw;
                return;
            }
            m_SpinAccumulated += Mathf.Abs(Mathf.DeltaAngle(m_SpinLastYaw, yaw));
            m_SpinLastYaw = yaw;
            // 0.2.2x (doc 15.2, item 14): over 200 degrees in place with no
            // fight going on, the heading is held for a second.
            if (m_SpinAccumulated >= SpinHoldDegrees
                && now >= m_SpinHoldUntil
                && (position - m_SpinStartPosition).GetHorizontalMagnitude() <= 0.5f
                && !IsPvpPlanted()
                && !((m_ObjectiveType == ObjectiveType.Combat || m_ObjectiveType == ObjectiveType.EscapeSwarm)
                     && IsTargetZombieAlive()))
            {
                m_SpinHoldUntil = now + SpinHoldSeconds;
            }
            if (m_SpinAccumulated < 270f
                || (position - m_SpinStartPosition).GetHorizontalMagnitude() > 1.5f
                || now < m_NextSpinLogAt)
            {
                return;
            }
            m_NextSpinLogAt = now + 10f;
            BotLog(LogLevel.Information,
                "Giro parado: {Degrees:0} graus em {Seconds:0.0}s sem sair do lugar em {Position}; objetivo {Objective}, recuperacao {Recovery}, alvo {Target}, loot olhando {Loot}, mira {Aim}.",
                m_SpinAccumulated,
                now - m_SpinWindowStart,
                FormatPosition(position),
                m_ObjectiveType,
                m_LocalRecoveryPhase,
                IsTargetZombieAlive() ? DescribeZombieKind(m_TargetZombie!) : "nenhum",
                m_ObjectiveType == ObjectiveType.Loot,
                m_Player.equipment.asset != null ? m_Player.equipment.asset.itemName : "nada");
            m_SpinWindowStart = now;
            m_SpinAccumulated = 0f;
            m_SpinStartPosition = position;
        }

        // ---- 0.2.2w (doc 14.3, item 2): one floor at a time ----
        private const float TryhardFloorMeters = 2.2f;
        private int m_PatrolFloorDirection;

        private float GetFloorChangeCost(float dy)
        {
            if (Mathf.Abs(dy) <= TryhardFloorMeters)
            {
                return 0f;
            }
            var floors = Mathf.Max(1f, Mathf.Round(Mathf.Abs(dy) / 3f));
            var cost = 20f * floors;
            // Going back the way it came costs more: up to the top, then down.
            if (m_PatrolFloorDirection != 0 && Mathf.Sign(dy) != m_PatrolFloorDirection)
            {
                cost += 25f;
            }
            return cost;
        }

        // ---- weapon back in the hands ----
        // After a meal the hands stayed empty for about 55s in the 0.2.2
        // test: the rearm was skipped with no zombie within 12m (a rule from
        // when the melee was put away between fights).
        private void UpdateWeaponInHand(float now)
        {
            if (!Tryhard)
            {
                return;
            }
            var equipment = m_Player.equipment;
            if (equipment.asset != null
                || equipment.isBusy
                || m_TreatmentPhase != 0
                || m_Player.life.isDead
                || IsLadderInProgress()
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || now < m_NextTreatmentAt - 1f && m_RearmPending && !IsPvpMovingOnHim())
            {
                m_EmptyHandsSince = -1f;
                return;
            }
            if (m_EmptyHandsSince < 0f)
            {
                m_EmptyHandsSince = now;
                return;
            }
            if (now - m_EmptyHandsSince < 1f)
            {
                return;
            }
            m_EmptyHandsSince = now;
            // 0.2.2v (doc 13.3, item 4): away from zombies the gun, not the
            // first melee found.
            if (m_CachedNearestZombieDistance > TryhardGunDrawZombieMeters
                && !IsFightObjective()
                && TryEquipKeeperGun(now, "maos vazias"))
            {
                return;
            }
            if (HasMeleeWeapon() || BestCarriedUsableGunScore() > 0f)
            {
                EnsureCombatWeapon(false, now);
            }
        }
    }
}
