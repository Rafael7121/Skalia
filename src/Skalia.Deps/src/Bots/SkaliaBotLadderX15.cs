using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x15 (the tester: "llo esta travando na escada da ponte"). The bridge
    // ladder at (-289, 38, 100) has failed since 11-10.
    //
    // 1. The game takes the ladder where the waist ray first touches it
    //    (PlayerStance.simulate), so the body's place across the ladder at
    //    the grab is where it climbs. In the recordings every climb grabbed
    //    0.42-0.58m off the centre line stopped at the gap at 49m (4 of 4;
    //    Tadeu let go and took it again for 20s there), and every grab within
    //    0.23m of it reached the top at 56m (3 of 3: two players, one bot).
    //    The players walk in facing the ladder (yaw 135) on its line (grabs
    //    0.01-0.10m off). The bot bent its walk toward the line, about 30
    //    degrees off (yaw 103-108), and caught the edge first. Now it faces
    //    the ladder and steps sideways onto the line before walking in, far
    //    enough back that the waist ray (0.75m) cannot touch it on the way
    //    (the tester lined up 0.86m behind the foot, 20:36 recording).
    // 2. llo stood behind the ladder, on the bridge side, 1.2-1.6m from the
    //    foot and facing away, three times; behind the ladder the walk-in
    //    holds still, so it waited 20s each time. Now it walks round to the
    //    front.
    //
    // x16, from the x15 test (bridge ladder: 38 of 40 climbs reached the top;
    // in the x14 test it was 0 of 3):
    // 3. Tadeu came to the foot from the side (yaw -28) 0.61m off the line
    //    and turned to face the ladder where he stood; the turn swept the
    //    waist ray over the ladder's edge (grab at yaw 97, 0.6m off) and he
    //    stopped at the 49m gap. Off the line and close to it, the bot now
    //    backs away without turning, and turns to the ladder only 0.86m back.
    // 4. Tadeu walked in crouched (sneaking past the Mega 14m away) and the
    //    game does not start a climb from a crouch (PlayerStance:
    //    canCurrentStanceTransitionToClimbing is STAND, SPRINT or SWIM).
    //    At the foot the bot stands up (LadderNeedsStanding).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LadderLineStartMeters = 0.23f;
        private const float LadderLineDoneMeters = 0.10f;
        private const float LadderLineBackMeters = 0.86f;
        private const float LadderLineMaxSeconds = 4f;
        private const float LadderFrontMeters = 0.5f;
        private const int LadderFrontTries = 2;

        private byte m_LadderStrafeAnalog;
        private bool m_LadderLining;
        private float m_LadderLiningSince;
        private bool m_LadderLineGaveUp;
        private int m_LadderFrontWalks;
        private bool m_LadderGoingFront;

        private void ResetLadderX15()
        {
            m_LadderStrafeAnalog = 0;
            m_LadderLining = false;
            m_LadderLiningSince = 0f;
            m_LadderLineGaveUp = false;
            m_LadderFrontWalks = 0;
            m_LadderGoingFront = false;
        }

        // Off the line and too close to turn toward the ladder: keep the yaw
        // until 0.86m back (x16).
        private bool LadderMustBackOffFirst(BotLadder ladder, Vector3 position)
        {
            if (m_LadderLineGaveUp || m_Player.stance.stance == EPlayerStance.CLIMB)
            {
                return false;
            }
            var limit = m_LadderLining ? LadderLineDoneMeters : LadderLineStartMeters;
            if (Mathf.Abs(LadderSideways(ladder, position)) <= limit)
            {
                return false;
            }
            var footLine = LadderFromLine(ladder, ladder.Bottom);
            return LadderFromLine(ladder, position) < footLine + LadderLineBackMeters
                && Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, ladder.FacingYaw)) >= 15f;
        }

        // The game only starts a climb from standing, sprinting or swimming.
        private bool LadderNeedsStanding()
        {
            return m_ObjectiveType == ObjectiveType.Ladder
                && m_Ladder != null
                && !m_LadderDown
                && m_LadderPhase >= 2;
        }

        private static float LadderSideways(BotLadder ladder, Vector3 position)
        {
            var forward = -FlatFace(ladder);
            var across = Vector3.Cross(Vector3.up, forward).normalized;
            var offset = position - ladder.Bottom;
            offset.y = 0f;
            return Vector3.Dot(offset, across);
        }

        private static float LadderFromLine(BotLadder ladder, Vector3 position)
        {
            var offset = position - ladder.Center;
            offset.y = 0f;
            return Vector3.Dot(offset, FlatFace(ladder));
        }

        // Behind the ladder's plane (the side it is fixed to): back to phase
        // 1 with the route to a point in front of the foot. True when it did.
        private bool TryWalkRoundToLadderFront(BotLadder ladder, Vector3 position, float now)
        {
            if (m_Player.stance.stance == EPlayerStance.CLIMB
                || LadderFromLine(ladder, position) >= 0.1f)
            {
                m_LadderGoingFront = false;
                return false;
            }
            if (m_LadderGoingFront)
            {
                // Already on the way round.
                return true;
            }
            if (m_LadderFrontWalks >= LadderFrontTries)
            {
                FailLadder(position, now, "atras da escada, do lado em que ela esta presa, depois de " + LadderFrontTries + " voltas para a frente");
                return true;
            }
            m_LadderFrontWalks++;
            m_LadderGoingFront = true;
            var front = ladder.Bottom + FlatFace(ladder) * LadderFrontMeters;
            BotLog(LogLevel.Information,
                "Escada {Name}: bot atras dela em {Position} ({Distance:0.0}m do pe); dando a volta para a frente em {Front} (volta {Try}).",
                ladder.Name,
                FormatPosition(position),
                (ladder.Bottom - position).GetHorizontalMagnitude(),
                FormatPosition(front),
                m_LadderFrontWalks);
            m_LadderPhase = 1;
            m_LadderStartedAt = now;
            m_LadderPhaseStartedAt = now;
            m_LadderLining = false;
            m_LadderStrafeAnalog = 0;
            m_ObjectivePosition = front;
            m_ObjectiveExpiresAt = now + 40f;
            ClearPath();
            return true;
        }

        // Before the walk-in: facing the ladder, sideways onto its line,
        // stepping back first when the waist ray could touch it. True while
        // this steers (the walk-in waits).
        private bool TrySteerOntoLadderLine(BotLadder ladder, Vector3 position, float now)
        {
            m_LadderStrafeAnalog = 0;
            if (m_LadderLineGaveUp || m_Player.stance.stance == EPlayerStance.CLIMB)
            {
                return false;
            }
            var sideways = LadderSideways(ladder, position);
            var limit = m_LadderLining ? LadderLineDoneMeters : LadderLineStartMeters;
            if (Mathf.Abs(sideways) <= limit)
            {
                if (m_LadderLining)
                {
                    m_LadderLining = false;
                    // The walk-in and its 1.2s and 8s clocks start now.
                    m_LadderPhaseStartedAt = now;
                    BotLog(LogLevel.Information,
                        "Escada {Name}: na linha do meio ({Off:0.00}m de lado) em {Seconds:0.0}s; entrando de frente, como os jogadores.",
                        ladder.Name,
                        sideways,
                        now - m_LadderLiningSince);
                }
                return false;
            }
            if (!m_LadderLining)
            {
                m_LadderLining = true;
                m_LadderLiningSince = now;
                BotLog(LogLevel.Information,
                    "Escada {Name}: bot a {Off:0.00}m da linha do meio (nas gravacoes, quem pega a 0,42m ou mais para no vao de 49m); indo de lado para a linha antes de entrar.",
                    ladder.Name,
                    sideways);
            }
            if (now - m_LadderLiningSince > LadderLineMaxSeconds)
            {
                m_LadderLining = false;
                m_LadderLineGaveUp = true;
                m_LadderPhaseStartedAt = now;
                BotLog(LogLevel.Information,
                    "Escada {Name}: nao chegou na linha do meio em {Seconds:0}s ({Off:0.00}m de lado); entrando assim mesmo.",
                    ladder.Name,
                    LadderLineMaxSeconds,
                    sideways);
                return false;
            }
            m_LadderPhaseStartedAt = now;
            var face = FlatFace(ladder);
            var forward = -face;
            var across = Vector3.Cross(Vector3.up, forward).normalized;
            var wanted = -across * Mathf.Sign(sideways);
            var footLine = LadderFromLine(ladder, ladder.Bottom);
            if (LadderFromLine(ladder, position) < footLine + LadderLineBackMeters)
            {
                wanted += face;
            }
            wanted.Normalize();
            var best = (byte)0x11;
            var bestDot = 0.3f;
            foreach (var candidate in s_CombatMovementCandidates)
            {
                // Never toward the ladder while off its line.
                if ((candidate & 0x0F) == 0x02)
                {
                    continue;
                }
                var dot = Vector3.Dot(GetWorldMovementDirection(candidate, m_CurrentYaw), wanted);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = candidate;
                }
            }
            var aligned = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, ladder.FacingYaw)) < 15f;
            var backingOff = !aligned && LadderMustBackOffFirst(ladder, position);
            if (backingOff)
            {
                // Straight away from the ladder, still not facing it.
                best = 0x11;
                bestDot = 0.3f;
                foreach (var candidate in s_CombatMovementCandidates)
                {
                    var dot = Vector3.Dot(GetWorldMovementDirection(candidate, m_CurrentYaw), face);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = candidate;
                    }
                }
            }
            var moving = aligned || backingOff;
            m_LadderStrafeAnalog = moving ? best : (byte)0x11;
            m_NavigationAnalog = m_LadderStrafeAnalog;
            m_IsWalking = moving && best != 0x11;
            m_IsSprinting = false;
            return true;
        }
    }
}
