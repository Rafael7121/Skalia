using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2 (doc "bots que pensam como jogadores", 9): what the short 0.2.1
    // test showed against the tester's own play.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- 9.1 close aim: the hand follows the zombie ----
        // 0.2.1 put the view at the configured degrees per second (it was 4x
        // too fast). The melee aim had leaned on that excess: the zombie moved
        // between the swing and its impact and the aim trailed it. Swings that
        // landed fell from 92% to 50%, on the head from 82% to 41%. A player
        // tracking a zombie at arm's length moves the mouse with it, not only
        // after it: the change of the wanted angle is applied as it happens
        // (80-95% of it, the hand is not perfect) and the rest is corrected
        // faster than a walking turn.
        //
        // How fast: measured on the players' recordings (zombie, NATANICK,
        // Onca_do_BOPE, DELUXE_ZEBRAUM, XaneBareBR; 6806 samples with a zombie
        // within 3m): yaw median 15/s, p90 140/s, p95 216/s, p99 448/s;
        // swinging, p90 64-140/s. The whole turn (following plus correcting)
        // stays under each bot's own ceiling of 300-450 degrees a second, the
        // players' p95-p99; the old 4x turn went past 1000.
        private const float CloseTrackMeters = 4.5f;
        private const float CloseTrackMaxDegreesPerSecond = 450f;
        private float m_TrackCeiling = -1f;
        private float m_TrackStartYaw;
        private float m_TrackStartPitch;
        private bool m_TrackActive;
        private Zombie? m_TrackZombie;
        private float m_TrackLastYaw;
        private float m_TrackLastPitch;
        private float m_TrackSkill = -1f;

        private bool ApplyCloseAimTracking(Vector3 position)
        {
            var target = m_TargetZombie;
            if (!s_Settings.CloseAimTracking
                || target == null
                || target.isDead
                || !target.gameObject.activeInHierarchy
                || (target.transform.position - position).GetHorizontalMagnitude() > CloseTrackMeters)
            {
                ResetCloseAimTracking();
                return false;
            }
            if (m_TrackSkill < 0f)
            {
                m_TrackSkill = Random.Range(0.8f, 0.95f);
                m_TrackCeiling = Random.Range(300f, CloseTrackMaxDegreesPerSecond);
            }
            m_TrackStartYaw = m_CurrentYaw;
            m_TrackStartPitch = m_CurrentPitch;
            if (m_TrackActive && m_TrackZombie == target)
            {
                var maxStep = CloseTrackMaxDegreesPerSecond * TurnStepSeconds;
                m_CurrentYaw += Mathf.Clamp(Mathf.DeltaAngle(m_TrackLastYaw, m_DesiredYaw), -maxStep, maxStep)
                    * m_TrackSkill;
                m_CurrentPitch += Mathf.Clamp(Mathf.DeltaAngle(m_TrackLastPitch, m_DesiredPitch), -maxStep, maxStep)
                    * m_TrackSkill;
            }
            m_TrackActive = true;
            m_TrackZombie = target;
            m_TrackLastYaw = m_DesiredYaw;
            m_TrackLastPitch = m_DesiredPitch;
            return true;
        }

        private void ResetCloseAimTracking()
        {
            m_TrackActive = false;
            m_TrackZombie = null;
        }

        // The rest of the error, closed a bit faster than a walking turn.
        private float GetCloseTrackTurnSpeed(float delta)
        {
            GetHumanTurnSpeed(0f, false);
            return Mathf.Clamp(delta * m_TurnEase * 1.5f, 35f, m_TrackCeiling)
                * (m_TurnSpeedDegreesPerSecond / 120f);
        }

        // Following and correcting together never pass the bot's ceiling.
        private void ClampCloseTrackStep()
        {
            var maxStep = m_TrackCeiling * (m_TurnSpeedDegreesPerSecond / 120f) * TurnStepSeconds;
            m_CurrentYaw = m_TrackStartYaw
                + Mathf.Clamp(Mathf.DeltaAngle(m_TrackStartYaw, m_CurrentYaw), -maxStep, maxStep);
            m_CurrentPitch = m_TrackStartPitch
                + Mathf.Clamp(Mathf.DeltaAngle(m_TrackStartPitch, m_CurrentPitch), -maxStep, maxStep);
        }

        // ---- 9.2 a few common zombies are fought, not fled ----
        // The swarm escape started with 3 zombies within 4m whatever the bot
        // held: a bot with a knife and 100 health ran from three walkers a
        // player kills. Armed (a real melee or a gun it can shoot), with 50+
        // health and only common zombies (walkers, crawlers, runners), up to
        // 4 within 8m are fought; the gun comes out for 3+ (IsGunWorthyFight).
        private const int SmallGroupMaxZombies = 4;
        private const float SmallGroupMinHealth = 50f;
        private const float SmallGroupRadiusMeters = 8f;
        private float m_NextSmallGroupLogAt;

        private bool ShouldFightSmallGroup(float now)
        {
            if (!s_Settings.FightSmallGroups
                || m_Player.life.health < SmallGroupMinHealth
                || !HasFightingWeapon())
            {
                return false;
            }
            var position = m_Player.transform.position;
            var count = 0;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie == null
                    || zombie.isDead
                    || !zombie.gameObject.activeInHierarchy)
                {
                    continue;
                }
                var offset = zombie.transform.position - position;
                if (offset.GetHorizontalMagnitude() > SmallGroupRadiusMeters
                    || Mathf.Abs(offset.y) > SurroundedMaximumVerticalDifferenceMeters)
                {
                    continue;
                }
                if (!IsCommonZombie(zombie) || ++count > (Tryhard ? StunKiteMaxChasers : SmallGroupMaxZombies))
                {
                    return false;
                }
            }
            // 0.2.2x8 (doc 16.16): 3+ only with the shots for them.
            if (!HasShotsForSmallGroup(count, now))
            {
                return false;
            }
            if (count >= SurroundedZombieCount && now >= m_NextSmallGroupLogAt)
            {
                m_NextSmallGroupLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "{Count} zumbis comuns a {Radius:0}m: lutando em vez de fugir (vida {Health}, arma {Weapon}).",
                    count,
                    SmallGroupRadiusMeters,
                    m_Player.life.health,
                    m_Player.equipment.asset?.itemName ?? "punhos");
            }
            return true;
        }

        private static bool IsCommonZombie(Zombie zombie)
        {
            if (zombie.isMega || zombie.isBoss)
            {
                return false;
            }
            switch (zombie.speciality)
            {
                case EZombieSpeciality.NONE:
                case EZombieSpeciality.NORMAL:
                case EZombieSpeciality.CRAWLER:
                case EZombieSpeciality.SPRINTER:
                    return true;
                default:
                    return false;
            }
        }

        private bool HasFightingWeapon()
        {
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    switch (jar?.GetAsset())
                    {
                        case ItemMeleeAsset melee when IsUsefulMelee(melee):
                            return true;
                        case ItemGunAsset gun when IsUsefulGun(gun) && IsUsableCarriedGun(page, jar!, gun):
                            return true;
                    }
                }
            }
            return false;
        }

        // The swarm, fire and unarmed escapes belong to the life that had
        // them: just born, the bot "fled" from no zombie for 20-55s (0.2.1
        // test, 22:40:02 and 22:40:05).
        private void ResetEscapeStateForLife()
        {
            m_SwarmEscapeActive = false;
            m_HordeFightCommit = false;
            m_SwarmJumpUntil = 0f;
            m_SwarmSafeSince = 0f;
            m_SwarmLowStaminaHoldSince = -1f;
            m_NextSwarmThreatScanAt = 0f;
            m_CachedSurroundedZombieCount = 0;
            m_CachedNearbyZombieCount = 0;
            m_UnarmedGroupFlee = false;
            m_HazardEscapeActive = false;
            m_Ladder = null;
            m_LadderPhase = 0;
            m_LadderDown = false;
            ResetCloseAimTracking();
            ResetTacticsForLife();
        }

        // ---- 9.5 torches and toys are not weapons ----
        // Unarmed, the bot took the first melee it saw (Flashlight 21) and the
        // first gun (Paintball Gun, then its hoppers), and a toy filled the
        // gun role so a real one was no longer sought. Zombie damage in the
        // game files: Flashlight 21, Blowtorch 10 (melee floor 25 keeps the
        // crowbar, hammer and chainsaw); Paintball Gun 15, next gun Nailgun 23.
        private const float UsefulMeleeMinimumDamage = 25f;
        private const float UsefulGunMinimumDamage = 20f;

        private static bool IsUsefulMelee(ItemMeleeAsset melee)
        {
            return !s_Settings.UsefulWeaponsOnly || MeleeDamage(melee) >= UsefulMeleeMinimumDamage;
        }

        private static bool IsUsefulGun(ItemGunAsset gun)
        {
            return !s_Settings.UsefulWeaponsOnly
                || (gun.zombieDamageMultiplier != null
                    && gun.zombieDamageMultiplier.damage >= UsefulGunMinimumDamage);
        }

        // ---- 9.3 / 9.4 ladders ----
        // A climb under way is not replaced by the loot it leads to: at 22:45
        // the Katana on the roof took the objective back every second and
        // each swap restarted the climb until the ladder was marked failed.
        private bool IsLadderInProgress()
        {
            return m_ObjectiveType == ObjectiveType.Ladder && m_Ladder != null;
        }

        // Going down: see SkaliaBotLadderX9 (0.2.2x9: S on the ladder, as
        // the recordings show; the short falls of before cost 13-23 health).
        private bool m_LadderDown;
        private bool m_LadderDownHeld;
        private float m_LadderDownGrabYaw = float.NaN;
        private float m_LadderDownLastHeldY;
        private int m_LadderDownDrops;
        private const float LadderDownTurnDegreesPerSecond = 540f;


        private void StartLadderDescent(BotLadder ladder, float now)
        {
            m_LadderFromRoute = true;
            m_Ladder = ladder;
            m_LadderDown = true;
            m_LadderPhase = 1;
            m_LadderClimbed = false;
            m_LadderStartedAt = now;
            m_LadderPhaseStartedAt = now;
            m_LadderStartY = m_Player.transform.position.y;
            m_LadderDownGrabYaw = float.NaN;
            m_LadderDownHeld = false;
            m_LadderDownDrops = 0;
            m_LadderGrabSearched = false;
            m_LadderGrabIndex = -1;
            ResetLadderX9();
            m_LocalRecoveryPhase = LocalRecoveryPhase.None;
            BotLog(LogLevel.Information,
                "Descendo a escada {Name} de {Top} ate {Bottom}: a rota da malha nova desce por ela.",
                ladder.Name,
                FormatPosition(ladder.Top),
                FormatPosition(ladder.Bottom));
            SetObjective(ObjectiveType.Ladder, ladder.Top, now + 40f);
        }

        // Phase 1: walk to the top exit. Phase 2: take hold (look at the
        // ladder and grab, like E; else back off the edge facing it).
        // Phase 3: on the ladder, S down to the foot (0.2.2x9).
        private bool UpdateLadderDescent(Vector3 position, float now)
        {
            var ladder = m_Ladder!;
            var climbing = m_Player.stance.stance == EPlayerStance.CLIMB;
            var faceYaw = ladder.FacingYaw;
            if (m_LadderPhase == 1)
            {
                var topDistance = (ladder.Top - position).GetHorizontalMagnitude();
                if (topDistance <= 1.2f && Mathf.Abs(ladder.Top.y - position.y) <= 1.5f)
                {
                    m_LadderPhase = 2;
                    m_LadderPhaseStartedAt = now;
                    m_LadderStartY = position.y;
                    ClearPath();
                }
                else if (now - m_LadderStartedAt >= 25f)
                {
                    FailLadderDescent(position, now, "nao chegou ao topo da escada");
                    return true;
                }
                else
                {
                    return false;
                }
            }
            m_StealthCrouch = false;
            m_IsSprinting = false;
            m_NavigationAnalog = 0x12;

            if (m_LadderPhase == 2)
            {
                if (climbing)
                {
                    if (float.IsNaN(m_LadderDownGrabYaw))
                    {
                        // Caught after backing off the edge.
                        m_LadderDownGrabYaw = faceYaw;
                    }
                    m_LadderPhase = 3;
                    m_LadderPhaseStartedAt = now;
                    m_LadderDownHeld = true;
                    m_LadderDownLastHeldY = position.y;
                    BotLog(LogLevel.Information,
                        "Agarrou a escada {Name} no topo ({How}); descendo segurando S.",
                        ladder.Name,
                        m_LadderBackOff ? "de costas pela beirada" : "olhando para ela");
                    return SteerLadderSlideDown(ladder, position, now, true);
                }
                if (m_Player.movement.isGrounded && position.y < m_LadderStartY - 1.5f)
                {
                    // Down without holding it: walked off and missed.
                    FinishLadderDescent(position, now, "caiu sem agarrar");
                    return true;
                }
                // 0.2.2x9: beside it off the mesh: take it from here.
                if (m_LadderMidGrab)
                {
                    if (SteerMidLadderGrab(ladder, position, now))
                    {
                        return true;
                    }
                    FailLadderDescent(position, now, "ao lado da escada sem ponto que o jogo aceite agarrar");
                    return true;
                }
                // 0.2.2x8: from a place the game's grab check passes
                // (SkaliaBotLadderX8), Onça's way; the next tick sees the
                // climb.
                if (!m_LadderBackOff && SteerLadderGrab(ladder, position, now, out _))
                {
                    return true;
                }
                if (now - m_LadderPhaseStartedAt >= 12f
                    || (m_LadderBackOff && now - m_LadderBackOffSince >= 8f))
                {
                    FailLadderDescent(position, now, "nao conseguiu agarrar a escada: " + DescribeLadderContact(ladder, position, true));
                    return true;
                }
                // 0.2.2x9: the tester's way (SkaliaBotLadderX9).
                return SteerLadderBackOff(ladder, position, now);
            }

            // Phase 3: on the ladder, S down to the foot (SkaliaBotLadderX9).
            if (now - m_LadderPhaseStartedAt >= 25f)
            {
                FailLadderDescent(position, now, "descida nao terminou");
                return true;
            }
            return SteerLadderSlideDown(ladder, position, now, climbing);
        }

        private void FinishLadderDescent(Vector3 position, float now, string? problem)
        {
            var ladder = m_Ladder;
            BotLog(LogLevel.Information,
                "Bot desceu a escada {Name} em {Seconds:0.#}s com {Drops} queda(s) curta(s) sem querer e {Dropped} queda(s) de proposito{Problem}; vida {Health}; agora em {Position}.",
                ladder?.Name ?? "?",
                now - m_LadderStartedAt,
                m_LadderDownDrops,
                m_LadderDropCount,
                problem == null ? string.Empty : " (" + problem + ")",
                m_Player.life.health,
                FormatPosition(position));
            m_Ladder = null;
            m_LadderPhase = 0;
            m_LadderDown = false;
            m_IsWalking = false;
            ClearObjective();
            ClearPath();
            m_StallAnchor = position;
            m_NextExplorationSelectionTime = 0f;
        }

        // 0.2.2x6: why the game did not start or grab the climb (47 of 102
        // climbs in the logs: the bot stood 6s at the foot facing the ladder).
        // Going up, PlayerStance needs stance stand/crouch/sprint, equipment
        // not busy, gesture not 11, and a ray from the waist (+0.5m) along
        // the body's forward that hits, within 0.75m, a collider tagged
        // Ladder whose face normal is its local up (|dot| > 0.9). Grabbing
        // from the top, the same checks with a 4m ray from the eye.
        private string DescribeLadderContact(BotLadder ladder, Vector3 position, bool fromTop)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var body = m_Player.transform;
            var forward = body.forward;
            var bodyYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            var text = new System.Text.StringBuilder();
            text.Append("postura ").Append(m_Player.stance.stance)
                .Append(", arma ocupada ").Append(m_Player.equipment.isBusy)
                .Append(", gesto ").Append(m_Player.animator.gesture)
                .Append(", no chao ").Append(m_Player.movement.isGrounded)
                .Append(", corpo a ").Append(Mathf.DeltaAngle(ladder.FacingYaw, bodyYaw).ToString("0", inv)).Append(" graus da escada")
                .Append(", a ").Append(((fromTop ? ladder.Top : ladder.Bottom) - position).GetHorizontalMagnitude().ToString("0.00", inv))
                .Append(fromTop ? "m do topo" : "m do pe");
            var origin = fromTop ? m_Player.look.aim.position : body.position + Vector3.up * 0.5f;
            var direction = fromTop ? m_Player.look.aim.forward : forward;
            var reach = fromTop ? 4f : 0.75f;
            text.Append(fromTop ? "; raio do olho: " : "; raio da cintura: ");
            if (Physics.Raycast(origin, direction, out var hit, 3f + reach, RayMasks.LADDER_INTERACT))
            {
                var collider = hit.collider;
                var root = collider.transform.root != null ? collider.transform.root.name : collider.name;
                var face = Mathf.Abs(Vector3.Dot(hit.normal, collider.transform.up));
                text.Append("acerta ").Append(root.Replace("(Clone)", string.Empty).Trim())
                    .Append('/').Append(collider.name)
                    .Append(" tag ").Append(collider.tag)
                    .Append(" a ").Append(hit.distance.ToString("0.00", inv)).Append('m')
                    .Append(" face ").Append(face.ToString("0.00", inv));
                if (!collider.CompareTag("Ladder"))
                {
                    text.Append(" (nao e a escada: algo na frente)");
                }
                else if (hit.distance > reach)
                {
                    text.Append(" (longe: o jogo pede ").Append(reach.ToString("0.00", inv)).Append("m)");
                }
                else if (face <= 0.9f)
                {
                    text.Append(" (acertou de lado: o jogo pede a face)");
                }
            }
            else
            {
                text.Append("nao acerta nada em ").Append((3f + reach).ToString("0.0", inv)).Append('m');
            }
            return text.ToString();
        }

        private void FailLadderDescent(Vector3 position, float now, string reason)
        {
            var ladder = m_Ladder;
            if (ladder != null)
            {
                PauseLadder(ladder, now, LadderRetrySeconds);
                BotLog(LogLevel.Information,
                    "Descida pela escada {Name} em {Top} falhou ({Reason}; lugares de agarrar achados {Grabs}); este bot tenta de novo em {Seconds:0}s.",
                    ladder.Name,
                    FormatPosition(ladder.Top),
                    reason,
                    m_LadderGrabStands.Count,
                    LadderRetrySeconds);
            }
            m_Ladder = null;
            m_LadderPhase = 0;
            m_LadderDown = false;
            m_IsWalking = false;
            ClearObjective();
            ClearPath();
        }
    }
}
