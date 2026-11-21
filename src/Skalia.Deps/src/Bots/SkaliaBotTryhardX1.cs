using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2x1 (doc 16): the 0.2.2x test (22:16-23:07) and the players'
    // recordings (18 sessions), before the 0.2.2y.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // ---- item 2: the fire mode ----
        // The plugin never changed it: a gun found on Semi or burst stayed so
        // and the held trigger (GunAutoFire) only worked on a gun already on
        // Auto. Auto when the gun has it, else Semi, else burst.
        private float m_NextFiremodeTryAt;

        private bool TrySetBestFiremode(ItemGunAsset gun, UseableGun useable)
        {
            var want = gun.hasAuto ? EFiremode.AUTO
                : gun.hasSemi ? EFiremode.SEMI
                : gun.hasBurst ? EFiremode.BURST
                : useable.firemode;
            if (useable.firemode == want || m_BehaviorNow < m_NextFiremodeTryAt)
            {
                return false;
            }
            m_NextFiremodeTryAt = m_BehaviorNow + 1f;
            if (m_Player.equipment.isBusy
                || useable.isFired
                || useable.isReloading
                || useable.isHammering
                || useable.isUnjamming
                || useable.needsRechamber)
            {
                return false;
            }
            var before = useable.firemode;
            useable.ReceiveChangeFiremode(want);
            if (useable.firemode != want)
            {
                return false;
            }
            BotLog(LogLevel.Information,
                "Modo de tiro de {Gun}: {Before} para {After}.",
                gun.itemName,
                before,
                want);
            return true;
        }

        // ---- items 3 and 13: an automatic gun ----
        private float m_AutoGunCheckedAt = -100f;
        private bool m_HasAutoGun;

        private bool HasAutoGunCached()
        {
            if (m_BehaviorNow - m_AutoGunCheckedAt < 5f)
            {
                return m_HasAutoGun;
            }
            m_AutoGunCheckedAt = m_BehaviorNow;
            m_HasAutoGun = false;
            for (byte page = 0; page < PlayerInventory.STORAGE && !m_HasAutoGun; page++)
            {
                var itemCount = m_Player.inventory.getItemCount(page);
                for (byte index = 0; index < itemCount; index++)
                {
                    var jar = m_Player.inventory.getItem(page, index);
                    if (jar?.GetAsset() is ItemGunAsset gun
                        && IsTryhardMegaGun(gun)
                        && IsUsableCarriedGun(page, jar, gun))
                    {
                        m_HasAutoGun = true;
                        break;
                    }
                }
            }
            return m_HasAutoGun;
        }

        // Where a Mega was seen and is out of sight: 30m kept for 5 min, 55m
        // without an automatic gun.
        private float GetTryhardMegaZoneMeters()
        {
            return HasAutoGunCached() ? 30f : BossNoLureMeters;
        }

        // ---- item 4: the short run from the Mega ----
        private float m_MegaTurnRunUntil;
        private float m_MegaTurnRunNextAt;
        private float m_MegaTurnRunMeters;

        // ---- 0.2.2x3 (doc 16.9, item 4): the Mega out of sight ----
        // For 30s after the hunt lost the Mega the bot stays alert within
        // 60m of where it was last seen: standing, gun in hand, no loot.
        private const float MegaLostAlertSeconds = 30f;
        private float m_MegaLostAlertUntil;
        private Vector3 m_MegaLostPosition;

        private void StartMegaLostAlert(Vector3 lastSeen, float now)
        {
            m_MegaLostPosition = lastSeen;
            m_MegaLostAlertUntil = now + MegaLostAlertSeconds;
            BotLog(LogLevel.Information,
                "Mega fora de vista em {Position}: {Seconds:0}s de alerta (sem deitar, sem faca, sem parar para olhar loot).",
                FormatPosition(lastSeen),
                MegaLostAlertSeconds);
        }

        private bool IsMegaLostAlert()
        {
            return Tryhard
                && m_BehaviorNow < m_MegaLostAlertUntil
                && (m_MegaLostPosition - m_BehaviorPosition).GetHorizontalMagnitude() <= RiskMegaMeters;
        }
        // 0.2.2x3: the Mega gun of the tryhard is automatic and takes
        // Military (caliber 1: Military Magazine/Drum) or Ranger (caliber 9:
        // Ranger Magazine/Drum) rounds.
        private const ushort MilitaryCaliber = 1;
        private const ushort RangerCaliber = 9;

        private static bool IsTryhardMegaGun(ItemGunAsset gun)
        {
            if (!gun.hasAuto || gun.magazineCalibers == null)
            {
                return false;
            }
            foreach (var caliber in gun.magazineCalibers)
            {
                if (caliber == MilitaryCaliber || caliber == RangerCaliber)
                {
                    return true;
                }
            }
            return false;
        }
        private static readonly bool s_MegaTurnRunOff = true;

        private bool TryUpdateMegaTurnRun(bool loaded, float distance, float now)
        {
            if (!Tryhard)
            {
                return false;
            }
            if (now < m_MegaTurnRunUntil)
            {
                return true;
            }
            // 0.2.2x2 (doc 16.7, item 1): 5 of the 7 deaths of the 0.2.2x1
            // test came 1-12s after a run started with the Mega at 1-6m
            // (the Mega walks 6 m/s against 7 sprinting: the run gains
            // nothing and the bot stops shooting). Loaded, the bot backs off
            // facing it and shooting, as in 0.2.2x (0 deaths); the back is
            // turned only to reposition while reloading (the run below).
            if (s_MegaTurnRunOff)
            {
                return false;
            }
            if (!loaded || now < m_MegaTurnRunNextAt)
            {
                return false;
            }
            if (m_MegaTurnRunMeters <= 0f)
            {
                m_MegaTurnRunMeters = Random.Range(4f, 7f);
            }
            if (distance >= m_MegaTurnRunMeters)
            {
                return false;
            }
            var seconds = Random.Range(0.6f, 2f);
            m_MegaTurnRunUntil = now + seconds;
            m_MegaTurnRunNextAt = m_MegaTurnRunUntil + Random.Range(0.8f, 1.6f);
            BotLog(LogLevel.Information,
                "Mega a {Distance:0.#}m: virando e correndo {Seconds:0.0}s para abrir distancia, depois volta a atirar de frente (vida {Health}).",
                distance,
                seconds,
                m_Player.life.health);
            m_MegaTurnRunMeters = Random.Range(4f, 7f);
            m_NextBossFleeReplanAt = 0f;
            return true;
        }

        // The run over: the escape point is dropped so the bot faces the Mega.
        private void EndMegaTurnRunObjective()
        {
            if (m_MegaTurnRunUntil <= 0f || m_BehaviorNow < m_MegaTurnRunUntil)
            {
                return;
            }
            m_MegaTurnRunUntil = 0f;
            if (m_ObjectiveType == ObjectiveType.Flee && m_FleeingFromPlayerName == "o Mega (recuo da caca)")
            {
                ClearObjective();
            }
        }

        // ---- item 6: calm with the horde in the hunt ----
        // The common zombies after the bot within 10m die first while the
        // Mega is over 20m away or not after the bot; with the Mega close,
        // only one within 3m. A target is kept at least 1.5s.
        private Zombie? m_HuntCommonTarget;
        private float m_HuntCommonSince;

        private bool TryFindHuntCommonThreat(Vector3 position, Zombie boss, float megaDistance, float now, out Zombie zombie, out string why)
        {
            zombie = null!;
            why = string.Empty;
            var megaClose = megaDistance <= 20f && IsBossAfterBot(boss);
            var radius = megaClose ? 3f : 10f;
            var kept = m_HuntCommonTarget;
            if (kept != null
                && !kept.isDead
                && kept.gameObject.activeInHierarchy
                && (kept.transform.position - position).GetHorizontalMagnitude() <= radius + 2f
                && (now - m_HuntCommonSince < 1.5f || (kept.isHunting && kept.player == m_Player)))
            {
                zombie = kept;
                why = "mesmo alvo ate ele cair";
                return true;
            }
            var best = radius;
            foreach (var perceived in m_PerceivedZombies)
            {
                var candidate = perceived.Key;
                if (candidate == null
                    || candidate.isDead
                    || !candidate.gameObject.activeInHierarchy
                    || IsFireZombie(candidate)
                    || IsBossZombie(candidate)
                    || IsZombieMarkedUnreachable(candidate, now))
                {
                    continue;
                }
                var distance = (candidate.transform.position - position).GetHorizontalMagnitude();
                if (distance > best)
                {
                    continue;
                }
                var after = candidate.isHunting && candidate.player == m_Player;
                if (!after && distance > TryhardMegaCommonMeters)
                {
                    continue;
                }
                best = distance;
                zombie = candidate;
            }
            if (zombie == null)
            {
                m_HuntCommonTarget = null;
                return false;
            }
            if (zombie != m_HuntCommonTarget)
            {
                m_HuntCommonTarget = zombie;
                m_HuntCommonSince = now;
            }
            why = megaClose ? "Mega perto: so o que encosta" : "vindo atras do bot";
            return true;
        }

        // ---- item 7: blade or bullet ----
        // The players' kills of common zombies (doc 16.2): each has a style
        // (one player 91% bullet, others 37%, 46% and 2%); acid and
        // burner go to the bullet (83-87%); with 2+ zombies within 15m the
        // bullet rises to 63-75%. One choice per zombie, kept until it dies.
        private float m_GunStyle = -1f;
        private Zombie? m_BladeChoiceTarget;
        private bool m_BladeChoice;
        private float m_NextBladeLogAt;

        private float m_NextCommonBladeLogAt;

        private void LogTryhardBladeForCommon(Zombie target)
        {
            if (m_BehaviorNow < m_NextCommonBladeLogAt)
            {
                return;
            }
            m_NextCommonBladeLogAt = m_BehaviorNow + 15f;
            BotLog(LogLevel.Information,
                "Zumbi {Kind} a {Distance:0.#}m com menos de 3 atras de mim: {Weapon}, as balas ficam para o PvP.",
                DescribeZombieKind(target),
                (target.transform.position - m_Player.transform.position).GetHorizontalMagnitude(),
                HasMeleeWeapon() ? "na faca" : "no soco");
        }

        private bool TryhardPrefersBlade(byte[]? state)
        {
            // 0.2.2x2 (doc 16.7, item 3): the knife came out with the Mega at
            // 3m (Tadeu 23:41) and 61m (Tadeu 01:21); within 60m of one the
            // gun stays in hand when it is an automatic (a shot from a weak
            // gun only calls the Mega).
            if (!Tryhard || m_BossHuntActive || !HasMeleeWeapon() || ((IsMegaNear() || IsMegaLostAlert()) && HasAutoGunCached()))
            {
                return false;
            }
            var target = m_TargetZombie;
            if (target == null || target.isDead)
            {
                return false;
            }
            if (target.isMega || IsBossZombie(target) || IsFireZombie(target))
            {
                return false;
            }
            // 0.2.2y3 (rec/pvp15.py): in the players' fights with a player a
            // knife was in hand in 0.9% of the rows, a gun in 93%. Ghost took
            // the knife out for zombies at 11 and 14m with the tester 11 to 18m
            // away (17/11 01:20:12, 01:20:18). 0.2.2y5: while he is
            // remembered (a minute), not only 10s: Ghost took it out at 9 and
            // 18s with the tester around (03:10:08, 03:10:17).
            if (m_PvpTarget != null && HasLoadedRangedWeapon())
            {
                return false;
            }
            // 0.1.0-y17.7 (the tester, 18/11): every round is kept for PvP; one
            // or two common zombies are fought with the knife or the fists,
            // three or more hunting the bot are evaded or shot. Ghost spent
            // 28 of its 30 Heartbreaker rounds on single walkers at 1-3m
            // through the keep-the-weapon-in-hand rules below.
            if (!IsGunWorthyFight())
            {
                LogTryhardBladeForCommon(target);
                return true;
            }
            // 0.2.2x7 (doc 16.13): a loaded gun in hand stays there with the
            // zombie within 3m or another one within 8m (Tadeu took the knife
            // out for a crawler at 2.6m right after a PDW kill, with another
            // zombie close, and bled out).
            if (m_Player.equipment.asset is ItemGunAsset handGun
                && IsEquippedGunLoaded(handGun)
                && IsGunHeldForCloseFight(target))
            {
                return false;
            }
            // 0.2.2x8 (doc 16.16): 2+ zombies within 8m and a loaded gun
            // carried: the bullet, unless the knife is in hand with one
            // within 3m.
            // 0.2.2x14: no swap with a zombie within 5m while the weapon in
            // hand can fight; the blade, quiet, next to resting zombies.
            var keepHand = KeepHandWeaponForSwap(target);
            if (keepHand.HasValue)
            {
                return keepHand.Value;
            }
            if (PreferQuietBlade(target))
            {
                return true;
            }
            var hordeChoice = HordeBladeOverride(target);
            if (hordeChoice.HasValue)
            {
                return hordeChoice.Value;
            }
            if (target == m_BladeChoiceTarget)
            {
                return m_BladeChoice;
            }
            if (m_GunStyle < 0f)
            {
                m_GunStyle = Random.Range(0.2f, 0.9f);
                BotLog(LogLevel.Information,
                    "Estilo de luta deste bot: {Gun:0}% das lutas com zumbi comum na bala, o resto na faca.",
                    m_GunStyle * 100f);
            }
            var chance = m_GunStyle;
            var reason = "estilo";
            switch (target.speciality)
            {
                case EZombieSpeciality.NORMAL:
                case EZombieSpeciality.CRAWLER:
                case EZombieSpeciality.SPRINTER:
                case EZombieSpeciality.FLANKER_FRIENDLY:
                case EZombieSpeciality.FLANKER_STALK:
                    break;
                default:
                    if (chance < 0.85f)
                    {
                        chance = 0.85f;
                        reason = "zumbi especial";
                    }
                    break;
            }
            var position = m_Player.transform.position;
            var crowd = 0;
            foreach (var perceived in m_PerceivedZombies)
            {
                var zombie = perceived.Key;
                if (zombie != null
                    && zombie != target
                    && !zombie.isDead
                    && zombie.gameObject.activeInHierarchy
                    && (zombie.transform.position - position).GetHorizontalMagnitude() <= 15f)
                {
                    crowd++;
                }
            }
            if (crowd >= 1 && chance < 0.7f)
            {
                chance = 0.7f;
                reason = (crowd + 1) + " zumbis em 15m";
            }
            m_BladeChoiceTarget = target;
            m_BladeChoice = Random.value >= chance;
            if (m_BehaviorNow >= m_NextBladeLogAt)
            {
                m_NextBladeLogAt = m_BehaviorNow + 10f;
                BotLog(LogLevel.Information,
                    "Luta com zumbi {Kind} a {Distance:0.#}m: {Choice} ({Chance:0}% de chance de bala, {Reason}).",
                    DescribeZombieKind(target),
                    (target.transform.position - position).GetHorizontalMagnitude(),
                    m_BladeChoice ? "na faca" : "na bala",
                    chance * 100f,
                    reason);
            }
            return m_BladeChoice;
        }

        // ---- item 8: the view ----
        // The players' pitch walking, degrees down: p10 2, p25 11, p50 21,
        // p75 31, p90 43.
        private static readonly float[] s_WalkPitchQuantiles = { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f };
        private static readonly float[] s_WalkPitchValues = { -2f, 2f, 11f, 21f, 31f, 43f, 50f };

        private static float DrawFromQuantiles(float[] quantiles, float[] values)
        {
            var u = Random.value;
            for (var i = 1; i < quantiles.Length; i++)
            {
                if (u <= quantiles[i])
                {
                    var t = (u - quantiles[i - 1]) / Mathf.Max(0.0001f, quantiles[i] - quantiles[i - 1]);
                    return Mathf.Lerp(values[i - 1], values[i], t);
                }
            }
            return values[values.Length - 1];
        }

        private static float DrawPlayerWalkPitch()
        {
            return DrawFromQuantiles(s_WalkPitchQuantiles, s_WalkPitchValues);
        }

        // A turn of the body eases in like the players' (half of a turn is
        // done at 59% of its time; the bots' started at full speed).
        private float m_TurnAccel = 2400f;
        private float m_YawTurnRate;

        private float ApplyTurnRamp(float target, float delta, bool aiming)
        {
            if (!s_Settings.HumanTurning || aiming)
            {
                m_YawTurnRate = target;
                return target;
            }
            if (delta < 3f)
            {
                m_YawTurnRate = 0f;
                return target;
            }
            m_YawTurnRate = Mathf.MoveTowards(m_YawTurnRate, target, m_TurnAccel * TurnStepSeconds);
            return Mathf.Clamp(m_YawTurnRate, 40f, Mathf.Max(40f, target));
        }

        // ---- item 9: the watch from high ground ----
        // The players' stops on high ground (y 48+, no zombie within 25m):
        // 8s median, 14s p75, 40s p90; prone 38%, crouched 15%; aiming 18%
        // (4% below); the view sweeps about 6 deg/s.
        private static readonly float[] s_WatchQuantiles = { 0f, 0.5f, 0.75f, 0.9f, 1f };
        // 0.2.2x13: capped at 20s (Tadeu sat 35s crouched on a Military Tower
        // with 7 loot points of the area still to sweep).
        private static readonly float[] s_WatchSeconds = { 6f, 8f, 12f, 16f, 20f };
        private const float HighWatchRiseMeters = 6f;
        private const float HighWatchSpotGapMeters = 60f;
        private float m_HighWatchUntil;
        private float m_NextHighWatchCheckAt;
        private float m_HighWatchBaseYaw;
        private float m_HighWatchYaw;
        private float m_HighWatchPitch;
        private float m_HighWatchDirection = 1f;
        private float m_HighWatchSpeed = 6f;
        private float m_HighWatchLastAt;
        private int m_HighWatchPosture;
        private bool m_HighWatchSpotSet;
        private Vector3 m_HighWatchSpot;

        private bool IsHighWatching(float now)
        {
            return Tryhard && now < m_HighWatchUntil && IsHabitPaused(now);
        }

        private bool TryUpdateHighWatch(Vector3 position, float routeYaw, float now)
        {
            if (!Tryhard)
            {
                return false;
            }
            if (m_HighWatchUntil > 0f)
            {
                if (IsHighWatching(now) && m_CachedNearestZombieDistance >= 25f)
                {
                    var step = Mathf.Clamp(now - m_HighWatchLastAt, 0f, 0.25f);
                    m_HighWatchLastAt = now;
                    m_HighWatchYaw += m_HighWatchDirection * m_HighWatchSpeed * step;
                    if (Mathf.Abs(Mathf.DeltaAngle(m_HighWatchBaseYaw, m_HighWatchYaw)) > 75f)
                    {
                        m_HighWatchDirection = -m_HighWatchDirection;
                        m_HighWatchSpeed = Random.Range(4f, 9f);
                    }
                    m_DesiredYaw = m_HighWatchYaw;
                    m_DesiredPitch = 90f + m_HighWatchPitch;
                    m_IsSprinting = false;
                    return true;
                }
                m_HighWatchUntil = 0f;
                m_HabitPauseUntil = 0f;
                return false;
            }
            if (now < m_NextHighWatchCheckAt)
            {
                return false;
            }
            m_NextHighWatchCheckAt = now + 8f;
            if (now < m_RespawnRushUntil
                || m_BossHuntActive
                || !(m_Player.equipment.asset is ItemGunAsset)
                || !m_Player.movement.isGrounded
                || m_PathPending
                || m_CachedNearestZombieDistance < 25f
                // 0.2.2x13: never on the way somewhere or while an area is
                // being swept; only on a free local walk.
                || m_ObjectiveType == ObjectiveType.Travel
                || m_ObjectiveType == ObjectiveType.Explore
                || m_HasInterestArea
                || (m_HighWatchSpotSet && (position - m_HighWatchSpot).GetHorizontalMagnitude() < HighWatchSpotGapMeters)
                || !IsHighOpenGround(position, out var rise, out var open, out var openYaw))
            {
                return false;
            }
            if (Random.value < 0.5f)
            {
                m_NextHighWatchCheckAt = now + 30f;
                return false;
            }
            var seconds = DrawFromQuantiles(s_WatchQuantiles, s_WatchSeconds);
            var roll = Random.value;
            m_HighWatchPosture = roll < 0.38f ? 2 : roll < 0.53f ? 1 : 0;
            m_HighWatchUntil = now + seconds;
            m_HabitPauseUntil = m_HighWatchUntil;
            // The stop is not a point the bot failed to reach.
            m_ObjectiveExpiresAt += seconds;
            m_InterestAreaExpiresAt += seconds;
            m_HabitPauseCrouch = false;
            // Facing the open side, not the route: the sweep looked at the
            // tower's inner wall and window.
            m_HighWatchBaseYaw = openYaw + Random.Range(-20f, 20f);
            m_HighWatchYaw = m_HighWatchBaseYaw;
            m_HighWatchPitch = Random.Range(-1f, 6f);
            m_HighWatchDirection = Random.value < 0.5f ? -1f : 1f;
            m_HighWatchSpeed = Random.Range(4f, 9f);
            m_HighWatchLastAt = now;
            m_HighWatchSpot = position;
            m_HighWatchSpotSet = true;
            m_IsSprinting = false;
            BotLog(LogLevel.Information,
                "Vigia no alto em {Position} ({Rise:0}m acima do terreno em volta, {Open} de 8 direcoes abertas): parado {Seconds:0}s {Posture}, varrendo o horizonte atras de gente.",
                FormatPosition(position),
                rise,
                open,
                seconds,
                m_HighWatchPosture == 2 ? "deitado" : m_HighWatchPosture == 1 ? "agachado" : "em pe");
            return true;
        }

        // High: the bot stands 6m+ over the terrain 30m around (a hill or a
        // roof). Open: from the eye, 4 of 8 directions clear for 40m.
        private static bool IsHighOpenGround(Vector3 position, out float rise, out int open, out float openYaw)
        {
            rise = 0f;
            open = 0;
            openYaw = 0f;
            var sum = 0f;
            for (var i = 0; i < 8; i++)
            {
                var direction = Quaternion.Euler(0f, i * 45f, 0f) * Vector3.forward;
                sum += LevelGround.getHeight(position + direction * 30f);
            }
            rise = position.y - sum / 8f;
            if (rise < HighWatchRiseMeters)
            {
                return false;
            }
            var eye = position + Vector3.up * 1.6f;
            var isOpen = new bool[8];
            for (var i = 0; i < 8; i++)
            {
                var direction = Quaternion.Euler(0f, i * 45f + 22.5f, 0f) * Vector3.forward;
                if (!Physics.Raycast(eye, direction, 40f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    open++;
                    isOpen[i] = true;
                }
            }
            // The middle of the widest run of open directions.
            var bestRun = -1;
            for (var i = 0; i < 8; i++)
            {
                if (!isOpen[i])
                {
                    continue;
                }
                var run = 0;
                while (run < 8 && isOpen[(i + run) % 8])
                {
                    run++;
                }
                if (run > bestRun)
                {
                    bestRun = run;
                    openYaw = i * 45f + 22.5f + (run - 1) * 22.5f;
                }
            }
            return open >= 4;
        }

        // ---- item 11: two fences, two corners ----
        // Tadeu (22:34, 22:37) and llo (22:36) turned for minutes between
        // Fence_Wood_0 and Fence_Wood_Broken_0: the corner of one led to the
        // other, whose corner led back, 1.4m apart. A fence low enough is
        // hopped; two detours that flip twice are dropped for 20s there.
        private BotMapObject? m_LastDetourBlocker;
        private Vector3 m_LastDetourCorner;
        private float m_LastDetourAt;
        private int m_DetourFlipCount;
        private float m_DetourSuppressUntil;
        private Vector3 m_DetourSuppressAt;

        private static bool IsHoppableFence(BotMapObject mapObject, float feetY)
        {
            var top = mapObject.MaxY - feetY;
            return mapObject.Category == BotObjectCategory.Fence && top >= 0.2f && top <= 1.3f;
        }

        private bool IsDetourSuppressed(Vector3 position, float now)
        {
            return now < m_DetourSuppressUntil
                && (position - m_DetourSuppressAt).GetHorizontalMagnitude() < 6f;
        }

        private bool IsDetourPingPong(BotMapObject blocker, Vector3 corner, Vector3 position, float now)
        {
            var recent = now - m_LastDetourAt < 8f;
            var flip = recent
                && m_LastDetourBlocker != null
                && !ReferenceEquals(m_LastDetourBlocker, blocker)
                && (corner - m_LastDetourCorner).GetHorizontalMagnitude() < 2.5f;
            m_DetourFlipCount = flip ? m_DetourFlipCount + 1 : recent ? m_DetourFlipCount : 0;
            var previous = m_LastDetourBlocker;
            m_LastDetourBlocker = blocker;
            m_LastDetourCorner = corner;
            m_LastDetourAt = now;
            if (m_DetourFlipCount < 2)
            {
                return false;
            }
            m_DetourFlipCount = 0;
            m_DetourSuppressUntil = now + 20f;
            m_DetourSuppressAt = position;
            m_FurnitureDetourActive = false;
            BotLog(LogLevel.Information,
                "{First} e {Second}: o desvio alterna entre as quinas dos dois em {Position}; seguindo a rota direto por 20s (pula se for baixo).",
                previous?.Name ?? "objeto",
                blocker.Name,
                FormatPosition(position));
            return true;
        }

        // ---- item 14: an empty sweep ----
        // 22:52-23:07 the three bots swept Seattle's empty streets: no kill,
        // no item. 4 min of that and the bot goes to a strong spot for 15 min.
        private const float HuntBoredSeconds = 240f;
        private const float HuntBoredRestSeconds = 900f;
        private float m_HuntProductiveAt;
        private int m_HuntSeenKills;
        private int m_HuntSeenLoots;
        private float m_HuntBoredUntil;

        private void UpdateHuntSweepBoredom(float now)
        {
            if (!Tryhard)
            {
                return;
            }
            if (!IsHuntSweep
                // Still on the way there: the trip is not the sweep.
                || (m_BehaviorPosition - s_HuntCenter).GetHorizontalMagnitude() > HuntSweepRadiusMeters + 60f
                || m_StatKillsTotal != m_HuntSeenKills
                || m_StatLootsTotal != m_HuntSeenLoots
                || IsFightObjective()
                || m_BossHuntActive
                || m_HuntProductiveAt <= 0f)
            {
                m_HuntSeenKills = m_StatKillsTotal;
                m_HuntSeenLoots = m_StatLootsTotal;
                m_HuntProductiveAt = now;
                return;
            }
            if (now - m_HuntProductiveAt < HuntBoredSeconds)
            {
                return;
            }
            m_HuntProductiveAt = now;
            m_HuntBoredUntil = now + HuntBoredRestSeconds;
            BotLog(LogLevel.Information,
                "Ponto de caca {Name}: {Minutes:0} min varrendo sem abate nem item; indo para um ponto forte por {Rest:0} min.",
                s_HuntName,
                HuntBoredSeconds / 60f,
                HuntBoredRestSeconds / 60f);
            FinishInterestArea(now);
            if (m_ObjectiveType == ObjectiveType.Patrol || m_ObjectiveType == ObjectiveType.Explore)
            {
                ClearObjective();
            }
        }
    }
}
