using Microsoft.Extensions.Logging;
using UnityEngine;
using SDG.Unturned;

namespace SkaliaDeps
{
    // 0.2.2x13 (the tester): "onde tem loot bom eles nao podem abrir mao com
    // tanta facilidade". Not "where the Mega is": on Washington every Mega
    // stands on good loot, on other maps a Mega can stand on nothing. What
    // counts is the loot of the place, measured on the map itself: the
    // strong spots (SkaliaBotTryhardMap) carry a value, the sum of their
    // gun-table points. A place is worth insisting on when it has at least
    // a quarter of the best spot's value, or when it is small but as rich
    // per point as the best (a crashed helicopter). There a burner, a long
    // escape from a swarm or a body nearby does not end the visit: the bot
    // deals with it and goes on, up to 3 times per visit; and if it still
    // leaves with much of the place unseen, it may come back in 5 min
    // instead of 30.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float WorthSpotMeters = 90f;
        private const float WorthShareOfBest = 0.25f;
        private const float WorthRichShare = 0.9f;
        private const int WorthRichMinPoints = 10;
        private const int WorthSetbacksPerVisit = 3;
        private const float WorthComeBackSeconds = 300f;

        private string m_WorthSetbackKey = string.Empty;
        private int m_WorthSetbacks;

        private static bool TryGetLootWorth(Vector3 center, out StrongSpot? spot, out float best, out bool high)
        {
            spot = null;
            best = 0f;
            high = false;
            EnsureStrongSpots();
            var spots = s_StrongSpots;
            if (spots == null || spots.Count == 0)
            {
                return false;
            }
            var bestRich = 0f;
            var nearest = WorthSpotMeters;
            foreach (var candidate in spots)
            {
                best = Mathf.Max(best, candidate.Value);
                if (candidate.Points >= WorthRichMinPoints)
                {
                    bestRich = Mathf.Max(bestRich, candidate.Value / candidate.Points);
                }
                var gap = (candidate.Center - center).GetHorizontalMagnitude();
                if (gap < nearest)
                {
                    nearest = gap;
                    spot = candidate;
                }
            }
            if (spot == null || best <= 0f)
            {
                return false;
            }
            high = spot.Value >= best * WorthShareOfBest
                || (spot.Points >= WorthRichMinPoints
                    && bestRich > 0f
                    && spot.Value / spot.Points >= bestRich * WorthRichShare);
            return true;
        }

        private bool IsHighWorthArea(out StrongSpot? spot, out float best)
        {
            spot = null;
            best = 0f;
            return Tryhard
                && m_HasInterestArea
                && TryGetLootWorth(m_InterestAreaCenter, out spot, out best, out var high)
                && high;
        }

        // At a place where the bot was about to leave for danger: true when
        // it stays.
        private bool TryInsistOnArea(string why, float now)
        {
            if (!IsHighWorthArea(out var spot, out var best))
            {
                return false;
            }
            if (m_WorthSetbackKey != m_InterestAreaKey)
            {
                m_WorthSetbackKey = m_InterestAreaKey;
                m_WorthSetbacks = 0;
            }
            if (m_Player.life.health < 40 || !HasFightingWeapon())
            {
                BotLog(LogLevel.Information,
                    "Area {Area} tem loot bom (nota {Value:0.0}, a melhor do mapa {Best:0.0}), mas {Why} com vida {Health} e {Weapon}: saindo.",
                    m_InterestAreaName,
                    spot!.Value,
                    best,
                    why,
                    m_Player.life.health,
                    DescribeEquipped());
                return false;
            }
            m_WorthSetbacks++;
            if (m_WorthSetbacks > WorthSetbacksPerVisit)
            {
                BotLog(LogLevel.Information,
                    "Area {Area} tem loot bom (nota {Value:0.0}), mas {Why} pela {Count}a vez nesta visita: saindo e voltando depois.",
                    m_InterestAreaName,
                    spot!.Value,
                    why,
                    m_WorthSetbacks);
                return false;
            }
            // The time lost to the danger is given back to the visit.
            m_InterestAreaExpiresAt = Mathf.Max(m_InterestAreaExpiresAt, now + 240f);
            BotLog(LogLevel.Information,
                "Area {Area} tem loot bom (nota {Value:0.0}, a melhor do mapa {Best:0.0}): {Why}, mas nao desiste dela ({Count} de {Max} nesta visita); resolve e continua.",
                m_InterestAreaName,
                spot!.Value,
                best,
                why,
                m_WorthSetbacks,
                WorthSetbacksPerVisit);
            return true;
        }

        // How long a place just left stays off the list: a rich place left
        // with a third or more of it unseen comes back in 5 min.
        private bool TryShortRestForWorth(float now, out float seconds)
        {
            seconds = 0f;
            if (!IsHighWorthArea(out var spot, out _) || m_AreaLootPoints.Count == 0)
            {
                return false;
            }
            var left = CountUnvisitedAreaPoints(now);
            if (left * 3 < m_AreaLootPoints.Count)
            {
                return false;
            }
            seconds = WorthComeBackSeconds;
            BotLog(LogLevel.Information,
                "Area {Area} tem loot bom (nota {Value:0.0}) e ficaram {Left} de {Count} pontos sem ver: volta a ela em {Minutes:0} min.",
                m_InterestAreaName,
                spot!.Value,
                left,
                m_AreaLootPoints.Count,
                seconds / 60f);
            return true;
        }
    }
}
