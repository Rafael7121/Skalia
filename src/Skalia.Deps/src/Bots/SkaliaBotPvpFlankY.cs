using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.28 (the tester, 20/11: "bot prever posicionamento do player e
    // se reposicionar em base a previsao. Ex se o player estiver na ponte do
    // Washington e o bot nos campos dar a volta e tentar achar um flanko
    // melhor").
    //
    // Where the players were after the last round of a fight (rec3/
    // predict.py, the public server and the tests, 71-76 ends of a fight):
    // - above the other one by more than 4m and standing still: half of
    //   them within 5m of the spot 10 and 20s later, 12m at 30s (44% under
    //   5m at 10s, 18 cases);
    // - on his level, or moving: 22-26m at 10s and 32-54m at 30s, in any
    //   direction (the way they had been going within 45 degrees only a
    //   third of the time).
    // So a man up high who was not moving is still there; anyone else is
    // not, and the hunt (SkaliaBotPvpHuntY) and the sector
    // (SkaliaBotShotSectorY) keep him as a place that grows.
    //
    // The man up high, lost from sight: the bot goes round to a spot off
    // the line he was watching it on (60 to 140 degrees round him), within
    // its gun's range, from where its head has a line to him, by a route
    // he does not see most of (his eye against the bot's chest, as the
    // watch's cover, SkaliaBotPvpCoverY). There the hunt's arrival takes
    // cover from him and watches him. No such route: the hunt as before.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpFlankAboveMeters = 4f;
        private const float PvpFlankStillSpeed = 2f;
        private const float PvpFlankMinMeters = 15f;
        private const float PvpFlankRouteStep = 6f;
        private const float PvpFlankMaxExposed = 0.5f;
        private const int PvpFlankMaxRoutes = 8;
        private static readonly float[] s_PvpFlankAngles = { 60f, 80f, 100f, 120f, 140f };
        private static readonly float[] s_PvpFlankRadiusShares = { 1f, 0.75f, 0.5f };

        private readonly List<Vector3> m_PvpFlankRoute = new List<Vector3>();

        private struct PvpFlankCandidate
        {
            public Vector3 Spot;
            public float Angle;
            public bool Hidden;
        }

        // From the hunt, with him lost after being seen.
        private bool TryStartPvpFlank(Vector3 position, float meters, float now, string name)
        {
            var range = GetPvpGunRange();
            if (!m_PvpEnemyArmed || range <= 0f)
            {
                return false;
            }
            var point = m_PvpLastSeenPoint;
            var hisEye = PvpStandingEyeAt(point);
            var above = hisEye.y - (position.y + PvpStandEyeHeight);
            var speed = new Vector2(m_PvpLeadVelocity.x, m_PvpLeadVelocity.z).magnitude;
            if (above < PvpFlankAboveMeters || speed >= PvpFlankStillSpeed)
            {
                return false;
            }
            var toBot = position - point;
            toBot.y = 0f;
            if (toBot.sqrMagnitude < 1f)
            {
                return false;
            }
            var reach = Mathf.Min(meters, range * 0.9f);
            if (reach < PvpFlankMinMeters)
            {
                return false;
            }
            // Spots that see him, nearest to the line first.
            var candidates = new List<PvpFlankCandidate>();
            var first = Random.value < 0.5f ? 1f : -1f;
            foreach (var angle in s_PvpFlankAngles)
            {
                foreach (var sign in new[] { first, -first })
                {
                    var direction = Quaternion.Euler(0f, angle * sign, 0f) * toBot.normalized;
                    foreach (var share in s_PvpFlankRadiusShares)
                    {
                        var radius = Mathf.Max(PvpFlankMinMeters, reach * share);
                        if (!TryGetPvpFlankSpot(point + direction * radius, position, out var spot)
                            || !CanPeekFrom(hisEye, spot))
                        {
                            continue;
                        }
                        candidates.Add(new PvpFlankCandidate
                        {
                            Spot = spot,
                            Angle = angle,
                            Hidden = !IsChestInHisLine(point, spot),
                        });
                        break;
                    }
                }
            }
            if (candidates.Count == 0)
            {
                LogPvpFlankNone(name, meters, above, "nenhum lugar ao redor ve ele");
                return false;
            }
            // The route he sees least of; a spot hiding the chest with a
            // peek, then the shorter way, break the ties.
            var bestScore = float.MaxValue;
            var best = default(PvpFlankCandidate);
            var bestWay = 0f;
            var bestExposed = 1f;
            var routes = 0;
            foreach (var candidate in candidates)
            {
                if (routes >= PvpFlankMaxRoutes)
                {
                    break;
                }
                routes++;
                if (!SkaliaBotUnityNavmesh.TryFindPath(position, candidate.Spot, m_PvpFlankRoute)
                    || m_PvpFlankRoute.Count == 0)
                {
                    continue;
                }
                var exposed = PvpFlankRouteExposure(point, position, out var way);
                if (exposed >= PvpFlankMaxExposed)
                {
                    continue;
                }
                var score = exposed * 1000f + (candidate.Hidden ? 0f : 100f) + way * 0.1f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                    bestWay = way;
                    bestExposed = exposed;
                }
            }
            if (bestScore == float.MaxValue)
            {
                LogPvpFlankNone(name, meters, above, $"todas as {routes} voltas passam a vista dele");
                return false;
            }
            m_PvpHuntPoint = point;
            m_PvpHuntCoverDone = false;
            m_FleeingFromPlayerName = PvpHuntName;
            var seconds = Mathf.Clamp(bestWay / 5f + 5f, 8f, 90f);
            SetObjective(ObjectiveType.Flee, best.Spot, now + seconds);
            m_PvpHuntPhase = 2;
            m_PvpHuntUntil = now + seconds;
            BotLog(LogLevel.Information,
                "PvP: perdi {Name} a {Meters:0}m, {Above:0}m acima de mim e parado; deve continuar la. Dando a volta para o flanco: {Angle:0} graus ao redor dele, {Way:0}m de caminho ({Exposed:0}% a vista dele) ate {Spot}{Hidden}.",
                name,
                meters,
                above,
                best.Angle,
                bestWay,
                bestExposed * 100f,
                FormatPosition(best.Spot),
                best.Hidden ? ", escondido com espiada" : string.Empty);
            return true;
        }

        private float m_PvpFlankNoneLogAt = float.NegativeInfinity;

        private void LogPvpFlankNone(string name, float meters, float above, string why)
        {
            var now = Time.realtimeSinceStartup;
            if (now < m_PvpFlankNoneLogAt)
            {
                return;
            }
            m_PvpFlankNoneLogAt = now + 15f;
            BotLog(LogLevel.Information,
                "PvP: {Name} a {Meters:0}m e {Above:0}m acima de mim, parado; sem flanco ({Why}), sigo a procura de sempre.",
                name,
                meters,
                above,
                why);
        }

        private bool TryGetPvpFlankSpot(Vector3 desired, Vector3 position, out Vector3 spot)
        {
            spot = desired;
            var now = Time.realtimeSinceStartup;
            if (!TryGetWalkableGround(desired, out var grounded))
            {
                desired.y = LevelGround.getHeight(desired);
                if (!TryGetWalkableGround(desired, out grounded))
                {
                    return false;
                }
            }
            if (WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                || IsHazardousPosition(grounded, now, 1.5f)
                || IsInRadiation(grounded)
                || IsWithinMegaReach(grounded)
                || !SkaliaBotUnityNavmesh.TrySnap(grounded, 2f, out var onMesh)
                || (onMesh - grounded).GetHorizontalMagnitude() > 2f)
            {
                return false;
            }
            spot = onMesh;
            return true;
        }

        // The share of the route (a point every 6m) where his eye has the
        // bot's chest, as the watch's cover counts it.
        private float PvpFlankRouteExposure(Vector3 point, Vector3 start, out float way)
        {
            way = 0f;
            var samples = 0;
            var seen = 0;
            var previous = start;
            var carry = 0f;
            foreach (var corner in m_PvpFlankRoute)
            {
                var segment = corner - previous;
                var length = segment.magnitude;
                var at = PvpFlankRouteStep - carry;
                while (at <= length)
                {
                    var sample = previous + segment * (at / Mathf.Max(0.01f, length));
                    samples++;
                    if (IsChestInHisLine(point, sample))
                    {
                        seen++;
                    }
                    at += PvpFlankRouteStep;
                }
                carry = length - (at - PvpFlankRouteStep);
                way += length;
                previous = corner;
            }
            return samples == 0 ? 0f : (float)seen / samples;
        }
    }
}
