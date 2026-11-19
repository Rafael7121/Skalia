using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t (doc 11.5): kill fast. The tower fight of the 0.2.2 test took
    // 24s for one common zombie: the bot stepped back after every blow or
    // two, stood under a zombie 2.1m up, and only noticed after 12s.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LoneFightMeters = 7f;
        private const int LoneFightBurst = 3;
        private const float TryhardMeleeProgressSeconds = 4f;
        private const float CloseTargetKeepMeters = 3f;
        private const float CombatForceRouteSeconds = 3f;

        private const float TryhardCommitMeters = 30f;
        private Zombie? m_CommittedZombie;

        private float m_CombatForceRouteUntil;
        private float m_NextCloseTargetLogAt;
        private float m_NextForceRouteLogAt;

        // One common zombie and nothing else around: no step back between
        // blows, the burst lasts until it is dead (3 blows at most).
        private bool IsLoneCommonFight(Zombie target, Vector3 position)
        {
            return Tryhard
                && target != null
                && IsCommonZombie(target)
                && CountZombiesNear(position, LoneFightMeters, target) == 0;
        }

        private int GetMeleeBurstTarget(Zombie target)
        {
            return IsLoneCommonFight(target, m_Player.transform.position)
                ? Mathf.Max(m_MeleeBurstTargetCount, LoneFightBurst)
                : m_MeleeBurstTargetCount;
        }

        private float GetCombatProgressTimeout(Vector3 position)
        {
            // With a loaded gun in hand a few seconds of missed shots are
            // not "no progress", and a bot covering ground is on a route
            // round something: both keep the old 12s. Standing in place
            // with a blade, 4s.
            return Tryhard
                   && !(m_Player.equipment.asset is ItemGunAsset gun && IsEquippedGunLoaded(gun))
                   && (position - m_CombatProgressStartPosition).GetHorizontalMagnitude() < 4f
                ? TryhardMeleeProgressSeconds
                : CombatProgressTimeoutSeconds;
        }

        // A zombie on the same level within 3m is in reach: it is not put on
        // the ignore list (it would hit the bot in the back); the route is
        // thrown away and the fight goes on.
        private bool TryKeepCloseCombatTarget(Zombie target, Vector3 position, float now)
        {
            if (!Tryhard || m_FloorRouteActive)
            {
                return false;
            }
            var offset = target.transform.position - position;
            if (Mathf.Abs(offset.y) > 1.2f || offset.GetHorizontalMagnitude() > CloseTargetKeepMeters)
            {
                return false;
            }
            // 0.2.2x (doc 15.2, item 7): the second time, the gun or let go.
            var gunTakesOver = TryResolveStuckCloseFight(target, position, Time.realtimeSinceStartup, out var dropIt);
            if (dropIt)
            {
                return false;
            }
            m_CombatProgressSince = now;
            m_CombatProgressBestDistance = offset.GetHorizontalMagnitude();
            m_CombatMovementPhase = CombatMovementPhase.Approach;
            m_CombatRetreatEndsAt = 0f;
            m_CombatRetreatPending = false;
            m_MeleeBurstAttackCount = 0;
            ClearPath();
            if (gunTakesOver)
            {
                return true;
            }
            if (now >= m_NextCloseTargetLogAt)
            {
                m_NextCloseTargetLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "Luta sem progresso por {Seconds:0}s com o zumbi {Kind} a {Distance:0.#}m, no alcance, em {Position}: nao abandona, refaz a aproximacao.",
                    GetCombatProgressTimeout(position),
                    DescribeZombieKind(target),
                    offset.GetHorizontalMagnitude(),
                    FormatPosition(position));
            }
            return true;
        }

        // No safe straight step for a second and the zombie is not in reach
        // of the blade: follow the mesh route to it instead of standing.
        private bool TryForceCombatRoute(Vector3 position, float combatDistance, float attackDistance, float now)
        {
            if (!Tryhard
                || m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || now - m_CombatSteeringBlockedSince < 1f
                || now < m_CombatForceRouteUntil)
            {
                return false;
            }
            var offset = m_TargetZombie!.transform.position - position;
            var inReach = combatDistance <= attackDistance + 0.3f && Mathf.Abs(offset.y) <= 1.2f;
            if (inReach)
            {
                return false;
            }
            m_CombatForceRouteUntil = now + CombatForceRouteSeconds;
            ClearPath();
            if (now >= m_NextForceRouteLogAt)
            {
                m_NextForceRouteLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "Sem passo seguro em linha reta ate o zumbi a {Distance:0.#}m (altura {Dy:0.#}m) em {Position}: seguindo a rota ate ele.",
                    offset.GetHorizontalMagnitude(),
                    offset.y,
                    FormatPosition(position));
            }
            return true;
        }

        // How far ahead a combat step is checked for ground. 2.5m is more
        // than a tower platform has: with the zombie on the same platform
        // within 3m, the step toward it is checked only as far as the zombie
        // (it is standing on that ground).
        private float GetCombatStepProbeMeters(Vector3 position, Vector3 direction)
        {
            const float normal = 2.5f;
            if (!Tryhard || !IsTargetZombieAlive() || direction.sqrMagnitude < 0.01f)
            {
                return normal;
            }
            var toZombie = m_TargetZombie!.transform.position - position;
            if (Mathf.Abs(toZombie.y) > 1.2f)
            {
                return normal;
            }
            toZombie.y = 0f;
            var distance = toZombie.magnitude;
            if (distance > CloseTargetKeepMeters
                || distance < 0.3f
                || Vector3.Dot(direction.normalized, toZombie / distance) < 0.6f)
            {
                return normal;
            }
            return Mathf.Clamp(distance, 0.8f, normal);
        }
    }
}
