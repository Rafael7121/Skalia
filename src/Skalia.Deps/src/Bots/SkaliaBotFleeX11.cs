using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x11: a flight always has somewhere to run. In the x11 test
    // thigas, fleeing the Mega, got two escape points with no mesh within 8m
    // (the snap gave the raw point back); each route failed, the flight was
    // dropped and he stood 4s with the Mega at 1.2m. Now an escape point
    // must have a route on the new mesh, and with none among the usual
    // candidates the bot runs to mesh ground away from the threats, the
    // nearest ring that has a route (25, 16, then 9m).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const int EscapeRouteChecks = 12;
        private static readonly float[] s_RunAwayRadii = { 25f, 16f, 9f };
        private static readonly float[] s_RunAwayAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f };
        private float m_NextRunAwayLogAt;
        private int m_RunAwayFallbacks;

        private bool PickRoutedEscapeCandidate(
            Vector3 origin,
            List<Vector3> threats,
            float rejectMeters,
            float penaltyMeters,
            float flatPenalty,
            float slope,
            out Vector3 best)
        {
            best = origin;
            m_EscapeCandidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            var bestScore = float.NegativeInfinity;
            var found = false;
            for (var i = 0; i < m_EscapeCandidates.Count && i < EscapeRouteChecks; i++)
            {
                var candidate = m_EscapeCandidates[i];
                if (!TryScoreEscapeRoute(origin, candidate.Point, threats, rejectMeters, penaltyMeters, flatPenalty, slope, out var routePenalty, out var rejected)
                    || rejected)
                {
                    continue;
                }
                var score = candidate.Score - routePenalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Point;
                    found = true;
                }
            }
            if (found)
            {
                return true;
            }
            if (!TryFindRunAwayOnMesh(origin, threats, out best))
            {
                return false;
            }
            m_RunAwayFallbacks++;
            if (Time.realtimeSinceStartup >= m_NextRunAwayLogAt)
            {
                m_NextRunAwayLogAt = Time.realtimeSinceStartup + 10f;
                BotLog(LogLevel.Information,
                    "Fuga sem ponto bom com rota ({Candidates} candidatos); correndo para {Point} ({Distance:0}m), chao da malha longe das ameacas ({Count} no total).",
                    m_EscapeCandidates.Count,
                    FormatPosition(best),
                    (best - origin).GetHorizontalMagnitude(),
                    m_RunAwayFallbacks);
            }
            return true;
        }

        private bool TryFindRunAwayOnMesh(Vector3 origin, List<Vector3> threats, out Vector3 point)
        {
            point = origin;
            var centroid = Vector3.zero;
            var count = 0;
            var nearestThreat = float.MaxValue;
            foreach (var threat in threats)
            {
                var distance = (threat - origin).GetHorizontalMagnitude();
                if (distance > 40f)
                {
                    continue;
                }
                centroid += threat;
                count++;
                nearestThreat = Mathf.Min(nearestThreat, distance);
            }
            var away = count > 0 ? origin - centroid / count : Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
            }
            away.Normalize();
            foreach (var radius in s_RunAwayRadii)
            {
                foreach (var angle in s_RunAwayAngles)
                {
                    var desired = origin + Quaternion.Euler(0f, angle, 0f) * away * radius;
                    if (!SkaliaBotUnityNavmesh.TrySnap(desired, 4f, out var snapped)
                        || Mathf.Abs(snapped.y - origin.y) > 6f
                        || (snapped - origin).GetHorizontalMagnitude() < radius * 0.5f
                        || WaterUtility.isPointUnderwater(snapped + Vector3.up * 0.5f)
                        || IsCloserToThreats(snapped, threats, nearestThreat + radius * 0.3f)
                        || !SkaliaBotUnityNavmesh.IsReachable(origin, snapped))
                    {
                        continue;
                    }
                    var walk = SkaliaBotUnityNavmesh.WalkLength(origin, snapped);
                    if (walk > radius * 2.5f)
                    {
                        continue;
                    }
                    point = snapped;
                    return true;
                }
            }
            return false;
        }

        private static bool IsCloserToThreats(Vector3 point, List<Vector3> threats, float minimum)
        {
            if (minimum >= float.MaxValue * 0.5f)
            {
                return false;
            }
            foreach (var threat in threats)
            {
                if ((threat - point).GetHorizontalMagnitude() < minimum)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
