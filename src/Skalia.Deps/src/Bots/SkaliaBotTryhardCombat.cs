using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x (doc 15.2): combat and movement after the 0.2.2w test.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- item 1: no minimum distance for the gun ----
        // The tryhard took the knife for every common zombie and the gun only
        // with the zombie past 4-6m; with the gun it still walked in to
        // 7-15m. Now the gun is the weapon at any distance within its reach,
        // with a line to the zombie and rounds to spare, and it shoots from
        // where it stands. The knife stays for an empty or reloading gun.
        private const float TryhardGunReachMeters = 50f;
        // 0.2.2x13: 6 -> 4m. The players stand and shoot with the sights up
        // down to 4m and step back (walking) only inside that; the bots
        // backpedalled from 6m and shot from the hip while moving.
        private const float TryhardGunStandMeters = 4f;
        private const float TryhardMegaStandMeters = 25f;
        private const float TryhardKnifeKeepMeters = 2.5f;
        private bool m_TryhardTargetInSight;
        private float m_TryhardTargetSeenAt;

        private void NoteCombatLineOfSight(bool inSight, float now)
        {
            m_TryhardTargetInSight = inSight;
            if (inSight)
            {
                m_TryhardTargetSeenAt = now;
            }
        }

        // A gun in a weapon slot with rounds to spare for this fight: loaded,
        // or reloadable with no zombie within 6m.
        private bool HasTryhardFightGunInSlot()
        {
            for (byte slot = 0; slot < PlayerInventory.SLOTS; slot++)
            {
                if (m_Player.inventory.getItemCount(slot) == 0)
                {
                    continue;
                }
                var jar = m_Player.inventory.getItem(slot, 0);
                if (jar?.GetAsset() is ItemGunAsset gun
                    && IsUsableCarriedGun(slot, jar, gun)
                    && IsUsefulGun(gun)
                    && HasSpareRangedShots(gun, jar.item.state)
                    && (IsLoadedGun(gun, jar.item.state)
                        || (m_CachedNearestZombieDistance > WeaponSwapZombieMeters && CanReloadGun(gun))))
                {
                    return true;
                }
            }
            return false;
        }

        // The blade in hand is kept only with no fight gun or a zombie on
        // the bot (the swap takes longer than the blow).
        private bool ShouldTryhardKeepMelee()
        {
            // 0.2.2x2 (doc 16.7, item 3): within 60m of a Mega or in the hunt
            // a fight gun replaces the blade even with a zombie on the bot.
            if (Tryhard && (m_BossHuntActive || ((IsMegaNear() || IsMegaLostAlert()) && HasAutoGunCached())) && HasTryhardFightGunInSlot())
            {
                return false;
            }
            return !Tryhard
                || m_CachedNearestZombieDistance <= TryhardKnifeKeepMeters
                || !HasTryhardFightGunInSlot()
                || TryhardPrefersBlade(null);
        }

        private bool TryGetTryhardGunAttackDistance(ItemGunAsset gun, out float meters)
        {
            meters = 0f;
            if (!Tryhard || !m_TryhardTargetInSight || !IsTargetZombieAlive())
            {
                return false;
            }
            meters = Mathf.Clamp(gun.range * 0.75f, 7f, TryhardGunReachMeters);
            return true;
        }

        // With the zombie in sight and in reach the tryhard stands and
        // shoots; it only steps (back) once the zombie is within 6m, the
        // Mega within 25m.
        private bool ShouldTryhardHoldFire(Vector3 position, float combatDistance, float attackDistance)
        {
            if (!Tryhard
                || m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || !m_TryhardTargetInSight
                || !(m_Player.equipment.asset is ItemGunAsset gun)
                || !IsEquippedGunLoaded(gun)
                || (m_FloorRouteActive && IsCombatTargetOnOtherFloor(position))
                || combatDistance > attackDistance)
            {
                return false;
            }
            var target = m_TargetZombie!;
            var stand = TryhardGunStandMeters;
            var keep = target.isMega || IsBossZombie(target)
                ? TryhardMegaStandMeters
                : IsFireZombie(target)
                    ? 15f
                    : stand;
            if (combatDistance < keep)
            {
                return false;
            }
            // Another zombie closing in on the bot: keep the feet free.
            return CountZombiesNear(position, stand, target) == 0;
        }

        // ---- item 2: prone to shoot and to heal ----
        // The players' recordings (doc 15.2): 14% of the shots prone, 20% at
        // 10-40m and on long shots, 5-8% with a zombie within 10m; up at
        // 3-5m. A bandage prone at 18-44 health.
        private const float ProneShotMinMeters = 15f;
        private const float ProneMegaMinMeters = 30f;
        private const float ProneUpTargetMeters = 6f;
        private const float ProneUpMegaMeters = 25f;
        private const float ProneOtherZombieMeters = 10f;
        private const float ProneUpOtherZombieMeters = 8f;
        private const float ProneLostSightSeconds = 1.5f;
        private const float ProneMinimumSeconds = 2f;
        private const float ProneHealQuietMeters = 25f;
        private bool m_TryhardProne;
        private float m_TryhardProneSince;
        private float m_NextProneLogAt;
        private float m_NextProneCheckAt;
        private bool m_ProneLineOk;

        private bool TryhardWantsProne(float now)
        {
            if (!Tryhard
                || m_Player.life.isDead
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.stance.stance == EPlayerStance.DRIVING
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_BoulderDodgePhase != 0)
            {
                return SetProne(false, "fora de condicao", now);
            }
            var position = m_Player.transform.position;
            if (WantsProneShot(position, now, out var whyNot))
            {
                m_ProneLingerUntil = 0f;
                m_TryhardProneForShot = true;
                return SetProne(true, "atirando deitado", now);
            }
            if (WantsProneHeal(position))
            {
                m_ProneLingerUntil = 0f;
                m_TryhardProneForShot = false;
                return SetProne(true, "curativo deitado atras de cobertura", now);
            }
            // 0.2.2x1 (doc 16.3, item 10): the fight over, a player stays
            // down a moment looking round instead of standing up at once
            // (16 of 18 prone shots ended "luta acabou" 2.0s later).
            // 0.1.0-y17.7: only after a shot, as in that data. After a
            // bandage the fight never ended: ToxicSniper (18/11, 17:38:27-29)
            // stayed down 2s more behind a stone fence after its antibiotics,
            // its eye and camera under the top, with the tester's steps running
            // at it from 30m; it saw him at 4m.
            if (m_TryhardProne && m_TryhardProneForShot && whyNot == "luta acabou" && m_CachedNearestZombieDistance > ProneOtherZombieMeters && !IsMegaLostAlert())
            {
                if (m_ProneLingerUntil <= 0f)
                {
                    m_ProneLingerUntil = now + Random.Range(1f, 3f);
                }
                if (now < m_ProneLingerUntil)
                {
                    return true;
                }
            }
            m_ProneLingerUntil = 0f;
            return SetProne(false, whyNot, now);
        }

        private float m_ProneLingerUntil;
        private bool m_TryhardProneForShot;

        private bool WantsProneShot(Vector3 position, float now, out string whyNot)
        {
            whyNot = "luta acabou";
            if (m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || !(m_Player.equipment.asset is ItemGunAsset handGun))
            {
                return false;
            }
            // 0.2.2x11: not with a gun the fight will not use. llo at the
            // Seattle police (03:27) lay down with 1 round in the Zubeknakov,
            // the weapon choice took the Katana (no rounds to spare), and he
            // lay 30m from the zombie with a blade until the fight was given
            // up; twice, the next zombie the same way.
            if (!(m_TargetZombie!.isMega || IsBossZombie(m_TargetZombie))
                && !IsGunAllowedForCommonZombie(handGun, m_Player.equipment.state, 6f)
                && !IsGunWorthyFight())
            {
                whyNot = "arma sem balas de sobra";
                return false;
            }
            var target = m_TargetZombie!;
            var mega = target.isMega || IsBossZombie(target);
            var distance = (target.transform.position - position).GetHorizontalMagnitude();
            var others = CountZombiesNear(position, m_TryhardProne ? ProneUpOtherZombieMeters : ProneOtherZombieMeters, target);
            if (m_TryhardProne)
            {
                // Up like the players: the target at 6m (the Mega at 25m),
                // another zombie at 8m, or the line lost for 1.5s.
                if (distance < (mega ? ProneUpMegaMeters : ProneUpTargetMeters))
                {
                    whyNot = "alvo a " + distance.ToString("0") + "m";
                    return false;
                }
                if (others > 0)
                {
                    whyNot = "outro zumbi a 8m";
                    return false;
                }
                if (now - m_TryhardTargetSeenAt > ProneLostSightSeconds)
                {
                    whyNot = "perdeu a linha do alvo";
                    return false;
                }
                return true;
            }
            if (distance < (mega ? ProneMegaMinMeters : ProneShotMinMeters)
                || others > 0
                || !m_TryhardTargetInSight
                || m_IsWalking
                || (mega && IsBossAfterBot(target) && distance < 40f))
            {
                return false;
            }
            // 0.2.2x1 (doc 16.3, item 10): down only when the shooting will
            // last: the Mega, a far shot (30m+) or more zombies in sight
            // within 40m. One common zombie at 15-30m dies to the first
            // round and the bot was getting up again 2.0s later.
            if (!mega && distance < 30f && CountZombiesNear(position, 40f, target) == 0)
            {
                return false;
            }
            // 0.2.2x3 (doc 16.9, item 4): thigas lay down for a common zombie
            // 6s after losing the Mega and took a boulder (15:00:12-15).
            if (!mega && IsMegaLostAlert())
            {
                return false;
            }
            // Down only if the line still holds from a prone eye.
            if (now >= m_NextProneCheckAt)
            {
                m_NextProneCheckAt = now + 0.5f;
                var eye = position + Vector3.up * 0.55f;
                var aim = GetGunAimPoint(target);
                m_ProneLineOk = !Physics.Linecast(eye, aim, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
            }
            return m_ProneLineOk;
        }

        private bool WantsProneHeal(Vector3 position)
        {
            return m_TreatmentPhase != 0
                && !m_TreatmentIsMeal
                && !m_IsWalking
                && m_CachedNearestZombieDistance > ProneHealQuietMeters
                && m_CachedNearestBossDistance > 60f
                && IsBehindCover(position);
        }

        private bool SetProne(bool want, string why, float now)
        {
            if (want == m_TryhardProne)
            {
                return want;
            }
            if (!want
                && now - m_TryhardProneSince < ProneMinimumSeconds
                && why != "fora de condicao"
                && !why.StartsWith("alvo a")
                && why != "outro zumbi a 8m")
            {
                // A second down at least, unless something comes.
                return true;
            }
            m_TryhardProne = want;
            if (want)
            {
                m_TryhardProneSince = now;
            }
            if (now >= m_NextProneLogAt || !want)
            {
                m_NextProneLogAt = now + 5f;
                if (want)
                {
                    BotLog(LogLevel.Information, "Deitando: {Why}.", why);
                }
                else
                {
                    BotLog(LogLevel.Information,
                        "Levantando depois de {Seconds:0.0}s deitado: {Why}.",
                        now - m_TryhardProneSince,
                        why);
                }
            }
            return want;
        }

        // ---- item 7: a fight that goes nowhere ----
        // llo, 12:05:56-12:08:04: two minutes on a common zombie 2.1m away
        // behind the Humvee; the blow needs a clear line and the rule only
        // walked him round again. Twice without progress in reach: the gun
        // if it sees the zombie, otherwise the zombie is left for 60s. A
        // fight with one common zombie has a 15s ceiling.
        private const float TryhardStuckFightSeconds = 15f;
        private const float TryhardForceGunSeconds = 10f;
        private Zombie? m_CloseKeepTarget;
        private int m_CloseKeepCount;
        private float m_CloseKeepFirstAt;
        private float m_TryhardForceGunUntil;

        // True: the fight goes on (the gun takes over). False: let the
        // caller drop the zombie.
        private bool TryResolveStuckCloseFight(Zombie target, Vector3 position, float now, out bool dropIt)
        {
            dropIt = false;
            if (!Tryhard)
            {
                return false;
            }
            if (target != m_CloseKeepTarget)
            {
                m_CloseKeepTarget = target;
                m_CloseKeepCount = 0;
                m_CloseKeepFirstAt = now;
            }
            m_CloseKeepCount++;
            if (m_CloseKeepCount < 2 && now - m_CloseKeepFirstAt < TryhardStuckFightSeconds)
            {
                return false;
            }
            var sees = HasClearLineToPoint(GetGunAimPoint(target));
            if (sees && now >= m_TryhardForceGunUntil && HasLoadedRangedWeapon())
            {
                m_TryhardForceGunUntil = now + TryhardForceGunSeconds;
                m_CloseKeepCount = 0;
                BotLog(LogLevel.Information,
                    "Luta sem progresso com o zumbi {Kind} a {Distance:0.#}m em {Position}: o golpe nao passa, resolvendo com a arma.",
                    DescribeZombieKind(target),
                    (target.transform.position - position).GetHorizontalMagnitude(),
                    FormatPosition(position));
                return true;
            }
            dropIt = true;
            m_CloseKeepTarget = null;
            m_CloseKeepCount = 0;
            BotLog(LogLevel.Information,
                "Luta sem progresso com o zumbi {Kind} a {Distance:0.#}m em {Position}: {Why}; deixando ele e seguindo.",
                DescribeZombieKind(target),
                (target.transform.position - position).GetHorizontalMagnitude(),
                FormatPosition(position),
                sees ? "nem a arma resolveu" : "nenhum dos dois alcanca o outro");
            return false;
        }

        private bool IsTryhardForcedGun(float now)
        {
            return Tryhard && now < m_TryhardForceGunUntil;
        }

        // ---- item 8: never the back to the target ----
        // 2 of the 5 deaths: "Mega a 8m: correndo (perto demais)", back
        // turned, caught at 1.2m. Armed and still meaning to fight, the bot
        // backs off facing the threat (S, A, D) and keeps shooting; only
        // when it gives the target up does it turn and run (the tester: "so faz
        // sentido ele dar as costas se ele for ABANDONAR o alvo").
        private float m_NextAbandonLogAt;
        private Zombie? m_LastAbandonLogged;

        private bool TryGetRetreatFacing(Vector3 position, float now, out Vector3 aimPoint)
        {
            aimPoint = default;
            if (!Tryhard || now < m_MegaTurnRunUntil)
            {
                // 0.2.2x1: the short run from the Mega is done back turned.
                return false;
            }
            Zombie? threat = null;
            if (m_ObjectiveType == ObjectiveType.Flee && m_FleeingFromPlayerName == "o Mega (recuo da caca)")
            {
                threat = m_BossHuntTarget;
            }
            else if (m_ObjectiveType == ObjectiveType.RepositionFire)
            {
                threat = m_RememberedFireZombie;
            }
            if (threat == null
                || threat.isDead
                || !threat.gameObject.activeInHierarchy
                || (threat.transform.position - position).GetHorizontalMagnitude() > 40f)
            {
                return false;
            }
            var gun = m_Player.equipment.asset as ItemGunAsset;
            var canFight = gun != null && (IsEquippedGunLoaded(gun) || CanReloadGun(gun));
            if (!canFight)
            {
                if (now >= m_NextAbandonLogAt || m_LastAbandonLogged != threat)
                {
                    m_NextAbandonLogAt = now + 10f;
                    m_LastAbandonLogged = threat;
                    BotLog(LogLevel.Information,
                        "Abandonando {Kind} a {Distance:0}m: {Why}; virando para correr.",
                        DescribeZombieKind(threat),
                        (threat.transform.position - position).GetHorizontalMagnitude(),
                        gun == null ? "sem arma na mao" : "arma vazia e sem como recarregar");
                }
                return false;
            }
            aimPoint = GetGunAimPoint(threat);
            return true;
        }

        // ---- item 10: bars and gates ----
        // The jump probe only knew obstacles that block at 0.3 or 0.6m; a
        // gate bar at about 1m with a gap under it was pushed against until
        // the unstuck (2-4s, up to 20s). Pressed against something for 0.5s
        // with its top under 1.3m and room above: jump.
        private const float ContactJumpStallSeconds = 0.5f;
        private const float ContactJumpTopMeters = 1.4f;
        private Vector3 m_ContactSamplePosition;
        private float m_ContactSampleAt;
        private float m_NextContactJumpLogAt;

        private bool TryContactJump(Vector3 position, Vector3 direction, float now)
        {
            if (!Tryhard || direction.sqrMagnitude < 0.01f)
            {
                return false;
            }
            if ((position - m_ContactSamplePosition).GetHorizontalMagnitude() > 0.2f)
            {
                m_ContactSamplePosition = position;
                m_ContactSampleAt = now;
                return false;
            }
            if (now - m_ContactSampleAt < ContactJumpStallSeconds)
            {
                return false;
            }
            direction.y = 0f;
            direction.Normalize();
            // Room for the head over it.
            if (Physics.Raycast(position + Vector3.up * 1.65f, direction, 1.2f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            if (!Physics.CapsuleCast(
                    position + Vector3.up * 0.3f,
                    position + Vector3.up * 1.25f,
                    0.25f,
                    direction,
                    out var hit,
                    0.9f,
                    RayMasks.BLOCK_COLLISION,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            if (SkaliaBotObjectMap.TryGetByCollider(hit.collider, out var blockingObject)
                && SkaliaBotObjectMap.BlocksBody(blockingObject))
            {
                return false;
            }
            // 0.2.2x12: the old single ray 0.4m past the contact missed a
            // thin gate arm (Crossing "Hinge") and read the road behind it,
            // so the bot pushed on the arm and never jumped.
            var height = MeasureHopTop(position, direction, hit);
            if (height < 0.2f || height > ContactJumpTopMeters)
            {
                return false;
            }
            var landing = position + direction * (hit.distance + 1.4f) + Vector3.up * 2.5f;
            if (!Physics.Raycast(landing, Vector3.down, out var ground, 6f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || ground.normal.y < 0.4f
                || ground.point.y < position.y - 2.5f
                || ground.point.y > position.y + 1.5f)
            {
                return false;
            }
            m_ContactSampleAt = now;
            if (now >= m_NextContactJumpLogAt)
            {
                m_NextContactJumpLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "Encostado em obstaculo baixo ({Name}, topo a {Height:0.0}m) em {Position}: pulando.",
                    hit.collider != null ? hit.collider.name : "?",
                    height,
                    FormatPosition(position));
            }
            return true;
        }

        // ---- item 11: no useless headgear ----
        // Tadeu took a Snorkel twice: the game puts clothing on by itself in
        // an empty slot and the rule wore anything on an empty head. The
        // tryhard keeps on the head only what protects (5% or more) or sees
        // at night; the rest comes off and is dropped.
        private float m_NextHeadgearCheckAt;

        private static bool IsUsefulHeadgear(ItemClothingAsset gear)
        {
            return gear.armor <= 0.95f
                || (gear is ItemGlassesAsset glasses && glasses.vision != ELightingVision.NONE)
                // 0.1.0-y17.25: the gas mask where there are deadzones.
                || (IsRadiationMask(gear) && MapHasDeadzone);
        }

        private void TakeOffUselessHeadgear(float now)
        {
            if (!Tryhard
                || now < m_NextHeadgearCheckAt
                || m_TreatmentPhase != 0
                || IsFightObjective()
                || m_Player.equipment.isBusy)
            {
                return;
            }
            m_NextHeadgearCheckAt = now + 2f;
            var clothing = m_Player.clothing;
            ItemClothingAsset? useless = null;
            if (clothing.hatAsset != null && !IsUsefulHeadgear(clothing.hatAsset))
            {
                useless = clothing.hatAsset;
                clothing.ReceiveSwapHatRequest(byte.MaxValue, byte.MaxValue, byte.MaxValue);
            }
            else if (clothing.maskAsset != null && !IsUsefulHeadgear(clothing.maskAsset))
            {
                useless = clothing.maskAsset;
                clothing.ReceiveSwapMaskRequest(byte.MaxValue, byte.MaxValue, byte.MaxValue);
            }
            else if (clothing.glassesAsset != null && !IsUsefulHeadgear(clothing.glassesAsset))
            {
                useless = clothing.glassesAsset;
                clothing.ReceiveSwapGlassesRequest(byte.MaxValue, byte.MaxValue, byte.MaxValue);
            }
            if (useless != null)
            {
                // Back in the bag; WearBetterHeadgear drops it on its pass.
                BotLog(LogLevel.Information,
                    "Tirando {Item} da cabeca: nao protege nem tem visao noturna.",
                    useless.itemName);
            }
        }

        // ---- item 13: the Mega's pile ----
        private const float MegaPileArriveMeters = 6f;
        private const float MegaPileSearchSeconds = 2.5f;
        private float m_MegaPileArrivedAt;
        private float m_NextMegaPileWalkLogAt;

        // No item seen from here: walk to the body first, and only call the
        // pile empty after 2.5s there.
        private bool TryWalkToMegaPile(Vector3 position, float now)
        {
            var gap = (m_DropPickupCenter - position).GetHorizontalMagnitude();
            if (gap <= MegaPileArriveMeters)
            {
                if (m_MegaPileArrivedAt <= 0f)
                {
                    m_MegaPileArrivedAt = now;
                }
                return now - m_MegaPileArrivedAt < MegaPileSearchSeconds;
            }
            m_MegaPileArrivedAt = 0f;
            if (m_ObjectiveType != ObjectiveType.Patrol
                || (m_ObjectivePosition - m_DropPickupCenter).GetHorizontalMagnitude() > 1f)
            {
                SetObjective(ObjectiveType.Patrol, m_DropPickupCenter, now + 20f);
            }
            if (now >= m_NextMegaPileWalkLogAt)
            {
                m_NextMegaPileWalkLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "Indo ao monte {Pile} a {Distance:0}m em {Position} ({Left:0}s de janela).",
                    m_DropPileName,
                    gap,
                    FormatPosition(m_DropPickupCenter),
                    m_DropPickupUntil - now);
            }
            return true;
        }

        // ---- item 14: no spin on the spot ----
        private float m_SpinHoldUntil;
        private const float SpinHoldDegrees = 200f;
        private const float SpinHoldSeconds = 1f;

        private bool IsSpinHeld(float now)
        {
            return Tryhard && now < m_SpinHoldUntil;
        }
    }
}
