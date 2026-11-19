using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x17, from the x16 test (Tadeu 32 min on the Barracks at Heritage
    // Valley, 20:36-21:09, dead of hunger at 21:09).
    //
    // 1. The zombies were 3 to 11m below him, under the ladder. The ladder
    //    was put off for "a zombie at 0.4m" measured flat, so 300 times in a
    //    row the fight asked for the ladder and the ladder was put off for
    //    the fight. The game lets a zombie hit only within
    //    GetVerticalAttackRange() up or down (Zombie.cs: 2.1m, hyper 3.5m,
    //    Mega x1.5); a zombie out of that reach no longer puts a ladder off.
    // 2. A fight whose only route is a ladder put off for this bot leaves
    //    that zombie aside until the ladder may be tried again, instead of
    //    taking it back on the next tick.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // Like GetNearestAggroedZombieDistance, only zombies that can hit
        // the bot from where they stand.
        private float GetNearestAggroedZombieDistanceInReach()
        {
            var position = m_Player.transform.position;
            var best = float.PositiveInfinity;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var offset = zombie.transform.position - position;
                if (Mathf.Abs(offset.y) >= zombie.GetVerticalAttackRange())
                {
                    continue;
                }
                var distance = offset.GetHorizontalMagnitude();
                if (distance < best
                    && ((zombie.isHunting && zombie.player == m_Player) || distance < 3f))
                {
                    best = distance;
                }
            }
            return best;
        }

        private void SetCombatTargetAsideForLadder(BotLadder ladder, float now)
        {
            if (m_ObjectiveType != ObjectiveType.Combat || !IsTargetZombieAlive())
            {
                return;
            }
            var target = m_TargetZombie!;
            var until = m_LadderRetryAfter.TryGetValue(ladder.Index, out var retry) && retry > now
                ? retry
                : now + LadderZombieRetrySeconds;
            if (m_UnreachableZombies.TryGetValue(target, out var marked) && marked >= until)
            {
                return;
            }
            m_UnreachableZombies[target] = until;
            m_UnreachableZombieDistance.Remove(target);
            var offset = target.transform.position - m_Player.transform.position;
            BotLog(LogLevel.Information,
                "Zumbi a {Distance:0.#}m (altura {Dy:0.#}m) so tem rota pela escada {Name}, adiada para este bot: deixado de lado por {Seconds:0}s.",
                offset.GetHorizontalMagnitude(),
                offset.y,
                ladder.Name,
                until - now);
        }
    }
}
