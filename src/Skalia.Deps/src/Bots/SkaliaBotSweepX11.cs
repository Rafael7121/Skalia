using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;

namespace SkaliaDeps
{
    // 0.2.2x11: the visit order of a house. In the x10 test the patrol took
    // the nearest unvisited loot point again at every pick, measured in a
    // straight line through the walls, and a point in sight counted as seen
    // from 5m: the bot turned to the next nearest one before getting
    // anywhere, often behind it. llo on the ground floor of the Arlington
    // Farm house had targets in six directions in 4s (7 routes in 8s);
    // thigas upstairs walked 7m north, 14m south and 18m north again.
    // A player goes room by room. Here, on reaching a floor of a house, the
    // points of that floor are put in order once, each the nearest to the
    // last by the walk (the mesh's route, through the door), and taken in
    // that order. The order moves only forward; a point seen on the way is
    // dropped from it, never walked back to.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float SweepFloorMeters = 2.2f;
        private const float SweepLooseMeters = 15f;
        private const int SweepWalkChecks = 6;

        private readonly List<Vector3> m_SweepCandidates = new List<Vector3>();
        private readonly HashSet<Vector3> m_SweepCandidateSet = new HashSet<Vector3>();
        private readonly List<Vector3> m_SweepOrder = new List<Vector3>();
        private readonly List<Vector3> m_SweepPool = new List<Vector3>();
        private int m_SweepCursor;
        private BotBuilding? m_SweepBuilding;
        private float m_SweepFloorY;
        private string m_SweepAreaKey = string.Empty;
        private int m_SweepPlans;
        private float m_PatrolNearBestLeft = float.MaxValue;

        // What is left of the route from here, or -1 without one.
        private float GetRouteMetersLeft(Vector3 position)
        {
            if (m_PathIndex >= m_PathPoints.Count)
            {
                return -1f;
            }
            var left = (m_PathPoints[m_PathIndex] - position).magnitude;
            for (var index = m_PathIndex + 1; index < m_PathPoints.Count; index++)
            {
                left += (m_PathPoints[index] - m_PathPoints[index - 1]).magnitude;
            }
            return left;
        }

        private void ResetSweepPlan()
        {
            m_SweepOrder.Clear();
            m_SweepCursor = 0;
            m_SweepBuilding = null;
            m_SweepAreaKey = string.Empty;
        }

        // `anchor` is the old pick (nearest, same house, same floor first):
        // it says which house and floor; the order says which point.
        private Vector3 PickSweepPoint(Vector3 position, Vector3 anchor, float now)
        {
            if (!SkaliaBotUnityNavmesh.MapFilled)
            {
                return anchor;
            }
            m_SweepCandidateSet.Clear();
            foreach (var point in m_SweepCandidates)
            {
                m_SweepCandidateSet.Add(point);
            }
            if (!m_SweepCandidateSet.Contains(anchor))
            {
                // The pick was a point to avoid (escape or horde): as before.
                return anchor;
            }
            var building = IsHuntSweep ? null : SkaliaBotStructureMap.FindBuilding(anchor);
            var samePlan = m_SweepOrder.Count > 0
                && m_SweepAreaKey == m_InterestAreaKey
                && building == m_SweepBuilding
                && Mathf.Abs(anchor.y - m_SweepFloorY) <= SweepFloorMeters
                && (building != null || (anchor - m_SweepOrder[0]).GetHorizontalMagnitude() <= SweepLooseMeters * 2f);
            if (samePlan && TryTakeSweepPoint(now, out var next))
            {
                return next;
            }
            BuildSweepPlan(position, anchor, building);
            return TryTakeSweepPoint(now, out next) ? next : anchor;
        }

        private bool TryTakeSweepPoint(float now, out Vector3 point)
        {
            for (var index = m_SweepCursor; index < m_SweepOrder.Count; index++)
            {
                var candidate = m_SweepOrder[index];
                if (!m_SweepCandidateSet.Contains(candidate) || IsHazardousPosition(candidate, now, 2f))
                {
                    continue;
                }
                m_SweepCursor = index;
                point = candidate;
                return true;
            }
            m_SweepCursor = m_SweepOrder.Count;
            point = Vector3.zero;
            return false;
        }

        private void BuildSweepPlan(Vector3 position, Vector3 anchor, BotBuilding? building)
        {
            m_SweepOrder.Clear();
            m_SweepCursor = 0;
            m_SweepBuilding = building;
            m_SweepFloorY = anchor.y;
            m_SweepAreaKey = m_InterestAreaKey;
            m_SweepPool.Clear();
            foreach (var point in m_SweepCandidates)
            {
                if (Mathf.Abs(point.y - anchor.y) > SweepFloorMeters)
                {
                    continue;
                }
                if (building != null
                        ? !building.Contains(point, 0.25f)
                        : (point - anchor).GetHorizontalMagnitude() > SweepLooseMeters)
                {
                    continue;
                }
                m_SweepPool.Add(point);
            }
            var from = position;
            var walks = 0;
            while (m_SweepPool.Count > 0)
            {
                var origin = from;
                m_SweepPool.Sort((a, b) => (a - origin).sqrMagnitude.CompareTo((b - origin).sqrMagnitude));
                var bestIndex = 0;
                var bestCost = float.MaxValue;
                var checks = Mathf.Min(SweepWalkChecks, m_SweepPool.Count);
                for (var index = 0; index < checks; index++)
                {
                    var cost = GetSweepWalkCost(origin, m_SweepPool[index], ref walks);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestIndex = index;
                    }
                }
                from = m_SweepPool[bestIndex];
                m_SweepOrder.Add(from);
                m_SweepPool.RemoveAt(bestIndex);
            }
            m_SweepPlans++;
            BotLog(LogLevel.Information,
                "Ordem de visita de {Where} (andar a {Floor:0.#}m): {Count} ponto(s), do mais perto andando ao seguinte; primeiro {First} ({Walks} rotas medidas).",
                building != null ? building.Name : "area sem casa",
                anchor.y,
                m_SweepOrder.Count,
                m_SweepOrder.Count > 0 ? FormatPosition(m_SweepOrder[0]) : "nenhum",
                walks);
        }

        // In a straight line on the mesh: the distance. Otherwise the length
        // of the mesh's route (round the wall, through the door).
        private static float GetSweepWalkCost(Vector3 from, Vector3 to, ref int walks)
        {
            var straight = (to - from).magnitude;
            if (Mathf.Abs(to.y - from.y) <= 1.2f && SkaliaBotUnityNavmesh.IsStraight(from, to))
            {
                return straight;
            }
            walks++;
            var length = SkaliaBotUnityNavmesh.WalkLength(from, to);
            return length > 0f ? Mathf.Max(length, straight) : straight * 3f + 20f;
        }

        private bool TryPickDoorwayOnMesh(Vector3 position, BotBuilding building, out Vector3 doorway)
        {
            doorway = Vector3.zero;
            var bestDistance = float.MaxValue;
            foreach (var candidate in building.Doorways)
            {
                var distance = (candidate - position).GetHorizontalMagnitude();
                if (distance < 3f
                    || distance >= bestDistance
                    || IsNearOrbitBadSpot(candidate, m_BehaviorNow, OrbitBadSpotMeters)
                    || !SkaliaBotUnityNavmesh.IsReachable(position, candidate))
                {
                    continue;
                }
                bestDistance = distance;
                doorway = candidate;
            }
            return bestDistance < float.MaxValue;
        }
    }

    internal static partial class SkaliaBotUnityNavmesh
    {
        private static NavMeshPath? s_WalkPath;

        // The length of the complete route between two points of the mesh
        // (loot points on a shelf are taken down to the floor), or -1.
        public static float WalkLength(Vector3 from, Vector3 to)
        {
            if (!MapFilled
                || !NavMesh.SamplePosition(Grounded(from), out var start, 2f, s_Filter)
                || !NavMesh.SamplePosition(Grounded(to), out var end, 2f, s_Filter))
            {
                return -1f;
            }
            s_WalkPath ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(start.position, end.position, s_Filter, s_WalkPath)
                || s_WalkPath.status != NavMeshPathStatus.PathComplete)
            {
                return -1f;
            }
            var corners = s_WalkPath.corners;
            var length = 0f;
            for (var index = 1; index < corners.Length; index++)
            {
                length += (corners[index] - corners[index - 1]).magnitude;
            }
            return length;
        }
    }
}
