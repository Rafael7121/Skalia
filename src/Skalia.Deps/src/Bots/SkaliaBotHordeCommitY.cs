using Microsoft.Extensions.Logging;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.12 (the tester, 18/11: "Ou ele vai MATAR todos os zumbis da
    // horda ou ele vai DESPISTAR TODO MUNDO inclusive eu/bot. ele nunca pode
    // ficar no meio termo; bato ou recuo, bato ou recuo").
    //
    // Test of 18/11, 22:30:24-22:31:00: LilJuninho, Chainsaw in hand and 3
    // to 5 commons on it, went Combat, EscapeSwarm, the route after the tester,
    // EscapeSwarm, Flee, a 4s fight, Patrol, Combat: 10 changes of objective
    // in 30s. Each pass of the escape asked again whether to fight (health,
    // stamina), and the stalled escape gave way to a fight of only 4s.
    //
    // Now the choice is made once:
    // - 3+ on the bot and the small-group fight says fight: it fights until
    //   no zombie is after it within 8m (none within 4m, none on it). No
    //   escape, no low-health run, no route after a man in the meantime;
    // - the escape is the other choice: it runs until the escape's own end,
    //   from the zombies and from the man it fights (FindZombieEscapePoint),
    //   with no route after him and no blade at him (SkaliaBotPvpChaseY,
    //   SkaliaBotPvpY);
    // - an escape that stalls or runs out of breath with a zombie on the bot
    //   (TryYieldSwarmToFight) becomes the fight to the end, not 4s.
    // The Mega within 10m still ends the fight (the boss flee's own rule),
    // and so does a weapon that can no longer hit.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private bool m_HordeFightCommit;
        private float m_HordeFightCommitSince;
        private int m_HordeFightCommitHealth;
        private string m_HordeFightCommitWhy = string.Empty;

        private void StartHordeFightCommit(string why, float now)
        {
            m_HordeFightCommit = true;
            m_HordeFightCommitSince = now;
            m_HordeFightCommitHealth = m_Player.life.health;
            m_HordeFightCommitWhy = why;
            BotLog(LogLevel.Information,
                "Horda: vou matar todos ({Why}): {Count} zumbis a 4m, atras de mim a 8m {Hunters8}; vida {Health}, stamina {Stamina}; arma {Weapon}. Sem fuga ate acabar.",
                why,
                m_CachedSurroundedZombieCount,
                m_ThreatHunters8,
                m_Player.life.health,
                m_Player.life.stamina,
                DescribeEquipped());
        }

        // From GetSwarmUtility each tick: true while the fight goes on.
        private bool UpdateHordeFightCommit(float now)
        {
            if (!m_HordeFightCommit)
            {
                return false;
            }
            string? end = null;
            if (m_Player.life.isDead)
            {
                m_HordeFightCommit = false;
                return false;
            }
            if (m_CachedNearestBoss != null
                && !m_CachedNearestBoss.isDead
                && m_CachedNearestBossDistance < MegaYieldMeters)
            {
                end = "o Mega chegou a " + m_CachedNearestBossDistance.ToString("0") + "m";
            }
            else if (!HasUsableCombatEquipment())
            {
                end = "sem arma que acerte";
            }
            else if (m_CachedSurroundedZombieCount == 0
                     && m_ThreatHunters8 == 0
                     && m_CachedNearestZombieDistance > CaughtByZombieMeters)
            {
                end = "nenhum zumbi atras de mim a 8m";
            }
            if (end == null)
            {
                return true;
            }
            m_HordeFightCommit = false;
            BotLog(LogLevel.Information,
                "Horda: luta encerrada depois de {Seconds:0}s ({End}); comecou {Why} com vida {Start}, termina com vida {Health}, stamina {Stamina}.",
                now - m_HordeFightCommitSince,
                end,
                m_HordeFightCommitWhy,
                m_HordeFightCommitHealth,
                m_Player.life.health,
                m_Player.life.stamina);
            return false;
        }
    }
}
