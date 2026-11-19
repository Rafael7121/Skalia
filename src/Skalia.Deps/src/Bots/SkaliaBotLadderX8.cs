using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x8 (doc 16.16): ladders by the game's own rules. The x7 test had
    // 6 of 11 climbs and 1 of 4 descents; every failure was the bot standing
    // where the game's check cannot pass (the side or the foot taken without
    // the check), and each failure banned the ladder for every bot for 30 to
    // 60 min. Now the catalog keeps the foot where PlayerStance.simulate
    // starts the climb, the grab from the top is searched with the checks of
    // PlayerStance.ReceiveClimbRequest, nothing is banned, and a zombie at the
    // foot is fought first.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // A failed try only keeps this bot off that ladder for a moment, so
        // the route is not asked for the same ladder in the same second.
        private const float LadderRetrySeconds = 20f;
        private const float LadderZombieRetrySeconds = 8f;
        private const float LadderZombieMeters = 4f;
        private readonly Dictionary<int, float> m_LadderRetryAfter = new Dictionary<int, float>();
        private float m_NextLadderJumpAt;

        private bool IsLadderPaused(int index, float now)
        {
            return m_LadderRetryAfter.TryGetValue(index, out var until) && now < until;
        }

        private void PauseLadder(BotLadder ladder, float now, float seconds)
        {
            m_LadderRetryAfter[ladder.Index] = now + seconds;
        }

        // A zombie coming at the bot before it is on the ladder: the fight
        // comes first (Tadeu stood at the foot of the bridge ladder with his
        // hands empty and was killed from 100 health, 18:33).
        private bool TryAbortLadderForZombie(Vector3 position, float now)
        {
            var ladder = m_Ladder;
            if (ladder == null || m_Player.stance.stance == EPlayerStance.CLIMB)
            {
                return false;
            }
            // 0.2.2x17: only a zombie that can hit the bot from where it
            // stands (SkaliaBotLadderX17).
            var aggroed = GetNearestAggroedZombieDistanceInReach();
            if (aggroed > LadderZombieMeters && now - m_LastZombieBiteAt >= 1f)
            {
                return false;
            }
            BotLog(LogLevel.Information,
                "Escada {Name} em {Bottom} adiada: zumbi a {Distance:0.#}m; lutando primeiro.",
                ladder.Name,
                FormatPosition(m_LadderDown ? ladder.Top : ladder.Bottom),
                aggroed);
            PauseLadder(ladder, now, LadderZombieRetrySeconds);
            m_Ladder = null;
            m_LadderPhase = 0;
            m_LadderDown = false;
            m_IsWalking = false;
            ClearObjective();
            ClearPath();
            return true;
        }

        // Phase 2 going up: walk in facing the ladder until the climb starts,
        // never past its line; a ladder that starts above the waist is
        // reached with a jump, like a player does.
        private void SteerLadderApproach(BotLadder ladder, Vector3 position, bool climbing, float now)
        {
            var aligned = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, m_DesiredYaw)) < 15f;
            if (climbing)
            {
                m_IsWalking = true;
                return;
            }
            // 0.2.2x15: behind it, round to the front; off its line, onto
            // it first (SkaliaBotLadderX15).
            if (TryWalkRoundToLadderFront(ladder, position, now)
                || TrySteerOntoLadderLine(ladder, position, now))
            {
                return;
            }
            var offset = position - ladder.Center;
            offset.y = 0f;
            var fromLine = Vector3.Dot(offset, ladder.Face);
            m_IsWalking = aligned && fromLine > 0.25f;
            if (ladder.JumpToGrab
                && aligned
                && fromLine <= ladder.StandGap + 0.9f
                && m_Player.movement.isGrounded
                && now >= m_NextLadderJumpAt)
            {
                m_NextLadderJumpAt = now + 0.9f;
                m_NavigationJumpUntil = Time.realtimeSinceStartup + 0.25f;
                m_IsWalking = true;
            }
        }

        // Facing the ladder. 0.2.2x15: the players walk in facing it (yaw
        // 135 at the bridge); the old walk-in bent up to 30 degrees toward
        // the line and caught the ladder's edge. The line is reached sideways
        // now (SkaliaBotLadderX15).
        private static float GetLadderApproachYaw(BotLadder ladder, Vector3 position)
        {
            return ladder.FacingYaw;
        }

        // ---- the grab from the top (PlayerStance.ReceiveClimbRequest) ----
        // From the eye along the look: a sphere cast (player radius, 4m)
        // and a ray (4m) must both hit a collider tagged Ladder, on its face
        // (|normal . up| > 0.9) with the collider's up level; the body goes
        // to the ladder's line + normal * 0.65 at the hit's height - 1.1m,
        // and needs a clear line from the hit to there and standing room.
        private readonly List<Vector3> m_LadderGrabStands = new List<Vector3>();
        private readonly List<Vector3> m_LadderGrabDirs = new List<Vector3>();
        private int m_LadderGrabIndex = -1;
        private float m_LadderGrabTriedAt;
        private bool m_LadderGrabSearched;

        private static bool WouldGrabFromTop(Vector3 eye, Vector3 direction)
        {
            var ray = new Ray(eye, direction);
            if (!Physics.SphereCast(ray, PlayerStance.RADIUS, out var sphere, 4f, RayMasks.LADDER_INTERACT)
                || sphere.collider == null
                || !sphere.collider.transform.CompareTag("Ladder"))
            {
                return false;
            }
            if (!Physics.Raycast(ray, out var hit, 4f, RayMasks.LADDER_INTERACT)
                || hit.collider == null
                || !hit.collider.transform.CompareTag("Ladder"))
            {
                return false;
            }
            var up = hit.collider.transform.up;
            if (Mathf.Abs(Vector3.Dot(hit.normal, up)) <= 0.9f
                || Mathf.Abs(Vector3.Dot(Vector3.up, up)) > 0.1f)
            {
                return false;
            }
            var line = hit.collider.transform.position;
            var point = new Vector3(line.x, hit.point.y - 0.5f - 0.5f - 0.1f, line.z) + hit.normal * 0.65f;
            var height = PlayerMovement.HEIGHT_STAND + 0.1f + 0.5f;
            if (Physics.Linecast(hit.point, point + new Vector3(0f, height * 0.5f, 0f), out _, RayMasks.BLOCK_STANCE, QueryTriggerInteraction.Collide))
            {
                return false;
            }
            return PlayerStance.hasHeightClearanceAtPosition(point, height);
        }

        // Places at the top's level within 1.6m of the ladder from which a
        // look grabs it, nearest to the bot first.
        private void SearchLadderGrabs(BotLadder ladder, Vector3 position)
        {
            m_LadderGrabSearched = true;
            m_LadderGrabStands.Clear();
            m_LadderGrabDirs.Clear();
            m_LadderGrabIndex = -1;
            var eyeOffset = m_Player.look.aim.position - m_Player.transform.position;
            var side = Vector3.Cross(Vector3.up, ladder.Face).normalized;
            var center = new Vector3(ladder.Center.x, ladder.Top.y, ladder.Center.z);
            var stands = new List<Vector3> { position };
            for (var radius = 0.5f; radius <= 1.6f; radius += 0.35f)
            {
                for (var angle = 0f; angle < 360f; angle += 30f)
                {
                    var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    stands.Add(center + direction * radius);
                }
            }
            var found = new List<KeyValuePair<float, int>>();
            foreach (var candidate in stands)
            {
                if (!Physics.Raycast(candidate + Vector3.up * 1.2f, Vector3.down, out var ground, 2.4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                    || Mathf.Abs(ground.point.y - ladder.Top.y) > 0.6f
                    || !PlayerStance.hasHeightClearanceAtPosition(ground.point, PlayerMovement.HEIGHT_STAND))
                {
                    continue;
                }
                var eye = ground.point + eyeOffset;
                if (!TryFindGrabDirection(ladder, eye, side, out var direction))
                {
                    continue;
                }
                found.Add(new KeyValuePair<float, int>((ground.point - position).GetHorizontalMagnitude(), m_LadderGrabStands.Count));
                m_LadderGrabStands.Add(ground.point);
                m_LadderGrabDirs.Add(direction);
            }
            found.Sort((a, b) => a.Key.CompareTo(b.Key));
            var standsSorted = new List<Vector3>();
            var dirsSorted = new List<Vector3>();
            foreach (var entry in found)
            {
                standsSorted.Add(m_LadderGrabStands[entry.Value]);
                dirsSorted.Add(m_LadderGrabDirs[entry.Value]);
            }
            m_LadderGrabStands.Clear();
            m_LadderGrabStands.AddRange(standsSorted);
            m_LadderGrabDirs.Clear();
            m_LadderGrabDirs.AddRange(dirsSorted);
            m_LadderGrabIndex = m_LadderGrabStands.Count > 0 ? 0 : -1;
            BotLog(LogLevel.Information,
                "Topo da escada {Name}: {Count} lugar(es) de onde o jogo aceita agarrar olhando{Nearest}.",
                ladder.Name,
                m_LadderGrabStands.Count,
                m_LadderGrabStands.Count > 0
                    ? " (o mais perto a " + (m_LadderGrabStands[0] - position).GetHorizontalMagnitude().ToString("0.0") + "m)"
                    : "; vai pelo passo na beirada");
        }

        private static bool TryFindGrabDirection(BotLadder ladder, Vector3 eye, Vector3 side, out Vector3 direction)
        {
            direction = Vector3.zero;
            foreach (var collider in ladder.Colliders)
            {
                if (collider == null)
                {
                    continue;
                }
                var bounds = collider.bounds;
                var high = Mathf.Min(bounds.max.y - 0.15f, ladder.Top.y + 0.8f);
                var low = Mathf.Max(bounds.min.y + 0.15f, ladder.Top.y - 3.2f);
                for (var y = high; y >= low; y -= 0.25f)
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

        // Phase 2 going down with a grab place found: walk there, look where
        // the game takes the ladder and grab. True while this handles it.
        private bool SteerLadderGrab(BotLadder ladder, Vector3 position, float now, out bool grabbed)
        {
            grabbed = false;
            if (!m_LadderGrabSearched)
            {
                SearchLadderGrabs(ladder, position);
            }
            if (m_LadderGrabIndex < 0 || m_LadderGrabIndex >= m_LadderGrabStands.Count)
            {
                return false;
            }
            var stand = m_LadderGrabStands[m_LadderGrabIndex];
            var toStand = stand - position;
            toStand.y = 0f;
            var turn = LadderDownTurnDegreesPerSecond * TurnStepSeconds;
            if (toStand.magnitude > 0.3f)
            {
                // A step or two on the roof: face it and walk.
                m_DesiredYaw = DirectionToYaw(toStand);
                m_DesiredPitch = 100f;
                m_CurrentYaw = Mathf.MoveTowardsAngle(m_CurrentYaw, m_DesiredYaw, turn);
                m_CurrentPitch = Mathf.MoveTowardsAngle(m_CurrentPitch, m_DesiredPitch, turn);
                m_NavigationAnalog = 0x12;
                m_IsWalking = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, m_DesiredYaw)) < 20f;
                if (now - m_LadderPhaseStartedAt >= 4f)
                {
                    // Could not get there: next place.
                    m_LadderGrabIndex++;
                    m_LadderPhaseStartedAt = now;
                }
                return true;
            }
            m_IsWalking = false;
            var eye = m_Player.look.aim.position;
            var side = Vector3.Cross(Vector3.up, ladder.Face).normalized;
            if (!TryFindGrabDirection(ladder, eye, side, out var direction))
            {
                direction = m_LadderGrabDirs[m_LadderGrabIndex];
            }
            var flat = new Vector3(direction.x, 0f, direction.z);
            m_DesiredYaw = flat.sqrMagnitude > 0.0001f ? DirectionToYaw(flat) : m_CurrentYaw;
            m_DesiredPitch = 90f - Mathf.Atan2(direction.y, Mathf.Max(0.01f, flat.magnitude)) * Mathf.Rad2Deg;
            m_CurrentYaw = Mathf.MoveTowardsAngle(m_CurrentYaw, m_DesiredYaw, turn);
            m_CurrentPitch = Mathf.MoveTowardsAngle(m_CurrentPitch, m_DesiredPitch, turn);
            var onTarget = Mathf.Abs(Mathf.DeltaAngle(m_CurrentYaw, m_DesiredYaw)) < 3f
                && Mathf.Abs(Mathf.DeltaAngle(m_CurrentPitch, m_DesiredPitch)) < 3f;
            if (!onTarget || now - m_LadderGrabTriedAt < 0.5f)
            {
                return true;
            }
            m_LadderGrabTriedAt = now;
            m_CurrentYaw = m_DesiredYaw;
            m_CurrentPitch = m_DesiredPitch;
            var before = m_Player.transform.position;
            var context = BotInvocationContext();
            m_Player.stance.ReceiveClimbRequest(in context, direction);
            var after = m_Player.transform.position;
            if ((after - before).sqrMagnitude > 0.04f)
            {
                grabbed = true;
                OnLadderGrabbedFromTop(ladder, after);
                return true;
            }
            // The game said no from here: the next place.
            BotLog(LogLevel.Information,
                "Agarrar a escada {Name} de {Position} nao funcionou; tentando o proximo lugar.",
                ladder.Name,
                FormatPosition(position));
            m_LadderGrabIndex++;
            m_LadderPhaseStartedAt = now;
            return m_LadderGrabIndex < m_LadderGrabStands.Count;
        }

        // The game put the bot on the ladder facing it; the packets already
        // queued still look at the edge and would let go at once.
        private void OnLadderGrabbedFromTop(BotLadder ladder, Vector3 after)
        {
            var outward = ladder.Face;
            var toCenter = new Vector3(ladder.Center.x, after.y, ladder.Center.z) - after;
            toCenter.y = 0f;
            var holdYaw = toCenter.sqrMagnitude > 0.01f ? DirectionToYaw(toCenter) : ladder.FacingYaw;
            m_LadderDownGrabYaw = holdYaw;
            m_CurrentYaw = holdYaw;
            m_DesiredYaw = holdYaw;
            m_CurrentPitch = 90f;
            m_DesiredPitch = 90f;
            m_LastTickPosition = after;
            foreach (var queued in m_Player.input.serversidePackets)
            {
                if (queued is WalkingPlayerInputPacket walking)
                {
                    walking.clientPosition = after;
                    walking.yaw = holdYaw;
                    walking.pitch = 90f;
                    walking.analog = 0x11;
                }
            }
            m_IsWalking = false;
            _ = outward;
        }
    }
}
