using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x14: the x13 test (3h20, 24 deaths to zombies, 16 of them out of
    // stamina) against the players' recordings (24 files, 11.4h on
    // Skalia3). Every number here comes from the tester or from those
    // recordings ("seguindo" in them: zombies after the player within 40m).
    //
    // - the tester: "ate 4~5 zumbis da pra fazer o kite com stun". With a blade
    //   and 2-5 after them, one within 2m, the players stand 48% of the time,
    //   step back 29%, sprint 14% and swing 29%; after a hit they step back
    //   within 0.7s 77% of the time with one after them, 87% with two and
    //   97% with three or more; 71% of their hits land on the nearest zombie.
    // - the tester: "mais que isso vale a pena so sair de perto ou descarregar o
    //   pente". With 6+ after them the players run 59% of the time and swing
    //   4%; they start running with the nearest at 1.9m (median, p75 3.6m).
    // - the tester: "tudo bem trocarem de estilo de luta mas facam isso
    //   recuando". 89% of the players' 405 fights use one weapon kind; the
    //   25 swaps with a zombie within 8m were all made moving (16 sprinting,
    //   the rest stepping back), with no hit taken in the next second. The
    //   bots drew the style again for every new zombie (thigas: 97 knife
    //   draws in 3h20).
    // - the tester: "7~10 zumbis ao redor ele atira em um e chama atencao do
    //   restante". With 7+ zombies within 15m the players swing or shoot
    //   0.16 times a second and run 45% of the time; the bots 0.34.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const int StunKiteMaxChasers = 5;
        private const float ChaserMeters = 40f;
        private const int BigGroupCount = 6;
        private const float BigGroupLeaveNearest = 3.6f;
        private const float SwapRetreatMeters = 8f;
        private const float SwapSprintStamina = 33f;
        private const float EncounterGapSeconds = 5f;
        private const int CrowdAroundCount = 7;
        private const float CrowdAroundMeters = 15f;
        private const int StepBackChasers = 3;

        private float m_EncounterUntil;
        private float m_NextStunRetargetAt;
        private float m_NextSwapKeepLogAt;
        private float m_NextSwapRetreatLogAt;
        private float m_NextBigGroupLogAt;
        private float m_NextSleepingHordeLogAt;
        private float m_NextQuietBladeLogAt;

        // Zombies hunting this bot within the radius.
        private int CountChasers(Vector3 position, float radius, out float nearest)
        {
            var count = 0;
            nearest = float.PositiveInfinity;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy
                    || !IsHuntingBot(zombie))
                {
                    continue;
                }
                var offset = zombie.transform.position - position;
                var distance = offset.GetHorizontalMagnitude();
                if (distance > radius || Mathf.Abs(offset.y) > 3.5f)
                {
                    continue;
                }
                count++;
                nearest = Mathf.Min(nearest, distance);
            }
            return count;
        }

        // The blade fight of up to 5: hit, step out of reach, hit.
        private bool IsStunKiteCrowd(Vector3 position)
        {
            return Tryhard
                && HasStunningMeleeInHand(out _)
                && CountChasers(position, ChaserMeters, out _) <= StunKiteMaxChasers;
        }

        // ---- the weapon swap ----
        // One weapon kind for the whole fight: while zombies are after the
        // bot (and 5s after the last one) the weapon in hand stays when it
        // can fight. Null when a swap is fine.
        private bool? KeepHandWeaponForSwap(Zombie target)
        {
            if (!Tryhard)
            {
                return null;
            }
            var position = m_Player.transform.position;
            if (CountChasers(position, ChaserMeters, out _) > 0)
            {
                m_EncounterUntil = m_BehaviorNow + EncounterGapSeconds;
            }
            if (m_BehaviorNow >= m_EncounterUntil)
            {
                return null;
            }
            var hand = m_Player.equipment.asset;
            bool keepBlade;
            if (hand is ItemMeleeAsset melee && MeleeStuns(melee, m_Player.equipment.quality, null))
            {
                keepBlade = true;
            }
            else if (hand is ItemGunAsset gun
                     && IsEquippedGunLoaded(gun)
                     && !(m_Player.equipment.useable is UseableGun useable && useable.isReloading))
            {
                keepBlade = false;
            }
            else
            {
                return null;
            }
            if (m_BehaviorNow >= m_NextSwapKeepLogAt)
            {
                m_NextSwapKeepLogAt = m_BehaviorNow + 15f;
                BotLog(LogLevel.Information,
                    "Luta em andamento: segue com {Weapon} ate ela acabar, como os jogadores (89% das lutas com um tipo de arma so).",
                    DescribeEquipped());
            }
            return keepBlade;
        }

        // A swap or a reload with a zombie within 8m: done moving away,
        // sprinting with 33+ stamina (the players: 16 of 25 sprinting, the
        // rest stepping back).
        private bool TryRetreatWhileSwapping(float distance, float attackDistance, float now)
        {
            if (!Tryhard
                || m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || m_CachedNearestZombieDistance > SwapRetreatMeters)
            {
                return false;
            }
            var equipment = m_Player.equipment;
            var swapping = !equipment.IsEquipAnimationFinished
                || (equipment.useable is UseableGun gun && gun.isReloading);
            if (!swapping)
            {
                return false;
            }
            if (m_CombatMovementPhase != CombatMovementPhase.Retreat || now >= m_CombatRetreatEndsAt - 0.1f)
            {
                m_CombatMovementPhase = CombatMovementPhase.Retreat;
                m_CombatRetreatEndsAt = now + 0.5f;
                m_CombatRetreatPending = false;
                m_CombatRetreatShouldSprint = m_Player.life.stamina >= SwapSprintStamina;
                if (now >= m_NextSwapRetreatLogAt)
                {
                    m_NextSwapRetreatLogAt = now + 6f;
                    BotLog(LogLevel.Debug,
                        "{What} com zumbi a {Distance:0.#}m: recuando ate a arma ficar pronta.",
                        equipment.useable is UseableGun reloading && reloading.isReloading ? "Recarregando" : "Trocando de arma",
                        distance);
                }
            }
            return true;
        }

        // The blade hits the nearest one (71% of the players' hits with 2+
        // after them): another one in reach 0.3m nearer than the target
        // becomes the target.
        private void TryRetargetStunKite(float attackDistance, float now)
        {
            if (now < m_NextStunRetargetAt
                || m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || m_PendingMeleeRaycast)
            {
                return;
            }
            var position = m_Player.transform.position;
            if (!IsStunKiteCrowd(position))
            {
                return;
            }
            m_NextStunRetargetAt = now + 0.3f;
            var target = m_TargetZombie!;
            var reach = Mathf.Max(attackDistance, 1.6f) + 0.3f;
            var targetDistance = (target.transform.position - position).GetHorizontalMagnitude();
            Zombie? best = null;
            var bestDistance = reach;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null
                    || zombie == target
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy
                    || !IsHuntingBot(zombie)
                    || !IsStandUpZombie(zombie))
                {
                    continue;
                }
                var offset = zombie.transform.position - position;
                var distance = offset.GetHorizontalMagnitude();
                if (distance < bestDistance && Mathf.Abs(offset.y) <= 1.5f)
                {
                    bestDistance = distance;
                    best = zombie;
                }
            }
            if (best == null || bestDistance > targetDistance - 0.3f)
            {
                return;
            }
            BotLog(LogLevel.Debug,
                "Corpo a corpo: trocando o alvo para o zumbi {Kind} a {Distance:0.#}m, o mais perto.",
                DescribeZombieKind(best),
                bestDistance);
            ForceCombatTarget(best, now);
        }

        // 6+ after the bot and no magazine for them: out, at the latest when
        // the nearest is 3.6m away (p75 of the players' starts).
        private bool ShouldLeaveBigGroup(float now)
        {
            if (!Tryhard || m_SwarmEscapeActive || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                return false;
            }
            var position = m_Player.transform.position;
            var count = CountChasers(position, ChaserMeters, out var nearest);
            if (count < BigGroupCount || nearest > BigGroupLeaveNearest)
            {
                return false;
            }
            var shots = GetBestLoadedGunShots();
            if (shots >= count + 2)
            {
                if (now >= m_NextBigGroupLogAt)
                {
                    m_NextBigGroupLogAt = now + 15f;
                    BotLog(LogLevel.Information,
                        "{Count} zumbis atras do bot e {Shots} tiros carregados: descarrega o pente neles.",
                        count,
                        shots);
                }
                return false;
            }
            if (now >= m_NextBigGroupLogAt)
            {
                m_NextBigGroupLogAt = now + 15f;
                BotLog(LogLevel.Information,
                    "{Count} zumbis atras do bot (o mais perto a {Nearest:0.#}m) e so {Shots} tiro(s): saindo de perto, como os jogadores com 6 ou mais.",
                    count,
                    nearest,
                    shots);
            }
            return true;
        }

        // 7+ zombies within 15m and none after the bot: not attacked.
        private bool IsSleepingHordeAhead(float now)
        {
            if (!Tryhard || IsTargetZombieAlive())
            {
                return false;
            }
            var position = m_Player.transform.position;
            if (CountChasers(position, ChaserMeters, out _) > 0)
            {
                return false;
            }
            var around = CountZombiesNear(position, CrowdAroundMeters);
            if (around < CrowdAroundCount)
            {
                return false;
            }
            if (now >= m_NextSleepingHordeLogAt)
            {
                m_NextSleepingHordeLogAt = now + 30f;
                BotLog(LogLevel.Information,
                    "{Count} zumbis a {Radius:0}m e nenhum atras do bot: nao ataca para nao chamar todos.",
                    around,
                    CrowdAroundMeters);
            }
            return true;
        }

        // 7+ zombies within 15m, some not after the bot yet: the blade, so
        // the shot does not call them.
        private bool PreferQuietBlade(Zombie target)
        {
            if (!Tryhard
                || !BestCarriedMeleeStuns()
                || !IsStandUpZombie(target)
                || IsFireZombie(target))
            {
                return false;
            }
            var position = m_Player.transform.position;
            var around = CountZombiesNear(position, CrowdAroundMeters);
            if (around < CrowdAroundCount || CountChasers(position, CrowdAroundMeters, out _) >= around)
            {
                return false;
            }
            if (m_BehaviorNow >= m_NextQuietBladeLogAt)
            {
                m_NextQuietBladeLogAt = m_BehaviorNow + 15f;
                BotLog(LogLevel.Information,
                    "{Count} zumbis a {Radius:0}m: luta na faca, sem tiro para nao chamar o resto.",
                    around,
                    CrowdAroundMeters);
            }
            return true;
        }

        private float m_NextMegaBackOffLogAt;

        private bool TryBackOffFromComingMega(Vector3 position, Zombie target, out Vector3 point)
        {
            point = position;
            if (!Tryhard
                || !IsHuntedBoss(target)
                || !IsBossAfterBot(target)
                || HasClearLineToPoint(GetZombieAimPoint(target)))
            {
                return false;
            }
            var away = position - target.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                return false;
            }
            point = FindNearestNavigablePoint(position + away.normalized * 6f);
            if (m_BehaviorNow >= m_NextMegaBackOffLogAt)
            {
                m_NextMegaBackOffLogAt = m_BehaviorNow + 8f;
                BotLog(LogLevel.Information,
                    "Mega vindo atras do bot a {Distance:0}m sem linha: recuando em vez de ir a um ponto de tiro mais perto dele.",
                    (target.transform.position - position).GetHorizontalMagnitude());
            }
            return true;
        }
    }
}
