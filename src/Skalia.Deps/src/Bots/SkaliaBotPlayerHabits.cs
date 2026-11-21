using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.0: what the players do on the way and when they stop, measured on
    // the recordings of the night of 11-08 (five players;
    // about 6h):
    //   - prone or crouched 15-60% of the time: prone to take items off the
    //     floor and to bandage, crouched in stops; at night 2-3 times more;
    //   - about 4 short stops a minute of ~3s, turning the view round at
    //     100-240 deg/s (p90) while stopped;
    //   - walking sideways 22-28% of the time they move: the view goes to a
    //     side while the feet keep to the way;
    //   - sprint in bursts (median 1.5s, nearly all under 10s) and let go for
    //     ~2s between them, so stamina stays high (above 80 at 52-71% of the
    //     stops, under 20 only 2-9% of the time);
    //   - 4-8 jumps a minute.
    // The bots did none of that: always standing, the view on the route, a
    // sprint until the stamina ran out. All of it only with nothing after the
    // bot: never chased, fighting, fleeing, in the water, on a ladder or
    // with the risk high (SkaliaBotRisk). serverBot:behavior:playerPosture
    // and playerHabits switch the two halves off.
    // 0.1.0-y17.27: the bursts, the walk between them and the looks on the
    // way are measured again, bots against players (SkaliaBotMoveFeelY).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float HabitCalmAggroMeters = 25f;
        private const float HabitCalmZombieMeters = 10f;
        private const float HabitGlanceRouteMeters = 6f;
        private const float HabitPauseRouteMeters = 3f;
        private const float HabitHopStamina = 40f;
        private const float LootProneReachMeters = 1.6f;
        private const float LootProneHeightMeters = 0.4f;

        // 0 standing, 1 crouched, 2 prone; read when the packet goes out.
        private int m_PostureKey;
        private float m_HabitNextPauseAt;
        private float m_HabitPauseUntil;
        private float m_HabitPauseNextLookAt;
        private float m_HabitPauseLookYaw;
        private float m_HabitPauseLookPitch;
        private bool m_HabitPauseCrouch;
        private float m_HabitNextGlanceAt;
        private float m_HabitGlanceUntil;
        private float m_HabitGlanceOffset;
        private float m_HabitSprintUntil;
        private float m_HabitRestUntil;
        private float m_HabitLastSprintWishAt = float.NegativeInfinity;
        private float m_HabitNextHopAt;
        private float m_HabitSidesCheckAt;
        private bool m_HabitSidesClear;

        private bool IsHabitPaused(float now)
        {
            return now < m_HabitPauseUntil;
        }

        private bool IsQuietAroundBot()
        {
            return !IsRiskHigh()
                && GetNearestAggroedZombieDistance() > HabitCalmAggroMeters
                && m_CachedNearestZombieDistance > HabitCalmZombieMeters
                && m_CachedNearestBossDistance > RiskMegaMeters
                && m_ThreatHuntingFireDistance > HabitCalmAggroMeters
                // 0.2.2y: a player who shot it in the last minute.
                && m_PvpTarget == null;
        }

        private bool IsDangerObjective()
        {
            switch (m_ObjectiveType)
            {
                case ObjectiveType.Combat:
                case ObjectiveType.Flee:
                case ObjectiveType.EscapeHazard:
                case ObjectiveType.RepositionFire:
                case ObjectiveType.EscapeSwarm:
                case ObjectiveType.ExitWater:
                case ObjectiveType.Unstuck:
                case ObjectiveType.Ladder:
                case ObjectiveType.OpenDoor:
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsUprightStance(EPlayerStance stance)
        {
            return stance == EPlayerStance.STAND
                || stance == EPlayerStance.SPRINT
                || stance == EPlayerStance.CROUCH
                || stance == EPlayerStance.PRONE;
        }

        // ---- posture ----

        private void UpdatePostureIntent(float now)
        {
            m_PostureKey = 0;
            // 0.2.2x11: on its feet while getting out of the press.
            if (m_CrowdDodgeActive)
            {
                return;
            }
            // 0.2.2x (doc 15.2, item 2): prone to shoot and to heal.
            if (TryhardWantsProne(now))
            {
                m_PostureKey = 2;
                return;
            }
            // 0.2.2x13: crouched to a crawler or a burner fought with a blade.
            if (TryhardWantsMeleeCrouch())
            {
                m_PostureKey = 1;
                return;
            }
            // 0.2.2x1 (doc 16.3, item 9): the watch from high ground.
            if (IsHighWatching(now))
            {
                m_PostureKey = m_HighWatchPosture;
                return;
            }
            if (!s_Settings.PlayerPosture
                || m_Player.life.isDead
                || !IsUprightStance(m_Player.stance.stance)
                || m_IsSprinting
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_BoulderDodgePhase != 0
                || IsDangerObjective()
                || !IsQuietAroundBot())
            {
                return;
            }
            if (m_TreatmentPhase != 0 && !m_IsWalking)
            {
                // A bandage lying down, a meal crouched. 0.2.2t: crouched,
                // and only behind cover.
                if (Tryhard)
                {
                    m_PostureKey = IsBehindCover(m_Player.transform.position) ? 1 : 0;
                    return;
                }
                m_PostureKey = m_TreatmentIsMeal ? 1 : 2;
                return;
            }
            if (IsLootProneSpot())
            {
                m_PostureKey = 2;
                return;
            }
            if (IsOverlookWatching(now))
            {
                // 0.2.2: crouched while looking an area over.
                m_PostureKey = 1;
                return;
            }
            if (IsHabitPaused(now) && m_HabitPauseCrouch)
            {
                m_PostureKey = 1;
            }
        }

        // An item on the floor right by the bot: down on it, and still down
        // for the next one if it lies as close (the players loot 7-11 items
        // in 25-30s without getting up). Not on a table or a shelf.
        private bool IsLootProneSpot()
        {
            if (m_ObjectiveType != ObjectiveType.Loot
                || !TryGetTargetLoot(out var loot))
            {
                return false;
            }
            var position = m_Player.transform.position;
            var offset = loot.point - position;
            if (Mathf.Abs(offset.y) > LootProneHeightMeters
                || offset.GetHorizontalMagnitude() > LootProneReachMeters)
            {
                return false;
            }
            var settled = m_LootSettledId == m_LootInstanceId;
            if (!settled && m_Player.stance.stance != EPlayerStance.PRONE)
            {
                return false;
            }
            return HasLootLineOfSight(loot.point);
        }

        // ---- on the way: stops, glances, sprint bursts, hops ----

        private bool IsHabitObjective(Vector3 position)
        {
            switch (m_ObjectiveType)
            {
                case ObjectiveType.Explore:
                case ObjectiveType.Patrol:
                case ObjectiveType.Travel:
                    return true;
                case ObjectiveType.Loot:
                    return (m_ObjectivePosition - position).GetHorizontalMagnitude() > LootSlowDownMeters * 2f;
                default:
                    return false;
            }
        }

        private void ResetHabits()
        {
            m_HabitPauseUntil = 0f;
            m_HabitGlanceUntil = 0f;
            m_HabitSprintUntil = 0f;
            m_HabitRestUntil = 0f;
        }

        private float NextHabitPauseGap()
        {
            var perMinute = s_Settings.HabitPausesPerMinute;
            if (perMinute <= 0.01f)
            {
                return float.PositiveInfinity;
            }
            var mean = 60f / perMinute;
            return Random.Range(mean * 0.5f, mean * 1.5f);
        }

        // Runs after the route heading, the sprint choice and the loot aim
        // are set, before the view turns. It only takes away (sprint, the
        // walk during a stop) and moves the view; it never makes the bot
        // sprint or walk where the rest of the code did not.
        private void UpdatePlayerHabits(Vector3 position, Vector3 navigationTarget, float now, bool chased, bool aiming)
        {
            if (!s_Settings.PlayerHabits
                || chased
                || aiming
                || IsSwimmingOrClimbing()
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_BoulderDodgePhase != 0
                || m_WallAvoidanceActive
                || now < m_DirectFallbackUntil
                || now < m_ThinkUntil
                || now < m_LootLingerUntil
                || m_TreatmentPhase != 0
                || !IsHabitObjective(position)
                || !IsQuietAroundBot())
            {
                ResetHabits();
                return;
            }
            var toNavigation = navigationTarget - position;
            toNavigation.y = 0f;
            var navigationDistance = toNavigation.magnitude;
            var routeYaw = navigationDistance > 0.1f ? DirectionToYaw(toNavigation) : m_DesiredYaw;
            var night = !LightingManager.isDaytime;

            // 0.2.2x1 (doc 16.3, item 9): on high open ground the tryhard
            // stops now and then to watch the land for people.
            if (TryUpdateHighWatch(position, routeYaw, now))
            {
                return;
            }

            // A short stop, turning the view round.
            if (m_HabitNextPauseAt <= 0f)
            {
                m_HabitNextPauseAt = now + NextHabitPauseGap();
            }
            if (IsHabitPaused(now))
            {
                if (now >= m_HabitPauseNextLookAt)
                {
                    m_HabitPauseNextLookAt = now + Random.Range(0.5f, 1.2f);
                    var side = Random.value < 0.5f ? -1f : 1f;
                    m_HabitPauseLookYaw = routeYaw + side * Random.Range(35f, 175f);
                    m_HabitPauseLookPitch = 90f + Random.Range(-6f, 8f);
                }
                m_DesiredYaw = m_HabitPauseLookYaw;
                m_DesiredPitch = m_HabitPauseLookPitch;
                m_IsSprinting = false;
                return;
            }
            // 0.2.1: the first minute after being born goes straight on.
            var rushing = now < m_RespawnRushUntil;
            // 0.2.2t: the tryhard never stops in the open for a look round.
            if (now >= m_HabitNextPauseAt
                && !Tryhard
                && !rushing
                && navigationDistance >= HabitPauseRouteMeters
                && m_Player.movement.isGrounded
                && !m_PathPending)
            {
                m_HabitPauseUntil = now + Random.Range(2f, 4f);
                m_HabitPauseNextLookAt = 0f;
                m_HabitPauseCrouch = Random.value < (night ? 0.7f : 0.35f);
                m_HabitNextPauseAt = m_HabitPauseUntil + NextHabitPauseGap();
                m_HabitGlanceUntil = 0f;
                m_IsSprinting = false;
                return;
            }

            // Sprint in bursts with a breath between them.
            if (m_IsSprinting)
            {
                if (now - m_HabitLastSprintWishAt > 1f)
                {
                    m_HabitSprintUntil = 0f;
                    m_HabitRestUntil = 0f;
                }
                m_HabitLastSprintWishAt = now;
                if (now < m_HabitRestUntil)
                {
                    m_IsSprinting = false;
                }
                else if (m_HabitSprintUntil <= 0f)
                {
                    // 0.1.0-y17.27: the players' sprints and the walk between
                    // them, day and night (SkaliaBotMoveFeelY).
                    m_HabitSprintUntil = now + DrawPlayersSprintRun();
                }
                else if (now >= m_HabitSprintUntil)
                {
                    m_HabitSprintUntil = 0f;
                    m_HabitRestUntil = now + DrawPlayersWalkBetweenSprints();
                    m_IsSprinting = false;
                }
            }

            // A glance to a side; the feet keep to the way (walking sideways,
            // see UpdateNavigationAnalog). 0.1.0-y17.27: sprinting too, as far
            // as the players looked (the sprint takes any direction).
            var wantsGlance = now < m_HabitGlanceUntil || now >= m_HabitNextGlanceAt;
            if (wantsGlance && now >= m_HabitSidesCheckAt)
            {
                // Four casts: a few times a second is plenty.
                m_HabitSidesCheckAt = now + 0.25f;
                m_HabitSidesClear = navigationDistance >= HabitGlanceRouteMeters
                    && AreHabitSidesClear(position, toNavigation / Mathf.Max(0.01f, navigationDistance));
            }
            var roomForGlance = navigationDistance >= HabitGlanceRouteMeters && m_HabitSidesClear;
            if (now < m_HabitGlanceUntil)
            {
                if (roomForGlance)
                {
                    m_DesiredYaw = routeYaw + m_HabitGlanceOffset;
                }
                else
                {
                    m_HabitGlanceUntil = 0f;
                }
            }
            else if (now >= m_HabitNextGlanceAt && !rushing)
            {
                // 0.1.0-y17.27: the players' looks, how often, how far and
                // how long, sprinting or walking (SkaliaBotMoveFeelY).
                m_HabitNextGlanceAt = now + NextMoveLookGap(m_IsSprinting) / SkaliaBotModifiers.LookAround;
                if (roomForGlance && !HasRoofOverBot(position))
                {
                    var side = Random.value < 0.5f ? -1f : 1f;
                    var degrees = DrawMoveLookDegrees(m_IsSprinting);
                    m_HabitGlanceOffset = side * degrees;
                    m_HabitGlanceUntil = now + DrawMoveLookSeconds(m_IsSprinting, degrees);
                }
            }

            // A hop on open, flat ground while running.
            if (m_HabitNextHopAt <= 0f)
            {
                m_HabitNextHopAt = now + Random.Range(4f, 10f) / SkaliaBotModifiers.Hops;
            }
            if (m_IsSprinting
                && m_IsWalking
                && now >= m_HabitNextHopAt
                && now >= m_NextNavigationJumpAt
                && m_Player.movement.isGrounded
                && m_Player.life.stamina >= HabitHopStamina
                && navigationDistance >= 5f)
            {
                m_HabitNextHopAt = now + Random.Range(4f, 10f) / SkaliaBotModifiers.Hops;
                // 0.2.2t: a hop only where being hard to hit matters, in
                // the open with nothing to hide behind within 10m.
                if (IsFlatOpenAhead(position, toNavigation / navigationDistance)
                    && (!Tryhard || !HasCoverWithin(position, HopCoverMeters)))
                {
                    m_NavigationJumpUntil = now + 0.3f;
                    m_NextNavigationJumpAt = now + 1.2f;
                }
            }
        }

        private bool IsSwimmingOrClimbing()
        {
            return m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB;
        }

        private static bool HasRoofOverBot(Vector3 position)
        {
            return Physics.Raycast(
                position + Vector3.up * 1.9f,
                Vector3.up,
                20f,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Ignore);
        }

        // Nothing at the waist 1.2m to either side and the way ahead free for
        // 4m: room to walk sideways without brushing a wall or a corner.
        private bool AreHabitSidesClear(Vector3 position, Vector3 forward)
        {
            if (forward.sqrMagnitude < 0.01f)
            {
                return false;
            }
            var right = new Vector3(forward.z, 0f, -forward.x);
            var waist = position + Vector3.up * 0.9f;
            return !Physics.Raycast(waist, right, 1.2f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                && !Physics.Raycast(waist, -right, 1.2f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                && !IsPathBlocked(position, forward, 4f);
        }

        // Knee and chest clear for 4m, and the ground 2m and 4m ahead within
        // 0.4m of the feet: no step, ledge or hole to land in.
        private static bool IsFlatOpenAhead(Vector3 position, Vector3 forward)
        {
            if (forward.sqrMagnitude < 0.01f || HasRoofOverBot(position))
            {
                return false;
            }
            if (Physics.Raycast(position + Vector3.up * 0.4f, forward, 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(position + Vector3.up * 1.2f, forward, 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            for (var step = 2f; step <= 4f; step += 2f)
            {
                var probe = position + forward * step + Vector3.up * 1.5f;
                if (!Physics.Raycast(probe, Vector3.down, out var ground, 3f, RayMasks.BLOCK_COLLISION | RayMasks.GROUND | RayMasks.GROUND2, QueryTriggerInteraction.Ignore)
                    || Mathf.Abs(ground.point.y - position.y) > 0.4f
                    || WaterUtility.isPointUnderwater(ground.point + Vector3.up * 0.2f))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
