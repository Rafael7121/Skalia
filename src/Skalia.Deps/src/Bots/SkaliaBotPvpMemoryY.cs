using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.4: the memory of the y17 plan (item 6) and the proof of its
    // cover (item 2).
    //
    // Footsteps (the tester, 18/11: "normalmente ficamos atento e tentamos
    // localizar o alvo, mas na maioria das vezes ja sabemos a onde ele esta
    // por pura nocao"). The game (PlayerMovement.PlayFootstepAudioClip):
    // a step is heard from a man on the ground, moving and not prone, with
    // a linear fall to nothing at 32m; crouched at half the volume, and the
    // Sneakybeaky skill takes up to 3/4 off. Here the reach is the 32m
    // scaled by that same volume (crouched 16m, full Sneakybeaky 8m). A man
    // it remembers and does not see, stepping within that, gives the side
    // the steps come from with the error of the turn to a shot
    // (SkaliaBotShotSectorY) and keeps him in memory: the hunt hides from
    // and watches that side (SkaliaBotPvpHuntY). The players of 08/11 did
    // not turn to steps above chance, so no turn of the head on its own.
    //
    // More than one enemy: in 27 of the 86 fights of 08/11 (rec/m18b.py)
    // another enemy fought the same player within 60s. ToxicSniper,
    // 11:50:4x of 18/11, hit by AlemaoGamer from 89m, switched to Ghost and
    // never thought of AlemaoGamer again. The one it leaves is remembered
    // where it last knew him, kept out of its loot, and taken up again when
    // the fight on hand ends.
    //
    // Loot in the open: the players of 08/11 (rec/m18b.py), in the 60s after
    // a fight both lived through, picked 25 of 53 items under a roof (47%),
    // against 123 of 404 (30%) out of fights and 29 of 110 (26%) 60-300s
    // after. the tester: "loot so em lugar coberto enquanto lembra de mim". For
    // 60s after it last saw, heard or was hit by him, only loot under a roof
    // (the recorder's test, SkaliaPlayerRecorder.HasRoof); after that the
    // players went back to the open, so does the bot.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        // 0.1.0-y17.37: times serverBot:modifiers:footsteps.
        private static float PvpStepReachMeters => 32f * SkaliaBotModifiers.Footsteps;
        private const float PvpStepCheckSeconds = 1f;
        // 0.1.0-y17.37: times serverBot:modifiers:coveredLoot.
        private static float PvpCoveredLootSeconds => 60f * SkaliaBotModifiers.CoveredLoot;

        private sealed class PvpRemembered
        {
            public Player Who = null!;
            public Vector3 Point;
            public float At;
            public bool Seen;
            public Vector3 StepFrom;
        }

        private readonly List<PvpRemembered> m_PvpOthers = new List<PvpRemembered>();
        private float m_PvpStepCheckAt;
        private Vector3 m_PvpStepFrom;
        private Player? m_PvpStepFromWho;
        private float m_PvpLastHeardAt = float.NegativeInfinity;
        private float m_PvpStepLogAt = float.NegativeInfinity;
        private float m_PvpCoveredLootLogAt = float.NegativeInfinity;

        // How far this man's steps carry now (0 when they make no sound).
        private static float PvpStepReach(Player other, Vector3 movedFrom)
        {
            var movement = other.movement;
            var stance = other.stance?.stance ?? EPlayerStance.STAND;
            if (movement == null
                || !movement.isGrounded
                || movement.getVehicle() != null
                || stance == EPlayerStance.PRONE
                || stance == EPlayerStance.SWIM
                || stance == EPlayerStance.CLIMB
                || stance == EPlayerStance.DRIVING
                || stance == EPlayerStance.SITTING
                || (other.transform.position - movedFrom).GetHorizontalMagnitude() < 0.5f)
            {
                return 0f;
            }
            var volume = 1f - (other.skills != null ? other.skills.mastery(1, 0) : 0f) * 0.75f;
            if (stance == EPlayerStance.CROUCH)
            {
                volume *= 0.5f;
            }
            return PvpStepReachMeters * volume;
        }

        // From TickPvpCore, him not in sight.
        private void TickPvpFootsteps(Player target, float now)
        {
            if (now < m_PvpStepCheckAt)
            {
                return;
            }
            m_PvpStepCheckAt = now + PvpStepCheckSeconds;
            var eye = m_Player.look.aim.position;
            foreach (var other in m_PvpOthers)
            {
                if (other.Who == null || other.Who.life == null || other.Who.life.isDead)
                {
                    continue;
                }
                var reachOther = PvpStepReach(other.Who, other.StepFrom);
                other.StepFrom = other.Who.transform.position;
                if (reachOther > 0f && (other.Who.transform.position - m_Player.transform.position).GetHorizontalMagnitude() <= reachOther)
                {
                    // 0.1.0-y17.5: a man it saw stays where it saw him; the
                    // steps only say he is still around.
                    if (other.Seen)
                    {
                        other.At = now;
                        continue;
                    }
                    // Where the steps are, with the error of a turn to a
                    // sound (as a shot, SkaliaBotShotSectorY).
                    var to = other.Who.transform.position - m_Player.transform.position;
                    var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) / SkaliaBotModifiers.ShotOrigin * (Random.value < 0.5f ? -1f : 1f);
                    other.Point = m_Player.transform.position
                        + Quaternion.Euler(0f, error, 0f) * new Vector3(to.x, 0f, to.z);
                    other.At = now;
                    other.Seen = false;
                }
            }
            if (TryTurnToSecondSteps(target, now))
            {
                return;
            }
            var from = m_PvpStepFromWho == target ? m_PvpStepFrom : target.transform.position;
            m_PvpStepFrom = target.transform.position;
            m_PvpStepFromWho = target;
            if (m_PvpAware || target.life == null || target.life.isDead)
            {
                return;
            }
            var reach = PvpStepReach(target, from);
            var meters = (target.transform.position - m_Player.transform.position).GetHorizontalMagnitude();
            if (reach <= 0f || meters > reach)
            {
                return;
            }
            m_PvpLastHeardAt = now;
            if (TryTurnToSameManSteps(target, from, meters, now))
            {
                return;
            }
            var feet = target.transform.position + Vector3.up * 1.6f;
            // 0.1.0-y17.5 (test of 18/11 13:04): a man it saw is where it
            // saw him, give or take how far he could have run since ("na
            // maioria das vezes ja sabemos a onde ele esta por pura nocao").
            // The steps drew a fresh turn error instead: Ghost saw the tester
            // at 150 degrees, lost him at 13:09:30, heard him running in
            // straight from there (143-145 degrees) and set its watch on
            // "setor 231 graus +-33", 88 degrees off; it lay facing away
            // and took the round from 10m.
            if (now - m_PvpLastSeenAt <= PvpMemorySeconds
                && (m_ShotSectorFromSeen || m_PvpLastSeenAt > m_ShotSectorAt || !IsShotSectorFresh(now)))
            {
                var believed = NoteStepsOfSeenMan(reach, now);
                m_PvpHitFrom = believed;
                m_PvpHitFromAt = now;
                m_PvpHitFromKnown = true;
                if (m_PvpHuntPhase == 1)
                {
                    m_PvpHuntPoint = believed;
                    m_PvpHuntKey = Mathf.Max(m_PvpHuntKey, now);
                }
            }
            else
            {
                // As a round from that side: the sector, the point the watch
                // faces and, out of a hunt, a new one from there.
                NoteShotDuringUnknownFight(target, feet, true, now);
            }
            if (now >= m_PvpStepLogAt)
            {
                m_PvpStepLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: ouvi passos de {Name} (alcance {Reach:0}m); setor {Sector}; lembrando dele.",
                    target.channel?.owner?.playerID.characterName ?? "?",
                    reach,
                    DescribeShotSector(now));
            }
        }

        // 0.1.0-y17.6 (test of 18/11, 17:08:00-04): ToxicSniper, turned to
        // the tester at 22m, never looked round at Vultox sprinting at its back
        // from 40m with a bat and was clubbed down at 1.5m. the tester (18/11):
        // a player turns to the steps of a second man coming at him from
        // behind, "com certeza e nao morreria facilmente na pancada". The
        // steps are the game's (PvpStepReach); "coming at him" is a man out
        // of its 120 degrees whose last second of steps brought him 1.5m or
        // more toward it, and nearer than where it knows the one it
        // fights. That one is remembered; the bot faces the steps (with the
        // turn error of a sound) standing, and the eye has to find him.
        private const float PvpSecondStepsTowardMeters = 1.5f;
        private const float PvpSecondStepsCooldownSeconds = 5f;
        // The turn is about 1s at the fight's turn rate; the one in front,
        // seen again in that time, does not take the head back.
        private const float PvpSecondStepsLookSeconds = 3f;
        private readonly Dictionary<Player, Vector3> m_PvpStepPrev = new Dictionary<Player, Vector3>();
        private readonly List<Player> m_PvpStepPrevGone = new List<Player>();
        private float m_PvpSecondStepsAt = float.NegativeInfinity;

        // The nearest man whose last second of steps (heard, PvpStepReach)
        // brought him 1.5m or more toward the bot, nearer than knownMeters;
        // out of its 120 degrees only, when asked. The positions of a
        // second ago are kept here; after a gap they are only taken anew.
        private float m_PvpStepPrevAt = float.NegativeInfinity;

        private Player? FindStepsComingAtMe(Player? skip, float knownMeters, bool outOfViewOnly, float now, out float bestMeters)
        {
            var position = m_Player.transform.position;
            var fresh = now - m_PvpStepPrevAt <= PvpStepCheckSeconds * 1.5f;
            m_PvpStepPrevAt = now;
            Player? best = null;
            bestMeters = float.MaxValue;
            foreach (var client in Provider.clients)
            {
                var other = client?.player;
                if (other == null
                    || other == m_Player
                    || other == skip
                    || other.life == null
                    || other.life.isDead
                    || IsPvpAlly(other))
                {
                    continue;
                }
                var at = other.transform.position;
                if (fresh && m_PvpStepPrev.TryGetValue(other, out var prev))
                {
                    var meters = (at - position).GetHorizontalMagnitude();
                    var reach = PvpStepReach(other, prev);
                    var toBot = position - at;
                    toBot.y = 0f;
                    var moved = at - prev;
                    moved.y = 0f;
                    var toward = toBot.sqrMagnitude > 0.01f ? Vector3.Dot(moved, toBot.normalized) : 0f;
                    if (reach > 0f
                        && meters <= reach
                        && toward >= PvpSecondStepsTowardMeters
                        && meters < knownMeters
                        && meters < bestMeters
                        && (!outOfViewOnly || !IsInPvpView(at + Vector3.up * 1.3f)))
                    {
                        best = other;
                        bestMeters = meters;
                    }
                }
                m_PvpStepPrev[other] = at;
            }
            m_PvpStepPrevGone.Clear();
            foreach (var key in m_PvpStepPrev.Keys)
            {
                if (key == null || key.life == null || key.life.isDead || key.channel?.owner == null || !Provider.clients.Contains(key.channel.owner))
                {
                    m_PvpStepPrevGone.Add(key!);
                }
            }
            foreach (var key in m_PvpStepPrevGone)
            {
                m_PvpStepPrev.Remove(key);
            }
            return best;
        }

        // 0.1.0-y17.8 (test of 18/11, 18:20:23-30): blazex, on the first
        // floor of an office with no one in mind, healed lying down while
        // the tester sprinted in from 33m to 9m, heard all the way; it stood up
        // facing a wall and saw him round it at 6.5m, two rounds to the
        // head 0.5s later. the tester: "fiz muito barulho, era pra ele ja ficar
        // ali me esperando e me matar pq fui reto". With no fight on, the
        // steps of a man coming at it (1.5m or more toward it in the last
        // second, within the game's reach of his steps) start one: it
        // faces where they come from (the turn error of a sound) standing,
        // the gun up on that angle, and waits for him there (the hunt's
        // watch, its sights held, SkaliaBotPvpCoverY).
        private float m_PvpUnknownStepsCheckAt;

        private void TryHearUnknownSteps(float now)
        {
            if (m_PvpTarget != null || now < m_PvpUnknownStepsCheckAt)
            {
                return;
            }
            m_PvpUnknownStepsCheckAt = now + PvpStepCheckSeconds;
            var best = FindStepsComingAtMe(null, float.PositiveInfinity, false, now, out var meters);
            if (best == null
                || now - m_PvpSecondStepsAt < PvpSecondStepsCooldownSeconds
                || !Provider.isPvP
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_CachedNearestZombieDistance <= PvpZombieFirstMeters
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.movement.getVehicle() != null)
            {
                return;
            }
            m_PvpSecondStepsAt = now;
            var position = m_Player.transform.position;
            ResetPvpHunt();
            m_PvpEscapeRequested = false;
            m_PvpTarget = best;
            m_PvpAware = false;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpShots = 0;
            m_PvpHits = 0;
            m_PvpTaken = 0;
            m_PvpEnemyArmed = true; // not seen: armed until shown otherwise
            m_PvpSpotted = null;
            m_PvpStepFromWho = null;
            m_PvpLastSeenAt = float.NegativeInfinity;
            m_PvpLastHeardAt = now;
            m_PvpLastHitAt = now;
            var to = best.transform.position - position;
            var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) / SkaliaBotModifiers.ShotOrigin * (Random.value < 0.5f ? -1f : 1f);
            var believed = position + Quaternion.Euler(0f, error, 0f) * new Vector3(to.x, 0f, to.z);
            m_PvpHitFrom = believed;
            m_PvpHitFromAt = now;
            m_PvpHitFromKnown = false;
            m_PvpHuntPoint = believed;
            StartPvpHuntWatch(now, null);
            m_PvpHuntPosture = 0;
            m_PvpHuntKey = now;
            BotLog(LogLevel.Information,
                "PvP: passos de alguem que nao vejo vindo para cima de mim a {Meters:0}m; parado de pe de arma levantada para la, esperando ele aparecer.",
                meters);
        }

        private bool TryTurnToSecondSteps(Player target, float now)
        {
            var position = m_Player.transform.position;
            var lastKnown = Mathf.Max(m_PvpHitFromAt, m_PvpLastSeenAt);
            var knownMeters = float.IsNegativeInfinity(lastKnown)
                ? float.PositiveInfinity
                : ((m_PvpHitFromAt > m_PvpLastSeenAt ? m_PvpHitFrom : m_PvpLastSeenPoint) - position).GetHorizontalMagnitude();
            var best = FindStepsComingAtMe(target, knownMeters, true, now, out var bestMeters);
            if (best == null || now - m_PvpSecondStepsAt < PvpSecondStepsCooldownSeconds)
            {
                return false;
            }
            m_PvpSecondStepsAt = now;
            var oldName = target.channel?.owner?.playerID.characterName ?? "?";
            // the tester (18/11): turned to the one at its back, it also gets out
            // of the sight of the one in front. A spot within 6m, reached
            // in a straight line, that hides its chest from where it knows
            // him, walked to with the face on the steps.
            var oldEye = m_PvpHitFromAt > m_PvpLastSeenAt ? m_PvpHitFrom : m_PvpLastSeenPoint + Vector3.up * 0.3f;
            var hasSide = !float.IsNegativeInfinity(lastKnown)
                && IsChestInHisLine(oldEye, position)
                && TryFindPvpWatchCover(position, oldEye, out m_PvpSecondSideSpot, out _, PvpFightCoverMeters, true);
            RememberPvpOther(target, now);
            ResetPvpHunt();
            m_PvpEscapeRequested = false;
            m_PvpTarget = best;
            m_PvpAware = false;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpShots = 0;
            m_PvpHits = 0;
            m_PvpTaken = 0;
            m_PvpEnemyArmed = true; // not seen: armed until shown otherwise
            m_PvpSpotted = null;
            m_PvpStepFromWho = null;
            m_PvpLastSeenAt = float.NegativeInfinity;
            m_PvpLastHeardAt = now;
            m_PvpLastHitAt = now;
            var to = best.transform.position - position;
            var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) / SkaliaBotModifiers.ShotOrigin * (Random.value < 0.5f ? -1f : 1f);
            var believed = position + Quaternion.Euler(0f, error, 0f) * new Vector3(to.x, 0f, to.z);
            m_PvpHitFrom = believed;
            m_PvpHitFromAt = now;
            m_PvpHitFromKnown = false;
            m_PvpHuntPoint = believed;
            StartPvpHuntWatch(now, null);
            m_PvpHuntPosture = 0;
            m_PvpHuntKey = now;
            var sideWay = (m_PvpSecondSideSpot - position).GetHorizontalMagnitude();
            m_PvpSecondSideUntil = hasSide ? now + Mathf.Clamp(sideWay / 2f + 1f, 1.5f, 4f) : 0f;
            BotLog(LogLevel.Information,
                "PvP: passos de alguem vindo para cima de mim por tras a {Meters:0}m (mais perto que {Old}); virando para la de pe{Side}.",
                bestMeters,
                oldName,
                hasSide
                    ? $" e saindo {sideWay:0.0}m de lado da linha de {oldName}"
                    : ", sem cobertura de " + oldName + " a 6m em linha reta");
            return true;
        }

        private Vector3 m_PvpSecondSideSpot;
        private float m_PvpSecondSideUntil;

        // 0.1.0-y17.8 (test of 18/11, 18:02:01-15): TheGhostSniper saw
        // the tester at 75m, lost him at 72m and lay watching that spot by a
        // jeep. the tester ran in from there round its left side: heard at
        // 30m ("ouvi passos ... lembrando dele"), 53 to 88 degrees off its
        // face, he reached 3m and shot it in the head with the bot still
        // facing the old spot (0 rounds fired). The steps of the man it
        // saw kept the sector of where it saw him (y17.5), right when he
        // comes in straight from there, wrong when he comes round. Out of
        // its 120 degrees and coming at it (1.5m or more in the last
        // second, nearer than where it knew him), his steps are the turn
        // of the second man's steps above (the tester: "com certeza"): it
        // faces them standing, with the turn error of a sound.
        // 0.1.0-y17.10 (test of 18/11, 18:37:44-48): AlemaoGamer had turned
        // to his steps at 12m; the tester then sprinted along the low wall out
        // of its 120 degrees, out to 22m and back to 14m, and it never
        // looked (the steps were farther than the 12m it believed him at,
        // and going round it, not at it). the tester (18/11): "passei do lado
        // dele atraves da mureta correndo e ele ignorou". Steps of the man
        // it fights out of its 120 degrees are a turn, coming at it or
        // going past. Not on a run (to cover, away without rounds, from the
        // Mega or a swarm): the watch would plant it there.
        private bool TryTurnToSameManSteps(Player target, Vector3 from, float meters, float now)
        {
            if (now - m_PvpSecondStepsAt < PvpSecondStepsCooldownSeconds
                || m_ObjectiveType == ObjectiveType.Flee
                || m_ObjectiveType == ObjectiveType.EscapeSwarm)
            {
                return false;
            }
            var position = m_Player.transform.position;
            var at = target.transform.position;
            var toBot = position - at;
            toBot.y = 0f;
            var moved = at - from;
            moved.y = 0f;
            var toward = toBot.sqrMagnitude > 0.01f ? Vector3.Dot(moved, toBot.normalized) : 0f;
            var lastKnown = Mathf.Max(m_PvpHitFromAt, m_PvpLastSeenAt);
            var knownMeters = float.IsNegativeInfinity(lastKnown)
                ? float.PositiveInfinity
                : ((m_PvpHitFromAt > m_PvpLastSeenAt ? m_PvpHitFrom : m_PvpLastSeenPoint) - position).GetHorizontalMagnitude();
            if (IsInPvpView(at + Vector3.up * 1.3f))
            {
                return false;
            }
            var coming = toward >= PvpSecondStepsTowardMeters && meters < knownMeters;
            m_PvpSecondStepsAt = now;
            var to = at - position;
            var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) / SkaliaBotModifiers.ShotOrigin * (Random.value < 0.5f ? -1f : 1f);
            var believed = position + Quaternion.Euler(0f, error, 0f) * new Vector3(to.x, 0f, to.z);
            m_PvpHitFrom = believed;
            m_PvpHitFromAt = now;
            m_PvpHitFromKnown = false;
            m_PvpHuntPoint = believed;
            // A hide or a watch under way keeps its time.
            var keepUntil = m_PvpHuntPhase == 1 ? m_PvpHuntUntil : 0f;
            StartPvpHuntWatch(now, null);
            m_PvpHuntUntil = Mathf.Max(m_PvpHuntUntil, keepUntil);
            m_PvpHuntPosture = 0;
            m_PvpHuntKey = now;
            BotLog(LogLevel.Information,
                "PvP: passos de {Name} {How} por fora da minha visao a {Meters:0}m (vi ele a {Known:0}m); virando para la de pe.",
                target.channel?.owner?.playerID.characterName ?? "?",
                coming ? "vindo para cima de mim" : "passando",
                meters,
                knownMeters);
            return true;
        }

        // From TickPvpCore: the side step out of the first one's sight,
        // the face on the steps (the hunt watch holds the head).
        private void TickPvpSecondStepsSide(float now)
        {
            if (now >= m_PvpSecondSideUntil)
            {
                return;
            }
            var to = m_PvpSecondSideSpot - m_Player.transform.position;
            if (to.GetHorizontalMagnitude() < 0.6f)
            {
                m_PvpSecondSideUntil = 0f;
                m_PvpPeekAnalog = 0x11;
                m_PvpCoverReachedAt = now;
                return;
            }
            var analog = PvpAnalogToward(to);
            if (analog == 0x11 || !IsPvpStepClear(analog))
            {
                m_PvpSecondSideUntil = 0f;
                m_PvpPeekAnalog = 0x11;
                return;
            }
            m_PvpPeekAnalog = analog;
        }

        // 0.1.0-y17.19 (the tester, 19/11: "ele ficou olhando pra direcao do
        // ghost depois de ter matado ele e nao voltou a atencao para o
        // the other player"). Test of 19/11, Vultox: hunting the other player by sound (sector 317,
        // 00:54:50), it heard TheGhostSniper's round at it at 00:55:44 and
        // took it as the other player's (any round in a fight with a man not yet seen
        // was his): the sector went to 173 +-76, Ghost's side. Ghost's
        // round hit it the same second, it turned on Ghost and remembered
        // the other player "de onde veio o tiro" there; Ghost dead, it went back to
        // the other player on that point and lay 40s (00:55:50-00:56:30) facing 161
        // degrees, Ghost's body at 111, while the other player crawled up from 333-37
        // degrees and shot it from 61m (81 to 19). Now the round heard is
        // kept as whose it was, with the clue on the old man before it; on
        // turning to the new man for a round of his that hit it, or for
        // seeing him on that side, the old man keeps his own clue.
        private Player? m_PvpClueShooter;
        private float m_PvpClueShooterAt = float.NegativeInfinity;
        private bool m_PvpOwnClueValid;
        private Vector3 m_PvpOwnCluePoint;
        private float m_PvpOwnClueAt = float.NegativeInfinity;
        private bool m_PvpOwnClueKnown;

        private bool IsPvpClueOfOther()
        {
            return m_PvpClueShooter != null
                && m_PvpClueShooter != m_PvpTarget
                && m_PvpHitFromAt == m_PvpClueShooterAt;
        }

        // Called before a round sets the clue (NoteShotDuringUnknownFight).
        private void NotePvpClueShooter(Player shooter, float now)
        {
            if (shooter == m_PvpTarget)
            {
                m_PvpClueShooter = null;
                m_PvpClueShooterAt = float.NegativeInfinity;
                return;
            }
            if (!IsPvpClueOfOther())
            {
                m_PvpOwnClueValid = !float.IsNegativeInfinity(m_PvpHitFromAt);
                m_PvpOwnCluePoint = m_PvpHitFrom;
                m_PvpOwnClueAt = m_PvpHitFromAt;
                m_PvpOwnClueKnown = m_PvpHitFromKnown;
            }
            m_PvpClueShooter = shooter;
            m_PvpClueShooterAt = now;
        }

        // Turning to next: hit by him (seenAt null) or seen at seenAt.
        private void GivePvpClueBack(Player next, Vector3? seenAt, float now)
        {
            if (!IsPvpClueOfOther() || m_PvpClueShooter != next)
            {
                return;
            }
            if (seenAt.HasValue && !IsOnShotSector(seenAt.Value, now))
            {
                return;
            }
            var old = m_PvpTarget?.channel?.owner?.playerID.characterName ?? "?";
            if (m_PvpOwnClueValid)
            {
                m_PvpHitFrom = m_PvpOwnCluePoint;
                m_PvpHitFromAt = m_PvpOwnClueAt;
                m_PvpHitFromKnown = m_PvpOwnClueKnown;
            }
            else
            {
                m_PvpHitFromAt = float.NegativeInfinity;
            }
            m_PvpClueShooter = null;
            m_PvpClueShooterAt = float.NegativeInfinity;
            BotLog(LogLevel.Information,
                "PvP: o tiro que eu achava ser de {Old} era de {New}; {Back}.",
                old,
                next.channel?.owner?.playerID.characterName ?? "?",
                m_PvpOwnClueValid ? "lembro de " + old + " pela pista de antes" : "de " + old + " fica so onde eu vi");
        }

        // The man it leaves for another: where it last knew him.
        private void RememberPvpOther(Player? old, float now)
        {
            var heardAt = m_PvpLastHeardAt;
            // The steps heard were his, not the next one's.
            m_PvpLastHeardAt = float.NegativeInfinity;
            if (old == null || old.life == null || old.life.isDead)
            {
                return;
            }
            var seen = m_PvpLastSeenAt >= m_PvpHitFromAt;
            var at = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt, heardAt);
            if (now - at > PvpMemorySeconds)
            {
                return;
            }
            RememberPvpOtherAt(old, seen ? m_PvpLastSeenPoint : m_PvpHitFrom, at, seen);
        }

        private void RememberPvpOtherAt(Player who, Vector3 point, float at, bool seen)
        {
            m_PvpOthers.RemoveAll(o => o.Who == who);
            m_PvpOthers.Add(new PvpRemembered { Who = who, Point = point, At = at, Seen = seen, StepFrom = who.transform.position });
            BotLog(LogLevel.Information,
                "PvP: lembrando de {Name} a {Meters:0}m ({How}) enquanto cuido de outro; {Count} na memoria.",
                who.channel?.owner?.playerID.characterName ?? "?",
                (point - m_Player.transform.position).GetHorizontalMagnitude(),
                seen ? "onde vi" : "de onde veio o tiro",
                m_PvpOthers.Count);
        }

        private void PrunePvpOthers(float now)
        {
            m_PvpOthers.RemoveAll(o => o.Who == null
                || o.Who.life == null
                || o.Who.life.isDead
                || o.Who.channel?.owner == null
                || !Provider.clients.Contains(o.Who.channel.owner)
                || now - o.At > PvpMemorySeconds
                || o.Who == m_PvpTarget);
        }

        // The fight on hand over: the next one it remembers, the most
        // recent, back as the man it looks for.
        private void ResumePvpOther(float now)
        {
            PrunePvpOthers(now);
            if (m_PvpTarget != null || m_PvpOthers.Count == 0 || m_Player.life.isDead)
            {
                return;
            }
            var next = m_PvpOthers[0];
            foreach (var other in m_PvpOthers)
            {
                if (other.At > next.At)
                {
                    next = other;
                }
            }
            m_PvpOthers.Remove(next);
            m_PvpTarget = next.Who;
            m_PvpAware = false;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpShots = 0;
            m_PvpHits = 0;
            m_PvpTaken = 0;
            m_PvpEnemyArmed = true; // not seen now: armed until shown otherwise
            m_PvpHuntKey = float.NegativeInfinity;
            m_PvpLastHeardAt = float.NegativeInfinity;
            if (next.Seen)
            {
                m_PvpLastSeenAt = next.At;
                m_PvpLastSeenPoint = next.Point;
                m_PvpHitFromAt = float.NegativeInfinity;
                m_PvpLastHitAt = float.NegativeInfinity;
            }
            else
            {
                m_PvpLastSeenAt = float.NegativeInfinity;
                m_PvpHitFrom = next.Point;
                m_PvpHitFromAt = next.At;
                m_PvpHitFromKnown = false;
                m_PvpLastHitAt = next.At;
            }
            // 0.1.0-y17.19: the watch and the cover on where it knew him,
            // not on the side of the one just fought (SkaliaBotShotSectorY).
            AimShotSectorAt(next.Point, next.At, next.Seen);
            m_PvpClueShooter = null;
            m_PvpClueShooterAt = float.NegativeInfinity;
            BotLog(LogLevel.Information,
                "PvP: voltando a {Name}, que eu lembrava {How} a {Meters:0}m ha {Seconds:0}s.",
                next.Who.channel?.owner?.playerID.characterName ?? "?",
                next.Seen ? "onde vi" : "de onde veio o tiro",
                (next.Point - m_Player.transform.position).GetHorizontalMagnitude(),
                now - next.At);
        }

        private bool IsLootTowardRememberedOther(ItemData item, float now)
        {
            PrunePvpOthers(now);
            foreach (var other in m_PvpOthers)
            {
                var own = (m_Player.transform.position - other.Point).GetHorizontalMagnitude();
                var toItem = (item.point - other.Point).GetHorizontalMagnitude();
                if (toItem < own && toItem < Mathf.Max(own * 0.5f, 20f))
                {
                    IgnoreLoot(item.instanceID, Mathf.Max(now + 5f, other.At + PvpMemorySeconds));
                    return true;
                }
            }
            return false;
        }

        // Loot in the open within 60s of the last word of a man alive.
        // 0.1.0-y17.5 (test of 18/11 13:04, Washington): the fight's runs
        // go to a spot picked for what it hides, and the forced move let go
        // of them 4m short. Vultox, 13:07:56: "correndo 3m para cobertura
        // do setor ... (escondido de 4 de 5 pontos)" stopped where it
        // stood, "peito escondido de 2 de 5". They end on the spot, as the
        // cover, chase and no-round runs already did.
        private static bool IsPvpSpotRunName(string? name)
        {
            return name == PvpChaseName
                || name == PvpNoAmmoName
                || name == PvpEscapeName
                || name == PvpHuntName
                || name == PvpRepositionName
                || name == PvpHealRunName;
        }

        // The last news of a living enemy: seen, heard, hit from, or one
        // remembered.
        private float LastPvpNewsAt()
        {
            var last = float.NegativeInfinity;
            if (m_PvpTarget != null && m_PvpTarget.life != null && !m_PvpTarget.life.isDead)
            {
                last = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt, m_PvpLastHeardAt);
            }
            foreach (var other in m_PvpOthers)
            {
                last = Mathf.Max(last, other.At);
            }
            return last;
        }

        private static bool IsRoofedPoint(Vector3 point)
        {
            return Physics.Raycast(point + Vector3.up * 1.9f, Vector3.up, 20f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
        }

        // 0.1.0-y17.5 (the tester, 18/11: "no PVP a gente mata sim zumbi, mas
        // depende da situacao. pq temos nocao que o zumbi spota a gente se
        // ele ficar seguindo nos entao no PVP mesmo evitamos zumbi"). The
        // players of 08/11 (rec/m20a.py) in the minute after a fight with
        // the other alive hit common zombies 1.0 times a minute (52 hits in
        // 51 minutes), against 2.4 a minute away from PvP (905 in 374).
        // So for that minute a zombie that is not after the bot is left
        // alone; one after it is fought or outrun as before.
        private float m_PvpZombieAvoidLogAt = float.NegativeInfinity;

        private bool IsAvoidingZombiesForPvp(Zombie zombie, float horizontal)
        {
            if (!Tryhard)
            {
                return false;
            }
            var now = Time.realtimeSinceStartup;
            var last = LastPvpNewsAt();
            if (now - last > PvpCoveredLootSeconds)
            {
                return false;
            }
            if (now >= m_PvpZombieAvoidLogAt)
            {
                m_PvpZombieAvoidLogAt = now + 20f;
                BotLog(LogLevel.Information,
                    "PvP: deixando em paz zumbi a {Distance:0.#}m que nao esta atras de mim; faz {Seconds:0}s que tive noticia de um inimigo (evito zumbi por {Window:0}s).",
                    horizontal,
                    now - last,
                    PvpCoveredLootSeconds);
            }
            return true;
        }

        // The pile of a man it killed (or the Mega's) in the open, with
        // another enemy heard of within the minute: later, as any loot.
        // Ghost, 13:09:20-30 of 18/11, set off for AlemaoGamer's pile 45m
        // off with the tester alive and 57m away.
        private float m_PvpPileLogAt = float.NegativeInfinity;

        private bool IsPileInOpenAfterPvp(Vector3 center, float now)
        {
            var last = LastPvpNewsAt();
            if (now - last > PvpCoveredLootSeconds || IsRoofedPoint(center))
            {
                return false;
            }
            if (now >= m_PvpPileLogAt)
            {
                m_PvpPileLogAt = now + 15f;
                BotLog(LogLevel.Information,
                    "Monte {Pile} fica para depois: sem teto, e faz {Seconds:0}s que tive noticia de um inimigo (so loot coberto por {Window:0}s).",
                    m_DropPileName,
                    now - last,
                    PvpCoveredLootSeconds);
            }
            return true;
        }

        // 0.1.0-y17.6 (test of 18/11, 17:08:59-17:09:03): ToxicSniper, 40
        // health after a round in the head from the tester at 52m, stood in his
        // line inside the gun shop picking up a Sportshot and five
        // magazines one by one (a roof over it, so the minute's rule let
        // it), and took the next round. the tester (18/11): in the middle of a
        // fight a player picks up only "na negativa do inimigo", out of his
        // line, and then even what he needs; health matters little ("se
        // vacilarmos podemos ir de 100 a 0 em questao de segundos"). For
        // 10s after the last word of a man alive (seen, heard, hit from),
        // an item or a pile his eye reaches from where it knows him waits.
        private float m_PvpLineLootLogAt = float.NegativeInfinity;

        private bool IsPointInPvpEnemyLine(Vector3 ground, float now, out string who)
        {
            who = string.Empty;
            var target = m_PvpTarget;
            if (target != null && target.life != null && !target.life.isDead)
            {
                var last = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt, m_PvpLastHeardAt);
                if (now - last <= PvpActiveSeconds)
                {
                    var eye = m_PvpHitFromAt > m_PvpLastSeenAt ? m_PvpHitFrom : m_PvpLastSeenPoint + Vector3.up * 0.3f;
                    // 0.1.0-y17.21: his line as the watch's cover takes it
                    // (SkaliaBotPvpCoverY), not one ray.
                    if (IsChestInHisLine(eye, ground))
                    {
                        who = target.channel?.owner?.playerID.characterName ?? "?";
                        return true;
                    }
                }
            }
            // 0.1.0-y17.7 (test of 18/11, 17:34:31-40): AlemaoGamer, a baton
            // only, left ToxicSniper "sem bala" and at once went for an Ace
            // and then a Cobra past him, was shot each time and died with
            // 14 health on the way to the Cobra. The one it just left is
            // still the fight.
            if (IsPvpLeftRecently(now) && now - m_PvpLastSeenAt <= PvpActiveSeconds
                && IsChestInHisLine(m_PvpLastSeenPoint, ground))
            {
                who = "quem deixei para tras";
                return true;
            }
            foreach (var other in m_PvpOthers)
            {
                if (now - other.At > PvpActiveSeconds || other.Who == null || other.Who.life == null || other.Who.life.isDead)
                {
                    continue;
                }
                var eye = other.Seen ? other.Point + Vector3.up * 0.3f : other.Point;
                if (IsChestInHisLine(eye, ground))
                {
                    who = other.Who.channel?.owner?.playerID.characterName ?? "?";
                    return true;
                }
            }
            return false;
        }

        private bool IsLootInPvpEnemyLine(Vector3 ground, string what, float now)
        {
            if (!IsPointInPvpEnemyLine(ground, now, out var who))
            {
                return false;
            }
            if (now >= m_PvpLineLootLogAt)
            {
                m_PvpLineLootLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "{What} fica para depois: na linha de {Name}, no meio da luta (so pego fora da linha dele).",
                    what,
                    who);
            }
            return true;
        }

        private bool IsLootInOpenAfterPvp(ItemData item, float now)
        {
            var last = LastPvpNewsAt();
            if (now - last > PvpCoveredLootSeconds)
            {
                return false;
            }
            if (IsRoofedPoint(item.point))
            {
                return false;
            }
            IgnoreLoot(item.instanceID, last + PvpCoveredLootSeconds + 1f);
            if (now >= m_PvpCoveredLootLogAt)
            {
                m_PvpCoveredLootLogAt = now + 15f;
                BotLog(LogLevel.Information,
                    "Loot {LootInstanceId} fica para depois: sem teto, e faz {Seconds:0}s que tive noticia de um inimigo (so loot coberto por {Window:0}s).",
                    item.instanceID,
                    now - last,
                    PvpCoveredLootSeconds);
            }
            return true;
        }

        // Item 2's proof: of the 5 points across the sector, how many do not
        // see its chest where it stands, and a roof.
        private void LogPvpSectorCoverReached(float now)
        {
            if (!IsShotSectorFresh(now))
            {
                return;
            }
            var position = m_Player.transform.position;
            var half = ShotSectorHalfNow(now);
            var hidden = 0;
            for (var i = 0; i < 5; i++)
            {
                if (!IsChestSeenFrom(ShotSectorPoint(Mathf.Lerp(-half, half, i / 4f)), position))
                {
                    hidden++;
                }
            }
            var roof = Physics.Raycast(position + Vector3.up * 1.9f, Vector3.up, 20f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
            BotLog(LogLevel.Information,
                "PvP: fim da corrida do tiro sem origem; peito escondido de {Hidden} de 5 pontos do setor {Sector}{Roof}.",
                hidden,
                DescribeShotSector(now),
                roof ? ", com teto" : ", sem teto");
        }
    }
}
