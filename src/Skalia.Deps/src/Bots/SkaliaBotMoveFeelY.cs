using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.27 (the tester, 20/11: "reforce a movimentacao dele e o
    // cover e tambem a reacao de combate quando escutar tiro"). The bots of
    // the tests of 16-19/11 against the players (the public server of
    // 04-11/11 and the tests, rec3/loco2.py, burst2.py, look60.py,
    // fightmove.py), outdoors, nothing after them:
    // - moving, the players sprinted 59-71% of the time (each of the 5 with
    //   more than 3 minutes), the bots 36%. The searching of an area
    //   ("Patrol") was walked end to end: 202 minutes moving, 1% of it
    //   sprinting; the trips 48%.
    // - between two sprints the players walked 0.1/0.2/0.3/0.8/1.8s (p10 to
    //   p90; p95 2.8, p99 5.9), the bots 1.2 to 2.8s by rule; the sprints
    //   themselves 0.2/0.5/1.7/4.7/11.0s (p95 16.3, p99 34.1), the bots'
    //   drawn up to 10s. 0.1.0-y17.29: timed by the rows' own clock
    //   (tools/recordings/movement.py); y17.27 took 0.1s a row and the rows
    //   come every 0.08-0.1s, 10-15% long. Day and night alike (the night's medians the day's);
    //   at night the players stood still more (16% against 7%), which the
    //   stops of SkaliaBotPlayerHabits keep.
    // - they looked more than 60 degrees off the way they went 4.8 times a
    //   minute sprinting and 15.6 times walking; the bots 0.1 and 7.4. The
    //   bots' look sprinting was held within 40 degrees, as if the sprint
    //   needed the forward key; it does not (PlayerStance.simulate: the key,
    //   breath and any movement; PlayerMovement: the same speed any way).
    // - in a fight (5s before to 2s after a round at a man or a hit), 43% of
    //   the players' sprints of 2s or more had hops (84 hops in 86 runs, 15
    //   a minute running), the bots' 19% (4 a minute): only the run from a
    //   hit had them (SkaliaBotPvpFeelY).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private static readonly float[] s_MoveRunQuantiles = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 0.95f, 0.99f, 1f };
        private static readonly float[] s_PlayersSprintRunSeconds = { 0.2f, 0.2f, 0.5f, 1.7f, 4.7f, 11.0f, 16.3f, 34.1f, 34.1f };
        private static readonly float[] s_PlayersWalkBetweenSeconds = { 0.1f, 0.1f, 0.2f, 0.3f, 0.8f, 1.8f, 2.8f, 5.9f, 5.9f };

        // The looks more than 60 degrees off the way: how far (the furthest
        // point of each) and how long past 60, p10 to p90.
        private static readonly float[] s_LookQuantiles = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_SprintLookDegrees = { 60f, 64.6f, 80.4f, 90.8f, 122.9f, 176.2f, 180f };
        private static readonly float[] s_SprintLookSeconds = { 0.1f, 0.1f, 0.2f, 0.6f, 1.2f, 2.1f, 2.1f };
        private static readonly float[] s_WalkLookDegrees = { 60f, 67.7f, 89.6f, 90.4f, 97.8f, 161.8f, 180f };
        private static readonly float[] s_WalkLookSeconds = { 0.1f, 0.1f, 0.2f, 0.4f, 0.8f, 1.5f, 1.5f };
        private const float SprintLooksPerMinute = 4.8f;
        private const float WalkLooksPerMinute = 15.6f;

        // The hops of a fight's sprint: the first one after the start of the
        // run and the time between them, p10 to p90.
        private const float PvpRunHopShare = 0.43f;
        private static readonly float[] s_PvpRunHopQuantiles = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_PvpRunFirstHopSeconds = { 0f, 0f, 0.3f, 1.2f, 2.5f, 3.3f, 3.3f };
        private static readonly float[] s_PvpRunHopGapSeconds = { 0.5f, 0.5f, 0.6f, 0.9f, 1.1f, 1.5f, 1.5f };
        // A sprint let go of for less than this goes on as the same run (the
        // recordings' blips of up to 0.3s were merged the same way).
        private const float PvpRunGapSeconds = 0.4f;
        private const float PvpRunMinimumSpeed = 2f;

        private float m_OpenSkyCheckedAt = float.NegativeInfinity;
        private bool m_OpenSky;
        private float m_PvpRunLastSprintAt = float.NegativeInfinity;
        private bool m_PvpRunHopping;
        private float m_PvpRunNextHopAt = float.PositiveInfinity;
        private Vector3 m_PvpRunLastPosition;
        private float m_PvpRunLastPositionAt = float.NegativeInfinity;
        private float m_PvpRunHopLogAt = float.NegativeInfinity;

        private static float DrawPlayersSprintRun()
        {
            return DrawFromQuantiles(s_MoveRunQuantiles, s_PlayersSprintRunSeconds);
        }

        private static float DrawPlayersWalkBetweenSprints()
        {
            return DrawFromQuantiles(s_MoveRunQuantiles, s_PlayersWalkBetweenSeconds);
        }

        private static float NextMoveLookGap(bool sprinting)
        {
            var mean = 60f / (sprinting ? SprintLooksPerMinute : WalkLooksPerMinute);
            return Random.Range(mean * 0.5f, mean * 1.5f);
        }

        private static float DrawMoveLookDegrees(bool sprinting)
        {
            return DrawFromQuantiles(s_LookQuantiles, sprinting ? s_SprintLookDegrees : s_WalkLookDegrees);
        }

        // The time past 60 degrees, plus the bot's own turn there and back
        // (its ramp, ApplyTurnRamp: from rest to a stop over a angle takes
        // 2 * sqrt(a / accel)).
        private float DrawMoveLookSeconds(bool sprinting, float degrees)
        {
            var held = DrawFromQuantiles(s_LookQuantiles, sprinting ? s_SprintLookSeconds : s_WalkLookSeconds);
            var turn = 2f * Mathf.Sqrt(Mathf.Abs(degrees) / Mathf.Max(1f, m_TurnAccel));
            return held + turn;
        }

        // Nothing over the bot (HasRoofOverBot), checked twice a second.
        private bool IsOpenSkyCached(Vector3 position, float now)
        {
            if (now - m_OpenSkyCheckedAt >= 0.5f)
            {
                m_OpenSkyCheckedAt = now;
                m_OpenSky = !HasRoofOverBot(position);
            }
            return m_OpenSky;
        }

        // From the packet: a fight's sprint gets the players' hops. The run
        // from a hit has its own (SkaliaBotPvpFeelY); the fight's own hops
        // over things go on as before (SkaliaBotPvpHopY).
        private void TickPvpRunHops(bool sprintKey)
        {
            var now = Time.realtimeSinceStartup;
            var position = m_Player.transform.position;
            var moved = position - m_PvpRunLastPosition;
            moved.y = 0f;
            var elapsed = now - m_PvpRunLastPositionAt;
            m_PvpRunLastPosition = position;
            m_PvpRunLastPositionAt = now;
            var running = sprintKey
                && m_PvpTarget != null
                && !IsPvpEscaping()
                && m_Player.stance.stance == EPlayerStance.SPRINT
                && m_Player.movement.getVehicle() == null
                && !IsLadderInProgress()
                && elapsed > 0f
                && elapsed < 0.5f
                && moved.magnitude / elapsed >= PvpRunMinimumSpeed;
            if (!running)
            {
                if (now - m_PvpRunLastSprintAt > PvpRunGapSeconds)
                {
                    m_PvpRunHopping = false;
                    m_PvpRunNextHopAt = float.PositiveInfinity;
                }
                return;
            }
            if (now - m_PvpRunLastSprintAt > PvpRunGapSeconds)
            {
                // A new run.
                m_PvpRunHopping = Random.value < PvpRunHopShare;
                m_PvpRunNextHopAt = now + DrawFromQuantiles(s_PvpRunHopQuantiles, s_PvpRunFirstHopSeconds);
            }
            m_PvpRunLastSprintAt = now;
            if (!m_PvpRunHopping || now < m_PvpRunNextHopAt || now < m_NavigationJumpUntil)
            {
                return;
            }
            m_PvpRunNextHopAt = now + DrawFromQuantiles(s_PvpRunHopQuantiles, s_PvpRunHopGapSeconds);
            // Grounded, under the open sky, nothing at the knee or the chest
            // and no step or hole in the 4m it lands in (the calm hop's test).
            if (!m_Player.movement.isGrounded || !IsFlatOpenAhead(position, moved.normalized))
            {
                return;
            }
            m_NavigationJumpUntil = now + 0.3f;
            if (now >= m_PvpRunHopLogAt)
            {
                m_PvpRunHopLogAt = now + 10f;
                BotLog(LogLevel.Debug, "PvP: pulando na corrida, como os jogadores em luta.");
            }
        }
    }
}
