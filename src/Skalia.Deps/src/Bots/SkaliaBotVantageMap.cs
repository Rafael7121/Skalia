using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // 0.2.2x6: taken from the new mesh (the one the bots walk, with the
    // ladders linked), reachable = a complete route on it; covered spots
    // with open sides count (a guard tower's cabin has a roof).
    //
    // 0.2.2x5: junk surfaces out (vehicles, trees, furniture, props on a
    // roof), object tops must be the object's own collider under open sky,
    // the view is taken from the roof edge against street points, and the
    // score is led by height. the tester: the bots can't climb the grey building
    // nor the helipad (jumps and plates), so for now those are threats only.
    //
    // 0.2.2x4 (doc 16.12): catalog of the high places of the hunt area, the
    // "ladder" the tester described in Seattle: the bridge sees the city but the
    // grey building sees the bridge; the helipad building beats the grey
    // one; the crane and the white tower beat all, higher though with no
    // cover. A place is good by what it sees and bad by the places that see
    // it, the higher ones above all.
    //
    // Step 1 only measures and writes a CSV (and the log); no bot uses it.
    // Everything comes from the map that is loaded (A* navmesh, colliders,
    // terrain), so any map gets its own catalog.
    internal static class SkaliaBotVantageMap
    {
        private const double FrameBudgetMilliseconds = 3.0;
        private const float AreaRadius = 350f;
        private const float MinimumRise = 6f;
        private const float ClusterCell = 8f;
        private const float ClusterBand = 3f;
        private const int MaximumVantages = 300;
        private const float PairRadius = 250f;
        private const int Directions = 16;
        private static readonly float[] s_Rings = { 40f, 80f, 120f };
        private const float ProneBody = 0.3f;
        private const float CrouchBody = 1.0f;
        private const float StandBody = 1.5f;
        private const float EyeHeight = 1.6f;
        private const float CameraUp = 2.0f;
        private const float CameraBack = 1.5f;
        private const float CoverReach = 3f;
        // 0.2.2x5: the view is taken from the edge of the roof (a player
        // walks to the edge to look down), against street points of the
        // navmesh, one per 20m cell, up to 150m away.
        private const float StreetCell = 20f;
        private const float StreetRange = 150f;
        private const float StreetHeight = 2.5f;
        private const float EdgeReach = 30f;
        private const float EdgeStep = 1f;
        private const float EdgeDrop = 1.2f;
        private const float SkyClear = 50f;
        // A covered spot counts when 5 of 16 directions are clear for 12m
        // at eye height: a guard tower's cabin, not a room with a window.
        private const int MinimumOpenSides = 5;
        private const float OpenSideReach = 12f;
        // Names that are never a place to stand, whatever the object map says:
        // vehicles and their wrecks, flags, props on a roof.
        private static readonly string[] s_JunkPrefixes =
        {
            "Taxi", "Ambulance", "Police", "Off_Roader", "Humvee", "Roadster", "Truck", "Sedan", "Jeep",
            "Bus", "Van_", "Car_", "Fire_Truck", "Firetruck", "Pickup", "Hatchback", "Flag", "Computer",
            "Coil", "Lamp", "Light", "Pole", "Sign", "Antenna", "Pine", "Tree", "Bush", "Birch", "AC_",
            "Tank_Fuel", "Wardrobe", "Fridge", "Crate", "Barrel", "Pallet", "Dumpster", "DL_Garbage",
            "Debris", "Ladder", "Fence", "Wire"
        };

        internal sealed class Vantage
        {
            public Vector3 Position;
            public float Rise;
            public float AboveGround;
            public string Surface = string.Empty;
            public bool Reachable;
            public float View;
            public float GroundSeesProne;
            public float GroundSeesCrouch;
            public float GroundSeesStand;
            public float Cover;
            public int Higher;
            public int SeenProneByHigher;
            public int SeenProneByLevel;
            public int SeenProneByLower;
            public int SeenCrouchByHigher;
            public int SeenCrouchByLevel;
            public int SeenCrouchByLower;
            public float Score;
            public string BestPosture = string.Empty;
            // 0.2.2x5: from the edge, in third person: street points seen,
            // and street points that see the body crouched at the edge.
            public float EdgeView;
            public float EdgeExposed;
            public int Streets;
            public bool Covered;
            public int OpenSides;
        }

        private static bool s_Started;
        private static string? s_Map;
        private static readonly List<Vantage> s_Vantages = new List<Vantage>();

        public static bool Ready { get; private set; }
        public static IReadOnlyList<Vantage> Vantages => s_Vantages;

        public static void EnsureStarted(ILogger logger, Vector3 center, string areaName)
        {
            var map = Level.info?.name;
            if (map == null || (s_Started && s_Map == map))
            {
                return;
            }
            s_Started = true;
            s_Map = map;
            Ready = false;
            s_Vantages.Clear();
            RunAsync(logger, map, center, areaName).Forget();
        }

        private static async UniTaskVoid RunAsync(ILogger logger, string map, Vector3 center, string areaName)
        {
            try
            {
                await UniTask.SwitchToMainThread();
                while (AstarPath.active == null || AstarPath.active.data?.graphs == null)
                {
                    await UniTask.Delay(1000);
                }
                // The other catalogs go first; this one is not urgent.
                await UniTask.Delay(20000);

                // 0.2.2x6: the bots walk the new mesh (baked from the
                // colliders, ladders linked foot to top), so the catalog is
                // taken from it and "reachable" means a complete route on it.
                // The zombies' mesh has no ladders: the guard tower came out
                // unreachable. If the new mesh is off or not ready in 3 min,
                // the zombies' mesh is used as before.
                for (var wait = 0; wait < 180 && SkaliaBotUnityNavmesh.Enabled && !SkaliaBotUnityNavmesh.MapFilled; wait++)
                {
                    await UniTask.Delay(1000);
                }
                var watch = Stopwatch.StartNew();
                var slice = Stopwatch.StartNew();
                var points = new List<Vector3>();
                var astarNodes = new List<GraphNode>();
                var newMesh = SkaliaBotUnityNavmesh.TryTriangulate(out var meshVertices, out var meshIndices);
                if (newMesh)
                {
                    for (var t = 0; t + 2 < meshIndices.Length; t += 3)
                    {
                        var centroid = (meshVertices[meshIndices[t]] + meshVertices[meshIndices[t + 1]] + meshVertices[meshIndices[t + 2]]) / 3f;
                        if ((centroid - center).GetHorizontalMagnitude() <= AreaRadius + StreetRange)
                        {
                            points.Add(centroid);
                        }
                    }
                    meshVertices = Array.Empty<Vector3>();
                    meshIndices = Array.Empty<int>();
                }
                else
                {
                    foreach (var graph in AstarPath.active.data.graphs)
                    {
                        if (graph is NavmeshBase navmesh)
                        {
                            navmesh.GetNodes(node =>
                            {
                                if (node is TriangleMeshNode triangle && node.Walkable)
                                {
                                    points.Add(((Vector3)triangle.GetVertex(0) + (Vector3)triangle.GetVertex(1) + (Vector3)triangle.GetVertex(2)) / 3f);
                                    astarNodes.Add(node);
                                }
                            });
                        }
                    }
                }
                var origin = newMesh ? null : AstarPath.active.GetNearest(center, NNConstraint.Walkable).node;

                // 1. High walkable triangles, one per cell: under open sky,
                // or under a roof with open sides (a guard tower's cabin).
                var cells = new Dictionary<long, Vantage>();
                var streetCells = new Dictionary<long, Vector3>();
                var triangles = 0;
                var junk = 0;
                var covered = 0;
                for (var index = 0; index < points.Count; index++)
                {
                    // Most triangles stop at a cheap check below, so the
                    // frame budget is checked first, not after them.
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        slice.Restart();
                    }
                    var point = points[index];
                    var fromCenter = (point - center).GetHorizontalMagnitude();
                    if (fromCenter <= AreaRadius + StreetRange
                        && point.y - LevelGround.getHeight(point) <= StreetHeight
                        && !WaterUtility.isPointUnderwater(point + Vector3.up * 0.5f))
                    {
                        var streetKey = StreetKey(point);
                        if (!streetCells.ContainsKey(streetKey) && HasOpenSky(point))
                        {
                            streetCells[streetKey] = point;
                        }
                    }
                    if (fromCenter > AreaRadius)
                    {
                        continue;
                    }
                    triangles++;
                    var rise = point.y - AverageTerrain(point, 30f);
                    if (rise < MinimumRise)
                    {
                        continue;
                    }
                    if (!TryFloor(point, out var floor, out var surface, out var floorCollider)
                        || WaterUtility.isPointUnderwater(floor + Vector3.up * 0.5f))
                    {
                        continue;
                    }
                    var key = CellKey(floor);
                    if (cells.TryGetValue(key, out var kept) && kept.Position.y >= floor.y)
                    {
                        continue;
                    }
                    var sky = HasOpenSky(floor);
                    var sides = OpenSides(floor);
                    if (!sky && sides < MinimumOpenSides)
                    {
                        continue;
                    }
                    if (floorCollider == null || !IsStandingSurface(floorCollider, surface))
                    {
                        junk++;
                        continue;
                    }
                    cells[key] = new Vantage
                    {
                        Position = floor,
                        Rise = floor.y - AverageTerrain(floor, 30f),
                        AboveGround = floor.y - LevelGround.getHeight(floor),
                        Surface = surface,
                        Covered = !sky,
                        OpenSides = sides,
                        Reachable = !newMesh && origin != null && PathUtilities.IsPathPossible(origin, astarNodes[index])
                    };
                }
                // Tall objects with no navmesh on top (a crane, a tower): a
                // bot cannot stand there, but someone up there sees the rest.
                for (var wait = 0; wait < 120 && !SkaliaBotObjectMap.Ready; wait++)
                {
                    await UniTask.Delay(1000);
                }
                var objectTops = 0;
                foreach (var mapObject in SkaliaBotObjectMap.Ready ? SkaliaBotObjectMap.Objects : Array.Empty<BotMapObject>())
                {
                    var top = mapObject.Bounds.center;
                    if ((top - center).GetHorizontalMagnitude() > AreaRadius
                        || mapObject.Bounds.max.y - AverageTerrain(top, 30f) < MinimumRise + 6f)
                    {
                        continue;
                    }
                    if (!IsStandingObject(mapObject))
                    {
                        junk++;
                        continue;
                    }
                    // The top of the object itself, not an AC unit or a crate
                    // sitting on it: the hit must be one of its own colliders,
                    // under open sky; the highest of five samples wins.
                    var found = false;
                    var roof = top;
                    var extents = mapObject.Bounds.extents;
                    for (var sample = 0; sample < 5; sample++)
                    {
                        var offset = sample == 0 ? Vector3.zero
                            : new Vector3((sample & 1) == 1 ? 0.4f : -0.4f, 0f, (sample & 2) == 2 ? 0.4f : -0.4f);
                        var xz = top + new Vector3(offset.x * extents.x, 0f, offset.z * extents.z);
                        if (!Physics.Raycast(new Vector3(xz.x, mapObject.Bounds.max.y + 2f, xz.z), Vector3.down, out var hit, mapObject.Bounds.size.y + 4f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                            || !SkaliaBotObjectMap.TryGetByCollider(hit.collider, out var owner)
                            || owner != mapObject
                            || hit.point.y < mapObject.Bounds.max.y - 3f
                            || !HasOpenSky(hit.point)
                            || WaterUtility.isPointUnderwater(hit.point + Vector3.up * 0.3f))
                        {
                            continue;
                        }
                        if (!found || hit.point.y > roof.y)
                        {
                            roof = hit.point;
                            found = true;
                        }
                    }
                    if (!found)
                    {
                        continue;
                    }
                    var key = CellKey(roof);
                    if (cells.ContainsKey(key))
                    {
                        continue;
                    }
                    cells[key] = new Vantage
                    {
                        Position = roof,
                        Rise = roof.y - AverageTerrain(roof, 30f),
                        AboveGround = roof.y - LevelGround.getHeight(roof),
                        Surface = mapObject.Name.Replace(';', '_') + " (topo de objeto)",
                        Reachable = false
                    };
                    objectTops++;
                }
                var list = new List<Vantage>(cells.Values);
                list.Sort((left, right) => right.Position.y.CompareTo(left.Position.y));
                if (list.Count > MaximumVantages)
                {
                    list.RemoveRange(MaximumVantages, list.Count - MaximumVantages);
                }
                var gather = watch.Elapsed.TotalSeconds;
                var streets = new List<Vector3>(streetCells.Values);
                if (newMesh)
                {
                    // Reachable: a complete route on the new mesh (ladders
                    // included) from one of the 3 nearest street points.
                    var nearest = new List<Vector3>();
                    foreach (var vantage in list)
                    {
                        nearest.Clear();
                        nearest.AddRange(streets);
                        var here = vantage.Position;
                        nearest.Sort((left, right) => (left - here).GetHorizontalMagnitude().CompareTo((right - here).GetHorizontalMagnitude()));
                        for (var n = 0; n < Mathf.Min(3, nearest.Count) && !vantage.Reachable; n++)
                        {
                            vantage.Reachable = SkaliaBotUnityNavmesh.IsPathComplete(nearest[n], here);
                        }
                        if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                        {
                            await UniTask.Yield(PlayerLoopTiming.Update);
                            slice.Restart();
                        }
                    }
                }
                foreach (var vantage in list)
                {
                    covered += vantage.Covered ? 1 : 0;
                }

                // 2. Each one alone: what the third-person camera sees of the
                // ground round it, what the ground sees of the body, cover,
                // and from the edge what it sees of the streets.
                foreach (var vantage in list)
                {
                    MeasureAlone(vantage);
                    MeasureEdges(vantage, streets);
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        slice.Restart();
                    }
                }

                // 3. Pairs: who sees whom, from a standing eye to the body
                // lying down and crouched.
                for (var i = 0; i < list.Count; i++)
                {
                    var target = list[i];
                    foreach (var watcher in list)
                    {
                        if (watcher == target)
                        {
                            continue;
                        }
                        var gap = (watcher.Position - target.Position).GetHorizontalMagnitude();
                        if (gap > PairRadius)
                        {
                            continue;
                        }
                        var dy = watcher.Position.y - target.Position.y;
                        if (dy > 2f)
                        {
                            target.Higher++;
                        }
                        var eye = watcher.Position + Vector3.up * EyeHeight;
                        var prone = Sees(eye, target.Position + Vector3.up * ProneBody);
                        var crouch = prone || Sees(eye, target.Position + Vector3.up * CrouchBody);
                        if (dy > 2f)
                        {
                            target.SeenProneByHigher += prone ? 1 : 0;
                            target.SeenCrouchByHigher += crouch ? 1 : 0;
                        }
                        else if (dy >= -2f)
                        {
                            target.SeenProneByLevel += prone ? 1 : 0;
                            target.SeenCrouchByLevel += crouch ? 1 : 0;
                        }
                        else
                        {
                            target.SeenProneByLower += prone ? 1 : 0;
                            target.SeenCrouchByLower += crouch ? 1 : 0;
                        }
                    }
                    if (slice.Elapsed.TotalMilliseconds >= FrameBudgetMilliseconds)
                    {
                        await UniTask.Yield(PlayerLoopTiming.Update);
                        slice.Restart();
                    }
                }

                // 4. A first score, to be tuned against the tester's ladder; the
                // CSV keeps every part so the weights can change offline.
                var groundY = AverageTerrain(center, 60f);
                foreach (var vantage in list)
                {
                    var proneSeen = vantage.SeenProneByHigher * 3f + vantage.SeenProneByLevel * 1.5f + vantage.SeenProneByLower * 0.5f;
                    var crouchSeen = vantage.SeenCrouchByHigher * 3f + vantage.SeenCrouchByLevel * 1.5f + vantage.SeenCrouchByLower * 0.5f;
                    var proneScore = -proneSeen * 0.6f - vantage.GroundSeesProne * 20f;
                    var crouchScore = -crouchSeen * 0.6f - vantage.GroundSeesCrouch * 20f + 5f;
                    vantage.BestPosture = proneScore >= crouchScore ? "deitado" : "agachado";
                    // Height leads (the tester's ladder goes by it), the view
                    // from the edge next, then who sees the body.
                    vantage.Score = (vantage.Position.y - groundY) * 1.0f
                        + vantage.EdgeView * 40f
                        - vantage.EdgeExposed * 10f
                        + Mathf.Max(proneScore, crouchScore)
                        + vantage.Cover * 8f;
                }
                list.Sort((left, right) => right.Score.CompareTo(left.Score));
                s_Vantages.Clear();
                s_Vantages.AddRange(list);
                Ready = true;

                var file = WriteCsv(map, areaName, list);
                var reachable = 0;
                foreach (var vantage in list)
                {
                    reachable += vantage.Reachable ? 1 : 0;
                }
                logger.LogInformation(
                    "Catalogo de pontos altos de {Area} pela {Mesh}: {Count} pontos ({Reachable} alcancaveis pela malha, {Covered} cobertos com laterais abertas, {Tops} topos de objeto, {Junk} descartados por serem veiculo/arvore/movel/objeto solto) de {Triangles} triangulos a ate {Radius:0}m, {Streets} pontos de rua; {Seconds:0.0}s ({Gather:0.0}s juntando). Arquivo: {File}.",
                    areaName,
                    newMesh ? "malha nova (com escadas)" : "malha dos zumbis (malha nova nao ficou pronta)",
                    list.Count,
                    reachable,
                    covered,
                    objectTops,
                    junk,
                    triangles,
                    AreaRadius,
                    streets.Count,
                    watch.Elapsed.TotalSeconds,
                    gather,
                    file);
                for (var i = 0; i < Mathf.Min(10, list.Count); i++)
                {
                    var vantage = list[i];
                    logger.LogInformation(
                        "Ponto alto #{Rank} em {Position} ({Surface}, {Rise:0}m acima do terreno, alcanca {Reachable}): nota {Score:0.0}, da borda ve {EdgeView:0}% da rua e {EdgeExposed:0}% da rua ve ele agachado na borda, do meio ve {View:0}%, visto por {Higher}/{Level}/{Lower} pontos mais altos/iguais/mais baixos {Posture}, cobertura {Cover:0}%.",
                        i + 1,
                        Format(vantage.Position),
                        vantage.Surface,
                        vantage.Rise,
                        vantage.Reachable,
                        vantage.Score,
                        vantage.EdgeView * 100f,
                        vantage.EdgeExposed * 100f,
                        vantage.View * 100f,
                        vantage.BestPosture == "deitado" ? vantage.SeenProneByHigher : vantage.SeenCrouchByHigher,
                        vantage.BestPosture == "deitado" ? vantage.SeenProneByLevel : vantage.SeenCrouchByLevel,
                        vantage.BestPosture == "deitado" ? vantage.SeenProneByLower : vantage.SeenCrouchByLower,
                        vantage.BestPosture,
                        vantage.Cover * 100f);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Catalogo de pontos altos falhou.");
            }
        }

        private static void MeasureAlone(Vantage vantage)
        {
            var p = vantage.Position;
            var seen = 0;
            var proneSeen = 0;
            var crouchSeen = 0;
            var standSeen = 0;
            var samples = 0;
            var cover = 0;
            for (var d = 0; d < Directions; d++)
            {
                var direction = Quaternion.Euler(0f, d * 360f / Directions, 0f) * Vector3.forward;
                if (Physics.Raycast(p + Vector3.up * 0.8f, direction, CoverReach, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    cover++;
                }
                var camera = p - direction * CameraBack + Vector3.up * CameraUp;
                foreach (var ring in s_Rings)
                {
                    var xz = p + direction * ring;
                    if (!TryTopSurface(xz, p.y + 60f, out var spot))
                    {
                        continue;
                    }
                    samples++;
                    if (Sees(camera, spot + Vector3.up * 0.5f))
                    {
                        seen++;
                    }
                    var eye = spot + Vector3.up * EyeHeight;
                    if (Sees(eye, p + Vector3.up * ProneBody))
                    {
                        proneSeen++;
                        crouchSeen++;
                        standSeen++;
                    }
                    else if (Sees(eye, p + Vector3.up * CrouchBody))
                    {
                        crouchSeen++;
                        standSeen++;
                    }
                    else if (Sees(eye, p + Vector3.up * StandBody))
                    {
                        standSeen++;
                    }
                }
            }
            var total = Mathf.Max(1, samples);
            vantage.View = seen / (float)total;
            vantage.GroundSeesProne = proneSeen / (float)total;
            vantage.GroundSeesCrouch = crouchSeen / (float)total;
            vantage.GroundSeesStand = standSeen / (float)total;
            vantage.Cover = cover / (float)Directions;
        }

        // From each of the 16 directions, walk out over the roof until the
        // floor drops or a wall stops it: that is where a player stands to
        // look down. Each street point is looked at from the edge facing it.
        private static void MeasureEdges(Vantage vantage, List<Vector3> streets)
        {
            var p = vantage.Position;
            var edges = new Vector3[Directions];
            for (var d = 0; d < Directions; d++)
            {
                var direction = Quaternion.Euler(0f, d * 360f / Directions, 0f) * Vector3.forward;
                var edge = p;
                // From 1m above the floor, not 3m: under a cabin roof a ray
                // from 3m hit the roof and the edge stayed at the bot's feet.
                for (var step = EdgeStep; step <= EdgeReach; step += EdgeStep)
                {
                    if (!TryTopSurface(p + direction * step, p.y + 1f, out var spot)
                        || Mathf.Abs(spot.y - p.y) > EdgeDrop
                        || !Sees(edge + Vector3.up * 0.5f, spot + Vector3.up * 0.5f))
                    {
                        break;
                    }
                    edge = spot;
                }
                edges[d] = edge;
            }
            var count = 0;
            var seen = 0;
            var exposed = 0;
            foreach (var street in streets)
            {
                var flat = street - p;
                flat.y = 0f;
                var gap = flat.magnitude;
                if (gap > StreetRange || gap < 5f)
                {
                    continue;
                }
                var angle = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
                var d = Mathf.RoundToInt(angle / (360f / Directions));
                d = ((d % Directions) + Directions) % Directions;
                var direction = Quaternion.Euler(0f, d * 360f / Directions, 0f) * Vector3.forward;
                var edge = edges[d];
                count++;
                var camera = edge - direction * CameraBack + Vector3.up * CameraUp;
                if (Sees(camera, street + Vector3.up * 0.5f))
                {
                    seen++;
                }
                if (Sees(street + Vector3.up * EyeHeight, edge + Vector3.up * CrouchBody))
                {
                    exposed++;
                }
            }
            vantage.Streets = count;
            vantage.EdgeView = count == 0 ? 0f : seen / (float)count;
            vantage.EdgeExposed = count == 0 ? 0f : exposed / (float)count;
        }

        private static int OpenSides(Vector3 floor)
        {
            var eye = floor + Vector3.up * EyeHeight;
            var open = 0;
            for (var d = 0; d < Directions; d++)
            {
                var direction = Quaternion.Euler(0f, d * 360f / Directions, 0f) * Vector3.forward;
                if (!Physics.Raycast(eye, direction, OpenSideReach, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
                {
                    open++;
                }
            }
            return open;
        }

        private static bool HasOpenSky(Vector3 floor)
        {
            return !Physics.Raycast(floor + Vector3.up * 0.5f, Vector3.up, SkyClear, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
        }

        private static bool IsJunkName(string name)
        {
            foreach (var prefix in s_JunkPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsStandingObject(BotMapObject mapObject)
        {
            switch (mapObject.Category)
            {
                case BotObjectCategory.Building:
                case BotObjectCategory.Other:
                case BotObjectCategory.Rock:
                    return !IsJunkName(mapObject.Name);
                default:
                    return false;
            }
        }

        // A roof, a bridge, the terrain: yes. A car, a tree, a couch, an AC
        // unit, something a player built: no.
        private static bool IsStandingSurface(Collider collider, string surface)
        {
            if (collider is TerrainCollider)
            {
                return true;
            }
            var layer = collider.gameObject.layer;
            if (layer == LayerMasks.RESOURCE || layer == LayerMasks.VEHICLE || layer == LayerMasks.BARRICADE
                || layer == LayerMasks.STRUCTURE || layer == LayerMasks.ITEM || layer == LayerMasks.PLAYER
                || collider.GetComponentInParent<InteractableVehicle>() != null)
            {
                return false;
            }
            if (SkaliaBotObjectMap.TryGetByCollider(collider, out var mapObject))
            {
                return IsStandingObject(mapObject);
            }
            return !IsJunkName(surface);
        }

        private static bool Sees(Vector3 from, Vector3 to)
        {
            return !Physics.Linecast(from, to, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore);
        }

        // The floor under a navmesh point and what it is (the root object).
        private static bool TryFloor(Vector3 point, out Vector3 floor, out string surface, out Collider? collider)
        {
            floor = point;
            surface = string.Empty;
            collider = null;
            if (!Physics.Raycast(point + Vector3.up * 1.0f, Vector3.down, out var hit, 2.0f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            floor = hit.point;
            collider = hit.collider;
            surface = DescribeSurface(hit.collider);
            return true;
        }

        // Where someone could stand at that spot: the first surface from above.
        private static bool TryTopSurface(Vector3 xz, float fromY, out Vector3 spot)
        {
            spot = xz;
            if (!Physics.Raycast(new Vector3(xz.x, fromY, xz.z), Vector3.down, out var hit, 200f, RayMasks.BLOCK_COLLISION, QueryTriggerInteraction.Ignore)
                || WaterUtility.isPointUnderwater(hit.point + Vector3.up * 0.3f))
            {
                return false;
            }
            spot = hit.point;
            return true;
        }

        private static string DescribeSurface(Collider collider)
        {
            if (collider is TerrainCollider)
            {
                return "terreno";
            }
            var name = collider.transform.root != null ? collider.transform.root.name : collider.name;
            name = name.Replace("(Clone)", string.Empty).Trim();
            return name.Length == 0 ? "?" : name.Replace(';', '_').Replace(',', '_');
        }

        private static float AverageTerrain(Vector3 point, float meters)
        {
            var sum = 0f;
            for (var d = 0; d < 8; d++)
            {
                var direction = Quaternion.Euler(0f, d * 45f, 0f) * Vector3.forward;
                sum += LevelGround.getHeight(point + direction * meters);
            }
            return sum / 8f;
        }

        private static long StreetKey(Vector3 point)
        {
            var x = (long)Mathf.FloorToInt(point.x / StreetCell);
            var z = (long)Mathf.FloorToInt(point.z / StreetCell);
            return ((x & 0xFFFFF) << 20) | (z & 0xFFFFF);
        }

        private static long CellKey(Vector3 point)
        {
            var x = (long)Mathf.FloorToInt(point.x / ClusterCell);
            var z = (long)Mathf.FloorToInt(point.z / ClusterCell);
            var y = (long)Mathf.FloorToInt(point.y / ClusterBand);
            return ((x & 0xFFFFF) << 40) | ((z & 0xFFFFF) << 20) | (y & 0xFFFFF);
        }

        private static string Format(Vector3 point)
        {
            return "(" + point.x.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + point.y.ToString("0.0", CultureInfo.InvariantCulture) + ", "
                + point.z.ToString("0.0", CultureInfo.InvariantCulture) + ")";
        }

        private static string WriteCsv(string map, string areaName, List<Vantage> list)
        {
            var path = System.IO.Path.Combine(
                ReadWrite.PATH,
                "Servers",
                Provider.serverID,
                "SkaliaPontosAltos_" + map + "_" + areaName.Replace(' ', '_') + ".csv");
            var builder = new StringBuilder();
            builder.AppendLine("rank;x;y;z;acima_terreno_30m;acima_chao;superficie;alcanca;nota;postura;visao_borda;borda_exposta;pontos_rua;coberto;lados_abertos;visao_3p;chao_ve_deitado;chao_ve_agachado;chao_ve_em_pe;cobertura;pontos_mais_altos;visto_deitado_por_mais_altos;visto_deitado_por_iguais;visto_deitado_por_mais_baixos;visto_agachado_por_mais_altos;visto_agachado_por_iguais;visto_agachado_por_mais_baixos");
            var inv = CultureInfo.InvariantCulture;
            for (var i = 0; i < list.Count; i++)
            {
                var v = list[i];
                builder.Append(i + 1).Append(';')
                    .Append(v.Position.x.ToString("0.0", inv)).Append(';')
                    .Append(v.Position.y.ToString("0.0", inv)).Append(';')
                    .Append(v.Position.z.ToString("0.0", inv)).Append(';')
                    .Append(v.Rise.ToString("0.0", inv)).Append(';')
                    .Append(v.AboveGround.ToString("0.0", inv)).Append(';')
                    .Append(v.Surface).Append(';')
                    .Append(v.Reachable ? 1 : 0).Append(';')
                    .Append(v.Score.ToString("0.0", inv)).Append(';')
                    .Append(v.BestPosture).Append(';')
                    .Append(v.EdgeView.ToString("0.000", inv)).Append(';')
                    .Append(v.EdgeExposed.ToString("0.000", inv)).Append(';')
                    .Append(v.Streets).Append(';')
                    .Append(v.Covered ? 1 : 0).Append(';')
                    .Append(v.OpenSides).Append(';')
                    .Append(v.View.ToString("0.000", inv)).Append(';')
                    .Append(v.GroundSeesProne.ToString("0.000", inv)).Append(';')
                    .Append(v.GroundSeesCrouch.ToString("0.000", inv)).Append(';')
                    .Append(v.GroundSeesStand.ToString("0.000", inv)).Append(';')
                    .Append(v.Cover.ToString("0.000", inv)).Append(';')
                    .Append(v.Higher).Append(';')
                    .Append(v.SeenProneByHigher).Append(';')
                    .Append(v.SeenProneByLevel).Append(';')
                    .Append(v.SeenProneByLower).Append(';')
                    .Append(v.SeenCrouchByHigher).Append(';')
                    .Append(v.SeenCrouchByLevel).Append(';')
                    .Append(v.SeenCrouchByLower).AppendLine();
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            return path;
        }
    }
}
