using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2t: loot in one pass. Everything worth taking within a few meters
    // of the bot is taken, worn and torn before it walks on; a lone common
    // zombie standing over the loot is killed instead of waited out; an item
    // that cannot be reached is given up sooner.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float LootPileMeters = 8f;
        private const float LootPileLingerSeconds = 0.6f;
        private const float LootGuardItemMeters = 15f;
        private const float LootGuardBotMeters = 12f;
        private const float LootGuardSeconds = 20f;
        private const float LootGuardChaseMeters = 35f;
        private const float TryhardLootStuckSeconds = 6f;
        private const float TryhardLootStuckMeters = 6f;
        private const float ValuableLootIgnoreSeconds = 120f;

        private bool m_LootScanPileOnly;
        private int m_LootPileCount;
        private float m_LootPileStartedAt;
        private float m_LootPileLastAt;
        private float m_LootPileUntil;
        private Vector3 m_LootProgressOrigin;
        private uint m_LootSecondChanceId;

        private Zombie? m_LootGuardZombie;
        private float m_LootGuardUntil;
        private Vector3 m_LootGuardPoint;
        private float m_LootGuardNotedAt = float.NegativeInfinity;

        private bool IsLootPileActive(float now)
        {
            return Tryhard && now < m_LootPileUntil;
        }

        // The scan of TryFindNearestLoot limited to what lies around the bot.
        private bool TryFindPileLoot(
            Vector3 position,
            float now,
            out byte regionX,
            out byte regionY,
            out ItemData item)
        {
            m_LootScanPileOnly = true;
            try
            {
                return TryFindNearestLoot(position, now, false, out regionX, out regionY, out item);
            }
            finally
            {
                m_LootScanPileOnly = false;
            }
        }

        private bool IsOutsideLootPile(Vector3 itemPoint, Vector3 position)
        {
            if (!m_LootScanPileOnly)
            {
                return false;
            }
            var center = m_LootPileCenterSet ? m_LootPileCenter : position;
            var radius = m_LootPileCenterSet ? m_LootPileRadius : LootPileMeters;
            var offset = itemPoint - center;
            return Mathf.Abs(offset.y) > (m_LootPileCenterSet ? 4f : 3f)
                || offset.x * offset.x + offset.z * offset.z > radius * radius;
        }

        // 0.2.2v (doc 13.3, item 3): the pile around a given point (the body
        // of the Mega), scanned from where the bot stands.
        private const float MegaDropReachMeters = 90f;
        private const float MegaDropSeconds = 90f; // 0.2.2x (doc 15.2, item 13): was 45s
        private const float MegaDropPileMeters = 10f;
        private bool m_MegaDropPending;
        private int m_MegaDropPicked;
        // 0.2.2y11: whose pile it is, for the log (the Mega's or a man's).
        private string m_DropPileName = "do Mega";
        private bool m_LootPileCenterSet;
        private Vector3 m_LootPileCenter;
        private float m_LootPileRadius;

        private bool TryFindLootAround(
            Vector3 center,
            float radius,
            Vector3 position,
            float now,
            out byte regionX,
            out byte regionY,
            out ItemData item)
        {
            m_LootScanPileOnly = true;
            m_LootPileCenterSet = true;
            m_LootPileCenter = center;
            m_LootPileRadius = radius;
            try
            {
                return TryFindNearestLoot(position, now, false, out regionX, out regionY, out item);
            }
            finally
            {
                m_LootScanPileOnly = false;
                m_LootPileCenterSet = false;
            }
        }

        // The places the Mega was seen keep loot off limits for 2 min; once
        // it is dead those around its body are dropped (they also covered
        // its own drop). Another living Mega nearby keeps them.
        private static void NoteMegaKilled(Vector3 point)
        {
            if (ZombieManager.regions != null)
            {
                foreach (var region in ZombieManager.regions)
                {
                    if (region?.zombies == null)
                    {
                        continue;
                    }
                    foreach (var zombie in region.zombies)
                    {
                        if (zombie != null
                            && zombie.isMega
                            && !zombie.isDead
                            && zombie.gameObject.activeInHierarchy
                            && (zombie.transform.position - point).GetHorizontalMagnitude() < 150f)
                        {
                            return;
                        }
                    }
                }
            }
            s_BossZones.RemoveAll(zone => (zone.Key - point).GetHorizontalMagnitude() < 120f);
        }

        // After a pickup: look again at once, and let the new piece be worn
        // on the next upkeep pass so its pockets count for the next item.
        private void NoteLootPilePickup(float now)
        {
            if (!Tryhard)
            {
                return;
            }
            // 0.2.2v (doc 13.3, item 8): one line per pile of 2 or more, so
            // the test shows the pile at work.
            if (now >= m_LootPileUntil)
            {
                if (m_LootPileCount >= 2)
                {
                    BotLog(LogLevel.Information,
                        "Monte de loot: {Count} itens pegos em {Seconds:0.#}s sem sair dali.",
                        m_LootPileCount,
                        m_LootPileLastAt - m_LootPileStartedAt);
                }
                m_LootPileCount = 0;
                m_LootPileStartedAt = now;
            }
            m_LootPileCount++;
            m_LootPileLastAt = now;
            m_LootPileUntil = now + LootPileLingerSeconds;
            m_NextLootScanTime = Mathf.Min(m_NextLootScanTime, now + 0.1f);
            m_NextBagTidyAt = Mathf.Min(m_NextBagTidyAt, now);
            m_NextHeadgearTidyAt = Mathf.Min(m_NextHeadgearTidyAt, now);
            m_NextGearWorkAt = Mathf.Min(m_NextGearWorkAt, now);
        }

        private bool IsValuableLoot(ItemAsset asset)
        {
            switch (asset.type)
            {
                case EItemType.GUN:
                    return asset is ItemGunAsset gun && IsUsefulGun(gun);
                case EItemType.MAGAZINE:
                case EItemType.MEDICAL:
                case EItemType.SIGHT:
                case EItemType.TACTICAL:
                case EItemType.GRIP:
                case EItemType.BARREL:
                    return true;
                case EItemType.SUPPLY:
                    return IsAmmoBox(asset.id);
                case EItemType.BACKPACK:
                case EItemType.VEST:
                case EItemType.SHIRT:
                case EItemType.PANTS:
                case EItemType.HAT:
                case EItemType.MASK:
                case EItemType.GLASSES:
                    return asset is ItemClothingAsset clothing && clothing.armor < 0.96f;
                default:
                    return false;
            }
        }

        // Seconds without getting a meter closer before the item is given
        // up. A bot still covering ground (a route around a building) keeps
        // the old 15s; one that is going nowhere gives up after 6s.
        private float GetLootProgressTimeout(Vector3 position)
        {
            // Within reach the pickup has its own clock (look, then take).
            if (!Tryhard
                || (m_ObjectivePosition - position).GetHorizontalMagnitude()
                   <= Mathf.Max(m_LootPickupDistanceMeters, LootSightReachMeters) + 1.5f)
            {
                return LootProgressTimeoutSeconds;
            }
            return (position - m_LootProgressOrigin).GetHorizontalMagnitude() < TryhardLootStuckMeters
                ? TryhardLootStuckSeconds
                : LootProgressTimeoutSeconds;
        }

        // True when the item gets one more try by another route.
        private bool TryGiveLootSecondChance(ItemAsset asset, Vector3 position, float now, float distance)
        {
            if (!Tryhard
                || m_LootSecondChanceId == m_LootInstanceId
                || !IsValuableLoot(asset))
            {
                return false;
            }
            m_LootSecondChanceId = m_LootInstanceId;
            m_LootProgressSince = now;
            m_LootProgressOrigin = position;
            m_LootProgressBestDistance = distance;
            ClearPath();
            BotLog(LogLevel.Information,
                "Loot {Item} sem progresso a {Distance:0.#}m; refazendo a rota uma vez antes de desistir.",
                asset.itemName,
                distance);
            return true;
        }

        private float GetUnreachableLootIgnoreSeconds(ItemAsset asset)
        {
            return Tryhard && IsValuableLoot(asset)
                ? ValuableLootIgnoreSeconds
                : UnreachableLootIgnoreSeconds;
        }

        // ---- lone zombie standing over the loot ----

        private void NoteLootGuarded(Vector3 lootPoint, float now)
        {
            if (!Tryhard)
            {
                return;
            }
            m_LootGuardPoint = lootPoint;
            m_LootGuardNotedAt = now;
        }

        private bool IsLootGuardZombie(Zombie zombie)
        {
            return m_LootGuardZombie != null
                && zombie == m_LootGuardZombie
                && m_BehaviorNow < m_LootGuardUntil;
        }

        private void UpdateLootGuardKill(Vector3 position, float now)
        {
            if (!Tryhard)
            {
                return;
            }
            if (m_LootGuardZombie != null)
            {
                var guard = m_LootGuardZombie;
                if (guard.isDead || !guard.gameObject.activeInHierarchy)
                {
                    // Dead: the loot it was holding is looked at right away.
                    m_LootGuardZombie = null;
                    m_NextLootScanTime = Mathf.Min(m_NextLootScanTime, now + 0.3f);
                    m_RaceDeferredLoot.Clear();
                }
                else if (now >= m_LootGuardUntil)
                {
                    m_LootGuardZombie = null;
                }
                return;
            }
            if (now - m_LootGuardNotedAt > 2f)
            {
                return;
            }
            // One look per cancelled loot.
            m_LootGuardNotedAt = float.NegativeInfinity;
            if (IsUnarmed()
                || IsLowHealth()
                || IsHordeCaution()
                || m_TreatmentPhase != 0
                || IsFightObjective()
                || IsDisengaging(now))
            {
                return;
            }
            Zombie? only = null;
            var count = 0;
            foreach (var entry in m_PerceivedZombies)
            {
                if (!ConsiderLootGuard(entry.Key, position, ref only, ref count))
                {
                    return;
                }
            }
            foreach (var entry in m_MarkedSeenZombies)
            {
                if (entry.Key != only && !ConsiderLootGuard(entry.Key, position, ref only, ref count))
                {
                    return;
                }
            }
            if (count != 1 || only == null)
            {
                return;
            }
            var zombiePoint = only.transform.position;
            // Not across the map for it: past 35m the fight would lose the
            // zombie and leave the bot with a target it cannot chase.
            if (Mathf.Abs(zombiePoint.y - position.y) > 2.5f
                || (zombiePoint - position).GetHorizontalMagnitude() > LootGuardChaseMeters
                || IsNearKnownBoss(zombiePoint)
                || IsWithinMegaReach(zombiePoint)
                || IsZombieMarkedUnreachable(only, now)
                || !IsCombatTargetAllowed(only, position, now, out _))
            {
                return;
            }
            m_LootGuardZombie = only;
            m_LootGuardUntil = now + LootGuardSeconds;
            if (m_TargetZombie != only)
            {
                m_TargetZombie = only;
                m_StatWindowFights++;
                m_CombatMovementPhase = CombatMovementPhase.Approach;
                m_CombatRetreatEndsAt = 0f;
                m_CombatRetreatPending = false;
                m_CombatAttackRecoveryUntil = 0f;
                m_CombatAttackFailsafeAt = 0f;
                m_MeleeBurstAttackCount = 0;
                m_MeleeBurstTargetCount = Random.Range(1, 3);
                m_CombatRetreatShouldSprint = false;
            }
            BotLog(LogLevel.Information,
                "Zumbi sozinho a {Distance:0.#}m guardando o loot em {Loot}: matando antes de pegar.",
                (zombiePoint - position).GetHorizontalMagnitude(),
                FormatPosition(m_LootGuardPoint));
        }

        // False when the scene is not "one common zombie": stop looking.
        private bool ConsiderLootGuard(Zombie zombie, Vector3 position, ref Zombie? only, ref int count)
        {
            if (zombie == null || zombie.isDead || !zombie.gameObject.activeInHierarchy)
            {
                return true;
            }
            var point = zombie.transform.position;
            if ((point - m_LootGuardPoint).GetHorizontalMagnitude() > LootGuardItemMeters
                && (point - position).GetHorizontalMagnitude() > LootGuardBotMeters)
            {
                return true;
            }
            if (!IsCommonZombie(zombie) || IsBossZombie(zombie) || IsFireZombie(zombie))
            {
                return false;
            }
            if (zombie != only)
            {
                count++;
                only = zombie;
            }
            return count <= 1;
        }
    }
}
