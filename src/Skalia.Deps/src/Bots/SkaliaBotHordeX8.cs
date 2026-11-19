using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x8 (doc 16.16): the horde deaths of the x7 test. 4 of 8 deaths
    // came with EscapeSwarm as the objective: the knife out against 3-4
    // zombies, the small-group fight taken with a magazine that ran out on
    // the second zombie, and an escape that stood still (no stamina, blocked)
    // with a zombie on the bot until it died.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float HordeCrowdMeters = 8f;
        private const float HordeKeepBladeMeters = 3f;
        private const float SwarmStallSeconds = 2f;
        private const float SwarmStallMoveMeters = 0.75f;
        private const float SwarmStallStamina = 10f;

        private Vector3 m_SwarmStallAnchor;
        private float m_SwarmStallSince = -1f;
        private float m_NextHordeBladeLogAt;
        private float m_NextSmallGroupSkipLogAt;

        // Zombies within 8m (the target included).
        private int CountZombiesWithin(Vector3 position, float meters)
        {
            var count = 0;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie != null
                    && !zombie.isDead
                    && zombie.gameObject.activeInHierarchy
                    && (zombie.transform.position - position).GetHorizontalMagnitude() <= meters)
                {
                    count++;
                }
            }
            return count;
        }

        private static int LoadedShots(ItemGunAsset gun, byte[]? state)
        {
            if (gun.infiniteAmmo)
            {
                return 99;
            }
            return state != null && state.Length > 10 ? state[10] / Mathf.Max(1, (int)gun.ammoPerShot) : 0;
        }

        // The shots already in the best gun the bot carries (the equipped one
        // from its live state).
        private int GetBestLoadedGunShots()
        {
            var equipment = m_Player.equipment;
            var best = 0;
            if (equipment.equippedPage < PlayerInventory.SLOTS
                && equipment.asset is ItemGunAsset handGun
                && IsUsefulGun(handGun)
                && IsEquippedGunLoaded(handGun))
            {
                best = LoadedShots(handGun, equipment.state);
            }
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null
                        || !(jar.GetAsset() is ItemGunAsset gun)
                        || !IsUsefulGun(gun)
                        || !CanBecomeHoldable(page, gun)
                        || (equipment.equippedPage == page && equipment.equipped_x == jar.x && equipment.equipped_y == jar.y)
                        || IsKnownEmptyGunSlot(page, jar.x, jar.y))
                    {
                        continue;
                    }
                    best = Mathf.Max(best, LoadedShots(gun, jar.item.state));
                }
            }
            return best;
        }

        // Item 3b: 3-4 common zombies are fought only with a gun holding at
        // least one shot per zombie plus two; with the knife, at most 2.
        private bool HasShotsForSmallGroup(int count, float now)
        {
            if (count < SurroundedZombieCount)
            {
                return true;
            }
            var shots = GetBestLoadedGunShots();
            if (shots >= count + 2)
            {
                return true;
            }
            // 0.2.2x13: the gun's shots do not decide a fight the blade takes.
            // llo, Katana in hand and 1 round in the Zubeknakov, ran from 3
            // common zombies, burned his stamina and was caught by 4. In the
            // players' recordings 2-3 zombies are cleared 80% of the time, the
            // commons mostly with the blade (1.7 swings a kill).
            if (count <= SmallGroupMaxZombies
                && BestCarriedMeleeDamage() >= 40f
                && m_Player.life.stamina >= 50
                && m_Player.life.health >= 60)
            {
                return true;
            }
            // 0.2.2x14: the stun kite (up to 5, the tester) needs a blade that
            // stuns and the stamina the players start its sprints with (33,
            // p10). thigas, full health, knife, 3 commons, stamina 45, ran
            // and died. (Health 50+ is already asked by ShouldFightSmallGroup.)
            if (Tryhard
                && count <= StunKiteMaxChasers
                && BestCarriedMeleeStuns()
                && m_Player.life.stamina >= SwapSprintStamina)
            {
                return true;
            }
            if (now >= m_NextSmallGroupSkipLogAt)
            {
                m_NextSmallGroupSkipLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "{Count} zumbis comuns a {Radius:0}m e so {Shots} tiro(s) carregado(s) (precisa {Need}): nao encara o grupo, foge do cerco (arma na mao {Weapon}).",
                    count,
                    SmallGroupRadiusMeters,
                    shots,
                    count + 2,
                    DescribeEquipped());
            }
            return false;
        }

        // Item 3a: with 2+ zombies within 8m a loaded gun wins over the knife,
        // unless the knife is already in hand with a zombie within 3m (the
        // swap would leave the bot empty-handed in reach).
        // Returns null when the usual choice applies.
        private bool? HordeBladeOverride(Zombie target)
        {
            var position = m_Player.transform.position;
            var crowd = CountZombiesWithin(position, HordeCrowdMeters);
            // A gun with a shot for each of them and one to spare; a lone
            // crossbow bolt stays for the burner (RangedReserveShots).
            if (crowd < 2 || GetBestLoadedGunShots() < crowd + 1)
            {
                return null;
            }
            var bladeInHand = m_Player.equipment.asset is ItemMeleeAsset;
            var keepBlade = bladeInHand && m_CachedNearestZombieDistance <= HordeKeepBladeMeters;
            if (m_BehaviorNow >= m_NextHordeBladeLogAt)
            {
                m_NextHordeBladeLogAt = m_BehaviorNow + 10f;
                BotLog(LogLevel.Information,
                    "{Crowd} zumbis a {Radius:0}m: {Choice} (zumbi mais perto a {Nearest:0.#}m, alvo {Kind}).",
                    crowd,
                    HordeCrowdMeters,
                    keepBlade ? "segue na faca, perto demais para trocar" : "na bala",
                    m_CachedNearestZombieDistance,
                    DescribeZombieKind(target));
            }
            return keepBlade;
        }

        // Item 3c: the swarm escape that stands still (blocked or out of
        // breath) with a zombie on the bot gives way to a 4s fight, the same
        // way the x7 flee does (IsFleeYielding stops the swarm, low health and
        // far Mega flights).
        private bool TryYieldSwarmToFight(Vector3 position, float now)
        {
            if (m_SwarmStallSince < 0f
                || (position - m_SwarmStallAnchor).GetHorizontalMagnitude() > SwarmStallMoveMeters)
            {
                m_SwarmStallAnchor = position;
                m_SwarmStallSince = now;
            }
            if (m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return false;
            }
            var nearest = m_CachedNearestZombieDistance;
            if (nearest > CaughtByZombieMeters)
            {
                return false;
            }
            if (m_CachedNearestBoss != null
                && !m_CachedNearestBoss.isDead
                && (m_CachedNearestBossDistance <= nearest + 0.1f
                    || m_CachedNearestBossDistance < MegaYieldMeters))
            {
                return false;
            }
            var stamina = m_Player.life.stamina;
            var stalled = now - m_SwarmStallSince >= SwarmStallSeconds;
            var breathless = stamina < SwarmStallStamina;
            if ((!stalled && !breathless) || !HasUsableCombatEquipment())
            {
                return false;
            }
            // 0.1.0-y17.12: the fight goes on until they are dead, not 4s
            // (SkaliaBotHordeCommitY); the escape ends here.
            BotLog(LogLevel.Information,
                "Fuga do cerco trocada por luta ate o fim ({Why}): zumbi a {Distance:0.#}m; {Count} zumbis a 8m; vida {Health}; stamina {Stamina}; tiros carregados {Shots}; arma {Weapon}.",
                stalled ? "parado ha " + (now - m_SwarmStallSince).ToString("0.#") + "s" : "sem stamina",
                nearest,
                CountZombiesWithin(position, HordeCrowdMeters),
                m_Player.life.health,
                stamina,
                GetBestLoadedGunShots(),
                DescribeEquipped());
            m_FleeYieldUntil = now + FleeYieldSeconds;
            m_StandGroundUntil = Mathf.Max(m_StandGroundUntil, now + StandGroundSeconds);
            m_SwarmStallSince = -1f;
            m_LowHealthFleeActive = false;
            m_SwarmEscapeActive = false;
            m_SwarmJumpUntil = 0f;
            m_LastSwarmEndAt = now;
            StartHordeFightCommit(stalled ? "a fuga travou" : "fuga sem stamina", now);
            ClearObjective();
            // The fight starts on the zombie that is on the bot (the cautious
            // combat only takes a target already in hand).
            if (TryFindNearestZombie(position, out var onBot))
            {
                m_TargetZombie = onBot;
            }
            return true;
        }
    }
}
