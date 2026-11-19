using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y7 (the tester, 17/11: "ele fica olhando para onde o outro estava,
    // mas ele nao faz mais nada. quando nos ficamos parado ficamos com
    // cobertura. ... eu fico esperando ele aparecer ... e se eu realmente
    // perder ele, eu vou me reposicionar" and "facam eles dar pre fire").
    //
    // The watch (SkaliaBotPvpHuntY) is kept from cover: when the bot's
    // chest is in line from where he was, it first goes to a spot within
    // 10m that is not, and not closer to him, where the head or the
    // third-person camera (1.1m to the side) still has the line; failing
    // that any spot that hides it. Out of the watch the routine takes it
    // elsewhere: that is the move to a new place.
    //
    // While it watches with a loaded gun in hand it holds the sights on
    // the spot. When he shows within 10 degrees of them the shot does not
    // wait the reaction of a man taken by surprise (0.8-2.1s) but the
    // quickest quarter of the players' first shots (rec/pvp9.py: 11 of 38
    // fired 0 to 0.4s after he came on the screen).
    //
    // Pre-fire: in the second after he goes out of sight within 30m on the
    // move, the trigger keeps going where he was heading when that spot is
    // in line (he went behind a tree, a post, a bush, a car).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpCoverSearchMeters = 10f;
        private const float PvpCoverFarMeters = 24f;
        // 0.1.0-y17.17 (test of 18/11, 23:36:35-23:37:46): blazex watched
        // the tester's place 92m off with the chest in his line and no cover
        // within 24m, left the watch for the routine, walked on in the open
        // and was shot twice down to 23. Past the 24m, cover up to what the
        // players hit ran in 8s (30-50m, SkaliaBotPvpFeelY).
        private const float PvpCoverRunMeters = 40f;
        private const float PvpHoldAngleMeters = 80f;
        private const float PvpHoldAngleDegrees = 10f;
        private const float PvpPrefireMeters = 30f;
        private const float PvpPrefireSeconds = 1f;
        private static readonly float[] s_PvpHeldQuantiles = { 0f, 0.64f, 0.91f, 1f };
        private static readonly float[] s_PvpHeldSeconds = { 0.1f, 0.1f, 0.2f, 0.4f };

        private bool m_PvpHuntCoverDone;
        private float m_PvpPrefireLogAt = float.NegativeInfinity;

        private bool IsChestInLineFrom(Vector3 eye, Vector3 ground)
        {
            return !Physics.Linecast(eye, ground + Vector3.up * 1.3f, s_PvpSightMask, QueryTriggerInteraction.Ignore);
        }

        // 0.1.0-y17.15 (test of 18/11, 23:24:37-23:25:09): blazex lost
        // the tester at 33m, prone beside a pine (the bot's eye had been blocked
        // by Pine_1 at 32.2m, a metre from him), and stood 24s in the open
        // field facing him: the one line from his point hit the trunk, so
        // the open watch and the hunt took it for covered. From where he
        // lay he saw the bot past the trunk. The tree was his cover, not
        // the bot's: a man looks from 1.1m to either side as the bot's own
        // third-person camera does (CanPeekFrom), so a spot is hidden from
        // him only when it is hidden from those too. A side point inside a
        // wall is no place he can look from.
        // 0.1.0-y17.18 (test of 19/11; the tester: "o lugar que ele esta e
        // zoado ... ele parado em campo aberto mirando sem cover"). From
        // 00:37:43 to 00:38:36 ToxicSniper lay 54s in the grass of a field
        // watching where the tester had been 89m off; the only thing on the line
        // from that point was a stone fence 37m in front of it. A thing far
        // from the bot hides it only from that one point: him a few steps to
        // the side and the line goes past it. Moving s metres sideways at D
        // metres moves the line s*x/D at a thing x metres from the bot, so a
        // tenth of the way from the bot (2m at least) moves it under a metre
        // (a trunk, a wall's corner) while he runs 10m, what the players
        // sprint in 2s. That is the bot's own cover; the ground (a hill,
        // a bank) is wide and counts wherever it is.
        private const float PvpOwnCoverShare = 0.1f;
        private const float PvpOwnCoverMinMeters = 2f;

        private bool IsChestSeenFrom(Vector3 eye, Vector3 ground)
        {
            var chest = ground + Vector3.up * 1.3f;
            var to = eye - chest;
            var length = to.magnitude;
            if (length < 0.1f)
            {
                return true;
            }
            if (!Physics.Raycast(chest, to / length, out var hit, length, s_PvpSightMask, QueryTriggerInteraction.Ignore))
            {
                return true;
            }
            if (hit.collider is TerrainCollider)
            {
                return false;
            }
            return hit.distance > Mathf.Max(PvpOwnCoverMinMeters, length * PvpOwnCoverShare);
        }

        // 0.1.0-y17.18 (same test, 00:55:49-00:56:31): Vultox, back to Onca
        // after killing TheGhostSniper, knew him only by a shot (sector 173
        // degrees +-76) and took a spot hidden from the middle of the sector
        // only; he was on the overpass at the sector's edge, 61m, and shot it
        // from 81 to 19 in one burst. A man known by sound is anywhere in
        // the sector: the watch is hidden from all of its 5 points.
        private bool IsPvpWatchOnSound(float now)
        {
            return m_PvpHitFromAt > m_PvpLastSeenAt && !m_PvpHitFromKnown && IsShotSectorFresh(now);
        }

        private bool IsChestExposedToHim(Vector3 point, Vector3 ground, float now)
        {
            if (!IsPvpWatchOnSound(now))
            {
                return IsChestInHisLine(point, ground);
            }
            var half = ShotSectorHalfNow(now);
            for (var i = 0; i < 5; i++)
            {
                if (IsChestSeenFrom(ShotSectorPoint(Mathf.Lerp(-half, half, i / 4f)), ground))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsChestInHisLine(Vector3 eye, Vector3 ground)
        {
            if (IsChestSeenFrom(eye, ground))
            {
                return true;
            }
            var to = ground - eye;
            to.y = 0f;
            if (to.sqrMagnitude < 0.01f)
            {
                return false;
            }
            var side = Vector3.Cross(Vector3.up, to.normalized) * 1.1f;
            foreach (var offset in new[] { side, -side })
            {
                if (!Physics.Linecast(eye, eye + offset, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                    && IsChestSeenFrom(eye + offset, ground))
                {
                    return true;
                }
            }
            return false;
        }

        private bool CanPeekFrom(Vector3 eye, Vector3 ground)
        {
            var head = ground + Vector3.up * 1.6f;
            if (!Physics.Linecast(eye, head, s_PvpSightMask, QueryTriggerInteraction.Ignore))
            {
                return true;
            }
            var to = eye - head;
            to.y = 0f;
            if (to.sqrMagnitude < 0.01f)
            {
                return false;
            }
            var side = Vector3.Cross(Vector3.up, to.normalized) * 1.1f;
            return !Physics.Linecast(eye, head + side, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                || !Physics.Linecast(eye, head - side, s_PvpSightMask, QueryTriggerInteraction.Ignore);
        }

        // A spot that hides the chest from the point; with a peek if any.
        private bool TryFindPvpWatchCover(Vector3 position, Vector3 point, out Vector3 spot, out bool peek, float maxRadius = PvpCoverSearchMeters, bool straight = false, bool sector = false)
        {
            var now = Time.realtimeSinceStartup;
            spot = position;
            peek = false;
            var eye = point;
            var baseMeters = (point - position).GetHorizontalMagnitude();
            var found = false;
            var bestScore = float.MaxValue;
            for (var radius = 1.5f; radius <= maxRadius; radius += 1.5f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f, 0f) * Vector3.forward;
                    if (!TryGetPvpHuntSpot(position + direction * radius, position, out var candidate)
                        || (point - candidate).GetHorizontalMagnitude() < baseMeters - 3f
                        || (sector ? IsChestExposedToHim(eye, candidate, now) : IsChestInHisLine(eye, candidate))
                        || (straight && Physics.Linecast(position + Vector3.up * 0.6f, candidate + Vector3.up * 0.6f,
                            RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)))
                    {
                        continue;
                    }
                    var canPeek = CanPeekFrom(eye, candidate);
                    var score = radius + (canPeek ? 0f : 100f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        spot = candidate;
                        peek = canPeek;
                        found = true;
                    }
                }
                if (found && peek)
                {
                    break;
                }
            }
            return found;
        }

        // From StartPvpHuntWatch: true when the bot goes to cover first.
        private bool TryStartPvpCoverMove(float now)
        {
            if (m_PvpHuntCoverDone)
            {
                return false;
            }
            m_PvpHuntCoverDone = true;
            if (!m_PvpEnemyArmed)
            {
                return false;
            }
            var position = m_Player.transform.position;
            var point = m_PvpHuntPoint;
            // 0.1.0-y17.2: past 10m too, up to 24m (the players hit ran
            // 14-25m in 4s). the tester, 18/11 11:53: ToxicSniper lost him at
            // 26m and stood 39s in the open looking his way.
            // 0.1.0-y17.12: at any distance (the bots hit each other from
            // 150-200m); in the test of 18/11 22:29-22:39, 20 of the 23
            // watches were of a man 66-208m off, all of them where they
            // stood.
            var sector = IsPvpWatchOnSound(now);
            if (!IsChestExposedToHim(point, position, now)
                || (!TryFindPvpWatchCover(position, point, out var spot, out var peek, PvpCoverSearchMeters, false, sector)
                    && !TryFindPvpWatchCover(position, point, out spot, out peek, PvpCoverFarMeters, false, sector)
                    && !TryFindPvpWatchCover(position, point, out spot, out peek, PvpCoverRunMeters, false, sector)))
            {
                return false;
            }
            var way = (spot - position).GetHorizontalMagnitude();
            if (way < 1.5f)
            {
                return false;
            }
            m_FleeingFromPlayerName = PvpHuntName;
            var until = now + Mathf.Clamp(way / 4f + 3f, 4f, 13f);
            SetObjective(ObjectiveType.Flee, spot, until);
            m_PvpHuntPhase = 2;
            m_PvpHuntUntil = until;
            BotLog(LogLevel.Information,
                "PvP: sem cobertura de onde {Name} estava; indo para {Spot} ({Way:0}m){Peek} para vigiar.",
                m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                FormatPosition(spot),
                way,
                peek ? ", com espiada" : ", so escondido");
            return true;
        }

        // 0.1.0-y17.12 (the tester, 18/11: "so ficamos parado mirando pro
        // barulho quando estamos com cobertura e ficamos vigiando com 3
        // pessoa ... se eles ouvirem tiro e ficarem parado no campo aberto n
        // vai dar certo"). AlemaoGamer, 22:36:51-22:38:27, watched Vultox's
        // and then the tester's last place from one spot for 96s with the tester
        // 36-50m behind it; the watch had only been checked for cover when
        // it started, and only within 80m. Now, once a second, a watch with
        // the chest in line from the point it faces goes to cover (twice at
        // most for one loss of him); with none, it is not kept: the routine
        // walks on.
        private const int PvpWatchCoverMovesMax = 2;
        private int m_PvpWatchCoverMoves;
        private float m_PvpOpenWatchCheckAt = float.NegativeInfinity;

        // From the hunt's watch (phase 1): true when it left the spot.
        private bool TryLeaveOpenWatch(Player target, float now)
        {
            if (now < m_PvpOpenWatchCheckAt || !m_PvpEnemyArmed)
            {
                return false;
            }
            m_PvpOpenWatchCheckAt = now + 1f;
            var here = m_Player.transform.position;
            // 0.1.0-y17.14: watching the way in, hidden or not from where
            // he is believed, not from the door it watches.
            var from = m_PvpWayInActive ? m_PvpWatchFrom : m_PvpHuntPoint;
            if (!IsChestExposedToHim(from, here, now))
            {
                return false;
            }
            if (m_PvpWatchCoverMoves < PvpWatchCoverMovesMax)
            {
                m_PvpWatchCoverMoves++;
                m_PvpHuntCoverDone = false;
                m_PvpHuntPoint = from;
                if (TryStartPvpCoverMove(now))
                {
                    return true;
                }
            }
            // 0.1.0-y17.15 (the tester, 18/11 23:25: "no campo aberto nao pode
            // ficar parado ou ele tenta fugir ou e tudo ou nada matar ou
            // morrer"): no cover and in his line, the routine took blazex
            // away past him at 30m with its back to him. Within the hunt's
            // reach it goes for him, round his side as the players who went
            // round (SkaliaBotPvpHuntY), and looks for cover again there;
            // for as long as the longest watch (60s after the last word of
            // him), not round and round after that.
            var name = target.channel?.owner?.playerID.characterName ?? "?";
            var meters = (from - here).GetHorizontalMagnitude();
            m_PvpHuntPoint = from;
            m_PvpWayInActive = false;
            if (meters <= PvpHuntMaxMeters
                && now - Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt) <= s_PvpHuntHoldSeconds[s_PvpHuntHoldSeconds.Length - 1]
                && TryStartPvpHuntMove(here, meters, false, now, name, m_PvpLastSeenAt >= m_PvpHitFromAt))
            {
                BotLog(LogLevel.Information,
                    "PvP: vigiando {Name} no aberto a {Meters:0}m e sem cobertura; nao fico parado, vou para cima dele.",
                    name,
                    meters);
                m_PvpHiding = false;
                m_PvpHuntHealWatch = false;
                return true;
            }
            BotLog(LogLevel.Information,
                "PvP: vigiando {Name} no aberto (peito na linha de onde olho, a {Meters:0}m) e sem cobertura a {Cover:0}m; nao fico parado aqui.",
                target.channel?.owner?.playerID.characterName ?? "?",
                (m_PvpHuntPoint - here).GetHorizontalMagnitude(),
                PvpCoverRunMeters);
            m_PvpWayInActive = false;
            m_PvpHuntPhase = 0;
            m_PvpHiding = false;
            m_PvpHuntHealWatch = false;
            return true;
        }

        // 0.1.0-y17.10 (test of 18/11, 18:37:50-18:38:25): AlemaoGamer lost
        // the tester behind a low wall at 14m and watched for 36s with the
        // Butterfly Knife in hand: a zombie fight had put the knife in
        // (the rounds stay for PvP) and nothing took the gun back out,
        // since only a fight in sight equips it. A loaded Maplestrike was in
        // the bag. In the players' fights with a player a gun is in hand
        // in 93% of the rows. Looking at where it saw him, watching or
        // going to watch, with no zombie within 6m (the knife's fight), the
        // gun comes back to the hand.
        private float m_PvpWatchGunLogAt = float.NegativeInfinity;

        private void TryRaisePvpGunWhileWatching(Player target, float now)
        {
            if (!(m_PvpLooking || IsPvpHuntWatching() || IsPvpHunting())
                || m_TreatmentPhase != 0
                || m_TreatmentUseRequested
                || m_RefillUseRequested
                || m_PvpMelee
                || m_CachedNearestZombieDistance <= WeaponSwapZombieMeters
                || ((m_ObjectiveType == ObjectiveType.Combat || m_ObjectiveType == ObjectiveType.EscapeSwarm)
                    && IsTargetZombieAlive())
                || (m_Player.equipment.asset is ItemGunAsset held && IsPvpGun(held))
                || GetPvpGunRange() <= 0f)
            {
                return;
            }
            if (now >= m_PvpWatchGunLogAt)
            {
                m_PvpWatchGunLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: de olho em {Name} com {Hand} na mao e nenhum zumbi a 6m; tirando a arma.",
                    target.channel?.owner?.playerID.characterName ?? "?",
                    DescribeEquipped());
            }
            EnsureCombatWeapon(true, now);
        }

        // Watching with a loaded gun: the sights on the spot.
        private bool IsPvpHoldingAngle()
        {
            if (m_PvpTarget == null
                || m_PvpEngaged
                || !(m_PvpLooking || IsPvpHuntWatching())
                || m_TreatmentPhase != 0
                || !(m_Player.equipment.asset is ItemGunAsset gun)
                || !IsPvpGun(gun)
                || !IsEquippedGunLoaded(gun))
            {
                return false;
            }
            var point = m_PvpLooking ? m_PvpLastSeenPoint : m_PvpHuntPoint;
            return (point - m_Player.look.aim.position).magnitude <= PvpHoldAngleMeters;
        }

        private bool ConfigurePvpHoldInput(WalkingPlayerInputPacket packet, float now)
        {
            ReleasePvpTrigger(packet, now);
            var readyGun = m_Player.equipment.useable as UseableGun;
            if (readyGun == null || !m_Player.equipment.IsEquipAnimationFinished || m_Player.equipment.isBusy
                || readyGun.isReloading || readyGun.needsRechamber)
            {
                return true;
            }
            if ((m_PvpAimHeld || m_GunAimHeld) && !readyGun.isAiming && now >= m_PvpAimReadyAt)
            {
                ReleasePvpAim(packet, now, "vigia: o jogo baixou a mira");
                return true;
            }
            if (!m_PvpAimHeld && m_Player.stance.stance != EPlayerStance.SPRINT)
            {
                packet.secondaryAttack = EAttackInputFlags.Start;
                m_PvpAimHeld = true;
                m_GunAimHeld = true;
                m_PvpAimReadyAt = now + Random.Range(0.3f, 0.6f);
            }
            return true;
        }

        // He showed where the sights were held.
        private bool TrySetHeldAngleReaction(Vector3 aimPoint, bool watching, float now)
        {
            if (!watching)
            {
                return false;
            }
            var to = aimPoint - m_Player.look.aim.position;
            if (to.sqrMagnitude < 0.01f
                || Vector3.Angle(AimDirection(m_CurrentYaw, m_CurrentPitch), to) > PvpHoldAngleDegrees)
            {
                return false;
            }
            m_PvpReactAt = now + DrawFromQuantiles(s_PvpHeldQuantiles, s_PvpHeldSeconds);
            return true;
        }

        // The second after he went out of sight on the move: where he was
        // heading, if that is in line.
        private bool IsPvpPrefire(float distance, float now)
        {
            if (!m_PvpEngaged
                || m_PvpVisible
                || now - m_PvpLastEyeAt > PvpPrefireSeconds
                || distance > PvpPrefireMeters
                || m_PvpLeadVelocity.magnitude < 1.5f)
            {
                return false;
            }
            var heading = m_PvpLastSeenPoint + m_PvpLeadVelocity * Mathf.Min(1f, now - m_PvpLastSeenAt);
            if (!HasPvpSight(heading, true))
            {
                return false;
            }
            if (now >= m_PvpPrefireLogAt)
            {
                m_PvpPrefireLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: {Name} sumiu atras de algo a {Distance:0}m; atirando onde ele ia sair.",
                    m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?",
                    distance);
            }
            return true;
        }
    }
}
