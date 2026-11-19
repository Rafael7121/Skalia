using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y4 (the tester, 17/11: "quando eu atiro ele esquece depois de um
    // tempo e segue ... ele nao tenta localizar a onde foi, e se for perto
    // tentar flankear para achar"). In the test of 17/11 (02:50) the tester
    // punched Ghost from 3m behind: "nao sei de onde", a 10m run and back
    // to the patrol 1s later without a look back. After the exchange at 21m
    // Ghost looked 6s where the tester was, went back to the patrol, drank and
    // ate, and found him again at 1m.
    //
    // What the players of 08/11 did once the other one was out of sight,
    // both alive (rec/pvp17.py, pvp18.py; 61 cases):
    // - they faced where he had been 68% of the first 10s, 62-67% up to
    //   40s and 54-57% up to 60s (25% by chance);
    // - food in hand only 1 time in 61 within 50s (8s, at 137m); the others
    //   55, 59, 94, 94, 107 and 110s after;
    // - within 60m (12 cases): 5 stayed (30 to 60s, standing, crouched or
    //   prone, facing that way), 2 went straight to where he was (54m in
    //   20s, 56m in 10s), 5 went around: across and forward by about 0.5 to
    //   0.8 of the distance (+24/+39m at 52m, +28/+24m at 40m, +43/+71m at
    //   16m), sprinting, in 10 to 20s, then stopped and watched;
    // - past 60m almost all stayed (2 of 27 went closer at 100-300m).
    // Hit from off the screen (rec/pvp16.py): 6 of 8 had turned to the
    // shooter after 0.2, 1.0, 1.2, 1.4, 2.0 and 7.1s, most of them running.
    //
    // The bot: lost him (after the 6s look) or hit and not found (after
    // the run), it stays and watches, goes to where he was, or goes around,
    // in the players' shares; facing the last point he was seen, or where
    // the round came from, and nothing is known of him past that: the
    // vision (120 degrees, line of sight) has to find him.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float PvpHuntMaxMeters = 60f;
        private const float PvpHuntHoldShare = 5f / 12f;
        private const float PvpHuntPushShare = 2f / 12f;
        // 0.1.0-y17.6 (test of 18/11, 17:08:20): Vultox lost the tester at 22m
        // behind the corner of a house, went "direto ate la" at a sprint
        // and ran into him waiting at the corner: two rounds in the head
        // at 4.8m, 0 fired. The players went straight to the point only
        // from 42 to 56m (rec/pvp18.py: 54m in 20s, 56m in 10s, 42m in
        // 15s); within 30m (4 cases) 2 stayed and 2 went round (16m), none
        // straight in. Under 40m the straight share is a stay.
        private const float PvpHuntPushMinMeters = 40f;
        private static readonly float[] s_PvpHuntHoldSeconds = { 30f, 45f, 60f };
        private const string PvpHuntName = "procura do atirador";

        // 0 none, 1 watching, 2 on the way.
        private int m_PvpHuntPhase;
        private float m_PvpHuntUntil;
        private Vector3 m_PvpHuntPoint;
        private float m_PvpHuntKey = float.NegativeInfinity;
        private float m_PvpHuntYaw;
        private float m_PvpHuntPitch;
        private int m_PvpHuntPosture;
        private bool m_PvpHuntHealWatch;
        private Vector3 m_PvpHitFrom;
        private float m_PvpHitFromAt = float.NegativeInfinity;
        // 0.1.0-y17.1: the last hit came from a man it had found.
        private bool m_PvpHitFromKnown;

        private bool IsPvpHunting()
        {
            return m_PvpHuntPhase == 2
                && m_ObjectiveType == ObjectiveType.Flee
                && m_FleeingFromPlayerName == PvpHuntName;
        }

        private bool IsPvpHuntWatching()
        {
            return m_PvpHuntPhase == 1 && m_PvpTarget != null;
        }

        private void ResetPvpHunt()
        {
            if (IsPvpHunting())
            {
                ClearObjective();
            }
            m_PvpHuntPhase = 0;
            m_PvpHuntHealWatch = false;
            m_PvpHuntKey = float.NegativeInfinity;
            m_PvpHitFromAt = float.NegativeInfinity;
            m_PvpHiding = false;
        }

        // A hit: where the round came from, as the shot is heard.
        // 0.1.0-y17: the exact point only when it found him; otherwise the
        // point it believes, with the players' error (SkaliaBotShotSectorY).
        private void NotePvpHitFrom(Player attacker, float now)
        {
            m_PvpHitFromKnown = m_PvpAware;
            if (m_PvpAware)
            {
                m_PvpHitFrom = attacker.transform.position + Vector3.up * 1.3f;
            }
            else
            {
                NoteShotSector(attacker, attacker.look?.aim != null ? attacker.look.aim.position : attacker.transform.position + Vector3.up * 1.6f, now, out var believed);
                m_PvpHitFrom = believed;
            }
            m_PvpHitFromAt = now;
        }

        // From TickPvpCore with the fight state set. True when the hunt
        // holds the head this tick.
        private bool TickPvpHunt(float now)
        {
            var target = m_PvpTarget;
            if (target == null)
            {
                return false;
            }
            // 0.1.0-y17.17 (all the tests of 18/11): 19 of the 70 hides with
            // no round ("sem bala, fora da vista ... fico escondido aqui por
            // ate 95s") ended in the same second as "deixei ele para tras":
            // he had been seen a second before, so he was still "aware" and
            // the line below took the watch off at once; the no-round code
            // read that as the hide being over, ended the fight and the
            // routine walked the bot back out (LilJuninho13 22:27:30 and
            // 22:42:20, TheGhostSniper 22:48:10, all shot 10-20s later by the
            // man they had hidden from). Out of his sight the hide stays;
            // him on the screen again ends it as before.
            if (m_PvpNoAmmoHiding && m_PvpHuntPhase == 1
                && !m_PvpEngaged && !m_PvpVisible && !m_PvpCameraOnly)
            {
                FacePvpHuntPoint(now);
                return true;
            }
            if (m_PvpEngaged || m_PvpLooking || m_PvpAware)
            {
                // He is in sight (or was a moment ago): the fight has him.
                if (IsPvpHunting())
                {
                    ClearObjective();
                }
                m_PvpHuntPhase = 0;
                m_PvpHuntHealWatch = false;
                m_PvpHiding = false;
                // 0.1.0-y17.5: out of sight a moment ago with a bandage in
                // the hands, the fight's look is off ("looking" plants and
                // steps, not for a bandage): the eyes stay on where he was.
                if (m_PvpAware && !m_PvpEngaged && !m_PvpLooking
                    && m_TreatmentPhase != 0 && !m_TreatmentIsMeal
                    && m_ObjectiveType != ObjectiveType.Flee
                    && m_CachedNearestZombieDistance > PvpZombieFirstMeters)
                {
                    m_PvpHuntPoint = m_PvpLastSeenPoint;
                    m_PvpHuntYaw = m_CurrentYaw;
                    m_PvpHuntPitch = m_CurrentPitch;
                    FacePvpHuntPoint(now);
                    return true;
                }
                return false;
            }
            // 0.1.0-y17.5 (test of 18/11 13:04; the tester: "ele deu as costas
            // para entrada e ficou deitado no canto"). A bandage ended the
            // watch: Ghost, inside the Mechanic, took a second bandage at
            // 13:09:32 with the tester's steps coming, the watch stopped, nothing
            // held the head and it lay facing 299 degrees with the tester coming
            // in from 105-145. The bandage keeps the bot where it is, not
            // its eyes: the watch goes on (the players of 08/11 faced the
            // other's last place 49% of the time with a medical item in
            // hand, rec/m19a.py); only the moves wait for the end of it.
            var treating = m_TreatmentPhase != 0 && !m_TreatmentIsMeal;
            var blocked = m_CachedNearestZombieDistance <= PvpZombieFirstMeters
                || (m_TreatmentPhase != 0 && m_TreatmentIsMeal)
                || m_ObjectiveType == ObjectiveType.Combat
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || (m_ObjectiveType == ObjectiveType.Flee && !IsPvpHunting())
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.movement.getVehicle() != null
                || IsLadderInProgress();
            if (m_PvpHuntPhase == 2)
            {
                if (!IsPvpHunting() || now >= m_PvpHuntUntil)
                {
                    // There, or the way took too long: stops and watches.
                    if (IsPvpHunting())
                    {
                        ClearObjective();
                    }
                    if (TryStartPvpCoverMove(now))
                    {
                        return false;
                    }
                    if (m_PvpHiding)
                    {
                        StartPvpHide(now, (m_PvpHuntPoint - m_Player.transform.position).GetHorizontalMagnitude(),
                            target.channel?.owner?.playerID.characterName ?? "?");
                        return false;
                    }
                    StartPvpHuntWatch(now, "chegou");
                    // 0.1.0-y17.14: a man known only by his shots: the way
                    // he would come (SkaliaBotPvpWayInY).
                    if (m_PvpHitFromAt > m_PvpLastSeenAt)
                    {
                        ApplyPvpWayIn(m_PvpHuntPoint, target.channel?.owner?.playerID.characterName ?? "?", now);
                    }
                }
                return false;
            }
            if (m_PvpHuntPhase == 1)
            {
                if (m_PvpHuntHealWatch && !treating)
                {
                    // Watched through the bandage: now the hunt decides.
                    m_PvpHuntHealWatch = false;
                    m_PvpHuntPhase = 0;
                    m_PvpHuntHealWatch = false;
                    return false;
                }
                if (blocked || (now >= m_PvpHuntUntil && !treating))
                {
                    // 0.1.0-y17.14: the hunt goes to where he was believed,
                    // not to the door it watched (SkaliaBotPvpWayInY).
                    if (m_PvpWayInActive)
                    {
                        m_PvpHuntPoint = m_PvpWatchFrom;
                        m_PvpWayInActive = false;
                    }
                    // 0.1.0-y17: hidden long enough with nothing new: the
                    // hunt goes on as after losing a man seen, around or
                    // straight to the sector (SkaliaBotShotSectorY).
                    if (m_PvpHiding && !blocked)
                    {
                        m_PvpHiding = false;
                        var from = m_Player.transform.position;
                        var away = (m_PvpHuntPoint - from).GetHorizontalMagnitude();
                        var hunter = target.channel?.owner?.playerID.characterName ?? "?";
                        var push = away >= PvpHuntPushMinMeters
                            && Random.value < PvpHuntPushShare / (1f - PvpHuntHoldShare);
                        if (away <= PvpHuntMaxMeters && TryStartPvpHuntMove(from, away, push, now, hunter, false))
                        {
                            return false;
                        }
                    }
                    m_PvpHiding = false;
                    if (!blocked)
                    {
                        BotLog(LogLevel.Information,
                            "PvP: {Name} nao apareceu; saindo da vigia para outro lugar.",
                            target.channel?.owner?.playerID.characterName ?? "?");
                    }
                    m_PvpHuntPhase = 0;
                    m_PvpHuntHealWatch = false;
                    return false;
                }
                // 0.1.0-y17.12: watched from cover only (SkaliaBotPvpCoverY).
                if (!treating && TryLeaveOpenWatch(target, now))
                {
                    return false;
                }
                FacePvpHuntPoint(now);
                return true;
            }
            var lastContact = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt);
            if (blocked
                || m_PvpEscapeActive
                || m_PvpEscapeRequested
                || lastContact <= m_PvpHuntKey
                || now - lastContact > PvpMemorySeconds)
            {
                return false;
            }
            var seen = m_PvpLastSeenAt >= m_PvpHitFromAt;
            m_PvpHuntPoint = seen ? m_PvpLastSeenPoint : m_PvpHitFrom;
            var position = m_Player.transform.position;
            var meters = (m_PvpHuntPoint - position).GetHorizontalMagnitude();
            var name = target.channel?.owner?.playerID.characterName ?? "?";
            if (treating)
            {
                // Eyes on where he was while the bandage goes on; the key
                // is not taken, so the hunt decides when it ends.
                if (!m_PvpHuntHealWatch)
                {
                    StartPvpHuntWatch(now, null);
                    m_PvpHuntHealWatch = true;
                    BotLog(LogLevel.Information,
                        "PvP: perdi {Name} durante o curativo; olhando para {Where} a {Meters:0}m enquanto curo.",
                        name,
                        seen ? "onde ele estava" : "de onde veio o tiro",
                        meters);
                }
                return false;
            }
            m_PvpHuntKey = lastContact;
            m_PvpHuntCoverDone = false;
            m_PvpWatchCoverMoves = 0;
            var roll = Random.value;
            // 0.2.2y8: a man with no gun is gone after (SkaliaBotPvpChaseY).
            if (!m_PvpEnemyArmed && meters <= PvpHuntMaxMeters
                && TryStartPvpHuntMove(position, meters, true, now, name, seen))
            {
                return false;
            }
            // 0.1.0-y17: the round came from a man it never placed: hidden
            // from that side, watching it (SkaliaBotShotSectorY).
            // 0.1.0-y17.1: not when the hit came from a man it had found
            // (AlemaoGamer, 23:21 of 17/11, hid from blazex at 151m whom it
            // had shot at), nor from a side past what it hears (100m).
            if (!seen && !m_PvpHitFromKnown && meters <= HearShotMeters)
            {
                m_PvpHiding = true;
                if (TryStartPvpCoverMove(now))
                {
                    return false;
                }
                if (!IsChestExposedToHim(m_PvpHuntPoint, position, now))
                {
                    StartPvpHide(now, meters, name);
                    return false;
                }
                // No cover from that side within reach: not planted in the
                // open, the hunt as before.
                m_PvpHiding = false;
            }
            var pushRoll = roll >= PvpHuntHoldShare && roll < PvpHuntHoldShare + PvpHuntPushShare;
            if (meters <= PvpHuntMaxMeters && roll >= PvpHuntHoldShare
                && (!pushRoll || meters >= PvpHuntPushMinMeters))
            {
                var push = pushRoll;
                if (TryStartPvpHuntMove(position, meters, push, now, name, seen))
                {
                    return false;
                }
            }
            // 0.2.2y7: from cover (SkaliaBotPvpCoverY).
            if (TryStartPvpCoverMove(now))
            {
                return false;
            }
            // 0.1.0-y17.2: no cover within 24m and in his line from where
            // he was: not planted there in the open (the tester, 18/11: "fica
            // parado exposto observando ... nao busca flankear/recuar da a
            // volta"); around him as the players who went round, and a new
            // look for cover where it gets to.
            if (m_PvpEnemyArmed && meters <= PvpHuntMaxMeters
                && IsChestInHisLine(m_PvpHuntPoint, position)
                && TryStartPvpHuntMove(position, meters, false, now, name, seen))
            {
                BotLog(LogLevel.Information, "PvP: sem cobertura de onde {Name} estava e a vista dele; mudando de lugar em vez de vigiar no aberto.", name);
                return false;
            }
            StartPvpHuntWatch(now, null);
            BotLog(LogLevel.Information,
                "PvP: perdi {Name}; fico olhando para {Where} a {Meters:0}m por {Seconds:0}s.",
                name,
                seen ? "onde ele estava" : "de onde veio o tiro",
                meters,
                m_PvpHuntUntil - now);
            // 0.1.0-y17.14: a shot only heard: the way he would come
            // (SkaliaBotPvpWayInY).
            if (!seen)
            {
                ApplyPvpWayIn(m_PvpHuntPoint, name, now);
            }
            return false;
        }

        private void StartPvpHuntWatch(float now, string? after)
        {
            m_PvpWayInActive = false;
            var lastContact = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt);
            var until = lastContact + s_PvpHuntHoldSeconds[Random.Range(0, s_PvpHuntHoldSeconds.Length)];
            m_PvpHuntPhase = 1;
            m_PvpHuntUntil = Mathf.Max(until, now + 5f);
            m_PvpHuntYaw = m_CurrentYaw;
            m_PvpHuntPitch = m_CurrentPitch;
            // Stood 2 of 4, crouched 1, prone 1.
            var posture = Random.value;
            m_PvpHuntPosture = posture < 0.5f ? 0 : posture < 0.75f ? 1 : 2;
            if (after != null)
            {
                BotLog(LogLevel.Information,
                    "PvP: {After} na procura; olhando para onde ele estava por {Seconds:0}s.",
                    after,
                    m_PvpHuntUntil - now);
            }
        }

        // Straight to the point, or around it: a spot about 0.8 of the
        // distance from it, 60 degrees off the line to the bot (40 and 80
        // if that one is no good), on either side.
        // 0.1.0-y17.7 (test of 18/11, 17:36:31-17:37:22): Vultox, no round
        // left for the Maplestrike and only the knife, took TheGhostSniper
        // back from its memory, went "dando a volta pelo lado" 46m toward
        // where his rounds came from, walked into the tester's line and died
        // fleeing "sem bala". With no rounds and him armed the fight is
        // left (SkaliaBotPvpNoAmmoY); going to him is the opposite.
        private float m_PvpHuntNoRoundsLogAt = float.NegativeInfinity;

        private bool TryStartPvpHuntMove(Vector3 position, float meters, bool push, float now, string name, bool seen)
        {
            if (m_PvpEnemyArmed && GetPvpGunRange() <= 0f)
            {
                if (now >= m_PvpHuntNoRoundsLogAt)
                {
                    m_PvpHuntNoRoundsLogAt = now + 15f;
                    BotLog(LogLevel.Information,
                        "PvP: sem bala para {Name}, que esta armado; nao vou ate ele, fico escondido vigiando.",
                        name);
                }
                return false;
            }
            Vector3 spot;
            if (push)
            {
                if (!TryGetPvpHuntSpot(m_PvpHuntPoint, position, out spot))
                {
                    return false;
                }
            }
            else
            {
                var away = position - m_PvpHuntPoint;
                away.y = 0f;
                if (away.sqrMagnitude < 1f)
                {
                    return false;
                }
                var radius = Mathf.Clamp(meters * 0.8f, 10f, 45f);
                var side = Random.value < 0.5f ? 1f : -1f;
                var found = false;
                spot = position;
                foreach (var angle in new[] { 60f, 40f, 80f })
                {
                    foreach (var sign in new[] { side, -side })
                    {
                        var direction = Quaternion.Euler(0f, angle * sign, 0f) * away.normalized;
                        if (TryGetPvpHuntSpot(m_PvpHuntPoint + direction * radius, position, out spot))
                        {
                            found = true;
                            break;
                        }
                    }
                    if (found)
                    {
                        break;
                    }
                }
                if (!found)
                {
                    return false;
                }
            }
            var way = (spot - position).GetHorizontalMagnitude();
            // 0.1.0-y17.2: cover looked for again where it gets to.
            m_PvpHuntCoverDone = false;
            m_FleeingFromPlayerName = PvpHuntName;
            // Sprinting about 5 m/s; the players took 10 to 20s.
            SetObjective(ObjectiveType.Flee, spot, now + Mathf.Clamp(way / 5f + 4f, 6f, 20f));
            m_PvpHuntPhase = 2;
            m_PvpHuntUntil = now + Mathf.Clamp(way / 5f + 4f, 6f, 20f);
            BotLog(LogLevel.Information,
                "PvP: perdi {Name} a {Meters:0}m; {How} ({Way:0}m ate {Spot}), atras de {What}.",
                name,
                meters,
                push ? "indo direto ate la" : "dando a volta pelo lado",
                way,
                FormatPosition(spot),
                seen ? "onde ele estava" : "de onde veio o tiro");
            return true;
        }

        private bool TryGetPvpHuntSpot(Vector3 desired, Vector3 position, out Vector3 spot)
        {
            spot = desired;
            var now = Time.realtimeSinceStartup;
            if (!TryGetWalkableGround(desired, out var grounded)
                || Mathf.Abs(grounded.y - position.y) > 6f
                || WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                || IsHazardousPosition(grounded, now, 1.5f)
                || IsInRadiation(grounded)
                || IsWithinMegaReach(grounded)
                || !SkaliaBotUnityNavmesh.TrySnap(grounded, 2f, out var onMesh)
                || (onMesh - grounded).GetHorizontalMagnitude() > 2f)
            {
                return false;
            }
            spot = onMesh;
            return true;
        }

        // Planted, the head on the point at the fight's turn rate.
        private void FacePvpHuntPoint(float now)
        {
            var to = m_PvpHuntPoint - m_Player.look.aim.position;
            if (to.sqrMagnitude < 0.25f)
            {
                return;
            }
            var horizontal = new Vector2(to.x, to.z).magnitude;
            var wantPitch = 90f - Mathf.Atan2(to.y, horizontal) * Mathf.Rad2Deg;
            var step = PvpTurnDegreesPerSecond * Time.fixedDeltaTime;
            m_PvpHuntYaw = Mathf.MoveTowardsAngle(m_PvpHuntYaw, DirectionToYaw(to), step);
            m_PvpHuntPitch = Mathf.Clamp(Mathf.MoveTowardsAngle(m_PvpHuntPitch, wantPitch, step * 0.7f), 0.5f, 179.5f);
            m_CurrentYaw = m_PvpHuntYaw;
            m_CurrentPitch = m_PvpHuntPitch;
            m_DesiredYaw = m_PvpHuntYaw;
            m_DesiredPitch = m_PvpHuntPitch;
            // The bandage has its own posture (prone or crouched in cover).
            if (m_TreatmentPhase == 0)
            {
                m_PostureKey = m_PvpHuntPosture;
            }
            m_LastStuckCheckPosition = m_Player.transform.position;
            m_NextStuckCheckTime = now + 2f;
            m_IsSprinting = false;
            m_NavigationJumpUntil = 0f;
        }

        // 0.2.2y5: no loot by him while he is remembered. In the bot fight
        // of 17/11 (03:10-03:15, 50-70m) Vultox went for the Heartbreaker
        // lying where LilJuninho stood and LilJuninho for a magazine where
        // Vultox stood; they walked into each other at 16m. The players of
        // 08/11 at 40-100m (rec/pvp17.py): the distance to where the other
        // one was stayed about the same over the minute (68, 79, 69m), only
        // 5 of 26 got within half of it, by going for him.
        private float m_PvpLootSkipLogAt = float.NegativeInfinity;

        private bool IsLootTowardPvpEnemy(ItemData item, float now)
        {
            if (m_PvpTarget == null || IsPvpHunting())
            {
                return false;
            }
            var lastContact = Mathf.Max(m_PvpLastSeenAt, m_PvpHitFromAt);
            if (now - lastContact > PvpMemorySeconds)
            {
                return false;
            }
            var enemy = m_PvpLastSeenAt >= m_PvpHitFromAt ? m_PvpLastSeenPoint : m_PvpHitFrom;
            var own = (m_Player.transform.position - enemy).GetHorizontalMagnitude();
            var toItem = (item.point - enemy).GetHorizontalMagnitude();
            if (toItem >= own || toItem >= Mathf.Max(own * 0.5f, 20f))
            {
                return false;
            }
            IgnoreLoot(item.instanceID, Mathf.Max(now + 5f, lastContact + PvpMemorySeconds));
            if (now >= m_PvpLootSkipLogAt)
            {
                m_PvpLootSkipLogAt = now + 15f;
                BotLog(LogLevel.Information,
                    "Loot {LootInstanceId} fica para depois: a {Meters:0}m de onde {Name} estava (o bot esta a {Own:0}m dele).",
                    item.instanceID,
                    toItem,
                    m_PvpTarget.channel?.owner?.playerID.characterName ?? "?",
                    own);
            }
            return true;
        }
    }
}
