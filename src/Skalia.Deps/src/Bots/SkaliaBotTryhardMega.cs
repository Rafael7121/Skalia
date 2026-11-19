using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2u (doc 12.2, 12.3): the Mega, after the first 0.2.2t test.
    //
    // The hunt (XXlloqiartyXX in Seattle, 13:47:45): 56s and not one shot.
    // The distance the gun fight looked for (14m) was inside the distance
    // the hunt backed off at (15m, released at 22m); backing off was a Flee
    // with the back turned, to a point that usually lost the line; the Mega
    // walks 6 m/s against 7 sprinting, so the 22m never opened; a common
    // zombie within 6m put the knife in hand with the Mega on the bot.
    // Now, like a player: shoot from wherever there is a line, walk backwards
    // shooting while it comes (it gains 1.5 m/s on a bot walking 4.5), turn
    // and run only with it within 8m or with the magazine out, reloading on
    // the run, and face it again at 16m or loaded.
    //
    // The base (thigas at Olympia, 13:45:43-13:47:32): 7 flights from a Mega
    // that never saw him, 35m away. The game notices a player at 22m
    // sprinting, 13m walking, 6.6m crouched. Now the tryhard only runs from
    // one that is after him or within 10m, walks within 30m of one that is
    // not, and crouches within 18m.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float TryhardMegaBreakMeters = 8f;
        private const float TryhardMegaResumeMeters = 16f;
        private const float TryhardMegaReloadRunMeters = 20f;
        private const float TryhardMegaReloadChasedMeters = 45f;
        private const float TryhardMegaKeepMeters = 30f;
        private const float TryhardMegaCommonMeters = 4f;
        private const float TryhardMegaGunReadyMeters = 120f;
        private const float TryhardMegaFleeMeters = 10f;
        private const float TryhardMegaWalkMeters = 30f;
        private const float TryhardMegaCrouchMeters = 18f;
        private const float TryhardMegaLootMeters = 10f;

        private float m_NextMegaApproachCheckAt;
        private float m_NextMegaRunLogAt;
        private float m_NextMegaSneakLogAt;
        private bool m_MegaSneakWalk;
        private bool m_MegaSneakCrouch;

        private bool IsBossAfterBot(Zombie boss)
        {
            return boss.isHunting && boss.player == m_Player;
        }

        // The distance kept from the zombie with a gun in hand.
        private float GetGunKeepDistance(Zombie target, float attackDistance)
        {
            return Tryhard && IsHuntedBoss(target)
                ? Mathf.Min(attackDistance, TryhardMegaKeepMeters)
                : Mathf.Clamp(attackDistance, 8f, 14f);
        }

        // With a line to the hunted Mega and a gun in hand the combat
        // steering moves the bot (keeps the distance, walks backwards);
        // the route to the Mega's feet would walk it in while reloading.
        private bool IsTryhardMegaGunFight(bool hasLineOfSight)
        {
            return Tryhard
                && hasLineOfSight
                && IsHuntedBoss(m_TargetZombie)
                && m_Player.equipment.asset is ItemGunAsset;
        }

        // During the hunt everything is shot with the gun in hand.
        private bool IsTryhardHuntShot()
        {
            return Tryhard && m_BossHuntActive && m_Player.equipment.asset is ItemGunAsset;
        }

        private BotBehaviorStatus TickTryhardBossHunt(Zombie boss, Vector3 position, float distance, float now)
        {
            EquipBossHuntGun(now);
            var equipment = m_Player.equipment;
            var handGun = equipment.asset as ItemGunAsset;
            var useable = equipment.useable as UseableGun;
            var reloading = useable != null && useable.isReloading;
            var loaded = handGun != null && IsEquippedGunLoaded(handGun) && !reloading;
            // 0.2.2x1 (doc 16.2, 16.3 item 4): the players back off facing the
            // Mega and shooting; at about 5m (2-10m) they turn and sprint for
            // about 1s (0.5-2.3s), gain 2-6m and turn to shoot again, hardly
            // losing health. llo backed off facing it from 7.9m to 1m and took
            // 28 (22:32). Loaded, the bot now does the same: a short run with
            // its back turned, then it faces the Mega again.
            var turnRun = TryUpdateMegaTurnRun(loaded, distance, now);
            // 0.2.2x2 (doc 16.7, item 6): llo went into the Mega at 01:57
            // with 8 rounds left and ran dry at 6m. With it 20m or more away
            // and the magazine under a third, the bot swaps it now, and the
            // reload run below turns its back to reposition meanwhile.
            if (loaded
                && handGun != null
                && useable != null
                && handGun.allowMagazineChange
                && distance >= TryhardMegaReloadRunMeters
                && useable.ammo < Mathf.CeilToInt(handGun.ammoMax * 0.3f)
                && TryTopOffEquippedGun(handGun, useable.ammo, now, "com o Mega a " + distance.ToString("0") + "m"))
            {
                loaded = false;
                reloading = true;
            }
            var tooClose = turnRun
                || (!loaded
                    && (distance < TryhardMegaBreakMeters
                        || (m_BossHuntKiting && distance < TryhardMegaResumeMeters)));
            if (loaded && !turnRun && distance < TryhardMegaBreakMeters && now >= m_NextMegaRunLogAt)
            {
                m_NextMegaRunLogAt = now + 4f;
                BotLog(LogLevel.Information,
                    "Mega a {Distance:0.#}m: recuando de frente para ele e atirando (vida {Health}).",
                    distance,
                    m_Player.life.health);
            }
            // Not loaded (reloading, empty, gun still coming to the hand):
            // run within 20m, and within 45m of one that is coming.
            var notReady = !loaded
                && (distance < TryhardMegaReloadRunMeters
                    || (distance < TryhardMegaReloadChasedMeters && IsBossAfterBot(boss)));
            if (tooClose || notReady)
            {
                if (now >= m_NextMegaRunLogAt)
                {
                    m_NextMegaRunLogAt = now + 4f;
                    BotLog(LogLevel.Information,
                        "Mega a {Distance:0.#}m: correndo ({Reason}); volta a atirar a {Resume:0}m ou com o pente cheio (vida {Health}).",
                        distance,
                        turnRun ? "virada curta para abrir distancia" : tooClose ? "perto demais" : handGun == null ? "arma ainda nao esta na mao" : reloading ? "recarregando" : "pente vazio",
                        TryhardMegaResumeMeters,
                        m_Player.life.health);
                }
                m_BossHuntKiting = tooClose && !turnRun;
                if (handGun != null && !reloading && !IsEquippedGunLoaded(handGun))
                {
                    TryReloadEquippedGun(handGun, now);
                }
                if (now >= m_NextBossFleeReplanAt
                    && (m_ObjectiveType != ObjectiveType.Flee
                        || now >= m_ObjectiveExpiresAt
                        || (m_ObjectivePosition - position).GetHorizontalMagnitude() <= 4f))
                {
                    m_NextBossFleeReplanAt = now + 1f;
                    if (TryFindFireEscapePoint(position, boss, now, out var escape)
                        || TryFindLowHealthEscapePoint(position, now, out escape))
                    {
                        m_FleeingFromPlayerName = "o Mega (recuo da caca)";
                        SetObjective(ObjectiveType.Flee, escape, now + 6f);
                    }
                }
                return BotBehaviorStatus.Running;
            }
            m_BossHuntKiting = false;
            EndMegaTurnRunObjective();
            if (TryFindHuntCommonThreat(position, boss, distance, now, out var closeZombie, out var why))
            {
                if (m_TargetZombie != closeZombie && now >= m_NextBossHuntMeleeLogAt)
                {
                    m_NextBossHuntMeleeLogAt = now + 5f;
                    BotLog(LogLevel.Information,
                        "Zumbi comum a {Distance:0.#}m na caca ao Mega (Mega a {Mega:0}m, {Why}); matando ele primeiro com a arma que esta na mao.",
                        (closeZombie.transform.position - position).GetHorizontalMagnitude(),
                        distance,
                        why);
                }
                ForceCombatTarget(closeZombie, now);
                return BotBehaviorStatus.Running;
            }
            m_HuntCommonTarget = null;
            ForceCombatTarget(boss, now);
            return BotBehaviorStatus.Running;
        }

        private bool TryFindNearestLivingMega(Vector3 position, float maximumMeters, out Zombie mega, out float distance)
        {
            mega = null!;
            distance = maximumMeters;
            if (ZombieManager.regions == null)
            {
                return false;
            }
            foreach (var region in ZombieManager.regions)
            {
                if (region?.zombies == null)
                {
                    continue;
                }
                foreach (var zombie in region.zombies)
                {
                    if (zombie == null
                        || zombie.isDead
                        || !zombie.isMega
                        || !zombie.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    var gap = (zombie.transform.position - position).GetHorizontalMagnitude();
                    if (gap < distance)
                    {
                        distance = gap;
                        mega = zombie;
                    }
                }
            }
            return mega != null;
        }

        // The gun for the Mega in hand 120m before it, instead of 5s into
        // the hunt. A fight with a common zombie on the way still takes the
        // melee; the gun comes back when it is over.
        private void UpdateMegaApproachGun(Vector3 position, float now)
        {
            if (!Tryhard || m_BossHuntActive || now < m_NextMegaApproachCheckAt)
            {
                return;
            }
            m_NextMegaApproachCheckAt = now + 1.5f;
            var equipment = m_Player.equipment;
            if (now < m_BossHuntBlockedUntil
                || m_TreatmentPhase != 0
                || equipment.isBusy
                || m_ObjectiveType == ObjectiveType.Combat
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.Flee
                || m_CachedNearestZombieDistance < 8f
                || m_Player.stance.stance == EPlayerStance.SWIM
                || !TryFindNearestLivingMega(position, TryhardMegaGunReadyMeters, out var mega, out var megaDistance)
                || !CanJoinBossHunt(mega, now, out var damage, out _))
            {
                return;
            }
            var team = GetBossHuntTeamDamage(mega, damage);
            var gun = m_BossHuntCandidateGun;
            if (gun == null
                || equipment.asset == gun
                || damage < mega.health * BossHuntOwnShare
                || team < mega.health * GetBossHuntMargin())
            {
                return;
            }
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar == null || jar.GetAsset() != gun)
                    {
                        continue;
                    }
                    if (!ItemTool.checkUseable(page, jar.item.id))
                    {
                        TryMoveToWeaponSlot(page, jar, gun);
                        return;
                    }
                    equipment.ServerEquip(page, jar.x, jar.y);
                    BotLog(LogLevel.Information,
                        "Mega a {Distance:0}m: {Gun} na mao antes de chegar nele.",
                        megaDistance,
                        gun.itemName);
                    return;
                }
            }
        }

        // ---- a Mega that has not seen the bot (doc 12.3) ----

        // Run from it only when it is after the bot or right there.
        private bool IsTryhardBossFleeWanted(Zombie boss, float distance)
        {
            // 0.2.2x8: or one walking at the bot (SkaliaBotMegaX8).
            return distance <= TryhardMegaFleeMeters || IsBossAfterBot(boss) || IsMegaClosing(boss);
        }

        private float GetMegaNoLureMeters()
        {
            // 0.2.2x1: 10m only next to a Mega the bot could take on.
            // 0.2.2x13: 20m without an automatic (was 55m: the bots left the
            // loot of whole blocks next to a Mega that had not seen them).
            return Tryhard ? (HasAutoGunCached() ? TryhardMegaLootMeters : 20f) : BossNoLureMeters;
        }

        // Called every tick: no sprint within 30m of a Mega that is not
        // after the bot, crouched within 18m.
        private void UpdateMegaSneak(float now)
        {
            m_MegaSneakWalk = false;
            m_MegaSneakCrouch = false;
            var boss = m_CachedNearestBoss;
            if (!Tryhard
                || boss == null
                || boss.isDead
                || m_BossHuntActive
                || m_BossFleeActive
                || IsBossAfterBot(boss)
                // A zombie after the bot: it runs or fights as always.
                || GetNearestAggroedZombieDistance() < 12f
                || m_ObjectiveType == ObjectiveType.Combat
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeHazard)
            {
                return;
            }
            var distance = m_CachedNearestBossDistance;
            m_MegaSneakWalk = distance <= TryhardMegaWalkMeters;
            m_MegaSneakCrouch = distance <= TryhardMegaCrouchMeters;
            if (m_MegaSneakWalk && now >= m_NextMegaSneakLogAt)
            {
                m_NextMegaSneakLogAt = now + 20f;
                BotLog(LogLevel.Information,
                    "{Boss} a {Distance:0}m sem ter visto o bot: {How} para nao chamar ele (objetivo \"{Objective}\").",
                    DescribeBoss(boss),
                    distance,
                    m_MegaSneakCrouch ? "andando agachado" : "andando sem correr",
                    m_ObjectiveType);
            }
        }
    }
}
