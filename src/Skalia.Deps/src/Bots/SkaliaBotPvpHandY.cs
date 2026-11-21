using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.19 (the tester, 19/11: "o principal problema e ele insta
    // locando o player. o player tem um tempo de reacao quando ve o jogador
    // inimigo e trava a mira ate ele. nao e instantaneo" and "travar a mira
    // nao e clique com o direito e o movimento do mouse ate o alvo").
    //
    // The fight's aim turned at a fixed 420 degrees a second, from the
    // first frame, straight onto him. The bots' own recordings of 17-19/11
    // (rec2/botaim2.py, 160 first shots at a recorded man) against the
    // players' of 08/11 (rec/aim2.py, rec/aim4.py), the turn onto the man
    // before the first shot:
    // - 25-45 degrees: players stopped at 0.34s, 5.3 degrees off (median),
    //   peak 116 degrees a second; bots stopped at 0.20s, 1.6 off (a
    //   quarter of them within 0.13), peak 252.
    // - 45-100 degrees: players peak 209, 6.3 off; bots peak 336, 1.8 off.
    // And no time between seeing him and the mouse moving: in the 60 "vi
    // ... virado para mim e armado: reagindo na hora" of the test of
    // 19/11 the turn started the same frame, and the reaction drawn for
    // the others held only the trigger: the head went to him through the
    // look at where he was seen.
    //
    // Now the hand: a reaction before it moves, one quick move onto him
    // that speeds up and slows down (the minimum-jerk curve) and lands
    // short of him or past him, then small moves to correct. Everything
    // is kept relative to the point aimed at, so a man walking is followed
    // during the move.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // The move onto him (24 turns of the players from 25-92 degrees to
        // a first stop): its length as a minimum-jerk move
        // (1.875 x amplitude / peak speed), 0.21, 0.26, 0.27, 0.46, 0.66,
        // 1.39, 1.72s (min, p10, p25, median, p75, p90, max).
        private static readonly float[] s_PvpHandQuantiles = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_PvpFlickSeconds = { 0.21f, 0.26f, 0.27f, 0.46f, 0.66f, 1.39f, 1.72f };

        // Where the move stopped, as a share of it: short of him (+) or
        // past him (-), -0.29, -0.12, -0.06, 0.07, 0.26, 0.40, 0.73 (11 of
        // the 24 went past).
        private static readonly float[] s_PvpFlickLandShare = { -0.29f, -0.12f, -0.06f, 0.07f, 0.26f, 0.40f, 0.73f };

        // From that stop to within 5 degrees, the 12 that needed it: 0.08,
        // 0.16, 0.26, 0.34, 0.40, 0.44, 0.52, 0.64, 0.78, 1.04s. The same
        // for the small moves while on him (the drawn error moving).
        private static readonly float[] s_PvpFixQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpFixSeconds = { 0.08f, 0.26f, 0.42f, 0.64f, 1.04f };

        // The reaction before the mouse moves (rec/aim3.py, first hits of
        // the players of 08/11 with the head still): the turn started
        // 0.18, 0.19, 0.57, 0.85 and 1.15s after the round. Two more, 1.85s
        // at 218m and 3.06s from 101 degrees, are the ones that did not
        // find him (s_PvpOffScreenReactSeconds).
        private static readonly float[] s_PvpOnsetQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpOnsetSeconds = { 0.18f, 0.19f, 0.57f, 0.85f, 1.15f };

        // 0.1.0-y17.20 (the tester, 19/11, the follow-up to the above): on him,
        // the y17.19 aim still followed a man on the move with no delay.
        // Players of 08/11 (rec/track1.py, 1246 rows of their fights with
        // the gun within 15 degrees of him), the aim along his move (+ =
        // behind him), by how fast he crossed their view:
        // - 2-5 degrees a second: +0.8 (median), behind in 60% of the rows
        // - 5-10: +3.9, behind in 73%
        // - 10-20: +6.3, behind in 81%
        // - 20-40: +2.4 (11 rows: past that a new move onto him)
        // The bots of 17-19/11 (rec2/bottrack.py, 6246 rows): +0.4 at
        // 5-10, +1.0 at 10-20. Per pair of players, the delay (aim behind
        // over his speed): -0.54, -0.41, 0.19, 0.33, 0.34, 0.54, 0.57, 0.72,
        // 0.98, 1.07s; the two below 0 led him, which the bot's own lead
        // for the round's flight (GetPvpSteerPoint) already does, so they
        // count as 0. The aim follows him through that delay, drawn for
        // each move onto him, and never more than PvpFlickDegrees behind:
        // past that the players moved onto him again.
        private static readonly float[] s_PvpTrackLagQuantiles = { 0f, 0.2f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_PvpTrackLagSeconds = { 0f, 0f, 0.44f, 0.68f, 1.07f };
        private float m_PvpHandLag;
        private float m_PvpHandRawYaw;
        private float m_PvpHandRawPitch;
        private float m_PvpHandSeenYaw;
        private float m_PvpHandSeenPitch;

        // Past this off him it is a move onto him, within it a correction:
        // the players' error at the first shot, p90 7.45 degrees
        // (rec/aim1.py, 37 fights).
        private const float PvpFlickDegrees = 7.45f;

        // Not driven for this long: the head was on something else (the
        // walk, a zombie), the next move starts from where it is.
        private const float PvpHandIdleSeconds = 0.25f;

        private float m_PvpHandGoAt = float.NegativeInfinity;
        private float m_PvpHandNextFlick;
        private float m_PvpHandDrivenAt = float.NegativeInfinity;
        private bool m_PvpHandMoving;
        private bool m_PvpHandFlick;
        private float m_PvpHandStart;
        private float m_PvpHandSeconds;
        private float m_PvpHandFromYaw;
        private float m_PvpHandFromPitch;
        private float m_PvpHandToYaw;
        private float m_PvpHandToPitch;
        private float m_PvpHandWantYaw;
        private float m_PvpHandWantPitch;
        private float m_PvpHandLastOffYaw;
        private float m_PvpHandLastOffPitch;

        // A man just noticed: the hand waits the players' reaction.
        private void StartPvpHandReaction(float now)
        {
            m_PvpHandGoAt = now + DrawFromQuantiles(s_PvpOnsetQuantiles, s_PvpOnsetSeconds) * SkaliaBotModifiers.ReactionTime;
            m_PvpHandNextFlick = 0f;
            m_PvpHandMoving = false;
        }

        // A drawn time to face him (the hit reactions of SkaliaBotPvpFeelY):
        // the hand sets off so that the move ends then, never sooner than
        // the quickest reaction.
        private float StartPvpHandReactionToFace(float faceSeconds, float now)
        {
            // 0.1.0-y17.34: the move over serverBot:modifiers:aimSpeed, the
            // quickest reaction times reactionTime.
            m_PvpHandNextFlick = DrawFromQuantiles(s_PvpHandQuantiles, s_PvpFlickSeconds) / SkaliaBotModifiers.AimSpeed;
            m_PvpHandGoAt = now + Mathf.Max(s_PvpOnsetSeconds[0] * SkaliaBotModifiers.ReactionTime, faceSeconds - m_PvpHandNextFlick);
            m_PvpHandMoving = false;
            return m_PvpHandGoAt + m_PvpHandNextFlick;
        }

        // yaw/pitch: the aim now, moved here. pointYaw/pointPitch: the
        // direction of what is aimed at; wantYaw/wantPitch: how far off it
        // the aim should rest (the drawn error).
        private void StepPvpHand(ref float yaw, ref float pitch, float pointYaw, float pointPitch,
            float wantYaw, float wantPitch, float now)
        {
            var dt = now - m_PvpHandDrivenAt;
            var fresh = dt > PvpHandIdleSeconds;
            // 0.1.0-y17.20: where the hand thinks he is, his move seen late.
            if (fresh
                || m_PvpHandLag <= 0f
                || Mathf.Abs(Mathf.DeltaAngle(m_PvpHandRawYaw, pointYaw)) > PvpFlickDegrees
                || Mathf.Abs(m_PvpHandRawPitch - pointPitch) > PvpFlickDegrees)
            {
                m_PvpHandSeenYaw = pointYaw;
                m_PvpHandSeenPitch = pointPitch;
            }
            else
            {
                var k = 1f - Mathf.Exp(-Mathf.Max(0f, dt) / m_PvpHandLag);
                m_PvpHandSeenYaw += Mathf.DeltaAngle(m_PvpHandSeenYaw, pointYaw) * k;
                m_PvpHandSeenPitch += (pointPitch - m_PvpHandSeenPitch) * k;
                m_PvpHandSeenYaw = pointYaw + Mathf.Clamp(Mathf.DeltaAngle(pointYaw, m_PvpHandSeenYaw), -PvpFlickDegrees, PvpFlickDegrees);
                m_PvpHandSeenPitch = pointPitch + Mathf.Clamp(m_PvpHandSeenPitch - pointPitch, -PvpFlickDegrees, PvpFlickDegrees);
            }
            m_PvpHandRawYaw = pointYaw;
            m_PvpHandRawPitch = pointPitch;
            pointYaw = m_PvpHandSeenYaw;
            pointPitch = m_PvpHandSeenPitch;
            if (fresh)
            {
                m_PvpHandMoving = false;
                m_PvpHandLastOffYaw = Mathf.DeltaAngle(pointYaw, yaw);
                m_PvpHandLastOffPitch = pitch - pointPitch;
            }
            m_PvpHandDrivenAt = now;
            if (now < m_PvpHandGoAt)
            {
                // Not reacted yet: the head stays where it was.
                m_PvpHandLastOffYaw = Mathf.DeltaAngle(pointYaw, yaw);
                m_PvpHandLastOffPitch = pitch - pointPitch;
                return;
            }
            var offYaw = Mathf.DeltaAngle(pointYaw, yaw);
            var offPitch = pitch - pointPitch;
            // The point jumped (another man, or seen somewhere else): the
            // move under way was for the old one.
            var jump = Mathf.Abs(Mathf.DeltaAngle(m_PvpHandLastOffYaw, offYaw)) > PvpFlickDegrees
                || Mathf.Abs(m_PvpHandLastOffPitch - offPitch) > PvpFlickDegrees;
            if (m_PvpHandMoving && (jump
                || (!m_PvpHandFlick && (Mathf.Abs(wantYaw - m_PvpHandWantYaw) > 0.01f || Mathf.Abs(wantPitch - m_PvpHandWantPitch) > 0.01f))))
            {
                m_PvpHandMoving = false;
            }
            if (!m_PvpHandMoving)
            {
                var gapYaw = Mathf.DeltaAngle(offYaw, wantYaw);
                var gapPitch = wantPitch - offPitch;
                var gap = Mathf.Sqrt(gapYaw * gapYaw + gapPitch * gapPitch);
                if (gap < 0.05f)
                {
                    SetPvpHandAim(ref yaw, ref pitch, pointYaw, pointPitch, wantYaw, wantPitch);
                    return;
                }
                m_PvpHandMoving = true;
                m_PvpHandStart = now;
                m_PvpHandFromYaw = offYaw;
                m_PvpHandFromPitch = offPitch;
                m_PvpHandWantYaw = wantYaw;
                m_PvpHandWantPitch = wantPitch;
                m_PvpHandFlick = gap > PvpFlickDegrees;
                if (m_PvpHandFlick)
                {
                    m_PvpHandSeconds = m_PvpHandNextFlick > 0f
                        ? m_PvpHandNextFlick
                        : DrawFromQuantiles(s_PvpHandQuantiles, s_PvpFlickSeconds) / SkaliaBotModifiers.AimSpeed;
                    m_PvpHandNextFlick = 0f;
                    m_PvpHandLag = DrawFromQuantiles(s_PvpTrackLagQuantiles, s_PvpTrackLagSeconds) / SkaliaBotModifiers.AimSpeed;
                    var land = DrawFromQuantiles(s_PvpHandQuantiles, s_PvpFlickLandShare);
                    m_PvpHandToYaw = offYaw + gapYaw * (1f - land);
                    m_PvpHandToPitch = offPitch + gapPitch * (1f - land);
                }
                else
                {
                    m_PvpHandSeconds = DrawFromQuantiles(s_PvpFixQuantiles, s_PvpFixSeconds) / SkaliaBotModifiers.AimSpeed;
                    m_PvpHandToYaw = offYaw + gapYaw;
                    m_PvpHandToPitch = wantPitch;
                }
            }
            var t = Mathf.Clamp01((now - m_PvpHandStart) / Mathf.Max(0.02f, m_PvpHandSeconds));
            var s = t * t * t * (10f + t * (-15f + 6f * t));
            var atYaw = m_PvpHandFromYaw + (m_PvpHandToYaw - m_PvpHandFromYaw) * s;
            var atPitch = m_PvpHandFromPitch + (m_PvpHandToPitch - m_PvpHandFromPitch) * s;
            if (t >= 1f)
            {
                // Stopped; what is left is the next move's.
                m_PvpHandMoving = false;
            }
            SetPvpHandAim(ref yaw, ref pitch, pointYaw, pointPitch, atYaw, atPitch);
        }

        private void SetPvpHandAim(ref float yaw, ref float pitch, float pointYaw, float pointPitch, float offYaw, float offPitch)
        {
            yaw = Mathf.Repeat(pointYaw + offYaw, 360f);
            pitch = Mathf.Clamp(pointPitch + offPitch, 0.5f, 179.5f);
            m_PvpHandLastOffYaw = Mathf.DeltaAngle(pointYaw, yaw);
            m_PvpHandLastOffPitch = pitch - pointPitch;
        }
    }
}
