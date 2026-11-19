using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x10 (doc 16.18): what the x9 test showed.
    //
    // Down over a parapet: 312 of the 314 failed descents were the bridge
    // ladder (-288, 99) and the barracks one (-826, -722). Both have a wall
    // between the floor and the ladder (the bridge rail, the barracks roof
    // edge 1m above the roof). The bot pressed S against it until the
    // timeout. the tester jumps onto that edge (barracks, 2026-11-09 468s).
    // Now: no progress backwards for 0.35s, jump backwards; in the air back
    // only until past the ladder's line, then W into it (air control is
    // 8x the walk speed per second: PlayerMovement), and the game takes it.
    //
    // Up through a gap: the bridge ladder is pieces stacked with a gap at
    // 49m. The game let the bot go there and the code took any end of the
    // climb as the top: W released 11m up, off the mesh, a grab to go down,
    // the route up again (the back and forth of the bridge). The two climbs
    // that kept W reached 55.9m. Now the climb ends only near the top; in a
    // gap the bot keeps W into the ladder and, if that does not take it,
    // looks at it and uses it.
    //
    // Down fast: the tester (2026-11-08 22:41, 301s) and Onça (00:59, 683s) do
    // not hold S. They turn the body off the ladder, fall a few meters and
    // turn back: the game takes the ladder in the air and the fall speed is
    // gone (10m in 1.5s). The bot holding S spent 13-23s on the metal
    // ladders (42-79 short falls in their gaps) and Tadeu died on one. Fall
    // damage starts at 22 m/s (PlayerLife.onLanded), a fall of 8m with the
    // game's gravity (3x): the drops here are at most 5m.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LadderBackOffStallSeconds = 0.35f;
        private const float LadderBackOffPastMeters = 0.3f;
        private const int LadderBackOffMaxJumps = 3;
        private const float LadderTopReachMeters = 1f;
        private const float LadderGapRegrabSeconds = 0.8f;
        private const float LadderDropMeters = 4f;
        private const float LadderDropWholeMeters = 6.5f;
        private const float LadderDropTurnDegrees = 100f;
        private const float LadderDropTopClearMeters = 2.5f;

        private bool m_LadderBackOffJumped;
        private float m_LadderBackOffJumpAt;
        private float m_LadderBackOffNextJumpAt;
        private int m_LadderBackOffJumps;
        private float m_LadderBackOffBest;
        private float m_LadderBackOffBestAt;

        private bool m_LadderInGap;
        private bool m_LadderGapJumped;
        private float m_LadderGapSince;
        private float m_LadderGapY;
        private int m_LadderGaps;

        private bool m_LadderDropping;
        private float m_LadderDropFromY;
        private float m_LadderDropStartedAt;
        private float m_LadderDropNextAt;
        private int m_LadderDropCount;

        private void ResetLadderX10()
        {
            m_LadderBackOffJumped = false;
            m_LadderBackOffJumpAt = 0f;
            m_LadderBackOffNextJumpAt = 0f;
            m_LadderBackOffJumps = 0;
            m_LadderBackOffBest = float.NegativeInfinity;
            m_LadderBackOffBestAt = 0f;
            m_LadderInGap = false;
            m_LadderGapSince = 0f;
            m_LadderGapY = 0f;
            m_LadderGaps = 0;
            m_LadderDropping = false;
            m_LadderDropFromY = 0f;
            m_LadderDropNextAt = 0f;
            m_LadderDropCount = 0;
            m_LadderGapJumped = false;
            m_LadderClimbBestY = float.NegativeInfinity;
            m_LadderClimbBestAt = 0f;
            m_LadderBlockedLogged = false;
        }

        private void SetLadderYawNow(float yaw, float pitch)
        {
            m_CurrentYaw = yaw;
            m_DesiredYaw = yaw;
            m_CurrentPitch = pitch;
            m_DesiredPitch = pitch;
        }

        // ---- down: back off the edge, jumping a parapet ----

        // Grounded, aligned and pressing S: true when the bot jumped now.
        private bool TryJumpLadderLip(BotLadder ladder, float fromPlane, float now)
        {
            if (fromPlane > m_LadderBackOffBest + 0.05f)
            {
                m_LadderBackOffBest = fromPlane;
                m_LadderBackOffBestAt = now;
                return false;
            }
            if (now - m_LadderBackOffBestAt < LadderBackOffStallSeconds
                || now < m_LadderBackOffNextJumpAt
                || m_LadderBackOffJumps >= LadderBackOffMaxJumps
                || m_Player.life.stamina < 12)
            {
                return false;
            }
            m_LadderBackOffJumps++;
            m_LadderBackOffJumped = true;
            m_LadderBackOffJumpAt = now;
            m_LadderBackOffNextJumpAt = now + 1.2f;
            m_LadderBackOffBestAt = now;
            m_NavigationJumpUntil = Time.realtimeSinceStartup + 0.25f;
            BotLog(LogLevel.Information,
                "Topo da escada {Name}: nao anda para tras (mureta de {Lip:0.0}m pelo catalogo); pulando de costas por cima dela (pulo {Jump} de {Max}), no ar vai para tras so ate passar a linha da escada.",
                ladder.Name,
                ladder.LipHeight,
                m_LadderBackOffJumps,
                LadderBackOffMaxJumps);
            return true;
        }

        // In the air off the top: back until past the ladder's line, then W
        // into it. True while this steers.
        private bool SteerLadderBackOffAir(BotLadder ladder, Vector3 position, float fromPlane, float now)
        {
            if (m_Player.movement.isGrounded)
            {
                if (m_LadderBackOffJumped && now - m_LadderBackOffJumpAt > 0.3f)
                {
                    // Landed back on the roof: the next jump after the pause.
                    m_LadderBackOffJumped = false;
                    m_LadderBackOffBest = float.NegativeInfinity;
                    m_LadderBackOffBestAt = now;
                }
                return false;
            }
            if (!m_LadderBackOffJumped && position.y >= m_LadderStartY - 0.3f)
            {
                return false;
            }
            SetLadderYawNow(ladder.FacingYaw, 90f);
            m_NavigationAnalog = fromPlane < LadderBackOffPastMeters ? (byte)0x10 : (byte)0x12;
            m_IsWalking = true;
            return true;
        }

        // ---- up: the gap between stacked pieces ----

        // The climb ended: at the top, or in a gap. True when it is the top.
        private bool IsLadderClimbDone(BotLadder ladder, Vector3 position)
        {
            if (position.y >= ladder.Top.y - LadderTopReachMeters)
            {
                return true;
            }
            // A top the catalog put on the floor beyond a parapet is up to
            // 1.5m off; standing on the mesh near that height is the top too.
            return m_Player.movement.isGrounded
                && position.y >= ladder.Top.y - 2.5f
                && SkaliaBotUnityNavmesh.IsOnMesh(position);
        }

        // Off the ladder below its top: W into it facing it (the game takes
        // the next piece), then look at it and use it. True while this
        // steers; false lets the usual climb steering run.
        private bool SteerLadderGap(BotLadder ladder, Vector3 position, bool climbing, float now)
        {
            if (climbing)
            {
                if (m_LadderInGap)
                {
                    m_LadderInGap = false;
                    // Taken again lower than the best height: the blocked
                    // climb check starts over from here.
                    m_LadderClimbBestAt = 0f;
                    m_LadderBlockedLogged = false;
                    BotLog(LogLevel.Information,
                        "Escada {Name}: pegou de novo apos o vao a {Y:0.0}m; subindo.",
                        ladder.Name,
                        position.y);
                }
                return false;
            }
            if (!m_LadderInGap)
            {
                m_LadderInGap = true;
                m_LadderGapSince = now;
                m_LadderGapY = position.y;
                m_LadderGaps++;
                m_LadderGapJumped = false;
                m_LadderUpRequests = 0;
                m_LadderUpRequestSearched = false;
                BotLog(LogLevel.Information,
                    "Escada {Name}: o jogo soltou o bot a {Y:0.0}m, abaixo do topo ({Top:0.0}m), num vao entre pedacos; segurando W para pegar o pedaco de cima (vao {Count}).",
                    ladder.Name,
                    position.y,
                    ladder.Top.y,
                    m_LadderGaps);
            }
            if (now - m_LadderGapSince >= LadderGapRegrabSeconds
                && TrySteerClimbRequest(ladder, position, now, position.y + 0.3f, position.y + 2.8f, true))
            {
                return true;
            }
            m_StealthCrouch = false;
            m_IsSprinting = false;
            SetLadderYawNow(ladder.FacingYaw, 60f);
            m_NavigationAnalog = 0x12;
            m_IsWalking = true;
            // Standing on the piece below with the next one above the waist:
            // a jump into it, as at the foot of a ladder that starts high.
            if (!m_LadderGapJumped
                && m_Player.movement.isGrounded
                && now - m_LadderGapSince >= 0.25f)
            {
                m_LadderGapJumped = true;
                m_NavigationJumpUntil = Time.realtimeSinceStartup + 0.25f;
            }
            return true;
        }

        // On the ladder and not going up for 0.8s below the top: something
        // over the head in this piece's column (the x8 recording: thigas hung
        // at 50.2m of the bridge ladder for 30s). The game moves the body to
        // the column of the piece it takes; a piece higher up, taken by
        // looking at it (the use key), may be in another one.
        private float m_LadderClimbBestY;
        private float m_LadderClimbBestAt;
        private bool m_LadderBlockedLogged;

        private bool SteerLadderBlockedClimb(BotLadder ladder, Vector3 position, float now)
        {
            if (position.y > m_LadderClimbBestY + 0.2f || m_LadderClimbBestAt <= 0f)
            {
                m_LadderClimbBestY = position.y;
                m_LadderClimbBestAt = now;
                return false;
            }
            if (now - m_LadderClimbBestAt < 0.8f || position.y >= ladder.Top.y - LadderTopReachMeters)
            {
                return false;
            }
            if (!m_LadderBlockedLogged)
            {
                m_LadderBlockedLogged = true;
                m_LadderUpRequests = 0;
                m_LadderUpRequestSearched = false;
                BotLog(LogLevel.Information,
                    "Escada {Name}: subida parada a {Y:0.0}m (topo {Top:0.0}m), algo em cima nesta coluna; olhando para o pedaco de cima e usando, como os jogadores.",
                    ladder.Name,
                    position.y,
                    ladder.Top.y);
            }
            return TrySteerClimbRequest(ladder, position, now, position.y + 1.6f, position.y + 4f, true);
        }

        // ---- down on the ladder: drop and catch ----

        // On the ladder: true while this steers.
        private bool SteerLadderDrop(BotLadder ladder, Vector3 position, float now, bool climbing, float holdYaw)
        {
            var heightLeft = position.y - ladder.Bottom.y;
            if (climbing)
            {
                if (m_LadderDropping)
                {
                    // Still on it right after letting go: the turn has not
                    // reached the game yet.
                    if (now - m_LadderDropStartedAt < 0.3f)
                    {
                        SetLadderYawNow(holdYaw + LadderDropTurnDegrees, 90f);
                        m_NavigationAnalog = 0x11;
                        m_IsWalking = false;
                        return true;
                    }
                    m_LadderDropping = false;
                    m_LadderDropNextAt = now + 0.15f;
                }
                // x19: not while the body is still at the edge it came from.
                // At the bridge (-289, 100) the bots let go 0.2s after the
                // grab, 16m over the foot, landed back on the top and the
                // descent was logged as done: 87 times in the x18 test.
                if (heightLeft <= 2f
                    || now < m_LadderDropNextAt
                    || ladder.Top.y - position.y < LadderDropTopClearMeters)
                {
                    return false;
                }
                m_LadderDropping = true;
                m_LadderDropStartedAt = now;
                m_LadderDropFromY = position.y;
                m_LadderDropCount++;
                m_LadderDownHeld = false;
                SetLadderYawNow(holdYaw + LadderDropTurnDegrees, 90f);
                m_NavigationAnalog = 0x11;
                m_IsWalking = false;
                return true;
            }
            if (!m_LadderDropping)
            {
                return false;
            }
            var fallen = m_LadderDropFromY - position.y;
            var wholeWay = m_LadderDropFromY - ladder.Bottom.y <= LadderDropWholeMeters;
            if (!m_Player.movement.isGrounded && !wholeWay && fallen < LadderDropMeters && heightLeft > 1.5f)
            {
                SetLadderYawNow(holdYaw + LadderDropTurnDegrees, 90f);
                m_NavigationAnalog = 0x11;
                m_IsWalking = false;
                return true;
            }
            // 0.2.2x11: near the foot it is not caught again either (the
            // catch 1.4m over the ground at the bridge held the bot there).
            if ((wholeWay || heightLeft <= 2f) && !m_Player.movement.isGrounded)
            {
                // The rest is under the fall damage height: down in one go,
                // like Onça's last 5m.
                m_NavigationAnalog = 0x11;
                m_IsWalking = false;
                return true;
            }
            // Catch: face it now; the game takes it in the air.
            m_LadderDropping = false;
            m_LadderDropNextAt = now + 0.2f;
            SetLadderYawNow(holdYaw, 90f);
            m_NavigationAnalog = 0x11;
            m_IsWalking = false;
            return !m_Player.movement.isGrounded;
        }

        // ---- the patrol that would wait for a failed ladder ----

        private int m_LadderAsideLadder = -1;
        private float m_LadderAsideUntil;
        private int m_LadderAsideCount;

        private bool TrySetObjectiveAsideForLadder(BotLadder ladder, Vector3 destination, float until, float now)
        {
            var type = m_ObjectiveType;
            if (type != ObjectiveType.Patrol
                && type != ObjectiveType.Loot
                && type != ObjectiveType.Explore
                && type != ObjectiveType.Travel)
            {
                return false;
            }
            if (m_LadderAsideLadder != ladder.Index || !Mathf.Approximately(m_LadderAsideUntil, until))
            {
                m_LadderAsideLadder = ladder.Index;
                m_LadderAsideUntil = until;
                m_LadderAsideCount = 0;
            }
            // On a roof whose only way down is that ladder every objective
            // needs it: after two the bot waits for the retry instead.
            if (m_LadderAsideCount >= 2)
            {
                return false;
            }
            m_LadderAsideCount++;
            var seconds = Mathf.Max(until - now, 10f);
            if (type == ObjectiveType.Loot || type == ObjectiveType.Patrol)
            {
                m_MegaRouteAvoid.Add(new KeyValuePair<Vector3, float>(destination, now + seconds));
            }
            if (type == ObjectiveType.Explore)
            {
                m_HasInterestArea = false;
            }
            if (type == ObjectiveType.Travel)
            {
                m_TravelActive = false;
                m_TravelPhase = 0;
                m_TravelMeshArea = false;
            }
            BotLog(LogLevel.Information,
                "A rota de {Objective} ate {Destination} so passa pela escada {Name}, que falhou ha pouco: objetivo deixado de lado por {Seconds:0}s em vez de esperar parado.",
                type,
                FormatPosition(destination),
                ladder.Name,
                seconds);
            ClearObjective();
            return true;
        }

        // ---- a stop on purpose is not a stuck bot ----

        private float m_DeliberateHoldSince = -1f;

        // Waiting for the Mega to leave the route, for a ladder retry, at
        // the end of a partial route: the 2s and 20s watchdogs took these for
        // a stuck bot (33 unstuck maneuvers and 35 teleports in the x9 test).
        // Up to 30s in a row; past that the watchdogs act again.
        private bool IsDeliberateHold(float now)
        {
            var holding = IsMegaRouteHolding()
                || m_NavigationHold
                || now < m_RouteWaitUntil;
            if (!holding)
            {
                m_DeliberateHoldSince = -1f;
                return false;
            }
            if (m_DeliberateHoldSince < 0f)
            {
                m_DeliberateHoldSince = now;
            }
            return now - m_DeliberateHoldSince < 30f;
        }

        // ---- corners passed between two checks ----

        private Vector3 m_CornerCheckFrom;
        private bool m_HasCornerCheckFrom;

        // The bot sends its keys every 4 physics frames and walks 0.36m
        // (0.6m sprinting) on each: a 0.3m corner is often stepped over
        // with no check inside it. The x8 recording on the Tower_Military_2
        // top shows it: past the corner, back for it, past again.
        private bool PassedCornerSinceLastCheck(Vector3 corner, Vector3 position)
        {
            if (!m_HasCornerCheckFrom)
            {
                return false;
            }
            var from = m_CornerCheckFrom;
            if ((position - from).GetHorizontalMagnitude() > 1.5f)
            {
                return false;
            }
            return DistanceToSegment2D(corner, from, position) <= CornerReachMeters;
        }

        private void NoteCornerCheck(Vector3 position)
        {
            m_CornerCheckFrom = position;
            m_HasCornerCheckFrom = true;
        }
    }
}
