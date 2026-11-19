using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;

namespace SkaliaDeps
{
    // 0.2.2: the mesh only walks (steps of 0.4m, 2m of head room). A player
    // also jumps up a ledge (0.55-0.95m: the jump lifts the feet 0.83m,
    // 7 m/s against 3x gravity, and the step takes the rest) and crouches
    // through a low passage (1.3-1.95m). Both are added as two-way links of
    // the mesh, found on its edges round the bots only (16m cells, one every
    // half second per bot), so the whole-map bake stays as it is. A link a
    // bot fails to cross is removed for every bot.
    internal static partial class SkaliaBotUnityNavmesh
    {
        public enum ShortcutKind
        {
            None,
            Jump,
            Crouch
        }

        private sealed class ShortcutLink
        {
            public int Id;
            public ShortcutKind Kind;
            public Vector3 Start;
            public Vector3 End;
            public NavMeshLinkInstance Instance;
        }

        private const float ShortcutCellMeters = 16f;
        private const float ShortcutSampleStep = 2f;
        private const float ShortcutGridMeters = 2f;
        private const int MaxShortcuts = 6000;
        private const int MaxShortcutPathChecks = 16;
        private const float MinJumpRise = 0.55f;
        private const float MaxJumpRise = 0.95f;
        private const float CrouchMinClear = 1.3f;
        private const float StandClear = 1.95f;
        private const float JumpLinkCost = 3f;
        private const float CrouchLinkCost = 2.5f;

        private static readonly Dictionary<int, ShortcutLink> s_Shortcuts = new Dictionary<int, ShortcutLink>();
        private static readonly Dictionary<long, List<ShortcutLink>> s_ShortcutGrid = new Dictionary<long, List<ShortcutLink>>();
        private static readonly HashSet<long> s_ShortcutCellsDone = new HashSet<long>();
        private static readonly HashSet<long> s_ShortcutEdgesSeen = new HashSet<long>();
        private static NavMeshPath? s_ShortcutPath;
        private static int s_NextShortcutId;
        private static int s_ShortcutJumps, s_ShortcutCrouches, s_ShortcutRemoved, s_ShortcutCells;
        private static float s_NextShortcutLogAt;

        public static int ShortcutCount => s_Shortcuts.Count;

        private static void ClearShortcuts()
        {
            // The link instances themselves are in s_Links and go with them.
            s_Shortcuts.Clear();
            s_ShortcutGrid.Clear();
            s_ShortcutCellsDone.Clear();
            s_ShortcutJumps = 0;
            s_ShortcutCrouches = 0;
            s_ShortcutRemoved = 0;
            s_ShortcutCells = 0;
        }

        private static long GridKey(float x, float z)
        {
            var gx = Mathf.FloorToInt(x / ShortcutGridMeters);
            var gz = Mathf.FloorToInt(z / ShortcutGridMeters);
            return ((long)gx << 32) ^ (uint)gz;
        }

        private static long CellKey(int cx, int cz, int band)
        {
            return ((long)(cx & 0xFFFFF) << 40) | ((long)(cz & 0xFFFFF) << 20) | (uint)(band & 0xFFFFF);
        }

        // The bot's own 16m cell first, then the eight round it; one cell a call.
        public static void ScanShortcutsNear(Vector3 position)
        {
            var settings = SkaliaBotIdleSimulation.s_Settings;
            if (!MapFilled
                || s_Shortcuts.Count >= MaxShortcuts
                || s_Links.Count >= MaxLinks)
            {
                return;
            }
            var cx = Mathf.FloorToInt(position.x / ShortcutCellMeters);
            var cz = Mathf.FloorToInt(position.z / ShortcutCellMeters);
            var band = Mathf.FloorToInt(position.y / 4f);
            for (var ring = 0; ring < 10; ring++)
            {
                var nx = cx + (ring == 0 ? 0 : (ring - 1) % 3 - 1);
                var nz = cz + (ring == 0 ? 0 : (ring - 1) / 3 - 1);
                if (ring == 5)
                {
                    // (0, 0) again in the ring order.
                    continue;
                }
                var key = CellKey(nx, nz, band);
                if (!s_ShortcutCellsDone.Add(key))
                {
                    continue;
                }
                ScanShortcutCell(nx, nz, position.y, settings.JumpLinks, settings.CrouchLinks);
                s_ShortcutCells++;
                if (s_Logger != null && Time.realtimeSinceStartup >= s_NextShortcutLogAt)
                {
                    s_NextShortcutLogAt = Time.realtimeSinceStartup + 120f;
                    s_Logger.LogInformation(
                        "Malha nova: atalhos perto dos bots: {Jumps} de pulo, {Crouches} agachado; {Cells} pedacos de 16m olhados, {Removed} removidos por falha.",
                        s_ShortcutJumps,
                        s_ShortcutCrouches,
                        s_ShortcutCells,
                        s_ShortcutRemoved);
                }
                return;
            }
        }

        private static void ScanShortcutCell(int cx, int cz, float level, bool jumps, bool crouches)
        {
            s_ShortcutEdgesSeen.Clear();
            var checks = 0;
            var steps = Mathf.RoundToInt(ShortcutCellMeters / ShortcutSampleStep);
            for (var gx = 0; gx < steps; gx++)
            {
                for (var gz = 0; gz < steps; gz++)
                {
                    var x = cx * ShortcutCellMeters + (gx + 0.5f) * ShortcutSampleStep;
                    var z = cz * ShortcutCellMeters + (gz + 0.5f) * ShortcutSampleStep;
                    if (!NavMesh.SamplePosition(new Vector3(x, level, z), out var sample, 2f, s_Filter)
                        || !NavMesh.FindClosestEdge(sample.position, out var edge, s_Filter)
                        || edge.distance > ShortcutSampleStep * 1.25f)
                    {
                        continue;
                    }
                    var foot = edge.position;
                    var edgeKey = GridKey(foot.x * 2f, foot.z * 2f) ^ ((long)Mathf.FloorToInt(foot.y) << 52);
                    if (!s_ShortcutEdgesSeen.Add(edgeKey))
                    {
                        continue;
                    }
                    var outward = -edge.normal;
                    outward.y = 0f;
                    if (outward.sqrMagnitude < 0.25f
                        || WaterUtility.isPointUnderwater(foot + Vector3.up * 0.5f))
                    {
                        continue;
                    }
                    outward.Normalize();
                    ShortcutKind kind;
                    Vector3 end;
                    if (jumps && TryFindJumpUp(foot, outward, out end))
                    {
                        kind = ShortcutKind.Jump;
                    }
                    else if (crouches && TryFindCrouchPassage(foot, outward, out end))
                    {
                        kind = ShortcutKind.Crouch;
                    }
                    else
                    {
                        continue;
                    }
                    if (HasShortcutNear(foot, end)
                        || checks >= MaxShortcutPathChecks)
                    {
                        continue;
                    }
                    checks++;
                    if (IsWalkedAlready(foot, end))
                    {
                        continue;
                    }
                    AddShortcut(kind, foot, end);
                    if (s_Shortcuts.Count >= MaxShortcuts || s_Links.Count >= MaxLinks)
                    {
                        return;
                    }
                }
            }
        }

        // A ledge face right ahead, its top 0.55-0.95m up with floor of the
        // mesh on it, head room for the jump and nothing at the chest.
        private static bool TryFindJumpUp(Vector3 foot, Vector3 outward, out Vector3 end)
        {
            end = foot;
            var mask = RayMasks.BLOCK_COLLISION & ~MovingMask;
            if (!Physics.Raycast(foot + Vector3.up * 0.25f, outward, out var face, 1f, mask, QueryTriggerInteraction.Ignore)
                || Mathf.Abs(face.normal.y) > 0.3f
                || IsDoor(face.collider))
            {
                return false;
            }
            // A table or a crate is walked around, never climbed on.
            if (SkaliaBotObjectMap.TryGetByCollider(face.collider, out var mapObject)
                && SkaliaBotObjectMap.BlocksBody(mapObject))
            {
                return false;
            }
            var over = foot + outward * (face.distance + 0.5f);
            if (!Physics.Raycast(
                    over + Vector3.up * (MaxJumpRise + 0.5f),
                    Vector3.down,
                    out var top,
                    MaxJumpRise + 0.3f,
                    mask,
                    QueryTriggerInteraction.Ignore)
                || top.normal.y < 0.8f)
            {
                return false;
            }
            // 0.2.2x11: nor jumped on from the side where the face is
            // another collider (thigas onto a bed, then no way down).
            if (SkaliaBotObjectMap.TryGetByCollider(top.collider, out var topObject)
                && SkaliaBotObjectMap.BlocksBody(topObject))
            {
                return false;
            }
            var rise = top.point.y - foot.y;
            if (rise < MinJumpRise || rise > MaxJumpRise)
            {
                return false;
            }
            if (Physics.Raycast(foot + Vector3.up * 0.3f, Vector3.up, 2.9f, mask, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(foot + Vector3.up * (rise + 0.45f), outward, face.distance + 1f, mask, QueryTriggerInteraction.Ignore)
                || Physics.Raycast(foot + Vector3.up * (rise + 1.5f), outward, face.distance + 1f, mask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            var land = new Vector3(foot.x, top.point.y, foot.z) + outward * (face.distance + 0.9f);
            if (!NavMesh.SamplePosition(land, out var landing, 0.7f, s_Filter)
                || Mathf.Abs(landing.position.y - top.point.y) > 0.3f
                || (landing.position - foot).GetHorizontalMagnitude() > 2.2f
                || Physics.CheckCapsule(
                    landing.position + Vector3.up * 0.45f,
                    landing.position + Vector3.up * 1.6f,
                    0.35f,
                    mask,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            end = landing.position;
            return true;
        }

        // Floor going on at the same height under a ceiling 1.3-1.95m up,
        // then standing room and mesh again within 4m; the crouched body
        // (1.2m) fits the whole way.
        private static bool TryFindCrouchPassage(Vector3 foot, Vector3 outward, out Vector3 end)
        {
            end = foot;
            var mask = RayMasks.BLOCK_COLLISION & ~MovingMask;
            var lowSeen = false;
            var length = 0f;
            var found = false;
            for (var along = 0.5f; along <= 4f; along += 0.5f)
            {
                var column = foot + outward * along;
                if (!Physics.Raycast(column + Vector3.up * 0.6f, Vector3.down, out var floor, 1.2f, mask, QueryTriggerInteraction.Ignore)
                    || floor.normal.y < 0.7f
                    || Mathf.Abs(floor.point.y - foot.y) > 0.35f)
                {
                    return false;
                }
                var clear = Physics.Raycast(floor.point + Vector3.up * 0.05f, Vector3.up, out var ceiling, 2f, mask, QueryTriggerInteraction.Ignore)
                    ? ceiling.distance + 0.05f
                    : float.PositiveInfinity;
                if (clear < CrouchMinClear)
                {
                    return false;
                }
                if (clear < StandClear)
                {
                    lowSeen = true;
                    continue;
                }
                if (!lowSeen)
                {
                    if (along >= 1f)
                    {
                        // No low ceiling at the mesh's edge: not a passage.
                        return false;
                    }
                    continue;
                }
                // Standing room again; the mesh starts a body's radius on.
                if (NavMesh.SamplePosition(floor.point, out var exit, 0.5f, s_Filter)
                    && Mathf.Abs(exit.position.y - floor.point.y) < 0.3f
                    && (exit.position - foot).GetHorizontalMagnitude() >= 1f)
                {
                    end = exit.position;
                    length = along;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                return false;
            }
            // 0.2.2u: the player's real radius (0.4m; 0.36 let a link through
            // a gap in the Research wall at Scorpion-7 nobody fits in).
            if (Physics.CapsuleCast(
                    foot + Vector3.up * 0.5f,
                    foot + Vector3.up * 0.8f,
                    PlayerStance.RADIUS,
                    outward,
                    out var hit,
                    length,
                    mask,
                    QueryTriggerInteraction.Ignore)
                && !IsDoor(hit.collider)
                && !IsStepHit(foot, outward, hit))
            {
                return false;
            }
            return !WaterUtility.isPointUnderwater(end + Vector3.up * 0.5f);
        }

        // Stairs or a ramp right there already: no link needed.
        private static bool IsWalkedAlready(Vector3 start, Vector3 end)
        {
            s_ShortcutPath ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(start, end, s_Filter, s_ShortcutPath)
                || s_ShortcutPath.status != NavMeshPathStatus.PathComplete)
            {
                return false;
            }
            var corners = s_ShortcutPath.corners;
            var length = 0f;
            for (var index = 1; index < corners.Length; index++)
            {
                length += (corners[index] - corners[index - 1]).magnitude;
            }
            return length <= (end - start).magnitude + 8f;
        }

        private static bool HasShortcutNear(Vector3 start, Vector3 end)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!s_ShortcutGrid.TryGetValue(GridKey(start.x + dx * ShortcutGridMeters, start.z + dz * ShortcutGridMeters), out var list))
                    {
                        continue;
                    }
                    foreach (var link in list)
                    {
                        if (((link.Start - start).sqrMagnitude < 4f && (link.End - end).sqrMagnitude < 6.25f)
                            || ((link.End - start).sqrMagnitude < 4f && (link.Start - end).sqrMagnitude < 6.25f))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static void AddShortcut(ShortcutKind kind, Vector3 start, Vector3 end)
        {
            var instance = NavMesh.AddLink(new NavMeshLinkData
            {
                startPosition = start,
                endPosition = end,
                width = 0f,
                costModifier = kind == ShortcutKind.Jump ? JumpLinkCost : CrouchLinkCost,
                bidirectional = true,
                area = 0,
                agentTypeID = s_Settings.agentTypeID
            });
            if (!instance.valid)
            {
                return;
            }
            s_Links.Add(instance);
            var link = new ShortcutLink { Id = ++s_NextShortcutId, Kind = kind, Start = start, End = end, Instance = instance };
            s_Shortcuts[link.Id] = link;
            AddToShortcutGrid(GridKey(start.x, start.z), link);
            var endKey = GridKey(end.x, end.z);
            if (endKey != GridKey(start.x, start.z))
            {
                AddToShortcutGrid(endKey, link);
            }
            if (kind == ShortcutKind.Jump)
            {
                s_ShortcutJumps++;
            }
            else
            {
                s_ShortcutCrouches++;
            }
        }

        private static void AddToShortcutGrid(long key, ShortcutLink link)
        {
            if (!s_ShortcutGrid.TryGetValue(key, out var list))
            {
                list = new List<ShortcutLink>();
                s_ShortcutGrid[key] = list;
            }
            list.Add(link);
        }

        public static void RemoveShortcut(int id)
        {
            if (!s_Shortcuts.TryGetValue(id, out var link))
            {
                return;
            }
            s_Shortcuts.Remove(id);
            NavMesh.RemoveLink(link.Instance);
            s_Links.Remove(link.Instance);
            if (s_ShortcutGrid.TryGetValue(GridKey(link.Start.x, link.Start.z), out var startList))
            {
                startList.Remove(link);
            }
            if (s_ShortcutGrid.TryGetValue(GridKey(link.End.x, link.End.z), out var endList))
            {
                endList.Remove(link);
            }
            s_ShortcutRemoved++;
            if (link.Kind == ShortcutKind.Jump)
            {
                s_ShortcutJumps--;
            }
            else
            {
                s_ShortcutCrouches--;
            }
        }

        // The shortcut whose ends are `a` (within `toleranceA`) and `b`
        // (within 0.7m), either way round; `from` is the end at `a`.
        public static bool TryGetShortcutLeg(Vector3 a, Vector3 b, float toleranceA, out int id, out ShortcutKind kind, out Vector3 from, out Vector3 to)
        {
            id = -1;
            kind = ShortcutKind.None;
            from = a;
            to = b;
            if (s_Shortcuts.Count == 0)
            {
                return false;
            }
            var toleranceASquared = toleranceA * toleranceA;
            const float toleranceBSquared = 0.49f;
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!s_ShortcutGrid.TryGetValue(GridKey(b.x + dx * ShortcutGridMeters, b.z + dz * ShortcutGridMeters), out var list))
                    {
                        continue;
                    }
                    foreach (var link in list)
                    {
                        if ((link.Start - a).sqrMagnitude <= toleranceASquared && (link.End - b).sqrMagnitude <= toleranceBSquared)
                        {
                            id = link.Id;
                            kind = link.Kind;
                            from = link.Start;
                            to = link.End;
                            return true;
                        }
                        if ((link.End - a).sqrMagnitude <= toleranceASquared && (link.Start - b).sqrMagnitude <= toleranceBSquared)
                        {
                            id = link.Id;
                            kind = link.Kind;
                            from = link.End;
                            to = link.Start;
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static bool IsShortcutEnd(Vector3 point)
        {
            if (s_Shortcuts.Count == 0)
            {
                return false;
            }
            for (var dx = -1; dx <= 1; dx++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (!s_ShortcutGrid.TryGetValue(GridKey(point.x + dx * ShortcutGridMeters, point.z + dz * ShortcutGridMeters), out var list))
                    {
                        continue;
                    }
                    foreach (var link in list)
                    {
                        if ((link.Start - point).sqrMagnitude < 0.25f || (link.End - point).sqrMagnitude < 0.25f)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }
}
