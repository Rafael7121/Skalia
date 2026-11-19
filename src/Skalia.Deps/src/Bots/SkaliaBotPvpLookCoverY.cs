using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.3 (the tester, 18/11, Paradise Point: "eu atiro ele escuta mas
    // fica parado exposto observando da onde veio o som, nao vai pra
    // cobertura, nao busca flankear/recuar da a volta"). ToxicSniper,
    // 12:08:28-12:09:08: the tester 39m off seen only through its third-person
    // camera (no line from the eye, so no shot), and it stood 38s facing
    // him in his line, in the open.
    //
    // Looking at a man it cannot shoot, with its chest in his line: not
    // planted there. Cover within 6m that a straight step reaches is taken
    // sideways or backwards with the face on him, as the fight's cover
    // (SkaliaBotPvpFightCoverY); with none, a run to cover within 24m (the
    // players hit ran 14-25m in 4s), or around him as the players who went
    // round a man they had lost (SkaliaBotPvpHuntY: 0.5-0.8 of the distance
    // across, 10-20s). From the cover the lean out (TickPvpPeek) shows him.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const string PvpRepositionName = "reposicionar na luta";
        private const float PvpLookCoverEverySeconds = 2.5f;
        private const float PvpRepositionEverySeconds = 8f;

        private Vector3 m_PvpLookCoverSpot;
        private float m_PvpLookCoverUntil;
        private float m_PvpLookCoverNextAt;
        private float m_PvpRepositionNextAt;
        private bool m_PvpRepositionActive;
        private Vector3 m_PvpRepositionSpot;

        private bool IsPvpRepositioning()
        {
            return m_ObjectiveType == ObjectiveType.Flee && m_FleeingFromPlayerName == PvpRepositionName;
        }

        // On the way to a man, around him or to cover from him. Same test
        // (12:05:02): LilJuninho set off on a 28m run round Ghost, took
        // painkillers out on the first step, ran on with them and then with
        // empty hands (the rearm waited for the next item), met Ghost at
        // 24m and died with 0 rounds fired. The players of 08/11 in the 2
        // minutes after a fight (rec/m17e.py): moving toward where the
        // other was, gun in hand in 76% of the samples, a medical item in
        // 2%; with one in hand they stayed or went away (median change of
        // the distance 0m, 56 times, never more than 9m nearer). So no
        // bandage starts on these runs (at 35 or less the fight's own
        // break still does, SkaliaBotPvpHealY), and the gun comes back as
        // soon as the hands are empty.
        private bool IsPvpMovingOnHim()
        {
            return m_PvpTarget != null && (IsPvpHunting() || IsPvpChasing() || IsPvpRepositioning());
        }

        // From TickPvpCore while looking (not shooting): true when the step
        // is set here.
        private bool TickPvpLookCover(Player target, float now)
        {
            var position = m_Player.transform.position;
            if (m_PvpRepositionActive && !IsPvpRepositioning())
            {
                m_PvpRepositionActive = false;
                if ((m_PvpRepositionSpot - position).GetHorizontalMagnitude() < 2f)
                {
                    m_PvpCoverReachedAt = now;
                }
            }
            if (now < m_PvpLookCoverUntil)
            {
                var to = m_PvpLookCoverSpot - position;
                if (to.GetHorizontalMagnitude() < 0.6f)
                {
                    m_PvpLookCoverUntil = 0f;
                    m_PvpCoverReachedAt = now;
                    m_PvpPeekAnalog = 0x11;
                    return true;
                }
                var analog = PvpAnalogToward(to);
                if (analog == 0x11 || !IsPvpStepClear(analog))
                {
                    m_PvpLookCoverUntil = 0f;
                    return false;
                }
                m_PvpPeekAnalog = analog;
                return true;
            }
            if (TryStartPvpShotStep(target, now))
            {
                return true;
            }
            if (now < m_PvpLookCoverNextAt
                || now < m_PvpReactAt
                || !m_PvpEnemyArmed
                || m_PvpVisible
                || m_TreatmentPhase != 0)
            {
                return false;
            }
            m_PvpLookCoverNextAt = now + PvpLookCoverEverySeconds;
            var point = m_PvpLastSeenPoint;
            var meters = (point - position).GetHorizontalMagnitude();
            if (meters < 4f || meters > PvpHoldAngleMeters || !IsChestInLineFrom(point, position))
            {
                return false;
            }
            var name = target.channel?.owner?.playerID.characterName ?? "?";
            if (TryFindPvpWatchCover(position, point, out var spot, out var peek, PvpFightCoverMeters, true))
            {
                m_PvpLookCoverSpot = spot;
                m_PvpLookCoverUntil = now + 3f;
                BotLog(LogLevel.Information,
                    "PvP: na linha de {Name} a {Meters:0}m sem tiro para ele; indo de lado para cobertura a {Way:0.0}m{Peek}.",
                    name,
                    meters,
                    (spot - position).GetHorizontalMagnitude(),
                    peek ? ", com espiada" : ", so escondido");
                var first = PvpAnalogToward(spot - position);
                m_PvpPeekAnalog = IsPvpStepClear(first) ? first : (byte)0x11;
                return true;
            }
            if (now < m_PvpRepositionNextAt)
            {
                return false;
            }
            m_PvpRepositionNextAt = now + PvpRepositionEverySeconds;
            string how;
            if (TryFindPvpWatchCover(position, point, out spot, out peek, PvpCoverFarMeters))
            {
                how = peek ? "cobertura com espiada" : "cobertura";
            }
            else if (meters <= PvpHuntMaxMeters && TryGetPvpFlankSpot(position, point, meters, out spot))
            {
                how = "a volta pelo lado";
            }
            else
            {
                return false;
            }
            var way = (spot - position).GetHorizontalMagnitude();
            m_FleeingFromPlayerName = PvpRepositionName;
            SetObjective(ObjectiveType.Flee, spot, now + Mathf.Clamp(way / 5f + 4f, 5f, 20f));
            m_PvpRepositionActive = true;
            m_PvpRepositionSpot = spot;
            m_PvpLookCoverUntil = 0f;
            BotLog(LogLevel.Information,
                "PvP: na linha de {Name} a {Meters:0}m sem tiro para ele e sem cobertura perto; correndo {Way:0}m para {How} em {Spot}.",
                name,
                meters,
                way,
                how,
                FormatPosition(spot));
            return true;
        }

        // 0.1.0-y17.3 (the tester, 18/11: "no fim do teste ele me ve ... e nao
        // atirou"). ToxicSniper, 12:08:28-12:09:08: the tester 39m off on the
        // hill, on its camera only; its eye line ran into the military
        // tower 23m out (the recording's aim column: Tower_Military_2
        // 22.9m) and it stood 38s facing him.
        //
        // the tester on it: "isso faz sentido so se eu nao estiver vendo ele
        // tambem em perspective 3 pessoa, no caso ali eu podia strefar e
        // matar ele a qualquer momento, por isso o mais sensato seria ele
        // abrir fogo o mais rapido possivel ... e situacional". Where he
        // faces is what the bot sees of him:
        // - Armed and facing the bot (it is in his 120 degrees): he may
        //   have it on his screen too. At once a step to the nearest spot
        //   within 10m with its eye on him, the sights already up (the
        //   watch, SkaliaBotPvpCoverY), and the trigger as soon as the eye
        //   has him. With no such spot it stays where its chest is out of
        //   his line, sights on him: he has to step out to shoot it.
        // - Facing away: he does not see it. As long as the players took
        //   from a man in view to their first shot (p90 6.2s, max 7s,
        //   SkaliaBotPvpBotsY), then the same step; none, round him
        //   (TryGetPvpFlankSpot).
        private const float PvpShotStepMeters = 10f;
        private const float PvpShotStepUrgentEverySeconds = 0.5f;
        private float m_PvpShotStepNextAt;

        // The bot inside his 120 degrees (his look, as the game shows it).
        private bool IsPvpEnemyFacingMe(Player target)
        {
            var aim = target.look?.aim;
            if (aim == null)
            {
                return false;
            }
            var forward = aim.forward;
            forward.y = 0f;
            var to = m_Player.look.aim.position - aim.position;
            to.y = 0f;
            return forward.sqrMagnitude > 0.0001f
                && to.sqrMagnitude > 0.0001f
                && Vector3.Angle(forward, to) <= PvpViewHalfDegrees;
        }

        private bool TryStartPvpShotStep(Player target, float now)
        {
            if (!m_PvpCameraOnly
                || m_PvpVisible
                || m_TreatmentPhase != 0
                || now < m_PvpReactAt
                || now < m_PvpShotStepNextAt)
            {
                return false;
            }
            var urgent = m_PvpEnemyArmed && IsPvpEnemyFacingMe(target);
            if (!urgent && now - m_PvpCameraOnlySince < m_PvpCameraOnlyWait)
            {
                return false;
            }
            m_PvpShotStepNextAt = now + (urgent ? PvpShotStepUrgentEverySeconds : PvpLookCoverEverySeconds);
            var position = m_Player.transform.position;
            var point = m_PvpLastSeenPoint;
            var meters = (point - position).GetHorizontalMagnitude();
            var gunRange = GetPvpGunRange();
            if (gunRange <= 0f || meters > gunRange)
            {
                return false;
            }
            var eyeHeight = Mathf.Max(0.5f, m_Player.look.aim.position.y - position.y);
            var name = target.channel?.owner?.playerID.characterName ?? "?";
            for (var radius = 1.5f; radius <= PvpShotStepMeters; radius += 1.5f)
            {
                var found = false;
                var spot = position;
                var bestAngle = float.MaxValue;
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    if (!TryGetPvpHuntSpot(position + direction * radius, position, out var candidate)
                        || Mathf.Abs(candidate.y - position.y) > 1.5f
                        || Physics.Linecast(position + Vector3.up * 0.6f, candidate + Vector3.up * 0.6f,
                            RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                        || Physics.Linecast(candidate + Vector3.up * eyeHeight, point, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                        // A bush stops the eye as it does from here (HasPvpSight).
                        || IsBushOnLine(candidate + Vector3.up * eyeHeight, point))
                    {
                        continue;
                    }
                    // Sideways first: the least toward or away from him.
                    var toHim = point - position;
                    toHim.y = 0f;
                    var angle = Mathf.Abs(90f - Vector3.Angle(toHim, direction));
                    if (angle < bestAngle)
                    {
                        bestAngle = angle;
                        spot = candidate;
                        found = true;
                    }
                }
                if (!found)
                {
                    continue;
                }
                m_PvpLookCoverSpot = spot;
                m_PvpLookCoverUntil = now + Mathf.Clamp(radius / 2f + 1f, 2f, 6f);
                if (urgent)
                {
                    // The wait was for a man who had not seen it.
                    m_PvpFireNotBefore = 0f;
                }
                BotLog(LogLevel.Information,
                    "PvP: vendo {Name} a {Meters:0}m so pela camera ha {Seconds:0.0}s{Facing}; indo {Way:0.0}m para o lado onde o olho alcanca ele.",
                    name,
                    meters,
                    now - m_PvpCameraOnlySince,
                    urgent ? ", ele virado para mim" : ", ele virado para outro lado",
                    radius);
                var first = PvpAnalogToward(spot - position);
                m_PvpPeekAnalog = IsPvpStepClear(first) ? first : (byte)0x11;
                return true;
            }
            if (urgent)
            {
                // Out of his line already: he has to come out to shoot.
                // Not across the open in front of him.
                m_PvpFireNotBefore = 0f;
                return false;
            }
            if (now < m_PvpRepositionNextAt
                || meters > PvpHuntMaxMeters
                || !TryGetPvpFlankSpot(position, point, meters, out var around))
            {
                return false;
            }
            m_PvpRepositionNextAt = now + PvpRepositionEverySeconds;
            var way = (around - position).GetHorizontalMagnitude();
            m_FleeingFromPlayerName = PvpRepositionName;
            SetObjective(ObjectiveType.Flee, around, now + Mathf.Clamp(way / 5f + 4f, 5f, 20f));
            m_PvpRepositionActive = true;
            m_PvpRepositionSpot = around;
            m_PvpLookCoverUntil = 0f;
            BotLog(LogLevel.Information,
                "PvP: vendo {Name} a {Meters:0}m so pela camera e sem lugar com tiro a 10m; dando a volta, {Way:0}m ate {Spot}.",
                name,
                meters,
                way,
                FormatPosition(around));
            return true;
        }

        // Around the point: about 0.8 of the distance from it, 60 degrees
        // off the line to the bot (40 and 80 if that one is no good), on
        // either side, as TryStartPvpHuntMove.
        private bool TryGetPvpFlankSpot(Vector3 position, Vector3 point, float meters, out Vector3 spot)
        {
            spot = position;
            var away = position - point;
            away.y = 0f;
            if (away.sqrMagnitude < 1f)
            {
                return false;
            }
            var radius = Mathf.Clamp(meters * 0.8f, 10f, 45f);
            var side = Random.value < 0.5f ? 1f : -1f;
            foreach (var angle in new[] { 60f, 40f, 80f })
            {
                foreach (var sign in new[] { side, -side })
                {
                    var direction = Quaternion.Euler(0f, angle * sign, 0f) * away.normalized;
                    if (TryGetPvpHuntSpot(point + direction * radius, position, out spot))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void ResetPvpLookCover()
        {
            if (IsPvpRepositioning())
            {
                ClearObjective();
            }
            m_PvpRepositionActive = false;
            m_PvpLookCoverUntil = 0f;
        }
    }
}
