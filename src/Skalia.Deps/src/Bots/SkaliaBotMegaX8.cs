using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x8 (doc 16.16): the Mega deaths of the x7 test.
    //
    // The route: the objective itself was checked against the Mega
    // (IsWithinMegaReach), the way to it was not. Patrol, travel and explore
    // routes walked the bots from 30m to 10m of a Mega that had not seen them,
    // and the flee started with it already on them. Now the next 40m of the
    // route are checked every half second: passing within 15m of a Mega that
    // is within 40m, the bot stops crouched (like a player letting it walk
    // by) for up to 20s while it is more than 20m away, and otherwise drops
    // that objective (a loot or patrol point is left out for 2 min).
    //
    // The flee: only a Mega after the bot or within 10m was run from. One
    // walking at the bot (6m closer in 3s, within 25m) is run from now,
    // before it is on top.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float MegaRouteLookMeters = 40f;
        // 0.2.2x13: 9m, no wait. llo sat crouched 20s at Olympia for a Mega
        // whose path crossed his, then gave the trip up anyway; the players
        // walk past a Mega at 7-16m and go round it.
        private const float MegaRouteNearMeters = 9f;
        private const float MegaRouteBossMeters = 40f;
        private const float MegaRouteHoldMinMeters = 20f;
        private const float MegaRouteHoldSeconds = 20f;
        private const float MegaRouteAvoidSeconds = 60f;
        private const float MegaRouteAvoidMeters = 6f;
        private const float MegaClosingMeters = 6f;
        private const float MegaClosingWindowSeconds = 3f;
        private const float MegaClosingRangeMeters = 25f;

        private float m_NextMegaRouteCheckAt;
        private bool m_MegaRouteHolding;
        private float m_MegaRouteHoldSince;
        private readonly List<KeyValuePair<Vector3, float>> m_MegaRouteAvoid = new List<KeyValuePair<Vector3, float>>();

        private struct MegaSample
        {
            public float Time;
            public Vector3 MegaPosition;
        }

        private readonly Queue<MegaSample> m_MegaSamples = new Queue<MegaSample>();
        private Zombie? m_MegaSampleBoss;
        private float m_NextMegaSampleAt;
        private float m_MegaClosingUntil;

        // Called every tick after UpdateMegaSneak.
        private void UpdateMegaRouteGuard(Vector3 position, float now)
        {
            SampleMegaApproach(position, now);
            if (m_MegaRouteHolding)
            {
                m_MegaSneakCrouch = true;
            }
            if (now < m_NextMegaRouteCheckAt)
            {
                return;
            }
            m_NextMegaRouteCheckAt = now + 0.5f;
            var boss = m_CachedNearestBoss;
            var distance = m_CachedNearestBossDistance;
            if (!Tryhard
                || boss == null
                || boss.isDead
                || m_BossHuntActive
                || m_BossFleeActive
                || IsHuntedBoss(boss)
                || IsBossAfterBot(boss)
                || distance > MegaRouteBossMeters
                || !IsMegaRouteGuarded(m_ObjectiveType)
                || GetNearestAggroedZombieDistance() < 12f)
            {
                EndMegaRouteHold(null);
                return;
            }
            var megaPosition = boss.transform.position;
            var closest = GetRouteClosestApproach(position, megaPosition);
            // Already close and the route leads away: nothing to wait for.
            if (closest >= MegaRouteNearMeters || closest >= distance - 2f)
            {
                EndMegaRouteHold(boss);
                return;
            }
            if (!Tryhard
                && distance > MegaRouteHoldMinMeters
                && (!m_MegaRouteHolding || now - m_MegaRouteHoldSince < MegaRouteHoldSeconds))
            {
                if (!m_MegaRouteHolding)
                {
                    m_MegaRouteHolding = true;
                    m_MegaRouteHoldSince = now;
                    m_MegaSneakCrouch = true;
                    BotLog(LogLevel.Information,
                        "Rota de {Objective} passa a {Closest:0}m de {Boss} (a {Distance:0}m, sem ter visto o bot): parado agachado ate ele sair do caminho (max {Max:0}s).",
                        m_ObjectiveType,
                        closest,
                        DescribeBoss(boss),
                        distance,
                        MegaRouteHoldSeconds);
                }
                return;
            }
            var objective = m_ObjectiveType;
            var target = m_ObjectivePosition;
            if (objective == ObjectiveType.Loot || objective == ObjectiveType.Patrol)
            {
                m_MegaRouteAvoid.Add(new KeyValuePair<Vector3, float>(target, now + MegaRouteAvoidSeconds));
            }
            // 0.2.2x13: the tryhard keeps the area (Olympia, Seattle) and only
            // stops walking to that point past the Mega for a minute.
            if (objective == ObjectiveType.Explore)
            {
                if (Tryhard)
                {
                    m_MegaRouteAvoid.Add(new KeyValuePair<Vector3, float>(target, now + MegaRouteAvoidSeconds));
                }
                else
                {
                    m_HasInterestArea = false;
                }
            }
            // A trip would ask for the same waypoint past the same Mega.
            if (objective == ObjectiveType.Travel)
            {
                m_TravelActive = false;
                m_TravelPhase = 0;
                m_TravelMeshArea = false;
            }
            BotLog(LogLevel.Information,
                "Desistindo de {Objective} em {Position}: a rota passa a {Closest:0}m de {Boss} (a {Distance:0}m){Why}.",
                objective,
                FormatPosition(target),
                closest,
                DescribeBoss(boss),
                distance,
                m_MegaRouteHolding ? ", esperou " + (now - m_MegaRouteHoldSince).ToString("0") + "s e ele nao saiu" : "");
            m_MegaRouteHolding = false;
            ClearObjective();
        }

        private static bool IsMegaRouteGuarded(ObjectiveType type)
        {
            return type == ObjectiveType.Explore
                || type == ObjectiveType.Patrol
                || type == ObjectiveType.Loot
                || type == ObjectiveType.Travel;
        }

        private void EndMegaRouteHold(Zombie? boss)
        {
            if (!m_MegaRouteHolding)
            {
                return;
            }
            m_MegaRouteHolding = false;
            BotLog(LogLevel.Information,
                "Fim da espera pelo Mega apos {Seconds:0}s: {Why}.",
                Time.realtimeSinceStartup - m_MegaRouteHoldSince,
                boss != null ? "a rota ja nao passa perto dele" : "a situacao mudou (objetivo " + m_ObjectiveType + ")");
        }

        // Standing still in UpdateMovementIntent while the Mega walks by.
        private bool IsMegaRouteHolding()
        {
            return m_MegaRouteHolding
                && !m_BossFleeActive
                && IsMegaRouteGuarded(m_ObjectiveType);
        }

        // The nearest the next 40m of the route (or the straight line to the
        // objective while no route is in hand) come to the Mega.
        private float GetRouteClosestApproach(Vector3 position, Vector3 megaPosition)
        {
            var closest = float.PositiveInfinity;
            var from = position;
            var walked = 0f;
            if (m_PathIndex < m_PathPoints.Count)
            {
                for (var i = m_PathIndex; i < m_PathPoints.Count && walked < MegaRouteLookMeters; i++)
                {
                    var to = m_PathPoints[i];
                    var length = (to - from).GetHorizontalMagnitude();
                    if (walked + length > MegaRouteLookMeters && length > 0.01f)
                    {
                        to = from + (to - from) * ((MegaRouteLookMeters - walked) / length);
                        length = MegaRouteLookMeters - walked;
                    }
                    closest = Mathf.Min(closest, DistanceToSegment2D(megaPosition, from, to));
                    walked += length;
                    from = to;
                }
                return closest;
            }
            var end = m_ObjectivePosition;
            var straight = (end - position).GetHorizontalMagnitude();
            if (straight > MegaRouteLookMeters)
            {
                end = position + (end - position) * (MegaRouteLookMeters / straight);
            }
            return DistanceToSegment2D(megaPosition, position, end);
        }

        // Loot and patrol points dropped for a route past the Mega.
        private bool IsMegaRouteAvoided(Vector3 point)
        {
            if (m_MegaRouteAvoid.Count == 0)
            {
                return false;
            }
            var now = Time.realtimeSinceStartup;
            m_MegaRouteAvoid.RemoveAll(entry => entry.Value <= now);
            foreach (var entry in m_MegaRouteAvoid)
            {
                if ((entry.Key - point).GetHorizontalMagnitude() < MegaRouteAvoidMeters)
                {
                    return true;
                }
            }
            return false;
        }

        // How far the Mega itself walked toward the bot in the last 3s. The
        // bot's own steps don't count: walking past a still Mega is not it
        // coming.
        private void SampleMegaApproach(Vector3 position, float now)
        {
            if (now < m_NextMegaSampleAt)
            {
                return;
            }
            m_NextMegaSampleAt = now + 0.5f;
            var boss = m_CachedNearestBoss;
            if (boss == null || boss.isDead || m_BossHuntActive)
            {
                m_MegaSamples.Clear();
                m_MegaSampleBoss = null;
                return;
            }
            if (boss != m_MegaSampleBoss)
            {
                m_MegaSamples.Clear();
                m_MegaSampleBoss = boss;
            }
            var megaPosition = boss.transform.position;
            m_MegaSamples.Enqueue(new MegaSample { Time = now, MegaPosition = megaPosition });
            while (m_MegaSamples.Count > 0 && now - m_MegaSamples.Peek().Time > MegaClosingWindowSeconds + 0.05f)
            {
                m_MegaSamples.Dequeue();
            }
            var distance = m_CachedNearestBossDistance;
            if (m_MegaSamples.Count < 2 || distance > MegaClosingRangeMeters)
            {
                return;
            }
            var oldest = m_MegaSamples.Peek();
            if (now - oldest.Time < MegaClosingWindowSeconds - 0.6f)
            {
                return;
            }
            var toBot = position - oldest.MegaPosition;
            toBot.y = 0f;
            if (toBot.sqrMagnitude < 0.01f)
            {
                return;
            }
            var moved = megaPosition - oldest.MegaPosition;
            moved.y = 0f;
            var closing = Vector3.Dot(moved, toBot.normalized);
            if (closing < MegaClosingMeters)
            {
                return;
            }
            if (now >= m_MegaClosingUntil)
            {
                BotLog(LogLevel.Information,
                    "{Boss} veio {Closing:0.#}m na direcao do bot em {Seconds:0.#}s e esta a {Distance:0}m: fugindo antes dele chegar.",
                    DescribeBoss(boss),
                    closing,
                    now - oldest.Time,
                    distance);
            }
            m_MegaClosingUntil = now + 3f;
        }

        private bool IsMegaClosing(Zombie boss)
        {
            return boss == m_MegaSampleBoss && Time.realtimeSinceStartup < m_MegaClosingUntil;
        }
    }
}
