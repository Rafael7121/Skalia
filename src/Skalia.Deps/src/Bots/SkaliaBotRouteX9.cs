using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using SDG.Framework.Water;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x9 (doc 16.17): no more routes on the zombie mesh. In the x7 and
    // x8 tests every new-mesh failure fell back to it, and that route began
    // at the ground node under the roof or tower the bot stood on and ended
    // where the bot could not stand (|dy| > 1.8 never counts as reached):
    // the bot walked into the parapet or fence in between, back and forth,
    // until the stuck handling or a teleport. Each reason the new mesh gives
    // is handled here instead:
    //   - the route goes over a ladder that just failed: wait for the retry
    //     where the bot stands (a flight fails the route instead);
    //   - the mesh is not ready yet: wait 2s;
    //   - longer than one route (260m): walk the long plan leg by leg;
    //   - the bot is off the mesh: walk straight to mesh ground 3-8m away
    //     first, or fail (three times in 2 min at one spot: a catalog
    //     ladder down, else the floor teleport of the circles);
    //   - no complete route: walk as far as the mesh goes when that ends
    //     within 4m of the destination (loot and combat also within 2.2m of
    //     height), and hold there; otherwise fail the objective, so each
    //     objective's own failure handling skips it.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private enum RouteEnd
        {
            // The route ends at the destination's floor: the last steps go
            // straight to the objective, as before.
            Full,
            // The mesh stops short: hold at the end, never walk on at the
            // objective (that was the walk into the wall).
            Partial,
            // A leg of a long trip: route again from its end.
            Leg,
        }

        private const string UnreachableRoutePrefix = "inalcancavel: ";
        private const float PartialReachMeters = 4f;
        private const float PartialReachRiseMeters = 2.2f;
        private const float PartialHoldSeconds = 3f;
        private const float LongLegMeters = 200f;
        private const float NotReadyRetrySeconds = 2f;
        private const int OffMeshFailuresBeforeExit = 3;
        private const float OffMeshWindowSeconds = 120f;
        private const float OffMeshSpotMeters = 4f;
        private const float OffMeshLadderMeters = 5f;

        private RouteEnd m_RouteEnd;
        private float m_RouteWaitUntil;
        private Vector3 m_RouteWaitDestination;
        private bool m_NavigationHold;
        private bool m_NavigationHoldMayCoast;
        private float m_PartialHoldSince = -1f;
        private int m_PartialHoldGeneration = -1;
        private float m_NextRouteFailLogAt;
        private float m_NextRouteWaitLogAt;
        private float m_NextRouteLegLogAt;
        private readonly List<Vector3> m_LongPlanScratch = new List<Vector3>();
        private readonly List<(Vector3 Position, float At)> m_OffMeshFailures = new List<(Vector3 Position, float At)>();

        private void AcceptUnityRoute(Vector3 origin, Vector3 destination, float now, RouteEnd end, string mesh)
        {
            ++m_PathRequestGeneration;
            m_PathPending = false;
            m_PathRequestedAt = now;
            m_PlannedDestination = destination;
            m_NextPathRequestTime = now + 1.5f;
            m_PathPoints.Clear();
            m_PathIndex = 0;
            m_PathFailed = false;
            m_PathFromUnity = true;
            m_RouteEnd = end;
            m_PartialHoldSince = -1f;
            if (m_ObjectiveType == ObjectiveType.Combat)
            {
                m_CombatPathFailures = 0;
            }
            m_PathPoints.AddRange(m_UnityPathScratch);
            AcceptPathPoints(origin, destination, now, mesh);
        }

        private void FailRoute(Vector3 origin, Vector3 destination, float now, string reason)
        {
            ++m_PathRequestGeneration;
            m_PathPending = false;
            m_PathRequestedAt = now;
            m_PlannedDestination = destination;
            m_NextPathRequestTime = now + 1.5f;
            m_PathPoints.Clear();
            m_PathIndex = 0;
            m_PathFailed = true;
            m_RouteEnd = RouteEnd.Full;
            m_LastPathFailureReason = reason;
            m_StatPathFailures++;
            if (now >= m_NextRouteFailLogAt)
            {
                m_NextRouteFailLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "Sem rota pela malha nova ({Reason}); objetivo {Objective} de {Origin} ate {Destination} ({Distance:0}m) desistido.",
                    reason,
                    m_ObjectiveType,
                    FormatPosition(origin),
                    FormatPosition(destination),
                    (destination - origin).GetHorizontalMagnitude());
            }
        }

        private void WaitForRoute(Vector3 destination, float now, float until)
        {
            ++m_PathRequestGeneration;
            m_PathPending = false;
            m_PathPoints.Clear();
            m_PathIndex = 0;
            m_PathFailed = false;
            m_PlannedDestination = destination;
            m_NextPathRequestTime = until;
            m_RouteWaitUntil = until;
            m_RouteWaitDestination = destination;
        }

        private bool IsFlightObjective()
        {
            return m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_ObjectiveType == ObjectiveType.RepositionFire
                || m_ObjectiveType == ObjectiveType.Combat;
        }

        // The route in m_UnityPathScratch goes over a ladder this bot just
        // failed on (or down one with descents off): wait where it stands
        // for the retry, like a player catching breath and trying again.
        private bool TryWaitForRouteLadder(Vector3 origin, Vector3 destination, float now)
        {
            var points = m_UnityPathScratch;
            BotLadder? paused = null;
            var descentOff = false;
            for (var index = 0; index + 1 < points.Count && paused == null && !descentOff; index++)
            {
                var up = SkaliaBotUnityNavmesh.LadderBetween(points[index], points[index + 1]);
                if (up != null && IsLadderPaused(up.Index, now))
                {
                    paused = up;
                    break;
                }
                var down = SkaliaBotUnityNavmesh.LadderBetween(points[index + 1], points[index]);
                if (down == null)
                {
                    continue;
                }
                if (!s_Settings.LadderDescend)
                {
                    descentOff = true;
                    paused = down;
                }
                else if (IsLadderPaused(down.Index, now))
                {
                    paused = down;
                }
            }
            if (paused == null)
            {
                return false;
            }
            if (descentOff)
            {
                FailRoute(origin, destination, now,
                    UnreachableRoutePrefix + "a rota desce a escada " + paused.Name + " e a descida esta desligada");
                return true;
            }
            // 0.2.2x10 (doc 16.18): first the way round without ladders, as
            // a player who cannot use one walks to the stairs.
            if (SkaliaBotUnityNavmesh.TryFindPathWithoutLadders(origin, destination, m_UnityPathScratch))
            {
                if (now >= m_NextRouteWaitLogAt)
                {
                    m_NextRouteWaitLogAt = now + 10f;
                    BotLog(LogLevel.Information,
                        "A rota de {Objective} ate {Destination} passava pela escada {Name}, que falhou ha pouco: indo pelo caminho sem escada.",
                        m_ObjectiveType,
                        FormatPosition(destination),
                        paused.Name);
                }
                AcceptUnityRoute(origin, destination, now, RouteEnd.Full, "malha nova sem escada");
                return true;
            }
            if (IsFlightObjective())
            {
                // 0.2.2x17: that zombie aside until the ladder may be tried
                // again (SkaliaBotLadderX17).
                SetCombatTargetAsideForLadder(paused, now);
                FailRoute(origin, destination, now,
                    "a rota passa pela escada " + paused.Name + ", que falhou ha pouco");
                return true;
            }
            var until = m_LadderRetryAfter.TryGetValue(paused.Index, out var retry) ? retry : now + NotReadyRetrySeconds;
            // 0.2.2x10: a patrol, loot, exploration or trip does not stand on
            // the roof for the retry (the x9 wait was 20s of "stopped", then
            // the unstuck maneuver): that point is left aside until the
            // ladder may be tried again, and the bot goes on with another.
            if (TrySetObjectiveAsideForLadder(paused, destination, until, now))
            {
                return true;
            }
            WaitForRoute(destination, now, until);
            if (now >= m_NextRouteWaitLogAt)
            {
                m_NextRouteWaitLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "A rota de {Objective} ate {Destination} passa pela escada {Name}, que falhou ha pouco: parado esperando {Seconds:0}s para tentar de novo.",
                    m_ObjectiveType,
                    FormatPosition(destination),
                    paused.Name,
                    until - now);
            }
            return true;
        }

        private void RequestRouteWithoutFullPath(Vector3 origin, Vector3 destination, float now)
        {
            var failure = SkaliaBotUnityNavmesh.LastFailure;
            if (!SkaliaBotUnityNavmesh.MapFilled || failure.Length == 0 || failure == "pedaco nao pronto")
            {
                WaitForRoute(destination, now, now + NotReadyRetrySeconds);
                return;
            }
            if (failure == "bot fora da malha" || failure.StartsWith("inicio ", System.StringComparison.Ordinal))
            {
                RequestRouteFromOffMesh(origin, destination, now, failure);
                return;
            }
            if (TryBuildRoute(origin, destination, now, out var end, out var how, out var reason))
            {
                if (TryWaitForRouteLadder(origin, destination, now))
                {
                    return;
                }
                AcceptUnityRoute(origin, destination, now, end, how);
                return;
            }
            // 0.2.2x11: on a piece of mesh of its own (a bed, a table, a
            // crate): every route failed and thigas stood on the bed.
            if (TryLeaveMeshIsland(origin, destination, now))
            {
                return;
            }
            FailRoute(origin, destination, now, reason);
        }

        // With TryFindPath already failed: a leg of the long plan, or the
        // route as far as the mesh goes. Leaves it in m_UnityPathScratch.
        private bool TryBuildRoute(Vector3 start, Vector3 destination, float now, out RouteEnd end, out string how, out string reason)
        {
            end = RouteEnd.Full;
            how = "malha nova";
            reason = string.Empty;
            if (SkaliaBotUnityNavmesh.LastFailure == "longe demais")
            {
                return TryBuildLongLeg(start, destination, now, out end, out how, out reason);
            }
            if (!SkaliaBotUnityNavmesh.TryFindReachRoute(start, destination, m_UnityPathScratch, out var stop))
            {
                reason = UnreachableRoutePrefix + SkaliaBotUnityNavmesh.LastFailure;
                return false;
            }
            var miss = (stop - destination).GetHorizontalMagnitude();
            var rise = Mathf.Abs(stop.y - destination.y);
            var needsHeight = m_ObjectiveType == ObjectiveType.Loot || m_ObjectiveType == ObjectiveType.Combat;
            if (miss <= PartialReachMeters && (!needsHeight || rise <= PartialReachRiseMeters))
            {
                end = RouteEnd.Partial;
                how = "malha nova ate " + miss.ToString("0.0") + "m do destino";
                return true;
            }
            reason = UnreachableRoutePrefix + "a malha nova para a " + miss.ToString("0.0") + "m do destino ("
                + rise.ToString("0.0") + "m de altura), em " + FormatPosition(stop);
            return false;
        }

        // Explore and patrol picks past 260m: the long plan of the trips,
        // walked to its farthest point within 200m, then again from there.
        private bool TryBuildLongLeg(Vector3 start, Vector3 destination, float now, out RouteEnd end, out string how, out string reason)
        {
            end = RouteEnd.Leg;
            how = "malha nova, trecho de rota longa";
            reason = string.Empty;
            if (!SkaliaBotUnityNavmesh.TryPlanLong(start, destination, m_LongPlanScratch, out var failure, out var length, out _))
            {
                reason = UnreachableRoutePrefix + "longe demais e sem rota longa (" + failure + ")";
                return false;
            }
            Vector3? leg = null;
            foreach (var point in m_LongPlanScratch)
            {
                if ((point - start).GetHorizontalMagnitude() > LongLegMeters)
                {
                    break;
                }
                leg = point;
            }
            if (leg == null)
            {
                reason = UnreachableRoutePrefix + "longe demais e o primeiro ponto da rota longa passa de " + LongLegMeters.ToString("0") + "m";
                return false;
            }
            if (!SkaliaBotUnityNavmesh.TryFindPath(start, leg.Value, m_UnityPathScratch))
            {
                reason = UnreachableRoutePrefix + "longe demais e sem rota ate o primeiro trecho (" + SkaliaBotUnityNavmesh.LastFailure + ")";
                return false;
            }
            if (now >= m_NextRouteLegLogAt)
            {
                m_NextRouteLegLogAt = now + 20f;
                BotLog(LogLevel.Information,
                    "{Objective} a {Distance:0}m ({Length:0}m pela malha): andando por trechos, o proximo ate {Leg}.",
                    m_ObjectiveType,
                    (destination - start).GetHorizontalMagnitude(),
                    length,
                    FormatPosition(leg.Value));
            }
            return true;
        }

        // Off the mesh (on a crate, a roof, a car) or with the nearest mesh
        // behind a wall: mesh ground 3-8m away that the body walks to in a
        // straight line, routed from there. The zombie mesh started at its
        // nearest node up to 25m away, through walls and off roofs.
        private void RequestRouteFromOffMesh(Vector3 origin, Vector3 destination, float now, string failure)
        {
            if (now >= m_SkipNavmeshReturnUntil
                && SkaliaBotUnityNavmesh.TryFindEscapePoint(origin, destination - origin, out var onMesh))
            {
                RouteEnd end;
                string how;
                var reason = string.Empty;
                var routed = SkaliaBotUnityNavmesh.TryFindPath(onMesh, destination, m_UnityPathScratch);
                end = RouteEnd.Full;
                how = "malha nova a partir de " + FormatPosition(onMesh);
                if (!routed)
                {
                    routed = TryBuildRoute(onMesh, destination, now, out end, out var built, out reason);
                    how = built + " a partir de " + FormatPosition(onMesh);
                }
                if (routed)
                {
                    if (TryWaitForRouteLadder(origin, destination, now))
                    {
                        return;
                    }
                    AcceptUnityRoute(origin, destination, now, end, how);
                    m_HasNavmeshReturnPoint = true;
                    m_NavmeshReturnPoint = onMesh;
                    m_NavmeshReturnUntil = now + 8f;
                    BotLog(LogLevel.Information,
                        "Bot fora da malha nova em {Position} ({Failure}); indo reto ate {Point} ({Distance:0.#}m) e dali pela rota.",
                        FormatPosition(origin),
                        failure,
                        FormatPosition(onMesh),
                        (onMesh - origin).GetHorizontalMagnitude());
                    return;
                }
                FailRoute(origin, destination, now, reason);
                return;
            }
            // Beside a ladder: take it down, like a player on a ledge.
            if (TryStartMidLadderDescent(origin, now))
            {
                return;
            }
            FailRoute(origin, destination, now, UnreachableRoutePrefix + failure + ", sem chao da malha a 8m em linha reta");
            // Swimming has its own way out (the water exit and its timer).
            if (m_Player.stance.stance != EPlayerStance.SWIM
                && !WaterUtility.isPointUnderwater(origin + Vector3.up * 0.5f))
            {
                NoteOffMeshFailure(origin, now);
            }
        }

        // Three times in 2 min at one spot with no mesh ground in reach: a
        // ladder down if one starts there, else the floor teleport the
        // circles already use as their last resort.
        private void NoteOffMeshFailure(Vector3 position, float now)
        {
            m_OffMeshFailures.RemoveAll(entry => now - entry.At > OffMeshWindowSeconds);
            m_OffMeshFailures.Add((position, now));
            var count = 0;
            foreach (var entry in m_OffMeshFailures)
            {
                if ((entry.Position - position).GetHorizontalMagnitude() <= OffMeshSpotMeters)
                {
                    count++;
                }
            }
            if (count < OffMeshFailuresBeforeExit || m_Ladder != null)
            {
                return;
            }
            m_OffMeshFailures.Clear();
            if (s_Settings.LadderDescend)
            {
                BotLadder? best = null;
                var bestDistance = OffMeshLadderMeters;
                foreach (var ladder in SkaliaBotLadderMap.Ladders)
                {
                    var distance = (ladder.Top - position).GetHorizontalMagnitude();
                    if (distance < bestDistance
                        && Mathf.Abs(ladder.Top.y - position.y) <= 1.5f
                        && !IsLadderPaused(ladder.Index, now))
                    {
                        best = ladder;
                        bestDistance = distance;
                    }
                }
                if (best != null)
                {
                    BotLog(LogLevel.Information,
                        "Bot fora da malha em {Position} ha {Count} pedidos de rota; descendo pela escada {Name} a {Distance:0.#}m.",
                        FormatPosition(position),
                        OffMeshFailuresBeforeExit,
                        best.Name,
                        bestDistance);
                    ClearPath();
                    StartLadderDescent(best, now);
                    return;
                }
            }
            var objective = m_ObjectivePosition;
            if (!SkaliaBotUnityNavmesh.TryFindTeleportPoint(position, objective, out var floor))
            {
                BotLog(LogLevel.Warning,
                    "Bot fora da malha em {Position} ha {Count} pedidos de rota e sem chao da malha alcancavel a 40m.",
                    FormatPosition(position),
                    OffMeshFailuresBeforeExit);
                return;
            }
            BotLog(LogLevel.Warning,
                "Bot fora da malha em {Position} ha {Count} pedidos de rota, sem chao da malha a 8m em linha reta nem escada; teleportando para {Destination} ({Distance:0}m).",
                FormatPosition(position),
                OffMeshFailuresBeforeExit,
                FormatPosition(floor),
                (floor - position).magnitude);
            ClearPath();
            TeleportBot(floor + Vector3.up * 0.3f, m_Player.look.yaw);
            m_StallAnchor = floor;
            m_NextPathRequestTime = 0f;
        }

        // Past the last corner of the route.
        private Vector3 GetRouteEndTarget(Vector3 position, float now)
        {
            if (m_PathPoints.Count == 0 || m_RouteEnd == RouteEnd.Full)
            {
                return m_ObjectivePosition;
            }
            if (m_RouteEnd == RouteEnd.Leg)
            {
                ClearPath();
                m_LastRouteRequestAt = now;
                m_LastRouteRequestDestination = m_ObjectivePosition;
                RequestPath(position, m_ObjectivePosition, now);
                if (m_PathPoints.Count > 0)
                {
                    return m_PathPoints[0];
                }
                m_NavigationHold = true;
                return position;
            }
            // Partial: hold at the end. The objective's own reach checks (the
            // loot in sight, the patrol point within 4m) end it; one still
            // open after 3s fails the route like any other.
            var last = m_PathPoints[m_PathPoints.Count - 1];
            if ((last - position).GetHorizontalMagnitude() > 0.6f)
            {
                return last;
            }
            m_NavigationHold = true;
            if (m_PartialHoldGeneration != m_PathRequestGeneration || m_PartialHoldSince < 0f)
            {
                m_PartialHoldGeneration = m_PathRequestGeneration;
                m_PartialHoldSince = now;
            }
            else if (now - m_PartialHoldSince >= PartialHoldSeconds)
            {
                m_PartialHoldSince = -1f;
                m_PathPoints.Clear();
                m_PathIndex = 0;
                m_RouteEnd = RouteEnd.Full;
                // The failure handling runs before the next request.
                m_NextPathRequestTime = now + 5f;
                m_PathFailed = true;
                m_LastPathFailureReason = "a rota parcial terminou a "
                    + (m_ObjectivePosition - position).GetHorizontalMagnitude().ToString("0.0")
                    + "m do destino e o objetivo nao se cumpriu";
                m_StatPathFailures++;
                BotLog(LogLevel.Information,
                    "Fim da rota parcial em {Position}, a {Distance:0.0}m de {Objective} em {Destination}; desistindo do objetivo.",
                    FormatPosition(position),
                    (m_ObjectivePosition - position).GetHorizontalMagnitude(),
                    m_ObjectiveType,
                    FormatPosition(m_ObjectivePosition));
            }
            return position;
        }
    }
}
