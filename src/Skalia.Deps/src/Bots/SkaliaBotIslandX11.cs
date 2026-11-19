using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;

namespace SkaliaDeps
{
    // 0.2.2x11: thigas jumped onto a bed upstairs in the Arlington Farm house
    // (02:43:05) and the mesh on top of it was a piece of its own: every
    // route stopped there ("a malha nova para a 41,7m do destino") and he
    // stood on it to the end of the test. The floor 1-5m away, lower, in a
    // straight line and with no route to it: step down onto it, as a player
    // does, and route from the floor.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private float m_NextIslandCheckAt;
        private int m_IslandExits;

        private bool TryLeaveMeshIsland(Vector3 origin, Vector3 destination, float now)
        {
            if (now < m_NextIslandCheckAt
                || m_Ladder != null
                || m_Player.stance.stance == EPlayerStance.SWIM
                || !m_Player.movement.isGrounded)
            {
                return false;
            }
            m_NextIslandCheckAt = now + 1f;
            if (!SkaliaBotUnityNavmesh.TryFindIslandExit(origin, destination - origin, out var exit))
            {
                return false;
            }
            m_IslandExits++;
            m_UnityPathScratch.Clear();
            m_UnityPathScratch.Add(exit);
            AcceptUnityRoute(origin, destination, now, RouteEnd.Leg, "descendo de uma ilha da malha");
            BotLog(LogLevel.Information,
                "Bot numa ilha da malha em {Position} (em cima de movel ou caixa, sem rota para o chao em volta); descendo reto para {Exit} ({Distance:0.#}m, {Drop:0.#}m abaixo) e pedindo a rota dali ({Count} no total).",
                FormatPosition(origin),
                FormatPosition(exit),
                (exit - origin).GetHorizontalMagnitude(),
                origin.y - exit.y,
                m_IslandExits);
            return true;
        }

        private bool TryPickLocalExplorationOnMesh(Vector3 position, float now, out Vector3 chosen)
        {
            chosen = position;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var direction = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f) * Vector3.forward;
                var desired = position + direction * Random.Range(25f, 75f);
                if (!Physics.Raycast(desired + Vector3.up * 40f, Vector3.down, out var ground, 120f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                    || !SkaliaBotUnityNavmesh.TrySnap(ground.point, 2f, out var candidate)
                    || IsHazardousPosition(candidate, now, 2f)
                    || (candidate - position).GetHorizontalMagnitude() < 15f
                    || !SkaliaBotUnityNavmesh.IsReachable(position, candidate))
                {
                    continue;
                }
                chosen = candidate;
                return true;
            }
            return false;
        }
    }

    internal static partial class SkaliaBotUnityNavmesh
    {
        private static readonly float[] s_IslandRadii = { 1.5f, 2.5f, 3.5f, 5f };
        private static NavMeshPath? s_IslandPath;

        // Mesh lower than the bot (0.3-2.4m down), 1-5m away in a straight
        // line, that the mesh has no route to from where the bot stands.
        public static bool TryFindIslandExit(Vector3 position, Vector3 toward, out Vector3 exit)
        {
            exit = position;
            if (!MapFilled || !NavMesh.SamplePosition(position, out var start, 1f, s_Filter))
            {
                return false;
            }
            toward.y = 0f;
            var preferred = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.zero;
            s_IslandPath ??= new NavMeshPath();
            // The piece the bot stands on is small: no mesh at its height
            // 3m away that it reaches (a room, a roof or a yard has some).
            for (var step = 0; step < 8; step++)
            {
                var angle = step * Mathf.PI / 4f;
                var probe = start.position + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 3f;
                if (NavMesh.SamplePosition(probe, out var level, 0.5f, s_Filter)
                    && Mathf.Abs(level.position.y - start.position.y) <= 0.3f
                    && NavMesh.CalculatePath(start.position, level.position, s_Filter, s_IslandPath)
                    && s_IslandPath.status == NavMeshPathStatus.PathComplete)
                {
                    return false;
                }
            }
            foreach (var radius in s_IslandRadii)
            {
                var bestScore = float.MaxValue;
                for (var step = 0; step < 16; step++)
                {
                    var angle = step * Mathf.PI / 8f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    var probe = position + direction * radius + Vector3.down * 1f;
                    if (!NavMesh.SamplePosition(probe, out var hit, 1.5f, s_Filter))
                    {
                        continue;
                    }
                    var candidate = hit.position;
                    var drop = start.position.y - candidate.y;
                    if ((candidate - position).GetHorizontalMagnitude() < 1f
                        || drop < 0.3f
                        || drop > 2.4f
                        || WaterUtility.isPointUnderwater(candidate + Vector3.up * 0.5f)
                        || Physics.Linecast(position + Vector3.up * 0.6f, candidate + Vector3.up * 0.6f, SourceMask, QueryTriggerInteraction.Ignore)
                        || Physics.Linecast(position + Vector3.up * 1.4f, candidate + Vector3.up * 1.4f, SourceMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    if (NavMesh.CalculatePath(start.position, candidate, s_Filter, s_IslandPath)
                        && s_IslandPath.status == NavMeshPathStatus.PathComplete)
                    {
                        // Reached by the mesh: not an island, or not this way.
                        return false;
                    }
                    var score = -Vector3.Dot(direction, preferred);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        exit = candidate;
                    }
                }
                if (bestScore < float.MaxValue)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
