using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x13: zombies the way the players take them, and why.
    //
    // The game (Zombie.askDamage): on a normal server a blow of more than
    // 20 damage stuns the zombie for 1s: it stops, its swing is cancelled
    // and it cannot move. A zombie also has to stand in reach 0.5s before
    // its own blow. So the players' recordings: they stand at the edge of
    // the blade's reach and let the zombie come (it stops to swing), hit it
    // (it is stunned), and hit again while it is still stunned, without a
    // step (60% stood still after a hit, 1.7 blows a kill, 95-99% hits).
    // Stepping back after every blow, as the bots did (61%), threw the
    // stun away, gave the zombie its 0.5s again and cost stamina on the run.
    // They step back walking only after taking a hit or with two on them
    // (to keep both in front), they almost never sprint or jump in a melee,
    // and they crouch to a crawler (its head is at their knees).
    //
    // With a gun they aim down the sights (55-86% of the shots) standing
    // still: the spread when aiming is a fraction of the hip spread, so the
    // shot at 7-11m hits; from the hip they shoot only up close.
    //
    // The burner: its death is a 40 damage blast within 4m, so the players
    // shoot it from 10-16m; with only a blade they still kill it (5 times in
    // the recordings, crouched, healthy, with nothing else next to it)
    // instead of running from it and losing the place they came to loot.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float MeleeWaitOutsideReachMeters = 1.5f;
        private const float MeleeWaitSeconds = 1.2f;
        private const float MeleeHurtStepBackSeconds = 1f;
        private const float MeleePressMeters = 2.5f;
        private const float MeleeStepBackSeconds = 0.7f;
        private const float MeleeCrouchMeters = 3f;
        private const byte FireMeleeMinHealth = 70;

        private byte m_FootworkHealth;
        private float m_FootworkHurtAt = float.NegativeInfinity;
        private float m_MeleeWaitSince = -1f;
        private float m_NextMeleeStandLogAt;
        private float m_NextMeleeStepLogAt;
        private float m_NextFireMeleeLogAt2;

        // Called every movement tick of a fight: when did the bot last lose
        // health.
        private void NoteFootworkHealth(float now)
        {
            var health = m_Player.life.health;
            if (health < m_FootworkHealth)
            {
                m_FootworkHurtAt = now;
            }
            m_FootworkHealth = health;
        }

        private bool HasStunningMeleeInHand(out ItemMeleeAsset? melee)
        {
            melee = m_Player.equipment.asset as ItemMeleeAsset;
            // 0.2.2y: the real blow (Overkill, wear, the zombie's hat)
            // against the zombie's threshold, not a fixed 25 of damage.
            return melee != null
                && MeleeStuns(melee, m_Player.equipment.quality, IsTargetZombieAlive() ? m_TargetZombie : null);
        }

        // A zombie the tryhard stands up to with a blade: anything but the
        // Mega and the bosses.
        private bool IsStandUpZombie(Zombie target)
        {
            return !target.isMega && !target.isBoss && !IsBossZombie(target)
                && (IsCommonZombie(target)
                    || target.speciality == EZombieSpeciality.FLANKER_FRIENDLY
                    || target.speciality == EZombieSpeciality.FLANKER_STALK
                    || target.speciality == EZombieSpeciality.ACID
                    || target.speciality == EZombieSpeciality.BURNER);
        }

        private bool IsTryhardMeleeFootwork(Vector3 position, out Zombie? target)
        {
            target = null;
            if (!Tryhard
                || m_ObjectiveType != ObjectiveType.Combat
                || !IsTargetZombieAlive()
                || !HasStunningMeleeInHand(out _)
                || (m_FloorRouteActive && IsCombatTargetOnOtherFloor(position)))
            {
                return false;
            }
            target = m_TargetZombie!;
            return IsStandUpZombie(target);
        }

        // Step back (walking, facing it) only for a reason: a hit taken in
        // the last second, two zombies on the bot, or low health.
        private bool ShouldTryhardMeleeStepBack(Vector3 position, float now, out string why)
        {
            why = string.Empty;
            if (now - m_FootworkHurtAt <= MeleeHurtStepBackSeconds)
            {
                why = "levou golpe";
                return true;
            }
            if (CountZombiesNear(position, MeleePressMeters, m_TargetZombie) >= 1)
            {
                why = "dois no alcance";
                return true;
            }
            if (m_Player.life.health < 35)
            {
                why = "vida baixa";
                return true;
            }
            // 0.2.2x14: three or more coming: hit and step back, so they
            // arrive one at a time.
            if (CountChasers(position, ChaserMeters, out _) >= StepBackChasers)
            {
                why = "tres ou mais vindo";
                return true;
            }
            return false;
        }

        // In UpdateCombatMovementPhase, in the approach and attack phases:
        // true when the tryhard's footwork decides. No kite after each
        // burst; a short step back, walking, only for a reason.
        private bool TryTryhardMeleePhase(float distance, float attackDistance, float now)
        {
            var position = m_Player.transform.position;
            if (!IsTryhardMeleeFootwork(position, out var target))
            {
                return false;
            }
            var swingDone = now >= m_CombatAttackRecoveryUntil
                && (!m_Player.equipment.isBusy || target!.speciality == EZombieSpeciality.CRAWLER);
            if (swingDone
                && now >= m_CombatRetreatBlockedUntil
                && distance <= attackDistance + 0.5f
                && ShouldTryhardMeleeStepBack(position, now, out var why))
            {
                m_CombatMovementPhase = CombatMovementPhase.Retreat;
                m_CombatRetreatEndsAt = now + MeleeStepBackSeconds;
                m_CombatRetreatPending = false;
                m_CombatRetreatShouldSprint = false;
                if (now >= m_NextMeleeStepLogAt)
                {
                    m_NextMeleeStepLogAt = now + 4f;
                    BotLog(LogLevel.Debug,
                        "Corpo a corpo: um passo para tras andando, de frente para o zumbi {Kind} a {Distance:0.#}m ({Why}).",
                        DescribeZombieKind(target!),
                        distance,
                        why);
                }
                return true;
            }
            if (m_CombatRetreatPending && swingDone)
            {
                // The zombie is stunned: the next blow, not a step back.
                m_CombatRetreatPending = false;
                m_MeleeBurstAttackCount = 0;
            }
            return true;
        }

        // In UpdateCombatSteering: stand still with the zombie in reach, or
        // just outside it while it comes, instead of strafing round it.
        private bool TryTryhardMeleeStand(Vector3 position, float combatDistance, float attackDistance, float now)
        {
            if (!IsTryhardMeleeFootwork(position, out var target)
                || m_CombatMovementPhase == CombatMovementPhase.Retreat)
            {
                m_MeleeWaitSince = -1f;
                return false;
            }
            var offset = target!.transform.position - position;
            if (Mathf.Abs(offset.y) > 1.2f)
            {
                m_MeleeWaitSince = -1f;
                return false;
            }
            var inReach = combatDistance <= attackDistance + 0.2f;
            if (!inReach)
            {
                // Outside the reach: wait for it only while it comes at the
                // bot (a spitter stops at range, a stuck one never comes).
                var coming = IsHuntingBot(target)
                    && target.speciality != EZombieSpeciality.ACID
                    && combatDistance <= attackDistance + MeleeWaitOutsideReachMeters;
                if (!coming)
                {
                    m_MeleeWaitSince = -1f;
                    return false;
                }
                if (m_MeleeWaitSince < 0f)
                {
                    m_MeleeWaitSince = now;
                }
                else if (now - m_MeleeWaitSince > MeleeWaitSeconds)
                {
                    return false;
                }
            }
            else
            {
                m_MeleeWaitSince = -1f;
            }
            m_CombatMovementAnalog = 0x11;
            m_CombatShouldSprint = false;
            m_CombatSteeringBlockedSince = 0f;
            if (now >= m_NextMeleeStandLogAt)
            {
                m_NextMeleeStandLogAt = now + 6f;
                BotLog(LogLevel.Debug,
                    "Corpo a corpo: parado {Where} do zumbi {Kind} a {Distance:0.#}m; o golpe atordoa por 1s e o proximo vem antes dele se recuperar.",
                    inReach ? "no alcance" : "esperando ele chegar ao alcance",
                    DescribeZombieKind(target),
                    combatDistance);
            }
            return true;
        }

        // Crouched to a crawler (its head is low) and to a burner fought
        // with a blade, as the players did.
        private bool TryhardWantsMeleeCrouch()
        {
            if (!IsTryhardMeleeFootwork(m_Player.transform.position, out var target)
                || m_IsSprinting
                || m_CombatMovementPhase == CombatMovementPhase.Retreat
                || !(target!.speciality == EZombieSpeciality.CRAWLER || IsFireZombie(target)))
            {
                return false;
            }
            return (target.transform.position - m_Player.transform.position).GetHorizontalMagnitude() <= MeleeCrouchMeters;
        }

        // ---- the gun: the sights stay up ----
        // The bot already raised the sights before a shot, but kept
        // "holding" them after the game had dropped them (a reload, a jam):
        // the game ignores a new press until the button is let go, so every
        // shot after the first reload went from the hip. And a pump or bolt
        // gun only works the action with the sights down (UseableGun.tick),
        // so held sights left it unable to shoot again. The button is let go
        // in both cases and pressed again before the next shot.
        private float m_GunAimPressedAt;
        private float m_NextGunAimDropLogAt;

        private void ReleaseDroppedGunAim(WalkingPlayerInputPacket packet, float now)
        {
            if (!m_GunAimHeld
                || packet.secondaryAttack != EAttackInputFlags.None
                || !(m_Player.equipment.useable is UseableGun gun))
            {
                return;
            }
            string? why = null;
            if (gun.isAiming && gun.needsRechamber)
            {
                why = "acao da arma (bomba/ferrolho) so volta com a mira baixa";
            }
            else if (!gun.isAiming && now - m_GunAimPressedAt > 0.6f)
            {
                why = gun.isReloading ? "recarga derrubou a mira" : "o jogo baixou a mira";
            }
            if (why == null)
            {
                return;
            }
            packet.secondaryAttack = EAttackInputFlags.Stop;
            m_GunAimHeld = false;
            m_GunAimReadyAt = 0f;
            if (now >= m_NextGunAimDropLogAt)
            {
                m_NextGunAimDropLogAt = now + 10f;
                BotLog(LogLevel.Debug,
                    "Soltando o botao de mira ({Why}); mira de novo antes do proximo tiro.",
                    why);
            }
        }

        // ---- the burner with a blade ----
        private bool CanTryhardMeleeFire(Zombie fire, float distance)
        {
            return Tryhard
                && IsFireZombie(fire)
                && fire.speciality == EZombieSpeciality.BURNER
                && !HasLoadedRangedWeapon()
                && BestCarriedMeleeStuns()
                && m_Player.life.health >= FireMeleeMinHealth
                && !m_Player.life.isBleeding
                && distance <= 20f
                && CountZombiesNear(fire.transform.position, 5f, fire, true) == 0
                && CountFireZombiesWithin(m_Player.transform.position, 25f) <= 1;
        }

        private bool TryTryhardMeleeFire(Zombie fire, Vector3 position, float now)
        {
            var distance = (fire.transform.position - position).GetHorizontalMagnitude();
            if (!CanTryhardMeleeFire(fire, distance))
            {
                return false;
            }
            if (m_TargetZombie != fire && now >= m_NextFireMeleeLogAt2)
            {
                m_NextFireMeleeLogAt2 = now + 10f;
                BotLog(LogLevel.Information,
                    "Zumbi de fogo a {Distance:0.#}m sozinho e sem arma de fogo carregada: enfrentando com {Weapon} agachado (vida {Health}), em vez de fugir da area.",
                    distance,
                    DescribeEquipped(),
                    m_Player.life.health);
            }
            ForceCombatTarget(fire, now);
            return true;
        }

        // A burner far enough out to shoot: the flee ends and the gun works.
        private bool CanTryhardStopFleeToShoot(Zombie fire, float distance)
        {
            return Tryhard
                && distance >= FireShootMinMeters + 2f
                && HasLoadedRangedWeapon()
                && IsFireShootable(fire, m_Player.transform.position)
                && HasClearLineToPoint(GetZombieAimPoint(fire));
        }
    }
}
