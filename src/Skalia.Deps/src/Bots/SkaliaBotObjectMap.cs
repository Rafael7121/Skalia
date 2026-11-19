using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    internal enum BotObjectCategory
    {
        Other,
        Building,
        Furniture,
        Door,
        Ladder,
        Rock,
        Wreck,
        Fence,
        Rubble,
        Tree,
        Obstacle
    }

    // One placed object of the map, as the bots know it: what it is, where
    // its colliders sit and how big they are.
    internal sealed class BotMapObject
    {
        public string Name = string.Empty;
        public BotObjectCategory Category;
        public EObjectType Type;
        public EObjectInteractability Interactability;
        public bool HasRubble;
        public Vector3 Position;
        public Bounds Bounds;
        public float Yaw;
        // Footprint in the object's own yaw frame, in meters: a counter
        // turned 45 degrees is a thin box, not the square around it.
        public Vector3 FrameOrigin;
        public Vector2 LocalMin;
        public Vector2 LocalMax;
        public float MinY;
        public float MaxY;
    }

    // Catalog of every object placed on the map, indexed on a 16m grid so a
    // bot can ask what stands around it. Step 1 only builds and reports it.
    internal static class SkaliaBotObjectMap
    {
        private const float CellSize = 16f;

        private static readonly string[] s_FurnitureWords =
        {
            "Oven", "Stove", "Counter", "Shelf", "Shelves", "Bed", "Table", "Chair", "Couch", "Sofa",
            "Fridge", "Desk", "Cabinet", "Wardrobe", "Dresser", "Dish_Washer", "Dishwasher", "Toilet",
            "Sink", "Bathtub", "Register", "Tickets", "Bench", "Locker", "Bookcase", "Washer", "Dryer",
            "Furniture", "Nightstand", "Piano", "Television", "TV_"
        };
        private static readonly string[] s_RockWords = { "Boulder", "Rock", "Cliff", "Stone" };
        private static readonly string[] s_WreckWords = { "Car", "Truck", "Bus", "Train", "Wreck", "Van", "Tank_Russia", "Sedan", "Jeep", "Tractor", "Wagon" };
        private static readonly string[] s_FenceWords = { "Fence", "Wall", "Barrier", "Railing", "Jersey", "Barricade", "Hesco", "Block_Road" };
        private static readonly string[] s_RubbleWords = { "Debris", "Rebar", "Bricks", "Rubble", "Pile", "Junk", "Trash" };
        private static readonly string[] s_TreeWords = { "Tree", "Bush", "Pine", "Birch" };
        private static readonly string[] s_ObstacleWords =
        {
            "Crate", "Barrel", "Pallet", "Dumpster", "HayBale", "Cooler", "Cardboard", "Box", "Garbage",
            "Generator", "Wheel", "Tire", "Sandbag", "Tank_Fuel", "Car_Lift", "Bollard", "Fire_Hydrant"
        };
        // Street furniture and flat pieces that only look big from above.
        private static readonly string[] s_PropWords = { "Light", "Lamp", "Power_Line", "Pole", "Sign", "Road_", "Crossing", "Grass" };

        private static bool s_Started;
        private static string? s_Map;
        private static readonly List<BotMapObject> s_Objects = new List<BotMapObject>();
        private static readonly Dictionary<long, List<BotMapObject>> s_Cells = new Dictionary<long, List<BotMapObject>>();
        private static readonly Dictionary<Collider, BotMapObject> s_ByCollider = new Dictionary<Collider, BotMapObject>();

        public static bool Ready { get; private set; }

        // Navmesh triangles lying on top of furniture: the mesh climbs onto
        // couches and counters, so routes went over a couch and the bot
        // circled it for 2 min. Routes pay a penalty to cross them, so they
        // go around unless the piece is where they end.
        private static volatile HashSet<GraphNode>? s_FurnitureNodes;
        private const double FrameBudgetMilliseconds = 3.0;

        public static bool FurnitureNodesReady => s_FurnitureNodes != null;

        public static bool IsOnFurniture(GraphNode node)
        {
            var nodes = s_FurnitureNodes;
            return nodes != null && nodes.Contains(node);
        }
        public static IReadOnlyList<BotMapObject> Objects => s_Objects;

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
            s_Objects.Clear();
            s_Cells.Clear();
            s_ByCollider.Clear();
            s_FurnitureNodes = null;
            try
            {
                Build(logger);
                Ready = true;
                MarkFurnitureNodesAsync(logger).Forget();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Catalogo de objetos do mapa falhou.");
            }
        }

        private static async UniTaskVoid MarkFurnitureNodesAsync(ILogger logger)
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
                var marked = new HashSet<GraphNode>();
                var area = 0f;
                var near = new List<BotMapObject>();
                var names = new Dictionary<string, int>();
                foreach (var node in nodes)
                {
                    if (!(node is TriangleMeshNode triangle) || !node.Walkable)
                    {
                        continue;
                    }
                    var a = (Vector3)triangle.GetVertex(0);
                    var b = (Vector3)triangle.GetVertex(1);
                    var c = (Vector3)triangle.GetVertex(2);
                    var center = (a + b + c) / 3f;
                    var reach = Mathf.Max(0.5f, (a - center).GetHorizontalMagnitude(), (b - center).GetHorizontalMagnitude(), (c - center).GetHorizontalMagnitude());
                    Query(center, reach, near);
                    foreach (var mapObject in near)
                    {
                        // Stable doors and similar are scenery, not doors: they
                        // never open, but the navmesh runs through them and a
                        // bot circled one at Chersky Farm until teleported.
                        if (IsFixedDoor(mapObject)
                            && center.y > mapObject.MinY - 0.6f
                            && center.y < mapObject.MinY + 1.2f
                            && (SegmentHitsFootprint(mapObject, a, b, 0f)
                                || SegmentHitsFootprint(mapObject, b, c, 0f)
                                || SegmentHitsFootprint(mapObject, c, a, 0f)
                                || FootprintDistance(mapObject, center) <= 0f))
                        {
                            marked.Add(node);
                            area += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
                            names.TryGetValue(mapObject.Name, out var doorCount);
                            names[mapObject.Name] = doorCount + 1;
                            break;
                        }
                        // On top of the piece: raised above its base and no
                        // higher than a step over its top.
                        if (!BlocksBody(mapObject)
                            || center.y < mapObject.MinY + 0.35f
                            || center.y > mapObject.MaxY + 0.6f
                            || FootprintDistance(mapObject, center) > 0.2f)
                        {
                            continue;
                        }
                        marked.Add(node);
                        area += Mathf.Abs((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z)) * 0.5f;
                        names.TryGetValue(mapObject.Name, out var count);
                        names[mapObject.Name] = count + 1;
                        break;
                    }
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        slice.Restart();
                    }
                }
                s_FurnitureNodes = marked;
                logger.LogInformation(
                    "Malha sobre moveis e obstaculos: {Count} triangulos ({Area:0} m2) de {Total} em {Seconds:0.0}s; rotas pagam penalidade para passar por eles. Mais comuns: {Top}.",
                    marked.Count,
                    area,
                    nodes.Count,
                    watch.Elapsed.TotalSeconds,
                    string.Join(", ", names.OrderByDescending(e => e.Value).Take(12).Select(e => $"{e.Key} x{e.Value}")));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Marcacao da malha sobre moveis falhou.");
            }
        }

        // Objects whose bounds come within radius of position, horizontally.
        public static void Query(Vector3 position, float radius, List<BotMapObject> results)
        {
            results.Clear();
            if (!Ready)
            {
                return;
            }
            var minX = CellOf(position.x - radius);
            var maxX = CellOf(position.x + radius);
            var minZ = CellOf(position.z - radius);
            var maxZ = CellOf(position.z + radius);
            for (var cx = minX; cx <= maxX; cx++)
            {
                for (var cz = minZ; cz <= maxZ; cz++)
                {
                    if (!s_Cells.TryGetValue(Key(cx, cz), out var cell))
                    {
                        continue;
                    }
                    foreach (var mapObject in cell)
                    {
                        if (results.Contains(mapObject))
                        {
                            continue;
                        }
                        var closest = mapObject.Bounds.ClosestPoint(new Vector3(position.x, mapObject.Bounds.center.y, position.z));
                        var dx = closest.x - position.x;
                        var dz = closest.z - position.z;
                        if (dx * dx + dz * dz <= radius * radius)
                        {
                            results.Add(mapObject);
                        }
                    }
                }
            }
        }

        // Furniture and loose obstacles: what a bot should walk around, not
        // hop onto. Inside a house the hop lands on the counter and circles.
        public static bool IsFixedDoor(BotMapObject mapObject)
        {
            return mapObject.Category == BotObjectCategory.Door
                && mapObject.Interactability == EObjectInteractability.NONE
                && mapObject.Name.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) < 0;
        }

        // What the bot walks around by a corner: furniture plus fences. The
        // mesh goes around a wooden fence correctly, but a bot following it
        // pressed against the posts and circled at Novosibirsk.
        public static bool DetourAround(BotMapObject mapObject)
        {
            return BlocksBody(mapObject) || mapObject.Category == BotObjectCategory.Fence;
        }

        public static bool BlocksBody(BotMapObject mapObject)
        {
            return mapObject.Category == BotObjectCategory.Furniture
                || mapObject.Category == BotObjectCategory.Obstacle;
        }

        public static bool TryGetByCollider(Collider? collider, out BotMapObject mapObject)
        {
            mapObject = null!;
            return Ready && collider != null && s_ByCollider.TryGetValue(collider, out mapObject);
        }

        // Whether the object stands in the body's way at feet height y.
        public static bool OverlapsBodyHeight(BotMapObject mapObject, float y)
        {
            return mapObject.MaxY > y + 0.4f && mapObject.MinY < y + 1.7f;
        }

        public static Vector2 ToLocal(BotMapObject mapObject, Vector3 point)
        {
            var offset = point - mapObject.FrameOrigin;
            var radians = -mapObject.Yaw * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            // Unity yaw turns +z toward +x.
            return new Vector2(offset.x * cos + offset.z * sin, -offset.x * sin + offset.z * cos);
        }

        public static Vector3 ToWorld(BotMapObject mapObject, Vector2 local, float y)
        {
            var radians = mapObject.Yaw * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            return new Vector3(
                mapObject.FrameOrigin.x + local.x * cos + local.y * sin,
                y,
                mapObject.FrameOrigin.z - local.x * sin + local.y * cos);
        }

        // Horizontal distance from point to the footprint; 0 inside it.
        public static float FootprintDistance(BotMapObject mapObject, Vector3 point)
        {
            var local = ToLocal(mapObject, point);
            var dx = Mathf.Max(mapObject.LocalMin.x - local.x, 0f, local.x - mapObject.LocalMax.x);
            var dz = Mathf.Max(mapObject.LocalMin.y - local.y, 0f, local.y - mapObject.LocalMax.y);
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Whether the horizontal segment a-b crosses the footprint grown by margin.
        public static bool SegmentHitsFootprint(BotMapObject mapObject, Vector3 a, Vector3 b, float margin)
        {
            var start = ToLocal(mapObject, a);
            var end = ToLocal(mapObject, b);
            var min = mapObject.LocalMin - new Vector2(margin, margin);
            var max = mapObject.LocalMax + new Vector2(margin, margin);
            var delta = end - start;
            var enter = 0f;
            var exit = 1f;
            for (var axis = 0; axis < 2; axis++)
            {
                var origin = start[axis];
                var step = delta[axis];
                if (Mathf.Abs(step) < 1e-5f)
                {
                    if (origin < min[axis] || origin > max[axis])
                    {
                        return false;
                    }
                    continue;
                }
                var t0 = (min[axis] - origin) / step;
                var t1 = (max[axis] - origin) / step;
                if (t0 > t1)
                {
                    var swap = t0;
                    t0 = t1;
                    t1 = swap;
                }
                enter = Mathf.Max(enter, t0);
                exit = Mathf.Min(exit, t1);
                if (enter > exit)
                {
                    return false;
                }
            }
            return true;
        }

        public static void GetFootprintCorners(BotMapObject mapObject, float margin, float y, Vector3[] corners)
        {
            var min = mapObject.LocalMin - new Vector2(margin, margin);
            var max = mapObject.LocalMax + new Vector2(margin, margin);
            corners[0] = ToWorld(mapObject, new Vector2(min.x, min.y), y);
            corners[1] = ToWorld(mapObject, new Vector2(max.x, min.y), y);
            corners[2] = ToWorld(mapObject, new Vector2(max.x, max.y), y);
            corners[3] = ToWorld(mapObject, new Vector2(min.x, max.y), y);
        }

        // Way out of the footprint for a body pressed against it: from the
        // nearest point of the box toward the bot, or out the closest side.
        public static Vector3 AwayDirection(BotMapObject mapObject, Vector3 point)
        {
            var local = ToLocal(mapObject, point);
            var clamped = new Vector2(
                Mathf.Clamp(local.x, mapObject.LocalMin.x, mapObject.LocalMax.x),
                Mathf.Clamp(local.y, mapObject.LocalMin.y, mapObject.LocalMax.y));
            var away = local - clamped;
            if (away.sqrMagnitude < 1e-4f)
            {
                var toMinX = local.x - mapObject.LocalMin.x;
                var toMaxX = mapObject.LocalMax.x - local.x;
                var toMinZ = local.y - mapObject.LocalMin.y;
                var toMaxZ = mapObject.LocalMax.y - local.y;
                var nearest = Mathf.Min(Mathf.Min(toMinX, toMaxX), Mathf.Min(toMinZ, toMaxZ));
                away = nearest == toMinX ? Vector2.left
                    : nearest == toMaxX ? Vector2.right
                    : nearest == toMinZ ? Vector2.down
                    : Vector2.up;
            }
            var world = ToWorld(mapObject, away.normalized, 0f) - ToWorld(mapObject, Vector2.zero, 0f);
            world.y = 0f;
            return world.normalized;
        }

        // Short text for logs: the nearest few objects around position.
        public static string DescribeNear(Vector3 position, float radius)
        {
            if (!Ready)
            {
                return "catalogo nao pronto";
            }
            var found = new List<BotMapObject>();
            Query(position, radius, found);
            found.RemoveAll(o => o.Category == BotObjectCategory.Building && o.Bounds.Contains(position));
            if (found.Count == 0)
            {
                return "nenhum";
            }
            return string.Join(", ", found
                .OrderBy(o => (o.Bounds.ClosestPoint(position) - position).sqrMagnitude)
                .Take(5)
                .Select(o => $"{o.Name} ({CategoryName(o.Category)}, {(o.Bounds.ClosestPoint(position) - position).magnitude:0.0}m)"));
        }

        private static void Build(ILogger logger)
        {
            var watch = Stopwatch.StartNew();
            var colliders = new List<Collider>();
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
                        colliders.Clear();
                        transform.GetComponentsInChildren(true, colliders);
                        var hasBounds = false;
                        var bounds = new Bounds(transform.position, Vector3.zero);
                        var frame = new BotMapObject
                        {
                            FrameOrigin = transform.position,
                            Yaw = transform.eulerAngles.y,
                            LocalMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity),
                            LocalMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity),
                            MinY = float.PositiveInfinity,
                            MaxY = float.NegativeInfinity
                        };
                        foreach (var collider in colliders)
                        {
                            if (collider == null || collider.isTrigger)
                            {
                                continue;
                            }
                            if (!hasBounds)
                            {
                                bounds = collider.bounds;
                                hasBounds = true;
                            }
                            else
                            {
                                bounds.Encapsulate(collider.bounds);
                            }
                            AddToFootprint(frame, collider);
                        }
                        if (!hasBounds)
                        {
                            // Decals and props without colliders don't block a bot.
                            continue;
                        }
                        var mapObject = frame;
                        mapObject.Name = asset.name ?? asset.objectName ?? string.Empty;
                        mapObject.Type = asset.type;
                        mapObject.Interactability = asset.interactability;
                        mapObject.HasRubble = asset.rubble != EObjectRubble.NONE;
                        mapObject.Position = transform.position;
                        mapObject.Bounds = bounds;
                        mapObject.Category = Classify(mapObject);
                        s_Objects.Add(mapObject);
                        Index(mapObject);
                        foreach (var collider in colliders)
                        {
                            if (collider != null && !collider.isTrigger)
                            {
                                s_ByCollider[collider] = mapObject;
                            }
                        }
                    }
                }
            }
            watch.Stop();
            Report(logger, watch.ElapsedMilliseconds);
        }

        private static readonly Vector3[] s_Corners = new Vector3[8];

        private static void AddToFootprint(BotMapObject frame, Collider collider)
        {
            var corners = s_Corners;
            var count = 8;
            var colliderTransform = collider.transform;
            if (collider is BoxCollider box)
            {
                FillCorners(box.center, box.size, colliderTransform, corners);
            }
            else if (collider is MeshCollider meshCollider && meshCollider.sharedMesh != null)
            {
                var meshBounds = meshCollider.sharedMesh.bounds;
                FillCorners(meshBounds.center, meshBounds.size, colliderTransform, corners);
            }
            else
            {
                var world = collider.bounds;
                FillCorners(world.center, world.size, null, corners);
            }
            for (var index = 0; index < count; index++)
            {
                var local = ToLocal(frame, corners[index]);
                frame.LocalMin = Vector2.Min(frame.LocalMin, local);
                frame.LocalMax = Vector2.Max(frame.LocalMax, local);
                frame.MinY = Mathf.Min(frame.MinY, corners[index].y);
                frame.MaxY = Mathf.Max(frame.MaxY, corners[index].y);
            }
        }

        private static void FillCorners(Vector3 center, Vector3 size, Transform? space, Vector3[] corners)
        {
            var half = size * 0.5f;
            var index = 0;
            for (var x = -1; x <= 1; x += 2)
            {
                for (var y = -1; y <= 1; y += 2)
                {
                    for (var z = -1; z <= 1; z += 2)
                    {
                        var point = center + new Vector3(half.x * x, half.y * y, half.z * z);
                        corners[index++] = space != null ? space.TransformPoint(point) : point;
                    }
                }
            }
        }

        private static BotObjectCategory Classify(BotMapObject mapObject)
        {
            var name = mapObject.Name;
            if (Has(name, "Ladder"))
            {
                return BotObjectCategory.Ladder;
            }
            if (Has(name, "Door") || Has(name, "Gate")
                || mapObject.Interactability == EObjectInteractability.BINARY_STATE && Has(name, "Hatch"))
            {
                return BotObjectCategory.Door;
            }
            if (s_FurnitureWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Furniture;
            }
            if (s_ObstacleWords.Any(word => HasWord(name, word)) || name.StartsWith("AC_", StringComparison.OrdinalIgnoreCase))
            {
                return BotObjectCategory.Obstacle;
            }
            if (s_RubbleWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Rubble;
            }
            if (s_FenceWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Fence;
            }
            if (s_RockWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Rock;
            }
            if (s_TreeWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Tree;
            }
            if (s_WreckWords.Any(word => HasWord(name, word)))
            {
                return BotObjectCategory.Wreck;
            }
            var size = mapObject.Bounds.size;
            if (s_PropWords.Any(word => HasWord(name, word)) || size.y < 0.5f)
            {
                return BotObjectCategory.Other;
            }
            if (mapObject.Type == EObjectType.LARGE || Mathf.Max(size.x, size.z) > 8f)
            {
                return BotObjectCategory.Building;
            }
            return BotObjectCategory.Other;
        }

        private static bool Has(string name, string word)
        {
            return name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Short words like "Car" or "Van" must start a name part, so that
        // "Carpet" or "Caravan" don't end up as wrecks.
        private static bool HasWord(string name, string word)
        {
            var index = name.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var startOk = index == 0 || !char.IsLetter(name[index - 1]) || char.IsUpper(name[index]) && char.IsLower(name[index - 1]);
                var end = index + word.Length;
                var endOk = end >= name.Length || !char.IsLower(name[end]);
                if (startOk && endOk)
                {
                    return true;
                }
                index = name.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static void Index(BotMapObject mapObject)
        {
            var bounds = mapObject.Bounds;
            var minX = CellOf(bounds.min.x);
            var maxX = CellOf(bounds.max.x);
            var minZ = CellOf(bounds.min.z);
            var maxZ = CellOf(bounds.max.z);
            for (var cx = minX; cx <= maxX; cx++)
            {
                for (var cz = minZ; cz <= maxZ; cz++)
                {
                    var key = Key(cx, cz);
                    if (!s_Cells.TryGetValue(key, out var cell))
                    {
                        cell = new List<BotMapObject>();
                        s_Cells[key] = cell;
                    }
                    cell.Add(mapObject);
                }
            }
        }

        private static int CellOf(float coordinate)
        {
            return Mathf.FloorToInt(coordinate / CellSize);
        }

        private static long Key(int cx, int cz)
        {
            return ((long)cx << 32) | (uint)cz;
        }

        internal static string CategoryName(BotObjectCategory category)
        {
            switch (category)
            {
                case BotObjectCategory.Building: return "construcao";
                case BotObjectCategory.Furniture: return "movel";
                case BotObjectCategory.Door: return "porta";
                case BotObjectCategory.Ladder: return "escada";
                case BotObjectCategory.Rock: return "pedra";
                case BotObjectCategory.Wreck: return "veiculo";
                case BotObjectCategory.Fence: return "cerca/muro";
                case BotObjectCategory.Rubble: return "entulho";
                case BotObjectCategory.Tree: return "arvore";
                case BotObjectCategory.Obstacle: return "obstaculo";
                default: return "outro";
            }
        }

        private static void Report(ILogger logger, long elapsedMs)
        {
            var counts = new StringBuilder();
            foreach (var group in s_Objects.GroupBy(o => o.Category).OrderByDescending(g => g.Count()))
            {
                if (counts.Length > 0)
                {
                    counts.Append(", ");
                }
                counts.Append(CategoryName(group.Key)).Append(' ').Append(group.Count());
            }
            logger.LogInformation(
                "Catalogo de objetos do mapa {Map}: {Count} objetos com colisao em {Cells} celulas de {CellSize:0}m, {Elapsed} ms. Por tipo: {Counts}.",
                s_Map, s_Objects.Count, s_Cells.Count, CellSize, elapsedMs, counts.ToString());
            foreach (var group in s_Objects.GroupBy(o => o.Category).OrderBy(g => g.Key))
            {
                var top = string.Join(", ", group
                    .GroupBy(o => o.Name)
                    .OrderByDescending(g => g.Count())
                    .Take(group.Key == BotObjectCategory.Other ? 40 : 15)
                    .Select(g => $"{g.Key} x{g.Count()}"));
                logger.LogInformation("Catalogo de objetos, {Category}: {Top}", CategoryName(group.Key), top);
            }
        }
    }
}
