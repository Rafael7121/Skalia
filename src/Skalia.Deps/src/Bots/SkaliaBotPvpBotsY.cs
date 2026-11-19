using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.2.2y (the tester, 17/11: "ligue o pvp também entre eles" and "jogador
    // tem que ser atacado também. mas tire o wallhack deles"): a bot opens
    // fire on a player or another bot it sees, and shoots back at either.
    //
    // Seeing is what a player's screen shows, nothing more:
    // - the 120 degrees in front of it (IsInPvpView);
    // - a line no wall, rock, car, building or small prop crosses
    //   (BLOCK_COLLISION plus the SMALL layer, which bullets cross but the
    //   eye does not);
    // - no bush on the line. The bushes have no collider at all (their .dat
    //   says Has_Clip_Prefab false), so every ray went through them: a
    //   player crouched in one was seen from 200m. They are found from the
    //   map's own list of resources.
    // Night is not a limit: on 08/11 the players opened fire farther at
    // night (median 118m, 26 fights) than by day (57m, 17 fights).
    //
    // How the players of 08/11 opened their fights (rec/pvp9.py: the 38
    // fights where the shooter fired first): the other one was in their
    // 120 degrees for 0, 0.4, 2.2, 3.6 and 6.2s (min, p25, median, p75,
    // p90; 7s the longest but one) before the first shot, at 6 to 261m
    // (median 85m).
    //
    // A bot in the same in-game group is left alone (the game itself takes
    // no damage between members of a group unless Friendly_Fire is on).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private static readonly float[] s_PvpOpenSeconds = { 0f, 0.4f, 2.2f, 3.6f, 6.2f, 7f };
        private const float PvpOpenScanSeconds = 0.25f;

        // 0.2.2y7 (the tester, 17/11: "passei na frente deles inumeras vezes e
        // nao me notaram"). In the test of 17/11:
        // - 07:37:04-14 LilJuninho looted with the tester 1 to 8m in front of
        //   it. The wait above is the players' at any range (median 85m);
        //   the 5 fights opened within 15m waited 0, 0.6, 1.2, 1.2 and 7s.
        //   And each scan he was out of sight (a door frame, a shelf)
        //   started the wait over.
        // - 07:42:45 LilJuninho, watching Vultox 110m off, let the tester run by
        //   at 5m: with a man on its mind it looked for no one else.
        // - Vultox with a Pocketknife, Ghost with a Saw: no gun, no reach,
        //   no one seen at 1 to 20m.
        // Now the close wait is the close fights', a second out of sight
        // does not reset it, a bot not shooting at its man still sees the
        // others (and turns to one in sight when its own is not), and with
        // no gun the 10m of the melee fight are watched.
        private static readonly float[] s_PvpOpenCloseSeconds = { 0f, 0.6f, 1.2f, 1.2f, 4.7f, 7f };
        private const float PvpOpenCloseMeters = 15f;
        private const float PvpSpotGraceSeconds = 1f;
        private float m_PvpSpottedLastAt = float.NegativeInfinity;

        private Player? m_PvpSpotted;
        private float m_PvpSpottedSince;
        private float m_PvpOpenAt;
        private float m_PvpFireNotBefore;
        private Player? m_PvpFireNotBeforeTarget;
        // The wait is the players' answer to a hit (SkaliaBotPvpY), not a
        // man who has not seen it yet: it stands whatever he does.
        private bool m_PvpFireNotBeforeFirm;
        private float m_NextPvpOpenScanAt;

        // ---- sight ----
        private static readonly int s_PvpSightMask = RayMasks.BLOCK_COLLISION | RayMasks.SMALL;
        private const float BushCellMeters = 8f;
        // Fallback size of a bush when its model gives none: an estimate,
        // the game files carry no size for them.
        private const float BushDefaultRadius = 1f;
        private const float BushDefaultHeight = 1.5f;

        private sealed class PvpBush
        {
            public ResourceSpawnpoint Spawn = null!;
            public float Radius;
            public float Height;
        }

        private static Dictionary<long, List<PvpBush>>? s_PvpBushes;
        private static string? s_PvpBushesLevel;

        // A bush hides a player until he shoots from it: a hit from the
        // shooter the bot found (in its 120 degrees, as the players of 08/11
        // always found theirs) gives his bush away for as long as the bot
        // keeps looking for a lost one (6s); the bullets go through it.
        // The trigger still waiting on the players' time to the first shot.
        // 0.1.0-y17.3 (the tester, 18/11: "o mais sensato seria ele abrir fogo
        // o mais rapido possivel ... e situacional"): the wait is for a man
        // who has not seen the bot. Once he turns to it with a gun in hand
        // or on his back (or with anything within the blade's 10m), he may
        // have it on his screen: the trigger goes as soon as the sights
        // allow.
        private bool IsPvpFireHeld(Player target, float now)
        {
            if (target != m_PvpFireNotBeforeTarget || now >= m_PvpFireNotBefore)
            {
                return false;
            }
            if (!m_PvpFireNotBeforeFirm
                && IsPvpEnemyFacingMe(target)
                && (IsPvpArmedToTheEye(target)
                    || (target.transform.position - m_Player.transform.position).magnitude <= PvpMeleeChaseMeters))
            {
                m_PvpFireNotBefore = 0f;
                BotLog(LogLevel.Information,
                    "PvP: {Name} virou para mim; atirando sem esperar.",
                    target.channel?.owner?.playerID.characterName ?? "?");
                return false;
            }
            return true;
        }

        private bool HasPvpSight(Vector3 point, bool shotFromThere = false)
        {
            var origin = m_Player.look.aim.position;
            return !Physics.Linecast(origin, point, s_PvpSightMask, QueryTriggerInteraction.Ignore)
                && (shotFromThere || !IsBushOnLine(origin, point));
        }

        private static long BushCell(float x, float z)
        {
            return ((long)Mathf.FloorToInt(x / BushCellMeters) << 32) ^ (uint)Mathf.FloorToInt(z / BushCellMeters);
        }

        private static Dictionary<long, List<PvpBush>> GetPvpBushes()
        {
            var level = Level.info?.name ?? string.Empty;
            if (s_PvpBushes != null && s_PvpBushesLevel == level)
            {
                return s_PvpBushes;
            }
            var map = new Dictionary<long, List<PvpBush>>();
            var all = new List<ResourceSpawnpoint>();
            LevelGround.GatherAllTrees(all);
            foreach (var spawn in all)
            {
                var name = spawn?.asset?.name;
                if (spawn == null || name == null || !name.StartsWith("Bush"))
                {
                    continue;
                }
                var radius = BushDefaultRadius * Mathf.Max(spawn.scale.x, spawn.scale.z);
                var height = BushDefaultHeight * spawn.scale.y;
                var renderer = spawn.model != null ? spawn.model.GetComponentInChildren<Renderer>(true) : null;
                if (renderer != null && renderer.bounds.size.y > 0.3f)
                {
                    radius = Mathf.Max(renderer.bounds.extents.x, renderer.bounds.extents.z);
                    height = renderer.bounds.max.y - spawn.point.y;
                }
                var key = BushCell(spawn.point.x, spawn.point.z);
                if (!map.TryGetValue(key, out var list))
                {
                    list = new List<PvpBush>();
                    map[key] = list;
                }
                list.Add(new PvpBush { Spawn = spawn, Radius = radius, Height = height });
            }
            s_PvpBushes = map;
            s_PvpBushesLevel = level;
            return map;
        }

        // A live bush the line goes through below its top, away from the
        // eye itself (a player in a bush sees out of it).
        private static bool IsBushOnLine(Vector3 from, Vector3 to)
        {
            var bushes = GetPvpBushes();
            if (bushes.Count == 0)
            {
                return false;
            }
            var flat = new Vector2(to.x - from.x, to.z - from.z);
            var length = flat.magnitude;
            if (length < 0.01f)
            {
                return false;
            }
            var minX = Mathf.FloorToInt((Mathf.Min(from.x, to.x) - 4f) / BushCellMeters);
            var maxX = Mathf.FloorToInt((Mathf.Max(from.x, to.x) + 4f) / BushCellMeters);
            var minZ = Mathf.FloorToInt((Mathf.Min(from.z, to.z) - 4f) / BushCellMeters);
            var maxZ = Mathf.FloorToInt((Mathf.Max(from.z, to.z) + 4f) / BushCellMeters);
            var dir = flat / length;
            for (var cx = minX; cx <= maxX; cx++)
            {
                for (var cz = minZ; cz <= maxZ; cz++)
                {
                    // Only the cells along the line, not the whole box.
                    var center = new Vector2((cx + 0.5f) * BushCellMeters - from.x, (cz + 0.5f) * BushCellMeters - from.z);
                    var across = Mathf.Abs(center.x * dir.y - center.y * dir.x);
                    if (across > BushCellMeters)
                    {
                        continue;
                    }
                    if (!bushes.TryGetValue(((long)cx << 32) ^ (uint)cz, out var list))
                    {
                        continue;
                    }
                    foreach (var bush in list)
                    {
                        var spawn = bush.Spawn;
                        // A berry bush picked clean still stands; another
                        // cut down is gone.
                        if (spawn.isDead && !(spawn.asset?.isForage ?? false))
                        {
                            continue;
                        }
                        var rel = new Vector2(spawn.point.x - from.x, spawn.point.z - from.z);
                        var along = Vector2.Dot(rel, dir);
                        if (along < 1f || along > length + bush.Radius)
                        {
                            continue;
                        }
                        var side = Mathf.Abs(rel.x * dir.y - rel.y * dir.x);
                        if (side > bush.Radius)
                        {
                            continue;
                        }
                        var t = Mathf.Clamp01(along / length);
                        var height = Mathf.Lerp(from.y, to.y, t);
                        if (height < spawn.point.y + bush.Height && height > spawn.point.y - 0.5f)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        // 0.1.0-y17.6 (test of 18/11, 17:07:51): ToxicSniper, hit by the tester
        // 8s before and hearing his steps 2s before ("fico olhando para de
        // onde veio o tiro a 24m"; he was 6m off behind a house), turned to
        // Vultox seen at 74m and stood shooting that way; the tester came back
        // at 26m. The players who went from shooting at one man to another
        // within 15s (rec/m21e.py, 5 times) took one at 0.55, 0.66, 0.93,
        // 1.11 and 1.21 times the first one's distance, never one three
        // times as far. With the man it fights heard, seen or hitting it in
        // the last 10s, a new one past 1.25 times where it knows him is
        // remembered, not fought.
        private const float PvpSwitchFartherRatio = 1.25f;
        private Player? m_PvpFarSkipWho;
        private float m_PvpFarSkipLogAt = float.NegativeInfinity;

        private bool IsPvpSwitchTooFar(Player other, float otherMeters, Vector3 otherPoint, float now)
        {
            var target = m_PvpTarget;
            if (target == null || target.life == null || target.life.isDead)
            {
                return false;
            }
            var lastContact = Mathf.Max(m_PvpLastSeenAt, Mathf.Max(m_PvpHitFromAt, m_PvpLastHeardAt));
            if (now - lastContact > PvpActiveSeconds)
            {
                return false;
            }
            var known = m_PvpHitFromAt > m_PvpLastSeenAt ? m_PvpHitFrom : m_PvpLastSeenPoint;
            var knownMeters = (known - m_Player.look.aim.position).magnitude;
            if (otherMeters <= knownMeters * PvpSwitchFartherRatio)
            {
                return false;
            }
            if (other != m_PvpFarSkipWho || now >= m_PvpFarSkipLogAt)
            {
                m_PvpFarSkipWho = other;
                m_PvpFarSkipLogAt = now + 10f;
                BotLog(LogLevel.Information,
                    "PvP: vendo {New} a {Meters:0}m, mas fico com {Old} (noticia dele ha {Ago:0}s, a {Known:0}m); os jogadores so trocaram por outro ate 1,2x a distancia.",
                    other.channel?.owner?.playerID.characterName ?? "?",
                    otherMeters,
                    target.channel?.owner?.playerID.characterName ?? "?",
                    now - lastContact,
                    knownMeters);
                RememberPvpOtherAt(other, otherPoint, now, true);
            }
            return true;
        }

        private bool IsPvpAlly(Player other)
        {
            return m_Player.quests != null && m_Player.quests.isMemberOfSameGroupAs(other);
        }

        // Called from TickPvp while the bot has no player to fight.
        private void TryOpenFireOnBot(float now)
        {
            if (now < m_NextPvpOpenScanAt)
            {
                return;
            }
            m_NextPvpOpenScanAt = now + PvpOpenScanSeconds;
            if (!Provider.isPvP
                || m_Player.life.isDead
                || m_ObjectiveType == ObjectiveType.EscapeSwarm
                || (m_ObjectiveType == ObjectiveType.Flee && !IsPvpHunting() && !IsPvpChasing() && !IsFleeingBoss())
                || m_ObjectiveType == ObjectiveType.EscapeHazard
                || m_CachedNearestZombieDistance <= PvpZombieFirstMeters
                || m_Player.stance.stance == EPlayerStance.CLIMB
                || m_Player.stance.stance == EPlayerStance.SWIM
                || m_Player.movement.getVehicle() != null
                || (m_PvpTarget != null && (m_PvpEngaged || m_PvpVisible))
                // 0.1.0-y17.6: turned to the steps at its back, it looks
                // for that man first (SkaliaBotPvpMemoryY).
                || (m_PvpTarget != null && now - m_PvpSecondStepsAt < PvpSecondStepsLookSeconds))
            {
                m_PvpSpotted = null;
                return;
            }
            var gunReach = now < m_PvpGunFailUntil ? 0f : Mathf.Min(GetPvpGunRange(), PvpMaxMeters);
            var reach = gunReach > 0f ? gunReach : PvpMeleeChaseMeters;
            var origin = m_Player.look.aim.position;
            Player? best = null;
            var bestDistance = float.MaxValue;
            Vector3 bestPoint = default;
            foreach (var client in Provider.clients)
            {
                var other = client?.player;
                if (other == null
                    || other == m_Player
                    || other == m_PvpTarget
                    || other.life == null
                    || other.life.isDead
                    || other.channel?.owner == null
                    || IsPvpAlly(other))
                {
                    continue;
                }
                var point = GetPvpAimPoint(other);
                var distance = (point - origin).magnitude;
                if (distance > reach || distance >= bestDistance)
                {
                    continue;
                }
                // 0.1.0-y17.8: running from the Mega, only a man nearer
                // than it (SkaliaBotPvpY, IsPvpManNearerThanMega).
                if (IsFleeingBoss() && distance >= m_CachedNearestBossDistance)
                {
                    continue;
                }
                // 0.2.2y6: or on the third-person screen (SkaliaBotPvpCameraY).
                if (!(IsInPvpView(point) && TryGetPvpVisiblePoint(other, false, out point, out _))
                    && !TryGetPvpCameraSight(other, out point, out _))
                {
                    continue;
                }
                best = other;
                bestDistance = distance;
                bestPoint = point;
            }
            if (best == null)
            {
                if (m_PvpSpotted != null && now - m_PvpSpottedLastAt <= PvpSpotGraceSeconds)
                {
                    return;
                }
                m_PvpSpotted = null;
                return;
            }
            if (IsPvpSwitchTooFar(best, bestDistance, bestPoint, now))
            {
                m_PvpSpotted = null;
                return;
            }
            m_PvpSpottedLastAt = now;
            if (best != m_PvpSpotted)
            {
                m_PvpSpotted = best;
                m_PvpSpottedSince = now;
                // 0.2.2y9: the man who just shot at it, found by turning to
                // the shot: that turn was the wait (SkaliaBotHearingY).
                // 0.1.0-y17.3 (the tester: "abrir fogo quanto antes e
                // situacional"): facing the bot with a gun, or with
                // anything within the blade's 10m (ToxicSniper's knife on
                // AlemaoGamer), he may be looking at it; the first shot as
                // soon as the turn and the sights allow
                // (SkaliaBotPvpLookCoverY).
                m_PvpOpenAt = IsHeardShooter(best, now)
                    || (IsPvpEnemyFacingMe(best) && (IsPvpArmedToTheEye(best) || bestDistance <= PvpMeleeChaseMeters))
                    ? now
                    : now + DrawFromQuantiles(s_PvpStepQuantiles,
                        bestDistance <= PvpOpenCloseMeters ? s_PvpOpenCloseSeconds : s_PvpOpenSeconds);
            }
            // 0.1.0-y17.3 (the tester, 18/11, Paradise Point: AlemaoGamer, gun in
            // hand with 11 rounds, "era pra ele ter rasgado ele na bala").
            // ToxicSniper ran at it with a knife from 8m, in front of it (30
            // degrees off its face) for 1.1s, and knifed it twice; it never
            // turned to him and fired nothing. The wait drawn here is the
            // players' time from a man in view to their first shot (the turn
            // and the sights inside it), but the bot spent it doing nothing
            // and only then turned and raised the gun. Now the turn and the
            // sights start at once and only the trigger waits for the drawn
            // time (ConfigurePvpInputCore).
            m_PvpFireNotBefore = m_PvpOpenAt;
            m_PvpFireNotBeforeTarget = best;
            m_PvpFireNotBeforeFirm = false;
            // The wait was spent looking at him: the fight starts with the
            // turn only (SetPvpReactionFrom's floor), not a second reaction.
            if (m_PvpTarget != null)
            {
                BotLog(LogLevel.Information,
                    "PvP: troquei de alvo: {Old} fora de vista, {New} a {Distance:0}m.",
                    m_PvpTarget.channel?.owner?.playerID.characterName ?? "?",
                    best.channel!.owner!.playerID.characterName,
                    bestDistance);
                // 0.1.0-y17.4: not forgotten (SkaliaBotPvpMemoryY).
                RememberPvpOther(m_PvpTarget, now);
                ResetPvpHunt();
                m_PvpEscapeRequested = false;
            }
            m_PvpTarget = best;
            m_PvpAware = true;
            m_PvpAwareSince = now;
            m_PvpReactAt = now;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpLastSeenAt = now;
            m_PvpLastSeenPoint = bestPoint;
            m_PvpLastHitAt = now;
            m_PvpShots = 0;
            m_PvpHits = 0;
            m_PvpTaken = 0;
            m_PvpSpotted = null;
            BotLog(LogLevel.Information,
                "PvP: vi {Kind} {Name} a {Distance:0}m; virando e mirando, primeiro tiro em {Wait:0.0}s (arma na mao {Weapon}).",
                s_ActiveSimulations.ContainsKey(best) ? "o bot" : "o jogador",
                best.channel!.owner!.playerID.characterName,
                bestDistance,
                Mathf.Max(0f, m_PvpOpenAt - now),
                m_Player.equipment.asset?.itemName ?? "nada");
        }
    }
}
