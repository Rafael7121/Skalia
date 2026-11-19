using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x14 (the tester): "os bots nao nadam, o que e ruim pq ficam presos em
    // ilhas. adicione a habilidade de nado. e eles mergulham enquanto nadam
    // para nao ficar expostos."
    //
    // The terrain map treated water as a wall, so an island had no route
    // and a river turned every trip back (thigas at the Clearwater river,
    // llo with Rainbridge Island). Now a planned route may cross water
    // (SkaliaBotTerrainMap: three times the cost of land, in from a bank
    // at most 3m over the surface, out onto one at most 1.5m over it), and
    // in the water the bot keeps following it instead of swimming back to
    // the nearest bank.
    //
    // The game (PlayerMovement, PlayerLife, Zombie):
    // - at the surface a swimmer moves flat; looking more than 20 degrees
    //   down while moving forward takes it under, and under water it moves
    //   along its aim; jump pushes it up at 3 m/s;
    // - oxygen falls under water and comes back at the surface (about 16s
    //   each way);
    // - a zombie gives up a player who swims, unless the zombie itself is
    //   under water.
    //
    // How the players swim (24 recordings, 11.4h, 9 swims of 4s or more):
    // - under the surface almost all the way: they go down 0.6s after
    //   entering (median) and come up 0.8s before leaving (median);
    // - looking 111 degrees (21 down; p25 111, p75 118), moving forward,
    //   tapping jump to hold the depth: feet 1.2-2.2m below where a
    //   floating swimmer's feet are (p25-p75; floating feet are 1.275m
    //   under the surface, PlayerMovement), i.e. 2.5-3.5m under it;
    // - 5.2 m/s (median).
    // The recordings have no oxygen column; the game drowns at 0 oxygen, so
    // the bot comes up at 10 (rising 3 m/s from 3.5m costs about 3 points)
    // and goes down again at 100, as it went in.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float SwimPitch = 111f;
        private const float SwimBandTop = 2.47f;
        private const float SwimBandBottom = 3.5f;
        private const float SwimDiveMinDepth = 3.5f;
        private const byte SwimSurfaceOxygen = 10;
        private const byte SwimDiveOxygen = 100;
        private const float SwimSurfaceNearExitMeters = 4f;
        // The same watch as a swim to the bank (UpdateExitWaterState: 12s
        // without getting closer) and the same 90s memory of a bad spot
        // (NoteWaterExit).
        private const float SwimCrossStallSeconds = 12f;
        private const float SwimCrossBlockedSeconds = 90f;
        private bool m_SwimHoldJump;

        private float m_TravelSwimMeters;
        private bool m_SwimCrossActive;
        private float m_SwimCrossBest;
        private float m_SwimCrossProgressAt;
        private float m_SwimCrossBlockedUntil;
        private Vector3 m_SwimCrossExit;
        private int m_SwimDiveMode;
        private float m_NextSwimLogAt;
        // x16: the x15 test had no crossing that worked (thigas 22:35, llo
        // 23:13, Tadeu 23:17): each bot turned in circles a few metres in,
        // around a point it had already reached. These lines say which
        // route point it is steering to and who set it.
        private float m_NextSwimCrossTraceAt;
        private Vector3 m_SwimCrossLastPoint;
        private bool m_SwimCrossHasPoint;

        private void TraceSwimCross(string what, float now, Vector3 position, int index, int next, int exitIndex)
        {
            if (now < m_NextSwimCrossTraceAt)
            {
                return;
            }
            m_NextSwimCrossTraceAt = now + 2f;
            var points = new System.Text.StringBuilder();
            var from = Mathf.Max(0, index - 1);
            var to = Mathf.Min(m_TravelPlan.Count - 1, index + 4);
            for (var i = from; i <= to; i++)
            {
                var p = m_TravelPlan[i];
                points.Append(i).Append(SkaliaBotTerrainMap.IsSwimPoint(p) ? "~" : "#")
                    .Append(FormatPosition(p)).Append(' ')
                    .Append((p - position).GetHorizontalMagnitude().ToString("0.0")).Append("m; ");
            }
            BotLog(LogLevel.Information,
                "Travessia ({What}): bot em {Position} postura {Stance}; objetivo {Type} em {Objective} ({Distance:0.0}m); ponto {Index}, seguinte {Next}, margem {Exit}, indice da rota {PlanIndex}/{Count}; pontos: {Points}",
                what,
                FormatPosition(position),
                m_Player.stance.stance,
                m_ObjectiveType,
                FormatPosition(m_ObjectivePosition),
                (m_ObjectivePosition - position).GetHorizontalMagnitude(),
                index,
                next,
                exitIndex,
                m_TravelPlanIndex,
                m_TravelPlan.Count,
                points.ToString());
        }

        // After a terrain plan: how much of it is swum.
        private void NoteTravelSwimPlan()
        {
            m_TravelSwimMeters = 0f;
            for (var i = 1; i < m_TravelPlan.Count; i++)
            {
                if (SkaliaBotTerrainMap.IsSwimPoint(m_TravelPlan[i])
                    || SkaliaBotTerrainMap.IsSwimPoint(m_TravelPlan[i - 1]))
                {
                    m_TravelSwimMeters += (m_TravelPlan[i] - m_TravelPlan[i - 1]).GetHorizontalMagnitude();
                }
            }
            if (m_TravelSwimMeters > 0f)
            {
                BotLog(LogLevel.Information,
                    "Rota ate {Name} atravessa {Meters:0}m de agua a nado.",
                    m_TravelTargetName,
                    m_TravelSwimMeters);
            }
        }

        private bool HasSwimLegAhead(Vector3 position, out int index)
        {
            index = -1;
            if (!m_TravelActive || m_TravelSwimMeters <= 0f || m_TravelPlan.Count == 0)
            {
                return false;
            }
            var start = Mathf.Clamp(m_TravelPlanIndex - 2, 0, m_TravelPlan.Count - 1);
            var nearest = 30f;
            for (var i = start; i < m_TravelPlan.Count; i++)
            {
                var gap = (m_TravelPlan[i] - position).GetHorizontalMagnitude();
                if (gap < nearest)
                {
                    nearest = gap;
                    index = i;
                }
            }
            return index >= 0;
        }

        // On land, the route's next point in the water: walk straight in
        // (the walking mesh ends at the bank).
        private bool IsSwimEntryLeg(Vector3 position)
        {
            return m_ObjectiveType == ObjectiveType.Travel
                && m_TravelActive
                && m_TravelSwimMeters > 0f
                && SkaliaBotTerrainMap.IsSwimPoint(m_ObjectivePosition)
                && (m_ObjectivePosition - position).GetHorizontalMagnitude() <= 30f;
        }

        // In ComputeTerrainWaypoint: the next planned point is in the water.
        private bool TrySwimWaypoint(Vector3 position, Vector3 goal, out Vector3 waypoint)
        {
            waypoint = goal;
            return m_TravelSwimMeters > 0f
                && SkaliaBotTerrainMap.IsSwimPoint(goal)
                && (goal - position).GetHorizontalMagnitude() <= 30f;
        }

        // In TickWaterBehavior: in the water on a route that crosses it,
        // follow the route to the far bank instead of the nearest one.
        private bool TryTickSwimCrossing(float now)
        {
            var position = m_BehaviorPosition;
            if (m_SwimCrossActive && m_SwimCrossHasPoint
                && (m_ObjectivePosition - m_SwimCrossLastPoint).sqrMagnitude > 0.01f)
            {
                m_NextSwimCrossTraceAt = 0f;
                TraceSwimCross("objetivo trocado fora da travessia", now, position, -1, -1, -1);
            }
            if (now < m_SwimCrossBlockedUntil || !HasSwimLegAhead(position, out var index))
            {
                if (m_SwimCrossActive)
                {
                    m_NextSwimCrossTraceAt = 0f;
                    TraceSwimCross(now < m_SwimCrossBlockedUntil ? "bloqueada" : "nenhum ponto da rota a 30m", now, position, -1, -1, -1);
                }
                m_SwimCrossActive = false;
                m_SwimCrossHasPoint = false;
                return false;
            }
            // The far bank: the first dry point after the water ahead.
            var exitIndex = -1;
            for (var i = index; i < m_TravelPlan.Count; i++)
            {
                if (!SkaliaBotTerrainMap.IsSwimPoint(m_TravelPlan[i]))
                {
                    exitIndex = i;
                    break;
                }
            }
            if (exitIndex < 0)
            {
                if (m_SwimCrossActive)
                {
                    m_NextSwimCrossTraceAt = 0f;
                    TraceSwimCross("sem margem depois da agua", now, position, index, -1, -1);
                }
                m_SwimCrossActive = false;
                m_SwimCrossHasPoint = false;
                return false;
            }
            var exit = m_TravelPlan[exitIndex];
            var toExit = (exit - position).GetHorizontalMagnitude();
            if (!m_SwimCrossActive || (m_SwimCrossExit - exit).GetHorizontalMagnitude() > 4f)
            {
                m_SwimCrossActive = true;
                m_SwimCrossExit = exit;
                m_SwimCrossBest = toExit;
                m_SwimCrossProgressAt = now;
                BotLog(LogLevel.Information,
                    "Atravessando a nado ate a margem em {Exit} ({Distance:0}m), seguindo a rota para {Name}.",
                    FormatPosition(exit),
                    toExit,
                    m_TravelTargetName);
            }
            else if (toExit < m_SwimCrossBest - 1f)
            {
                m_SwimCrossBest = toExit;
                m_SwimCrossProgressAt = now;
            }
            else if (now - m_SwimCrossProgressAt > SwimCrossStallSeconds)
            {
                // A current, a rock or a bank it cannot climb: the usual
                // way out of the water, and no crossing here for 90s.
                m_NextSwimCrossTraceAt = 0f;
                TraceSwimCross("sem progresso por 12s", now, position, index, -1, exitIndex);
                m_SwimCrossActive = false;
                m_SwimCrossHasPoint = false;
                m_SwimCrossBlockedUntil = now + SwimCrossBlockedSeconds;
                FailTerrainTravel(now, "travessia a nado sem progresso por 12s");
                return false;
            }
            // The water timer that teleports a lost swimmer (60s) counts
            // from the last progress, not from the jump in.
            m_WaterEpisodeSince = Mathf.Max(m_WaterEpisodeSince, m_SwimCrossProgressAt);
            m_LastInWaterAt = now;
            if (m_InWaterSince < 0f)
            {
                m_InWaterSince = now;
            }
            // The next point: through the water, or the bank itself.
            // 0.2.2x17: never behind the point already reached. In the x16
            // test the nearest point was taken each tick: 4m past it, the
            // target went back to it (9 switches in 8s, the bot turning on
            // the spot; all 3 crossings, 20:07, 21:11 and 23:25).
            var next = Mathf.Min(Mathf.Max(index, m_TravelPlanIndex), exitIndex);
            while (next < exitIndex && (m_TravelPlan[next] - position).GetHorizontalMagnitude() <= 4f)
            {
                next++;
            }
            m_TravelPlanIndex = Mathf.Max(m_TravelPlanIndex, next);
            var point = m_TravelPlan[next];
            m_SwimExitActive = true;
            m_SwimExitShallows = next == exitIndex && next > 0 ? m_TravelPlan[next - 1] : point;
            m_SwimExitBestDistance = toExit;
            m_SwimExitProgressSince = now;
            if (m_ObjectiveType != ObjectiveType.ExitWater)
            {
                SetObjective(ObjectiveType.ExitWater, point, now + 30f);
            }
            else
            {
                m_ObjectivePosition = point;
                m_ObjectiveExpiresAt = now + 30f;
            }
            m_SwimCrossLastPoint = point;
            m_SwimCrossHasPoint = true;
            TraceSwimCross("seguindo", now, position, index, next, exitIndex);
            return true;
        }

        // The dive, on the packet: the pitch and the jump key.
        private void ApplySwimDive(WalkingPlayerInputPacket packet, ref ushort keys, bool climbingOut)
        {
            var player = m_Player;
            if (player.stance.stance != EPlayerStance.SWIM || climbingOut)
            {
                m_SwimDiveMode = 0;
                return;
            }
            var position = player.transform.position;
            WaterUtility.getUnderwaterInfo(position, out _, out var surface);
            var bottom = position.y - 30f;
            if (Physics.Raycast(position + Vector3.up * 0.2f, Vector3.down, out var hit, 30f, SwimExitGroundMask, QueryTriggerInteraction.Ignore))
            {
                bottom = hit.point.y;
            }
            var depthHere = surface - bottom;
            var below = surface - position.y;
            var toGoal = (m_ObjectivePosition - position).GetHorizontalMagnitude();
            var oxygen = player.life.oxygen;
            var wantsUnder = m_ObjectiveType == ObjectiveType.ExitWater
                && m_IsWalking
                && depthHere >= SwimDiveMinDepth
                && toGoal > SwimSurfaceNearExitMeters
                && (!m_SwimCrossActive || (m_SwimCrossExit - position).GetHorizontalMagnitude() > SwimSurfaceNearExitMeters);
            var previous = m_SwimDiveMode;
            if (!wantsUnder || oxygen <= SwimSurfaceOxygen)
            {
                m_SwimDiveMode = player.stance.isSubmerged ? 3 : 0;
            }
            else if (m_SwimDiveMode == 3)
            {
                if (!player.stance.isSubmerged)
                {
                    m_SwimDiveMode = 0;
                }
            }
            else if (m_SwimDiveMode == 0)
            {
                // Down again only with the lungs full; under water by
                // accident, up first.
                m_SwimDiveMode = player.stance.isSubmerged ? 3 : oxygen >= SwimDiveOxygen ? 1 : 0;
            }
            switch (m_SwimDiveMode)
            {
                case 1:
                case 2:
                    // Down at 111 degrees, jump tapped to stay in the band.
                    packet.pitch = SwimPitch;
                    if (below > SwimBandBottom)
                    {
                        m_SwimHoldJump = true;
                    }
                    else if (below < SwimBandTop)
                    {
                        m_SwimHoldJump = false;
                    }
                    if (m_SwimHoldJump)
                    {
                        keys |= player.input.flags[0];
                    }
                    m_SwimDiveMode = below >= SwimBandTop ? 2 : 1;
                    break;
                case 3:
                    packet.pitch = SwimPitch;
                    keys |= player.input.flags[0];
                    break;
            }
            if (previous != m_SwimDiveMode && Time.realtimeSinceStartup >= m_NextSwimLogAt
                && (m_SwimDiveMode == 1 || m_SwimDiveMode == 3))
            {
                m_NextSwimLogAt = Time.realtimeSinceStartup + 8f;
                BotLog(LogLevel.Information,
                    m_SwimDiveMode == 1
                        ? "Mergulhando como os jogadores: 2,5 a 3,5m abaixo da superficie, olhando 21 graus para baixo (oxigenio {Oxygen}, fundo a {Bottom:0}m; {Depth:0.#})."
                        : "Subindo para respirar (profundidade {Depth:0.#}m, oxigenio {Oxygen}, fundo a {Bottom:0}m).",
                    m_SwimDiveMode == 1 ? SwimBandTop : below,
                    oxygen,
                    depthHere);
            }
        }
    }
}
