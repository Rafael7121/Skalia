using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y10 (the tester, 17/11, test of 08:31: "nao sei ficam parados o que e
    // complicado no pvp ... problema e literal PARADOS"; and before, 17/11:
    // "quando nos ficamos parado ficamos com cobertura").
    //
    // In that test Ghost stood in the open, facing the tester and doing nothing:
    // - 08:33:40-44, the Ace's one round fired at 33m and no other: stood
    //   while the tester sprinted the 33m and punched it;
    // - 08:36:05-15, the Heartbreaker empty with no magazine, the tester at
    //   14-23m shooting it with a Cobra;
    // - 08:36:31-42, the knife only, the tester walking up from 31 to 11m.
    // The blade only goes for him inside 10m (SkaliaBotPvpMeleeY), and past
    // that the bot only looked where he was, planted.
    //
    // Now with no round to fire and him armed past those 10m, the bot gets
    // out of his sight as the bandage does (SkaliaBotTryhardMove): a spot
    // with cover all round within 12m that his last seen place cannot see,
    // or 20m away from him. Out of his sight it holds, and he coming within
    // 10m is the blade's again.
    //
    // 0.2.2y11 (the tester, 17/11, test of 08:48: "juninho conseguiu fugir mas
    // sequelou, correu e ficou um bom tempo na costa"). With no cover the
    // 20m legs went on while it could see him, out to 290m, and on the
    // shore (water behind) they went back and forth 20m for over a minute.
    // Running from him is leaving him ("so faz sentido ele dar as costas se
    // ele for ABANDONAR o alvo"): one leg, then the fight ends and the
    // patrol and loot keep away from where he was (StartDisengage). With no
    // gun the bot only takes him on again inside the blade's 10m.
    //
    // 0.1.0-y17.5 (test of 18/11 13:04, Washington). A gun that does not
    // reach him is no round for him. LilJuninho, Bluntforce, hit by Vultox
    // at 118m (13:05:57), stood 24s in the open facing him at 118-129m and
    // took 4 more rounds; AlemaoGamer, the Ace and then a Rake, stood 10s
    // facing ToxicSniper at 137-157m and died (13:06:14-25). The "looking"
    // of the fight plants the bot, the look for cover stops at 80m, and
    // this leave was only for no round at all. the tester's rule for a man
    // facing it armed (item 4 of y17: "atira o quanto antes ou muda de
    // lugar para um ponto com tiro") with no spot in reach that has a shot:
    // out of his sight and leaving him, as with no round. Only when he is
    // a danger now: a hit from a player in the last 10s, or him armed and
    // facing the bot.
    //
    // Before the 20m leg, cover from where he was within 24m (the players
    // hit ran 14-25m in 4s, SkaliaBotShotSectorY): Vultox, no round, ran
    // three 20m legs across open ground and took rounds from Ghost at 81,
    // 140 and 148m (13:07:57-13:08:17).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const string PvpNoAmmoName = "sem bala, fora da vista";
        private const float PvpNoAmmoSearchSeconds = 2f;

        private float m_PvpNoAmmoSearchAt = float.NegativeInfinity;
        private bool m_PvpNoAmmoLeaving;
        private bool m_PvpNoAmmoOutOfReach;

        // 0.1.0-y17.9 (test of 18/11, 18:22:28-18:23:31): ToxicSniper, only
        // a Rake, the tester armed 15-30m off and following it. Each leave was
        // "correndo 2m (3m) para cobertura ... e deixando ele" and the fight
        // ended the same second: the trip it was on (Travel to Paradise
        // Point) took it back into the open in front of him, 15-30m, the tester
        // facing it (18:22:36-48), and later at 92-122m it was shot three
        // times on the same trip, the last while it bandaged. the tester: "ainda
        // estao tontos no reposicionamento se expondo muito". The leg's end
        // now looks at where it is:
        // - still in his sight (his eye at the place it last saw him, or
        //   him on its screen) after a leg to cover: the next cover from
        //   where he is now, at once;
        // - out of his sight and him seen in the last 10s: it stays there
        //   hidden, the face on where he was, as the players who were shot
        //   at by someone they did not see (40 to 120s, rec/m16.py,
        //   SkaliaBotShotSectorY); he showing again is a new leg, him
        //   inside 10m the blade's;
        // - otherwise, or once that hide is over: the fight ends and the
        //   patrol and loot keep away from him, as before.
        private bool m_PvpNoAmmoCoverLeg;
        private bool m_PvpNoAmmoHiding;
        // 0.1.0-y17.10 (test of 18/11, 18:40:59-18:41:03, 18:43:24,
        // 18:43:54-57): ToxicSniper, blazex and LilJuninho13, no rounds and
        // the tester in sight, went "correndo 1m para cobertura de onde ele
        // esta" 10 times a second: the cover is against where he was seen,
        // the end of the leg asks whether he still sees it (and the
        // third-person screens over the cover), so a 1m leg ended still in
        // his sight and the next one picked the same spot. A leg that
        // follows one that did not get it out of his sight has to take it
        // at least 3m from where it is; nearer than that it runs away from
        // him instead.
        private const float PvpNoAmmoNextLegMinMeters = 3f;
        private bool m_PvpNoAmmoChained;
        private float m_PvpNoAmmoNextLegLogAt = float.NegativeInfinity;

        private void EndPvpNoAmmoLeave(bool known, float now)
        {
            var from = m_PvpLastSeenPoint;
            var left = m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?";
            m_PvpNoAmmoLeaving = false;
            m_PvpNoAmmoHiding = false;
            // 0.1.0-y16.1: left, not forgotten (SkaliaBotPvpStableY).
            EndPvp(m_PvpNoAmmoOutOfReach ? "fora do alcance da minha arma, deixei ele para tras" : "sem bala, deixei ele para tras", now);
            NotePvpLeftWithoutRounds(known, now);
            StartDisengage(from, (m_PvpNoAmmoOutOfReach ? "fugir de quem nao alcanco, " : "fugir sem bala de ") + left, now);
        }

        private bool IsPvpNoAmmoMoving()
        {
            return m_ObjectiveType == ObjectiveType.Flee && m_FleeingFromPlayerName == PvpNoAmmoName;
        }

        // From TickPvpCore after the chase: true while the walk out of his
        // sight has the bot.
        private bool TickPvpNoAmmo(float distance, float gunRange, float now)
        {
            // 0.1.0-y17.12: the swarm escape already runs from him too, and
            // the group chosen to be killed is killed first
            // (SkaliaBotHordeCommitY).
            if (m_SwarmEscapeActive || m_HordeFightCommit)
            {
                return false;
            }
            if (m_PvpNoAmmoHiding && !IsPvpHuntWatching())
            {
                m_PvpNoAmmoHiding = false;
                if (!m_PvpVisible && !m_PvpCameraOnly && now - m_PvpLastSeenAt > 1f)
                {
                    EndPvpNoAmmoLeave(true, now);
                    return true;
                }
            }
            if (m_PvpNoAmmoLeaving)
            {
                if (distance <= GetPvpBladeMeters() || (gunRange > 0f && distance <= gunRange))
                {
                    // He came to the blade, or a round turned up, or he
                    // came within the gun's reach.
                    m_PvpNoAmmoLeaving = false;
                    if (IsPvpNoAmmoMoving())
                    {
                        ClearObjective();
                    }
                }
                else if (IsPvpNoAmmoMoving())
                {
                    return true;
                }
                else
                {
                    var here = m_Player.transform.position;
                    var leftName = m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?";
                    var stillSeen = IsSeenFromPvpShooter(here) || m_PvpCameraOnly || m_PvpVisible;
                    if (stillSeen && m_PvpNoAmmoCoverLeg && now - m_PvpLastSeenAt <= PvpRushLostSeconds)
                    {
                        // Still in his sight: the next cover now (below).
                        m_PvpNoAmmoLeaving = false;
                        m_PvpNoAmmoSearchAt = float.NegativeInfinity;
                        m_PvpNoAmmoChained = true;
                        if (now >= m_PvpNoAmmoNextLegLogAt)
                        {
                            m_PvpNoAmmoNextLegLogAt = now + 5f;
                            BotLog(LogLevel.Information,
                                "PvP: fim da corrida ainda na vista de {Name}; indo para a proxima cobertura em vez de seguir o caminho.",
                                leftName);
                        }
                    }
                    else if (!stillSeen && now - m_PvpLastSeenAt <= PvpActiveSeconds)
                    {
                        m_PvpNoAmmoLeaving = false;
                        m_PvpNoAmmoHiding = true;
                        m_PvpHuntPoint = m_PvpLastSeenPoint;
                        StartPvpHuntWatch(now, null);
                        m_PvpHuntUntil = now + Random.Range(PvpHideMinSeconds, PvpHideMaxSeconds);
                        m_PvpHiding = true;
                        m_PvpHideStartedAt = now;
                        m_PvpHuntKey = now;
                        BotLog(LogLevel.Information,
                            "PvP: sem bala, fora da vista de {Name} (visto ha {Ago:0}s a {Distance:0}m); fico escondido aqui por ate {Seconds:0}s em vez de seguir o caminho na frente dele.",
                            leftName,
                            now - m_PvpLastSeenAt,
                            distance,
                            m_PvpHuntUntil - now);
                        return true;
                    }
                    else
                    {
                        EndPvpNoAmmoLeave(m_PvpAware, now);
                        return true;
                    }
                }
            }
            var outOfReach = gunRange > 0f
                && distance > gunRange
                && m_PvpTarget != null
                && (now - m_LastHurtByPlayerAt <= PvpActiveSeconds || IsPvpEnemyFacingMe(m_PvpTarget));
            var wanted = (gunRange <= 0f || outOfReach)
                && m_PvpEnemyArmed
                && m_PvpAware
                && distance > GetPvpBladeMeters()
                && now - m_PvpLastSeenAt <= PvpRushLostSeconds
                && m_TreatmentPhase == 0
                && m_CachedNearestZombieDistance > PvpZombieFirstMeters
                && m_Player.stance.stance != EPlayerStance.CLIMB
                && m_Player.stance.stance != EPlayerStance.SWIM
                && m_Player.movement.getVehicle() == null;
            if (!wanted)
            {
                if (IsPvpNoAmmoMoving())
                {
                    ClearObjective();
                }
                return false;
            }
            if (IsPvpNoAmmoMoving())
            {
                return true;
            }
            // Another run (zombies, fire, the Mega) keeps the bot.
            if (m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_ObjectiveType == ObjectiveType.ExitWater
                || m_ObjectiveType == ObjectiveType.Unstuck
                || IsLadderInProgress())
            {
                return false;
            }
            var position = m_Player.transform.position;
            // 0.1.0-y16.2: him on the bot's own third-person screen is the
            // bot on his. 17:35:27-36:19 Ghost, the knife only, stood 52s
            // facing the tester 13m off on the walkway: the coil hid the eyes from
            // each other, the cameras over it did not, and the tester watched it
            // stare at him.
            if (!(IsSeenFromPvpShooter(position) || m_PvpCameraOnly) || now < m_PvpNoAmmoSearchAt)
            {
                return false;
            }
            m_PvpNoAmmoSearchAt = now + PvpNoAmmoSearchSeconds;
            var name = m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?";
            m_PvpNoAmmoOutOfReach = gunRange > 0f;
            var why = m_PvpNoAmmoOutOfReach
                ? $"{name} a {distance:0}m, fora do alcance da minha arma ({gunRange:0}m), e ele {(now - m_LastHurtByPlayerAt <= PvpActiveSeconds ? "me acertando" : "virado para mim armado")}"
                : $"sem bala e {name} armado a {distance:0}m";
            var chained = m_PvpNoAmmoChained;
            m_PvpNoAmmoChained = false;
            var minLeg = chained ? PvpNoAmmoNextLegMinMeters : 0f;
            if (TryFindCoverSpot(position, now, out var spot)
                && (spot - position).GetHorizontalMagnitude() >= minLeg)
            {
                m_PvpNoAmmoCoverLeg = true;
                m_PvpNoAmmoHiding = false;
                m_FleeingFromPlayerName = PvpNoAmmoName;
                SetObjective(ObjectiveType.Flee, spot, now + 5f);
                // 0.1.0-y16.2: in cover the fight ends as the 20m leg ends it
                // (patrol and loot away from him, him remembered). With the
                // fight going on, the trip it was on walked it back into his
                // sight: ToxicSniper, empty Peacemaker, LilJuninho at 56m,
                // went "Flee" and "Travel" by turns every 2s and stood 30s
                // at one spot (17:34:29-17:35:41).
                m_PvpNoAmmoLeaving = true;
                BotLog(LogLevel.Information,
                    "PvP: {Why}; saindo da vista dele para a cobertura a {Meters:0.#}m e deixando ele.",
                    why,
                    (spot - position).GetHorizontalMagnitude());
                return true;
            }
            // 0.1.0-y17.5: cover from where he was within 24m before the
            // blind leg (the players hit ran 14-25m in 4s).
            if (TryFindPvpWatchCover(position, m_PvpLastSeenPoint, out spot, out _, PvpCoverFarMeters)
                && (spot - position).GetHorizontalMagnitude() >= minLeg)
            {
                var way = (spot - position).GetHorizontalMagnitude();
                m_PvpNoAmmoCoverLeg = true;
                m_PvpNoAmmoHiding = false;
                m_FleeingFromPlayerName = PvpNoAmmoName;
                SetObjective(ObjectiveType.Flee, spot, now + Mathf.Clamp(way / 4f + 3f, 4f, 10f));
                m_PvpNoAmmoLeaving = true;
                BotLog(LogLevel.Information,
                    "PvP: {Why}; sem cobertura a {Meters:0}m, correndo {Way:0}m para cobertura de onde ele esta e deixando ele.",
                    why,
                    CoverSearchMeters,
                    way);
                return true;
            }
            if (TryFindPvpAwayGround(position, now, out var away))
            {
                m_PvpNoAmmoCoverLeg = false;
                m_PvpNoAmmoHiding = false;
                m_FleeingFromPlayerName = PvpNoAmmoName;
                SetObjective(ObjectiveType.Flee, away, now + 5f);
                m_PvpNoAmmoLeaving = true;
                BotLog(LogLevel.Information,
                    "PvP: {Why}; sem cobertura a {Meters:0}m, correndo {Away:0}m para longe dele e deixando ele.",
                    why,
                    PvpCoverFarMeters,
                    (away - position).GetHorizontalMagnitude());
                return true;
            }
            return false;
        }
    }
}
