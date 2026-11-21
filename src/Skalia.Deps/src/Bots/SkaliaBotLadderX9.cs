using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x9 (doc 16.17): ladders the way the players' recordings show.
    //
    // Up (34 climbs of four players): walk or run into
    // it facing it and hold W (2.7 m/s up). Where the ladder starts high (the
    // Ladder_Metal_0 by the barracks) they stop at the foot, look up at it
    // (pitch 39-64) and take it (the use key: the game puts them on it
    // 2.7m up). The bot did only the walk-in and failed there.
    //
    // Down: the other player looks down at it from the roof (60 degrees) and takes it;
    // the tester, facing the wall as when he climbed, walks backwards off the
    // edge and the ladder in front of him catches him. On it, both hold S
    // and slide down at 2.7 m/s (PlayerMovement: climbing speed is the W/S
    // input times half the walk speed). The bots let go and grabbed again
    // in short falls (77 and 87 health at the bottom in the x8 test) and,
    // with no place to grab by looking, stepped off facing out and never
    // pressed W: 8 failed descents of the two Ladder_Wood_0.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LadderBackOffStandMeters = 0.5f;
        private const float LadderBackOffPitch = 130f;
        private const float LadderSlidePitch = 80f;
        private const float LadderFootLeaveMeters = 0.4f;
        private const float LadderUpRequestAfterSeconds = 1.2f;

        private bool m_LadderBackOff;
        private float m_LadderBackOffSince;
        private float m_LadderUpRequestTriedAt;
        private int m_LadderUpRequests;
        private bool m_LadderUpRequestSearched;
        private Vector3 m_LadderUpRequestDirection;
        private bool m_LadderUpRequestFound;

        private void ResetLadderX9()
        {
            m_LadderMidGrab = false;
            m_LadderBackOff = false;
            m_LadderBackOffSince = 0f;
            m_LadderUpRequestTriedAt = 0f;
            m_LadderUpRequests = 0;
            m_LadderUpRequestSearched = false;
            m_LadderUpRequestFound = false;
            ResetLadderX10();
            ResetLadderX15();
        }

        private static Vector3 FlatFace(BotLadder ladder)
        {
            var face = ladder.Face;
            face.y = 0f;
            if (face.sqrMagnitude < 0.01f)
            {
                face = -(Quaternion.Euler(0f, ladder.FacingYaw, 0f) * Vector3.forward);
                face.y = 0f;
            }
            return face.normalized;
        }

        private void TurnLadder(float yaw, float pitch)
        {
            var turn = LadderDownTurnDegreesPerSecond * TurnStepSeconds;
            m_DesiredYaw = yaw;
            m_DesiredPitch = pitch;
            m_CurrentYaw = Mathf.MoveTowardsAngle(m_CurrentYaw, m_DesiredYaw, turn);
            m_CurrentPitch = Mathf.MoveTowardsAngle(m_CurrentPitch, m_DesiredPitch, turn);
        }

        // the tester's way down when no place lets the bot take it by looking:
        // on the roof just behind the ladder, face the wall as when climbing
        // up, walk backwards over the edge, and in the air W into the ladder
        // (the waist ray takes it within 0.75m).
        private bool SteerLadderBackOff(BotLadder ladder, Vector3 position, float now)
        {
            if (!m_LadderBackOff)
            {
                m_LadderBackOff = true;
                m_LadderBackOffSince = now;
                BotLog(LogLevel.Information,
                    "Topo da escada {Name}: descendo de costas pela beirada de frente para a escada, como os jogadores.",
                    ladder.Name);
            }
            var face = FlatFace(ladder);
            var faceYaw = ladder.FacingYaw;
            m_StealthCrouch = false;
            m_IsSprinting = false;
            var line = new Vector3(ladder.Center.x, position.y, ladder.Center.z);
            var offset = position - line;
            offset.y = 0f;
            var across = Vector3.Cross(Vector3.up, face).normalized;
            var fromPlane = Vector3.Dot(offset, face);
            var sideways = Vector3.Dot(offset, across);
            // 0.2.2x10: in the air off the top (walked or jumped off), back
            // only until past the ladder's line, then W into it.
            if (SteerLadderBackOffAir(ladder, position, fromPlane, now))
            {
                return true;
            }
            if (Mathf.Abs(sideways) > 0.3f || fromPlane < -1.5f)
            {
                // Onto the ladder's line, on the roof side.
                var stand = line - face * LadderBackOffStandMeters;
                var toStand = stand - position;
                toStand.y = 0f;
                TurnLadder(DirectionToYaw(toStand), 100f);
                m_NavigationAnalog = 0x12;
                m_IsWalking = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, m_DesiredYaw)) < 25f;
                return true;
            }
            TurnLadder(faceYaw, LadderBackOffPitch);
            m_NavigationAnalog = 0x10;
            m_IsWalking = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, faceYaw)) < 10f;
            // 0.2.2x10: a parapet stops the S: jump backwards over it.
            if (m_IsWalking)
            {
                TryJumpLadderLip(ladder, fromPlane, now);
            }
            return true;
        }

        // On the ladder going down: S, facing it, to the foot; there turn
        // away and walk off (the tester at 290.7s: he turned and was off).
        private bool SteerLadderSlideDown(BotLadder ladder, Vector3 position, float now, bool climbing)
        {
            var holdYaw = float.IsNaN(m_LadderDownGrabYaw) ? ladder.FacingYaw : m_LadderDownGrabYaw;
            var heightLeft = position.y - ladder.Bottom.y;
            m_StealthCrouch = false;
            m_IsSprinting = false;
            // 0.2.2x10: drop a few meters and catch it again, like the tester and
            // the other player (SkaliaBotLadderX10).
            if (SteerLadderDrop(ladder, position, now, climbing, holdYaw))
            {
                return true;
            }
            if (climbing)
            {
                m_LadderDownHeld = true;
                m_LadderDownLastHeldY = position.y;
                if (heightLeft <= LadderFootLeaveMeters)
                {
                    TurnLadder(holdYaw + 180f, 90f);
                    m_NavigationAnalog = 0x12;
                    m_IsWalking = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, holdYaw)) > 100f;
                    return true;
                }
                TurnLadder(holdYaw, LadderSlidePitch);
                m_NavigationAnalog = 0x10;
                m_IsWalking = true;
                return true;
            }
            // x19: standing at the top again is not down.
            if (m_Player.movement.isGrounded && ladder.Top.y - position.y < LadderDropTopClearMeters)
            {
                FailLadderDescent(position, now, "voltou para o topo (" + heightLeft.ToString("0.0") + "m acima do pe)");
                return true;
            }
            if (m_Player.movement.isGrounded)
            {
                FinishLadderDescent(
                    position,
                    now,
                    heightLeft < 1.5f ? null : "soltou a " + heightLeft.ToString("0.0") + "m do pe");
                return true;
            }
            // 0.2.2x11: off it at the foot is down. In the x10 test llo let go
            // 0.3m over the ground at the bridge, took it again with W and
            // turned off it again for 25s (the tester: the climb ends only at the
            // top or by looking away from the ladder).
            if (heightLeft <= 1.5f)
            {
                FinishLadderDescent(position, now, null);
                return true;
            }
            // Off it in the air: W into it again.
            if (m_LadderDownHeld)
            {
                m_LadderDownHeld = false;
                m_LadderDownDrops++;
            }
            TurnLadder(holdYaw, 90f);
            m_NavigationAnalog = 0x12;
            m_IsWalking = true;
            return true;
        }

        // Going up a ladder the walk-in did not take within 1.2s: look up at
        // it from the foot and take it, like the players at the barracks
        // ladder. True while this steers.
        private bool TrySteerClimbRequestFromFoot(BotLadder ladder, Vector3 position, float now)
        {
            return TrySteerClimbRequest(ladder, position, now, position.y + 0.3f, position.y + 3.6f, true);
        }

        // Look at the ladder between `low` and `high` and take it (the use
        // key). `up`: hold W once on it, else stand still for the descent.
        private bool TrySteerClimbRequest(BotLadder ladder, Vector3 position, float now, float low, float high, bool up)
        {
            if (m_LadderUpRequests >= 3)
            {
                return false;
            }
            if (!m_LadderUpRequestSearched)
            {
                m_LadderUpRequestSearched = true;
                var side = Vector3.Cross(Vector3.up, FlatFace(ladder)).normalized;
                m_LadderUpRequestFound = TryFindClimbDirection(
                    ladder,
                    m_Player.look.aim.position,
                    side,
                    low,
                    high,
                    out m_LadderUpRequestDirection);
                BotLog(LogLevel.Information,
                    "Escada {Name}: {Why}; {What}.",
                    ladder.Name,
                    up
                        ? "entrando andando nao pegou em " + (now - m_LadderPhaseStartedAt).ToString("0.0") + "s"
                        : "bot ao lado dela fora da malha",
                    m_LadderUpRequestFound
                        ? "olhando para ela para agarrar, como os jogadores"
                        : "nenhum ponto dela que o jogo aceite agarrar daqui");
            }
            if (!m_LadderUpRequestFound)
            {
                return false;
            }
            var direction = m_LadderUpRequestDirection;
            var flat = new Vector3(direction.x, 0f, direction.z);
            var yaw = flat.sqrMagnitude > 0.0001f ? DirectionToYaw(flat) : m_CurrentYaw;
            var pitch = 90f - Mathf.Atan2(direction.y, Mathf.Max(0.01f, flat.magnitude)) * Mathf.Rad2Deg;
            TurnLadder(yaw, pitch);
            m_IsWalking = false;
            m_StealthCrouch = false;
            m_IsSprinting = false;
            var onTarget = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, yaw)) < 3f
                && Mathf.Abs(Mathf.DeltaAngle(m_CurrentPitch, pitch)) < 3f;
            if (!onTarget || now - m_LadderUpRequestTriedAt < 0.5f)
            {
                return true;
            }
            m_LadderUpRequestTriedAt = now;
            m_LadderUpRequests++;
            m_CurrentYaw = yaw;
            m_CurrentPitch = pitch;
            var before = m_Player.transform.position;
            var context = BotInvocationContext();
            m_Player.stance.ReceiveClimbRequest(in context, direction);
            var after = m_Player.transform.position;
            if ((after - before).sqrMagnitude <= 0.04f)
            {
                // Search again from where the bot stands now.
                m_LadderUpRequestSearched = false;
                return m_LadderUpRequests < 3;
            }
            BotLog(LogLevel.Information,
                "Agarrou a escada {Name} olhando para ela; {Way} de {From:0.0}m.",
                ladder.Name,
                up ? "subindo" : "descendo",
                after.y);
            var hold = ladder.FacingYaw;
            m_CurrentYaw = hold;
            m_DesiredYaw = hold;
            m_CurrentPitch = 60f;
            m_DesiredPitch = 60f;
            m_LastTickPosition = after;
            foreach (var queued in m_Player.input.serversidePackets)
            {
                if (queued is WalkingPlayerInputPacket walking)
                {
                    walking.clientPosition = after;
                    walking.yaw = hold;
                    walking.pitch = 60f;
                }
            }
            m_NavigationAnalog = 0x12;
            m_IsWalking = up;
            return true;
        }

        // 0.2.2x9: off the mesh beside a ladder (a ledge the climb left the
        // bot on: thigas at 50m on the bridge ladder, 20:10): take it from
        // where it stands and go down it.
        private bool m_LadderMidGrab;

        private bool TryStartMidLadderDescent(Vector3 position, float now)
        {
            if (!s_Settings.LadderDescend || m_Ladder != null)
            {
                return false;
            }
            foreach (var ladder in SkaliaBotLadderMap.Ladders)
            {
                var line = new Vector3(ladder.Center.x, position.y, ladder.Center.z);
                if ((line - position).GetHorizontalMagnitude() > 3f
                    || position.y < ladder.Bottom.y + 1.5f
                    || position.y > ladder.Top.y + 1.5f
                    || IsLadderPaused(ladder.Index, now))
                {
                    continue;
                }
                BotLog(LogLevel.Information,
                    "Bot fora da malha em {Position}, ao lado da escada {Name} ({Height:0.0}m acima do pe): agarrando ela daqui para descer.",
                    FormatPosition(position),
                    ladder.Name,
                    position.y - ladder.Bottom.y);
                StartLadderDescent(ladder, now);
                m_LadderPhase = 2;
                m_LadderPhaseStartedAt = now;
                m_LadderStartY = position.y;
                m_LadderMidGrab = true;
                ClearPath();
                return true;
            }
            return false;
        }

        // Phase 2 of a mid-ladder descent.
        private bool SteerMidLadderGrab(BotLadder ladder, Vector3 position, float now)
        {
            var eye = m_Player.look.aim.position;
            return TrySteerClimbRequest(ladder, position, now, Mathf.Max(ladder.Bottom.y, eye.y - 3.5f), eye.y + 0.8f, false);
        }

        // A look direction from `eye` that the game's grab (ReceiveClimbRequest)
        // accepts, aiming at the ladder between `low` and `high`.
        private static bool TryFindClimbDirection(BotLadder ladder, Vector3 eye, Vector3 side, float low, float high, out Vector3 direction)
        {
            direction = Vector3.zero;
            foreach (var collider in ladder.Colliders)
            {
                if (collider == null)
                {
                    continue;
                }
                var bounds = collider.bounds;
                var top = Mathf.Min(bounds.max.y - 0.15f, high);
                var bottom = Mathf.Max(bounds.min.y + 0.15f, low);
                for (var y = bottom; y <= top; y += 0.25f)
                {
                    for (var lateral = -0.2f; lateral <= 0.21f; lateral += 0.2f)
                    {
                        var target = new Vector3(bounds.center.x, y, bounds.center.z) + side * lateral;
                        var toTarget = target - eye;
                        if (toTarget.magnitude > 3.9f || toTarget.sqrMagnitude < 0.01f)
                        {
                            continue;
                        }
                        var candidate = toTarget.normalized;
                        if (WouldGrabFromTop(eye, candidate))
                        {
                            direction = candidate;
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
