using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17 (the tester, 17/11: "quero que eles parem de olhar da onde veio o
    // tiro e passem a buscar cobertura sabendo com base no ultimo lugar que
    // veio o tiro e reposicionar para ter vantagem. ... informacao e mais
    // preciosa que tudo. se ele tomou um tiro ouviu e nao sabe da onde veio
    // ele tem que se esconder ate achar a pessoa/bot").
    //
    // Until y16 a shot heard (or a hit from a man not found) gave the bot
    // the exact point the round left from. Now it gives a direction with
    // the error the players of 08/11 had (rec/m17a.py): a shot off their
    // screen (more than 60 degrees off) within 100m, the heading where
    // their turn stopped was 25, 31, 35, 37, 47, 47, 66, 78, 92, 115 and
    // 127 degrees off the shooter (median 47; the same turns at random
    // moments with no shot, median 97). So the estimate is the true
    // direction off by a draw of those, to either side, and the sector
    // around it is 47 degrees each way (half the turns were within it).
    // Each new shot from the same side narrows it (the mean of the
    // estimates); with none it widens as far as a man sprinting (5 m/s,
    // 30-50m in 8s in the same recordings) could have gone.
    //
    // The distance: only near, middle or far by the sound, in the bands
    // the fight already uses (SkaliaBotPvpFeelY.PvpBand).
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private static readonly float[] s_ShotErrorQuantiles = { 0f, 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] s_ShotErrorDegrees = { 25f, 35f, 47f, 85f, 127f };
        private const float ShotSectorHalfDegrees = 47f;
        private const float ShotSectorMaxHalfDegrees = 90f;
        private const float ShotSectorRunMetersPerSecond = 5f;
        private static readonly float[] s_ShotBandMeters = { 8f, 27f, 60f, 115f, 200f };

        private Vector3 m_ShotSectorApex;
        private float m_ShotSectorYaw;
        private float m_ShotSectorMeters;
        private int m_ShotSectorCount;
        private float m_ShotSectorAt = float.NegativeInfinity;
        private Player? m_ShotSectorLastShooter;
        // 0.1.0-y17.5: the sector set from where the man was seen (his
        // steps heard after), with no turn error, only how far he could
        // have run since (SkaliaBotPvpMemoryY).
        private bool m_ShotSectorFromSeen;
        private float m_ShotSectorLastShotAt = float.NegativeInfinity;

        // A shot heard from a point it does not see: the sector updated and
        // the point it now believes, at the shooter's eye height guess.
        // True when the shot was on the same side as the sector it had.
        // With onlySameSide a shot from another side leaves it as it was.
        private bool NoteShotSector(Player shooter, Vector3 origin, float now, out Vector3 believed, bool onlySameSide = false)
        {
            var eye = m_Player.look.aim.position;
            // The same round heard and then felt: one estimate, not two.
            if (shooter == m_ShotSectorLastShooter && now - m_ShotSectorLastShotAt < 0.5f && IsShotSectorFresh(now))
            {
                believed = ShotSectorPoint();
                return true;
            }
            var to = origin - eye;
            var meters = to.GetHorizontalMagnitude();
            var error = DrawFromQuantiles(s_ShotErrorQuantiles, s_ShotErrorDegrees) * (Random.value < 0.5f ? -1f : 1f);
            var yaw = DirectionToYaw(to) + error;
            var band = s_ShotBandMeters[PvpBand(meters)];
            var same = IsShotSectorFresh(now)
                && Mathf.Abs(Mathf.DeltaAngle(m_ShotSectorYaw, yaw)) <= ShotSectorHalfNow(now) + ShotSectorHalfDegrees;
            if (onlySameSide && !same)
            {
                believed = ShotSectorPoint();
                return false;
            }
            m_ShotSectorLastShooter = shooter;
            m_ShotSectorLastShotAt = now;
            m_ShotSectorFromSeen = false;
            if (same)
            {
                // The same side: the mean of the estimates so far.
                m_ShotSectorCount++;
                m_ShotSectorYaw += Mathf.DeltaAngle(m_ShotSectorYaw, yaw) / m_ShotSectorCount;
                m_ShotSectorMeters += (band - m_ShotSectorMeters) / m_ShotSectorCount;
            }
            else
            {
                m_ShotSectorCount = 1;
                m_ShotSectorYaw = yaw;
                m_ShotSectorMeters = band;
            }
            m_ShotSectorApex = eye;
            m_ShotSectorAt = now;
            believed = ShotSectorPoint();
            return same;
        }

        // 0.1.0-y17.19: a point on the side the sector holds, as wide as
        // a new round is taken for the same side (NoteShotSector).
        private bool IsOnShotSector(Vector3 point, float now)
        {
            return IsShotSectorFresh(now)
                && Mathf.Abs(Mathf.DeltaAngle(m_ShotSectorYaw, DirectionToYaw(point - m_ShotSectorApex)))
                    <= ShotSectorHalfNow(now) + ShotSectorHalfDegrees;
        }

        // 0.1.0-y17.19: back to a man it remembers: the sector on where it
        // knew him, not on the rounds of the one just fought.
        private void AimShotSectorAt(Vector3 point, float at, bool seen)
        {
            var eye = m_Player.look.aim.position;
            var to = point - eye;
            m_ShotSectorYaw = DirectionToYaw(to);
            m_ShotSectorMeters = Mathf.Max(5f, to.GetHorizontalMagnitude());
            m_ShotSectorCount = 1;
            m_ShotSectorApex = eye;
            m_ShotSectorAt = at;
            m_ShotSectorFromSeen = seen;
            m_ShotSectorLastShooter = null;
        }

        private bool IsShotSectorFresh(float now)
        {
            return now - m_ShotSectorAt <= PvpMemorySeconds;
        }

        private float ShotSectorHalfNow(float now)
        {
            var half = m_ShotSectorFromSeen ? 0f : ShotSectorHalfDegrees / Mathf.Sqrt(Mathf.Max(1, m_ShotSectorCount));
            var age = Mathf.Max(0f, now - m_ShotSectorAt);
            half += Mathf.Atan(ShotSectorRunMetersPerSecond * age / Mathf.Max(5f, m_ShotSectorMeters)) * Mathf.Rad2Deg;
            return Mathf.Min(half, ShotSectorMaxHalfDegrees);
        }

        private Vector3 ShotSectorPoint(float offsetDegrees = 0f)
        {
            var direction = Quaternion.Euler(0f, m_ShotSectorYaw + offsetDegrees, 0f) * Vector3.forward;
            var point = m_ShotSectorApex + direction * m_ShotSectorMeters;
            // Not under a hill: a man stands on the ground at least.
            point.y = Mathf.Max(point.y, LevelGround.getHeight(point) + PvpStandEyeHeight);
            return point;
        }

        private string DescribeShotSector(float now)
        {
            var band = m_ShotSectorMeters < 15f ? "perto" : m_ShotSectorMeters < 80f ? "medio" : "longe";
            return $"{Mathf.Repeat(m_ShotSectorYaw, 360f):0} graus +-{ShotSectorHalfNow(now):0}, {band}";
        }

        // The run of a hit from a man not found (SkaliaBotPvpFeelY): a spot
        // the chest is hidden at from the whole sector. The players hit on
        // 08/11 ran 8-10m in 2s and 14-25m in 4s (so up to 24m here), 10 of
        // 16 to the side of the line of the shot (60 to 120 degrees), 4
        // away and 2 toward it, several into a building (a roof over them
        // 26-71% of the time). Ladders stay out: only spots on the mesh at
        // the bot's height or close to it.
        private bool TryFindPvpSectorCover(Vector3 position, float now, out Vector3 spot, out bool roof, out int hiddenFrom)
        {
            spot = position;
            roof = false;
            hiddenFrom = 0;
            if (!IsShotSectorFresh(now))
            {
                return false;
            }
            var half = ShotSectorHalfNow(now);
            var points = new Vector3[5];
            for (var i = 0; i < points.Length; i++)
            {
                var offset = Mathf.Lerp(-half, half, i / (float)(points.Length - 1));
                points[i] = ShotSectorPoint(offset);
            }
            var toward = ShotSectorPoint() - position;
            toward.y = 0f;
            var best = float.MaxValue;
            var found = false;
            for (var radius = 3f; radius <= 24.1f; radius += 3f)
            {
                for (var step = 0; step < 16; step++)
                {
                    var direction = Quaternion.Euler(0f, step * 22.5f + (radius % 6f == 0f ? 11.25f : 0f), 0f) * Vector3.forward;
                    if (!TryGetWalkableGround(position + direction * radius, out var grounded)
                        || Mathf.Abs(grounded.y - position.y) > 1.5f
                        || WaterUtility.isPointUnderwater(grounded + Vector3.up * 0.5f)
                        || IsHazardousPosition(grounded, now, 1.5f)
                        || IsInRadiation(grounded)
                        || IsWithinMegaReach(grounded)
                        || !SkaliaBotUnityNavmesh.TrySnap(grounded, 1.5f, out var onMesh)
                        || (onMesh - grounded).GetHorizontalMagnitude() > 1.5f
                        || Mathf.Abs(onMesh.y - grounded.y) > 1.5f)
                    {
                        continue;
                    }
                    var hidden = 0;
                    foreach (var point in points)
                    {
                        // 0.1.0-y17.18: hidden by its own cover (SkaliaBotPvpCoverY).
                        if (!IsChestSeenFrom(point, onMesh))
                        {
                            hidden++;
                        }
                    }
                    if (hidden < 3)
                    {
                        continue;
                    }
                    var angle = toward.sqrMagnitude > 0.01f ? Vector3.Angle(toward, direction) : 90f;
                    var hasRoof = Physics.Raycast(onMesh + Vector3.up * 1.9f, Vector3.up, 20f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
                    // Nearer first; fully hidden, a roof and the side the
                    // players ran to count; toward the sector least.
                    var score = radius
                        + (points.Length - hidden) * 6f
                        - (hasRoof ? 4f : 0f)
                        + (angle < 60f ? 12f : angle > 120f ? 3f : 0f);
                    if (score < best)
                    {
                        best = score;
                        spot = onMesh;
                        roof = hasRoof;
                        hiddenFrom = hidden;
                        found = true;
                    }
                }
                if (found && hiddenFrom == points.Length && radius >= 9f)
                {
                    break;
                }
            }
            return found;
        }

        // Steps of a man it saw: the sector from where it saw him, no
        // farther than the steps carry.
        private Vector3 NoteStepsOfSeenMan(float reach, float now)
        {
            var eye = m_Player.look.aim.position;
            if (!m_ShotSectorFromSeen || m_PvpLastSeenAt > m_ShotSectorAt)
            {
                var to = m_PvpLastSeenPoint - eye;
                m_ShotSectorYaw = DirectionToYaw(to);
                m_ShotSectorMeters = Mathf.Max(5f, to.GetHorizontalMagnitude());
                m_ShotSectorCount = 1;
                m_ShotSectorApex = eye;
                m_ShotSectorAt = m_PvpLastSeenAt;
                m_ShotSectorFromSeen = true;
            }
            m_ShotSectorMeters = Mathf.Clamp(reach, 5f, m_ShotSectorMeters);
            return ShotSectorPoint();
        }

        // From the hunt: hit and not found, the bot stays hidden and
        // watches the sector until it sees him, a new shot moves the
        // sector, or the time runs out. The players who heard a shot from a
        // man they did not see (rec/m16.py, 12 times) fired none in the
        // next 30s, the first at 40s, 5 of 12 within 120s: hidden that
        // long, 40 to 120s, then the hunt as after losing a man seen.
        private const float PvpHideMinSeconds = 40f;
        private const float PvpHideMaxSeconds = 120f;
        private bool m_PvpHiding;
        private float m_PvpHideStartedAt;

        private void StartPvpHide(float now, float meters, string name)
        {
            StartPvpHuntWatch(now, null);
            m_PvpHuntUntil = now + Random.Range(PvpHideMinSeconds, PvpHideMaxSeconds);
            m_PvpHiding = true;
            m_PvpHideStartedAt = now;
            BotLog(LogLevel.Information,
                "PvP: nao sei onde {Name} esta; escondido olhando para o setor {Sector} (~{Meters:0}m) por ate {Seconds:0}s.",
                name,
                DescribeShotSector(now),
                meters,
                m_PvpHuntUntil - now);
            // 0.1.0-y17.14: the way he would come, not the wall toward him
            // (SkaliaBotPvpWayInY).
            ApplyPvpWayIn(m_PvpHuntPoint, name, now);
        }

        // A round at the bot from close, and the turn to it found no one:
        // as a hit from a man not found, without the hit.
        private void StartPvpUnknownThreat(Player shooter, float now, string why = "tiro passou por mim e nao achei quem foi")
        {
            m_PvpTarget = shooter;
            m_PvpAware = false;
            m_PvpEngaged = false;
            m_PvpLooking = false;
            m_PvpShots = 0;
            m_PvpHits = 0;
            m_PvpTaken = 0;
            m_PvpLastHitAt = now;
            m_PvpHitFrom = ShotSectorPoint();
            m_PvpHitFromAt = now;
            m_PvpHitFromKnown = false;
            m_PvpEscapeRequested = true;
            BotLog(LogLevel.Information,
                "PvP: {Why}; setor {Sector}; saindo da linha.",
                why,
                DescribeShotSector(now));
        }

        // A shot heard during the fight with the man not found: the sector
        // and the point the watch faces follow it. From another side only
        // a round at the bot moves it: who fired is not known, only where
        // from and whether it came its way.
        private void NoteShotDuringUnknownFight(Player shooter, Vector3 origin, bool aimed, float now)
        {
            var same = NoteShotSector(shooter, origin, now, out var believed, !aimed);
            if (!same && !aimed)
            {
                return;
            }
            // 0.1.0-y17.19: whose round it was, and the clue it had before
            // (SkaliaBotPvpMemoryY, GivePvpClueBack).
            NotePvpClueShooter(shooter, now);
            m_PvpHitFrom = believed;
            m_PvpHitFromAt = now;
            m_PvpHitFromKnown = false;
            if (m_PvpHuntPhase == 1)
            {
                m_PvpHuntPoint = believed;
                if (m_PvpWayInActive)
                {
                    ApplyPvpWayIn(believed, shooter.channel?.owner?.playerID.characterName ?? "?", now);
                }
                // A new clue: the hide starts over, the hunt waits.
                m_PvpHuntKey = Mathf.Max(m_PvpHuntKey, now);
                if (m_PvpHiding)
                {
                    // 0.1.0-y17.3: never past the longest hide of the
                    // players (120s from its start), or a man firing every
                    // 10s would keep it there for good.
                    m_PvpHuntUntil = Mathf.Min(Mathf.Max(m_PvpHuntUntil, now + PvpHideMinSeconds),
                        m_PvpHideStartedAt + PvpHideMaxSeconds);
                }
            }
            BotLog(LogLevel.Debug, "PvP: tiro de novo do setor {Sector}.", DescribeShotSector(now));
        }
    }
}
