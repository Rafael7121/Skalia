using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;

namespace SkaliaDeps
{
    internal static partial class SkaliaBotUnityNavmesh
    {
        private static NavMeshPath? s_WayInPath;

        // The walk a man would take from a point (snapped within snapMeters)
        // to another, as the mesh has it; only a whole way.
        public static bool TryWalkPath(Vector3 from, Vector3 to, float snapMeters, List<Vector3> corners)
        {
            corners.Clear();
            if (!MapFilled
                || !NavMesh.SamplePosition(Grounded(from), out var start, snapMeters, s_Filter)
                || !NavMesh.SamplePosition(Grounded(to), out var end, 2f, s_Filter))
            {
                return false;
            }
            s_WayInPath ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(start.position, end.position, s_Filter, s_WayInPath)
                || s_WayInPath.status != NavMeshPathStatus.PathComplete)
            {
                return false;
            }
            corners.AddRange(s_WayInPath.corners);
            return corners.Count >= 2;
        }
    }

    // 0.1.0-y17.14 (the tester, 18/11 23:16, test at Scorpion-7 with blazex:
    // "quando eu atiro ele sempre crava a mira a onde eu estou ... ele n
    // deve mirar pra onde eu estou, com base na distancia que da pra saber
    // por conta do som ele deve supor que estou ali no setor da floresta e
    // marcar a entrada da scorpion que e o unico caminho que eu posso fazer
    // pra chegar ate ele (isso se ele nao ouvir barulho no telhado)").
    // 23:13:13-23:14:56: blazex, hidden in the lab, faced the sector it
    // heard him in (204 degrees, ~88m; he was at 213), a wall between them,
    // for 103s; the mean of two estimates with the players' error had
    // landed 9 degrees off him.
    //
    // Watching a man known only by the sound of his shots (the hide, and
    // the watch of where a shot came from), the bot faces the way he would
    // come: the mesh's walk from the sector to the bot, followed from his
    // end, and the first point of it the bot's eye has in line (a door, a
    // gate, the corner of a corridor). In the open that is the sector
    // itself, as before. Steps heard keep their own turn to them
    // (SkaliaBotPvpMemoryY). Whether it is hidden is still judged from the
    // sector (TryLeaveOpenWatch), not from the door it watches.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpWayInSnapMeters = 12f;
        private const float PvpWayInStepMeters = 1f;
        private const float PvpWayInMinMeters = 3f;
        private readonly List<Vector3> m_PvpWayInCorners = new List<Vector3>();
        private bool m_PvpWayInActive;
        private Vector3 m_PvpWatchFrom;

        // The point the hidden man is watched for, from where he is believed.
        private void ApplyPvpWayIn(Vector3 from, string name, float now)
        {
            m_PvpWatchFrom = from;
            m_PvpWayInActive = true;
            if (!TryFindPvpWayIn(from, out var watch, out var walk))
            {
                m_PvpHuntPoint = from;
                return;
            }
            m_PvpHuntPoint = watch;
            var here = m_Player.transform.position;
            BotLog(LogLevel.Information,
                "PvP: {Name} so pelo som, para la de {From:0}m; vigiando por onde ele chega: {Watch} a {Meters:0}m de mim ({Walk:0}m do caminho dele).",
                name,
                (from - here).GetHorizontalMagnitude(),
                FormatPosition(watch),
                (watch - here).GetHorizontalMagnitude(),
                walk);
        }

        private bool TryFindPvpWayIn(Vector3 from, out Vector3 watch, out float walk)
        {
            watch = from;
            walk = 0f;
            var feet = m_Player.transform.position;
            var eye = m_Player.look.aim.position;
            if (!SkaliaBotUnityNavmesh.TryWalkPath(from, feet, PvpWayInSnapMeters, m_PvpWayInCorners))
            {
                return false;
            }
            for (var i = 0; i + 1 < m_PvpWayInCorners.Count; i++)
            {
                var a = m_PvpWayInCorners[i];
                var b = m_PvpWayInCorners[i + 1];
                var length = (b - a).magnitude;
                var steps = Mathf.Max(1, Mathf.CeilToInt(length / PvpWayInStepMeters));
                for (var s = 0; s < steps; s++)
                {
                    var point = Vector3.Lerp(a, b, s / (float)steps);
                    if ((point - feet).GetHorizontalMagnitude() < PvpWayInMinMeters)
                    {
                        return false;
                    }
                    var chest = point + Vector3.up * 1.3f;
                    if (!Physics.Linecast(eye, chest, s_PvpSightMask, QueryTriggerInteraction.Ignore))
                    {
                        watch = chest;
                        walk += length * s / steps;
                        return true;
                    }
                }
                walk += length;
            }
            return false;
        }
    }
}
