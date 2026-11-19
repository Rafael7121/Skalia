using Microsoft.Extensions.Logging;
using SDG.Unturned;
using Steamworks;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y: a bot shot by a player shoots back (the tester, 16/11: "fazer eles
    // revidarem", nothing complex yet; the full PvP comes after the LAN
    // recordings). Taken from the player fights of 08/11 (doc "Confrontos
    // entre jogadores"): the one who was hit answered with the gun in hand,
    // crouched or lay down to steady the aim, and the aim was off by 1 to 6
    // degrees. Only a bot with a gun answers; it stands and fights while it
    // sees the attacker, looks where he was for a few seconds after, and
    // goes back to what it was doing. A zombie within 3m comes first.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // 0.1.0-y16: 5 minutes, 60s before (SkaliaBotPvpSpotY).
        private const float PvpMemorySeconds = 300f;
        private const float PvpLookAfterLostSeconds = 6f;
        private const float PvpMaxMeters = 250f;
        private const float PvpZombieFirstMeters = 3f;
        private const float PvpTurnDegreesPerSecond = 420f;
        private const float PvpBodyRadius = 0.4f;
        // 0.2.2y: a second out of sight (a tree, a post, the target ducking)
        // does not end the stance: the aim stays up and only the trigger
        // waits for him to show again.
        private const float PvpLostSightGraceSeconds = 1f;
        private const float PvpEngageLogSeconds = 15f;
        private const float PvpNoGunSeconds = 4f;

        private Player? m_PvpTarget;
        private float m_PvpLastHitAt;
        private float m_PvpLastSeenAt;
        private Vector3 m_PvpLastSeenPoint;
        private bool m_PvpEngaged;
        private bool m_PvpLooking;
        private float m_PvpEngagedSince;
        private float m_PvpYaw;
        private float m_PvpPitch;
        private bool m_PvpAimHeld;
        private float m_PvpAimReadyAt;
        private float m_PvpErrorYaw;
        private float m_PvpErrorPitch;
        private float m_PvpNextErrorAt;
        private int m_PvpShots;
        private int m_PvpHits;
        private int m_PvpTaken;
        private Vector3 m_PvpLeadLast;
        private float m_PvpLeadLastAt;
        private Vector3 m_PvpLeadVelocity;
        private float m_PvpGunFailUntil;
        private bool m_PvpVisible;
        private float m_PvpGunReadySince;
        private float m_PvpEngageLoggedAt = float.NegativeInfinity;
        private float m_PvpPlantedTickAt = float.NegativeInfinity;

        internal static void NotifyPlayerAttack(PlayerLife life, EDeathCause cause, byte amount, CSteamID killer)
        {
            if (amount == 0 || life == null || life.player == null
                || !s_ActiveSimulations.TryGetValue(life.player, out var simulation)
                || !IsPlayerAttackCause(cause))
            {
                return;
            }
            // Runs inside the game's damage call (a Harmony prefix): nothing
            // here may stop the damage itself.
            try
            {
                var attacker = PlayerTool.getPlayer(killer);
                // 0.2.2y: another bot counts too (the tester: "ligue o pvp
                // tambem entre eles"); a member of its group does not.
                if (attacker == null || attacker == life.player
                    || attacker.channel?.owner == null || attacker.life == null || attacker.life.isDead
                    || life.player.quests.isMemberOfSameGroupAs(attacker))
                {
                    return;
                }
                simulation.HandlePlayerAttack(attacker, cause, amount);
            }
            catch (System.Exception exception)
            {
                simulation.BotLog(LogLevel.Warning, "PvP: erro ao registrar o ataque: {Error}", exception.Message);
            }
        }

        private static bool IsPlayerAttackCause(EDeathCause cause)
        {
            switch (cause)
            {
                case EDeathCause.GUN:
                case EDeathCause.MELEE:
                case EDeathCause.PUNCH:
                case EDeathCause.GRENADE:
                case EDeathCause.SPLASH:
                case EDeathCause.MISSILE:
                case EDeathCause.CHARGE:
                case EDeathCause.SHRED:
                case EDeathCause.LANDMINE:
                case EDeathCause.ROADKILL:
                    return true;
                default:
                    return false;
            }
        }

        private void HandlePlayerAttack(Player attacker, EDeathCause cause, byte amount)
        {
            var now = Time.realtimeSinceStartup;
            m_LastHurtByPlayerAt = now;
            m_LastHurtByPlayerName = attacker.channel.owner.playerID.characterName;
            // Shot at by the man it was about to open on: no more waiting
            // than the answer to a hit (below).
            if (attacker == m_PvpFireNotBeforeTarget)
            {
                m_PvpFireNotBefore = 0f;
            }
            var same = m_PvpTarget == attacker;
            var fresh = !same || now - m_PvpLastHitAt > PvpMemorySeconds;
            var busy = m_PvpTarget != null && !same && (m_PvpEngaged || m_PvpLooking);
            var noticed = string.Empty;
            if (!same || !m_PvpAware)
            {
                // Found or not as the players of 08/11 found their shooter,
                // then the turn after their reaction time (SkaliaBotPvpFeelY).
                var found = RollPvpNotice(attacker, out var reactSeconds, out noticed);
                // 0.2.2y4: a punch or a blade only lands from arm's reach;
                // Ghost, punched from 3m behind (17/11 02:50:15), ran 10m
                // "without knowing" where from.
                if (!found && (cause == EDeathCause.PUNCH || cause == EDeathCause.MELEE))
                {
                    found = true;
                    reactSeconds = s_PvpReactSeconds;
                    noticed = "golpe de perto, virei";
                }
                if (busy && !found)
                {
                    // 0.1.0-y17.4: remembered from the side it came
                    // (SkaliaBotPvpMemoryY).
                    var attackerEye = attacker.look?.aim != null ? attacker.look.aim.position : attacker.transform.position + Vector3.up * 1.6f;
                    var toAttacker = attackerEye - m_Player.look.aim.position;
                    var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) * (Random.value < 0.5f ? -1f : 1f);
                    RememberPvpOtherAt(attacker,
                        m_Player.transform.position + Quaternion.Euler(0f, error, 0f) * new Vector3(toAttacker.x, 0f, toAttacker.z),
                        now,
                        false);
                    // A second shooter it cannot place: the fight with the
                    // one it sees goes on.
                    BotLog(LogLevel.Information,
                        "PvP: {Name} me acertou ({Amount} de dano) e {Noticed}; sigo no {Current}.",
                        attacker.channel.owner.playerID.characterName,
                        amount,
                        noticed,
                        m_PvpTarget!.channel?.owner?.playerID.characterName ?? "?");
                    return;
                }
                m_PvpAware = found;
                // Not found: run for cover (SkaliaBotPvpFeelY), started from
                // the bot's own tick, not from inside the game's damage call.
                m_PvpEscapeRequested = !found;
                if (found)
                {
                    m_PvpAwareSince = now;
                    SetPvpReactionFrom(reactSeconds!, attacker, now);
                    // 0.1.0-y17.3 (the tester, 18/11: "reacao dele ta mt
                    // lerda"). The 0.8-2.1s above are the players' turn to
                    // a man hitting them from off their screen. With him on
                    // the screen (rec/pvp16.py, first hits within 60m, 9
                    // times) 7 shot back, 0.0, 0.0, 0.2, 0.2, 0.3, 0.4 and
                    // 0.5s after the round; the bot took the 0.8-2.1s and
                    // then raised its sights. Now the turn and the sights
                    // start at once and the trigger waits only that long.
                    if (IsInPvpView(attacker.transform.position + Vector3.up * 1.3f)
                        && (attacker.transform.position - m_Player.transform.position).magnitude <= PvpHuntMaxMeters)
                    {
                        m_PvpReactAt = now;
                        m_PvpFireNotBefore = now + DrawFromQuantiles(s_PvpHitSeenQuantiles, s_PvpHitSeenShotSeconds);
                        m_PvpFireNotBeforeTarget = attacker;
                        m_PvpFireNotBeforeFirm = true;
                    }
                    if (!same)
                    {
                        m_PvpEngaged = false;
                        m_PvpLooking = false;
                    }
                }
            }
            // 0.1.0-y17.4: the one it had is not forgotten (SkaliaBotPvpMemoryY).
            if (m_PvpTarget != null && m_PvpTarget != attacker)
            {
                RememberPvpOther(m_PvpTarget, now);
            }
            m_PvpTarget = attacker;
            m_PvpLastHitAt = now;
            NotePvpHitFrom(attacker, now);
            // The killing round counts only what health was left (it was
            // 212 taken from 98 health in the test of 17/11).
            var taken = Mathf.Min(amount, m_Player.life.health);
            m_PvpTaken += taken;
            if (fresh)
            {
                // 0.2.2y5: the same man back after a minute keeps the count
                // ("0 apertos de gatilho" after 37 in the test of 17/11).
                if (!same)
                {
                    m_PvpShots = 0;
                    m_PvpHits = 0;
                    m_PvpTaken = taken;
                }
                BotLog(LogLevel.Information,
                    "PvP: {Name} me acertou ({Amount} de dano, {Cause}) a {Distance:0}m; vida {Health}; arma na mao {Weapon}; {Noticed}.",
                    attacker.channel.owner.playerID.characterName,
                    amount,
                    cause,
                    (attacker.transform.position - m_Player.transform.position).magnitude,
                    m_Player.life.health,
                    m_Player.equipment.asset?.itemName ?? "nada",
                    noticed.Length > 0 ? noticed : "ja estava nele");
            }
        }

        private bool IsPvpEngaged()
        {
            return m_PvpEngaged && m_PvpTarget != null;
        }

        // Standing still on purpose: shooting, or looking where he was.
        // The idle (2s), stall (20s) and stranded (90s) watchdogs leave the
        // bot alone; without this the 2s one took the fight for a stuck bot
        // and started unstuck maneuvers on the third strike (6s).
        private bool IsPvpPlanted()
        {
            return m_PvpTarget != null && !IsPvpChasing() && (m_PvpEngaged || m_PvpLooking || IsPvpHuntWatching());
        }

        // The time planted is not time spent failing to get somewhere: the
        // objective, travel, area and loot clocks are moved on by it, as the
        // watch from high ground does (SkaliaBotTryhardX1). Otherwise a 30s
        // fight marked the loot or the patrol point it was heading to as
        // unreachable and gave up the trip.
        private void PausePvpClocks(float now)
        {
            if (!IsPvpPlanted())
            {
                m_PvpPlantedTickAt = float.NegativeInfinity;
                return;
            }
            var paused = now - m_PvpPlantedTickAt;
            m_PvpPlantedTickAt = now;
            if (paused <= 0f || paused > 0.5f)
            {
                return;
            }
            m_ObjectiveExpiresAt += paused;
            m_InterestAreaExpiresAt += paused;
            m_InterestAreaStartedAt += paused;
            m_TravelDeadline += paused;
            m_TravelPhaseDeadline += paused;
            m_TravelLastProgressAt += paused;
            m_RampDeadline += paused;
            m_LastAreaProgressAt += paused;
            m_CombatProgressSince += paused;
            m_LootProgressSince += paused;
            // 0.2.2y2: the route's stall clock too. In the short test of
            // 17/11 Vultox, planted to shoot, was taken for stuck twice
            // ("Sem avancar 2.5s") and two good stretches of the mesh were
            // cut for 10 min.
            m_FollowSince += paused;
            if (m_PatrolNearSince > 0f)
            {
                m_PatrolNearSince += paused;
            }
            if (m_LootWithinReachSince > 0f)
            {
                m_LootWithinReachSince += paused;
            }
            if (m_LootRouteEndSince > 0f)
            {
                m_LootRouteEndSince += paused;
            }
            if (m_LootLookSince > 0f)
            {
                m_LootLookSince += paused;
            }
        }

        private static float PvpBodyHeight(Player player)
        {
            if (player.movement.getVehicle() != null)
            {
                return 1.5f;
            }
            switch (player.stance.stance)
            {
                case EPlayerStance.CROUCH:
                    return 1.25f;
                case EPlayerStance.PRONE:
                    return 0.55f;
                case EPlayerStance.SWIM:
                    return 0.8f;
                default:
                    return 1.85f;
            }
        }

        // Center of the chest, where the players' rounds went in the fights.
        private const float PvpChestFraction = 0.72f;
        private float m_PvpAimFraction = PvpChestFraction;

        private Vector3 GetPvpAimPoint(Player target, float fraction = PvpChestFraction)
        {
            return target.transform.position + Vector3.up * (PvpBodyHeight(target) * fraction);
        }

        // 0.2.2y5: the chest, else the head, else the hips: whatever of him
        // shows. In the test of 17/11 (03:08:52-58) the tester stood 8-16m off
        // with the chest behind the corner of a building and shot Ghost in
        // the head twice; Ghost, looking at the chest only, never saw him.
        private static readonly float[] s_PvpSightFractions = { PvpChestFraction, 0.92f, 0.5f };

        private bool TryGetPvpVisiblePoint(Player target, bool shotFromThere, out Vector3 point, out float fraction)
        {
            foreach (var candidate in s_PvpSightFractions)
            {
                point = GetPvpAimPoint(target, candidate);
                if (HasPvpSight(point, shotFromThere))
                {
                    fraction = candidate;
                    return true;
                }
            }
            point = GetPvpAimPoint(target);
            fraction = PvpChestFraction;
            return false;
        }

        private Vector3 GetPvpSteerPoint(Player target)
        {
            // Out of sight (the second of grace): where he was going when
            // last seen, not where the server says he is.
            // 0.1.0-y12: seen, one input step ahead of him
            // (SkaliaBotPvpCloseAimY).
            var point = m_PvpVisible
                ? GetPvpAimPoint(target, m_PvpAimFraction) + PvpInputStepLead(target)
                : m_PvpLastSeenPoint + m_PvpLeadVelocity * Mathf.Min(1f, Time.realtimeSinceStartup - m_PvpLastSeenAt);
            if (!(Provider.modeConfigData?.Gameplay?.Ballistics ?? true)
                || !(m_Player.equipment.asset is ItemGunAsset gun)
                || gun.projectile != null
                || gun.muzzleVelocity < 1f)
            {
                return point;
            }
            var flight = (point - m_Player.look.aim.position).magnitude / gun.muzzleVelocity;
            var gravityMultiplier = m_Player.equipment.useable is UseableGun useable
                ? useable.CalculateBulletGravityMultiplier()
                : gun.bulletGravityMultiplier;
            var drop = 0.5f * -Physics.gravity.y * gravityMultiplier * flight * flight;
            return point + m_PvpLeadVelocity * flight + Vector3.up * drop;
        }

        private void SamplePvpLead(Player target, float now)
        {
            var position = target.transform.position;
            if (now - m_PvpLeadLastAt > 1f)
            {
                m_PvpLeadLast = position;
                m_PvpLeadLastAt = now;
                m_PvpLeadVelocity = Vector3.zero;
                return;
            }
            if (now - m_PvpLeadLastAt < 0.1f)
            {
                return;
            }
            var velocity = (position - m_PvpLeadLast) / (now - m_PvpLeadLastAt);
            velocity.y = 0f;
            if (velocity.magnitude > 12f)
            {
                velocity = Vector3.zero;
            }
            m_PvpLeadVelocity = Vector3.Lerp(m_PvpLeadVelocity, velocity, 0.5f);
            m_PvpLeadLast = position;
            m_PvpLeadLastAt = now;
        }

        // 0.2.2y: a gun whose round this code can answer. A bow or a launcher
        // shoots a projectile that AttachGunHits never answers, so it would
        // plant the bot firing blanks.
        private static bool IsPvpGun(ItemGunAsset gun)
        {
            return gun.projectile == null && IsUsefulGun(gun);
        }

        // The reach of the gun the bot has or can take in hand: 0 with none.
        // A target past it is not fought (the bot would stand still forever
        // with the trigger refused by the range check).
        private float GetPvpGunRange()
        {
            if (m_Player.equipment.asset is ItemGunAsset held
                && IsPvpGun(held)
                && (IsEquippedGunLoaded(held) || CanReloadGun(held)))
            {
                return held.range;
            }
            if (!HasLoadedRangedWeapon())
            {
                return 0f;
            }
            var best = 0f;
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                for (byte index = 0; index < m_Player.inventory.getItemCount(page); index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar?.GetAsset() is ItemGunAsset gun
                        && IsPvpGun(gun)
                        && (IsLoadedGun(gun, jar.item.state) || CanReloadGun(gun))
                        && CanBecomeHoldable(page, gun))
                    {
                        best = Mathf.Max(best, gun.range);
                    }
                }
            }
            return best;
        }

        // Called every tick after the look and posture are decided, before
        // the packet: while the attacker is in sight the bot stands, turns to
        // him and (ConfigurePvpInput) shoots.
        // 0.2.2y: a fault in the fight code ends the fight, never the bot.
        private void TickPvp(float now)
        {
            if (m_PvpTarget == null)
            {
                m_PvpPlantedTickAt = float.NegativeInfinity;
                try
                {
                    // 0.2.2y2: a shot heard turns the head (SkaliaBotHearingY).
                    TickHeardLook(now);
                    TryOpenFireOnBot(now);
                    // 0.1.0-y17.8: steps coming at it (SkaliaBotPvpMemoryY).
                    TryHearUnknownSteps(now);
                }
                catch (System.Exception exception)
                {
                    BotLog(LogLevel.Warning, "PvP: erro procurando bot para atacar: {Error}", exception.Message);
                    m_PvpSpotted = null;
                }
                return;
            }
            try
            {
                TickPvpCore(now);
                // 0.2.2y7: the others are still seen while this one is not
                // being shot at (SkaliaBotPvpBotsY).
                if (m_PvpTarget != null && !m_PvpEngaged)
                {
                    TryOpenFireOnBot(now);
                }
                PausePvpClocks(now);
            }
            catch (System.Exception exception)
            {
                BotLog(LogLevel.Warning, "PvP: erro na luta, abandonando o alvo: {Error}", exception.ToString());
                ResetPvp(null);
            }
        }

        private void TickPvpCore(float now)
        {
            var target = m_PvpTarget;
            if (target == null)
            {
                return;
            }
            var reason = string.Empty;
            if (target.life == null || target.life.isDead)
            {
                reason = "o jogador morreu";
            }
            else if (target.channel?.owner == null || !Provider.clients.Contains(target.channel.owner))
            {
                reason = "o jogador saiu";
            }
            else if (now - m_PvpLastHitAt > PvpMemorySeconds && now - m_PvpLastSeenAt > PvpMemorySeconds
                && now - m_PvpLastHeardAt > PvpMemorySeconds)
            {
                reason = "sem ver, ouvir nem levar tiro dele ha 5 min";
            }
            if (reason.Length > 0)
            {
                EndPvp(reason, now);
                // 0.1.0-y17.4: another one it remembers (SkaliaBotPvpMemoryY).
                ResumePvpOther(now);
                return;
            }
            var position = m_Player.transform.position;
            var aimPoint = GetPvpAimPoint(target);
            UpdatePvpEscape(now);
            var distance = (aimPoint - m_Player.look.aim.position).magnitude;
            var seenPoint = aimPoint;
            var seenFraction = PvpChestFraction;
            var inLine = distance <= PvpMaxMeters
                && TryGetPvpVisiblePoint(target, m_PvpAware && now - m_PvpLastHitAt <= PvpLookAfterLostSeconds, out seenPoint, out seenFraction);
            // 0.2.2y6: not in line from the eye, maybe on the third-person
            // screen (SkaliaBotPvpCameraY): seen, not shot at.
            var onCamera = false;
            if (inLine)
            {
                aimPoint = seenPoint;
                m_PvpAimFraction = seenFraction;
            }
            else if (distance <= PvpMaxMeters && TryGetPvpCameraSight(target, out var cameraPoint, out var cameraFraction))
            {
                onCamera = true;
                aimPoint = cameraPoint;
                m_PvpAimFraction = cameraFraction;
            }
            // Seen only on the screen: a player he does not know of yet has
            // to be in the 120 degrees a player sees (SkaliaBotPvpFeelY).
            if (((inLine && IsInPvpView(aimPoint)) || onCamera) && !m_PvpAware)
            {
                m_PvpAware = true;
                m_PvpAwareSince = now;
                // 0.1.0-y17.3: facing it with a gun he may be seeing it
                // too: the turn and the sights at once, the trigger as soon
                // as they allow (SkaliaBotPvpLookCoverY).
                var facing = IsPvpArmedToTheEye(target) && IsPvpEnemyFacingMe(target);
                if (facing)
                {
                    m_PvpReactAt = now;
                    m_PvpFireNotBefore = 0f;
                }
                else if (!TrySetHeldAngleReaction(aimPoint, IsPvpHuntWatching(), now))
                {
                    SetPvpReactionFrom(s_PvpReactSeconds, target, now);
                }
                BotLog(LogLevel.Information,
                    "PvP: vi {Name} a {Distance:0}m{How}{Facing}.",
                    target.channel!.owner!.playerID.characterName,
                    distance,
                    onCamera ? " pela camera de terceira pessoa (sem linha de tiro)" : string.Empty,
                    facing ? ", virado para mim e armado: reagindo na hora" : string.Empty);
            }
            if (m_PvpAware && !m_PvpEngaged && !m_PvpLooking && now - m_PvpLastSeenAt > PvpLookAfterLostSeconds
                && now - m_PvpAwareSince > PvpLookAfterLostSeconds)
            {
                // Lost for good: found again only on the screen.
                m_PvpAware = false;
            }
            // Known where he is (eye or camera); the trigger only with the
            // eye on him, where the bullets leave from.
            var visible = (inLine || onCamera) && m_PvpAware;
            var eyeOnHim = inLine && m_PvpAware;
            m_PvpVisible = eyeOnHim;
            NotePvpCameraOnly(visible && !eyeOnHim, target, distance, now);
            // 0.1.0-y17.4: his steps, and those of the others it remembers
            // (SkaliaBotPvpMemoryY).
            TickPvpFootsteps(target, now);
            if (m_PvpTarget != target)
            {
                // 0.1.0-y17.6: turned to steps at its back; this tick's sight
                // was of the other one.
                m_PvpVisible = false;
                m_PvpCameraOnly = false;
                return;
            }
            if (m_PvpEngaged)
            {
                RaisePvpPostureIfHidden(eyeOnHim, now);
            }
            if (visible)
            {
                m_PvpLastSeenAt = now;
                m_PvpLastSeenPoint = aimPoint;
            }
            if (eyeOnHim)
            {
                m_PvpLastEyeAt = now;
            }
            // A gun that does not come to hand in 4s (stuck in the
            // backpack): not planted as a free target for 30s. The time
            // with a bandage in hand does not count.
            var gunInHand = m_Player.equipment.asset is ItemGunAsset handGun && IsPvpGun(handGun);
            if (!m_PvpEngaged || gunInHand || m_PvpMelee || m_TreatmentPhase != 0)
            {
                m_PvpGunReadySince = now;
            }
            else if (now - m_PvpGunReadySince > PvpNoGunSeconds)
            {
                m_PvpGunFailUntil = now + 30f;
                BotLog(LogLevel.Information,
                    "PvP: nenhuma arma de fogo utilizavel veio para a mao em {Seconds:0}s (na mao: {Held}); seguindo sem revidar por 30s.",
                    PvpNoGunSeconds,
                    m_Player.equipment.asset?.itemName ?? "nada");
            }
            var climbing = m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.movement.getVehicle() != null;
            var zombieClose = m_CachedNearestZombieDistance <= PvpZombieFirstMeters;
            // Running from the Mega, a swarm, fire or gas, or with low
            // health: planting there is death, the run goes on.
            // 0.1.0-y17.12: the swarm escape is the state, not this tick's
            // objective: between two of its points the objective is "None"
            // and LilJuninho went at the tester with the blade (22:30:34).
            var running = m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_SwarmEscapeActive
                || (m_ObjectiveType == ObjectiveType.Flee && !IsPvpChasing() && !IsPvpRepositioning())
                || m_ObjectiveType == ObjectiveType.EscapeHazard;
            if (running && IsPvpManNearerThanMega(eyeOnHim, distance, now))
            {
                running = false;
            }
            // 0.2.2y2: a bandage or a drink in hand (SkaliaBotPvpHealY).
            var healing = m_TreatmentPhase != 0 && !m_TreatmentIsMeal;
            // Seen now, or a moment ago while already fighting.
            var inSight = eyeOnHim
                || (m_PvpEngaged && now - m_PvpLastEyeAt <= PvpLostSightGraceSeconds);
            // 0.2.2y7: no round left and him within 10m: the blade or the
            // fists (SkaliaBotPvpMeleeY).
            var gunRange = GetPvpGunRange();
            var melee = gunRange <= 0f && distance <= GetPvpBladeMeters();
            var canFight = inSight
                && now >= m_PvpReactAt
                && (melee || now >= m_PvpGunFailUntil)
                && !climbing
                && !zombieClose
                && !running
                && !healing
                && (distance <= gunRange || melee);
            // Looking where he was only with no zombie being fought: a zombie
            // at 5m must not find the bot standing with its back to it.
            // 0.2.2y6: also a man only seen, never a hit from him: in the
            // test of 17/11 LilJuninho lost the tester behind a pillar at 5.7m
            // and stood 6s facing nothing in particular.
            var looking = !canFight
                && now - m_PvpLastSeenAt <= PvpLookAfterLostSeconds
                && (now - m_PvpLastHitAt <= PvpMemorySeconds
                    || (m_PvpAware && (m_PvpLastSeenPoint - m_Player.look.aim.position).magnitude <= gunRange))
                && !climbing
                && !zombieClose
                && !running
                && !healing
                && m_ObjectiveType != ObjectiveType.Combat
                // 0.1.0-y17.3: on the way to cover or around him the run
                // goes on; a shot only with the eye on him (SkaliaBotPvpLookCoverY).
                && !IsPvpRepositioning();
            if (canFight && !m_PvpEngaged)
            {
                m_PvpEngaged = true;
                // 0.1.0-y17.3: a shot at him on the way: the run ends there.
                if (IsPvpRepositioning())
                {
                    ClearObjective();
                }
                // 0.2.2y6: settled already when the gun is still on him
                // (SkaliaBotPvpSpreadY).
                m_PvpEngagedSince = IsPvpGunOnHim(aimPoint) ? now - 1.5f : now;
                m_PvpGunReadySince = now;
                m_PvpLastPullAt = now;
                m_PvpYaw = m_PvpLooking ? m_PvpYaw : m_CurrentYaw;
                m_PvpPitch = m_PvpLooking ? m_PvpPitch : m_CurrentPitch;
                m_PvpNextErrorAt = 0f;
                DrawPvpStance(distance, now);
                if (melee)
                {
                    NotePvpMeleeStart(target, distance, now);
                }
                if (m_PvpHipFire)
                {
                    m_PvpAimReadyAt = now + Random.Range(0.3f, 0.6f);
                }
                if (now - m_PvpEngageLoggedAt >= PvpEngageLogSeconds)
                {
                    m_PvpEngageLoggedAt = now;
                    BotLog(LogLevel.Information,
                        "PvP: vendo {Name} a {Distance:0}m; atirando {Posture}{Hip}.",
                        target.channel!.owner!.playerID.characterName,
                        distance,
                        m_PvpPosture == 2 ? "deitado" : m_PvpPosture == 1 ? "agachado" : "em pe",
                        m_PvpHipFire ? ", sem mirar" : string.Empty);
                }
            }
            else if (!canFight && m_PvpEngaged)
            {
                m_PvpEngaged = false;
            }
            m_PvpMelee = m_PvpEngaged && melee;
            if (looking && !m_PvpLooking)
            {
                m_PvpYaw = m_CurrentYaw;
                m_PvpPitch = m_CurrentPitch;
            }
            m_PvpLooking = looking;
            // 0.2.2y4: lost him: watch, go to where he was or around
            // (SkaliaBotPvpHuntY).
            TickPvpHunt(now);
            // 0.2.2y8: the route after him (SkaliaBotPvpChaseY).
            NotePvpEnemyLook(target, visible);
            if (TickPvpChase(distance, eyeOnHim, gunRange, now))
            {
                return;
            }
            // 0.2.2y10: no round left and him armed past the blade's reach
            // (SkaliaBotPvpNoAmmoY).
            if (TickPvpNoAmmo(distance, gunRange, now))
            {
                return;
            }
            // 0.1.0-y17.6: out of the first one's sight while it turns to
            // the steps at its back (SkaliaBotPvpMemoryY).
            TickPvpSecondStepsSide(now);
            if (!m_PvpEngaged && !m_PvpLooking)
            {
                return;
            }

            if (visible)
            {
                // His speed only from what is seen of him.
                SamplePvpLead(target, now);
            }
            // The aim: 90% of the rounds on him out to 100m, past it with a
            // scope (SkaliaBotPvpFeelY, 0.1.0-y14); the first-sight miss of
            // 08/11 (6 degrees settling in 1.5s) is gone.
            if (now >= m_PvpNextErrorAt)
            {
                m_PvpNextErrorAt = now + Random.Range(0.4f, 0.8f);
                var sigma = PvpAimSigmaDegrees(distance);
                sigma = PvpAimSigmaLessSpread(sigma);
                m_PvpErrorYaw = Gaussian() * sigma;
                m_PvpErrorPitch = Gaussian() * sigma;
            }
            var lookPoint = m_PvpEngaged ? GetPvpSteerPoint(target) : m_PvpLastSeenPoint;
            var toTarget = lookPoint - m_Player.look.aim.position;
            if (toTarget.sqrMagnitude < 0.01f)
            {
                return;
            }
            var horizontal = new Vector2(toTarget.x, toTarget.z).magnitude;
            var drawnError = m_PvpEngaged && !m_PvpMelee;
            var wantYaw = DirectionToYaw(toTarget) + (drawnError ? m_PvpErrorYaw : 0f);
            var wantPitch = 90f - Mathf.Atan2(toTarget.y, horizontal) * Mathf.Rad2Deg
                + (drawnError ? m_PvpErrorPitch : 0f);
            var step = PvpTurnDegreesPerSecond * Time.fixedDeltaTime;
            m_PvpYaw = Mathf.MoveTowardsAngle(m_PvpYaw, wantYaw, step);
            m_PvpPitch = Mathf.Clamp(Mathf.MoveTowardsAngle(m_PvpPitch, wantPitch, step * 0.7f), 0.5f, 179.5f);
            m_CurrentYaw = m_PvpYaw;
            m_CurrentPitch = m_PvpPitch;
            m_DesiredYaw = m_PvpYaw;
            m_DesiredPitch = m_PvpPitch;
            // Planted on purpose: not a stuck bot, no sprint key, no hop
            // left over from the route.
            m_LastStuckCheckPosition = position;
            m_NextStuckCheckTime = now + 2f;
            m_IsSprinting = false;
            // 0.1.0-y16.2: a hop of the fight's own goes on (SkaliaBotPvpHopY).
            m_NavigationJumpUntil = PvpHopLeft(now);
            if (m_PvpMelee)
            {
                TickPvpMeleeMove(distance);
            }
            else if (m_PvpEngaged && IsPvpRushing(now))
            {
                TickPvpRushSteps(distance, now);
            }
            else if (m_PvpEngaged)
            {
                m_PostureKey = m_PvpPosture;
                // 0.2.2y8: to cover with him looking (SkaliaBotPvpFightCoverY).
                // 0.1.0-y16: off the spot it gave away first
                // (SkaliaBotPvpSpotY).
                if (!TickPvpSpotLeave(now) && !TickPvpFightCover(target, distance, now))
                {
                    UpdatePvpSteps(distance, now);
                }
            }
            else if (!TickPvpLookCover(target, now))
            {
                TickPvpPeek(now);
            }
        }

        // 0.1.0-y17.8 (test of 18/11, 18:18:30-48): ToxicSniper, a loaded
        // Crossbow in hand, ran from the Mega (18-35m) for 18s with the tester
        // following it from 15m down to 0.7m, never fired, and was shot in
        // the back. the tester: "ele nunca pode ficar sem fazer nada, no maximo
        // ele se afasta de mim, lida com os zumbis ainda de olho em mim e
        // depois ve o que faz". The run from the Mega no longer holds the
        // fire on a man in the eye's line nearer than the Mega; fire, gas
        // and the swarm (zombies on the bot) still do.
        private float m_PvpMegaRunFightLogAt = float.NegativeInfinity;

        private bool IsFleeingBoss()
        {
            return m_ObjectiveType == ObjectiveType.Flee
                && m_CachedNearestBoss != null
                && m_FleeingFromPlayerName == DescribeBoss(m_CachedNearestBoss);
        }

        private bool IsPvpManNearerThanMega(bool eyeOnHim, float distance, float now)
        {
            if (!eyeOnHim
                || !IsFleeingBoss()
                || distance >= m_CachedNearestBossDistance)
            {
                return false;
            }
            if (now >= m_PvpMegaRunFightLogAt)
            {
                m_PvpMegaRunFightLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: fugindo do Mega a {Mega:0}m, mas {Name} esta mais perto ({Distance:0}m) e na minha linha: lutando com ele em vez de dar as costas.",
                    m_CachedNearestBossDistance,
                    m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                    distance);
            }
            return true;
        }

        private void EndPvp(string reason, float now)
        {
            var target = m_PvpTarget;
            if (target != null)
            {
                BotLog(LogLevel.Information,
                    "PvP: luta com {Name} acabou ({Reason}): {Shots} apertos de gatilho, {Hits} acertos, {Taken} de dano recebido; vida {Health}.",
                    target.channel?.owner?.playerID.characterName ?? "?",
                    reason,
                    m_PvpShots,
                    m_PvpHits,
                    m_PvpTaken,
                    m_Player.life.health);
            }
            ResetPvpHunt();
            ResetPvpSpot();
            ResetPvpLookCover();
            m_PvpTarget = null;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpVisible = false;
            m_PvpLastEyeAt = float.NegativeInfinity;
            m_PvpCameraOnly = false;
            m_PvpMelee = false;
            m_PvpMeleePending = false;
            m_PvpEnemyArmed = true;
            m_PvpPeekAnalog = 0x11;
            m_PvpJumpSideUntil = 0f;
            m_PvpSecondSideUntil = 0f;
            m_PvpFightCoverUntil = 0f;
            m_PvpReloadCover = false;
            m_PvpNoAmmoLeaving = false;
            m_PvpNoAmmoHiding = false;
            m_PvpNoAmmoChained = false;
            m_PvpChaseOwn = false;
            m_PvpHopUntil = 0f;
            if (IsPvpChasing())
            {
                ClearObjective();
            }
            m_PvpAware = false;
            m_PvpEngageLoggedAt = float.NegativeInfinity;
        }

        // 0.2.2y: death, respawn and standby. The game drops the aim and the
        // trigger on death; the fight state goes with them so a revived bot
        // does not turn to an old attacker or think it is still aiming.
        private void ResetPvp(string? reason)
        {
            var now = Time.realtimeSinceStartup;
            if (m_PvpTarget != null && reason != null)
            {
                EndPvp(reason, now);
            }
            m_PvpTarget = null;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpVisible = false;
            m_PvpLastEyeAt = float.NegativeInfinity;
            m_PvpCameraOnly = false;
            m_PvpMelee = false;
            m_PvpMeleePending = false;
            m_PvpEnemyArmed = true;
            m_PvpPeekAnalog = 0x11;
            m_PvpJumpSideUntil = 0f;
            m_PvpSecondSideUntil = 0f;
            m_PvpFightCoverUntil = 0f;
            m_PvpReloadCover = false;
            m_PvpNoAmmoLeaving = false;
            m_PvpNoAmmoHiding = false;
            m_PvpNoAmmoChained = false;
            m_PvpChaseOwn = false;
            m_PvpHopUntil = 0f;
            if (IsPvpChasing())
            {
                ClearObjective();
            }
            m_PvpAware = false;
            m_PvpLeftUntil = float.NegativeInfinity;
            m_PvpEscapeRequested = false;
            m_PvpOthers.Clear();
            m_PvpLastHeardAt = float.NegativeInfinity;
            m_PvpEscapeActive = false;
            m_PvpAimHeld = false;
            m_PvpTriggerHeld = false;
            m_PvpStepAnalog = 0x11;
            m_PvpReactAt = 0f;
            m_PvpGunFailUntil = 0f;
            m_PvpLeadLastAt = float.NegativeInfinity;
            m_PvpLeadVelocity = Vector3.zero;
            m_PvpPlantedTickAt = float.NegativeInfinity;
            m_PvpBreakAt = float.PositiveInfinity;
            m_PvpAimDrops = 0;
            m_PvpAimFailed = false;
            ResetHeardLook();
            ResetPvpHunt();
            ResetPvpLookCover();
        }

        // Standing still while shooting (players planted to fire) and
        // while looking where he was: the walk is set for the old facing.
        private bool TryGetPvpAnalog(out byte analog)
        {
            analog = m_PvpEngaged ? m_PvpStepAnalog : m_PvpPeekAnalog;
            return (m_PvpEngaged || m_PvpLooking || IsPvpHuntWatching()) && m_PvpTarget != null && !IsPvpChasing();
        }

        // Called from ConfigureCombatInput before the zombie fight; true
        // means the packet belongs to the fight with the player.
        private bool ConfigurePvpInput(WalkingPlayerInputPacket packet, float now)
        {
            try
            {
                return ConfigurePvpInputCore(packet, now);
            }
            catch (System.Exception exception)
            {
                BotLog(LogLevel.Warning, "PvP: erro no comando de tiro, abandonando o alvo: {Error}", exception.ToString());
                ResetPvp(null);
                return false;
            }
        }

        private bool ConfigurePvpInputCore(WalkingPlayerInputPacket packet, float now)
        {
            var target = m_PvpTarget;
            if (!m_PvpEngaged && target != null)
            {
                TryRaisePvpGunWhileWatching(target, now);
            }
            // 0.2.2y7: watching with the sights up (SkaliaBotPvpCoverY).
            if (!m_PvpEngaged && target != null && IsPvpHoldingAngle())
            {
                return ConfigurePvpHoldInput(packet, now);
            }
            if (!m_PvpEngaged || target == null)
            {
                ReleasePvpTrigger(packet, now);
                if (m_PvpAimHeld)
                {
                    packet.secondaryAttack = EAttackInputFlags.Stop;
                    m_PvpAimHeld = false;
                    m_GunAimHeld = false;
                }
                m_PvpStepAnalog = 0x11;
                return false;
            }
            if (m_GunTriggerHeld)
            {
                packet.primaryAttack = EAttackInputFlags.Stop;
                m_GunTriggerHeld = false;
                NoteGunBurstEnd("jogador atirando no bot", now);
            }
            // A knife swing waiting for its impact frame belongs to the
            // zombie fight that is being skipped: dropped, or the button
            // would stay down and the gun would never fire again.
            if (m_PendingMeleeRaycast)
            {
                m_PendingMeleeRaycast = false;
                m_PendingMeleeIsRepeated = false;
                m_PendingMeleeTarget = null;
            }
            if (m_TreatmentPhase != 0 || m_TreatmentUseRequested || m_RefillUseRequested)
            {
                ReleasePvpTrigger(packet, now);
                return true;
            }
            if (m_PrimaryAttackHeld && !(m_PvpMelee && m_PvpMeleeRepeating))
            {
                packet.primaryAttack = EAttackInputFlags.Stop;
                m_PrimaryAttackHeld = false;
                m_PvpMeleeRepeating = false;
                return true;
            }
            if (m_PvpMelee)
            {
                return ConfigurePvpMeleeInput(packet, target, now);
            }
            m_PvpMeleePending = false;
            if (!(m_Player.equipment.asset is ItemGunAsset gun) || !IsPvpGun(gun))
            {
                m_PvpTriggerHeld = false;
                EnsureCombatWeapon(true, now);
                return true;
            }
            var readyGun = m_Player.equipment.useable as UseableGun;
            if (readyGun == null || !m_Player.equipment.IsEquipAnimationFinished)
            {
                m_PvpTriggerHeld = false;
                return true;
            }
            if (!IsEquippedGunLoaded(gun) || readyGun.isReloading || readyGun.isHammering
                || readyGun.isAttaching || readyGun.isUnjamming)
            {
                ReleasePvpTrigger(packet, now);
            }
            var toTarget = GetPvpSteerPoint(target) - m_Player.look.aim.position;
            var distance = toTarget.magnitude;
            // Trigger only with the gun near him: the body, plus the 1-6
            // degrees the players were off by.
            var tolerance = Mathf.Max(7f, Mathf.Atan2(PvpBodyRadius, Mathf.Max(0.5f, distance)) * Mathf.Rad2Deg + 6f);
            var onTarget = Vector3.Angle(AimDirection(GetSentYaw(), GetSentPitch()), toTarget) <= tolerance;
            // 0.1.0-y15: not into the cover in front of the gun
            // (SkaliaBotPvpRecoilY).
            m_PvpMuzzleClear = IsPvpMuzzleClear(distance);
            onTarget = onTarget && m_PvpMuzzleClear;
            // 0.1.0-y16: not again from the spot it gave away
            // (SkaliaBotPvpSpotY).
            onTarget = onTarget && !IsPvpSpotBurned(now);
            if (m_PvpTriggerHeld)
            {
                // Held for the drawn time (0.1-0.8s, the players' pulls),
                // let go sooner when the gun leaves him or he goes out of
                // sight. Each packet held answers the rounds it fires.
                if (now >= m_PvpTriggerUntil || !onTarget || !(m_PvpVisible || IsPvpPrefire(distance, now)) || distance > gun.range)
                {
                    ReleasePvpTrigger(packet, now);
                }
                else
                {
                    m_GunCombatPacket = true;
                }
                return true;
            }
            if (TrySetBestFiremode(gun, readyGun))
            {
                return true;
            }
            // 0.2.2y3: a bolt or pump works its action only with the sights
            // down (UseableGun.tick).
            if ((m_PvpAimHeld || m_GunAimHeld) && readyGun.isAiming && readyGun.needsRechamber)
            {
                ReleasePvpAim(packet, now, "acao da arma so volta com a mira baixa");
                return true;
            }
            if (readyGun.isReloading || readyGun.isHammering || readyGun.isAttaching
                || readyGun.isUnjamming || readyGun.needsRechamber || readyGun.isFired
                || m_Player.equipment.isBusy)
            {
                return true;
            }
            // The aim the server holds, not the one asked for: a gun taken
            // out and put back, or a reload, leaves the new gun unaimed.
            // 0.2.2y3: and the game takes no new press of the sights until
            // the button is let go (PlayerEquipment.simulate_UseableInput:
            // a reload or a jam drops the sights, the button stays down).
            // Only the flag was cleared here, so every new press was
            // ignored: in the test of 17/11 (01:21:18 to 01:23:02) Ghost
            // reloaded with the zombie fight's sights up, then pressed every
            // 0.45s for 1m45s at the tester without the sights ever coming up,
            // and never fired. The button is let go first, pressed again on
            // a later packet.
            if ((m_PvpAimHeld || m_GunAimHeld) && !readyGun.isAiming && now >= m_PvpAimReadyAt)
            {
                ReleasePvpAim(packet, now, readyGun.isReloading ? "recarga derrubou a mira" : "o jogo baixou a mira");
                NotePvpAimDropped(gun, readyGun, now);
                return true;
            }
            if (!IsEquippedGunLoaded(gun))
            {
                // A player lets go of the aim to reload (the game takes no R
                // while aiming).
                if (m_PvpAimHeld || m_GunAimHeld)
                {
                    ReleasePvpAim(packet, now, "recarregar");
                    return true;
                }
                // 0.1.0-y17.6: out of his line first (SkaliaBotPvpFightCoverY).
                if (IsPvpReloadWaitingForCover(now))
                {
                    return true;
                }
                if (!TryReloadEquippedGun(gun, now))
                {
                    m_NextWeaponSelectionAt = 0f;
                    EnsureCombatWeapon(true, now);
                }
                return true;
            }
            // Close in, 29% of the players' rows were from the hip; a gun
            // that only fires aimed is aimed anyway.
            if (!m_PvpAimHeld && (!(m_PvpHipFire || m_PvpAimFailed) || gun.MustAimToShoot || gun.action == EAction.String))
            {
                if (m_Player.stance.stance == EPlayerStance.SPRINT)
                {
                    return true;
                }
                packet.secondaryAttack = EAttackInputFlags.Start;
                m_PvpAimHeld = true;
                m_GunAimHeld = true;
                m_PvpAimReadyAt = now + Random.Range(0.3f, 0.6f);
                return true;
            }
            if (now < m_PvpAimReadyAt || now < m_NextAttackTime || !(m_PvpVisible || IsPvpPrefire(distance, now))
                || IsPvpFireHeld(target, now)
                || distance > gun.range || !onTarget
                || (!(m_PvpHipFire || m_PvpAimFailed) && IsPvpSightsComingUp(readyGun)))
            {
                LogPvpNotFiring(gun, readyGun, distance, onTarget, now);
                return true;
            }
            packet.primaryAttack = EAttackInputFlags.Start;
            m_PvpTriggerHeld = true;
            // 0.2.2y11: point blank with an automatic, held down
            // (SkaliaBotPvpSprayY).
            m_PvpTriggerUntil = IsPvpSprayRange(readyGun, distance)
                ? now + PvpSpraySeconds
                : now + DrawFromQuantiles(s_PvpStepQuantiles, s_PvpHoldSeconds);
            // A semi gun fires once per pull: the next pull waits its rate.
            m_NextAttackTime = now + Mathf.Max(0.18f, (gun.firerate + 1f) * 0.02f);
            // Without ballistics the hit is answered on this packet itself
            // (AttachGunHits), only when it is marked as a gun fight.
            m_GunCombatPacket = true;
            m_PvpShots++;
            m_PvpLastPullAt = now;
            m_PvpAimDrops = 0;
            return true;
        }

        // A round's flight against the player being fought: the same body
        // test as a zombie's (an upright cylinder), the limb by the height.
        private bool TryHitPvpTarget(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out RaycastInfo info)
        {
            distance = 0f;
            info = null!;
            var target = m_PvpTarget;
            if (target == null || target.life == null || target.life.isDead)
            {
                return false;
            }
            var root = target.transform.position;
            var height = PvpBodyHeight(target);
            var prone = target.stance.stance == EPlayerStance.PRONE;
            var radius = prone ? 0.6f : PvpBodyRadius;
            if (!SkaliaPvpMath.TryHitUprightBody(root, height, radius, origin, direction, maxDistance, out distance))
            {
                return false;
            }
            var point = origin + direction * distance;
            ELimb limb;
            switch (SkaliaPvpMath.PickZone(root, height, radius, prone, target.transform.right, point))
            {
                case PvpBodyZone.Head:
                    limb = ELimb.SKULL;
                    break;
                case PvpBodyZone.LeftArm:
                    limb = ELimb.LEFT_ARM;
                    break;
                case PvpBodyZone.RightArm:
                    limb = ELimb.RIGHT_ARM;
                    break;
                case PvpBodyZone.LeftLeg:
                    limb = ELimb.LEFT_LEG;
                    break;
                case PvpBodyZone.RightLeg:
                    limb = ELimb.RIGHT_LEG;
                    break;
                default:
                    limb = ELimb.SPINE;
                    break;
            }
            info = new RaycastInfo(target.transform)
            {
                player = target,
                point = point,
                direction = direction,
                normal = -direction,
                limb = limb,
                materialName = "Flesh_Dynamic"
            };
            return true;
        }
    }
}
