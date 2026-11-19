using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.172: one place that says when the bot is in danger and why, and the
    // places that ask it. In the 0.1.171 test (11-06, 17 deaths) 14 deaths
    // came in Flee or EscapeSwarm after long melee contact; in 7 of them the
    // bot had gone crouched after a "distracted" zombie 22-25m away at 40-60
    // health with 6-8 zombies around, Tadeu crouched with the Mega 5m away,
    // and Tadeu went for a knife 13m from where XXlloqiartyXX had died one
    // second before and died there 30s later.
    //
    // Being afraid of everything would be the next problem (no loot, no
    // healing), so: danger turns on at once and turns off only after 4s
    // without any reason, it never stops the bandage (only the standing
    // still for it), and every block is counted in the 5 min summary.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        internal static SkaliaBotBehaviorSettings s_Settings = new SkaliaBotBehaviorSettings();

        private const float RiskMegaMeters = 60f;
        private const float RiskCalmSeconds = 4f;
        private const float StealthIsolationMeters = 6f;
        private const float StealthChasedMeters = 15f;
        private const float LootHurryZombieMeters = 10f;
        private const float TreatmentStopAggroMeters = 12f;
        private const float FreshDeathMeters = 40f;
        private const float FreshDeathSeconds = 120f;
        private const float FreshDeathEscapePenalty = 12f;
        private const float LootBehindFurnitureSeconds = 3f;

        private struct FreshDeath
        {
            public Vector3 Position;
            public float At;
            public ulong SteamId;
            public string Name;
        }

        // Shared by every bot, like the danger zones.
        private static readonly List<FreshDeath> s_FreshDeaths = new List<FreshDeath>();

        private string m_RiskReason = string.Empty;
        private float m_RiskCalmSince = -1f;
        private bool m_RiskHurtCrowd;
        private float m_RiskLastUpdateAt;
        private float m_StatWindowRiskSeconds;
        private int m_StatWindowRiskOn;
        private readonly HashSet<Zombie> m_StatStealthBlocked = new HashSet<Zombie>();
        private int m_StatWindowEvictions;
        private int m_StatWindowLootBehindFurniture;
        private float m_NextFreshDeathCheckAt;
        private float m_NextFreshDeathLootLogAt;
        private float m_NextStealthDropLogAt;
        private float m_LootApproachSeenAt;
        private uint m_LootRouteTrackId;
        private float m_LootRouteBest;
        private float m_LootRouteImprovedAt;
        private float m_LootRouteSeenAt;
        private float m_EvictionDeathAt = float.NegativeInfinity;
        private int m_EvictionAttempts;

        private bool IsRiskHigh()
        {
            return m_RiskReason.Length > 0;
        }

        private bool IsMegaNear()
        {
            return m_CachedNearestBoss != null && m_CachedNearestBossDistance <= RiskMegaMeters;
        }

        private ulong OwnSteamId
        {
            get
            {
                var owner = m_Player != null ? m_Player.channel?.owner : null;
                return owner != null ? owner.playerID.steamID.m_SteamID : 0UL;
            }
        }

        private void UpdateRisk(Vector3 position, float now)
        {
            if (m_RiskReason.Length > 0 && m_RiskLastUpdateAt > 0f)
            {
                m_StatWindowRiskSeconds += Mathf.Clamp(now - m_RiskLastUpdateAt, 0f, 1f);
            }
            m_RiskLastUpdateAt = now;

            // Same numbers as caution, with the same slack to leave it.
            var fullMoon = LightingManager.isFullMoon;
            var health = m_Player.life.health;
            var healthLimit = (fullMoon ? FullMoonCautionHealth : HordeCautionHealth) + (m_RiskHurtCrowd ? 10f : 0f);
            var countLimit = (fullMoon ? FullMoonCautionZombies : HordeCautionZombies) - (m_RiskHurtCrowd ? 2 : 0);
            var count = CountPerceivedLivingZombies();
            m_RiskHurtCrowd = health < healthLimit && count >= countLimit;

            var reason = string.Empty;
            if (IsMegaNear())
            {
                reason = DescribeBoss(m_CachedNearestBoss!) + " a " + m_CachedNearestBossDistance.ToString("0") + "m";
            }
            else if (IsMegaLostAlert())
            {
                // 0.2.2x3: the Mega lost from sight is still close.
                reason = "o Mega sumiu de vista a " + (m_MegaLostPosition - position).GetHorizontalMagnitude().ToString("0") + "m";
            }
            else if (m_RiskHurtCrowd)
            {
                reason = "vida " + health + " com " + count + " zumbis";
            }
            else if (s_Settings.FreshDeathAvoidance
                     && TryGetFreshDeathNear(position, FreshDeathMeters, out var death, out var age))
            {
                reason = death.Name + " morreu a "
                    + (death.Position - position).GetHorizontalMagnitude().ToString("0") + "m ha "
                    + age.ToString("0") + "s";
            }

            if (reason.Length > 0)
            {
                if (m_RiskReason.Length == 0)
                {
                    m_StatWindowRiskOn++;
                    BotLog(LogLevel.Information,
                        "Risco alto ({Reason}): sem ir atras de zumbi distraido, sem agachar perto do Mega e sem parar para olhar loot.",
                        reason);
                }
                m_RiskReason = reason;
                m_RiskCalmSince = -1f;
                return;
            }
            if (m_RiskReason.Length == 0)
            {
                return;
            }
            if (m_RiskCalmSince < 0f)
            {
                m_RiskCalmSince = now;
                return;
            }
            if (now - m_RiskCalmSince >= RiskCalmSeconds)
            {
                BotLog(LogLevel.Information,
                    "Risco normal de novo ({Seconds:0}s sem perigo; era: {Reason}).",
                    now - m_RiskCalmSince,
                    m_RiskReason);
                m_RiskReason = string.Empty;
                m_RiskCalmSince = -1f;
            }
        }

        // A distracted zombie is worth sneaking up on only when it is near,
        // alone, nothing is after the bot, the Mega is not around and nobody
        // just died there. 22-25m crouched through a horde was 7 deaths.
        private bool IsStealthApproachSafe(Zombie zombie, Vector3 position, float distance)
        {
            if (distance > s_Settings.StealthApproachMeters)
            {
                return false;
            }
            var blocked = IsMegaNear()
                || GetNearestAggroedZombieDistance() <= StealthChasedMeters
                || CountPerceivedZombiesNear(zombie.transform.position, StealthIsolationMeters) > 1
                || (s_Settings.FreshDeathAvoidance
                    && TryGetFreshDeathNear(zombie.transform.position, FreshDeathMeters, out _, out _));
            if (blocked)
            {
                m_StatStealthBlocked.Add(zombie);
            }
            return !blocked;
        }

        // Keeping the target asks a little less than picking it, so a second
        // zombie wandering by does not flip it every scan.
        private bool IsStealthTargetStillSafe(Zombie zombie, out string reason)
        {
            reason = string.Empty;
            if (IsMegaNear())
            {
                reason = DescribeBoss(m_CachedNearestBoss!) + " a " + m_CachedNearestBossDistance.ToString("0") + "m";
                return false;
            }
            var around = CountPerceivedZombiesNear(zombie.transform.position, StealthIsolationMeters);
            if (around > 2)
            {
                reason = around + " zumbis juntos";
                return false;
            }
            return true;
        }

        private void DropUnsafeStealthTarget(Vector3 position, float now)
        {
            if (!IsTargetZombieAlive()
                || !IsHordeCaution()
                || m_TargetZombie!.isHunting
                || IsStealthTargetStillSafe(m_TargetZombie!, out var reason))
            {
                return;
            }
            m_StatStealthBlocked.Add(m_TargetZombie!);
            if (now >= m_NextStealthDropLogAt)
            {
                m_NextStealthDropLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "Cautela: desistindo do zumbi distraido a {Distance:0.#}m ({Reason}).",
                    (m_TargetZombie!.transform.position - position).GetHorizontalMagnitude(),
                    reason);
            }
            m_TargetZombie = null;
        }

        // Near zombies, the Mega or a fresh death: take the item the moment
        // it is in reach, no half-second look and no pause after it.
        private bool IsLootHurried()
        {
            return !s_Settings.LootLook
                || (s_Settings.LootLookFurnitureOnly
                    && TryGetTargetLoot(out var hurriedLoot)
                    && !IsLootLookWanted(hurriedLoot))
                || IsRiskHigh()
                || GetNearestAggroedZombieDistance() <= LootHurryZombieMeters;
        }

        // The bandage goes on while walking; standing still for it waits
        // until nothing is coming for the bot within 12m.
        private bool IsTreatmentStopSafe()
        {
            return GetNearestAggroedZombieDistance() > TreatmentStopAggroMeters;
        }

        // ---- Where a bot died a moment ago ----

        private void RecordFreshDeath(Vector3 deathPosition, float realNow)
        {
            s_FreshDeaths.RemoveAll(death => realNow - death.At > FreshDeathSeconds);
            s_FreshDeaths.Add(new FreshDeath
            {
                Position = deathPosition,
                At = realNow,
                SteamId = OwnSteamId,
                Name = BotName
            });
        }

        // Another bot's death within radius in the last 2 min. Its own death
        // does not count: the way back for the items has its own checks.
        private bool TryGetFreshDeathNear(Vector3 point, float radius, out FreshDeath found, out float age)
        {
            found = default;
            age = 0f;
            if (s_FreshDeaths.Count == 0)
            {
                return false;
            }
            var realNow = Time.realtimeSinceStartup;
            var own = OwnSteamId;
            var best = float.PositiveInfinity;
            foreach (var death in s_FreshDeaths)
            {
                if (death.SteamId == own || realNow - death.At > FreshDeathSeconds)
                {
                    continue;
                }
                var distance = (death.Position - point).GetHorizontalMagnitude();
                if (distance <= radius && distance < best)
                {
                    best = distance;
                    found = death;
                    age = realNow - death.At;
                }
            }
            return best < float.PositiveInfinity;
        }

        private bool IsNearFreshDeath(Vector3 point, float radius)
        {
            return s_Settings.FreshDeathAvoidance && TryGetFreshDeathNear(point, radius, out _, out _);
        }

        // Loot within 40m of where another bot just died waits until the
        // 2 min are over (the zombies that killed it are still there).
        private bool IsLootInFreshDeathZone(ItemData item, float now)
        {
            if (!s_Settings.FreshDeathAvoidance
                || !TryGetFreshDeathNear(item.point, FreshDeathMeters, out var death, out var age))
            {
                return false;
            }
            IgnoreLoot(item.instanceID, now + Mathf.Max(5f, FreshDeathSeconds - age));
            if (now >= m_NextFreshDeathLootLogAt)
            {
                m_NextFreshDeathLootLogAt = now + 15f;
                BotLog(LogLevel.Information,
                    "Loot {LootInstanceId} fica para depois: a {Distance:0}m de onde {Name} morreu ha {Age:0}s.",
                    item.instanceID,
                    (item.point - death.Position).GetHorizontalMagnitude(),
                    death.Name,
                    age);
            }
            return true;
        }

        // Inside 40m of another bot's death in the last 2 min, with nothing
        // on the bot: leave toward the side it is on, 55m from the spot, on
        // dry ground. Fights, escapes, doors, ladders and trips go on; the
        // check comes back every second, so it leaves when they end.
        private void UpdateFreshDeathEviction(Vector3 position, float now)
        {
            if (!s_Settings.FreshDeathAvoidance || now < m_NextFreshDeathCheckAt)
            {
                return;
            }
            m_NextFreshDeathCheckAt = now + 1f;
            if (!TryGetFreshDeathNear(position, FreshDeathMeters, out var death, out var age))
            {
                return;
            }
            if (m_ObjectiveType != ObjectiveType.None
                && m_ObjectiveType != ObjectiveType.Explore
                && m_ObjectiveType != ObjectiveType.Patrol
                && m_ObjectiveType != ObjectiveType.Loot)
            {
                return;
            }
            if (m_CachedNearestZombieDistance <= 3f || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return;
            }
            var sameDeath = Mathf.Approximately(death.At, m_EvictionDeathAt);
            if (sameDeath && m_EvictionAttempts >= 3)
            {
                return;
            }
            // Already on the way out.
            if (m_ObjectiveType == ObjectiveType.Explore
                && (m_ObjectivePosition - death.Position).GetHorizontalMagnitude() > FreshDeathMeters)
            {
                return;
            }
            var away = position - death.Position;
            away.y = 0f;
            if (away.sqrMagnitude < 1f)
            {
                away = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
                away.y = 0f;
            }
            away.Normalize();
            var found = false;
            var exit = position;
            for (var i = 0; i < 9 && !found; i++)
            {
                var angle = i == 0 ? 0f : ((i + 1) / 2) * 30f * (i % 2 == 1 ? 1f : -1f);
                var direction = Quaternion.Euler(0f, angle, 0f) * away;
                var candidate = FindNearestNavigablePoint(death.Position + direction * (FreshDeathMeters + 15f));
                if ((candidate - death.Position).GetHorizontalMagnitude() < FreshDeathMeters
                    || Mathf.Abs(candidate.y - position.y) > 8f
                    || WaterUtility.isPointUnderwater(candidate + Vector3.up * 0.5f)
                    || CrossesWater(position, candidate)
                    || IsHazardousPosition(candidate, now, 2f)
                    || TryGetFreshDeathNear(candidate, FreshDeathMeters, out _, out _))
                {
                    continue;
                }
                exit = candidate;
                found = true;
            }
            if (!found)
            {
                m_NextFreshDeathCheckAt = now + 10f;
                BotLog(LogLevel.Debug,
                    "Sem saida seca a 55m de onde {Name} morreu; tentando de novo em 10s.",
                    death.Name);
                return;
            }
            // On the way back for its own items: that waits (the same as the
            // check that interrupts it), the items are not given up.
            if (m_DeathRecoveryActive && m_InterestAreaName == "recuperar itens da morte")
            {
                m_DeathRecoveryActive = false;
                m_HasInterestArea = false;
                m_NextDeathRecoveryLogAt = now + 30f;
            }
            // The area it was searching is where the zombies are: done with
            // it for now, like an unarmed bot that sees a horde.
            else if (m_HasInterestArea
                     && (m_InterestAreaCenter - death.Position).GetHorizontalMagnitude() <= InterestAreaRadiusMeters
                     && !TryInsistOnArea(death.Name + " morreu ali perto", now))
            {
                FinishInterestArea(now);
            }
            ClearObjective();
            SetObjective(ObjectiveType.Explore, exit, now + 30f);
            m_StatWindowEvictions++;
            m_EvictionAttempts = sameDeath ? m_EvictionAttempts + 1 : 1;
            m_EvictionDeathAt = death.At;
            m_NextFreshDeathCheckAt = now + 10f;
            BotLog(LogLevel.Information,
                "{Name} morreu a {Distance:0}m daqui ha {Age:0}s; saindo para {Exit} ({Away:0}m do lugar) em vez de ficar ali.",
                death.Name,
                (death.Position - position).GetHorizontalMagnitude(),
                age,
                FormatPosition(exit),
                (exit - death.Position).GetHorizontalMagnitude());
        }

        // ---- Loot behind a counter ----

        // The 11-06 log: a granola bar 3.7m away behind a counter, the route
        // ending by it, and the bot circling for 8s until the circle check
        // and then the no-sight check gave up on it. In reach, out of sight,
        // the route nearly over and not 0.2m closer in 3s: give up at once.
        // A long route left (around a wall, through a door) is still walked.
        private bool TryGiveUpLootBehindFurniture(Vector3 position, float now, Vector3 lootPoint, float distance, string itemName)
        {
            if (m_LootApproachId != m_LootInstanceId
                || distance > LootSightReachMeters + 0.5f
                || Mathf.Abs(lootPoint.y - position.y) > 2.2f)
            {
                m_LootRouteTrackId = 0;
                return false;
            }
            var remaining = GetRemainingRouteMeters(position, 12f);
            if (m_LootRouteTrackId != m_LootInstanceId || now - m_LootRouteSeenAt > 1f)
            {
                m_LootRouteTrackId = m_LootInstanceId;
                m_LootRouteBest = remaining;
                m_LootRouteImprovedAt = now;
            }
            else if (remaining < m_LootRouteBest - 0.5f)
            {
                m_LootRouteBest = remaining;
                m_LootRouteImprovedAt = now;
            }
            m_LootRouteSeenAt = now;
            if (now - m_LootApproachImprovedAt < LootBehindFurnitureSeconds
                || now - m_LootRouteImprovedAt < LootBehindFurnitureSeconds
                || now - m_LootApproachSeenAt > 0.5f
                || remaining > distance + 4f
                || HasLootLineOfSight(lootPoint))
            {
                return false;
            }
            IgnoreLoot(m_LootInstanceId, now + UnreachableLootIgnoreSeconds);
            m_StatWindowLootBehindFurniture++;
            BotLog(LogLevel.Information,
                "Bot desistiu de {Item} a {Distance:0.#}m: atras de movel ou parede, sem visao e sem chegar mais perto em {Seconds:0.#}s; ignorado por 10 min.",
                itemName,
                distance,
                now - m_LootApproachImprovedAt);
            ClearObjective();
            m_NextLootScanTime = now + 0.75f;
            return true;
        }

        private float GetRemainingRouteMeters(Vector3 position, float cap)
        {
            if (m_PathPoints.Count == 0 || m_PathIndex >= m_PathPoints.Count)
            {
                return 0f;
            }
            var total = 0f;
            var from = position;
            for (var i = Mathf.Max(0, m_PathIndex); i < m_PathPoints.Count && total <= cap; i++)
            {
                total += (m_PathPoints[i] - from).GetHorizontalMagnitude();
                from = m_PathPoints[i];
            }
            return total;
        }

        // ---- 5 min summary ----

        private int GetRiskPercent(float now)
        {
            var window = now - m_ActivityWindowStart;
            return window > 1f ? Mathf.RoundToInt(100f * Mathf.Clamp01(m_StatWindowRiskSeconds / window)) : 0;
        }

        private void ResetRiskStats()
        {
            m_StatWindowRiskSeconds = 0f;
            m_StatWindowRiskOn = 0;
            m_StatStealthBlocked.Clear();
            m_StatWindowEvictions = 0;
            m_StatWindowLootBehindFurniture = 0;
            LogAndResetDecisionStats();
        }
    }
}
