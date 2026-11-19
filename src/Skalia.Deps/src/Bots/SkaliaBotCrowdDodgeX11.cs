using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x11: out of the press of zombies the way the tester showed it.
    //
    // llo, 02:14:37-02:14:42 (x10 test): 3 common zombies at 0.7-0.9m, the
    // swarm escape routed 25m away along a route that hit a mesh edge, the
    // bot stood 2s, the escape gave way to a knife fight and it died from
    // 100 health in 5s. the tester took the same 3 zombies right there
    // (recording 2026-11-13 02:14:03, 40-143s) and let them close in on him
    // 6 times: each time he was out in under a second and took no hit while
    // moving. He never stood still among them: a sprint to the open side,
    // forwards, sideways or backwards facing them (the sprint works in all 8
    // directions), often with a jump.
    //
    // The game (Zombie.cs): a common zombie on a dedicated server reaches
    // 1m (sprinter and crawler 1.41m) and 2.1m up; it has to stay in reach
    // 0.5s before it swings, and the blow lands half a swing later only if
    // the player is still in reach. Out of reach for a moment cancels the
    // swing. So the bot keeps moving: toward the side with no zombie, away
    // from walls and drops, holding the way it chose for a moment, sprinting
    // in whatever direction that is while it keeps facing the closest one.
    //
    // With no open side (against the container at 79-80s, three around him
    // at 86s, 89s, 92s, 112s, 119s, 121s, 123s) the tester jumped straight up as
    // they touched him, landed on their heads (1.9-2.4m over the ground,
    // over the 2.1m a zombie reaches up), stood a moment and then stepped
    // off at the top of the jump to one side, over them, and was 2-3m away
    // when he touched the ground: 7 of 9 times without a hit. Here: no open
    // side, a straight jump with no key for 0.3s, then the way with the
    // fewest zombies, now over their heads. Fighting only without the
    // stamina for the jump.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float CrowdReachMeters = 1.6f;
        private const float CrowdVerticalMeters = 2.1f;
        private const float CrowdNearMeters = 4f;
        private const float CrowdThreatMeters = 7f;
        private const float CrowdClearMeters = 2.4f;
        private const float CrowdMinimumSeconds = 0.6f;
        private const float CrowdMaximumSeconds = 8f;
        private const float CrowdStepMeters = 1.6f;
        private const float CrowdPickSeconds = 0.2f;
        private const int CrowdDirections = 16;
        // 0.2.2x14, the players' recordings (24, 11.4h) with a blade and 2-5
        // zombies after them, one within 2m: a sprint lasts 0.6s (median,
        // p75 1.26s) and never has a jump (0 in 27), starts with 33+ stamina
        // (p10); out of reach (1.2m to 1.8m) in 0.5s (median, p10 0.17s);
        // 0.82s between two hits (median).
        private const float CrowdKiteMaximumSeconds = 1.26f;
        private const float CrowdKiteMinimumSeconds = 0.17f;
        private const float CrowdKiteClearMeters = 1.8f;
        private const float CrowdKiteCooldownSeconds = 0.82f;
        private const float CrowdKiteSprintStamina = 33f;
        private bool m_CrowdDodgeKite;

        private bool m_CrowdDodgeActive;
        private float m_CrowdDodgeStartedAt;
        private float m_CrowdDodgeClearSince = -1f;
        private float m_CrowdDodgeCooldownUntil;
        private float m_CrowdDodgeNextPickAt;
        private float m_CrowdDodgeNextJumpAt;
        private Vector3 m_CrowdDodgeDirection;
        private byte m_CrowdDodgeAnalog = 0x11;
        private byte m_CrowdDodgeHealth;
        private int m_CrowdDodgeJumps;
        private int m_CrowdDodgeStartReach;
        private int m_CrowdDodgeStartNear;
        private float m_CrowdDodgeTravel;
        private Vector3 m_CrowdDodgeLastPosition;
        private readonly List<Vector3> m_CrowdThreats = new List<Vector3>();

        // True while this steers the body.
        private bool UpdateCrowdDodge(Vector3 position, float now)
        {
            var unable = m_Player.life.isDead
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_ObjectiveType == ObjectiveType.Ladder
                || m_BoulderDodgePhase != 0;
            CountCrowd(position, out var inReach, out var near, out var nearest, out var nearestDistance);
            // The Mega has its own flight (SkaliaBotTryhardMega).
            var megaClose = m_CachedNearestBoss != null
                && !m_CachedNearestBoss.isDead
                && m_CachedNearestBossDistance < 8f;
            if (!m_CrowdDodgeActive)
            {
                // 0.2.2x14: with a stunning blade and up to 5 after it, only
                // two about to swing move the bot, for a step.
                // 0.1.0-y17.13: the group it chose to kill, the same: a
                // step, then the next blow or shot (SkaliaBotHordeCommitY).
                var kite = IsStunKiteCrowd(position) || m_HordeFightCommit;
                if (unable
                    || megaClose
                    || now < m_CrowdDodgeCooldownUntil
                    || !(kite ? inReach >= 2 : (inReach >= 2 || (inReach >= 1 && near >= 3))))
                {
                    return false;
                }
                m_CrowdDodgeKite = kite;
                m_CrowdDodgeActive = true;
                m_CrowdDodgeStartedAt = now;
                m_CrowdDodgeClearSince = -1f;
                m_CrowdDodgeNextPickAt = 0f;
                m_CrowdDodgeDirection = Vector3.zero;
                m_CrowdDodgeHealth = m_Player.life.health;
                m_CrowdDodgeJumps = 0;
                m_CrowdDodgeHeadJumps = 0;
                m_CrowdHeadJumpAt = 0f;
                m_CrowdDodgeOpen = true;
                m_CrowdDodgeStartReach = inReach;
                m_CrowdDodgeStartNear = near;
                m_CrowdDodgeTravel = 0f;
                m_CrowdDodgeLastPosition = position;
            }
            else
            {
                // 0.1.0-y17.13 (the tester, 18/11 23:03, "ainda ta bugado em
                // horda"): LilJuninho chose at 23:02:55 to kill the 8 on it
                // (Heartbreaker, 28 rounds, health 70) and then ran 33m in 8s
                // three times in a row (23:02:52, 23:03:01, 23:03:09) with
                // them at 0.9-1.3m, firing nothing for 6-7s each time: the
                // press's run lasts until it is clear or 8s, and runners keep
                // pace. A dodge that was running when the fight was chosen
                // becomes the step: out of reach for a moment, then fight.
                if (m_HordeFightCommit && !m_CrowdDodgeKite)
                {
                    m_CrowdDodgeKite = true;
                }
                var step = position - m_CrowdDodgeLastPosition;
                step.y = 0f;
                if (step.magnitude < 2f)
                {
                    m_CrowdDodgeTravel += step.magnitude;
                }
                m_CrowdDodgeLastPosition = position;
                var clear = inReach == 0 && nearestDistance > CrowdClearMeters;
                if (!clear)
                {
                    m_CrowdDodgeClearSince = -1f;
                }
                else if (m_CrowdDodgeClearSince < 0f)
                {
                    m_CrowdDodgeClearSince = now;
                }
                var elapsed = now - m_CrowdDodgeStartedAt;
                // Walled in and no breath left for a jump onto their heads:
                // fighting beats pushing against a wall (4s before the next try).
                if (elapsed >= 1.5f
                    && m_CrowdDodgeTravel < 1.5f
                    && !clear
                    && m_Player.life.stamina < 10
                    && m_Player.movement.isGrounded)
                {
                    EndCrowdDodge(now, nearestDistance, "sem saida e sem folego para pular, lutando");
                    m_CrowdDodgeCooldownUntil = now + 4f;
                    return false;
                }
                if (m_CrowdDodgeKite
                    && (elapsed >= CrowdKiteMaximumSeconds
                        || (inReach == 0 && nearestDistance > CrowdKiteClearMeters && elapsed >= CrowdKiteMinimumSeconds)))
                {
                    // One step out of reach, then the next blow.
                    EndCrowdDodge(now, nearestDistance, elapsed >= CrowdKiteMaximumSeconds ? "passo do kite, tempo" : "passo do kite");
                    m_CrowdDodgeCooldownUntil = now + CrowdKiteCooldownSeconds;
                    return false;
                }
                if (unable
                    || megaClose
                    || elapsed >= CrowdMaximumSeconds
                    || (clear && elapsed >= CrowdMinimumSeconds && now - m_CrowdDodgeClearSince >= 0.3f))
                {
                    EndCrowdDodge(now, nearestDistance, unable ? "outra acao" : megaClose ? "Mega perto" : clear ? "saiu do alcance" : "tempo maximo");
                    return false;
                }
            }

            var grounded = m_Player.movement.isGrounded;
            var overHeads = m_CrowdHeadJumpAt > 0f;
            if (overHeads && grounded && now - m_CrowdHeadJumpAt > 0.5f)
            {
                // Down again.
                m_CrowdHeadJumpAt = 0f;
                overHeads = false;
            }
            // Standing on them (the game counts that as ground): walk off
            // over them to the side with the fewest.
            var onHeads = nearest != null && position.y - nearest.transform.position.y > 1.2f;
            if (onHeads && !overHeads)
            {
                overHeads = true;
                m_CrowdDodgeNextPickAt = 0f;
            }
            if (now >= m_CrowdDodgeNextPickAt)
            {
                m_CrowdDodgeNextPickAt = now + CrowdPickSeconds;
                m_CrowdDodgeDirection = PickCrowdDodgeDirection(position, overHeads, out m_CrowdDodgeOpen);
            }
            m_IsWalking = true;
            // 0.2.2x14: the kite's step sprints only with stamina to spare.
            m_IsSprinting = m_CrowdDodgeKite
                ? m_Player.life.stamina >= CrowdKiteSprintStamina
                : m_Player.life.stamina > 0;
            if (m_CrowdDodgeKite
                && nearest != null
                && nearest != m_TargetZombie
                && m_ObjectiveType == ObjectiveType.Combat
                && nearestDistance <= 1.8f
                && now >= m_NextStunRetargetAt
                && IsStandUpZombie(nearest))
            {
                // Facing the nearest one: the blow goes to it.
                m_NextStunRetargetAt = now + 0.3f;
                ForceCombatTarget(nearest, now);
            }
            m_StealthCrouch = false;
            m_PrimaryAttackHeld = false;
            m_SecondaryAttackHeld = false;
            // Facing the closest one, as the tester ran backwards from them.
            if (nearest != null)
            {
                var toNearest = nearest.transform.position - position;
                toNearest.y = 0f;
                if (toNearest.sqrMagnitude > 0.01f)
                {
                    m_DesiredYaw = DirectionToYaw(toNearest);
                }
            }
            var delta = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, m_DesiredYaw));
            m_CurrentYaw = Mathf.MoveTowardsAngle(m_CurrentYaw, m_DesiredYaw, GetHumanTurnSpeed(delta, true) * TurnStepSeconds);
            m_CrowdDodgeAnalog = AnalogToward(m_CrowdDodgeDirection, m_CurrentYaw);
            // No open side and two on the bot: straight up onto their heads.
            if (!overHeads
                && !onHeads
                && grounded
                && !m_CrowdDodgeOpen
                && inReach >= 2
                && nearestDistance <= 1.2f
                && now >= m_CrowdDodgeNextJumpAt
                && m_Player.life.stamina >= 10)
            {
                m_CrowdHeadJumpAt = now;
                m_CrowdDodgeNextJumpAt = now + 0.9f;
                m_NavigationJumpUntil = now + 0.3f;
                m_CrowdDodgeJumps++;
                m_CrowdDodgeHeadJumps++;
                overHeads = true;
            }
            if (overHeads && now - m_CrowdHeadJumpAt < 0.3f)
            {
                // Rising: no key, so the jump goes up and not into them.
                m_CrowdDodgeAnalog = 0x11;
                m_IsSprinting = false;
                m_CrowdDodgeNextPickAt = 0f;
                return true;
            }
            // An open side: a jump on the run when one touches the bot, as
            // the tester did at 0.9m.
            if (!overHeads
                && !m_CrowdDodgeKite
                && nearestDistance <= 1.2f
                && grounded
                && now >= m_CrowdDodgeNextJumpAt
                && m_Player.life.stamina >= 10)
            {
                m_CrowdDodgeNextJumpAt = now + 1f;
                m_NavigationJumpUntil = now + 0.3f;
                m_CrowdDodgeJumps++;
            }
            return true;
        }

        private float m_CrowdHeadJumpAt;
        private bool m_CrowdDodgeOpen = true;
        private int m_CrowdDodgeHeadJumps;

        private void EndCrowdDodge(float now, float nearestDistance, string why)
        {
            m_CrowdDodgeActive = false;
            m_CrowdDodgeCooldownUntil = now + 0.4f;
            var lost = m_CrowdDodgeHealth - m_Player.life.health;
            BotLog(LogLevel.Information,
                "Saiu do aperto de zumbis ({Why}) em {Seconds:0.0}s: comecou com {Reach} ao alcance e {Near} a 4m; correu {Travel:0.#}m sem parar, {Jumps} pulo(s) ({HeadJumps} por cima das cabecas, sem lado livre); {Damage} de dano; zumbi mais perto a {Distance:0.#}m.",
                why,
                now - m_CrowdDodgeStartedAt,
                m_CrowdDodgeStartReach,
                m_CrowdDodgeStartNear,
                m_CrowdDodgeTravel,
                m_CrowdDodgeJumps,
                m_CrowdDodgeHeadJumps,
                Mathf.Max(0, lost),
                nearestDistance);
        }

        private void CountCrowd(Vector3 position, out int inReach, out int near, out Zombie? nearest, out float nearestDistance)
        {
            inReach = 0;
            near = 0;
            nearest = null;
            nearestDistance = float.MaxValue;
            m_CrowdThreats.Clear();
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var zombiePosition = zombie.transform.position;
                if (Mathf.Abs(zombiePosition.y - position.y) >= CrowdVerticalMeters + 0.4f)
                {
                    continue;
                }
                var distance = (zombiePosition - position).GetHorizontalMagnitude();
                if (distance <= CrowdThreatMeters)
                {
                    m_CrowdThreats.Add(zombiePosition);
                }
                if (distance <= CrowdReachMeters && Mathf.Abs(zombiePosition.y - position.y) < CrowdVerticalMeters)
                {
                    inReach++;
                }
                if (distance <= CrowdNearMeters)
                {
                    near++;
                }
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = zombie;
                }
            }
        }

        // The open side: away from every zombie (the close ones count much
        // more), not into a wall or off a drop, and keeping the way taken.
        // Over their heads the zombies are no wall: only walls and drops and
        // where they are thickest count. open: the best way has no wall and
        // no zombie within 2.5m in front of it.
        private Vector3 PickCrowdDodgeDirection(Vector3 position, bool overHeads, out bool open)
        {
            var best = m_CrowdDodgeDirection;
            var bestScore = float.NegativeInfinity;
            open = false;
            var hasPrevious = m_CrowdDodgeDirection.sqrMagnitude > 0.5f;
            var toObjective = Vector3.zero;
            if (m_ObjectiveType == ObjectiveType.EscapeSwarm)
            {
                toObjective = m_ObjectivePosition - position;
                toObjective.y = 0f;
                toObjective = toObjective.sqrMagnitude > 1f ? toObjective.normalized : Vector3.zero;
            }
            for (var i = 0; i < CrowdDirections; i++)
            {
                var radians = i * (360f / CrowdDirections) * Mathf.Deg2Rad;
                var direction = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
                var score = 0f;
                var free = true;
                foreach (var threat in m_CrowdThreats)
                {
                    var toThreat = threat - position;
                    toThreat.y = 0f;
                    var distance = Mathf.Max(toThreat.magnitude, 0.4f);
                    var dot = Vector3.Dot(direction, toThreat / distance);
                    score -= dot / (distance * distance);
                    // Straight into one that is already close.
                    if (distance <= 2.5f && dot > 0.64f)
                    {
                        free = false;
                        if (!overHeads && dot > 0.82f)
                        {
                            score -= 3f;
                        }
                    }
                }
                if (IsBodyRouteBlocked(position, direction, CrowdStepMeters))
                {
                    score -= 4f;
                    free = false;
                }
                var ahead = position + direction * CrowdStepMeters;
                if (!Physics.Raycast(ahead + Vector3.up * 1f, Vector3.down, out var ground, 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    score -= 6f;
                }
                else if (ground.point.y < position.y - 1.6f)
                {
                    score -= 2f;
                }
                if (hasPrevious)
                {
                    score += 0.35f * Vector3.Dot(direction, m_CrowdDodgeDirection);
                }
                score += 0.25f * Vector3.Dot(direction, toObjective);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = direction;
                    open = free;
                }
            }
            return best;
        }
    }
}
