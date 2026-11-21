using Microsoft.Extensions.Logging;
using SDG.Unturned;
using SDG.Framework.Water;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y2 (short test of 17/11, 00:44): Vultox kept shooting back at
    // the tester standing in the open down to 21 health with 9 medical items in
    // the bag, and died. The bandage was barred for the whole fight.
    //
    // What the players of 08/11 did at 35 health or less in a fight
    // (rec/pvp11.py, pvp12.py; 15 times):
    // - 10 took a medical item in hand 0 to 1.6s after (0, 0, 0, 0.4, 0.6,
    //   0.6, 0.7, 1.2, 1.5, 1.6s); 7 of them were alive 15s later.
    // - The other 5 died 1.2 to 7.5s after without one; 3 of them were
    //   still shooting.
    // - Of the 10, 5 sprinted almost the whole 4s after (53 to 62m in 10s
    //   when they did not die), the others healed where they were, two of
    //   them prone.
    // The bot does the same: at 35 or less the bandage goes on after the
    // drawn wait, the fight stops while it is in hand, and the bandage is
    // finished behind cover the shooter cannot see, or running away from
    // him when there is none in 12m.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // 0.1.0-y17.34: over serverBot:modifiers:courage.
        private static float PvpBreakHealth => 35f / SkaliaBotModifiers.Courage;
        private static readonly float[] s_PvpBreakQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpBreakSeconds = { 0f, 0f, 0.6f, 1.2f, 1.6f };
        // Players ran 53 to 62m in 10s; the bandage takes about 4s.
        private const float PvpHealRunMeters = 20f;
        private const string PvpHealRunName = "curativo longe do atirador";

        private float m_PvpBreakAt = float.PositiveInfinity;

        // 0.2.2y6 (test of 17/11, 07:09:45): Ghost at 35 took the bandage
        // out with the tester charging at 2.8m, turned and ran, and was punched
        // to death. At 35 or less with the other within 10m (rec/pvp19.py)
        // 4 of 5 players shot at once (0.0-0.1s) and none healed first; the
        // one who healed shot first and took it out 1.2s later at 7.5m. At
        // 14-18m both healed (0.4 and 1.6s).
        private const float PvpBreakCloseMeters = 10f;
        private const float PvpBreakCloseSeenSeconds = 2f;

        // He is right there: the bandage would be a free kill.
        private bool IsPvpEnemyTooCloseToHeal(float now)
        {
            return m_PvpTarget != null
                && m_PvpAware
                && now - m_PvpLastSeenAt <= PvpBreakCloseSeenSeconds
                && (m_PvpLastSeenPoint - m_Player.look.aim.position).magnitude <= PvpBreakCloseMeters;
        }

        // 0.2.2y11 (test of 08:54, 08:56:25): LilJuninho, AK in hand, was
        // clubbed by Ghost at 2m with 61 health and bleeding; the low health
        // flee from a zombie 14m away turned it round, and it ran with Ghost
        // on its back until it died, 0 rounds fired. The players at low
        // health with the enemy within 10m shot at once (4 of 5, none healed
        // first, rec/pvp19.py). With the man it fights within 10m, seen or
        // hit by him in the last 2s, the zombie flee waits.
        private bool IsPvpEnemyOnMe(float now)
        {
            var target = m_PvpTarget;
            return target != null
                && target.life != null
                && !target.life.isDead
                && (now - m_PvpLastSeenAt <= PvpBreakCloseSeenSeconds || now - m_PvpLastHitAt <= PvpBreakCloseSeenSeconds)
                && (target.transform.position - m_Player.transform.position).magnitude <= PvpBreakCloseMeters;
        }

        // From TryTreatWounds: true lets the bandage go on in the middle of
        // the fight.
        private bool IsPvpLowHealthBreak(float now)
        {
            var health = m_Player.life.health;
            if (health > PvpBreakHealth || m_PvpTarget == null)
            {
                m_PvpBreakAt = float.PositiveInfinity;
                return false;
            }
            if (IsPvpEnemyTooCloseToHeal(now))
            {
                return false;
            }
            if (float.IsPositiveInfinity(m_PvpBreakAt))
            {
                var wait = DrawFromQuantiles(s_PvpBreakQuantiles, s_PvpBreakSeconds) / SkaliaBotModifiers.HealSpeed;
                m_PvpBreakAt = now + wait;
                BotLog(LogLevel.Information,
                    "PvP: vida {Health} na luta com {Name}; curativo em {Wait:0.0}s.",
                    health,
                    m_PvpTarget.channel?.owner?.playerID.characterName ?? "?",
                    wait);
            }
            return now >= m_PvpBreakAt;
        }

        // The shooter's view of a point, from where the bot last saw him.
        // Nothing is known of him before he was found: no wallhack here.
        private bool IsSeenFromPvpShooter(Vector3 ground)
        {
            // 0.1.0-y16.1: also the one it left with no rounds
            // (SkaliaBotPvpStableY).
            if (!IsPvpShooterKnown(Time.realtimeSinceStartup))
            {
                return false;
            }
            return !Physics.Linecast(
                m_PvpLastSeenPoint,
                ground + Vector3.up * 1.2f,
                s_PvpSightMask,
                QueryTriggerInteraction.Ignore);
        }

        // No cover in 12m with the shooter found: away from him, as the
        // players who sprinted with the bandage in hand.
        private bool TryStartPvpHealRun(Vector3 position, float now)
        {
            if (!IsPvpShooterKnown(now))
            {
                return false;
            }
            if (!TryFindPvpAwayGround(position, now, out var onMesh))
            {
                return false;
            }
            m_FleeingFromPlayerName = PvpHealRunName;
            SetObjective(ObjectiveType.Flee, onMesh, now + 5f);
            BotLog(LogLevel.Information,
                "Curativo: sem cobertura fora da vista do atirador a {Meters:0}m; correndo {Distance:0}m para longe dele com o curativo na mao.",
                CoverSearchMeters,
                (onMesh - position).GetHorizontalMagnitude());
            return true;
        }

        // 20m straight away from where he was seen, or up to 90 degrees off.
        private bool TryFindPvpAwayGround(Vector3 position, float now, out Vector3 onMesh)
        {
            onMesh = position;
            var away = position - m_PvpLastSeenPoint;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                return false;
            }
            var baseYaw = DirectionToYaw(away);
            var turns = new[] { 0f, 45f, -45f, 90f, -90f };
            foreach (var turn in turns)
            {
                var direction = Quaternion.Euler(0f, baseYaw + turn, 0f) * Vector3.forward;
                if (!TryGetWalkableGround(position + direction * PvpHealRunMeters, out var grounded)
                    || Mathf.Abs(grounded.y - position.y) > 4f
                    || WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                    || IsHazardousPosition(grounded, now, 1.5f)
                    || IsInRadiation(grounded)
                    || IsWithinMegaReach(grounded)
                    || !SkaliaBotUnityNavmesh.TrySnap(grounded, 1.5f, out onMesh)
                    || (onMesh - grounded).GetHorizontalMagnitude() > 1.5f)
                {
                    continue;
                }
                return true;
            }
            onMesh = position;
            return false;
        }
    }
}
