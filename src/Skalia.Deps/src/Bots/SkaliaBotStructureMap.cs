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
    // A house or shed of the map: a large object with item spawns inside its
    // collider bounds, and the points where the navmesh crosses its outline
    // (the doorways; Russia's houses have open doorways, not door objects).
    internal sealed class BotBuilding
    {
        public int Index;
        public string Name = string.Empty;
        public Bounds Bounds;
        public readonly List<Vector3> LootPoints = new List<Vector3>();
        public readonly List<Vector3> Doorways = new List<Vector3>();

        public bool Contains(Vector3 point, float margin)
        {
            var min = Bounds.min;
            var max = Bounds.max;
            return point.x >= min.x - margin && point.x <= max.x + margin
                && point.z >= min.z - margin && point.z <= max.z + margin
                && point.y >= min.y - 1.5f && point.y <= max.y + 0.5f;
        }
    }

    // An object the bot can open: shipping containers, fridges, wardrobes and
    // the few real doors (prison, blast, research). Closed, it cuts the navmesh.
    internal sealed class BotDoor
    {
        public Transform Transform = null!;
        public InteractableObjectBinaryState State = null!;
        public Vector3 Center;
        public string Name = string.Empty;
        // 0.1.169: the leaves that swing. A shipping container is one 6m
        // object with its doors at one end: Tadeu stood at the closed doors,
        // 3m from the middle of the box, and the 2.5m reach to open it was
        // measured to the middle, so he walked into them.
        public readonly List<Collider> Leaves = new List<Collider>();

        public bool IsClosed => State != null && !State.isUsed;

        // 0.1.0-y17.26: the map's own conditions to open it, as the game
        // checks them for a player (ObjectManager.ReceiveToggleObjectBinaryStateRequest):
        // a key door of Arid or Elver needs the key item in the bag.
        public ObjectAsset? Asset;
        public readonly List<ushort> Keys = new List<ushort>();

        public bool CanOpen(Player player)
        {
            return Asset == null
                || (Asset.areConditionsMet(player) && Asset.areInteractabilityConditionsMet(player));
        }

        // Where a player presses F: the middle of the leaves (the object's
        // middle when it has none).
        public Vector3 UsePoint
        {
            get
            {
                var found = false;
                var bounds = default(Bounds);
                foreach (var leaf in Leaves)
                {
                    if (leaf == null || !leaf.enabled)
                    {
                        continue;
                    }
                    if (!found)
                    {
                        bounds = leaf.bounds;
                        found = true;
                    }
                    else
                    {
                        bounds.Encapsulate(leaf.bounds);
                    }
                }
                return found ? bounds.center : Center;
            }
        }
    }

    internal static class SkaliaBotStructureMap
    {
        private const float CellSize = 32f;
        private const double FrameBudgetMilliseconds = 3.0;
        private const float MinimumFootprint = 12f;
        private const float MaximumFootprint = 4000f;
        private const int MaxDoorwayNodes = 3000;

        private static bool s_Started;
        private static bool s_Ready;
        private static string? s_Map;
        private static readonly List<BotBuilding> s_Buildings = new List<BotBuilding>();
        private static readonly List<BotDoor> s_Doors = new List<BotDoor>();
        private static readonly HashSet<ushort> s_KeyItems = new HashSet<ushort>();

        // 0.1.0-y17.26: the items some door of the map asks for.
        public static HashSet<ushort> KeyItems => s_KeyItems;

        public static IReadOnlyList<BotDoor> Doors => s_Doors;
        private static readonly Dictionary<long, List<int>> s_BuildingCells = new Dictionary<long, List<int>>();
        private static readonly Dictionary<long, List<int>> s_DoorCells = new Dictionary<long, List<int>>();

        public static bool Ready => s_Ready && s_Map == Level.info?.name;

        public static string Status { get; private set; } = "nao iniciado";

        public static void EnsureStarted(ILogger logger)
        {
            var map = Level.info?.name;
            if (map == null || (s_Started && s_Map == map))
            {
                return;
            }
            s_Started = true;
            s_Ready = false;
            s_Map = map;
            BuildAsync(map, logger).Forget();
        }

        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static void AddToCells(Dictionary<long, List<int>> cells, Bounds bounds, int index)
        {
            var minX = Mathf.FloorToInt(bounds.min.x / CellSize);
            var maxX = Mathf.FloorToInt(bounds.max.x / CellSize);
            var minZ = Mathf.FloorToInt(bounds.min.z / CellSize);
            var maxZ = Mathf.FloorToInt(bounds.max.z / CellSize);
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    var key = Key(x, z);
                    if (!cells.TryGetValue(key, out var list))
                    {
                        list = new List<int>();
                        cells[key] = list;
                    }
                    list.Add(index);
                }
            }
        }

        private static async UniTaskVoid BuildAsync(string map, ILogger logger)
        {
            try
            {
                await UniTask.SwitchToMainThread();
                var watch = Stopwatch.StartNew();
                var slice = Stopwatch.StartNew();
                var buildings = new List<BotBuilding>();
                var doors = new List<BotDoor>();
                var colliders = new List<Collider>();
                var scannedObjects = 0;
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
                            scannedObjects++;
                            var asset = levelObject?.asset;
                            var transform = levelObject?.transform;
                            if (asset == null || transform == null)
                            {
                                continue;
                            }
                            if (levelObject!.interactable is InteractableObjectBinaryState binary
                                && asset.interactabilityHint == EObjectInteractabilityHint.DOOR)
                            {
                                var door = new BotDoor
                                {
                                    Asset = asset,
                                    Transform = transform,
                                    State = binary,
                                    Center = CombinedBounds(transform, colliders, out var doorBounds)
                                        ? doorBounds.center
                                        : transform.position,
                                    Name = asset.objectName ?? asset.name
                                };
                                foreach (var collider in colliders)
                                {
                                    if (collider != null
                                        && collider.enabled
                                        && !collider.isTrigger
                                        && SkaliaBotUnityNavmesh.IsThinLeaf(collider))
                                    {
                                        door.Leaves.Add(collider);
                                    }
                                }
#pragma warning disable CS0612
                                var conditions = asset.interactabilityConditions;
#pragma warning restore CS0612
                                if (conditions != null)
                                {
                                    foreach (var condition in conditions)
                                    {
                                        if (condition is NPCItemCondition needsItem
                                            && needsItem.GetItemAsset() is ItemAsset key
                                            && !door.Keys.Contains(key.id))
                                        {
                                            door.Keys.Add(key.id);
                                        }
                                    }
                                }
                                doors.Add(door);
                            }
                            if (asset.type != EObjectType.LARGE
                                || !CombinedBounds(transform, colliders, out var bounds))
                            {
                                continue;
                            }
                            var footprint = bounds.size.x * bounds.size.z;
                            if (footprint < MinimumFootprint
                                || footprint > MaximumFootprint
                                || bounds.size.y < 2.5f)
                            {
                                continue;
                            }
                            var building = new BotBuilding
                            {
                                Name = asset.objectName ?? asset.name,
                                Bounds = bounds
                            };
                            CollectLoot(building);
                            if (building.LootPoints.Count > 0)
                            {
                                buildings.Add(building);
                            }
                        }
                        if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                        {
                            await UniTask.Yield(PlayerLoopTiming.Update);
                            if (s_Map != map || Level.info?.name != map)
                            {
                                return;
                            }
                            slice.Restart();
                        }
                    }
                }

                var doorways = 0;
                for (var i = 0; i < buildings.Count; i++)
                {
                    buildings[i].Index = i;
                    FindDoorways(buildings[i]);
                    doorways += buildings[i].Doorways.Count;
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        if (s_Map != map || Level.info?.name != map)
                        {
                            return;
                        }
                        slice.Restart();
                    }
                }

                s_Buildings.Clear();
                s_Buildings.AddRange(buildings);
                s_Doors.Clear();
                s_Doors.AddRange(doors);
                s_BuildingCells.Clear();
                s_DoorCells.Clear();
                for (var i = 0; i < s_Buildings.Count; i++)
                {
                    AddToCells(s_BuildingCells, s_Buildings[i].Bounds, i);
                }
                for (var i = 0; i < s_Doors.Count; i++)
                {
                    var doorCell = new Bounds(s_Doors[i].Center, Vector3.one);
                    doorCell.Encapsulate(s_Doors[i].UsePoint);
                    AddToCells(s_DoorCells, doorCell, i);
                }
                var closed = 0;
                s_KeyItems.Clear();
                var keyDoors = 0;
                foreach (var door in s_Doors)
                {
                    if (door.IsClosed) closed++;
                    if (door.Keys.Count > 0)
                    {
                        keyDoors++;
                        s_KeyItems.UnionWith(door.Keys);
                    }
                }
                if (keyDoors > 0)
                {
                    var names = new List<string>();
                    foreach (var id in s_KeyItems)
                    {
                        names.Add((Assets.find(EAssetType.ITEM, id) as ItemAsset)?.itemName ?? id.ToString());
                    }
                    logger.LogInformation(
                        "Portas com chave em {Map}: {Doors} porta(s), chaves {Keys}.",
                        map,
                        keyDoors,
                        string.Join(", ", names));
                }
                var withoutDoorway = 0;
                foreach (var building in s_Buildings)
                {
                    if (building.Doorways.Count == 0) withoutDoorway++;
                }
                s_Ready = true;
                Status = "pronto";
                logger.LogInformation(
                    "Catalogo de construcoes de {Map} pronto em {Seconds:0.0}s: {Objects} objetos lidos, {Buildings} construcoes com loot ({Doorways} entradas na malha, {NoDoorway} sem entrada), {Doors} portas/containers abriveis ({Closed} fechados).",
                    map,
                    watch.Elapsed.TotalSeconds,
                    scannedObjects,
                    s_Buildings.Count,
                    doorways,
                    withoutDoorway,
                    s_Doors.Count,
                    closed);
            }
            catch (Exception exception)
            {
                Status = "falhou";
                logger.LogError(exception, "Falha ao catalogar as construcoes de {Map}.", map);
            }
        }

        private static bool CombinedBounds(Transform transform, List<Collider> colliders, out Bounds bounds)
        {
            bounds = default;
            colliders.Clear();
            transform.GetComponentsInChildren(false, colliders);
            var found = false;
            foreach (var collider in colliders)
            {
                if (collider == null || !collider.enabled || collider.isTrigger)
                {
                    continue;
                }
                if (!found)
                {
                    bounds = collider.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }
            return found;
        }

        private static void CollectLoot(BotBuilding building)
        {
            if (LevelItems.spawns == null)
            {
                return;
            }
            var min = building.Bounds.min;
            var max = building.Bounds.max;
            if (!Regions.tryGetCoordinate(min, out var minX, out var minY)
                || !Regions.tryGetCoordinate(max, out var maxX, out var maxY))
            {
                return;
            }
            for (var x = minX; x <= maxX; x++)
            {
                for (var y = minY; y <= maxY; y++)
                {
                    var spawns = LevelItems.spawns[x, y];
                    if (spawns == null)
                    {
                        continue;
                    }
                    foreach (var spawn in spawns)
                    {
                        if (building.Contains(spawn.point, 0.25f))
                        {
                            building.LootPoints.Add(spawn.point);
                        }
                    }
                }
            }
        }

        // Flood the navmesh from the loot inside the building; every edge that
        // leaves its outline horizontally is an entrance.
        private static void FindDoorways(BotBuilding building)
        {
            if (AstarPath.active == null)
            {
                return;
            }
            var visited = new HashSet<GraphNode>();
            var queue = new Queue<GraphNode>();
            foreach (var point in building.LootPoints)
            {
                var nearest = AstarPath.active.GetNearest(point, NNConstraint.Walkable);
                if (nearest.node != null
                    && (nearest.position - point).GetHorizontalMagnitude() <= 2.5f
                    && Mathf.Abs(nearest.position.y - point.y) <= 2f
                    && visited.Add(nearest.node))
                {
                    queue.Enqueue(nearest.node);
                }
            }
            var candidates = new List<Vector3>();
            while (queue.Count > 0 && visited.Count < MaxDoorwayNodes)
            {
                var node = queue.Dequeue();
                var nodePosition = (Vector3)node.position;
                node.GetConnections(neighbor =>
                {
                    if (!neighbor.Walkable || visited.Contains(neighbor))
                    {
                        return;
                    }
                    var neighborPosition = (Vector3)neighbor.position;
                    if (building.Contains(neighborPosition, 0.3f))
                    {
                        visited.Add(neighbor);
                        queue.Enqueue(neighbor);
                        return;
                    }
                    // Outside only by height is another floor's mesh, not an entrance.
                    if (building.Contains(new Vector3(neighborPosition.x, nodePosition.y, neighborPosition.z), 0.3f))
                    {
                        return;
                    }
                    candidates.Add((nodePosition + neighborPosition) * 0.5f);
                });
            }
            foreach (var candidate in candidates)
            {
                var merged = false;
                foreach (var doorway in building.Doorways)
                {
                    if ((doorway - candidate).GetHorizontalMagnitude() < 3f)
                    {
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                {
                    building.Doorways.Add(candidate);
                    if (building.Doorways.Count >= 8)
                    {
                        break;
                    }
                }
            }
        }

        public static BotBuilding? FindBuilding(Vector3 point)
        {
            if (!Ready
                || !s_BuildingCells.TryGetValue(
                    Key(Mathf.FloorToInt(point.x / CellSize), Mathf.FloorToInt(point.z / CellSize)),
                    out var list))
            {
                return null;
            }
            BotBuilding? best = null;
            var bestVolume = float.MaxValue;
            foreach (var index in list)
            {
                var building = s_Buildings[index];
                var volume = building.Bounds.size.x * building.Bounds.size.z;
                if (building.Contains(point, 0.25f) && volume < bestVolume)
                {
                    best = building;
                    bestVolume = volume;
                }
            }
            return best;
        }

        // 0.2.2c: shipping containers (objectName "Container", assets
        // Container_0 to _5) are left out while there is no building or PvP.
        public static bool IsContainerName(string? name)
        {
            return name != null && name.StartsWith("Container", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInsideContainer(Vector3 point)
        {
            var building = FindBuilding(point);
            return building != null && IsContainerName(building.Name);
        }

        // 0.2.2c: an item inside openable furniture (fridge, wardrobe,
        // cupboard): within the object's footprint, open or closed. Real
        // doors and shipping containers do not count.
        public static bool IsInOpenableFurniture(Vector3 point)
        {
            if (!Ready)
            {
                return false;
            }
            const float reach = 1.1f;
            var minX = Mathf.FloorToInt((point.x - reach) / CellSize);
            var maxX = Mathf.FloorToInt((point.x + reach) / CellSize);
            var minZ = Mathf.FloorToInt((point.z - reach) / CellSize);
            var maxZ = Mathf.FloorToInt((point.z + reach) / CellSize);
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    if (!s_DoorCells.TryGetValue(Key(x, z), out var list))
                    {
                        continue;
                    }
                    foreach (var index in list)
                    {
                        var door = s_Doors[index];
                        if (door.Transform == null
                            || door.State == null
                            || IsContainerName(door.Name)
                            || SkaliaBotUnityNavmesh.IsRealDoor(door.State))
                        {
                            continue;
                        }
                        if ((door.Center - point).GetHorizontalMagnitude() <= reach
                            && Mathf.Abs(door.Center.y - point.y) <= 1.3f)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        public static BotDoor? FindClosedDoor(Vector3 point, float radius)
        {
            if (!Ready)
            {
                return null;
            }
            BotDoor? best = null;
            var bestDistance = radius;
            var minX = Mathf.FloorToInt((point.x - radius) / CellSize);
            var maxX = Mathf.FloorToInt((point.x + radius) / CellSize);
            var minZ = Mathf.FloorToInt((point.z - radius) / CellSize);
            var maxZ = Mathf.FloorToInt((point.z + radius) / CellSize);
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    if (!s_DoorCells.TryGetValue(Key(x, z), out var list))
                    {
                        continue;
                    }
                    foreach (var index in list)
                    {
                        var door = s_Doors[index];
                        if (door.Transform == null || !door.IsClosed)
                        {
                            continue;
                        }
                        var usePoint = door.UsePoint;
                        var distance = (usePoint - point).GetHorizontalMagnitude();
                        if (distance <= bestDistance && Mathf.Abs(usePoint.y - point.y) <= 2.5f)
                        {
                            best = door;
                            bestDistance = distance;
                        }
                    }
                }
            }
            return best;
        }

        // 0.1.170: the open doors and containers near a point (by the
        // middle of their leaves, which swing with them).
        public static void FindOpenDoors(Vector3 point, float radius, List<BotDoor> results)
        {
            results.Clear();
            if (!Ready)
            {
                return;
            }
            var minX = Mathf.FloorToInt((point.x - radius) / CellSize);
            var maxX = Mathf.FloorToInt((point.x + radius) / CellSize);
            var minZ = Mathf.FloorToInt((point.z - radius) / CellSize);
            var maxZ = Mathf.FloorToInt((point.z + radius) / CellSize);
            for (var x = minX; x <= maxX; x++)
            {
                for (var z = minZ; z <= maxZ; z++)
                {
                    if (!s_DoorCells.TryGetValue(Key(x, z), out var list))
                    {
                        continue;
                    }
                    foreach (var index in list)
                    {
                        var door = s_Doors[index];
                        if (door.Transform == null || door.State == null || door.IsClosed || results.Contains(door))
                        {
                            continue;
                        }
                        var usePoint = door.UsePoint;
                        if ((usePoint - point).GetHorizontalMagnitude() <= radius && Mathf.Abs(usePoint.y - point.y) <= 3f)
                        {
                            results.Add(door);
                        }
                    }
                }
            }
        }

        // 0.1.0-y17.26: opened as the game opens it for a player: only with
        // its conditions met (the key in the bag), which are then applied
        // (a key the map takes is taken) and its rewards given. Before, a
        // locked door opened for a bot with no key.
        public static bool Open(BotDoor door, Player player)
        {
            if (door.Transform == null || !door.IsClosed || !door.State.isUsable || !door.CanOpen(player))
            {
                return false;
            }
            if (door.Asset != null)
            {
                door.Asset.ApplyInteractabilityConditions(player);
                door.Asset.GrantInteractabilityRewards(player);
            }
            ObjectManager.forceObjectBinaryState(door.Transform, true);
            return !door.IsClosed;
        }
    }
}
