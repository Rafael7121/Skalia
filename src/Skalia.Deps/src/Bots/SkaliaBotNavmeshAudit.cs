using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // Checks every navmesh triangle against the colliders the player walks
    // on. The bot circled for 20 min in a pit 2m under the mesh next to the
    // train cars; this measures where the mesh and the physical floor
    // disagree before deciding what to block or link.
    internal static class SkaliaBotNavmeshAudit
    {
        private const double FrameBudgetMilliseconds = 3.0;
        private const float FalseFloorDrop = 1.2f;
        private const float ClusterCell = 16f;

        private static bool s_Started;
        private static string? s_Map;
        private static readonly HashSet<GraphNode> s_FalseFloor = new HashSet<GraphNode>();
        private static readonly HashSet<GraphNode> s_Obstructed = new HashSet<GraphNode>();
        // Triangles next to a crossing the body cannot make (a fence, a
        // fixed door, a wall or boulder the mesh runs through). Routes pay
        // a large penalty for them.
        private static readonly HashSet<GraphNode> s_BodyBlocked = new HashSet<GraphNode>();
        public static bool BodyAuditReady { get; private set; }
        public static bool IsBodyBlocked(GraphNode node) => BodyAuditReady && s_BodyBlocked.Contains(node);

        public static bool Ready { get; private set; }

        public static bool IsFalseFloor(GraphNode node) => Ready && s_FalseFloor.Contains(node);

        public static void EnsureStarted(ILogger logger)
        {
            var map = Level.info?.name;
            if (map == null || (s_Started && s_Map == map))
            {
                return;
            }
            s_Started = true;
            s_Map = map;
            Ready = false;
            RunAsync(logger).Forget();
        }

        private static async UniTaskVoid RunAsync(ILogger logger)
        {
            try
            {
                await UniTask.SwitchToMainThread();
                while (AstarPath.active == null || AstarPath.active.data?.graphs == null)
                {
                    await UniTask.Delay(1000);
                }
                var watch = Stopwatch.StartNew();
                var slice = Stopwatch.StartNew();
                var nodes = new List<GraphNode>();
                foreach (var graph in AstarPath.active.data.graphs)
                {
                    if (graph is NavmeshBase navmesh)
                    {
                        navmesh.GetNodes(node => nodes.Add(node));
                    }
                }
                var falseArea = 0f;
                var obstructedArea = 0f;
                var totalArea = 0f;
                var falseCells = new Dictionary<long, float>();
                var obstructedCells = new Dictionary<long, float>();
                var falseSamples = new Dictionary<long, Vector3>();
                var obstructedSamples = new Dictionary<long, Vector3>();
                var mask = RayMasks.BLOCK_COLLISION;
                foreach (var node in nodes)
                {
                    if (!(node is TriangleMeshNode triangle) || !node.Walkable)
                    {
                        continue;
                    }
                    var a = (Vector3)triangle.GetVertex(0);
                    var b = (Vector3)triangle.GetVertex(1);
                    var c = (Vector3)triangle.GetVertex(2);
                    var area = Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
                    totalArea += area;
                    var center = (a + b + c) / 3f;
                    var key = ((long)Mathf.FloorToInt(center.x / ClusterCell) << 32)
                        ^ (uint)Mathf.FloorToInt(center.z / ClusterCell);
                    var grounded = Physics.Raycast(
                        center + Vector3.up * 1.6f,
                        Vector3.down,
                        out var hit,
                        1.6f + FalseFloorDrop + 4f,
                        mask,
                        QueryTriggerInteraction.Ignore);
                    if (!grounded || hit.point.y < center.y - FalseFloorDrop)
                    {
                        s_FalseFloor.Add(node);
                        falseArea += area;
                        falseCells.TryGetValue(key, out var sum);
                        falseCells[key] = sum + area;
                        falseSamples[key] = center;
                    }
                    else if (hit.point.y > center.y + 0.8f)
                    {
                        // Something solid stands on the mesh between its
                        // surface and head height.
                        s_Obstructed.Add(node);
                        obstructedArea += area;
                        obstructedCells.TryGetValue(key, out var sum);
                        obstructedCells[key] = sum + area;
                        obstructedSamples[key] = center;
                    }
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        slice.Restart();
                    }
                }
                Ready = true;
                logger.LogInformation(
                    "Auditoria da malha: {Count} triangulos ({Area:0} m2) em {Seconds:0.0}s; chao falso (chao fisico mais de {Drop:0.#}m abaixo) {False} ({FalseArea:0} m2, {FalsePct:0.0}%); obstruidos {Obstructed} ({ObstructedArea:0} m2).",
                    nodes.Count,
                    totalArea,
                    watch.Elapsed.TotalSeconds,
                    FalseFloorDrop,
                    s_FalseFloor.Count,
                    falseArea,
                    totalArea > 0f ? falseArea / totalArea * 100f : 0f,
                    s_Obstructed.Count,
                    obstructedArea);
                logger.LogInformation("Auditoria da malha: piores pontos de chao falso: {Cells}.", Top(falseCells, falseSamples));
                logger.LogInformation("Auditoria da malha: piores pontos obstruidos: {Cells}.", Top(obstructedCells, obstructedSamples));
                LogInspection(logger, "chao falso", falseCells, falseSamples);
                LogInspection(logger, "obstruido", obstructedCells, obstructedSamples);
                while (!SkaliaBotObjectMap.Ready)
                {
                    await UniTask.Delay(1000);
                }
                await AuditCrossingsAsync(logger, nodes);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Auditoria da malha falhou.");
            }
        }

        // Knee and just above jump height: the first audit asked for chest
        // height too and missed every wooden fence (about 1.2m tall).
        private static readonly float[] BodyHeights = { 0.5f, 1.1f };

        // Every pair of neighbouring triangles: can a standing body go from
        // one center through the shared edge to the other? Both the knee
        // and the chest ray must hit something that is not floor.
        private static async UniTask AuditCrossingsAsync(ILogger logger, List<GraphNode> nodes)
        {
            var watch = Stopwatch.StartNew();
            var slice = Stopwatch.StartNew();
            var mask = RayMasks.BLOCK_COLLISION;
            var pairs = 0;
            var blockedPairs = 0;
            var blockedArea = 0f;
            var byName = new Dictionary<string, int>();
            var byCategory = new Dictionary<string, int>();
            var cells = new Dictionary<long, float>();
            var samples = new Dictionary<long, Vector3>();
            var neighbors = new List<GraphNode>();
            var insideTriangles = 0;
            foreach (var node in nodes)
            {
                if (!(node is TriangleMeshNode triangle) || !node.Walkable)
                {
                    continue;
                }
                var centerA = (Vector3)triangle.position;
                // A wall inside one triangle: on the top floor of an apartment
                // block in St. Petersburg the mesh ran through a wall and the
                // route aimed 1m into it 35 times; no neighbour pair saw it.
                if (TryFindWallInside(triangle, centerA, mask, out var insideWall))
                {
                    var insideName = "terreno";
                    var insideCategory = "terreno";
                    if (SkaliaBotObjectMap.TryGetByCollider(insideWall, out var insideObject))
                    {
                        insideName = insideObject.Name;
                        insideCategory = SkaliaBotObjectMap.CategoryName(insideObject.Category);
                    }
                    else if (!(insideWall is TerrainCollider))
                    {
                        insideName = insideWall.transform.root.name;
                        insideCategory = "sem catalogo";
                    }
                    if (!(insideObject != null
                          && insideObject.Category == BotObjectCategory.Door
                          && insideObject.Interactability != EObjectInteractability.NONE)
                        && s_BodyBlocked.Add(triangle))
                    {
                        insideTriangles++;
                        blockedArea += Area(triangle);
                        byName.TryGetValue(insideName, out var n0);
                        byName[insideName] = n0 + 1;
                        byCategory.TryGetValue(insideCategory, out var k0);
                        byCategory[insideCategory] = k0 + 1;
                    }
                }
                neighbors.Clear();
                node.GetConnections(neighbor => neighbors.Add(neighbor));
                foreach (var neighbor in neighbors)
                {
                    if (!(neighbor is TriangleMeshNode other) || !neighbor.Walkable || neighbor.NodeIndex < node.NodeIndex)
                    {
                        continue;
                    }
                    if (!TrySharedEdgeMiddle(triangle, other, out var middle))
                    {
                        continue;
                    }
                    pairs++;
                    var centerB = (Vector3)other.position;
                    Collider? blocker = null;
                    var blocked = true;
                    foreach (var height in BodyHeights)
                    {
                        var hitCollider = FirstWall(centerA, middle, height, mask) ?? FirstWall(middle, centerB, height, mask);
                        if (hitCollider == null)
                        {
                            blocked = false;
                            break;
                        }
                        blocker ??= hitCollider;
                    }
                    if (!blocked || blocker == null)
                    {
                        continue;
                    }
                    var name = "terreno";
                    var category = "terreno";
                    if (SkaliaBotObjectMap.TryGetByCollider(blocker, out var mapObject))
                    {
                        // A door that opens is handled by opening it.
                        if (mapObject.Category == BotObjectCategory.Door && mapObject.Interactability != EObjectInteractability.NONE)
                        {
                            continue;
                        }
                        name = mapObject.Name;
                        category = SkaliaBotObjectMap.CategoryName(mapObject.Category);
                    }
                    else if (!(blocker is TerrainCollider))
                    {
                        name = blocker.transform.root.name;
                        category = "sem catalogo";
                    }
                    blockedPairs++;
                    var smaller = Area(triangle) <= Area(other) ? (GraphNode)triangle : other;
                    if (s_BodyBlocked.Add(smaller))
                    {
                        blockedArea += Area((TriangleMeshNode)smaller);
                    }
                    byName.TryGetValue(name, out var n);
                    byName[name] = n + 1;
                    byCategory.TryGetValue(category, out var k);
                    byCategory[category] = k + 1;
                    var key = ((long)Mathf.FloorToInt(middle.x / ClusterCell) << 32) ^ (uint)Mathf.FloorToInt(middle.z / ClusterCell);
                    cells.TryGetValue(key, out var sum);
                    cells[key] = sum + 1f;
                    samples[key] = middle;
                }
                if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                {
                    await UniTask.Yield(PlayerLoopTiming.Update);
                    slice.Restart();
                }
            }
            BodyAuditReady = true;
            string TopOf(Dictionary<string, int> counts, int take)
            {
                var list = new List<KeyValuePair<string, int>>(counts);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                var text = new System.Text.StringBuilder();
                for (var i = 0; i < list.Count && i < take; i++)
                {
                    if (text.Length > 0) text.Append(", ");
                    text.Append(list[i].Key).Append(" x").Append(list[i].Value);
                }
                return text.Length > 0 ? text.ToString() : "nenhum";
            }
            logger.LogInformation(
                "Auditoria de passagens: {Pairs} passagens entre triangulos em {Seconds:0.0}s; {Blocked} o corpo nao atravessa, {Inside} triangulos com parede por dentro ({Nodes} triangulos, {Area:0} m2, evitados nas rotas). Por tipo: {Categories}.",
                pairs,
                watch.Elapsed.TotalSeconds,
                blockedPairs,
                insideTriangles,
                s_BodyBlocked.Count,
                blockedArea,
                TopOf(byCategory, 12));
            logger.LogInformation("Auditoria de passagens, objetos mais comuns: {Names}.", TopOf(byName, 25));
            logger.LogInformation("Auditoria de passagens, piores pontos: {Cells}.", Top(cells, samples).Replace(" m2", " passagens"));
        }

        // From the center to the middle of each edge at knee and above-jump
        // height: both blocked on some edge means a wall cuts the triangle.
        private static bool TryFindWallInside(TriangleMeshNode triangle, Vector3 center, int mask, out Collider wall)
        {
            wall = null!;
            for (var i = 0; i < 3; i++)
            {
                var edgeMiddle = ((Vector3)triangle.GetVertex(i) + (Vector3)triangle.GetVertex((i + 1) % 3)) * 0.5f;
                Collider? first = null;
                var blocked = true;
                foreach (var height in BodyHeights)
                {
                    var hit = FirstWall(center, edgeMiddle, height, mask);
                    if (hit == null)
                    {
                        blocked = false;
                        break;
                    }
                    first ??= hit;
                }
                if (blocked && first != null)
                {
                    wall = first;
                    return true;
                }
            }
            return false;
        }

        private static float Area(TriangleMeshNode t)
        {
            var a = (Vector3)t.GetVertex(0);
            var b = (Vector3)t.GetVertex(1);
            var c = (Vector3)t.GetVertex(2);
            return Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
        }

        private static bool TrySharedEdgeMiddle(TriangleMeshNode a, TriangleMeshNode b, out Vector3 middle)
        {
            middle = Vector3.zero;
            var found = 0;
            for (var i = 0; i < 3; i++)
            {
                var va = a.GetVertex(i);
                for (var j = 0; j < 3; j++)
                {
                    if (va == b.GetVertex(j))
                    {
                        middle += (Vector3)va;
                        found++;
                        break;
                    }
                }
            }
            if (found < 2)
            {
                return false;
            }
            middle /= found;
            return true;
        }

        // First collider a body-height ray from a to b hits that is not
        // walkable floor (slopes the ray clips count as ground).
        internal static Collider? FirstWall(Vector3 a, Vector3 b, float height, int mask)
        {
            var origin = a + Vector3.up * height;
            var target = b + Vector3.up * height;
            var direction = target - origin;
            var distance = direction.magnitude;
            if (distance < 0.05f)
            {
                return null;
            }
            direction /= distance;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (!Physics.Raycast(origin, direction, out var hit, distance, mask, QueryTriggerInteraction.Ignore))
                {
                    return null;
                }
                if (hit.normal.y < 0.6f && !hit.collider.CompareTag("Ladder"))
                {
                    return hit.collider;
                }
                var advance = hit.distance + 0.05f;
                origin += direction * advance;
                distance -= advance;
                if (distance <= 0.05f)
                {
                    return null;
                }
            }
            return null;
        }

        // What actually stands at the worst points: every collider a ray from
        // 3m above the mesh to 12m below crosses, on any layer, so a bridge,
        // roof or platform the walking mask ignores shows up by name.
        private static void LogInspection(
            ILogger logger,
            string kind,
            Dictionary<long, float> cells,
            Dictionary<long, Vector3> samples)
        {
            var list = new List<KeyValuePair<long, float>>(cells);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            for (var i = 0; i < list.Count && i < 10; i++)
            {
                var point = samples[list[i].Key];
                var hits = Physics.RaycastAll(
                    point + Vector3.up * 3f,
                    Vector3.down,
                    15f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Collide);
                Array.Sort(hits, (x, y) => y.point.y.CompareTo(x.point.y));
                var text = new System.Text.StringBuilder();
                foreach (var hit in hits)
                {
                    if (text.Length > 0)
                    {
                        text.Append(" | ");
                    }
                    var transform = hit.collider.transform;
                    var root = transform.root;
                    text.Append((hit.point.y - point.y).ToString("+0.0;-0.0")).Append("m ")
                        .Append(transform.name);
                    if (root != transform)
                    {
                        text.Append(" (").Append(root.name).Append(')');
                    }
                    text.Append(" camada ").Append(LayerMask.LayerToName(hit.collider.gameObject.layer))
                        .Append('/').Append(hit.collider.gameObject.layer);
                    if (hit.collider.isTrigger)
                    {
                        text.Append(" gatilho");
                    }
                }
                logger.LogInformation(
                    "Auditoria da malha: {Kind} #{Index} em ({X:0}, {Y:0.0}, {Z:0}) {Area:0} m2; colisores abaixo/acima da malha: {Hits}.",
                    kind,
                    i + 1,
                    point.x,
                    point.y,
                    point.z,
                    list[i].Value,
                    text.Length > 0 ? text.ToString() : "nenhum");
            }
        }

        private static string Top(Dictionary<long, float> cells, Dictionary<long, Vector3> samples)
        {
            var list = new List<KeyValuePair<long, float>>(cells);
            list.Sort((x, y) => y.Value.CompareTo(x.Value));
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < list.Count && i < 12; i++)
            {
                var point = samples[list[i].Key];
                if (text.Length > 0)
                {
                    text.Append("; ");
                }
                text.Append('(')
                    .Append(point.x.ToString("0")).Append(", ")
                    .Append(point.y.ToString("0")).Append(", ")
                    .Append(point.z.ToString("0")).Append(") ")
                    .Append(list[i].Value.ToString("0")).Append(" m2");
            }
            return text.Length > 0 ? text.ToString() : "nenhum";
        }
    }
}
