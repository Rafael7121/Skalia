using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y2 (the tester, 17/11: "escutar passo e barulho de tiros e ficar
    // atento ... ele so me notou quando atirei nele, antes realizei 3 ou 4
    // disparos"). In the short test of 17/11 the tester fired 3 times at 28 to
    // 43m behind Vultox (00:44:21-24) and Vultox never turned.
    //
    // The players of 08/11 (rec/pvp13.py, pvp14.py), a shot from another
    // player they had out of their 120 degrees (more than 60 off):
    // - within 100m they faced it (30 degrees) inside 5s 7 times in 11
    //   (64%), against 42% at any moment with no shot; after 0.5, 1.3, 1.3,
    //   1.5, 1.7, 3.0 and 3.2s;
    // - past 100m no more than with no shot (100-300m 33% vs 37%, 600-1000m
    //   20% vs 29%): not heard as something to look at;
    // - they kept looking 0.1, 0.2, 0.3, 1.3, 2.9, 5.8 and 14.7s, and most
    //   kept walking.
    // The game: a gun is heard to Gunshot_Rolloff_Distance (512m, a bow
    // 16m), a silencer halves it (Gunshot_Rolloff_Distance_Multiplier).
    //
    // Footsteps: no answer to them in the recordings above chance (within
    // 30m, players walking or still, 27 to 60% either way; the players
    // close to each other were mostly together), so none here yet.
    //
    // The bot turns the way the shot came from; what it then sees is found
    // by the usual vision (SkaliaBotPvpBotsY): the same 120 degrees and line
    // of sight, no wallhack.
    //
    // 0.2.2y9 (the tester, 17/11: "a reacao deles quando eu atiro e nula ...
    // dei 2 tiros a queima roupa e nada"). At 08:16:34 he fired twice at
    // Ghost's back from 10-12m; Ghost drew the slowest turn of the table
    // (3.2s) and a 0.2s look, and never saw him. The same players of 08/11
    // (rec/pvp21.py), a shot from someone aiming at them (within 10 degrees)
    // from 20m or less while they faced away: 4 times in 4 they faced him,
    // after 0.2, 0.3, 0.5 and 5.3s. So a shot at the bot from close is
    // always turned to, on that table, and it comes before a slower turn
    // already waiting.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float HearAimedMeters = 20f;
        private const float HearAimedDegrees = 10f;
        private static readonly float[] s_HearAimedQuantiles = { 0f, 1f / 3f, 2f / 3f, 1f };
        private static readonly float[] s_HearAimedTurnSeconds = { 0.2f, 0.3f, 0.5f, 5.3f };
        private const float HearShotMeters = 100f;
        private const float HearTurnChance = 0.64f;
        private static readonly float[] s_HearQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_HearTurnSeconds = { 0.5f, 1.3f, 1.5f, 3.0f, 3.2f };
        private static readonly float[] s_HearLookSeconds = { 0.1f, 0.3f, 1.3f, 4.3f, 14.7f };

        private static bool s_HearingHooked;

        private Vector3 m_HeardPoint;
        private float m_HeardTurnAt = float.PositiveInfinity;
        private float m_HeardLookUntil = float.NegativeInfinity;
        private bool m_HeardLooking;
        private Player? m_HeardAimedBy;
        private float m_HeardLookFor;
        private float m_HeardYaw;
        private float m_HeardPitch;

        internal static void HookHearing()
        {
            if (s_HearingHooked)
            {
                return;
            }
            UseableGun.onBulletSpawned += OnBulletSpawnedHeard;
            s_HearingHooked = true;
        }

        internal static void UnhookHearing()
        {
            if (!s_HearingHooked)
            {
                return;
            }
            UseableGun.onBulletSpawned -= OnBulletSpawnedHeard;
            s_HearingHooked = false;
        }

        // Runs inside the game's shot (server side, once per pellet):
        // nothing here may stop it.
        private static void OnBulletSpawnedHeard(UseableGun gun, BulletInfo bullet)
        {
            try
            {
                if (bullet == null || bullet.pellet != 0 || gun == null || s_ActiveSimulations.Count == 0)
                {
                    return;
                }
                var shooter = gun.player;
                var asset = gun.equippedGunAsset;
                if (shooter == null || asset == null || shooter.channel?.owner == null)
                {
                    return;
                }
                var reach = asset.gunshotRolloffDistance;
                if (bullet.barrelAsset != null && shooter.equipment.state != null
                    && shooter.equipment.state.Length > 16 && shooter.equipment.state[16] > 0)
                {
                    reach *= bullet.barrelAsset.gunshotRolloffDistanceMultiplier;
                }
                reach = Mathf.Min(reach, HearShotMeters);
                var origin = bullet.origin;
                foreach (var pair in s_ActiveSimulations)
                {
                    if (pair.Key != shooter)
                    {
                        pair.Value.HearShot(shooter, origin, reach);
                    }
                }
            }
            catch (System.Exception)
            {
                // A shot heard or not is never worth the game's own shot.
            }
        }

        private void HearShot(Player shooter, Vector3 origin, float reach)
        {
            if (m_Player == null || m_Player.life == null || m_Player.life.isDead
                || IsPvpAlly(shooter))
            {
                return;
            }
            var now = Time.realtimeSinceStartup;
            var eye = m_Player.look.aim.position;
            var distance = (origin - eye).magnitude;
            if (distance > reach)
            {
                return;
            }
            var aimed = distance <= HearAimedMeters && IsShotAimedAtMe(shooter, eye);
            // 0.1.0-y17.3: a round at it from the man it was waiting to open
            // on: he has seen it (SkaliaBotPvpBotsY, IsPvpFireHeld).
            if (aimed && shooter == m_PvpFireNotBeforeTarget && !m_PvpFireNotBeforeFirm)
            {
                m_PvpFireNotBefore = 0f;
            }
            var inView = IsInPvpView(origin);
            if (inView && !Physics.Linecast(eye, origin, s_PvpSightMask, QueryTriggerInteraction.Ignore))
            {
                // In front and in line: the vision has him.
                return;
            }
            if (m_PvpTarget != null)
            {
                // 0.1.0-y17: in a fight with a man it has not found, a new
                // shot moves the sector it hides from (SkaliaBotShotSectorY).
                if (!m_PvpAware)
                {
                    NoteShotDuringUnknownFight(shooter, origin,
                        aimed || IsShotLandingNearMe(shooter, eye, distance), now);
                    return;
                }
                if (shooter != m_PvpTarget)
                {
                    HearSecondShooter(shooter, origin, eye, distance, aimed, now);
                }
                return;
            }
            // 0.1.0-y17.3 (the tester, 18/11: "eu atiro ele escuta mas fica
            // parado exposto observando da onde veio o som, nao vai pra
            // cobertura, nao busca flankear/recuar da a volta"). His
            // recording: of his 21 rounds 12:07:07-12:08:52, 6 went at
            // ToxicSniper from 23-43m within 7 degrees of it (12:07:13, :20,
            // :33, :45, 12:08:17, :19); past the 20m of the turn above they
            // counted as any shot, and it went on looting. Rounds at it from
            // 20-60m (the range the players went after a man,
            // SkaliaBotPvpHuntY), landing within 3.5m of it (10 degrees at
            // 20m), are a man shooting at it that it does not see: out of
            // that line to cover, hidden, then around (SkaliaBotShotSectorY).
            // A shot not at it stays as the players had it above (turn to it
            // 64% of the time, look, walk on): with no round near it a bot
            // firing at zombies 50m off is not a man to hide from.
            // A round at it from in front, from behind something: no turn to
            // make, the same (it used to be dropped here).
            // 0.1.0-y17.12: out to all it hears (100m), not only the 60m.
            var aimedFar = !aimed && IsShotLandingNearMe(shooter, eye, distance);
            if ((aimedFar || (aimed && inView)) && shooter.life != null && !shooter.life.isDead)
            {
                NotePvpShotHeard(shooter.channel.owner.playerID.characterName, distance, true);
                NoteShotSector(shooter, origin, now, out _);
                ResetHeardLook();
                StartPvpUnknownThreat(shooter, now, $"tiro passou por mim a {distance:0}m de quem nao vejo");
                return;
            }
            // 0.1.0-y17.11 (the tester, 18/11: "a reacao deles ao ouvir tiro e
            // quase nula, quase nunca buscam cobertura, achar o inimigo").
            // In the tests of 18/11 the shots heard from him at 16-98m, not
            // at the bot, gave a turn and a look of 0.1 to 0.8s most of the
            // time (13:09:08, 17:34:02, 17:35:48-17:37:22, 18:22:11-18:23:26,
            // 18:48:51), and the bot went on with its loot. the tester (18/11):
            // it depends on where the bot is. In the middle of the military
            // base with the shot outside, he can come over the wall and shoot
            // at any moment: it hides. Already in a place with closed angles
            // from that side (the shot from the open field or the street),
            // staying on the loot makes sense. So within the 60m the
            // players went after a man (SkaliaBotPvpHuntY), the side of the
            // shot is looked at from where the bot stands: from points on
            // the ground along the sector (8m out to where it believes the
            // shot was, the middle and both edges), can a man's eye see its
            // chest? Any open line: out of the line to cover, hidden
            // watching the sector with the gun in hand, then around to find
            // him (as a round at it, above). None: what it was doing goes
            // on, with the turn and the look as before. A bot in a fight
            // already (the one shooting into the base) does not get here.
            // 0.1.0-y17.12 (the tester, 18/11: "o barulho do tiro continua sendo
            // ignorado, teste com theghostsniper scorpion 7"; "so ficamos
            // parado mirando pro barulho quando estamos com cobertura").
            // At 22:33:44 Ghost heard the tester at 73m, past the 60m, and only
            // looked (11.3s) walking in the open. Every shot it hears now
            // (out to 100m): open on that side, to cover; closed, the look.
            if (shooter.life != null && !shooter.life.isDead)
            {
                NoteShotSector(shooter, origin, now, out _);
                var open = CountShotSideOpenLines(now, out var tried);
                var shooterName = shooter.channel.owner.playerID.characterName;
                if (open > 0)
                {
                    NotePvpShotHeard(shooterName, distance, aimed);
                    ResetHeardLook();
                    StartPvpUnknownThreat(shooter, now,
                        $"ouvi tiro a {distance:0}m de quem nao vejo, e daqui um homem daquele lado me ve de {open} de {tried} pontos");
                    return;
                }
                if (now >= m_HeardClosedLogAt)
                {
                    m_HeardClosedLogAt = now + 10f;
                    BotLog(LogLevel.Information,
                        "Ouvi tiro de {Name} a {Distance:0}m fora da vista, mas aqui estou fechado para aquele lado (0 de {Tried} pontos me veem); sigo no que estava fazendo, de olho.",
                        shooterName,
                        distance,
                        tried);
                }
            }
            if (inView)
            {
                return;
            }
            if (now < m_HeardLookUntil && (!aimed || m_HeardLooking || now >= m_HeardTurnAt))
            {
                // Already turning to (or looking at) one: the first stands,
                // unless this one is at the bot and the turn is still to come.
                return;
            }
            var name = shooter.channel.owner.playerID.characterName;
            // 0.1.0-y16: quiet, not to give itself away (SkaliaBotPvpSpotY).
            NotePvpShotHeard(name, distance, aimed);
            if (!aimed && Random.value > HearTurnChance)
            {
                BotLog(LogLevel.Debug, "Ouvi tiro de {Name} a {Distance:0}m fora da tela; nao virei.", name, distance);
                return;
            }
            var turnIn = aimed
                ? DrawFromQuantiles(s_HearAimedQuantiles, s_HearAimedTurnSeconds)
                : DrawFromQuantiles(s_HearQuantiles, s_HearTurnSeconds);
            if (aimed && now < m_HeardLookUntil && now + turnIn >= m_HeardTurnAt)
            {
                return;
            }
            // 0.1.0-y17: where it believes the shot came from, with the
            // players' error, not the point the round left from.
            NoteShotSector(shooter, origin, now, out var believed);
            var lookFor = DrawFromQuantiles(s_HearQuantiles, s_HearLookSeconds);
            if (aimed)
            {
                // Long enough for one vision scan once the head is there.
                lookFor = Mathf.Max(lookFor, PvpOpenScanSeconds + 0.05f);
            }
            m_HeardPoint = believed;
            m_HeardAimedBy = aimed ? shooter : null;
            m_HeardLookFor = lookFor;
            m_HeardTurnAt = now + turnIn;
            m_HeardLookUntil = m_HeardTurnAt + lookFor;
            BotLog(LogLevel.Information,
                "Ouvi tiro de {Name} a {Distance:0}m fora da tela{Aimed}; olhando para o setor {Sector} em {Turn:0.0}s por {Look:0.0}s.",
                name,
                distance,
                aimed ? " (mirando em mim)" : string.Empty,
                DescribeShotSector(now),
                turnIn,
                lookFor);
        }

        // 0.1.0-y17.12 (the tester, 18/11: "alemao travou uma hora que atirei
        // nas costas dele mt perto n acertei mas ele travou, acho que estava
        // lutando com o sniper"). 22:36:40-22:37:20: AlemaoGamer, after
        // Vultox at 165m, then watching where it lost him, stood two
        // minutes at one spot while the tester fired from 36-50m behind it
        // (140-150 degrees off its face): a shot from anyone but the man it
        // fought was dropped. Now a second man's shot:
        // - not in a shooting exchange (watching, looking, hiding): the same
        //   test as a shot with no fight (open on that side, or the round at
        //   it): the first one is remembered and this one is the new threat;
        // - in an exchange: a round at it from a man nearer than the one it
        //   shoots at makes him the threat; else he is remembered for after.
        private void HearSecondShooter(Player shooter, Vector3 origin, Vector3 eye, float distance, bool aimed, float now)
        {
            if (shooter.life == null || shooter.life.isDead || shooter.channel?.owner == null)
            {
                return;
            }
            var atMe = aimed || IsShotLandingNearMe(shooter, eye, distance);
            var current = (m_PvpLastSeenPoint - eye).magnitude;
            var name = shooter.channel.owner.playerID.characterName;
            NoteShotSector(shooter, origin, now, out var believed);
            string? why = null;
            if (m_PvpEngaged)
            {
                if (atMe && distance < current)
                {
                    why = $"no meio da luta, tiro de outro a {distance:0}m passou por mim (o de antes a {current:0}m)";
                }
            }
            else if (atMe)
            {
                why = $"tiro de outro a {distance:0}m passou por mim enquanto vigiava";
            }
            else
            {
                var open = CountShotSideOpenLines(now, out var tried);
                if (open > 0)
                {
                    why = $"ouvi tiro de outro a {distance:0}m enquanto vigiava, e daqui um homem daquele lado me ve de {open} de {tried} pontos";
                }
            }
            if (why == null)
            {
                if (!m_PvpOthers.Exists(o => o.Who == shooter))
                {
                    RememberPvpOtherAt(shooter, believed, now, false);
                }
                return;
            }
            RememberPvpOther(m_PvpTarget, now);
            ResetPvpHunt();
            ResetHeardLook();
            NotePvpShotHeard(name, distance, atMe);
            StartPvpUnknownThreat(shooter, now, why);
        }

        // Points on the ground on the side of the shot (SkaliaBotShotSectorY)
        // from which a man's eye sees the bot's chest; tried counts the
        // points with ground under them.
        private static readonly float[] s_ShotSideMeters = { 8f, 15f, 25f, 40f };
        private const float ShotSideMaxHalfDegrees = 45f;
        private float m_HeardClosedLogAt = float.NegativeInfinity;

        private int CountShotSideOpenLines(float now, out int tried)
        {
            tried = 0;
            var open = 0;
            var feet = m_Player.transform.position;
            var half = Mathf.Min(ShotSectorHalfNow(now), ShotSideMaxHalfDegrees);
            var far = Mathf.Max(5f, m_ShotSectorMeters);
            var offsets = half < 1f ? new[] { 0f } : new[] { -half, 0f, half };
            var distances = new System.Collections.Generic.List<float>();
            foreach (var step in s_ShotSideMeters)
            {
                if (step < far)
                {
                    distances.Add(step);
                }
            }
            distances.Add(far);
            foreach (var offset in offsets)
            {
                var direction = Quaternion.Euler(0f, m_ShotSectorYaw + offset, 0f) * Vector3.forward;
                foreach (var meters in distances)
                {
                    var probe = feet + direction * meters;
                    if (!TryGetWalkableGround(probe, out var ground))
                    {
                        probe.y = LevelGround.getHeight(probe);
                        if (!TryGetWalkableGround(probe, out ground))
                        {
                            continue;
                        }
                    }
                    tried++;
                    if (IsChestInLineFrom(ground + Vector3.up * 1.6f, feet))
                    {
                        open++;
                    }
                }
            }
            return open;
        }

        // 0.1.0-y17.3: from further than HearAimedMeters, the round's line
        // passing within the 3.5m that 10 degrees make at 20m.
        private const float HearAimedFarMissMeters = 3.5f;

        private bool IsShotLandingNearMe(Player shooter, Vector3 eye, float distance)
        {
            var aim = shooter.look?.aim;
            if (aim == null || distance <= 0.1f)
            {
                return false;
            }
            var feet = m_Player.transform.position;
            var chest = Vector3.Lerp(feet, eye, 0.7f);
            var to = chest - aim.position;
            if (Vector3.Dot(aim.forward, to) <= 0f)
            {
                return false;
            }
            return Vector3.Cross(aim.forward.normalized, to).magnitude <= HearAimedFarMissMeters;
        }

        // The shooter's aim within a few degrees of the bot's body.
        private bool IsShotAimedAtMe(Player shooter, Vector3 eye)
        {
            var aim = shooter.look?.aim;
            if (aim == null)
            {
                return false;
            }
            var feet = m_Player.transform.position;
            var chest = Vector3.Lerp(feet, eye, 0.7f);
            var to = chest - aim.position;
            return to.sqrMagnitude > 0.01f && Vector3.Angle(aim.forward, to) <= HearAimedDegrees;
        }

        // From TickPvp with no player being fought, before the vision scan:
        // the head goes to where the shot was, the vision does the rest.
        private void TickHeardLook(float now)
        {
            var wanted = now >= m_HeardTurnAt
                && now < m_HeardLookUntil
                && m_PvpTarget == null
                && !m_Player.life.isDead
                && m_CachedNearestZombieDistance > PvpZombieFirstMeters
                && m_ObjectiveType != ObjectiveType.Combat
                && m_ObjectiveType != ObjectiveType.EscapeSwarm
                && m_ObjectiveType != ObjectiveType.Flee
                && m_ObjectiveType != ObjectiveType.EscapeHazard
                && m_TreatmentPhase == 0
                && m_Player.stance.stance != EPlayerStance.CLIMB
                && m_Player.stance.stance != EPlayerStance.SWIM
                && m_Player.movement.getVehicle() == null
                && !IsLadderInProgress();
            if (!wanted)
            {
                // 0.1.0-y17: a round at the bot from a man the look did not
                // find: hidden from that side until it finds him
                // (SkaliaBotShotSectorY).
                if (m_HeardAimedBy != null && m_HeardLooking && now >= m_HeardLookUntil && m_PvpTarget == null
                    && !m_Player.life.isDead && m_HeardAimedBy.life != null && !m_HeardAimedBy.life.isDead)
                {
                    StartPvpUnknownThreat(m_HeardAimedBy, now);
                }
                if (m_HeardLooking || now >= m_HeardLookUntil)
                {
                    m_HeardTurnAt = float.PositiveInfinity;
                    m_HeardLookUntil = float.NegativeInfinity;
                    m_HeardAimedBy = null;
                }
                m_HeardLooking = false;
                return;
            }
            if (!m_HeardLooking)
            {
                m_HeardLooking = true;
                m_HeardYaw = m_CurrentYaw;
                m_HeardPitch = m_CurrentPitch;
            }
            var to = m_HeardPoint - m_Player.look.aim.position;
            if (to.sqrMagnitude < 0.01f)
            {
                return;
            }
            var horizontal = new Vector2(to.x, to.z).magnitude;
            var wantPitch = 90f - Mathf.Atan2(to.y, horizontal) * Mathf.Rad2Deg;
            // 0.1.0-y17.19: the players' hand (SkaliaBotPvpHandY).
            StepPvpHand(ref m_HeardYaw, ref m_HeardPitch, DirectionToYaw(to), wantPitch, 0f, 0f, now);
            // The players' look was timed from the head being on him
            // (within 30 degrees); a shot at the bot is looked at that long
            // after the turn, however long the turn took.
            if (m_HeardAimedBy != null && Mathf.Abs(Mathf.DeltaAngle(m_HeardYaw, DirectionToYaw(to))) > 30f)
            {
                m_HeardLookUntil = Mathf.Max(m_HeardLookUntil, now + m_HeardLookFor);
            }
            m_CurrentYaw = m_HeardYaw;
            m_CurrentPitch = m_HeardPitch;
            m_DesiredYaw = m_HeardYaw;
            m_DesiredPitch = m_HeardPitch;
            // Most of the players kept walking while they looked: the walk
            // goes on in whichever of the 8 directions fits the new facing
            // (UpdateNavigationAnalog), only not at a run.
            m_IsSprinting = false;
        }

        // The man who just shot at the bot from close, seen where the shot
        // came from: the turn was the reaction (TryOpenFireOnBot).
        private bool IsHeardShooter(Player player, float now)
        {
            return m_HeardAimedBy == player && m_HeardLooking && now < m_HeardLookUntil;
        }

        private void ResetHeardLook()
        {
            m_HeardAimedBy = null;
            m_HeardLooking = false;
            m_HeardTurnAt = float.PositiveInfinity;
            m_HeardLookUntil = float.NegativeInfinity;
        }
    }
}
