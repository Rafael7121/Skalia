using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // A ladder of the map: where to stand at its foot, which way to face and
    // where the climb ends. Russia has 52 of them; the roundhouse pit that
    // held a bot for 20 min has its only exit up one.
    internal sealed class BotLadder
    {
        public int Index;
        public string Name = string.Empty;
        public Vector3 Bottom;
        public Vector3 Top;
        public float FacingYaw;
        // 0.2.2x8: the foot is where the game's own climb check passes
        // (PlayerStance.simulate), walking in from it; JumpToGrab when the
        // ladder starts above the waist and only a jump reaches it.
        public Vector3 Center;
        public Vector3 Face;
        public bool JumpToGrab;
        public float StandGap;
        // 0.2.2x10: height of the wall between the top floor and the ladder
        // (the bridge rail, the barracks roof edge), 0 when none.
        public float LipHeight;
        public Collider[] Colliders = Array.Empty<Collider>();
    }

    internal static class SkaliaBotLadderMap
    {
        private const float PlayerRadius = 0.35f;

        private static bool s_Started;
        private static string? s_Map;
        private static readonly List<BotLadder> s_Ladders = new List<BotLadder>();

        public static IReadOnlyList<BotLadder> Ladders => s_Ladders;

        public static void EnsureStarted(ILogger logger)
        {
            var map = Level.info?.name;
            if (map == null || (s_Started && s_Map == map))
            {
                return;
            }
            s_Started = true;
            s_Map = map;
            s_Ladders.Clear();
            try
            {
                Build(logger);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Catalogo de escadas falhou.");
            }
        }

        private sealed class LadderPiece
        {
            public Collider Collider = null!;
            public string Name = string.Empty;
            public Transform Owner = null!;
            public Bounds Bounds;
            public bool Merged;
        }

        private static void Build(ILogger logger)
        {
            var colliders = new List<Collider>();
            var pieces = new List<LadderPiece>();
            var rejected = 0;
            var builtIn = 0;
            for (byte x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    var objects = LevelObjects.objects?[x, y];
                    if (objects == null)
                    {
                        continue;
                    }
                    foreach (var levelObject in objects)
                    {
                        var asset = levelObject?.asset;
                        var transform = levelObject?.transform;
                        if (asset == null || transform == null)
                        {
                            continue;
                        }
                        // 0.2.2x9: every collider the game climbs (tag
                        // Ladder), not only objects named Ladder: towers,
                        // water tanks and bridges carry their own.
                        colliders.Clear();
                        transform.GetComponentsInChildren(true, colliders);
                        foreach (var collider in colliders)
                        {
                            if (!collider.CompareTag("Ladder"))
                            {
                                continue;
                            }
                            if (asset.name.IndexOf("Ladder", StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                builtIn++;
                            }
                            pieces.Add(new LadderPiece { Collider = collider, Name = asset.name, Owner = transform, Bounds = collider.bounds });
                        }
                    }
                }
            }
            // The roundhouse pit ladder is two 6.8m pieces stacked: alone,
            // the lower one has no top and the upper one no foot.
            pieces.Sort((a, b) => a.Bounds.min.y.CompareTo(b.Bounds.min.y));
            var merged = 0;
            var group = new List<LadderPiece>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var first = pieces[i];
                if (first.Merged)
                {
                    continue;
                }
                group.Clear();
                group.Add(first);
                var bounds = first.Bounds;
                var normal = first.Collider.transform.up;
                for (var j = i + 1; j < pieces.Count; j++)
                {
                    var next = pieces[j];
                    if (next.Merged
                        || next.Bounds.min.y > bounds.max.y + 0.6f
                        || (next.Bounds.center - bounds.center).GetHorizontalMagnitude() > 0.4f
                        || Mathf.Abs(Vector3.Dot(next.Collider.transform.up, normal)) < 0.9f)
                    {
                        continue;
                    }
                    next.Merged = true;
                    group.Add(next);
                    bounds.Encapsulate(next.Bounds);
                    merged++;
                }
                if (TryDescribe(bounds, normal, first.Name, group, out var ladder, out var reason))
                {
                    ladder.Index = s_Ladders.Count;
                    s_Ladders.Add(ladder);
                    if (ladder.LipHeight > 0.3f)
                    {
                        logger.LogInformation(
                            "Escada {Name} em ({X:0.#}, {Y:0.#}, {Z:0.#}): mureta de {Lip:0.0}m entre o topo e a escada; desce pulando de costas por cima dela.",
                            ladder.Name,
                            bounds.center.x,
                            bounds.min.y,
                            bounds.center.z,
                            ladder.LipHeight);
                    }
                }
                else
                {
                    rejected++;
                    logger.LogInformation(
                        "Escada recusada: {Name} em ({X:0.#}, {Y:0.#}, {Z:0.#}) altura {Height:0.#}m: {Reason}.",
                        first.Name,
                        bounds.center.x,
                        bounds.min.y,
                        bounds.center.z,
                        bounds.size.y,
                        reason);
                }
            }
            logger.LogInformation(
                "Catalogo de escadas: {Count} escada(s) de mao utilizaveis ({Rejected} sem pe ou topo livre, {Merged} pedaco(s) empilhado(s) juntado(s), {BuiltIn} pedaco(s) de escada dentro de outros objetos).",
                s_Ladders.Count,
                rejected,
                merged,
                builtIn);
        }

        // PlayerStance starts a climb when a collider tagged Ladder is within
        // 0.75m in front of the waist and its face normal is the collider's
        // local up; the climb ends at the top with a push forward.
        // 32 of 52 Russia ladders were refused with one fixed foot (0.6m) and
        // a capsule test that also hit the ladder's own object; the foot now
        // tries 0.6-1.2m out and ignores colliders of the ladder itself.
        private static readonly float[] FootDistances = { 0.6f, 0.9f, 1.2f };
        private static readonly float[] ExitDistances = { 0.9f, 1.4f };
        private static readonly Collider[] s_Overlap = new Collider[16];

        // The foot is searched from 1.5m above the ladder's lowest point up
        // to 1.5m below its top: some ladders start under the floor they
        // stand on, and a ray from inside that floor never hits it.
        private static bool TryDescribe(Bounds bounds, Vector3 normal, string name, List<LadderPiece> pieces, out BotLadder ladder, out string reason)
        {
            ladder = null!;
            reason = "sem pe";
            normal.y = 0f;
            if (normal.sqrMagnitude < 0.25f || bounds.size.y < 1.5f)
            {
                reason = "inclinada ou baixa";
                return false;
            }
            normal.Normalize();
            var center = new Vector3(bounds.center.x, 0f, bounds.center.z);
            // 0.2.2x8: first a foot where the climb starts walking in, then
            // one where it needs a jump.
            for (var pass = 0; pass < 2; pass++)
            for (var side = 1; side >= -1; side -= 2)
            {
                var face = normal * side;
                foreach (var footDistance in FootDistances)
                for (var footHeight = bounds.min.y + 1.5f; footHeight <= Mathf.Max(bounds.min.y + 1.5f, bounds.max.y - 1.5f); footHeight += 1f)
                {
                    var foot = center + face * footDistance;
                    foot.y = footHeight;
                    if (!Physics.Raycast(foot, Vector3.down, out var groundHit, 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                        || groundHit.point.y < bounds.min.y - 1f)
                    {
                        continue;
                    }
                    var bottom = groundHit.point;
                    if (IsCapsuleBlocked(bottom, pieces))
                    {
                        reason = "pe bloqueado";
                        continue;
                    }
                    var top = Vector3.zero;
                    var hasTop = false;
                    foreach (var exitDistance in ExitDistances)
                    {
                        var exit = center - face * exitDistance;
                        exit.y = bounds.max.y + 1.5f;
                        if (Physics.Raycast(exit, Vector3.down, out var topHit, 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                            && topHit.point.y >= bottom.y + 1f
                            && !IsCapsuleBlocked(topHit.point, pieces))
                        {
                            top = topHit.point;
                            hasTop = true;
                            break;
                        }
                    }
                    if (!hasTop)
                    {
                        reason = "sem topo livre";
                        continue;
                    }
                    // 0.2.2x8 (doc 16.16): the side and the foot used to be
                    // taken without the game's check. At (-944, -157) the
                    // waist ray hit the ladder's own frame (tag Medium) and at
                    // the bridge (-289, 100) the bridge; a guard tower ladder
                    // that starts above the waist was walked under for 25m.
                    if (!TryFindClimbStand(bottom, face, pass == 1, out var stand, out var lift, out var gap, out var why))
                    {
                        reason = "o jogo nao aceita a subida por esse lado (" + why + ")";
                        continue;
                    }
                    var toLadder = -face;
                    var colliders = new Collider[pieces.Count];
                    for (var i = 0; i < pieces.Count; i++)
                    {
                        colliders[i] = pieces[i].Collider;
                    }
                    ladder = new BotLadder
                    {
                        Name = name,
                        Bottom = stand,
                        Top = top,
                        FacingYaw = Mathf.Atan2(toLadder.x, toLadder.z) * Mathf.Rad2Deg,
                        Center = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z),
                        Face = face,
                        JumpToGrab = lift > 0f,
                        StandGap = gap,
                        Colliders = colliders,
                        LipHeight = MeasureLip(center, face, top, pieces)
                    };
                    return true;
                }
            }
            return false;
        }

        // The game's climb start (PlayerStance.simulate): a ray from the
        // waist (feet + 0.5m) along the body's forward hits, within 0.75m, a
        // collider tagged Ladder on its face (|normal . up| > 0.9); the body
        // then moves to the ladder's line + normal * 0.5 at the hit's height
        // - 0.5m, and neither that move (capsule cast) nor the new place
        // (capsule) may touch BLOCK_LADDER. Walked in from the foot 10cm at
        // a time, standing (lift 0) or at the top of a jump.
        private static readonly float[] s_ClimbLifts = { 0f, 0.4f, 0.8f };
        private static readonly float[] s_ClimbLaterals = { 0f, 0.25f, -0.25f };

        private static bool TryFindClimbStand(Vector3 bottom, Vector3 face, bool allowJump, out Vector3 stand, out float lift, out float gap, out string why)
        {
            stand = bottom;
            lift = 0f;
            gap = 0f;
            why = "raio da cintura nao acerta a escada";
            var forward = -face;
            var across = Vector3.Cross(Vector3.up, forward).normalized;
            // The middle first, then a hand to each side (a frame in the
            // middle, a rail on one side).
            foreach (var lateral in s_ClimbLaterals)
            for (var step = 0f; step <= 1.6f; step += 0.1f)
            {
                var probe = bottom + forward * step + across * lateral;
                if (!Physics.Raycast(probe + Vector3.up * 1f, Vector3.down, out var ground, 2.5f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                    || Mathf.Abs(ground.point.y - bottom.y) > 0.6f)
                {
                    why = "chao acaba antes da escada";
                    break;
                }
                var feet = ground.point;
                if ((step > 0f || lateral != 0f) && IsBodyBlocked(feet))
                {
                    // The body can't get any closer (or can't stand here).
                    break;
                }
                foreach (var candidateLift in s_ClimbLifts)
                {
                    if (candidateLift > 0f && !allowJump)
                    {
                        break;
                    }
                    var body = feet + Vector3.up * candidateLift;
                    if (!Physics.Raycast(new Ray(body + Vector3.up * 0.5f, forward), out var hit, 0.75f, RayMasks.LADDER_INTERACT))
                    {
                        continue;
                    }
                    var collider = hit.collider;
                    if (collider == null || !collider.transform.CompareTag("Ladder"))
                    {
                        why = "o raio da cintura acerta " + (collider == null ? "?" : collider.name + " tag " + collider.tag) + " antes da escada";
                        continue;
                    }
                    if (Mathf.Abs(Vector3.Dot(hit.normal, collider.transform.up)) <= 0.9f)
                    {
                        why = "acerta a escada de lado";
                        continue;
                    }
                    var ladderLine = collider.transform.position;
                    var target = new Vector3(ladderLine.x, hit.point.y - 0.5f, ladderLine.z) + hit.normal * 0.5f;
                    var move = target - body;
                    var radius = PlayerStance.RADIUS;
                    if (Physics.CapsuleCast(
                            body + new Vector3(0f, radius, 0f),
                            body + new Vector3(0f, PlayerMovement.HEIGHT_STAND - radius, 0f),
                            radius,
                            move.normalized,
                            move.magnitude,
                            RayMasks.BLOCK_LADDER,
                            QueryTriggerInteraction.Ignore))
                    {
                        why = "o corpo bate em algo indo para a escada";
                        continue;
                    }
                    if (Physics.CheckCapsule(
                            target + new Vector3(0f, radius, 0f),
                            target + new Vector3(0f, PlayerMovement.HEIGHT_STAND - radius, 0f),
                            radius,
                            RayMasks.BLOCK_LADDER,
                            QueryTriggerInteraction.Ignore))
                    {
                        why = "sem espaco para o corpo na escada";
                        continue;
                    }
                    stand = feet;
                    lift = candidateLift;
                    gap = hit.distance;
                    return true;
                }
            }
            return false;
        }

        // A standing body here touches something solid (the ladder's own
        // frame counts: the game's walk does not go through it).
        private static bool IsBodyBlocked(Vector3 feet)
        {
            var count = Physics.OverlapCapsuleNonAlloc(
                feet + Vector3.up * (PlayerRadius + 0.1f),
                feet + Vector3.up * 1.6f,
                PlayerRadius * 0.8f,
                s_Overlap,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                if (!s_Overlap[i].CompareTag("Ladder"))
                {
                    return true;
                }
            }
            return false;
        }

        // 0.2.2x10: rays down just behind the ladder on the top side; the
        // highest thing that is not the ladder, over the top floor.
        private static readonly RaycastHit[] s_LipHits = new RaycastHit[16];

        private static float MeasureLip(Vector3 center, Vector3 face, Vector3 top, List<LadderPiece> pieces)
        {
            var lip = 0f;
            foreach (var back in s_LipBacks)
            {
                var origin = center - face * back;
                origin.y = top.y + 2.2f;
                var count = Physics.RaycastNonAlloc(origin, Vector3.down, s_LipHits, 2.5f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
                for (var i = 0; i < count; i++)
                {
                    var hit = s_LipHits[i];
                    if (hit.collider == null || hit.collider.CompareTag("Ladder") || IsOwnPiece(hit.collider, pieces))
                    {
                        continue;
                    }
                    lip = Mathf.Max(lip, hit.point.y - top.y);
                }
            }
            return Mathf.Clamp(lip, 0f, 2.2f);
        }

        private static readonly float[] s_LipBacks = { 0.15f, 0.35f, 0.55f };

        private static bool IsOwnPiece(Collider collider, List<LadderPiece> pieces)
        {
            foreach (var piece in pieces)
            {
                if (collider == piece.Collider || collider.transform.IsChildOf(piece.Owner))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsCapsuleBlocked(Vector3 feet, List<LadderPiece> pieces)
        {
            var count = Physics.OverlapCapsuleNonAlloc(
                feet + Vector3.up * (PlayerRadius + 0.1f),
                feet + Vector3.up * 1.6f,
                PlayerRadius * 0.8f,
                s_Overlap,
                RayMasks.BLOCK_COLLISION,
                QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var hit = s_Overlap[i];
                if (hit.CompareTag("Ladder"))
                {
                    continue;
                }
                var own = false;
                foreach (var piece in pieces)
                {
                    if (hit.transform.IsChildOf(piece.Owner))
                    {
                        own = true;
                        break;
                    }
                }
                if (!own)
                {
                    return true;
                }
            }
            return false;
        }

        // A ladder whose foot is in the given region of the navmesh and whose
        // top is in another one: the way out of a pit.
        // allowSameArea: the mesh links the pit to the floor above with a
        // step too high to climb, so both are one region; a ladder whose top
        // is clearly above the bot still gets it out.
        public static BotLadder? FindExit(Vector3 position, uint area, float radius, Func<BotLadder, bool> allowed, bool allowSameArea = false)
        {
            if (AstarPath.active == null)
            {
                return null;
            }
            BotLadder? best = null;
            var bestDistance = radius;
            foreach (var ladder in s_Ladders)
            {
                var distance = (ladder.Bottom - position).GetHorizontalMagnitude();
                if (distance >= bestDistance || !allowed(ladder))
                {
                    continue;
                }
                var bottomNode = AstarPath.active.GetNearest(ladder.Bottom, NNConstraint.Walkable).node;
                var topNode = AstarPath.active.GetNearest(ladder.Top, NNConstraint.Walkable).node;
                // A pit floor off the mesh snaps to the mesh above it; then a
                // foot at the bot's own height is enough.
                var footInPit = bottomNode != null && bottomNode.Area == area;
                var footAtBotLevel = Mathf.Abs(ladder.Bottom.y - position.y) <= 1f && ladder.Top.y > position.y + 1f;
                var sameArea = footInPit && topNode != null && topNode.Area == area;
                if (allowSameArea)
                {
                    if (!footAtBotLevel || ladder.Top.y < position.y + 1.2f)
                    {
                        continue;
                    }
                }
                else if (!(footInPit || footAtBotLevel) || sameArea)
                {
                    continue;
                }
                best = ladder;
                bestDistance = distance;
            }
            return best;
        }
    }
}
