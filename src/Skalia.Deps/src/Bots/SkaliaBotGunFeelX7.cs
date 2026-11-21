using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x7 (doc 16.13, 16.13.1): the shot and the seconds after a kill,
    // from the players' recordings, plus the two deaths of the x6 test.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- the aim before the shot ----
        // The players go into the sights 0.3-0.8s before the first round; up
        // close (6m) one fight in four starts from the hip.
        private const float HipFireMeters = 6f;
        private const float HipFireChance = 0.25f;
        private Zombie? m_GunHipDecisionTarget;
        private float m_GunHipFireUntil;

        private float DrawGunAimSettleSeconds(float distance)
        {
            // Up close the settle is the short end of the players' range: a
            // zombie at 4m reaches the bot in about a second.
            // 0.1.0-y17.37: over serverBot:modifiers:zombieAimSpeed.
            return (distance <= HipFireMeters
                ? Random.Range(0.25f, 0.5f)
                : Random.Range(0.3f, 0.8f)) / SkaliaBotModifiers.ZombieAimSpeed;
        }

        // True while this fight opens from the hip (no sights): decided once
        // per zombie, only for a gun that can fire unaimed (not a bow) and
        // only for the first moment of the fight.
        private bool IsGunHipFireOpening(Zombie target, ItemGunAsset gun, float distance, float now)
        {
            if (target != m_GunHipDecisionTarget)
            {
                m_GunHipDecisionTarget = target;
                m_GunHipFireUntil = 0f;
                if (distance <= HipFireMeters
                    && gun.action != EAction.String
                    && m_Player.stance.stance != EPlayerStance.SPRINT
                    && Random.value < SkaliaBotModifiers.Share(HipFireChance, SkaliaBotModifiers.HipFire))
                {
                    m_GunHipFireUntil = now + 0.6f;
                    BotLog(LogLevel.Debug,
                        "Zumbi a {Distance:0.#}m: primeiro tiro sem mirar.",
                        distance);
                }
            }
            return now < m_GunHipFireUntil;
        }

        // ---- the burst ----
        // Players shoot common zombies in short bursts (median 0.17s, 2-5
        // rounds up close, 1-3 in the middle, 1-2 far); the held trigger
        // stays only for the Mega.
        private float m_GunBurstStartAlpha;
        private float m_GunBurstStartDistance;
        private uint m_GunBurstHitBase;
        private string m_GunBurstWeapon = string.Empty;
        private string m_GunBurstKind = string.Empty;
        private bool m_GunBurstMoving;
        // The line goes out 0.4s after the trigger is let go, when the last
        // bullets have landed (or at the next burst, whichever is first).
        private bool m_GunBurstLogPending;
        private float m_GunBurstLogAt;
        private int m_GunBurstLoggedShots;
        private string m_GunBurstReason = string.Empty;
        private EPlayerStance m_GunBurstStance;

        private bool TryPickGunBurst(Zombie target, float distance, out int limit)
        {
            limit = 0;
            if (!s_Settings.GunAutoFire
                || !(m_Player.equipment.useable is UseableGun useable)
                || useable.firemode != EFiremode.AUTO
                || distance > GunBurstMeters)
            {
                return false;
            }
            if (target.isMega || IsBossZombie(target))
            {
                limit = distance <= GunHoldMeters ? 0 : Random.Range(3, 7);
            }
            else if (distance <= 5f)
            {
                limit = Random.Range(2, 6);
            }
            else if (distance <= GunHoldMeters)
            {
                limit = Random.Range(1, 4);
            }
            else
            {
                limit = Random.Range(1, 3);
            }
            return true;
        }

        private void NoteGunBurstStart(Zombie target, float distance)
        {
            FlushGunBurstLog();
            m_GunBurstHitBase = m_TotalAttackHits;
            m_GunBurstStartDistance = distance;
            m_GunBurstWeapon = m_Player.equipment.asset?.itemName ?? "?";
            m_GunBurstKind = DescribeZombieKind(target);
            m_GunBurstMoving = m_IsWalking;
            m_GunBurstStartAlpha = m_Player.equipment.useable is UseableGun gun
                ? Mathf.Clamp01(gun.GetSimulationAimAlpha())
                : 0f;
        }

        private float NextGunBurstPause(Zombie? target)
        {
            return target != null && (target.isMega || IsBossZombie(target))
                ? Random.Range(0.25f, 0.45f)
                : Random.Range(0.15f, 0.35f);
        }

        private void NoteGunBurstEnd(string reason, float now)
        {
            m_GunBurstLogPending = true;
            m_GunBurstLogAt = now + 0.4f;
            m_GunBurstLoggedShots = m_GunBurstShots;
            m_GunBurstReason = reason;
            m_GunBurstStance = m_Player.stance.stance;
        }

        private void TickGunBurstLog(float now)
        {
            if (m_GunBurstLogPending && now >= m_GunBurstLogAt)
            {
                FlushGunBurstLog();
            }
        }

        private void FlushGunBurstLog()
        {
            if (!m_GunBurstLogPending)
            {
                return;
            }
            m_GunBurstLogPending = false;
            BotLog(LogLevel.Information,
                "Rajada com {Weapon} no zumbi {Kind} a {Distance:0.#}m: {Shots} tiros, {Hits} acertos; mira {Alpha:0.00} no inicio; andando {Moving}; postura {Stance}; soltou: {Reason}.",
                m_GunBurstWeapon,
                m_GunBurstKind,
                m_GunBurstStartDistance,
                m_GunBurstLoggedShots,
                m_TotalAttackHits - m_GunBurstHitBase,
                m_GunBurstStartAlpha,
                m_GunBurstMoving,
                m_GunBurstStance,
                m_GunBurstReason);
        }

        private bool IsGunHeldForCloseFight(Zombie target)
        {
            var position = m_Player.transform.position;
            if ((target.transform.position - position).GetHorizontalMagnitude() <= 3f)
            {
                return true;
            }
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie != null
                    && zombie != target
                    && !zombie.isDead
                    && zombie.gameObject.activeInHierarchy
                    && (zombie.transform.position - position).GetHorizontalMagnitude() <= 8f)
                {
                    return true;
                }
            }
            return false;
        }

        // ---- after the kill ----
        // The players hold the gaze on the body (2-6 deg in the first 0.5s,
        // the first 30 deg turn at 1.1-1.7s with an automatic, 0.6s with a
        // semi or a bow), look down 5-20 deg (the Mega about 25), keep the
        // sights 0.4-1.7s and keep moving: sideways 74-100% of the time,
        // backing off 34-54% (the Mega 78-100%).
        private float m_PostKillGazeUntil;
        private float m_PostKillAimUntil;
        private float m_PostKillStepUntil;
        private Vector3 m_PostKillCorpse;
        private float m_PostKillPitchMin;
        private float m_PostKillPitchMax;
        private byte m_PostKillStepAnalog = 0x11;
        private byte m_PostKillStepNow = 0x11;
        private float m_NextPostKillStepCheckAt;

        private void StartPostKillLook(Zombie zombie, float now)
        {
            if (!(m_Player.equipment.asset is ItemGunAsset gun))
            {
                return;
            }
            var mega = zombie.isMega || IsBossZombie(zombie);
            var special = !mega
                && zombie.speciality != EZombieSpeciality.NONE
                && zombie.speciality != EZombieSpeciality.NORMAL;
            var auto = m_Player.equipment.useable is UseableGun useable
                && useable.firemode == EFiremode.AUTO
                && gun.action != EAction.String;
            m_PostKillCorpse = zombie.transform.position;
            m_PostKillGazeUntil = now + (auto ? Random.Range(0.7f, 2f) : Random.Range(0.3f, 0.6f));
            m_PostKillPitchMin = mega ? 20f : 5f;
            m_PostKillPitchMax = mega ? 30f : 20f;
            m_PostKillAimUntil = now + (mega
                ? Random.Range(0.4f, 0.9f)
                : special
                ? Random.Range(0.4f, 1.6f)
                : Random.Range(0.4f, 1.7f));
            m_PostKillStepUntil = now + Random.Range(0.6f, 1.5f);
            if (Random.value < (mega ? 0.85f : 0.4f))
            {
                var back = Random.value;
                m_PostKillStepAnalog = back < 0.4f ? (byte)0x10 : back < 0.7f ? (byte)0x00 : (byte)0x20;
            }
            else
            {
                m_PostKillStepAnalog = Random.value < 0.5f ? (byte)0x01 : (byte)0x21;
            }
            m_PostKillStepNow = 0x11;
            m_NextPostKillStepCheckAt = 0f;
        }

        private void EndPostKillLook()
        {
            m_PostKillGazeUntil = 0f;
            m_PostKillStepUntil = 0f;
            m_PostKillStepNow = 0x11;
        }

        private bool IsPostKillLookCut()
        {
            return m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_ObjectiveType == ObjectiveType.Loot
                || m_ObjectiveType == ObjectiveType.Ladder
                || m_ObjectiveType == ObjectiveType.ExitWater
                || m_ObjectiveType == ObjectiveType.Unstuck
                || m_LocalRecoveryPhase != LocalRecoveryPhase.None
                || m_BoulderDodgePhase != 0
                || IsLadderInProgress()
                || m_Player.stance.stance == EPlayerStance.SWIM
                || ((m_ObjectiveType == ObjectiveType.Combat
                     || m_ObjectiveType == ObjectiveType.EscapeSwarm)
                    && IsTargetZombieAlive())
                || m_CachedNearestZombieDistance < 15f;
        }

        // Called in the look chain when no zombie is being aimed at: the
        // gaze stays on the body and the step goes on.
        private void ApplyPostKillLook(Vector3 position, float now)
        {
            m_PostKillStepNow = 0x11;
            var sightsUp = now < m_PostKillAimUntil && m_GunAimHeld;
            if (now >= m_PostKillGazeUntil && now >= m_PostKillStepUntil && !sightsUp)
            {
                return;
            }
            if (IsPostKillLookCut())
            {
                EndPostKillLook();
                return;
            }
            if (sightsUp)
            {
                // The sights are still up: no sprint (it would drop them).
                m_IsSprinting = false;
            }
            if (now < m_PostKillGazeUntil)
            {
                var toBody = m_PostKillCorpse + Vector3.up * 0.3f - m_Player.look.aim.position;
                var flat = new Vector3(toBody.x, 0f, toBody.z);
                if (flat.sqrMagnitude > 0.25f)
                {
                    m_DesiredYaw = DirectionToYaw(flat);
                }
                var down = Mathf.Atan2(-toBody.y, Mathf.Max(0.01f, flat.magnitude)) * Mathf.Rad2Deg;
                m_DesiredPitch = 90f + Mathf.Clamp(down, m_PostKillPitchMin, m_PostKillPitchMax);
            }
            if (now < m_PostKillStepUntil)
            {
                // Used only while the bot would otherwise stand
                // (GetCurrentMovementAnalog); a route keeps its own step.
                if (now >= m_NextPostKillStepCheckAt)
                {
                    m_NextPostKillStepCheckAt = now + 0.2f;
                    m_PostKillStepAnalog = PickOpenPostKillStep(position, m_PostKillStepAnalog);
                    if (m_PostKillStepAnalog == 0x11)
                    {
                        m_PostKillStepUntil = 0f;
                        return;
                    }
                }
                m_PostKillStepNow = m_PostKillStepAnalog;
            }
        }

        private byte PickOpenPostKillStep(Vector3 position, byte wanted)
        {
            if (IsPostKillStepOpen(position, wanted))
            {
                return wanted;
            }
            byte mirror;
            switch (wanted)
            {
                case 0x01: mirror = 0x21; break;
                case 0x21: mirror = 0x01; break;
                case 0x00: mirror = 0x20; break;
                case 0x20: mirror = 0x00; break;
                default: mirror = Random.value < 0.5f ? (byte)0x00 : (byte)0x20; break;
            }
            return IsPostKillStepOpen(position, mirror) ? mirror : (byte)0x11;
        }

        private bool IsPostKillStepOpen(Vector3 position, byte analog)
        {
            var direction = GetWorldMovementDirection(analog, m_CurrentYaw);
            return direction.sqrMagnitude > 0.01f
                && !IsBodyRouteBlocked(position, direction, 1.05f)
                && !IsLedgeAhead(position, direction, position);
        }

        // Keeps the sights up after the kill (the packet would let go of
        // them as soon as the fight is over).
        private bool ShouldKeepPostKillAim(float now)
        {
            return now < m_PostKillAimUntil
                && m_Player.equipment.asset is ItemGunAsset
                && m_Player.equipment.useable is UseableGun useable
                && !useable.isReloading
                && !m_Player.equipment.isBusy
                && m_Player.stance.stance != EPlayerStance.SPRINT
                && m_Player.stance.stance != EPlayerStance.SWIM
                && m_ObjectiveType != ObjectiveType.Flee
                && m_ObjectiveType != ObjectiveType.EscapeHazard
                && m_ObjectiveType != ObjectiveType.Ladder
                && m_TreatmentPhase == 0;
        }

        // ---- the flee with a zombie on the bot ----
        // Both deaths of the x6 test were a Flee objective run as forced
        // movement (it comes before the fight in the tree): Tadeu stood 4s
        // with no route and a crawler at 0.9m until he bled out, thigas
        // walked away from the Mega with no stamina, 9 zombies behind and a
        // loaded Heartbreaker, until a burner at 0.9m killed him. A flee
        // gives way to the fight when a zombie is on the bot or when it has
        // stalled with one close.
        private const float FleeYieldSeconds = 4f;
        private const float FleeStallSeconds = 2f;
        private const float FleeStallZombieMeters = 6f;
        private const float MegaYieldMeters = 10f;
        private float m_FleeYieldUntil;
        private Vector3 m_FleeStallAnchor;
        private float m_FleeStallSince;
        private float m_NextFleeYieldLogAt;

        private bool IsFleeYielding(float now)
        {
            // 0.1.0-y17.12: the group the bot chose to kill holds the runs
            // back until it is done (SkaliaBotHordeCommitY).
            return now < m_FleeYieldUntil || m_HordeFightCommit;
        }

        // Only the flee from zombies (low health, bandage, the Mega) gives
        // way. A ramp, the burner's flee (its own fight is in the fire
        // behaviour), acid, spit and cover from a player keep running.
        private bool IsFleeYieldable()
        {
            var from = m_FleeingFromPlayerName ?? string.Empty;
            return m_ObjectiveType == ObjectiveType.Flee
                && m_RampPhase == 0
                && from != FireWaterFleeName
                && from != "zumbi de fogo"
                && from != AcidFleeName
                && from != CoverFleeName
                && from != "cuspidor";
        }

        // True when the flee objective was dropped for a fight.
        private bool TryYieldFleeToFight(Vector3 position, float now)
        {
            if (!IsFleeYieldable()
                || m_Player.stance.stance == EPlayerStance.SWIM)
            {
                m_FleeStallSince = now;
                m_FleeStallAnchor = position;
                return false;
            }
            if ((position - m_FleeStallAnchor).GetHorizontalMagnitude() > 0.75f)
            {
                m_FleeStallAnchor = position;
                m_FleeStallSince = now;
            }
            var nearest = m_CachedNearestZombieDistance;
            // A Mega closer than the common zombie is not fought here: its
            // fists hit harder than the run costs.
            // Within 10m of a Mega the run goes on as well.
            var bossCloser = m_CachedNearestBoss != null
                && !m_CachedNearestBoss.isDead
                && (m_CachedNearestBossDistance <= nearest + 0.1f
                    || m_CachedNearestBossDistance < MegaYieldMeters);
            if (bossCloser)
            {
                return false;
            }
            string? why = null;
            if (nearest <= CaughtByZombieMeters)
            {
                why = "zumbi colado";
            }
            else if (nearest <= FleeStallZombieMeters && now - m_FleeStallSince >= FleeStallSeconds)
            {
                why = "fuga parada";
            }
            if (why == null || !HasUsableCombatEquipment())
            {
                return false;
            }
            if (now >= m_NextFleeYieldLogAt)
            {
                m_NextFleeYieldLogAt = now + 5f;
                BotLog(LogLevel.Information,
                    "Fuga de {From} trocada por luta ({Why}): zumbi a {Distance:0.#}m; vida {Health}; stamina {Stamina}; arma {Weapon}.",
                    m_FleeingFromPlayerName,
                    why,
                    nearest,
                    m_Player.life.health,
                    m_Player.life.stamina,
                    DescribeEquipped());
            }
            m_FleeYieldUntil = now + FleeYieldSeconds;
            m_StandGroundUntil = Mathf.Max(m_StandGroundUntil, now + StandGroundSeconds);
            m_LowHealthFleeActive = false;
            m_FleeStallSince = now;
            ClearObjective();
            return true;
        }
    }
}
