using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.173: the base the decisions read, and the fixes built on it.
    //
    // In the 0.1.172 night test (11-06, 16 deaths in 3.8h) every behavior
    // measured danger with its own radius (4, 6, 8, 10, 12, 15, 20, 25m) and
    // the decisions contradicted each other: loot started with the hunter at
    // 25m and was cancelled at 15m (117 of 126 cancels within 6s), the swarm
    // escape ended with 7-14 zombies still seen and the bot walked back into
    // them (25 of 76 escapes restarted within 30s), and the burner flee ended
    // with the burner still running after the bot.
    //
    // One snapshot every 0.25s (hunters at 8/20m, how soon the nearest one
    // arrives, the burner, marked zombies included), a 20s window after any
    // escape, a memory of where hordes were, and threat responses that may
    // interrupt routine at any time.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float FireFleeHuntingMeters = 30f;
        private const float FireFleeHuntingAgainMeters = 22f;
        private const float FireFleeAnyMeters = 12f;
        private const float FireFleeEndMeters = 40f;
        private const float FireFleeEndNotHuntingMeters = 25f;
        private const float FireFleeEndNoSightMeters = 30f;
        private const float FireFleeStillSampleSeconds = 6f;
        private const float FireFleeLeaveAreaSeconds = 45f;
        private const float FireShootMinMeters = 10f;
        private const byte FireLastResortHealth = 55;
        private const byte FireLastResortStamina = 5;
        private const byte FireWeaponRunHealth = 50;
        private const byte FireWaterRefugeStamina = 20;
        private const float FireWaterRefugeBurnerMeters = 15f;
        private const float FireWaterRefugeSearchMeters = 25f;
        private const float FireWaterRefugeSeconds = 30f;
        private const string FireWaterFleeName = "agua (zumbi de fogo)";
        private const string AcidFleeName = "poca de acido";

        private const float DisengageSeconds = 20f;
        private const float DisengageEngageMeters = 8f;
        private const float DisengageIsolationMeters = 6f;
        private const float DisengageFireTargetMeters = 20f;
        private const float DisengageAvoidMeters = 20f;

        private const float HordeRadiusMeters = 10f;
        private const int HordeCount = 4;
        private const float HordeShootMeters = 12f;
        private const float HordeMemorySeconds = 120f;
        private const float HordeAvoidMeters = 15f;

        private const float SwarmMaximumSeconds = 60f;
        private const byte SwarmPureRunHealth = 50;
        private const int SwarmPureRunHunters = 4;

        private const float LootRaceMarginSeconds = 2f;
        private const float LootRaceIdleZombieMeters = 8f;
        private const float PendingLootSeconds = 60f;

        private const float AcidLootIgnoreSeconds = 20f;
        private const float SpitReactionDelaySeconds = 1.5f;

        // ---- threat snapshot ----

        // Zombies marked "no progress" or "no route" are left alone as targets,
        // but they are still there: they count as threats (0.1.172 removed them
        // from perception entirely until they were within 3m).
        private readonly Dictionary<Zombie, float> m_MarkedSeenZombies = new Dictionary<Zombie, float>();
        private readonly List<Zombie> m_ThreatRemoval = new List<Zombie>();
        private int m_ThreatHunters8;
        private int m_ThreatHunters20;
        private float m_ThreatNearestHunterDistance = float.PositiveInfinity;
        private float m_ThreatNearestHunterEta = float.PositiveInfinity;
        private float m_ThreatFireDistance = float.PositiveInfinity;
        private float m_ThreatHuntingFireDistance = float.PositiveInfinity;

        private bool IsHuntingBot(Zombie zombie)
        {
            return zombie.isHunting && zombie.player == m_Player;
        }

        private static float GetZombieSpeed(Zombie zombie)
        {
            switch (zombie.speciality)
            {
                case EZombieSpeciality.CRAWLER:
                    return 3f;
                case EZombieSpeciality.SPRINTER:
                    return 6.5f;
                case EZombieSpeciality.FLANKER_FRIENDLY:
                case EZombieSpeciality.FLANKER_STALK:
                case EZombieSpeciality.MEGA:
                    return 6f;
                default:
                    return zombie.isBoss ? 6f : 5.5f;
            }
        }

        private void NoteMarkedZombieSeen(Zombie zombie, float now)
        {
            m_MarkedSeenZombies[zombie] = now + ZombieMemorySeconds;
        }

        private void RefreshThreatSnapshot(Vector3 position, float now)
        {
            m_ThreatRemoval.Clear();
            foreach (var entry in m_MarkedSeenZombies)
            {
                var zombie = entry.Key;
                if (zombie == null
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy
                    || now >= entry.Value
                    || m_PerceivedZombies.ContainsKey(zombie))
                {
                    m_ThreatRemoval.Add(zombie!);
                }
            }
            foreach (var expired in m_ThreatRemoval)
            {
                m_MarkedSeenZombies.Remove(expired);
            }

            m_ThreatHunters8 = 0;
            m_ThreatHunters20 = 0;
            m_ThreatNearestHunterDistance = float.PositiveInfinity;
            m_ThreatNearestHunterEta = float.PositiveInfinity;
            m_ThreatFireDistance = float.PositiveInfinity;
            m_ThreatHuntingFireDistance = float.PositiveInfinity;
            foreach (var entry in m_PerceivedZombies)
            {
                ConsiderThreat(entry.Key, position);
            }
            foreach (var entry in m_MarkedSeenZombies)
            {
                ConsiderThreat(entry.Key, position);
            }
            if (m_ThreatFireDistance < m_CachedNearestFireZombieDistance)
            {
                m_CachedNearestFireZombieDistance = m_ThreatFireDistance;
            }
            UpdateHordeMemory(position, now);
            UpdateTrail(position, now);
        }

        private void ConsiderThreat(Zombie zombie, Vector3 position)
        {
            if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
            {
                return;
            }
            var offset = zombie.transform.position - position;
            if (Mathf.Abs(offset.y) > 8f)
            {
                return;
            }
            var distance = offset.GetHorizontalMagnitude();
            var hunting = IsHuntingBot(zombie);
            if (IsFireZombie(zombie))
            {
                m_ThreatFireDistance = Mathf.Min(m_ThreatFireDistance, distance);
                if (hunting)
                {
                    m_ThreatHuntingFireDistance = Mathf.Min(m_ThreatHuntingFireDistance, distance);
                }
            }
            // A zombie within 6m counts whoever it is after.
            if (!hunting && distance > 6f)
            {
                return;
            }
            if (distance <= 8f)
            {
                m_ThreatHunters8++;
            }
            if (distance <= 20f)
            {
                m_ThreatHunters20++;
            }
            if (hunting)
            {
                m_ThreatNearestHunterDistance = Mathf.Min(m_ThreatNearestHunterDistance, distance);
                m_ThreatNearestHunterEta = Mathf.Min(m_ThreatNearestHunterEta, distance / GetZombieSpeed(zombie));
            }
        }

        // The burner, marked or not: a mark only stops the bot attacking it.
        private bool TryFindFireThreat(Vector3 position, float radius, out Zombie fire)
        {
            fire = null!;
            var best = radius;
            foreach (var entry in m_PerceivedZombies)
            {
                ConsiderFireThreat(entry.Key, position, ref best, ref fire);
            }
            foreach (var entry in m_MarkedSeenZombies)
            {
                ConsiderFireThreat(entry.Key, position, ref best, ref fire);
            }
            return fire != null;
        }

        private static void ConsiderFireThreat(Zombie candidate, Vector3 position, ref float best, ref Zombie fire)
        {
            if (candidate == null
                || candidate.isDead
                || !candidate.gameObject.activeInHierarchy
                || !IsFireZombie(candidate))
            {
                return;
            }
            var offset = candidate.transform.position - position;
            if (Mathf.Abs(offset.y) > 12f)
            {
                return;
            }
            var distance = offset.GetHorizontalMagnitude();
            if (distance <= best)
            {
                best = distance;
                fire = candidate;
            }
        }

        private int CountZombiesNear(Vector3 point, float radius, Zombie? except = null, bool normalOnly = false)
        {
            var count = 0;
            foreach (var entry in m_PerceivedZombies)
            {
                if (IsCountedNear(entry.Key, point, radius, except, normalOnly))
                {
                    count++;
                }
            }
            foreach (var entry in m_MarkedSeenZombies)
            {
                if (IsCountedNear(entry.Key, point, radius, except, normalOnly))
                {
                    count++;
                }
            }
            return count;
        }

        private static bool IsCountedNear(Zombie zombie, Vector3 point, float radius, Zombie? except, bool normalOnly)
        {
            return zombie != null
                && zombie != except
                && !zombie.isDead
                && zombie.gameObject.activeInHierarchy
                && !(normalOnly && (IsFireZombie(zombie) || IsBossZombie(zombie)))
                && Mathf.Abs(zombie.transform.position.y - point.y) <= 4f
                && (zombie.transform.position - point).GetHorizontalMagnitude() <= radius;
        }

        private Vector3 GetThreatCentroid(Vector3 position, float radius)
        {
            var sum = Vector3.zero;
            var count = 0;
            foreach (var entry in m_PerceivedZombies)
            {
                var zombie = entry.Key;
                if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var distance = (zombie.transform.position - position).GetHorizontalMagnitude();
                if (distance <= radius && (IsHuntingBot(zombie) || distance <= 6f))
                {
                    sum += zombie.transform.position;
                    count++;
                }
            }
            return count > 0 ? sum / count : position;
        }

        // ---- horde memory ----

        private struct HordeSpot
        {
            public Vector3 Position;
            public float Until;
            public int Count;
        }

        private readonly List<HordeSpot> m_HordeSpots = new List<HordeSpot>();
        private readonly List<Vector3> m_HordeScratch = new List<Vector3>();
        private float m_NextHordeScanAt;

        private void UpdateHordeMemory(Vector3 position, float now)
        {
            if (now < m_NextHordeScanAt)
            {
                return;
            }
            m_NextHordeScanAt = now + 1f;
            m_HordeScratch.Clear();
            foreach (var entry in m_PerceivedZombies)
            {
                var zombie = entry.Key;
                if (zombie != null && !zombie.isDead && zombie.gameObject.activeInHierarchy)
                {
                    m_HordeScratch.Add(zombie.transform.position);
                }
            }
            for (var i = m_HordeSpots.Count - 1; i >= 0; i--)
            {
                var spot = m_HordeSpots[i];
                if (now >= spot.Until)
                {
                    m_HordeSpots.RemoveAt(i);
                    continue;
                }
                // Within 15m the bot sees the spot itself: gone or killed,
                // the horde is forgotten at once.
                if ((spot.Position - position).GetHorizontalMagnitude() <= 15f
                    && CountPointsNear(m_HordeScratch, spot.Position, 12f) < 2)
                {
                    m_HordeSpots.RemoveAt(i);
                }
            }
            for (var i = 0; i < m_HordeScratch.Count; i++)
            {
                var center = Vector3.zero;
                var count = 0;
                foreach (var other in m_HordeScratch)
                {
                    if ((other - m_HordeScratch[i]).GetHorizontalMagnitude() <= HordeRadiusMeters)
                    {
                        center += other;
                        count++;
                    }
                }
                if (count >= HordeCount)
                {
                    RememberHordeSpot(center / count, count, now);
                }
            }
        }

        private static int CountPointsNear(List<Vector3> points, Vector3 center, float radius)
        {
            var count = 0;
            foreach (var point in points)
            {
                if ((point - center).GetHorizontalMagnitude() <= radius)
                {
                    count++;
                }
            }
            return count;
        }

        private void RememberHordeSpot(Vector3 center, int count, float now)
        {
            for (var i = 0; i < m_HordeSpots.Count; i++)
            {
                var spot = m_HordeSpots[i];
                if ((spot.Position - center).GetHorizontalMagnitude() <= 15f)
                {
                    spot.Until = now + HordeMemorySeconds;
                    if (count >= spot.Count)
                    {
                        spot.Position = center;
                        spot.Count = count;
                    }
                    m_HordeSpots[i] = spot;
                    return;
                }
            }
            if (m_HordeSpots.Count >= 8)
            {
                var oldest = 0;
                for (var i = 1; i < m_HordeSpots.Count; i++)
                {
                    if (m_HordeSpots[i].Until < m_HordeSpots[oldest].Until)
                    {
                        oldest = i;
                    }
                }
                m_HordeSpots.RemoveAt(oldest);
            }
            m_HordeSpots.Add(new HordeSpot { Position = center, Until = now + HordeMemorySeconds, Count = count });
            m_StatWindowHordesSeen++;
            BotLog(LogLevel.Debug,
                "Horda de {Count} zumbis lembrada em {Position} por {Seconds:0}s.",
                count,
                FormatPosition(center),
                HordeMemorySeconds);
        }

        private bool IsInHordeMemory(Vector3 point, float radius)
        {
            var now = m_BehaviorNow;
            foreach (var spot in m_HordeSpots)
            {
                if (now < spot.Until
                    && Mathf.Abs(spot.Position.y - point.y) <= 6f
                    && (spot.Position - point).GetHorizontalMagnitude() <= radius)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- trail: where the bot came from is known to be clear ----

        private readonly List<Vector3> m_TrailPoints = new List<Vector3>();
        private readonly List<float> m_TrailTimes = new List<float>();
        private float m_NextTrailAt;

        private void UpdateTrail(Vector3 position, float now)
        {
            if (now < m_NextTrailAt)
            {
                return;
            }
            m_NextTrailAt = now + 1f;
            while (m_TrailTimes.Count > 0 && now - m_TrailTimes[0] > 40f)
            {
                m_TrailTimes.RemoveAt(0);
                m_TrailPoints.RemoveAt(0);
            }
            m_TrailPoints.Add(position);
            m_TrailTimes.Add(now);
        }

        private bool IsOnRecentTrail(Vector3 point, float radius)
        {
            var now = m_BehaviorNow;
            for (var i = 0; i < m_TrailPoints.Count; i++)
            {
                if (now - m_TrailTimes[i] >= 8f
                    && Mathf.Abs(m_TrailPoints[i].y - point.y) <= 3f
                    && (m_TrailPoints[i] - point).GetHorizontalMagnitude() <= radius)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- the window after an escape ----

        private float m_DisengageUntil;
        private Vector3 m_DisengageFrom;
        private string m_DisengageReason = string.Empty;
        private Zombie? m_DisengageFire;
        private float m_NextCombatDeclineLogAt;

        private bool IsDisengaging(float now)
        {
            return now < m_DisengageUntil;
        }

        private void StartDisengage(Vector3 threat, string reason, float now, Zombie? fire = null)
        {
            m_DisengageUntil = now + DisengageSeconds;
            m_DisengageFrom = threat;
            m_DisengageReason = reason;
            m_DisengageFire = fire;
            m_StatWindowDisengages++;
            BotLog(LogLevel.Information,
                "Janela de {Seconds:0}s depois de {Reason}: so luta com zumbi isolado que vem atras do bot a ate {Meters:0}m; patrulha e loot longe de {Position}.",
                DisengageSeconds,
                reason,
                DisengageEngageMeters,
                FormatPosition(threat));
        }

        private bool IsAvoidedAfterEscape(Vector3 point, float now)
        {
            return IsDisengaging(now)
                && (point - m_DisengageFrom).GetHorizontalMagnitude() < DisengageAvoidMeters;
        }

        // Whether ordinary combat may go for this zombie. Self-defense (2.5m)
        // always may; a hunted boss and the burner have their own rules.
        private bool IsCombatTargetAllowed(Zombie target, Vector3 position, float now, out string why)
        {
            why = string.Empty;
            if (target == null || IsHuntedBoss(target) || IsFireZombie(target))
            {
                return true;
            }
            var distance = (target.transform.position - position).GetHorizontalMagnitude();
            // 0.1.174: self-defense reached only 2.5m; zombies refused at
            // 2.5-4m hit the bot right before 3 deaths, and the target flipped
            // Combat/Patrol on the 2.5m line. Now 4m to take a zombie, 5m to
            // let the current one go, and one hunting the bot is never refused
            // within 6m: it would hit the bot anyway.
            if (distance <= (target == m_TargetZombie ? SelfDefenseReleaseMeters : SelfDefenseMeters))
            {
                return true;
            }
            if (distance <= HunterNeverRefuseMeters && IsHuntingBot(target))
            {
                return true;
            }
            if (IsDisengaging(now))
            {
                if (!IsHuntingBot(target)
                    || distance > DisengageEngageMeters
                    || CountZombiesNear(target.transform.position, DisengageIsolationMeters, target) > 0)
                {
                    why = "janela depois de " + m_DisengageReason + " (so zumbi isolado a ate 8m)";
                    return false;
                }
                var fire = m_DisengageFire;
                if (fire != null
                    && !fire.isDead
                    && fire.gameObject.activeInHierarchy
                    && (target.transform.position - fire.transform.position).GetHorizontalMagnitude() < DisengageFireTargetMeters)
                {
                    why = "a menos de 20m do zumbi de fogo";
                    return false;
                }
            }
            var crowd = CountZombiesNear(target.transform.position, HordeRadiusMeters) ;
            if (crowd >= HordeCount
                && !(HasLoadedRangedWeapon() && distance >= HordeShootMeters))
            {
                why = crowd + " zumbis a 10m dele";
                return false;
            }
            return true;
        }

        private const float SelfDefenseMeters = 4f;
        private const float SelfDefenseReleaseMeters = 5f;
        private const float HunterNeverRefuseMeters = 6f;

        private Zombie? m_LastDeclinedZombie;

        private void LogCombatDeclined(Zombie target, Vector3 position, float now, string why)
        {
            // Counted once per zombie, not once per scan.
            if (target != m_LastDeclinedZombie)
            {
                m_LastDeclinedZombie = target;
                m_StatWindowCombatDeclined++;
            }
            if (now < m_NextCombatDeclineLogAt)
            {
                return;
            }
            m_NextCombatDeclineLogAt = now + 5f;
            BotLog(LogLevel.Information,
                "Nao atacando zumbi a {Distance:0.#}m: {Why}.",
                (target.transform.position - position).GetHorizontalMagnitude(),
                why);
        }

        // ---- fire ----

        private Zombie? m_FireFleeZombie;
        private float m_FireFleeStartedAt;
        private float m_FireFleeEndedAt = float.NegativeInfinity;
        private float m_FireFleeSampleAt;
        private Vector3 m_FireFleeSamplePosition;
        private float m_FireFleeMovedMeters = float.PositiveInfinity;
        private bool m_FireFleeLeftArea;
        private float m_FireEscapeFailedAt = float.NegativeInfinity;
        private Zombie? m_FireLastResortZombie;
        private float m_FireWaterRefugeUntil;
        private float m_NextFireWaterRefugeAt;
        private bool m_FireWaterHoldLogged;
        private float m_NextFireCrowdLogAt;
        private float m_NextFireWeaponGateLogAt;

        private bool ShouldStartFireFlee(Zombie fire, float distance, Vector3 position, float now)
        {
            var huntingLimit = now - m_FireFleeEndedAt < DisengageSeconds
                ? FireFleeHuntingAgainMeters
                : FireFleeHuntingMeters;
            if (!(IsHuntingBot(fire) && distance <= huntingLimit) && distance > FireFleeAnyMeters)
            {
                return false;
            }
            // 0.2.2x13: alone, healthy and with a good blade the tryhard
            // kills it, as the players did, instead of running.
            if (CanTryhardMeleeFire(fire, distance))
            {
                return false;
            }
            // A loaded gun from 10m out shoots it instead, unless ordinary
            // zombies stand next to it (its explosion turns them into burners).
            return !(HasLoadedRangedWeapon()
                && distance >= FireShootMinMeters
                && !IsFireTooCrowdedToShoot(fire, position));
        }

        // A gun only from 10m out (inside that the bot runs) and with no
        // ordinary zombies next to it.
        private bool IsFireShootable(Zombie fire, Vector3 position)
        {
            return (fire.transform.position - position).GetHorizontalMagnitude() >= FireShootMinMeters
                && !IsFireTooCrowdedToShoot(fire, position);
        }

        private bool IsFireTooCrowdedToShoot(Zombie fire, Vector3 position)
        {
            return (fire.transform.position - position).GetHorizontalMagnitude() < 25f
                && CountZombiesNear(fire.transform.position, 4f, fire, true) >= 2;
        }

        // Fighting a burner up close is the last thing to do: its death
        // explosion is up to 40 damage within 4m and turns every ordinary
        // zombie within 4m into a burner.
        private bool IsFireLastResort(Zombie fire, float distance, float now)
        {
            if (!m_FireFleeActive || !(HasMeleeWeapon() || HasLoadedRangedWeapon()))
            {
                return false;
            }
            // Once started, the fight goes on while the burner stays within
            // 4m: stamina comes back while fighting, and turning to run again
            // at 6 stamina only takes its hits in the back.
            if (fire == m_FireLastResortZombie
                && fire == m_TargetZombie
                && m_ObjectiveType == ObjectiveType.Combat
                && distance <= 4f)
            {
                return true;
            }
            return distance <= 3f
                && m_Player.life.stamina <= FireLastResortStamina
                && now - m_FireEscapeFailedAt <= 3f
                && m_Player.life.health > FireLastResortHealth
                && CountZombiesNear(fire.transform.position, 5f, fire, true) == 0;
        }

        private BotBehaviorStatus TickFireFlee(Zombie fire, Vector3 position, float now)
        {
            var distance = (fire.transform.position - position).GetHorizontalMagnitude();
            var hunting = IsHuntingBot(fire);
            if (!m_FireFleeActive)
            {
                m_FireFleeActive = true;
                m_FireFleeZombie = fire;
                m_FireFleeStartedAt = now;
                m_FireFleeSampleAt = now;
                m_FireFleeSamplePosition = fire.transform.position;
                m_FireFleeMovedMeters = float.PositiveInfinity;
                m_FireFleeLeftArea = false;
                m_FireEscapeFailedAt = float.NegativeInfinity;
                m_FireLastResortZombie = null;
                m_NextIgnoredFireFleeAt = 0f;
                m_StatWindowFireFlees++;
                BotLog(LogLevel.Information,
                    "Fugindo do zumbi de fogo a {Distance:0.#}m ({Why}); vida {Health}, stamina {Stamina}.",
                    distance,
                    hunting ? "vem atras do bot" : "perto demais",
                    m_Player.life.health,
                    m_Player.life.stamina);
                m_FireFleeStarts.Add(now);
                m_FireFleeStarts.RemoveAll(t => now - t > 60f);
                if (m_FireFleeStarts.Count >= 3
                    && TryInsistOnArea("zumbi de fogo atras do bot 3 vezes em 60s", now))
                {
                    m_FireFleeStarts.Clear();
                }
                if (m_FireFleeStarts.Count >= 3)
                {
                    m_FireFleeStarts.Clear();
                    BotLog(LogLevel.Information,
                        "Zumbi de fogo continua atras do bot (3 fugas em 60s); saindo da area {Area}.",
                        m_HasInterestArea ? m_InterestAreaName : "atual");
                    FinishInterestArea(now);
                    AvoidFireArea(fire.transform.position, FireZombieReconsiderSeconds);
                    m_NextExplorationSelectionTime = now;
                }
            }
            else if (fire != m_FireFleeZombie)
            {
                m_FireFleeZombie = fire;
                m_FireFleeSampleAt = now;
                m_FireFleeSamplePosition = fire.transform.position;
                m_FireFleeMovedMeters = float.PositiveInfinity;
            }
            if (now - m_FireFleeSampleAt >= FireFleeStillSampleSeconds)
            {
                m_FireFleeMovedMeters = (fire.transform.position - m_FireFleeSamplePosition).GetHorizontalMagnitude();
                m_FireFleeSampleAt = now;
                m_FireFleeSamplePosition = fire.transform.position;
            }

            // 0.1.172 ended the flee when the distance stopped shrinking, which
            // a burner running after the bot at the same speed also does: 28
            // flees ended with it 25m behind and back at 5-7m seconds later.
            if (now - m_FireFleeStartedAt >= 3f)
            {
                string? reason = null;
                if (distance >= FireFleeEndMeters)
                {
                    reason = "mais de 40m";
                }
                else if (distance >= FireFleeEndNotHuntingMeters && !hunting)
                {
                    reason = "zumbi deixou de cacar o bot";
                }
                else if (distance >= FireFleeEndNotHuntingMeters && m_FireFleeMovedMeters < 2f)
                {
                    reason = "zumbi parado";
                }
                else if (distance >= FireFleeEndNoSightMeters && !CanZombieSee(fire, position))
                {
                    reason = "fora da vista";
                }
                // 0.2.2x13: far enough out to shoot it: the players turn
                // and shoot from 10-16m instead of running on to 40m.
                else if (CanTryhardStopFleeToShoot(fire, distance))
                {
                    reason = "longe o bastante para atirar nele";
                }
                if (reason != null)
                {
                    EndFireFlee(now, reason);
                    return BotBehaviorStatus.Failure;
                }
            }

            if (!m_FireFleeLeftArea && now - m_FireFleeStartedAt >= FireFleeLeaveAreaSeconds
                && TryInsistOnArea("fuga do zumbi de fogo passou de " + FireFleeLeaveAreaSeconds.ToString("0") + "s", now))
            {
                m_FireFleeLeftArea = true;
            }
            if (!m_FireFleeLeftArea && now - m_FireFleeStartedAt >= FireFleeLeaveAreaSeconds)
            {
                m_FireFleeLeftArea = true;
                BotLog(LogLevel.Information,
                    "Fuga do zumbi de fogo passou de {Seconds:0}s (a {Distance:0.#}m, stamina {Stamina}); saindo da area {Area}.",
                    FireFleeLeaveAreaSeconds,
                    distance,
                    m_Player.life.stamina,
                    m_HasInterestArea ? m_InterestAreaName : "atual");
                FinishInterestArea(now);
                AvoidFireArea(fire.transform.position, FireZombieReconsiderSeconds);
                m_NextExplorationSelectionTime = now;
            }

            if (IsFireLastResort(fire, distance, now))
            {
                m_FireLastResortZombie = fire;
                if (m_TargetZombie != fire)
                {
                    BotLog(LogLevel.Information,
                        "Zumbi de fogo colado a {Distance:0.#}m, sem stamina e sem ponto de fuga; ultimo recurso: lutando com {Weapon} (vida {Health}).",
                        distance,
                        DescribeEquipped(),
                        m_Player.life.health);
                }
                ForceCombatTarget(fire, now);
                return BotBehaviorStatus.Running;
            }

            if (m_ObjectiveType == ObjectiveType.Flee
                && m_FleeingFromPlayerName == FireWaterFleeName
                && now < m_ObjectiveExpiresAt)
            {
                return BotBehaviorStatus.Running;
            }
            if (m_Player.life.stamina < FireWaterRefugeStamina
                && distance < FireWaterRefugeBurnerMeters
                && now >= m_NextFireWaterRefugeAt)
            {
                m_NextFireWaterRefugeAt = now + 5f;
                if (TryFindWaterRefuge(position, fire.transform.position, out var water))
                {
                    m_NextFireWaterRefugeAt = now + 60f;
                    m_FireWaterRefugeUntil = now + FireWaterRefugeSeconds;
                    m_FireWaterHoldLogged = false;
                    m_StatWindowWaterRefuges++;
                    m_FleeingFromPlayerName = FireWaterFleeName;
                    SetObjective(ObjectiveType.Flee, water, now + 10f);
                    m_DirectFallbackUntil = now + 10f;
                    BotLog(LogLevel.Information,
                        "Sem stamina ({Stamina}) com o zumbi de fogo a {Distance:0.#}m; indo para a agua em {Water} ({Meters:0}m): ele desiste de quem nada.",
                        m_Player.life.stamina,
                        distance,
                        FormatPosition(water),
                        (water - position).GetHorizontalMagnitude());
                    return BotBehaviorStatus.Running;
                }
            }

            if (now >= m_NextIgnoredFireFleeAt
                && (m_ObjectiveType != ObjectiveType.Flee
                    || now >= m_ObjectiveExpiresAt
                    || (m_ObjectivePosition - position).GetHorizontalMagnitude() <= 4f))
            {
                m_NextIgnoredFireFleeAt = now + 1.5f;
                if (TryFindFireEscapePoint(position, fire, now, out var fireEscape)
                    || TryFindLowHealthEscapePoint(position, now, out fireEscape))
                {
                    m_FleeingFromPlayerName = "zumbi de fogo";
                    SetObjective(ObjectiveType.Flee, fireEscape, now + 8f);
                }
                else
                {
                    m_FireEscapeFailedAt = now;
                }
                if (now >= m_NextIgnoredFireFleeLogAt)
                {
                    m_NextIgnoredFireFleeLogAt = now + 5f;
                    BotLog(LogLevel.Information,
                        "Zumbi de fogo a {Distance:0.#}m ({Hunting}); se afastando dele (vida {Health}, stamina {Stamina}).",
                        distance,
                        hunting ? "cacando o bot" : "parado ou distraido",
                        m_Player.life.health,
                        m_Player.life.stamina);
                }
            }
            return BotBehaviorStatus.Running;
        }

        // Water deep enough to swim, ahead of the flee: a zombie stops
        // hunting a player who swims (Zombie: leave(quick) when the target's
        // stance is SWIM and the zombie itself is not under water).
        private bool TryFindWaterRefuge(Vector3 position, Vector3 firePosition, out Vector3 water)
        {
            water = position;
            var away = position - firePosition;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
            }
            away.Normalize();
            for (var radius = 6f; radius <= FireWaterRefugeSearchMeters; radius += 4f)
            {
                for (var step = -4; step <= 4; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 25f, 0f) * away;
                    var probe = position + direction * radius;
                    if (!Physics.Raycast(
                            new Vector3(probe.x, position.y + 20f, probe.z),
                            Vector3.down,
                            out var ground,
                            60f,
                            RayMasks.BLOCK_COLLISION,
                            QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    if (ground.point.y > position.y + 2f
                        || !WaterUtility.isPointUnderwater(ground.point + Vector3.up * 1.6f))
                    {
                        continue;
                    }
                    // The navmesh ends at the bank: the bot walks there in a
                    // straight line, so the line must be free.
                    var target = ground.point + Vector3.up * 1.2f;
                    if (!SkaliaBotUnityNavmesh.IsBodyClear(position, new Vector3(target.x, position.y, target.z)))
                    {
                        continue;
                    }
                    water = target;
                    return true;
                }
            }
            return false;
        }

        // In the water with the burner still around: stay there.
        private bool IsHoldingInFireWaterRefuge(float now)
        {
            if (now >= m_FireWaterRefugeUntil)
            {
                return false;
            }
            if (!TryFindFireThreat(m_BehaviorPosition, FireFleeHuntingMeters, out var fire)
                || !(IsHuntingBot(fire)
                     || (fire.transform.position - m_BehaviorPosition).GetHorizontalMagnitude() < FireWaterRefugeBurnerMeters))
            {
                m_FireWaterRefugeUntil = 0f;
                BotLog(LogLevel.Information,
                    "Zumbi de fogo desistiu do bot na agua; saindo da agua.");
                return false;
            }
            if (!m_FireWaterHoldLogged)
            {
                m_FireWaterHoldLogged = true;
                BotLog(LogLevel.Information,
                    "Bot na agua esperando o zumbi de fogo a {Distance:0.#}m desistir (stamina {Stamina}).",
                    (fire.transform.position - m_BehaviorPosition).GetHorizontalMagnitude(),
                    m_Player.life.stamina);
            }
            if (m_ObjectiveType != ObjectiveType.None)
            {
                ClearObjective();
            }
            return true;
        }

        private bool CanRunForFireWeapon(Vector3 position, out string why)
        {
            why = string.Empty;
            if (m_Player.life.health < FireWeaponRunHealth)
            {
                why = "vida " + m_Player.life.health;
                return false;
            }
            if (CountFireZombiesWithin(position, 15f) > 1)
            {
                why = "mais de um zumbi de fogo a 15m";
                return false;
            }
            return true;
        }

        private bool IsFireWeaponItemSafe(Vector3 item, Vector3 position)
        {
            if (!TryFindFireThreat(position, FireZombieVisualRadiusMeters, out var fire))
            {
                return true;
            }
            return (item - fire.transform.position).GetHorizontalMagnitude()
                > (position - fire.transform.position).GetHorizontalMagnitude();
        }

        private void LogFireWeaponGate(string why, float now)
        {
            if (now < m_NextFireWeaponGateLogAt)
            {
                return;
            }
            m_NextFireWeaponGateLogAt = now + 10f;
            BotLog(LogLevel.Information,
                "Zumbi de fogo: sem corrida atras de arma ({Why}); so se afastando.",
                why);
        }

        // ---- escape routes ----

        private readonly List<Vector3> m_EscapeRouteScratch = new List<Vector3>();
        private readonly List<Vector3> m_EscapeThreatPoints = new List<Vector3>();

        private struct EscapeCandidate
        {
            public Vector3 Point;
            public float Score;
            public float StraightPenalty;
        }

        private readonly List<EscapeCandidate> m_EscapeCandidates = new List<EscapeCandidate>();

        // The real route to an escape point (its corners), not the straight
        // line: false when there is no route.
        // 0.1.174: each threat counts once per route, at its closest point to
        // it. 0.1.173 added a penalty per corner, so a long route through the
        // same zombies scored worse than a short one and the swarm escape
        // point fell from a median 33m to 13m. A zombie within 3m of the bot
        // can't reject the route (every route starts next to it), but one the
        // first leg runs into still costs.
        private bool TryScoreEscapeRoute(
            Vector3 origin,
            Vector3 candidate,
            List<Vector3> threats,
            float rejectMeters,
            float penaltyMeters,
            float flatPenalty,
            float slope,
            out float penalty,
            out bool rejected)
        {
            penalty = 0f;
            rejected = false;
            if (!SkaliaBotUnityNavmesh.TryFindPath(origin, candidate, m_EscapeRouteScratch)
                || m_EscapeRouteScratch.Count == 0)
            {
                return false;
            }
            var length = 0f;
            var previous = origin;
            foreach (var corner in m_EscapeRouteScratch)
            {
                length += (corner - previous).GetHorizontalMagnitude();
                previous = corner;
            }
            var firstLeg = m_EscapeRouteScratch[0] - origin;
            firstLeg.y = 0f;
            foreach (var threat in threats)
            {
                var toThreat = threat - origin;
                toThreat.y = 0f;
                if (toThreat.magnitude <= 3f)
                {
                    if (firstLeg.sqrMagnitude > 0.01f
                        && toThreat.sqrMagnitude > 0.01f
                        && Vector3.Dot(firstLeg.normalized, toThreat.normalized) > 0.5f)
                    {
                        penalty += flatPenalty + slope * penaltyMeters;
                    }
                    continue;
                }
                var closest = float.PositiveInfinity;
                previous = origin;
                foreach (var corner in m_EscapeRouteScratch)
                {
                    closest = Mathf.Min(closest, GetHorizontalDistanceToSegment(threat, previous, corner));
                    previous = corner;
                }
                if (closest <= rejectMeters)
                {
                    rejected = true;
                    return true;
                }
                if (closest <= penaltyMeters)
                {
                    penalty += flatPenalty + slope * (penaltyMeters - closest);
                }
            }
            var straight = (candidate - origin).GetHorizontalMagnitude();
            if (length > straight * 2.5f + 5f)
            {
                penalty += 15f;
            }
            return true;
        }

        // 0.1.174: the best three are checked along their real route; when all
        // three are rejected, the next three are, and only then the best of the
        // rest by straight line (0.1.173 took the fourth one unchecked).
        private bool PickEscapeCandidateByRoute(
            Vector3 origin,
            List<Vector3> threats,
            float rejectMeters,
            float penaltyMeters,
            float flatPenalty,
            float slope,
            out Vector3 best)
        {
            best = origin;
            // 0.2.2x11: with the new mesh whole, only a point it has a route
            // to. thigas fled the Mega at 03:36 toward points off the mesh
            // ("destino fora da malha (nada a 8m)") twice, the flight was
            // given up each time and he stood until the Mega killed him.
            if (SkaliaBotUnityNavmesh.MapFilled)
            {
                return PickRoutedEscapeCandidate(origin, threats, rejectMeters, penaltyMeters, flatPenalty, slope, out best);
            }
            if (m_EscapeCandidates.Count == 0)
            {
                return false;
            }
            m_EscapeCandidates.Sort((left, right) => right.Score.CompareTo(left.Score));
            var bestScore = float.NegativeInfinity;
            var found = false;
            for (var start = 0; start < 6 && start < m_EscapeCandidates.Count && !found; start += 3)
            {
                for (var i = start; i < start + 3 && i < m_EscapeCandidates.Count; i++)
                {
                    var candidate = m_EscapeCandidates[i];
                    float score;
                    if (TryScoreEscapeRoute(origin, candidate.Point, threats, rejectMeters, penaltyMeters, flatPenalty, slope, out var routePenalty, out var rejected))
                    {
                        if (rejected)
                        {
                            continue;
                        }
                        score = candidate.Score - routePenalty;
                    }
                    else
                    {
                        score = candidate.Score - candidate.StraightPenalty;
                    }
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate.Point;
                        found = true;
                    }
                }
            }
            if (found)
            {
                return true;
            }
            for (var i = 6; i < m_EscapeCandidates.Count; i++)
            {
                var candidate = m_EscapeCandidates[i];
                var score = candidate.Score - candidate.StraightPenalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate.Point;
                    found = true;
                }
            }
            return found;
        }

        // ---- acid ----

        private float m_NextAcidStepAt;
        private float m_PendingSpitReactionAt = -1f;
        private float m_NextAcidNoStepLogAt;

        private static bool IsPointInAcidOrFire(Vector3 point)
        {
            var temperature = GetPointTemperature(point);
            return temperature == EPlayerTemperature.ACID || temperature == EPlayerTemperature.BURNING;
        }

        private static EPlayerTemperature GetPointTemperature(Vector3 point)
        {
            // The bubble list only exists once the level is loaded.
            if (!Level.isLoaded)
            {
                return EPlayerTemperature.NONE;
            }
            try
            {
                return TemperatureManager.checkPointTemperature(point, false);
            }
            catch (System.Exception)
            {
                return EPlayerTemperature.NONE;
            }
        }

        // "SPIT" damage is not the spit hitting: it is standing in the puddle
        // the spitter leaves (PlayerLife: 10 every 10 ticks while the point
        // is in an ACID bubble). 5 of the 16 deaths of the 0.1.172 night.
        private void HandleAcidDamage()
        {
            if (!m_Running || m_Player.life.isDead)
            {
                return;
            }
            var now = Time.realtimeSinceStartup;
            var position = m_Player.transform.position;
            if (m_ObjectiveType == ObjectiveType.Loot && IsPointInAcidOrFire(m_ObjectivePosition))
            {
                BotLog(LogLevel.Information,
                    "Item alvo em {Position} esta dentro de acido ou fogo; largando por {Seconds:0}s.",
                    FormatPosition(m_ObjectivePosition),
                    AcidLootIgnoreSeconds);
                IgnoreLoot(m_LootInstanceId, now + AcidLootIgnoreSeconds);
                ClearObjective();
            }
            if (GetPointTemperature(position) == EPlayerTemperature.ACID)
            {
                TryStepOutOfAcid(position, now);
            }
            if (!m_SwarmEscapeActive && m_PendingSpitReactionAt < 0f)
            {
                m_PendingSpitReactionAt = now + SpitReactionDelaySeconds;
            }
        }

        private void UpdatePendingSpitReaction(float now)
        {
            if (m_PendingSpitReactionAt < 0f || now < m_PendingSpitReactionAt)
            {
                return;
            }
            // Still stepping out of the puddle: the reaction waits for that.
            if (m_ObjectiveType == ObjectiveType.Flee
                && m_FleeingFromPlayerName == AcidFleeName
                && now < m_ObjectiveExpiresAt
                && (m_ObjectivePosition - m_BehaviorPosition).GetHorizontalMagnitude() > 4f)
            {
                m_PendingSpitReactionAt = now + 0.5f;
                return;
            }
            m_PendingSpitReactionAt = -1f;
            if (!m_SwarmEscapeActive)
            {
                ReactToSpit();
            }
        }

        private bool IsSpitterAttackAllowed(Zombie spitter, Vector3 position, float now)
        {
            if (IsCombatTargetAllowed(spitter, position, now, out var why))
            {
                return true;
            }
            LogCombatDeclined(spitter, position, now, "cuspidor; " + why);
            return false;
        }

        private void TryStepOutOfAcid(Vector3 position, float now)
        {
            if (now < m_NextAcidStepAt)
            {
                return;
            }
            m_NextAcidStepAt = now + 1f;
            // Already on the way to a point out of the acid: leave it.
            if ((m_ObjectiveType == ObjectiveType.Flee
                 || m_ObjectiveType == ObjectiveType.EscapeSwarm
                 || m_ObjectiveType == ObjectiveType.EscapeHazard)
                && (m_ObjectivePosition - position).GetHorizontalMagnitude() > 4f
                && !IsPointInAcidOrFire(m_ObjectivePosition))
            {
                return;
            }
            var best = position;
            var bestScore = float.NegativeInfinity;
            // The forced movement stops 4m short of a flee point: the point
            // is 9-12m out so the bot ends 5-8m away.
            for (var radius = 9f; radius <= 12f; radius += 1.5f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    if (!SkaliaBotUnityNavmesh.TrySnap(position + direction * radius, 1.5f, out var point))
                    {
                        continue;
                    }
                    var stop = position + direction * (radius - 4f);
                    if (Mathf.Abs(point.y - position.y) > 2f
                        || !SkaliaBotUnityNavmesh.IsBodyClear(position, point)
                        || IsHazardousPosition(point, now, 1f)
                        || IsPointInAcidOrFire(point)
                        || IsPointInAcidOrFire(stop))
                    {
                        continue;
                    }
                    var nearest = 30f;
                    foreach (var entry in m_PerceivedZombies)
                    {
                        var zombie = entry.Key;
                        if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
                        {
                            continue;
                        }
                        nearest = Mathf.Min(nearest, (zombie.transform.position - point).GetHorizontalMagnitude());
                    }
                    var score = nearest - radius * 0.2f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = point;
                    }
                }
            }
            if (bestScore == float.NegativeInfinity)
            {
                if (now >= m_NextAcidNoStepLogAt)
                {
                    m_NextAcidNoStepLogAt = now + 10f;
                    BotLog(LogLevel.Information,
                        "Bot em poca de acido em {Position} (vida {Health}) sem ponto bom para sair; seguindo como estava.",
                        FormatPosition(position),
                        m_Player.life.health);
                }
                return;
            }
            m_StatWindowAcidSteps++;
            BotLog(LogLevel.Information,
                "Bot em poca de acido em {Position} (vida {Health}); saindo para {Destination}.",
                FormatPosition(position),
                m_Player.life.health,
                FormatPosition(best));
            m_FleeingFromPlayerName = AcidFleeName;
            SetObjective(ObjectiveType.Flee, best, now + 3f);
        }

        // ---- upper floors ----

        private float m_UpperTeleportFailedAt = float.NegativeInfinity;
        private Vector3 m_UpperTeleportFailedPosition;

        private static bool IsUpperFloor(Vector3 position)
        {
            return position.y - LevelGround.getHeight(position) > 3f;
        }

        private void NoteStallTeleportFailed(Vector3 position, float now)
        {
            if (IsUpperFloor(position))
            {
                m_UpperTeleportFailedAt = now;
                m_UpperTeleportFailedPosition = position;
            }
        }

        private bool ShouldTeleportDown(Vector3 position, bool repeated, float now)
        {
            if (!IsUpperFloor(position))
            {
                return false;
            }
            return repeated
                || (now - m_UpperTeleportFailedAt <= 120f
                    && (position - m_UpperTeleportFailedPosition).GetHorizontalMagnitude() <= 6f);
        }

        // 0.1.172 night: XXlloqiartyXX took 29 unstuck teleports of 3m in 6
        // min on the second floor of a building, every one to the same floor
        // and none held; Tadeu waited 9 min for the long rescue.
        private bool TryTeleportDownFromUpperFloor(Vector3 position, float now)
        {
            var height = position.y - LevelGround.getHeight(position);
            for (var radius = 4f; radius <= 40f; radius += 4f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    var probe = position + direction * radius;
                    var ground = LevelGround.getHeight(probe);
                    if (!SkaliaBotUnityNavmesh.TrySnap(new Vector3(probe.x, ground, probe.z), 2f, out var point)
                        || Mathf.Abs(point.y - ground) > 1.5f
                        || point.y > position.y - 2.5f
                        || IsPointInAcidOrFire(point)
                        || IsHazardousPosition(point, now, 2f)
                        || IsNearFreshDeath(point, 15f)
                        || IsInHordeMemory(point, 10f)
                        || CountZombiesNear(point, 8f) > 0)
                    {
                        continue;
                    }
                    if (m_PatrolTargetSet)
                    {
                        NotePatrolPointFailed(m_PatrolTargetPoint, now, "andar de cima travado", true);
                        m_PatrolTargetSet = false;
                    }
                    MarkOrbitBadSpot(position, now);
                    m_StatWindowUpperDown++;
                    BotLog(LogLevel.Warning,
                        "Bot travado de novo no andar de cima em {Position} ({Height:0.#}m acima do chao); descendo para o chao em {Destination}; ponto marcado por 10 min.",
                        FormatPosition(position),
                        height,
                        FormatPosition(point));
                    m_LocalRecoveryPhase = LocalRecoveryPhase.None;
                    ClearObjective();
                    var destination = point + Vector3.up * 0.3f;
                    m_TeleportExpected = destination;
                    m_TeleportCheckAt = now + 1.5f;
                    m_TeleportExpectDown = true;
                    TeleportBot(destination, m_Player.look.yaw);
                    m_StallAnchor = destination;
                    m_NextExplorationSelectionTime = 0f;
                    return true;
                }
            }
            BotLog(LogLevel.Information,
                "Bot travado no andar de cima em {Position}: nenhum ponto de chao seguro a 40m; seguindo com o destravar normal.",
                FormatPosition(position));
            return false;
        }

        // ---- loot: who gets there first ----

        private float m_NextLootRaceLogAt;
        private byte m_PendingLootRegionX;
        private byte m_PendingLootRegionY;
        private uint m_PendingLootId;
        private float m_PendingLootUntil;
        private int m_PendingLootTries;
        private float m_NextPendingLootAt;

        // 0.1.172 looked only at distances now: a hunter 25-30m away covers
        // the 10m between "start" (25m) and "cancel" (15m) in 2s.
        private bool IsLootRaceLost(Vector3 item, Vector3 position, out float botSeconds, out float zombieSeconds)
        {
            botSeconds = (item - position).GetHorizontalMagnitude() / 6f + 1f;
            zombieSeconds = float.PositiveInfinity;
            foreach (var entry in m_PerceivedZombies)
            {
                ConsiderLootRace(entry.Key, item, ref zombieSeconds);
            }
            foreach (var entry in m_MarkedSeenZombies)
            {
                ConsiderLootRace(entry.Key, item, ref zombieSeconds);
            }
            return zombieSeconds < botSeconds + LootRaceMarginSeconds;
        }

        private void ConsiderLootRace(Zombie zombie, Vector3 item, ref float zombieSeconds)
        {
            if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
            {
                return;
            }
            var distance = (zombie.transform.position - item).GetHorizontalMagnitude();
            if (IsHuntingBot(zombie))
            {
                zombieSeconds = Mathf.Min(zombieSeconds, distance / GetZombieSpeed(zombie));
            }
            else if (distance <= LootRaceIdleZombieMeters
                     && Mathf.Abs(zombie.transform.position.y - item.y) <= 3f)
            {
                // Idle next to the item: the pickup would be cancelled there.
                zombieSeconds = 0f;
            }
        }

        // An item lost to a zombie is left for 5s so the search moves on to
        // the next one instead of picking it again every second.
        private readonly Dictionary<uint, float> m_RaceDeferredLoot = new Dictionary<uint, float>();

        private void DeferLootRace(uint instanceId, float now)
        {
            if (m_RaceDeferredLoot.Count >= 32)
            {
                m_RaceDeferredLoot.Clear();
            }
            m_RaceDeferredLoot[instanceId] = now + 5f;
        }

        private bool IsLootRaceDeferred(uint instanceId, float now)
        {
            return m_RaceDeferredLoot.Count > 0
                && m_RaceDeferredLoot.TryGetValue(instanceId, out var until)
                && now < until;
        }

        // Acid or fire always; a remembered horde (12m) and the place just
        // escaped from (20m, for 20s) unless the item is exempt.
        private bool IsLootSpotAvoided(Vector3 point, float now, bool exempt)
        {
            if (IsPointInAcidOrFire(point))
            {
                return true;
            }
            return !exempt
                && (IsInHordeMemory(point, 12f) || IsAvoidedAfterEscape(point, now));
        }

        private void RememberPendingLoot(float now)
        {
            if (m_LootInstanceId == 0)
            {
                return;
            }
            m_PendingLootRegionX = m_LootRegionX;
            m_PendingLootRegionY = m_LootRegionY;
            m_PendingLootId = m_LootInstanceId;
            m_PendingLootPoint = m_ObjectivePosition;
            m_PendingLootUntil = now + PendingLootSeconds;
            m_PendingLootTries = 0;
            m_NextPendingLootAt = now + 3f;
        }

        // 0.1.174: resumed loot was a median 49m away (up to 124m) and 16 of
        // 53 were cancelled again: only within 30m now. Farther, the item
        // stays remembered until it expires, in case the bot comes back near.
        private Vector3 m_PendingLootPoint;
        private const float PendingLootResumeMeters = 30f;

        private bool HasPendingLoot(float now)
        {
            return m_PendingLootId != 0
                && now >= m_NextPendingLootAt
                && now < m_PendingLootUntil
                && (m_PendingLootPoint - m_BehaviorPosition).GetHorizontalMagnitude() <= PendingLootResumeMeters;
        }

        private bool TryResumePendingLoot(float now)
        {
            if (m_PendingLootId == 0 || now < m_NextPendingLootAt)
            {
                return false;
            }
            m_NextPendingLootAt = now + 3f;
            if (now < m_PendingLootUntil
                && (m_PendingLootPoint - m_BehaviorPosition).GetHorizontalMagnitude() > PendingLootResumeMeters)
            {
                return false;
            }
            if (now >= m_PendingLootUntil
                || ++m_PendingLootTries > 3
                || IsLootIgnored(m_PendingLootId, now)
                || IsLootClaimedByAnotherBot(m_PendingLootId, now)
                || ItemManager.regions == null
                || !Regions.checkSafe(m_PendingLootRegionX, m_PendingLootRegionY))
            {
                m_PendingLootId = 0;
                return false;
            }
            var region = ItemManager.regions[m_PendingLootRegionX, m_PendingLootRegionY];
            if (region == null)
            {
                m_PendingLootId = 0;
                return false;
            }
            foreach (var item in region.items)
            {
                if (item.instanceID != m_PendingLootId)
                {
                    continue;
                }
                if (SetLootObjective(m_PendingLootRegionX, m_PendingLootRegionY, item, now))
                {
                    m_PendingLootId = 0;
                    m_StatWindowPendingResumed++;
                    BotLog(LogLevel.Information,
                        "Voltando ao loot que tinha largado por causa de zumbi: {Item} a {Distance:0.#}m.",
                        DescribeItem(item.item),
                        (item.point - m_BehaviorPosition).GetHorizontalMagnitude());
                    return true;
                }
                return false;
            }
            // Someone took it.
            m_PendingLootId = 0;
            return false;
        }

        // ---- routes: back onto the route before throwing it away ----

        private int m_RejoinGeneration = -1;
        private int m_RejoinIndex = -1;
        private float m_RejoinAt;

        private bool TryRejoinRoute(Vector3 position, float now)
        {
            if (m_RejoinGeneration == m_PathRequestGeneration
                && m_RejoinIndex == m_PathIndex
                && now - m_RejoinAt < 3f)
            {
                return false;
            }
            var next = m_PathIndex + 1;
            if (next < m_PathPoints.Count
                && Mathf.Abs(m_PathPoints[next].y - position.y) <= 1.8f
                && SkaliaBotUnityNavmesh.IsStraight(position, m_PathPoints[next])
                && SkaliaBotUnityNavmesh.IsBodyClear(position, m_PathPoints[next]))
            {
                m_PathIndex = next;
            }
            else
            {
                // 0.2.2x11: never back to the corner just passed. That walked
                // the bot back up to 8m, then forward over the same leg, then
                // back again (the tester: "vai e volta"); a route from here goes
                // forward instead.
                return false;
            }
            m_RejoinGeneration = m_PathRequestGeneration;
            m_RejoinIndex = m_PathIndex;
            m_RejoinAt = now;
            m_StatWindowRejoins++;
            return true;
        }

        // ---- swarm ----

        private bool m_SwarmLeftArea;
        private Vector3 m_SwarmThreatCenter;

        private Zombie? FindSwarmBlocker(Vector3 position)
        {
            var heading = m_ObjectivePosition - position;
            heading.y = 0f;
            if (heading.sqrMagnitude < 0.01f)
            {
                heading = Quaternion.Euler(0f, m_CurrentYaw, 0f) * Vector3.forward;
            }
            heading.Normalize();
            Zombie? best = null;
            var bestDistance = 1.5f;
            foreach (var entry in m_PerceivedZombies)
            {
                var zombie = entry.Key;
                // Never a burner (its explosion) nor a boss.
                if (zombie == null
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy
                    || IsFireZombie(zombie)
                    || IsBossZombie(zombie))
                {
                    continue;
                }
                var offset = zombie.transform.position - position;
                if (Mathf.Abs(offset.y) > 2f)
                {
                    continue;
                }
                offset.y = 0f;
                var distance = offset.magnitude;
                if (distance <= bestDistance
                    && (distance < 0.3f || Vector3.Dot(offset / distance, heading) >= 0.5f))
                {
                    best = zombie;
                    bestDistance = distance;
                }
            }
            return best;
        }

        // ---- 5 min decision summary ----

        private int m_StatWindowSwarms;
        private int m_StatWindowSwarmRestarts;
        private float m_LastSwarmEndAt = float.NegativeInfinity;
        private int m_StatWindowLootCancels;
        private int m_StatWindowLootFastCancels;
        private int m_StatWindowLootRaceLost;
        private int m_StatWindowCombatDeclined;
        private int m_StatWindowDisengages;
        private int m_StatWindowFireFlees;
        private int m_StatWindowWaterRefuges;
        private int m_StatWindowAcidSteps;
        private int m_StatWindowUpperDown;
        private int m_StatWindowRejoins;
        private int m_StatWindowPendingResumed;
        private int m_StatWindowHordesSeen;
        private int m_StatWindowRepathBase;

        private void LogAndResetDecisionStats()
        {
            BotLog(LogLevel.Information,
                "Decisoes: cerco {Swarms} (recomecou em 30s {SwarmRestarts}); loot cancelado {Cancels} (em ate 6s {FastCancels}), adiado por zumbi chegando {RaceLost}, retomado {Pending}; "
                + "ataques recusados (horda ou janela) {Declined}; janelas depois de fugir {Disengages}; fugas do fogo {FireFlees} (agua {Water}); "
                + "passos para fora do acido {Acid}; descidas do andar de cima {Down}; hordas lembradas {Hordes}; rotas jogadas fora {Repaths}, reaproveitadas {Rejoins}.",
                m_StatWindowSwarms,
                m_StatWindowSwarmRestarts,
                m_StatWindowLootCancels,
                m_StatWindowLootFastCancels,
                m_StatWindowLootRaceLost,
                m_StatWindowPendingResumed,
                m_StatWindowCombatDeclined,
                m_StatWindowDisengages,
                m_StatWindowFireFlees,
                m_StatWindowWaterRefuges,
                m_StatWindowAcidSteps,
                m_StatWindowUpperDown,
                m_StatWindowHordesSeen,
                m_OffLineRepaths - m_StatWindowRepathBase,
                m_StatWindowRejoins);
            m_StatWindowSwarms = 0;
            m_StatWindowSwarmRestarts = 0;
            m_StatWindowLootCancels = 0;
            m_StatWindowLootFastCancels = 0;
            m_StatWindowLootRaceLost = 0;
            m_StatWindowPendingResumed = 0;
            m_StatWindowCombatDeclined = 0;
            m_StatWindowDisengages = 0;
            m_StatWindowFireFlees = 0;
            m_StatWindowWaterRefuges = 0;
            m_StatWindowAcidSteps = 0;
            m_StatWindowUpperDown = 0;
            m_StatWindowHordesSeen = 0;
            m_StatWindowRepathBase = m_OffLineRepaths;
            m_StatWindowRejoins = 0;
        }

        private static bool IsThreatObjective(ObjectiveType type)
        {
            return type == ObjectiveType.Flee
                || type == ObjectiveType.EscapeSwarm
                || type == ObjectiveType.EscapeHazard
                || type == ObjectiveType.RepositionFire
                || type == ObjectiveType.ExitWater;
        }

        private void ResetThreatState()
        {
            m_MarkedSeenZombies.Clear();
            m_ThreatHunters8 = 0;
            m_ThreatHunters20 = 0;
            m_ThreatNearestHunterDistance = float.PositiveInfinity;
            m_ThreatNearestHunterEta = float.PositiveInfinity;
            m_ThreatFireDistance = float.PositiveInfinity;
            m_ThreatHuntingFireDistance = float.PositiveInfinity;
            m_DisengageUntil = 0f;
            m_DisengageFire = null;
            m_FireFleeZombie = null;
            m_FireLastResortZombie = null;
            m_FireFleeEndedAt = float.NegativeInfinity;
            m_FireWaterRefugeUntil = 0f;
            m_PendingSpitReactionAt = -1f;
            m_PendingLootId = 0;
            m_RaceDeferredLoot.Clear();
            m_HordeSpots.Clear();
            m_TrailPoints.Clear();
            m_TrailTimes.Clear();
        }
    }
}
