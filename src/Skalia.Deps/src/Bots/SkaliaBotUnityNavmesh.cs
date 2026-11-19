using System;
using System.Collections.Generic;
using System.Diagnostics;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SDG.Framework.Landscapes;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using UnityEngine.AI;
using Unity.Jobs.LowLevel.Unsafe;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // A second navmesh, baked by Unity from the colliders the player really
    // collides with, sized to the player's body. In 8 test areas its routes
    // crossed a wall in 0-2 of 40 trips where the zombie mesh crossed one in
    // 8-15. It is baked in 102m pieces around the bots, one piece at a time,
    // and only used where it has a complete route; everything else keeps the
    // zombie mesh.
    internal static partial class SkaliaBotUnityNavmesh
    {
        // The player is a 0.4m-radius, 2m capsule (PlayerStance.RADIUS,
        // PlayerMovement.HEIGHT_STAND). A 0.32m body let routes through
        // 0.64m gaps the player can't fit: in the 0.1.115 test the bots that
        // still circled on the new mesh stood ~1.4m from a route corner past
        // a sofa, a clothes rack or a wall corner, asked for a route and got
        // the same one. With 0.2m voxels the 0.4m body rounded up to 0.6m and
        // shut every kitchen gap under ~1.2m (House_12 at -977,-740); with
        // 0.4/3m voxels it is exactly 3 voxels, so gaps of 0.8m stay open.
        private const float VoxelSize = 0.4f / 3f;
        private const float BodyRadius = 0.4f;
        // 384 voxels keep the pieces at 102m (and the link count the same).
        private const int TileVoxels = 384;
        private const float ChunkSize = TileVoxels * VoxelSize * 2f;
        // Drops off a ledge the bot may take on a route: the 2.5m a route
        // going down already allows, above the 0.4m step.
        private const float MinDropMeters = 0.6f;
        private const float MaxDropMeters = 2.4f;
        private const float MinBakeIntervalSeconds = 0.75f;
        private const float BottomY = -60f;
        private const float TopY = 340f;
        private const float MaxRouteMeters = 260f;
        private const float SourceMargin = 3f;
        // Joins between neighbouring pieces: one every 3m of the shared edge.
        private const float LinkSpacing = 3f;
        private const float LinkWidth = 3f;
        private const float LinkReach = 0.35f;
        private const float TerrainStep = 1f;

        // What the mesh is baked from: every layer the player's body really
        // collides with, read from the game's own collision table at start
        // (BodyMask). This list is only the fallback; it was the only source
        // up to 0.1.123 and is the bullet list, not the body one: it leaves
        // out SMALL, where the cardboard boxes are.
        private static int SourceMask = RayMasks.BLOCK_COLLISION | RayMasks.GROUND | RayMasks.GROUND2;
        private static readonly int MovingMask = RayMasks.PLAYER | RayMasks.ENEMY | RayMasks.AGENT | RayMasks.VEHICLE;

        // Unity drops every tile outside the box of an update, and rebuilding
        // one mesh with all pieces got slower with each piece (911 ms at 22).
        // Each piece is its own mesh instead, joined to its neighbours by
        // short links along the shared edge.
        private static readonly Dictionary<long, NavMeshDataInstance> s_Pieces = new Dictionary<long, NavMeshDataInstance>();
        private static readonly List<NavMeshData> s_PieceData = new List<NavMeshData>();
        private static readonly List<NavMeshLinkInstance> s_Links = new List<NavMeshLinkInstance>();
        private static readonly HashSet<long> s_Queued = new HashSet<long>();
        private static readonly Queue<long> s_Queue = new Queue<long>();
        private static readonly List<NavMeshBuildSource> s_Sources = new List<NavMeshBuildSource>();
        private static readonly List<NavMeshBuildMarkup> s_NoMarkups = new List<NavMeshBuildMarkup>();
        private static NavMeshPath? s_Path;
        private static NavMeshBuildSettings s_Settings;
        private static NavMeshQueryFilter s_Filter;
        private static bool s_Started;
        private static ILogger? s_Logger;
        private static float s_NextOffMeshLogAt;
        // Ladders from the ladder catalogue, as one-way links foot to top.
        private sealed class LadderLink
        {
            public BotLadder Ladder = null!;
            public Vector3 Start;
            public Vector3 End;
        }
        private static readonly List<LadderLink> s_LadderLinks = new List<LadderLink>();
        private static readonly HashSet<int> s_LinkedLadders = new HashSet<int>();
        private static string? s_Map;
        private static int s_Generation;

        private static double s_BakeMilliseconds;
        private static double s_WorstBakeMilliseconds;
        private static double s_LastBakeMilliseconds;
        private static int s_Baked;
        private static int s_Routes;
        private static int s_Fallbacks;
        private static int s_NotReady, s_TooFar, s_NoSample, s_Incomplete, s_Underwater, s_Drops, s_CornersPulled, s_LegsBent;
        private static int s_StartAcross;
        private static float s_NextStartLogAt;

        public static bool Enabled { get; set; } = true;

        // One mesh for the whole map (0.1.164). The 102m pieces were separate
        // meshes joined by links: Unity's path search crossed the links, but
        // a raycast, a nearest-floor search or an edge search stopped at
        // every border as if it were a wall. In the 0.1.162 Washington test
        // 75 of 113 circles and 36 of 80 wall stops were within 1.5m of a
        // border (6% of the map), and 350 of 622 "not in a straight line"
        // route drops. Tiles of one mesh are stitched by Unity itself.
        private static NavMeshDataInstance s_WholeInstance;
        private static NavMeshData? s_WholeData;
        private static bool s_Whole;
        private static bool s_WholeBuilding;
        private static bool s_WholeFailed;
        private static string s_WholeStatus = "aguardando o mapa";
        public static bool WholeMesh => s_Whole;

        private static bool HasPiece(long key) => s_Whole || s_Pieces.ContainsKey(key);

        public static int ReadyChunks => s_Whole ? s_MapPieces : s_Pieces.Count;
        public static bool MapFilled => s_Started && s_FillDoneLogged;
        public static int QueuedChunks => s_Queue.Count;

        public static void EnsureStarted(ILogger logger)
        {
            var map = Level.info?.name;
            if (map == null || s_Map == map)
            {
                return;
            }
            s_Map = map;
            foreach (var link in s_Links)
            {
                NavMesh.RemoveLink(link);
            }
            s_Links.Clear();
            ClearPortals();
            s_LinkLimitLogged = false;
            foreach (var block in s_Blocks)
            {
                if (block.Go != null)
                {
                    UnityEngine.Object.Destroy(block.Go);
                }
            }
            s_Blocks.Clear();
            s_LadderLinks.Clear();
            s_LinkedLadders.Clear();
            ClearShortcuts();
            foreach (var piece in s_Pieces.Values)
            {
                NavMesh.RemoveNavMeshData(piece);
            }
            s_Pieces.Clear();
            foreach (var data in s_PieceData)
            {
                UnityEngine.Object.Destroy(data);
            }
            s_PieceData.Clear();
            s_Queued.Clear();
            s_Queue.Clear();
            s_SeaPieces.Clear();
            s_FillDoneLogged = false;
            if (s_Whole)
            {
                NavMesh.RemoveNavMeshData(s_WholeInstance);
            }
            if (s_WholeData != null)
            {
                UnityEngine.Object.Destroy(s_WholeData);
                s_WholeData = null;
            }
            s_Whole = false;
            s_WholeBuilding = false;
            s_WholeFailed = false;
            s_GoalFailStarts.Clear();
            s_Settings = NavMesh.CreateSettings();
            s_Settings.agentRadius = BodyRadius;
            s_Settings.agentHeight = 2f;
            // The player's CharacterController steps 0.4m and walks slopes up
            // to the level's Max_Walkable_Slope (59 degrees by default).
            s_Settings.agentClimb = 0.4f;
            s_Settings.agentSlope = Level.info?.configData != null && Level.info.configData.Max_Walkable_Slope > -0.5f
                ? Mathf.Min(60f, Level.info.configData.Max_Walkable_Slope)
                : 59f;
            s_Settings.overrideVoxelSize = true;
            s_Settings.voxelSize = VoxelSize;
            s_Settings.overrideTileSize = true;
            s_Settings.tileSize = TileVoxels;
            s_Filter = new NavMeshQueryFilter { agentTypeID = s_Settings.agentTypeID, areaMask = NavMesh.AllAreas };
            s_Path = new NavMeshPath();
            s_Logger = logger;
            UseBodyMask(logger);
            s_Started = true;
            logger.LogInformation("Malha nova: iniciando a geracao para {Map}.", map);
            PumpAsync(logger, ++s_Generation).Forget();
            HitchMonitorAsync(s_Generation).Forget();
            BuildWholeAsync(logger, s_Generation).Forget();
        }

        private static void UseBodyMask(ILogger logger)
        {
            var body = 0;
            for (var layer = 0; layer < 32; layer++)
            {
                if (!Physics.GetIgnoreLayerCollision(LayerMasks.PLAYER, layer))
                {
                    body |= 1 << layer;
                }
            }
            body &= ~MovingMask;
            body |= RayMasks.GROUND | RayMasks.GROUND2;
            var old = RayMasks.BLOCK_COLLISION | RayMasks.GROUND | RayMasks.GROUND2;
            SourceMask = body;
            logger.LogInformation(
                "Malha nova: gerada com as camadas que batem no corpo do jogador: {Layers}. Entraram em relacao a lista antiga: {Added}. Sairam: {Removed}.",
                LayerNames(body), LayerNames(body & ~old), LayerNames(old & ~body));
        }

        private static string LayerNames(int mask)
        {
            var names = new System.Text.StringBuilder();
            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0)
                {
                    continue;
                }
                var name = LayerMask.LayerToName(layer);
                names.Append(names.Length > 0 ? ", " : string.Empty)
                    .Append(string.IsNullOrEmpty(name) ? "camada" : name).Append(' ').Append(layer);
            }
            return names.Length > 0 ? names.ToString() : "nenhuma";
        }

        // Server frames longer than 100ms, for the minute report: a bot that
        // seems to lag on a local server is the whole server stopping.
        private static int s_Hitches, s_HitchesBaking, s_BakeFrame = -10;
        private static double s_WorstHitch;

        private static async UniTaskVoid HitchMonitorAsync(int generation)
        {
            await UniTask.SwitchToMainThread();
            var last = Time.realtimeSinceStartup;
            while (generation == s_Generation)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                var now = Time.realtimeSinceStartup;
                var frame = (now - last) * 1000.0;
                last = now;
                if (frame >= 100.0)
                {
                    s_Hitches++;
                    s_WorstHitch = Math.Max(s_WorstHitch, frame);
                    if (Time.frameCount - s_BakeFrame <= 1)
                    {
                        s_HitchesBaking++;
                    }
                }
            }
        }

        private static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        private static int Cell(float value) => Mathf.FloorToInt(value / ChunkSize);

        // Queue the pieces within `radius` pieces of a point.
        public static void Want(Vector3 position, int radius)
        {
            if (!s_Started || s_Whole || s_WholeBuilding)
            {
                return;
            }
            var cx = Cell(position.x);
            var cz = Cell(position.z);
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    var key = Key(cx + dx, cz + dz);
                    if (!s_Pieces.ContainsKey(key) && s_Queued.Add(key))
                    {
                        s_Queue.Enqueue(key);
                    }
                }
            }
        }

        private static async UniTaskVoid PumpAsync(ILogger logger, int generation)
        {
            await UniTask.SwitchToMainThread();
            var nextReport = Time.realtimeSinceStartup + 60f;
            while (generation == s_Generation)
            {
                // At most a quarter of the server's time goes into baking.
                var wait = Math.Max(MinBakeIntervalSeconds, s_LastBakeMilliseconds * 4.0 / 1000.0);
                await UniTask.Delay(TimeSpan.FromSeconds(wait));
                if (generation != s_Generation)
                {
                    return;
                }
                try
                {
                    if (s_Whole || s_WholeBuilding || (!s_WholeFailed && !Level.isLoaded))
                    {
                        // The whole mesh replaces the pieces; they come back
                        // only if it fails.
                    }
                    else if (s_Queue.Count > 0)
                    {
                        var key = s_Queue.Dequeue();
                        s_Queued.Remove(key);
                        await BakeAsync(key, generation);
                    }
                    else if (FillMap(logger, out var fill))
                    {
                        await BakeAsync(fill, generation);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Malha nova: erro gerando um pedaco.");
                }
                if (Time.realtimeSinceStartup >= nextReport && (s_Whole || s_WholeBuilding))
                {
                    nextReport = Time.realtimeSinceStartup + 60f;
                    logger.LogInformation(
                        "Malha nova: {Status}; memoria do servidor {Memory:0} MB; rotas no ultimo minuto: {Routes} pela malha nova, {Fallbacks} pela malha dos zumbis (longe {TooFar}, fora da malha {NoSample}, inicio atras de parede ou em outro andar {StartAcross}, sem rota completa {Incomplete}, agua {Underwater}; com descida de beirada {Drops}); travadas do servidor acima de 100 ms: {Hitches} (pior {WorstHitch:0} ms); cortes na malha {Blocks}; quinas afastadas da parede {Pulled}, trechos dobrados {Bent}.",
                        s_WholeStatus,
                        UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024.0 * 1024.0),
                        s_Routes,
                        s_Fallbacks,
                        s_TooFar,
                        s_NoSample,
                        s_StartAcross,
                        s_Incomplete,
                        s_Underwater,
                        s_Drops,
                        s_Hitches,
                        s_WorstHitch,
                        BlockCount,
                        s_CornersPulled,
                        s_LegsBent);
                    s_CornersPulled = 0;
                    s_LegsBent = 0;
                    s_Hitches = s_HitchesBaking = 0;
                    s_WorstHitch = 0.0;
                    s_DetoursUnity = s_DetoursZombie = 0;
                    s_Routes = 0;
                    s_Fallbacks = 0;
                    s_NotReady = s_TooFar = s_NoSample = s_StartAcross = s_Incomplete = s_Underwater = s_Drops = 0;
                }
                else if (Time.realtimeSinceStartup >= nextReport && s_Baked == 0)
                {
                    nextReport = Time.realtimeSinceStartup + 60f;
                    logger.LogWarning(
                        "Malha nova: nenhum pedaco pronto ainda ({Queued} na fila); as rotas estao saindo da malha dos zumbis.",
                        s_Queue.Count);
                }
                else if (Time.realtimeSinceStartup >= nextReport)
                {
                    nextReport = Time.realtimeSinceStartup + 60f;
                    logger.LogInformation(
                        "Malha nova: {Ready} pedacos de {Size:0}m prontos, {Queued} na fila, {Links} ligacoes entre pedacos ({Ladders} escadas); geracao media {Average:0} ms (pior {Worst:0} ms, ultimo {Last:0} ms); mapa inteiro {Filled}/{MapPieces} ({Skipped} so mar); memoria do servidor {Memory:0} MB; rotas no ultimo minuto: {Routes} pela malha nova, {Fallbacks} pela malha dos zumbis{Off} (pedaco nao pronto {NotReady}, longe {TooFar}, fora da malha {NoSample}, inicio atras de parede ou em outro andar {StartAcross}, sem rota completa {Incomplete}, agua {Underwater}; com descida de beirada {Drops}); desvios locais: {DetoursUnity} em rota da malha nova, {DetoursZombie} em rota da malha dos zumbis; travadas do servidor acima de 100 ms: {Hitches} (pior {WorstHitch:0} ms, {HitchesBaking} gerando a malha); cortes na malha {Blocks}.",
                        s_Pieces.Count,
                        ChunkSize,
                        s_Queue.Count,
                        s_Links.Count,
                        s_LadderLinks.Count,
                        s_BakeMilliseconds / s_Baked,
                        s_WorstBakeMilliseconds,
                        s_LastBakeMilliseconds,
                        s_Pieces.Count + s_SeaPieces.Count,
                        s_MapPieces,
                        s_SeaPieces.Count,
                        UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024.0 * 1024.0),
                        s_Routes,
                        s_Fallbacks,
                        Enabled ? string.Empty : " (desligada)",
                        s_NotReady,
                        s_TooFar,
                        s_NoSample,
                        s_StartAcross,
                        s_Incomplete,
                        s_Underwater,
                        s_Drops,
                        s_DetoursUnity,
                        s_DetoursZombie,
                        s_Hitches,
                        s_WorstHitch,
                        s_HitchesBaking,
                        BlockCount);
                    if (s_Routes > 0)
                    {
                        logger.LogInformation(
                            "Malha nova: {Pulled} quina(s) de rota afastada(s) da parede e {Bent} trecho(s) dobrado(s) longe de colisor que o corpo tocaria, em {Routes} rota(s) no ultimo minuto.",
                            s_CornersPulled,
                            s_LegsBent,
                            s_Routes);
                    }
                    s_CornersPulled = 0;
                    s_LegsBent = 0;
                    s_Hitches = s_HitchesBaking = 0;
                    s_WorstHitch = 0.0;
                    s_DetoursUnity = s_DetoursZombie = 0;
                    s_Routes = 0;
                    s_Fallbacks = 0;
                    s_NotReady = s_TooFar = s_NoSample = s_StartAcross = s_Incomplete = s_Underwater = s_Drops = 0;
                }
            }
        }

        // With nothing asked for, the rest of the map is baked too, the piece
        // nearest a player first, so later routes find it ready. A piece of
        // open sea is skipped. The fill stops if the server grows 3 GB.
        private const long FillMemoryLimitBytes = 3L * 1024 * 1024 * 1024;
        private static readonly HashSet<long> s_SeaPieces = new HashSet<long>();
        private static int s_MapPieces;
        private static long s_FillStartMemory;
        private static bool s_FillStopped;
        private static bool s_FillDoneLogged;

        private static bool FillMap(ILogger logger, out long next)
        {
            next = 0L;
            if (s_FillStopped || !Level.isLoaded)
            {
                return false;
            }
            // The process working set reads 0 on this server; Unity's own
            // count includes the native navmesh data.
            var memory = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
            if (s_FillStartMemory == 0)
            {
                s_FillStartMemory = memory;
            }
            if (memory - s_FillStartMemory > FillMemoryLimitBytes)
            {
                s_FillStopped = true;
                logger.LogWarning("Malha nova: geracao do mapa inteiro parada em {Ready} pedacos; o servidor cresceu {Grown:0} MB.",
                    s_Pieces.Count, (memory - s_FillStartMemory) / (1024.0 * 1024.0));
                return false;
            }
            var half = Level.size / 2f - Level.border;
            var low = Cell(-half);
            var high = Cell(half - 0.01f);
            s_MapPieces = (high - low + 1) * (high - low + 1);
            var best = 0L;
            var bestDistance = float.MaxValue;
            for (var x = low; x <= high; x++)
            {
                for (var z = low; z <= high; z++)
                {
                    var key = Key(x, z);
                    if (s_Pieces.ContainsKey(key) || s_SeaPieces.Contains(key))
                    {
                        continue;
                    }
                    var center = new Vector3((x + 0.5f) * ChunkSize, 0f, (z + 0.5f) * ChunkSize);
                    var distance = float.MaxValue;
                    foreach (var client in Provider.clients)
                    {
                        if (client?.player != null)
                        {
                            distance = Mathf.Min(distance, (client.player.transform.position - center).GetHorizontalMagnitude());
                        }
                    }
                    if (distance == float.MaxValue)
                    {
                        distance = center.GetHorizontalMagnitude();
                    }
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = key;
                    }
                }
            }
            if (bestDistance == float.MaxValue)
            {
                if (!s_FillDoneLogged)
                {
                    s_FillDoneLogged = true;
                    logger.LogInformation("Malha nova: mapa inteiro pronto ({Ready} pedacos, {Sea} so mar).", s_Pieces.Count, s_SeaPieces.Count);
                    AuditObjectsAsync(logger, s_Generation).Forget();
                    AuditLinks(logger);
                }
                return false;
            }
            if (IsOpenSea(best))
            {
                s_SeaPieces.Add(best);
                return false;
            }
            next = best;
            return true;
        }

        private static bool IsOpenSea(long key)
        {
            var cx = (int)(key >> 32);
            var cz = (int)(uint)key;
            for (var ix = 0; ix <= 4; ix++)
            {
                for (var iz = 0; iz <= 4; iz++)
                {
                    var point = new Vector3((cx + ix / 4f) * ChunkSize, 0f, (cz + iz / 4f) * ChunkSize);
                    if (!Landscape.getWorldHeight(point, out var height))
                    {
                        continue;
                    }
                    point.y = height + 0.5f;
                    if (!WaterUtility.isPointUnderwater(point))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        // BuildNavMeshData held the server's main thread 150 to 500ms per
        // piece, a freeze every 1.2s for the half hour the whole map takes:
        // players, zombies and bots all jumped as if lagging. The voxels are
        // now built on Unity's worker threads; the main thread only collects
        // the sources and adds the links.
        private const float AsyncBakeTimeoutSeconds = 10f;
        private const float WholeStallSeconds = 120f;
        private static bool s_UseDirectWhole;
        private const float WholeTimeoutSeconds = 1800f;

        private static async UniTaskVoid BuildWholeAsync(ILogger logger, int generation)
        {
            await UniTask.SwitchToMainThread();
            s_WholeBuilding = true;
            try
            {
                while (!Level.isLoaded && generation == s_Generation)
                {
                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
                // Let the server finish starting before the heavy part.
                await UniTask.Delay(TimeSpan.FromSeconds(5));
                if (generation != s_Generation)
                {
                    return;
                }
                // The background build never finished on this server (0.1.113:
                // not a piece in 14 min; 0.1.162: the first piece over 10s),
                // which is what a job system with no worker threads does.
                var workers = JobsUtility.JobWorkerCount;
                var maximum = JobsUtility.JobWorkerMaximumCount;
                var wanted = Math.Min(maximum, Math.Max(2, SystemInfo.processorCount - 2));
                if (workers < 2 && wanted > workers)
                {
                    try
                    {
                        JobsUtility.JobWorkerCount = wanted;
                    }
                    catch (Exception exception)
                    {
                        logger.LogWarning("Malha nova: nao foi possivel ligar as threads de trabalho da Unity: {Error}", exception.Message);
                    }
                }
                logger.LogInformation(
                    "Malha nova: threads de trabalho da Unity: {Before} no inicio, {After} agora (maximo {Max}, processadores {Cpus}).",
                    workers, JobsUtility.JobWorkerCount, maximum, SystemInfo.processorCount);

                var watch = Stopwatch.StartNew();
                var half = Level.size / 2f - Level.border;
                var low = Cell(-half);
                var high = Cell(half - 0.01f);
                s_MapPieces = (high - low + 1) * (high - low + 1);
                var min = new Vector3(low * ChunkSize, BottomY, low * ChunkSize);
                var size = new Vector3((high - low + 1) * ChunkSize, TopY - BottomY, (high - low + 1) * ChunkSize);
                var center = min + size * 0.5f;
                var collect = new Bounds(center, size + new Vector3(SourceMargin * 2f, 0f, SourceMargin * 2f));
                s_WholeStatus = "malha unica: juntando o mapa";
                var sources = new List<NavMeshBuildSource>();
                NavMeshBuilder.CollectSources(collect, SourceMask, NavMeshCollectGeometry.PhysicsColliders, 0, s_NoMarkups, sources);
                sources.RemoveAll(source =>
                    source.shape == NavMeshBuildSourceShape.Terrain
                    || (source.component is Component component
                        && component != null
                        && (((1 << component.gameObject.layer) & MovingMask) != 0
                            || (component is Collider collider && collider.isTrigger)
                            || IsDoor(component))));
                var colliders = sources.Count;
                // Unity skips a mesh it can't read ("Source mesh Collision is
                // skipped because it does not allow read access", 6 times in
                // the 0.1.164 start): that object has no wall in the mesh.
                var unreadable = new Dictionary<string, int>();
                foreach (var source in sources)
                {
                    if (source.shape == NavMeshBuildSourceShape.Mesh
                        && source.sourceObject is Mesh sourceMesh
                        && sourceMesh != null
                        && !sourceMesh.isReadable)
                    {
                        var owner = source.component != null ? source.component.transform.root.name : "?";
                        var where = source.component != null ? FormatPoint(source.component.transform.position) : string.Empty;
                        var name = owner + " (" + sourceMesh.name + ")";
                        unreadable.TryGetValue(name, out var seen);
                        unreadable[name] = seen + 1;
                        if (seen == 0 && unreadable.Count <= 12)
                        {
                            logger.LogInformation("Malha nova: colisao ilegivel fica fora da malha: {Name} em {Where}.", name, where);
                        }
                    }
                }
                // The terrain surface in piece-sized meshes; open sea left out
                // as the pieces did.
                var terrains = new List<Mesh>();
                var sea = 0;
                for (var x = low; x <= high; x++)
                {
                    for (var z = low; z <= high; z++)
                    {
                        if (IsOpenSea(Key(x, z)))
                        {
                            sea++;
                            continue;
                        }
                        var terrain = BuildTerrainMesh(x * ChunkSize - SourceMargin, z * ChunkSize - SourceMargin, ChunkSize + SourceMargin * 2f);
                        if (terrain == null)
                        {
                            continue;
                        }
                        terrains.Add(terrain);
                        sources.Add(new NavMeshBuildSource
                        {
                            shape = NavMeshBuildSourceShape.Mesh,
                            sourceObject = terrain,
                            transform = Matrix4x4.identity,
                            area = 0
                        });
                    }
                }
                AddWaterSources(collect, sources);
                var collectMs = watch.Elapsed.TotalMilliseconds;
                logger.LogInformation(
                    "Malha nova: montando uma malha unica para o mapa inteiro ({Size:0}m x {Size:0}m): {Colliders} colisores ({Unreadable} ilegiveis), {Terrains} trechos de terreno ({Sea} so mar), juntados em {Ms:0} ms.",
                    size.x, size.z, colliders, unreadable.Count, terrains.Count, sea, collectMs);

                NavMeshData? data = null;
                var mode = "em segundo plano";
                var bounds = new Bounds(center, size);
                watch.Restart();
                // 0.1.164: the background build held the server 24s when
                // started, read 100% and never finished; the direct build
                // took 21s. One stop of ~21s beats two.
                s_UseDirectWhole = true;
                var pending = s_UseDirectWhole ? null : new NavMeshData(s_Settings.agentTypeID);
                var operation = pending == null ? null : NavMeshBuilder.UpdateNavMeshDataAsync(pending, s_Settings, sources, bounds);
                var lastProgress = -1f;
                var lastProgressAt = Time.realtimeSinceStartup;
                var nextLog = Time.realtimeSinceStartup + 30f;
                var stalled = false;
                while (operation != null && !operation.isDone)
                {
                    await UniTask.Yield();
                    if (generation != s_Generation)
                    {
                        return;
                    }
                    var now = Time.realtimeSinceStartup;
                    if (operation.progress > lastProgress + 0.0001f)
                    {
                        lastProgress = operation.progress;
                        lastProgressAt = now;
                    }
                    s_WholeStatus = "malha unica: montando em segundo plano, " + (operation.progress * 100f).ToString("0") + "% em " + watch.Elapsed.TotalSeconds.ToString("0") + "s";
                    if (now >= nextLog)
                    {
                        nextLog = now + 30f;
                        logger.LogInformation("Malha nova: {Status}.", s_WholeStatus);
                    }
                    if (now - lastProgressAt > WholeStallSeconds || watch.Elapsed.TotalSeconds > WholeTimeoutSeconds)
                    {
                        stalled = true;
                        break;
                    }
                }
                if (!stalled && operation != null)
                {
                    data = pending;
                }
                else if (operation == null)
                {
                    logger.LogInformation("Malha nova: montando a malha unica direto; o servidor fica parado uns 20 a 30 segundos.");
                    s_WholeStatus = "malha unica: montando direto";
                    await UniTask.Yield();
                    mode = "direto (servidor parado)";
                    watch.Restart();
                    data = NavMeshBuilder.BuildNavMeshData(s_Settings, sources, bounds, Vector3.zero, Quaternion.identity);
                }
                else
                {
                    logger.LogWarning(
                        "Malha nova: a montagem em segundo plano parou em {Progress:0}% ({Seconds:0}s sem avancar); montando direto. O servidor fica parado ate terminar (alguns minutos).",
                        Mathf.Max(0f, lastProgress) * 100f, Time.realtimeSinceStartup - lastProgressAt);
                    s_WholeStatus = "malha unica: montando direto";
                    await UniTask.Yield();
                    mode = "direto (servidor parado)";
                    watch.Restart();
                    data = NavMeshBuilder.BuildNavMeshData(s_Settings, sources, bounds, Vector3.zero, Quaternion.identity);
                }
                var buildSeconds = watch.Elapsed.TotalSeconds;
                foreach (var terrain in terrains)
                {
                    UnityEngine.Object.Destroy(terrain);
                }
                if (generation != s_Generation || data == null)
                {
                    if (data != null)
                    {
                        UnityEngine.Object.Destroy(data);
                    }
                    if (data == null)
                    {
                        throw new InvalidOperationException("a Unity nao devolveu a malha");
                    }
                    return;
                }
                watch.Restart();
                // Pieces baked before it (none unless it failed once) go.
                foreach (var link in s_Links)
                {
                    NavMesh.RemoveLink(link);
                }
                s_Links.Clear();
                s_LadderLinks.Clear();
                s_LinkedLadders.Clear();
                ClearShortcuts();
                ClearPortals();
                foreach (var piece in s_Pieces.Values)
                {
                    NavMesh.RemoveNavMeshData(piece);
                }
                s_Pieces.Clear();
                foreach (var old in s_PieceData)
                {
                    UnityEngine.Object.Destroy(old);
                }
                s_PieceData.Clear();
                s_Queue.Clear();
                s_Queued.Clear();
                s_WholeData = data;
                s_WholeInstance = NavMesh.AddNavMeshData(data);
                s_Whole = true;
                LinkLadders();
                var addMs = watch.Elapsed.TotalMilliseconds;
                s_WholeStatus = "malha unica pronta (" + mode + ", " + buildSeconds.ToString("0") + "s)";
                s_FillDoneLogged = true;
                logger.LogInformation(
                    "Malha nova: malha unica pronta para o mapa inteiro, sem emendas: montada {Mode} em {Seconds:0}s, adicionada em {Add:0} ms; {Ladders} escadas ligadas; memoria do servidor {Memory:0} MB.",
                    mode, buildSeconds, addMs, s_LadderLinks.Count,
                    UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() / (1024.0 * 1024.0));
                AuditObjectsAsync(logger, s_Generation).Forget();
            }
            catch (Exception exception)
            {
                s_WholeFailed = true;
                s_WholeStatus = "malha unica falhou";
                logger.LogError(exception, "Malha nova: a malha unica falhou; voltando aos pedacos de 102m.");
            }
            finally
            {
                s_WholeBuilding = false;
            }
        }
        private static bool s_AsyncBakeBroken;

        private static async UniTask BakeAsync(long key, int generation)
        {
            // Asked for again while it was building: already there.
            if (s_Pieces.ContainsKey(key))
            {
                return;
            }
            var watch = Stopwatch.StartNew();
            var cx = (int)(key >> 32);
            var cz = (int)(uint)key;
            var min = new Vector3(cx * ChunkSize, BottomY, cz * ChunkSize);
            var size = new Vector3(ChunkSize, TopY - BottomY, ChunkSize);
            var center = min + size * 0.5f;
            var collect = new Bounds(center, size + new Vector3(SourceMargin * 2f, 0f, SourceMargin * 2f));
            var sources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(collect, SourceMask, NavMeshCollectGeometry.PhysicsColliders, 0, s_NoMarkups, sources);
            // Doors open (the bot opens them) and players, zombies and cars
            // move: none of them is a wall of the mesh.
            sources.RemoveAll(source =>
                source.shape == NavMeshBuildSourceShape.Terrain
                || (source.component is Component component
                    && component != null
                    && (((1 << component.gameObject.layer) & MovingMask) != 0
                        || (component is Collider collider && collider.isTrigger)
                        || IsDoor(component))));
            // The terrain goes in as its surface only: as a Unity terrain it
            // is solid below, which filled the bunker rooms under it.
            var terrain = BuildTerrainMesh(min.x - SourceMargin, min.z - SourceMargin, ChunkSize + SourceMargin * 2f);
            if (terrain != null)
            {
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = terrain,
                    transform = Matrix4x4.identity,
                    area = 0
                });
            }
            AddWaterSources(collect, sources);
            NavMeshData? data = null;
            var mainThread = 0.0;
            if (!s_AsyncBakeBroken)
            {
                data = new NavMeshData(s_Settings.agentTypeID);
                var operation = NavMeshBuilder.UpdateNavMeshDataAsync(data, s_Settings, sources, new Bounds(center, size));
                watch.Stop();
                s_BakeFrame = Time.frameCount;
                mainThread = watch.Elapsed.TotalMilliseconds;
                var startedAt = Time.realtimeSinceStartup;
                while (operation != null && !operation.isDone)
                {
                    if (Time.realtimeSinceStartup - startedAt > AsyncBakeTimeoutSeconds)
                    {
                        // The 0.1.113 test: not one piece in 14 min, so every
                        // route came from the zombie mesh. Never wait on it again.
                        s_AsyncBakeBroken = true;
                        s_Logger?.LogWarning(
                            "Malha nova: geracao em segundo plano nao terminou em {Seconds:0}s no pedaco ({X}, {Z}); passando a gerar direto.",
                            AsyncBakeTimeoutSeconds, cx, cz);
                        break;
                    }
                    await UniTask.Yield();
                }
                if (s_AsyncBakeBroken || operation == null)
                {
                    // Left to the stuck job; a fresh build below.
                    data = null;
                    s_AsyncBakeBroken = true;
                }
            }
            if (data == null)
            {
                watch.Restart();
                data = NavMeshBuilder.BuildNavMeshData(s_Settings, sources, new Bounds(center, size), Vector3.zero, Quaternion.identity);
                watch.Stop();
                s_BakeFrame = Time.frameCount;
                mainThread += watch.Elapsed.TotalMilliseconds;
            }
            if (terrain != null)
            {
                UnityEngine.Object.Destroy(terrain);
            }
            if (generation != s_Generation || data == null)
            {
                if (data != null)
                {
                    UnityEngine.Object.Destroy(data);
                }
                return;
            }
            watch.Restart();
            {
                s_PieceData.Add(data);
                s_Pieces[key] = NavMesh.AddNavMeshData(data);
                LinkTo(cx, cz, cx + 1, cz);
                LinkTo(cx, cz, cx - 1, cz);
                LinkTo(cx, cz, cx, cz + 1);
                LinkTo(cx, cz, cx, cz - 1);
                LinkLadders();
            }
            watch.Stop();
            s_BakeFrame = Time.frameCount;
            mainThread += watch.Elapsed.TotalMilliseconds;
            s_Baked++;
            s_BakeMilliseconds += mainThread;
            s_WorstBakeMilliseconds = Math.Max(s_WorstBakeMilliseconds, mainThread);
            s_LastBakeMilliseconds = mainThread;
        }

        // Climbing is slower than walking: a ladder costs as much as 4 times
        // its length on foot.
        private const float LadderCost = 4f;
        // 0.2.2x10: ladder links in their own area, so a route can be asked
        // for without them (a bot whose ladder just failed walks round).
        private const int LadderArea = 2;
        private static int s_PathAreaMask = NavMesh.AllAreas;

        private static void LinkLadders()
        {
            foreach (var ladder in SkaliaBotLadderMap.Ladders)
            {
                if (s_LinkedLadders.Contains(ladder.Index)
                    || !HasPiece(Key(Cell(ladder.Bottom.x), Cell(ladder.Bottom.z)))
                    || !HasPiece(Key(Cell(ladder.Top.x), Cell(ladder.Top.z)))
                    || !NavMesh.SamplePosition(ladder.Bottom, out var bottom, 1f, s_Filter)
                    || !NavMesh.SamplePosition(ladder.Top, out var top, 1f, s_Filter)
                    || top.position.y - bottom.position.y < 1f)
                {
                    continue;
                }
                var instance = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = bottom.position,
                    endPosition = top.position,
                    width = 0f,
                    costModifier = LadderCost,
                    bidirectional = SkaliaBotIdleSimulation.s_Settings.LadderDescend,
                    area = LadderArea,
                    agentTypeID = s_Settings.agentTypeID
                });
                if (!instance.valid)
                {
                    continue;
                }
                s_Links.Add(instance);
                s_LinkedLadders.Add(ladder.Index);
                s_LadderLinks.Add(new LadderLink { Ladder = ladder, Start = bottom.position, End = top.position });
            }
        }

        // The ladder whose link a route takes between corners `a` and `b`.
        public static BotLadder? LadderBetween(Vector3 a, Vector3 b)
        {
            foreach (var link in s_LadderLinks)
            {
                if ((link.Start - a).sqrMagnitude < 1f && (link.End - b).sqrMagnitude < 1f)
                {
                    return link.Ladder;
                }
            }
            return null;
        }

        public static int LadderLinkCount => s_LadderLinks.Count;

        // Local detours (furniture, corner, wall) by the mesh of the route
        // being followed, to see whether new-mesh routes still need them.
        private static int s_DetoursUnity, s_DetoursZombie;

        public static void CountDetour(bool unityRoute)
        {
            if (unityRoute) s_DetoursUnity++;
            else s_DetoursZombie++;
        }

        // Short two-way links across the edge shared with a ready neighbour,
        // wherever both sides have floor at the same height.
        // Unity holds at most 65535 links: with one per 3m and per storey the
        // whole map ran out at 1300 of 1500 pieces and the rest stayed cut
        // off. Neighbouring samples on the same level now share one wide
        // link, up to 30m, as long as the run stays within 0.6m of height
        // (12m still left 42 links a piece, 63 thousand for the whole map).
        private const float MaxLinkRunMeters = 30f;
        private const float MaxLinkRunRise = 0.6f;
        private const int MaxLinks = 65000;
        private static bool s_LinkLimitLogged;

        private sealed class LinkRun
        {
            public Vector3 FirstA, FirstB, LastA, LastB;
            public float MinY, MaxY;
            public int Count;
            public int LastIndex;
        }

        private static readonly List<LinkRun> s_OpenRuns = new List<LinkRun>();
        private static readonly List<LinkRun> s_ClosedRuns = new List<LinkRun>();

        private static void LinkTo(int cx, int cz, int nx, int nz)
        {
            if (!s_Pieces.ContainsKey(Key(nx, nz)))
            {
                return;
            }
            var alongX = nz != cz;
            var edge = alongX
                ? Mathf.Max(cz, nz) * ChunkSize
                : Mathf.Max(cx, nx) * ChunkSize;
            var start = alongX ? cx * ChunkSize : cz * ChunkSize;
            var across = alongX ? Vector3.forward : Vector3.right;
            // A samples sit on the lower side of the edge, B on the upper.
            s_RunPieceA = alongX ? Key(cx, Mathf.Min(cz, nz)) : Key(Mathf.Min(cx, nx), cz);
            s_RunPieceB = alongX ? Key(cx, Mathf.Max(cz, nz)) : Key(Mathf.Max(cx, nx), cz);
            var hits = new List<NavMeshHit>();
            s_OpenRuns.Clear();
            var index = 0;
            for (var offset = LinkSpacing * 0.5f; offset < ChunkSize; offset += LinkSpacing, index++)
            {
                var along = start + offset;
                // The ground and each storey of a house on the edge, from a
                // cellar 6m down to a roof 30m up.
                var ground = alongX ? new Vector3(along, 0f, edge) : new Vector3(edge, 0f, along);
                if (Landscape.getWorldHeight(ground, out var groundY))
                {
                    for (var probeY = groundY - 6f; probeY < groundY + 30f; probeY += 3f)
                    {
                        var probe = alongX
                            ? new Vector3(along, probeY, edge)
                            : new Vector3(edge, probeY, along);
                        if (!NavMesh.SamplePosition(probe - across * LinkReach, out var a, 2.1f, s_Filter)
                            || Mathf.Abs(a.position.y - probeY) > 1.55f
                            || ((alongX ? a.position.z : a.position.x) > edge)
                            || !NavMesh.SamplePosition(probe + across * LinkReach, out var b, 2.1f, s_Filter)
                            || ((alongX ? b.position.z : b.position.x) < edge)
                            || Mathf.Abs(a.position.y - b.position.y) > 0.6f
                            || (a.position - b.position).GetHorizontalMagnitude() > 2f)
                        {
                            continue;
                        }
                        var duplicate = false;
                        foreach (var hit in hits)
                        {
                            if ((hit.position - a.position).sqrMagnitude < 1f)
                            {
                                duplicate = true;
                                break;
                            }
                        }
                        if (duplicate)
                        {
                            continue;
                        }
                        hits.Add(a);
                        AddToRun(a.position, b.position, index);
                    }
                }
                hits.Clear();
                // Runs the last sample did not extend are finished.
                for (var run = s_OpenRuns.Count - 1; run >= 0; run--)
                {
                    if (s_OpenRuns[run].LastIndex < index)
                    {
                        AddRunLink(s_OpenRuns[run]);
                        s_OpenRuns.RemoveAt(run);
                    }
                }
            }
            foreach (var run in s_OpenRuns)
            {
                AddRunLink(run);
            }
            s_OpenRuns.Clear();
        }

        private static void AddToRun(Vector3 a, Vector3 b, int index)
        {
            foreach (var run in s_OpenRuns)
            {
                if (run.LastIndex == index - 1
                    && Mathf.Abs(run.LastA.y - a.y) <= 0.5f
                    && Mathf.Max(run.MaxY, a.y) - Mathf.Min(run.MinY, a.y) <= MaxLinkRunRise
                    && run.Count * LinkSpacing < MaxLinkRunMeters
                    && Mathf.Abs((a - run.LastA).GetHorizontalMagnitude() - LinkSpacing) < 1f
                    && IsFloorContinuous(run.LastA, a)
                    && IsFloorContinuous(run.LastB, b))
                {
                    run.LastA = a;
                    run.LastB = b;
                    run.LastIndex = index;
                    run.MinY = Mathf.Min(run.MinY, a.y);
                    run.MaxY = Mathf.Max(run.MaxY, a.y);
                    run.Count++;
                    return;
                }
            }
            s_OpenRuns.Add(new LinkRun { FirstA = a, FirstB = b, LastA = a, LastB = b, MinY = a.y, MaxY = a.y, Count = 1, LastIndex = index });
        }

        // A wide link is a doorway the whole run long: a route may cross it
        // anywhere and slide along it. Samples 3m apart on both sides of a
        // building wall that stands on the edge joined into one run, and the
        // routes walked along the edge through Diner_1, Barn_1, the Hesco
        // walls and Medic_0 (Washington test: every wall circle was on a
        // piece edge, x or z a multiple of 102.4m). The floor between two
        // samples must be one stretch of mesh on each side.
        private static bool IsFloorContinuous(Vector3 from, Vector3 to)
        {
            return !NavMesh.Raycast(from, to, out _, s_Filter);
        }

        private static void AddRunLink(LinkRun run)
        {
            if (s_Links.Count >= MaxLinks)
            {
                if (!s_LinkLimitLogged && s_Logger != null)
                {
                    s_LinkLimitLogged = true;
                    s_Logger.LogWarning("Malha nova: limite de {Links} ligacoes entre pedacos atingido; pedacos novos ficam sem ligacao.", s_Links.Count);
                }
                return;
            }
            var instance = NavMesh.AddLink(new NavMeshLinkData
            {
                startPosition = (run.FirstA + run.LastA) * 0.5f,
                endPosition = (run.FirstB + run.LastB) * 0.5f,
                width = (run.Count - 1) * LinkSpacing + LinkWidth,
                costModifier = -1f,
                bidirectional = true,
                area = 0,
                agentTypeID = s_Settings.agentTypeID
            });
            if (instance.valid)
            {
                s_Links.Add(instance);
                AddPortal((run.FirstA + run.LastA) * 0.5f, (run.FirstB + run.LastB) * 0.5f, s_RunPieceA, s_RunPieceB);
                var portal = s_Portals[s_Portals.Count - 1];
                portal.FirstA = run.FirstA;
                portal.LastA = run.LastA;
                portal.FirstB = run.FirstB;
                portal.LastB = run.LastB;
            }
        }

        // ---- Long routes over the pieces ----
        // A trip of 700m is too long for one mesh route (260m at most): the
        // trip used to aim at a straight line of points and failed when the
        // line crossed a mountain side (37 of 39 failed trips in the 0.1.150
        // test, all after the mesh was complete). Each link between two
        // pieces is a doorway ("portal"); a search over the doorways finds
        // the order of pieces to cross, and each step inside a piece is
        // checked with a real mesh route before the plan is trusted. The
        // checks are kept for every bot.
        private sealed class Portal
        {
            public Vector3 A, B;
            public Vector3 FirstA, LastA, FirstB, LastB;
            public long PieceA, PieceB;
        }

        private static long s_RunPieceA, s_RunPieceB;
        private static readonly List<Portal> s_Portals = new List<Portal>();
        private static readonly Dictionary<long, List<int>> s_PiecePortals = new Dictionary<long, List<int>>();
        private static readonly Dictionary<(int, int, long), float> s_PortalCost = new Dictionary<(int, int, long), float>();
        private static NavMeshPath? s_PortalPath;

        // The object audit only looks inside the pieces; the wide links on
        // their edges are doorways too, and in the Washington test routes
        // slid along them through walls. Walk each wide link end to end with
        // the standing body, on both sides.
        private static void AuditLinks(ILogger logger)
        {
            var wide = 0;
            var blocked = 0;
            var examples = new Dictionary<string, int>();
            foreach (var portal in s_Portals)
            {
                if ((portal.LastA - portal.FirstA).GetHorizontalMagnitude() < 2f)
                {
                    continue;
                }
                wide++;
                var hitA = WhatBlocksBody(portal.FirstA, portal.LastA);
                var hitB = hitA == null ? WhatBlocksBody(portal.FirstB, portal.LastB) : null;
                var hit = hitA ?? hitB;
                if (hit == null)
                {
                    continue;
                }
                blocked++;
                examples.TryGetValue(hit, out var count);
                examples[hit] = count + 1;
            }
            var top = new List<KeyValuePair<string, int>>(examples);
            top.Sort((x, y) => y.Value.CompareTo(x.Value));
            var names = new List<string>();
            for (var index = 0; index < top.Count && index < 8; index++)
            {
                names.Add(top[index].Key + " x" + top[index].Value);
            }
            logger.LogInformation(
                "Malha nova: auditoria das ligacoes entre pedacos: {Blocked} de {Wide} ligacoes largas tem algo no meio que o corpo nao atravessa{Names}.",
                blocked,
                wide,
                names.Count > 0 ? "; mais comuns: " + string.Join(", ", names) : string.Empty);
        }

        private static string? WhatBlocksBody(Vector3 from, Vector3 to)
        {
            var lift = PlayerStance.RADIUS + 0.45f;
            var bottom = from + Vector3.up * lift;
            var top = from + Vector3.up * (PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS);
            var leg = to - from;
            var length = leg.magnitude;
            if (length < 0.1f)
            {
                return null;
            }
            if (Physics.CapsuleCast(bottom, top, PlayerStance.RADIUS - 0.02f, leg / length, out var hit, length,
                    RayMasks.BLOCK_COLLISION & ~MovingMask, QueryTriggerInteraction.Ignore))
            {
                return hit.collider != null ? hit.collider.transform.root.name : "?";
            }
            return null;
        }

        private static void AddPortal(Vector3 a, Vector3 b, long pieceA, long pieceB)
        {
            var index = s_Portals.Count;
            s_Portals.Add(new Portal { A = a, B = b, PieceA = pieceA, PieceB = pieceB });
            AddPiecePortal(pieceA, index);
            AddPiecePortal(pieceB, index);
            s_PieceComponent.Clear();
        }

        // Pieces joined by any chain of links share a group. A destination in
        // another group can never be reached, wherever the bot starts.
        private static readonly Dictionary<long, int> s_PieceComponent = new Dictionary<long, int>();
        private static int s_NextComponent;

        private static int ComponentOf(long piece)
        {
            if (!s_PiecePortals.ContainsKey(piece))
            {
                return -1;
            }
            if (s_PieceComponent.TryGetValue(piece, out var known))
            {
                return known;
            }
            if (s_PieceComponent.Count == 0)
            {
                s_NextComponent = 0;
            }
            var id = s_NextComponent++;
            var queue = new Queue<long>();
            queue.Enqueue(piece);
            s_PieceComponent[piece] = id;
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var portal in s_PiecePortals[current])
                {
                    var other = OtherPiece(portal, current);
                    if (!s_PieceComponent.ContainsKey(other) && s_PiecePortals.ContainsKey(other))
                    {
                        s_PieceComponent[other] = id;
                        queue.Enqueue(other);
                    }
                }
            }
            return id;
        }

        private static void AddPiecePortal(long piece, int index)
        {
            if (!s_PiecePortals.TryGetValue(piece, out var list))
            {
                list = new List<int>();
                s_PiecePortals[piece] = list;
            }
            list.Add(index);
        }

        private static void ClearPortals()
        {
            s_Portals.Clear();
            s_PiecePortals.Clear();
            s_PortalCost.Clear();
            s_FailedPlans.Clear();
            s_UnreachableGoals.Clear();
            s_PieceComponent.Clear();
            s_GoalFailStarts.Clear();
        }

        private static Vector3 PortalSide(int portal, long piece)
        {
            var p = s_Portals[portal];
            return p.PieceA == piece ? p.A : p.B;
        }

        private static long OtherPiece(int portal, long piece)
        {
            var p = s_Portals[portal];
            return p.PieceA == piece ? p.PieceB : p.PieceA;
        }

        private static float Optimistic(Vector3 a, Vector3 b)
        {
            return (a - b).GetHorizontalMagnitude() + Mathf.Abs(a.y - b.y);
        }

        // Length of a complete, dry mesh route between two points, or
        // infinity.
        private static float MeasureRoute(Vector3 from, Vector3 to)
        {
            s_PortalPath ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, s_Filter, s_PortalPath)
                || s_PortalPath.status != NavMeshPathStatus.PathComplete)
            {
                return float.PositiveInfinity;
            }
            var corners = s_PortalPath.corners;
            var length = 0f;
            for (var index = 0; index < corners.Length; index++)
            {
                if (WaterUtility.isPointUnderwater(corners[index] + Vector3.up * 0.5f))
                {
                    return float.PositiveInfinity;
                }
                if (index > 0)
                {
                    length += (corners[index] - corners[index - 1]).magnitude;
                }
            }
            return length;
        }

        public static int PortalCount => s_Portals.Count;
        public static int PortalChecksCached => s_PortalCost.Count;

        // States are (portal, side the bot leaves it on): index*2 (+1 when it
        // leaves into PieceB). -1 is the bot, -2 the destination.
        private struct Step
        {
            public int From, To;
        }

        public static bool TryPlanLong(Vector3 from, Vector3 to, List<Vector3> output, out string failure, out float length, out int checks)
        {
            output.Clear();
            length = 0f;
            checks = 0;
            if (!MapFilled)
            {
                failure = "malha incompleta";
                return false;
            }
            if (!NavMesh.SamplePosition(Grounded(from), out var startHit, 2f, s_Filter))
            {
                failure = "bot fora da malha";
                return false;
            }
            if (!NavMesh.SamplePosition(to, out var goalHit, 8f, s_Filter))
            {
                failure = "destino fora da malha";
                return false;
            }
            var start = startHit.position;
            var goal = goalHit.position;
            var startPiece = Key(Cell(start.x), Cell(start.z));
            var goalPiece = Key(Cell(goal.x), Cell(goal.z));
            // Washington test: 133 searches for Rainbridge Island, ~240ms
            // each, all with the same answer. Remember it for 20 min.
            var now = Time.realtimeSinceStartup;
            if (s_FailedPlans.TryGetValue((startPiece, goalPiece), out var failed) && now < failed.Until)
            {
                failure = failed.Reason + " (lembrado)";
                return false;
            }
            // Washington 0.1.162: the pair memo still let 14 trips to
            // Rainbridge Island through, each from a different piece.
            if (TryGetUnreachableReason(startPiece, goalPiece, now, true, out var unreachable))
            {
                failure = unreachable;
                return false;
            }
            var planned = s_Whole
                ? TryPlanWhole(start, goal, output, out failure, out length, out checks)
                : TryPlanLongCore(start, goal, startPiece, goalPiece, output, out failure, out length, out checks);
            if (!planned)
            {
                s_FailedPlans[(startPiece, goalPiece)] = (now + 1200f, failure);
                if (failure.StartsWith("nenhuma sequencia", StringComparison.Ordinal))
                {
                    if (!s_Whole)
                    {
                        s_UnreachableGoals[(ComponentOf(startPiece), goalPiece)] = now + 1200f;
                    }
                    else if (s_GoalFailStarts.TryGetValue(goalPiece, out var first) && now < first.Until && first.Start != startPiece)
                    {
                        // On one mesh, failing from two different places
                        // means the place itself is cut off (an island).
                        s_UnreachableGoals[(0, goalPiece)] = now + 1200f;
                    }
                    else
                    {
                        s_GoalFailStarts[goalPiece] = (startPiece, now + 1200f);
                    }
                }
            }
            return planned;
        }

        private static readonly Dictionary<long, (long Start, float Until)> s_GoalFailStarts = new Dictionary<long, (long, float)>();
        private const float WholePlanPointSpacing = 60f;
        private const int WholePlanMaxLegs = 16;

        // A long route on the whole mesh: Unity's own path, chained when it
        // comes back partial (its search has a node budget), cut where it
        // would go deeper into water than the body wades. Points every ~60m
        // along it; the legs between them are routed again as the bot walks.
        private static bool TryPlanWhole(Vector3 start, Vector3 goal, List<Vector3> output, out string failure, out float length, out int checks)
        {
            failure = string.Empty;
            length = 0f;
            checks = 0;
            s_PortalPath ??= new NavMeshPath();
            var watch = Stopwatch.StartNew();
            var current = start;
            var sinceKept = 0f;
            var previous = start;
            for (var leg = 0; leg < WholePlanMaxLegs; leg++)
            {
                checks++;
                if (!NavMesh.CalculatePath(current, goal, s_Filter, s_PortalPath)
                    || s_PortalPath.status == NavMeshPathStatus.PathInvalid
                    || s_PortalPath.corners.Length == 0)
                {
                    failure = "nenhuma sequencia de trechos chega la (sem caminho de " + FormatPoint(current) + ")";
                    return false;
                }
                var corners = s_PortalPath.corners;
                var cut = corners.Length;
                for (var index = 1; index < corners.Length; index++)
                {
                    if (WaterUtility.isPointUnderwater(corners[index] + Vector3.up * 0.5f)
                        || WaterUtility.isPointUnderwater((corners[index] + corners[index - 1]) * 0.5f + Vector3.up * 0.5f))
                    {
                        cut = index;
                        break;
                    }
                }
                for (var index = 1; index < cut; index++)
                {
                    // A straight stretch can be 300m long: the travel legs only
                    // look 240m ahead, so the 0.1.164 Everett trip found "no
                    // point of the route in reach". A point every 60m, also
                    // inside long stretches.
                    var from = previous;
                    var to = corners[index];
                    var segment = (to - from).magnitude;
                    var walked = 0f;
                    while (segment - walked + sinceKept >= WholePlanPointSpacing)
                    {
                        walked += WholePlanPointSpacing - sinceKept;
                        var point = Vector3.Lerp(from, to, walked / segment);
                        if (NavMesh.SamplePosition(point, out var onMesh, 2f, s_Filter))
                        {
                            point = onMesh.position;
                        }
                        output.Add(point);
                        sinceKept = 0f;
                    }
                    sinceKept += segment - walked;
                    length += segment;
                    previous = to;
                    if (index == cut - 1 && sinceKept > 1f)
                    {
                        output.Add(to);
                        sinceKept = 0f;
                    }
                }
                var end = cut > 1 ? corners[cut - 1] : current;
                var left = (goal - end).GetHorizontalMagnitude();
                // The search stopped a step short of a goal sampled on a
                // ledge or object: that is arriving (0.1.164: "the mesh
                // stops 1m from the destination").
                var complete = (s_PortalPath.status == NavMeshPathStatus.PathComplete && cut == corners.Length)
                               || (cut == corners.Length && left <= 3f && Mathf.Abs(goal.y - end.y) <= 2f);
                if (complete)
                {
                    if (output.Count == 0 || (output[output.Count - 1] - goal).sqrMagnitude > 0.01f)
                    {
                        output.Add(goal);
                    }
                    failure = string.Empty;
                    return true;
                }
                if (left >= (goal - current).GetHorizontalMagnitude() - 25f)
                {
                    failure = cut < corners.Length
                        ? "nenhuma sequencia de trechos chega la (agua no caminho em " + FormatPoint(corners[cut]) + ", " + left.ToString("0") + "m do destino)"
                        : "nenhuma sequencia de trechos chega la (a malha para a " + left.ToString("0") + "m do destino, em " + FormatPoint(end) + ")";
                    output.Clear();
                    return false;
                }
                if (watch.Elapsed.TotalMilliseconds > 200.0)
                {
                    failure = "tempo esgotado (" + checks + " trechos)";
                    output.Clear();
                    return false;
                }
                current = end;
            }
            failure = "rota longa demais (" + WholePlanMaxLegs + " trechos)";
            output.Clear();
            return false;
        }

        private static string FormatPoint(Vector3 point)
        {
            return "(" + point.x.ToString("0") + ", " + point.z.ToString("0") + ")";
        }

        private static readonly Dictionary<(long, long), (float Until, string Reason)> s_FailedPlans = new Dictionary<(long, long), (float, string)>();
        private static readonly Dictionary<(int, long), float> s_UnreachableGoals = new Dictionary<(int, long), float>();

        private static bool TryGetUnreachableReason(long startPiece, long goalPiece, float now, bool goalOnMesh, out string reason)
        {
            reason = string.Empty;
            if (s_Whole)
            {
                if (startPiece != goalPiece && s_UnreachableGoals.TryGetValue((0, goalPiece), out var wholeUntil) && now < wholeUntil)
                {
                    reason = "nenhuma sequencia de trechos chega la (lembrado: ninguem chegou ate la)";
                    return true;
                }
                return false;
            }
            if (startPiece == goalPiece || !s_PiecePortals.ContainsKey(startPiece))
            {
                return false;
            }
            var startGroup = ComponentOf(startPiece);
            var goalGroup = ComponentOf(goalPiece);
            // An area centre can sit in a piece with no mesh at all (a lake);
            // only a point already on the mesh proves the piece is cut off.
            if ((goalGroup < 0 && goalOnMesh) || (goalGroup >= 0 && goalGroup != startGroup))
            {
                reason = "nenhuma sequencia de pedacos chega la (destino em outra ilha da malha)";
                return true;
            }
            if (s_UnreachableGoals.TryGetValue((startGroup, goalPiece), out var until) && now < until)
            {
                reason = "nenhuma sequencia de pedacos chega la (lembrado para toda esta parte do mapa)";
                return true;
            }
            return false;
        }

        public static bool IsKnownUnreachable(Vector3 from, Vector3 to)
        {
            if (!MapFilled)
            {
                return false;
            }
            var startPiece = Key(Cell(from.x), Cell(from.z));
            var goalPiece = Key(Cell(to.x), Cell(to.z));
            if (TryGetUnreachableReason(startPiece, goalPiece, Time.realtimeSinceStartup, false, out _))
            {
                return true;
            }
            return s_FailedPlans.TryGetValue((startPiece, goalPiece), out var failed)
                && Time.realtimeSinceStartup < failed.Until
                && failed.Reason.StartsWith("nenhuma sequencia", StringComparison.Ordinal);
        }

        private static bool TryPlanLongCore(Vector3 start, Vector3 goal, long startPiece, long goalPiece, List<Vector3> output, out string failure, out float length, out int checks)
        {
            length = 0f;
            checks = 0;
            if (!s_PiecePortals.ContainsKey(startPiece) || !s_PiecePortals.ContainsKey(goalPiece))
            {
                failure = "pedaco sem ligacoes";
                return false;
            }
            var watch = Stopwatch.StartNew();
            var startCost = new Dictionary<int, float>();
            var goalCost = new Dictionary<int, float>();
            var minX = Mathf.Min(start.x, goal.x) - 600f;
            var maxX = Mathf.Max(start.x, goal.x) + 600f;
            var minZ = Mathf.Min(start.z, goal.z) - 600f;
            var maxZ = Mathf.Max(start.z, goal.z) + 600f;
            var gScore = new Dictionary<int, float>();
            var parent = new Dictionary<int, int>();
            var open = new SortedSet<(float, int)>();
            var closed = new HashSet<int>();
            var steps = new List<Step>();
            for (var iteration = 0; iteration < 60; iteration++)
            {
                gScore.Clear();
                parent.Clear();
                open.Clear();
                closed.Clear();
                foreach (var portal in s_PiecePortals[startPiece])
                {
                    var side = PortalSide(portal, startPiece);
                    var cost = startCost.TryGetValue(portal, out var known) ? known : Optimistic(start, side);
                    if (float.IsInfinity(cost))
                    {
                        continue;
                    }
                    var state = portal * 2 + (s_Portals[portal].PieceA == startPiece ? 1 : 0);
                    var exit = PortalSide(portal, OtherPiece(portal, startPiece));
                    var g = cost + (exit - side).magnitude;
                    if (!gScore.TryGetValue(state, out var old) || g < old)
                    {
                        gScore[state] = g;
                        parent[state] = -1;
                        open.Add((g + (goal - exit).GetHorizontalMagnitude(), state));
                    }
                }
                var found = false;
                var bestGoal = float.PositiveInfinity;
                var goalParent = -1;
                if (startPiece == goalPiece)
                {
                    var direct = MeasureRoute(start, goal);
                    checks++;
                    if (!float.IsInfinity(direct))
                    {
                        output.Add(goal);
                        length = direct;
                        failure = string.Empty;
                        return true;
                    }
                }
                var expansions = 0;
                while (open.Count > 0)
                {
                    var current = open.Min;
                    open.Remove(current);
                    if (current.Item1 >= bestGoal)
                    {
                        found = true;
                        break;
                    }
                    var state = current.Item2;
                    if (!closed.Add(state))
                    {
                        continue;
                    }
                    if (++expansions > 40000 || watch.ElapsedMilliseconds > 200)
                    {
                        break;
                    }
                    var portal = state >> 1;
                    var piece = (state & 1) == 1 ? s_Portals[portal].PieceB : s_Portals[portal].PieceA;
                    var here = PortalSide(portal, piece);
                    var gHere = gScore[state];
                    if (piece == goalPiece)
                    {
                        var cost = goalCost.TryGetValue(portal, out var known) ? known : Optimistic(here, goal);
                        if (gHere + cost < bestGoal)
                        {
                            bestGoal = gHere + cost;
                            goalParent = state;
                        }
                    }
                    if (!s_PiecePortals.TryGetValue(piece, out var neighbours))
                    {
                        continue;
                    }
                    foreach (var next in neighbours)
                    {
                        if (next == portal)
                        {
                            continue;
                        }
                        var entry = PortalSide(next, piece);
                        if (entry.x < minX || entry.x > maxX || entry.z < minZ || entry.z > maxZ)
                        {
                            continue;
                        }
                        var key = portal < next ? (portal, next, piece) : (next, portal, piece);
                        var cost = s_PortalCost.TryGetValue(key, out var known) ? known : Optimistic(here, entry);
                        if (float.IsInfinity(cost))
                        {
                            continue;
                        }
                        var nextPiece = OtherPiece(next, piece);
                        var nextState = next * 2 + (s_Portals[next].PieceB == nextPiece ? 1 : 0);
                        if (closed.Contains(nextState))
                        {
                            continue;
                        }
                        var exit = PortalSide(next, nextPiece);
                        var g = gHere + cost + (exit - entry).magnitude;
                        if (!gScore.TryGetValue(nextState, out var old) || g < old)
                        {
                            gScore[nextState] = g;
                            parent[nextState] = state;
                            open.Add((g + (goal - exit).GetHorizontalMagnitude(), nextState));
                        }
                    }
                }
                if (!found && goalParent >= 0 && open.Count == 0)
                {
                    found = true;
                }
                if (!found || goalParent < 0)
                {
                    failure = watch.ElapsedMilliseconds > 200 || expansions > 40000
                        ? "busca longa demais"
                        : "nenhuma sequencia de pedacos chega la";
                    return false;
                }
                // The chain of states, bot first.
                var chain = new List<int>();
                for (var state = goalParent; state != -1; state = parent[state])
                {
                    chain.Add(state);
                }
                chain.Reverse();
                // Check each unverified step with a real route; a step that
                // is much longer than hoped (or impossible) sends the search
                // around again.
                var changed = false;
                var firstPortal = chain[0] >> 1;
                if (!startCost.ContainsKey(firstPortal))
                {
                    var side = PortalSide(firstPortal, startPiece);
                    var real = MeasureRoute(start, side);
                    checks++;
                    startCost[firstPortal] = real;
                    changed |= real > Optimistic(start, side) * 1.5f + 10f;
                }
                for (var index = 1; index < chain.Count && !changed; index++)
                {
                    var a = chain[index - 1] >> 1;
                    var b = chain[index] >> 1;
                    var piece = (chain[index - 1] & 1) == 1 ? s_Portals[a].PieceB : s_Portals[a].PieceA;
                    var key = a < b ? (a, b, piece) : (b, a, piece);
                    if (s_PortalCost.ContainsKey(key))
                    {
                        continue;
                    }
                    var from2 = PortalSide(a, piece);
                    var to2 = PortalSide(b, piece);
                    var real = MeasureRoute(from2, to2);
                    checks++;
                    s_PortalCost[key] = real;
                    changed |= real > Optimistic(from2, to2) * 1.5f + 10f;
                }
                var lastPortal = goalParent >> 1;
                if (!changed && !goalCost.ContainsKey(lastPortal))
                {
                    var lastPiece = (goalParent & 1) == 1 ? s_Portals[lastPortal].PieceB : s_Portals[lastPortal].PieceA;
                    var side = PortalSide(lastPortal, lastPiece);
                    var real = MeasureRoute(side, goal);
                    checks++;
                    goalCost[lastPortal] = real;
                    changed |= real > Optimistic(side, goal) * 1.5f + 10f;
                }
                if (changed)
                {
                    if (checks > 200 || watch.ElapsedMilliseconds > 200)
                    {
                        failure = "verificacao esgotada (" + checks + " rotas)";
                        return false;
                    }
                    continue;
                }
                foreach (var state in chain)
                {
                    var portal = state >> 1;
                    var p = s_Portals[portal];
                    var leaveB = (state & 1) == 1;
                    output.Add(leaveB ? p.A : p.B);
                    output.Add(leaveB ? p.B : p.A);
                }
                output.Add(goal);
                length = bestGoal;
                failure = string.Empty;
                return true;
            }
            failure = "verificacao esgotada (" + checks + " rotas)";
            return false;
        }

        // Places the bake got wrong, cut out of the mesh for a while. In the
        // 0.1.103 log a bot was teleported 17 times out of the same corner by
        // an opened wardrobe, and 15 times at a stone fence the mesh walked
        // through: every route came back the same way. A carved obstacle
        // makes the next route go around.
        private static readonly List<(GameObject Go, float Until)> s_Blocks = new List<(GameObject, float)>();
        private const int MaxBlocks = 300;

        public static void BlockArea(Vector3 center, Vector3 size, float yaw, float seconds)
        {
            if (!s_Started)
            {
                return;
            }
            ExpireBlocks();
            if (s_Blocks.Count >= MaxBlocks)
            {
                if (s_Blocks[0].Go != null)
                {
                    UnityEngine.Object.Destroy(s_Blocks[0].Go);
                }
                s_Blocks.RemoveAt(0);
            }
            var go = new GameObject("SkaliaBotNavBlock");
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yaw, 0f));
            var obstacle = go.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = Vector3.zero;
            obstacle.size = size;
            obstacle.carveOnlyStationary = false;
            obstacle.carving = true;
            s_Blocks.Add((go, Time.realtimeSinceStartup + seconds));
        }

        // Cuts for solid objects the bake let the mesh run through (found by
        // the object audit): they stay for the whole session.
        private static readonly List<GameObject> s_StaticBlocks = new List<GameObject>();
        private const int MaxStaticBlocks = 4000;

        private static void ClearStaticBlocks()
        {
            foreach (var go in s_StaticBlocks)
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
            }
            s_StaticBlocks.Clear();
        }

        private static void AddStaticBlock(Vector3 center, Vector3 size, float yaw)
        {
            var go = new GameObject("SkaliaBotNavStaticBlock");
            go.transform.SetPositionAndRotation(center, Quaternion.Euler(0f, yaw, 0f));
            var obstacle = go.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = Vector3.zero;
            obstacle.size = size;
            obstacle.carveOnlyStationary = true;
            obstacle.carving = true;
            s_StaticBlocks.Add(go);
        }

        // The audit used to test one point, the middle of the object's
        // footprint: on a wooden fence that point fell between the boards
        // and 94 fences were reported as "the body fits", which it does not.
        // Now the mesh is crossed from side to side every meter along the
        // object; where the mesh goes straight across and the body's capsule
        // hits a collider on the way, that meter of the object is cut out of
        // the mesh with the object's own thickness and height (the carving
        // keeps the agent radius, as the bake does).
        private static int CutCrossings(BotMapObject mapObject)
        {
            var size = mapObject.LocalMax - mapObject.LocalMin;
            var longOnX = size.x >= size.y;
            var length = longOnX ? size.x : size.y;
            var thickness = longOnX ? size.y : size.x;
            if (thickness > 6f)
            {
                return 0;
            }
            var height = mapObject.MaxY - mapObject.MinY;
            var mid = (mapObject.LocalMin + mapObject.LocalMax) * 0.5f;
            var along = longOnX ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
            var across = longOnX ? new Vector2(0f, 1f) : new Vector2(1f, 0f);
            var samples = Mathf.Clamp(Mathf.FloorToInt(length), 1, 40);
            var step = length / samples;
            var lift = PlayerStance.RADIUS + 0.45f;
            var top = PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS;
            var floorY = mapObject.MinY + 0.05f;
            var cuts = 0;
            for (var index = 0; index < samples && s_StaticBlocks.Count < MaxStaticBlocks; index++)
            {
                var offset = -length * 0.5f + step * (index + 0.5f);
                var local = mid + along * offset;
                var sideA = SkaliaBotObjectMap.ToWorld(mapObject, local - across * (thickness * 0.5f + 0.9f), floorY);
                var sideB = SkaliaBotObjectMap.ToWorld(mapObject, local + across * (thickness * 0.5f + 0.9f), floorY);
                if (!NavMesh.SamplePosition(sideA, out var a, 0.5f, s_Filter)
                    || !NavMesh.SamplePosition(sideB, out var b, 0.5f, s_Filter)
                    || Mathf.Abs(a.position.y - floorY) > 0.6f
                    || Mathf.Abs(b.position.y - floorY) > 0.6f
                    || !RaycastAcrossPieces(a.position, b.position, out _))
                {
                    continue;
                }
                var leg = b.position - a.position;
                if (leg.magnitude < 0.3f
                    || !Physics.CapsuleCast(
                        a.position + Vector3.up * lift,
                        a.position + Vector3.up * top,
                        PlayerStance.RADIUS - 0.04f,
                        leg.normalized,
                        out var hit,
                        leg.magnitude,
                        RayMasks.BLOCK_COLLISION & ~MovingMask,
                        QueryTriggerInteraction.Ignore)
                    || IsDoor(hit.collider)
                    || Mathf.Abs(hit.normal.y) > 0.7f
                    || Physics.GetIgnoreLayerCollision(LayerMasks.PLAYER, hit.collider.gameObject.layer))
                {
                    continue;
                }
                var sizeAlong = step + 0.05f;
                var blockSize = longOnX
                    ? new Vector3(sizeAlong, Mathf.Max(height, 1f), Mathf.Max(thickness, 0.1f))
                    : new Vector3(Mathf.Max(thickness, 0.1f), Mathf.Max(height, 1f), sizeAlong);
                AddStaticBlock(
                    SkaliaBotObjectMap.ToWorld(mapObject, local, mapObject.MinY + Mathf.Max(height, 1f) * 0.5f),
                    blockSize,
                    mapObject.Yaw);
                cuts++;
            }
            return cuts;
        }

        public static int BlockCount
        {
            get
            {
                ExpireBlocks();
                return s_Blocks.Count;
            }
        }

        private static void ExpireBlocks()
        {
            var now = Time.realtimeSinceStartup;
            for (var index = s_Blocks.Count - 1; index >= 0; index--)
            {
                if (now >= s_Blocks[index].Until)
                {
                    if (s_Blocks[index].Go != null)
                    {
                        UnityEngine.Object.Destroy(s_Blocks[index].Go);
                    }
                    s_Blocks.RemoveAt(index);
                }
            }
        }

        // Nearest point of the new mesh within radius (a planned point in a
        // river or inside a boulder has dry mesh beside it).
        public static bool TrySnap(Vector3 point, float radius, out Vector3 snapped)
        {
            snapped = point;
            if (!Enabled || !s_Started || !NavMesh.SamplePosition(Grounded(point), out var hit, radius, s_Filter))
            {
                return false;
            }
            snapped = hit.position;
            return !WaterUtility.isPointUnderwater(snapped + Vector3.up * 0.5f);
        }

        public static bool IsReadyAt(Vector3 point)
        {
            return Enabled && s_Started && HasPiece(Key(Cell(point.x), Cell(point.z)));
        }

        // The mesh is baked from the colliders, so no furniture, obstacle or
        // fence should have floor mesh inside its footprint. Count the ones
        // that do, and why their collider didn't make it into the bake: this
        // is what to fix in the bake, instead of cutting spots where bots
        // got stuck.
        private static async UniTaskVoid AuditObjectsAsync(ILogger logger, int generation)
        {
            try
            {
                if (!SkaliaBotObjectMap.Ready)
                {
                    return;
                }
                var watch = Stopwatch.StartNew();
                var slice = Stopwatch.StartNew();
                var checkedCount = 0;
                var through = 0;
                var byName = new Dictionary<string, (int Count, string Reason, Vector3 Where)>();
                var byReason = new Dictionary<string, int>();
                var overlap = new Collider[16];
                int crossCuts = 0, crossObjects = 0;
                var crossByName = new Dictionary<string, int>();
                ClearStaticBlocks();
                foreach (var mapObject in SkaliaBotObjectMap.Objects)
                {
                    if (generation != s_Generation)
                    {
                        return;
                    }
                    if (!SkaliaBotObjectMap.BlocksBody(mapObject) && mapObject.Category != BotObjectCategory.Fence)
                    {
                        continue;
                    }
                    var size = mapObject.LocalMax - mapObject.LocalMin;
                    if (size.x < 0.5f || size.y < 0.5f || mapObject.MaxY - mapObject.MinY < 0.5f)
                    {
                        continue;
                    }
                    var center = SkaliaBotObjectMap.ToWorld(mapObject, (mapObject.LocalMin + mapObject.LocalMax) * 0.5f, mapObject.MinY + 0.05f);
                    if (!IsReadyAt(center))
                    {
                        continue;
                    }
                    checkedCount++;
                    var cut = CutCrossings(mapObject);
                    if (cut > 0)
                    {
                        crossCuts += cut;
                        crossObjects++;
                        crossByName.TryGetValue(mapObject.Name, out var crossCount);
                        crossByName[mapObject.Name] = crossCount + cut;
                    }
                    if (!NavMesh.SamplePosition(center, out var hit, 0.6f, s_Filter)
                        || hit.position.y > mapObject.MinY + 0.5f)
                    {
                        continue;
                    }
                    var local = SkaliaBotObjectMap.ToLocal(mapObject, hit.position);
                    if (local.x < mapObject.LocalMin.x + 0.1f || local.x > mapObject.LocalMax.x - 0.1f
                        || local.y < mapObject.LocalMin.y + 0.1f || local.y > mapObject.LocalMax.y - 0.1f)
                    {
                        continue;
                    }
                    through++;
                    // Floor inside the drawing of an object is only wrong if
                    // the player's body can't stand there: a fence with gaps
                    // or a crate without collision lets the body through too.
                    var reason = "o corpo cabe ali (o objeto nao tem colisao nesse ponto)";
                    var count = Physics.OverlapCapsuleNonAlloc(
                        hit.position + Vector3.up * (PlayerStance.RADIUS + 0.1f),
                        hit.position + Vector3.up * (PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS),
                        PlayerStance.RADIUS - 0.04f, overlap, ~MovingMask, QueryTriggerInteraction.Ignore);
                    for (var index = 0; index < count; index++)
                    {
                        var collider = overlap[index];
                        if (collider == null || Physics.GetIgnoreLayerCollision(LayerMasks.PLAYER, collider.gameObject.layer))
                        {
                            continue;
                        }
                        reason = "corpo nao cabe: " + DescribeMeshRole(collider);
                        break;
                    }
                    var kind = !reason.StartsWith("corpo nao cabe") ? "o corpo cabe ali"
                        : reason.Contains("fora das camadas") ? "corpo nao cabe, colisor fora das camadas"
                        : reason.Contains("como porta") ? "corpo nao cabe, tirado como porta"
                        : reason.Contains("se move") ? "corpo nao cabe, tirado como objeto que se move"
                        : "corpo nao cabe, colisor esta na malha";
                    byReason.TryGetValue(kind, out var reasonCount);
                    byReason[kind] = reasonCount + 1;
                    if (byName.TryGetValue(mapObject.Name, out var entry))
                    {
                        byName[mapObject.Name] = (entry.Count + 1, entry.Reason, entry.Where);
                    }
                    else
                    {
                        byName[mapObject.Name] = (1, reason, center);
                    }
                    if (slice.Elapsed.TotalMilliseconds >= 4)
                    {
                        await UniTask.Yield();
                        slice.Restart();
                    }
                }
                var reasons = new System.Text.StringBuilder();
                foreach (var pair in byReason)
                {
                    reasons.Append(reasons.Length > 0 ? ", " : string.Empty).Append(pair.Key).Append(' ').Append(pair.Value);
                }
                var top = new List<KeyValuePair<string, (int Count, string Reason, Vector3 Where)>>(byName);
                top.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
                var names = new System.Text.StringBuilder();
                for (var index = 0; index < top.Count && index < 15; index++)
                {
                    var item = top[index];
                    names.Append(index > 0 ? " | " : string.Empty)
                        .Append(item.Key).Append(" x").Append(item.Value.Count)
                        .Append(" (").Append(item.Value.Reason)
                        .Append("; ex. ").Append(item.Value.Where.x.ToString("0")).Append(", ").Append(item.Value.Where.z.ToString("0")).Append(')');
                }
                var crossTop = new List<KeyValuePair<string, int>>(crossByName);
                crossTop.Sort((a, b) => b.Value.CompareTo(a.Value));
                var crossNames = new System.Text.StringBuilder();
                for (var index = 0; index < crossTop.Count && index < 12; index++)
                {
                    crossNames.Append(index > 0 ? ", " : string.Empty).Append(crossTop[index].Key).Append(" x").Append(crossTop[index].Value);
                }
                logger.LogInformation(
                    "Malha nova: a malha atravessava {Objects} objeto(s) solido(s) em {Cuts} trecho(s) (o corpo bate neles); cada trecho cortado da malha com o tamanho do objeto. Mais comuns: {Top}.",
                    crossObjects, crossCuts, crossNames.ToString());
                logger.LogInformation(
                    "Malha nova: auditoria de objetos em {Seconds:0.0}s: {Through} de {Checked} moveis/obstaculos/cercas tem chao da malha por dentro. Por motivo (camadas da malha agora: {Mask}): {Reasons}. Mais comuns: {Top}.",
                    watch.Elapsed.TotalSeconds, through, checkedCount, LayerNames(SourceMask), reasons.ToString(), names.ToString());
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Malha nova: auditoria de objetos falhou.");
            }
        }

        // Why a collider the bot ran into is missing from the mesh, for the
        // "parede entre o bot e o ponto da rota" line.
        public static string DescribeMeshRole(Collider collider)
        {
            var name = Describe(collider);
            if (((1 << collider.gameObject.layer) & SourceMask) == 0)
            {
                return name + ", fora das camadas da malha";
            }
            if (((1 << collider.gameObject.layer) & MovingMask) != 0)
            {
                return name + ", tirado da malha como objeto que se move";
            }
            if (collider.isTrigger)
            {
                return name + ", gatilho (fora da malha)";
            }
            if (IsDoor(collider))
            {
                return name + ", tirado da malha como porta";
            }
            return name + ", esta na malha";
        }

        public static bool IsRealDoor(InteractableObjectBinaryState state)
        {
            return state != null && IsDoorAsset(state.objectAsset);
        }

        // Only doors and gates swing out of the way; counters, ovens and
        // fridges open too but stay where they are.
        // Only the leaf that swings, not the whole object: a shipping
        // container is one openable object, and its 8m body went out of the
        // bake with its doors. In the 0.1.135 test routes went straight
        // through container walls (12 stops, the bot standing on a body
        // "taken out as a door"). A leaf is thin: under 0.6m in its own
        // smallest dimension, whatever way it is turned.
        private static bool IsDoor(Component component)
        {
            var state = component.GetComponentInParent<InteractableObjectBinaryState>();
            return state != null && IsDoorAsset(state.objectAsset) && IsThinLeaf(component);
        }

        private const float DoorLeafMaxThickness = 0.6f;

        internal static bool IsThinLeaf(Component component)
        {
            Vector3 size;
            switch (component)
            {
                case BoxCollider box:
                    size = box.size;
                    break;
                case MeshCollider mesh when mesh.sharedMesh != null:
                    size = mesh.sharedMesh.bounds.size;
                    break;
                case Collider other:
                    size = other.bounds.size;
                    return Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < DoorLeafMaxThickness
                           || Mathf.Min(size.x, size.z) < DoorLeafMaxThickness;
                default:
                    return true;
            }
            var scale = component.transform.lossyScale;
            size = new Vector3(Mathf.Abs(size.x * scale.x), Mathf.Abs(size.y * scale.y), Mathf.Abs(size.z * scale.z));
            return Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < DoorLeafMaxThickness;
        }

        // Every openable object is hinted "Door", so a door is one that opens
        // the zombie path (Interactability_Nav) or is named a door or gate.
        private static bool IsDoorAsset(ObjectAsset? asset)
        {
            if (asset == null)
            {
                return true;
            }
            if (asset.interactabilityNav != EObjectInteractabilityNav.NONE)
            {
                return true;
            }
            var name = asset.name ?? string.Empty;
            return name.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("gate", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static readonly List<NavMeshBuildSource> s_ProbeSources = new List<NavMeshBuildSource>();

        // Why a point has no mesh: what it stands on, whether that went into
        // the bake, and whether a small bake right there has mesh (with the
        // bot's 0.4m body, and with a thin body that keeps every scrap).
        private static string Diagnose(Vector3 point)
        {
            var report = new System.Text.StringBuilder();
            var origin = point + Vector3.up * 0.6f;
            if (Physics.Raycast(origin, Vector3.down, out var hit, 4f, SourceMask, QueryTriggerInteraction.Ignore))
            {
                report.Append("chao ").Append(Describe(hit.collider)).Append(" a ").Append((origin.y - hit.point.y - 0.6f).ToString("0.00")).Append("m");
            }
            else
            {
                report.Append("nenhum chao da malha ate 3.4m abaixo");
            }
            if (Physics.Raycast(origin, Vector3.down, out var any, 4f, ~0, QueryTriggerInteraction.Ignore)
                && (hit.collider == null || any.collider != hit.collider))
            {
                report.Append("; antes disso, fora da lista de camadas: ").Append(Describe(any.collider));
            }
            var hitBackfaces = Physics.queriesHitBackfaces;
            Physics.queriesHitBackfaces = true;
            var oneSided = Physics.Raycast(origin, Vector3.down, out var back, 4f, SourceMask, QueryTriggerInteraction.Ignore)
                && (hit.collider == null || back.distance < hit.distance - 0.05f);
            Physics.queriesHitBackfaces = hitBackfaces;
            if (oneSided)
            {
                report.Append("; chao de um lado so: ").Append(Describe(back.collider)).Append(" a ").Append((origin.y - back.point.y - 0.6f).ToString("0.00")).Append("m");
            }
            if (Physics.Raycast(point + Vector3.up * 0.1f, Vector3.up, out var roof, 3f, SourceMask, QueryTriggerInteraction.Ignore))
            {
                report.Append("; teto a ").Append((roof.point.y - point.y).ToString("0.00")).Append("m (").Append(Describe(roof.collider)).Append(")");
            }
            var bounds = new Bounds(point, new Vector3(16f, 8f, 16f));
            s_ProbeSources.Clear();
            NavMeshBuilder.CollectSources(bounds, SourceMask, NavMeshCollectGeometry.PhysicsColliders, 0, s_NoMarkups, s_ProbeSources);
            int door = 0, moving = 0, trigger = 0, terrain = 0;
            var floorKept = false;
            var floorSeen = false;
            for (var index = s_ProbeSources.Count - 1; index >= 0; index--)
            {
                var source = s_ProbeSources[index];
                var component = source.component;
                if (hit.collider != null && component == hit.collider)
                {
                    floorSeen = true;
                }
                string? reason = null;
                if (source.shape == NavMeshBuildSourceShape.Terrain) { terrain++; reason = "terreno"; }
                else if (component != null && ((1 << component.gameObject.layer) & MovingMask) != 0) { moving++; reason = "movel"; }
                else if (component is Collider collider && collider.isTrigger) { trigger++; reason = "gatilho"; }
                else if (component != null && IsDoor(component)) { door++; reason = "porta"; }
                if (reason != null)
                {
                    if (hit.collider != null && component == hit.collider)
                    {
                        report.Append("; o chao foi tirado da malha como ").Append(reason);
                    }
                    s_ProbeSources.RemoveAt(index);
                }
                else if (hit.collider != null && component == hit.collider)
                {
                    floorKept = true;
                }
            }
            if (hit.collider != null && !floorSeen)
            {
                report.Append("; o chao NAO veio na coleta da Unity");
            }
            report.Append("; pecas na area ").Append(s_ProbeSources.Count)
                .Append(" (tiradas: porta ").Append(door).Append(", movel ").Append(moving).Append(", gatilho ").Append(trigger).Append(", terreno ").Append(terrain).Append(")")
                .Append(floorKept ? "; chao entrou na geracao" : "");
            var ground = BuildTerrainMesh(point.x - 8f, point.z - 8f, 16f);
            if (ground != null)
            {
                s_ProbeSources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = ground, transform = Matrix4x4.identity, area = 0 });
            }
            report.Append("; teste local: corpo 0.4m ").Append(ProbeBake(point, bounds, BodyRadius, s_Settings.minRegionArea));
            report.Append(", corpo 0.2m sem descarte ").Append(ProbeBake(point, bounds, 0.2f, 0f));
            report.Append(" (descarte atual ").Append(s_Settings.minRegionArea.ToString("0.0")).Append("m2)");
            if (ground != null)
            {
                UnityEngine.Object.Destroy(ground);
            }
            s_ProbeSources.Clear();
            return report.ToString();
        }

        private static string Describe(Collider collider)
        {
            var path = collider.name;
            var parent = collider.transform.parent;
            for (var depth = 0; parent != null && depth < 3; depth++, parent = parent.parent)
            {
                path = parent.name + "/" + path;
            }
            var kind = collider.GetType().Name;
            if (collider is MeshCollider mesh)
            {
                kind += mesh.sharedMesh == null ? " sem malha" : (mesh.sharedMesh.isReadable ? " legivel" : " NAO legivel") + (mesh.convex ? " convexo" : "");
            }
            return "'" + path + "' camada " + collider.gameObject.layer + " " + kind;
        }

        private static string ProbeBake(Vector3 point, Bounds bounds, float radius, float minRegion)
        {
            var settings = NavMesh.CreateSettings();
            settings.agentRadius = radius;
            settings.agentHeight = s_Settings.agentHeight;
            settings.agentClimb = s_Settings.agentClimb;
            settings.agentSlope = s_Settings.agentSlope;
            settings.overrideVoxelSize = true;
            settings.voxelSize = radius < 0.3f ? 0.1f : VoxelSize;
            settings.minRegionArea = minRegion;
            var data = NavMeshBuilder.BuildNavMeshData(settings, s_ProbeSources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                NavMesh.RemoveSettings(settings.agentTypeID);
                return "falhou";
            }
            var instance = NavMesh.AddNavMeshData(data);
            var filter = new NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = NavMesh.AllAreas };
            var result = NavMesh.SamplePosition(point, out var near, 6f, filter)
                ? "malha a " + (near.position - point).magnitude.ToString("0.0") + "m"
                : "sem malha ate 6m";
            NavMesh.RemoveNavMeshData(instance);
            UnityEngine.Object.Destroy(data);
            NavMesh.RemoveSettings(settings.agentTypeID);
            return result;
        }

        // Why the bot at `from` can't walk straight onto the mesh point
        // `onMesh`, or null when it can.
        private static bool TryFindStartOnThisSide(Vector3 from, out NavMeshHit start)
        {
            start = default;
            var best = float.PositiveInfinity;
            for (var radius = 0.6f; radius <= 3f; radius += 0.6f)
            {
                for (var angle = 0; angle < 360; angle += 30)
                {
                    var probe = from + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
                    if (!NavMesh.SamplePosition(probe, out var hit, 2.5f, s_Filter)
                        || hit.position.y > from.y + 0.6f)
                    {
                        continue;
                    }
                    var distance = (hit.position - from).magnitude;
                    if (distance >= best || WhatSeparates(from, hit.position) != null)
                    {
                        continue;
                    }
                    best = distance;
                    start = hit;
                }
                if (best < float.PositiveInfinity)
                {
                    return true;
                }
            }
            return false;
        }

        private static string? WhatSeparates(Vector3 from, Vector3 onMesh)
        {
            var rise = onMesh.y - from.y;
            if (rise > 0.6f)
            {
                return "em outro andar (" + rise.ToString("0.0") + "m)";
            }
            if (rise < -0.6f)
            {
                // A step down is walked off, like a player getting off a couch:
                // in the 0.1.131 test a bot stood on a couch for 30 min because
                // the floor 0.7m below was "another floor". It is one only when
                // something solid lies between the bot and that floor.
                if (rise < -MaxDropMeters)
                {
                    return "em outro andar (" + rise.ToString("0.0") + "m)";
                }
                var over = new Vector3(onMesh.x, from.y + 1f, onMesh.z);
                if (Physics.Linecast(from + Vector3.up * 1f, over, out var side, SourceMask, QueryTriggerInteraction.Ignore))
                {
                    return "atras de " + DescribeMeshRole(side.collider) + " a " + side.distance.ToString("0.0") + "m do bot";
                }
                if (Physics.Linecast(over, onMesh + Vector3.up * 0.3f, out var floor, SourceMask, QueryTriggerInteraction.Ignore))
                {
                    return "em outro andar (" + rise.ToString("0.0") + "m, piso " + Describe(floor.collider) + " no meio)";
                }
                return null;
            }
            var flat = onMesh - from;
            flat.y = 0f;
            if (flat.magnitude < 0.2f)
            {
                return null;
            }
            foreach (var height in s_BodyHeights)
            {
                if (Physics.Linecast(from + Vector3.up * height, onMesh + Vector3.up * height, out var wall, SourceMask, QueryTriggerInteraction.Ignore))
                {
                    return "atras de " + DescribeMeshRole(wall.collider) + " a " + wall.distance.ToString("0.0") + "m do bot";
                }
            }
            return null;
        }

        private static readonly float[] s_BodyHeights = { 0.5f, 1.0f, 1.6f };

        private static readonly Dictionary<string, float> s_ExplainedAt = new Dictionary<string, float>();

        // A collider the bot ran into while the route went through it. Says
        // whether it reached the bake and whether baking it now would block
        // the way: the Lodge shelf at Camp Kazan stopped bots 199 times in
        // the 0.1.131 test with the shelf reported "in the mesh".
        public static string? ExplainBlocker(Collider blocker, Vector3 from, Vector3 corner)
        {
            if (!s_Started || blocker == null)
            {
                return null;
            }
            var now = Time.realtimeSinceStartup;
            if (s_ExplainedAt.TryGetValue(blocker.name, out var at) && now - at < 600f)
            {
                return null;
            }
            s_ExplainedAt[blocker.name] = now;
            var report = new System.Text.StringBuilder();
            report.Append("ativo ").Append(blocker.enabled && blocker.gameObject.activeInHierarchy)
                .Append(", tamanho ").Append(blocker.bounds.size.ToString("0.0"))
                .Append(", escala ").Append(blocker.transform.lossyScale.ToString("0.00"));
            if (blocker is MeshCollider meshCollider)
            {
                report.Append(", convexo ").Append(meshCollider.convex)
                    .Append(", vertices ").Append(meshCollider.sharedMesh != null ? meshCollider.sharedMesh.vertexCount : -1);
            }
            var bounds = new Bounds((from + corner) * 0.5f, new Vector3(16f, 8f, 16f));
            s_ProbeSources.Clear();
            NavMeshBuilder.CollectSources(bounds, SourceMask, NavMeshCollectGeometry.PhysicsColliders, 0, s_NoMarkups, s_ProbeSources);
            var collected = false;
            for (var index = s_ProbeSources.Count - 1; index >= 0; index--)
            {
                var source = s_ProbeSources[index];
                if (source.component == blocker)
                {
                    collected = true;
                }
                if (source.shape == NavMeshBuildSourceShape.Terrain
                    || (source.component is Component component
                        && component != null
                        && (((1 << component.gameObject.layer) & MovingMask) != 0
                            || (component is Collider collider && collider.isTrigger)
                            || IsDoor(component))))
                {
                    if (source.component == blocker)
                    {
                        report.Append(", tirado na geracao");
                    }
                    s_ProbeSources.RemoveAt(index);
                }
            }
            report.Append(", veio na coleta ").Append(collected);
            var ground = BuildTerrainMesh(bounds.min.x, bounds.min.z, 16f);
            if (ground != null)
            {
                s_ProbeSources.Add(new NavMeshBuildSource { shape = NavMeshBuildSourceShape.Mesh, sourceObject = ground, transform = Matrix4x4.identity, area = 0 });
            }
            AddWaterSources(bounds, s_ProbeSources);
            var live = NavMesh.SamplePosition(from, out var liveStart, 1.5f, s_Filter)
                && !NavMesh.Raycast(liveStart.position, corner, out _, s_Filter);
            report.Append("; malha atual: caminho ").Append(live ? "livre" : "bloqueado");
            var settings = NavMesh.CreateSettings();
            settings.agentRadius = s_Settings.agentRadius;
            settings.agentHeight = s_Settings.agentHeight;
            settings.agentClimb = s_Settings.agentClimb;
            settings.agentSlope = s_Settings.agentSlope;
            settings.overrideVoxelSize = true;
            settings.voxelSize = VoxelSize;
            var data = NavMeshBuilder.BuildNavMeshData(settings, s_ProbeSources, bounds, Vector3.zero, Quaternion.identity);
            if (data != null)
            {
                var instance = NavMesh.AddNavMeshData(data);
                var filter = new NavMeshQueryFilter { agentTypeID = settings.agentTypeID, areaMask = NavMesh.AllAreas };
                var fresh = NavMesh.SamplePosition(from, out var freshStart, 1.5f, filter)
                    && !NavMesh.Raycast(freshStart.position, corner, out _, filter);
                report.Append("; gerando de novo agora: caminho ").Append(fresh ? "livre" : "bloqueado");
                NavMesh.RemoveNavMeshData(instance);
                UnityEngine.Object.Destroy(data);
            }
            NavMesh.RemoveSettings(settings.agentTypeID);
            if (ground != null)
            {
                UnityEngine.Object.Destroy(ground);
            }
            s_ProbeSources.Clear();
            return report.ToString();
        }

        // Water deeper than half a meter is not floor: the beds of rivers,
        // lakes and the sea were walkable mesh, so routes went straight
        // through them. In the 0.1.123 test 30 trips and 13 loot runs ended
        // in the same river by Nordvik Farm, and the swims out are what sent
        // two bots into the island teleport loop. Each water volume goes in
        // as a not-walkable box up to 0.5m under its surface; shallow water,
        // bridges and docks above it stay floor.
        private const int NotWalkableArea = 1;
        private const float WadeDepth = 0.5f;

        private static void AddWaterSources(Bounds collect, List<NavMeshBuildSource> sources)
        {
            var manager = VolumeManager<WaterVolume, WaterVolumeManager>.Get();
            if (manager == null)
            {
                return;
            }
            foreach (var volume in manager.GetAllVolumes())
            {
                if (volume == null || !(volume.volumeCollider is BoxCollider box) || !box.bounds.Intersects(collect))
                {
                    continue;
                }
                var scale = box.transform.lossyScale;
                var size = Vector3.Scale(box.size, scale);
                size = new Vector3(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
                if (size.y <= WadeDepth)
                {
                    continue;
                }
                var center = box.transform.TransformPoint(box.center) - box.transform.up * (WadeDepth * 0.5f);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    transform = Matrix4x4.TRS(center, box.transform.rotation, Vector3.one),
                    size = new Vector3(size.x, size.y - WadeDepth, size.z),
                    area = NotWalkableArea
                });
            }
        }

        // A floor point of the new mesh between `minimum` and `maximum` meters
        // from `from`, where the player's body fits, for a last-resort
        // teleport out of a spot the bot can't walk out of.
        public static bool TryFindFreeSpot(Vector3 from, float minimum, float maximum, out Vector3 spot)
        {
            spot = from;
            if (!Enabled || !s_Started)
            {
                return false;
            }
            for (var ring = minimum; ring <= maximum; ring += 1.5f)
            {
                for (var step = 0; step < 12; step++)
                {
                    var angle = step * 30f * Mathf.Deg2Rad;
                    var probe = from + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * ring;
                    if (!NavMesh.SamplePosition(probe, out var hit, 1.5f, s_Filter)
                        || Mathf.Abs(hit.position.y - from.y) > 2.5f
                        || WaterUtility.isPointUnderwater(hit.position + Vector3.up * 0.5f)
                        || Physics.CheckCapsule(
                            hit.position + Vector3.up * (PlayerStance.RADIUS + 0.1f),
                            hit.position + Vector3.up * (PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS),
                            PlayerStance.RADIUS - 0.04f,
                            SourceMask & ~(RayMasks.GROUND | RayMasks.GROUND2),
                            QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    spot = hit.position + Vector3.up * 0.1f;
                    return true;
                }
            }
            return false;
        }

        // What the bot stands on and how far the mesh is: a bot on a floor
        // the mesh lacks shows which collider the bake missed.
        public static string DescribeStanding(Vector3 position)
        {
            var standing = Physics.Raycast(position + Vector3.up * 0.3f, Vector3.down, out var ground, 1.5f, ~MovingMask, QueryTriggerInteraction.Ignore)
                ? "pisando em " + DescribeMeshRole(ground.collider)
                : "sem chao ate 1,2m abaixo";
            var mesh = NavMesh.SamplePosition(position, out var near, 6f, s_Filter)
                ? "malha a " + (near.position - position).GetHorizontalMagnitude().ToString("0.0") + "m na horizontal, altura " + (near.position.y - position.y).ToString("0.0") + "m"
                : "sem malha ate 6m";
            return standing + "; " + mesh;
        }

        private static Vector3 Grounded(Vector3 point)
        {
            if (NavMesh.SamplePosition(point, out _, 1f, s_Filter)
                || !Physics.Raycast(point + Vector3.up * 0.3f, Vector3.down, out var hit, 4f, SourceMask, QueryTriggerInteraction.Ignore))
            {
                return point;
            }
            return hit.point;
        }

        private struct DropCandidate
        {
            public Vector3 Edge;
            public Vector3 Landing;
            public float Score;
        }

        private static readonly List<DropCandidate> s_DropCandidates = new List<DropCandidate>();
        private static NavMeshPath? s_DropPath;

        // A route the mesh cannot finish because it ends on a rock or roof
        // with no walkable way down: step off its edge where the ground is
        // 0.6-2.4m below and the rest of the route goes on from there.
        private static bool TryDropRoute(Vector3 start, Vector3 end, List<Vector3> output)
        {
            var corners = s_Path!.corners;
            if (corners.Length == 0)
            {
                return false;
            }
            var stop = corners[corners.Length - 1];
            s_DropPath ??= new NavMeshPath();
            s_DropCandidates.Clear();
            for (var step = 0; step < 16; step++)
            {
                var angle = step * Mathf.PI / 8f;
                var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                if (!NavMesh.Raycast(stop, stop + direction * 4f, out var edgeHit, s_Filter))
                {
                    continue;
                }
                var edge = edgeHit.position;
                var beyond = edge + direction * 0.9f;
                if (Physics.Linecast(edge + Vector3.up * 0.9f, beyond + Vector3.up * 0.9f, SourceMask, QueryTriggerInteraction.Ignore)
                    || !Physics.Raycast(beyond + Vector3.up * 0.5f, Vector3.down, out var ground, MaxDropMeters + 0.9f, SourceMask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                var drop = edge.y - ground.point.y;
                if (drop < MinDropMeters
                    || drop > MaxDropMeters
                    || WaterUtility.isPointUnderwater(ground.point + Vector3.up * 0.5f)
                    || !NavMesh.SamplePosition(ground.point, out var landing, 0.6f, s_Filter)
                    || Mathf.Abs(landing.position.y - ground.point.y) > 0.4f)
                {
                    continue;
                }
                s_DropCandidates.Add(new DropCandidate
                {
                    Edge = edge,
                    Landing = landing.position,
                    Score = (landing.position - end).magnitude + (edge - stop).magnitude
                });
            }
            s_DropCandidates.Sort((a, b) => a.Score.CompareTo(b.Score));
            for (var index = 0; index < s_DropCandidates.Count && index < 4; index++)
            {
                var candidate = s_DropCandidates[index];
                if (!NavMesh.CalculatePath(candidate.Landing, end, s_Filter, s_DropPath)
                    || s_DropPath.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }
                var after = s_DropPath.corners;
                if (!NavMesh.CalculatePath(start, candidate.Edge, s_Filter, s_DropPath)
                    || s_DropPath.status != NavMeshPathStatus.PathComplete)
                {
                    continue;
                }
                var before = s_DropPath.corners;
                for (var corner = 1; corner < before.Length; corner++)
                {
                    output.Add(before[corner]);
                }
                output.Add(candidate.Landing);
                for (var corner = 1; corner < after.Length; corner++)
                {
                    output.Add(after[corner]);
                }
                if (s_Logger != null && Time.realtimeSinceStartup >= s_NextDropLogAt)
                {
                    s_NextDropLogAt = Time.realtimeSinceStartup + 20f;
                    s_Logger.LogInformation(
                        "Malha nova: rota com descida de beirada em ({X:0.0}, {Y:0.0}, {Z:0.0}), queda de {Drop:0.0}m.",
                        candidate.Edge.x, candidate.Edge.y, candidate.Edge.z,
                        candidate.Edge.y - candidate.Landing.y);
                }
                return true;
            }
            return false;
        }

        private static float s_NextDropLogAt;

        // A walkable spot of the new mesh 2.5-8m away that the body can walk
        // to in a straight line: the way out of a cluster of boulders.
        public static bool TryFindEscapePoint(Vector3 position, Vector3 toward, out Vector3 point)
        {
            point = position;
            if (!s_Started)
            {
                return false;
            }
            Want(position, 0);
            toward.y = 0f;
            var preferred = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.zero;
            var bestScore = float.MaxValue;
            foreach (var radius in s_EscapeRadii)
            {
                for (var step = 0; step < 16; step++)
                {
                    var angle = step * Mathf.PI / 8f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    if (!NavMesh.SamplePosition(position + direction * radius, out var hit, 1.5f, s_Filter))
                    {
                        continue;
                    }
                    var candidate = hit.position;
                    var offset = candidate - position;
                    if (offset.GetHorizontalMagnitude() < 2.5f
                        || offset.y > 1.2f
                        || offset.y < -2.4f
                        || WaterUtility.isPointUnderwater(candidate + Vector3.up * 0.5f)
                        || Physics.Linecast(position + Vector3.up * 0.6f, candidate + Vector3.up * 0.6f, SourceMask, QueryTriggerInteraction.Ignore)
                        || Physics.Linecast(position + Vector3.up * 1.4f, candidate + Vector3.up * 1.4f, SourceMask, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    // Nearest ring first; within a ring, toward the objective.
                    var score = radius * 10f - Vector3.Dot(direction, preferred);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        point = candidate;
                    }
                }
                if (bestScore < float.MaxValue)
                {
                    return true;
                }
            }
            return false;
        }

        private static readonly float[] s_EscapeRadii = { 3f, 5f, 8f };

        // Last resort after five circles: the 0.1.100 log teleported a bot
        // 3m inside the boulder pit it had fallen into. Pick floor 8 to 40m
        // away, above water, from where the mesh has a complete route to the
        // objective.
        private static readonly float[] s_TeleportRadii = { 8f, 15f, 25f, 40f };
        private static readonly List<Vector3> s_TeleportScratch = new List<Vector3>();
        private static readonly List<(float Dot, Vector3 Point)> s_TeleportCandidates = new List<(float, Vector3)>();

        public static bool TryFindTeleportPoint(Vector3 position, Vector3 objective, out Vector3 point)
        {
            point = position;
            if (!s_Started)
            {
                return false;
            }
            var toward = objective - position;
            toward.y = 0f;
            var preferred = toward.sqrMagnitude > 0.01f ? toward.normalized : Vector3.zero;
            foreach (var radius in s_TeleportRadii)
            {
                s_TeleportCandidates.Clear();
                for (var step = 0; step < 16; step++)
                {
                    var angle = step * Mathf.PI / 8f;
                    var direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                    if (!NavMesh.SamplePosition(position + direction * radius, out var hit, 4f, s_Filter))
                    {
                        continue;
                    }
                    var offset = hit.position - position;
                    if (offset.GetHorizontalMagnitude() < 6f
                        || offset.y < -2f
                        || offset.y > 25f
                        || WaterUtility.isPointUnderwater(hit.position + Vector3.up * 0.5f))
                    {
                        continue;
                    }
                    s_TeleportCandidates.Add((Vector3.Dot(direction, preferred), hit.position));
                }
                s_TeleportCandidates.Sort((left, right) => right.Dot.CompareTo(left.Dot));
                for (var index = 0; index < s_TeleportCandidates.Count && index < 4; index++)
                {
                    var candidate = s_TeleportCandidates[index].Point;
                    if (TryFindPath(candidate, objective, s_TeleportScratch))
                    {
                        point = candidate;
                        return true;
                    }
                }
            }
            return false;
        }

        private static Mesh? BuildTerrainMesh(float x0, float z0, float extent)
        {
            var count = Mathf.CeilToInt(extent / TerrainStep) + 1;
            var vertices = new Vector3[count * count];
            var valid = new bool[count * count];
            var any = false;
            for (var row = 0; row < count; row++)
            {
                for (var column = 0; column < count; column++)
                {
                    var point = new Vector3(x0 + column * TerrainStep, 0f, z0 + row * TerrainStep);
                    var index = row * count + column;
                    if (Landscape.getWorldHeight(point, out var height))
                    {
                        point.y = height;
                        valid[index] = true;
                        any = true;
                    }
                    vertices[index] = point;
                }
            }
            if (!any)
            {
                return null;
            }
            var triangles = new List<int>((count - 1) * (count - 1) * 6);
            for (var row = 0; row + 1 < count; row++)
            {
                for (var column = 0; column + 1 < count; column++)
                {
                    var a = row * count + column;
                    var b = a + 1;
                    var c = a + count;
                    var d = c + 1;
                    if (!valid[a] || !valid[b] || !valid[c] || !valid[d])
                    {
                        continue;
                    }
                    var middle = (vertices[a] + vertices[d]) * 0.5f;
                    if (Landscape.IsPointInsideHole(middle))
                    {
                        continue;
                    }
                    triangles.Add(a);
                    triangles.Add(c);
                    triangles.Add(b);
                    triangles.Add(b);
                    triangles.Add(c);
                    triangles.Add(d);
                }
            }
            if (triangles.Count == 0)
            {
                return null;
            }
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static bool IsReadyAlong(Vector3 from, Vector3 to)
        {
            var distance = (to - from).GetHorizontalMagnitude();
            var steps = Mathf.CeilToInt(distance / (ChunkSize * 0.5f));
            for (var step = 0; step <= steps; step++)
            {
                var point = Vector3.Lerp(from, to, steps == 0 ? 0f : (float)step / steps);
                if (!HasPiece(Key(Cell(point.x), Cell(point.z))))
                {
                    return false;
                }
            }
            return true;
        }

        // The nearest floor of the new mesh that comes out of the water at
        // most 1.3m above its surface: a bank the player can climb from a
        // swim. The mesh has no floor on banks steeper than 59 degrees and
        // a bridge deck stands too high over the water.
        public static bool TryFindShore(Vector3 position, float surface, Func<Vector3, bool> rejected, out Vector3 land)
        {
            land = position;
            if (!s_Started || !HasPiece(Key(Cell(position.x), Cell(position.z))))
            {
                return false;
            }
            for (var radius = 3f; radius <= 60f; radius += 3f)
            {
                var bestDistance = float.PositiveInfinity;
                var found = false;
                for (var angle = 0; angle < 360; angle += 15)
                {
                    var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    var probe = new Vector3(position.x, surface + 0.5f, position.z) + direction * radius;
                    if (!NavMesh.SamplePosition(probe, out var hit, 2f, s_Filter)
                        || hit.position.y < surface - 0.2f
                        || hit.position.y > surface + 1.3f
                        || WaterUtility.isPointUnderwater(hit.position + Vector3.up * 0.3f)
                        || rejected(hit.position))
                    {
                        continue;
                    }
                    var distance = (hit.position - position).GetHorizontalMagnitude();
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        land = hit.position;
                        found = true;
                    }
                }
                if (found)
                {
                    return true;
                }
            }
            return false;
        }

        // Nothing but floor between the bot and `to` on the new mesh, at the
        // body's width from every wall.
        // Standing on the mesh itself, not beside it or above it.
        public static bool IsOnMesh(Vector3 position)
        {
            return s_Started
                   && NavMesh.SamplePosition(position, out var hit, 1f, s_Filter)
                   && (hit.position - position).GetHorizontalMagnitude() <= 0.3f;
        }

        // A route turns exactly at the mesh edge, and the edge is exactly the
        // body's radius from the wall: at every door frame and building
        // corner the body passed the wall with no room at all, and any turn
        // or dodge put it into the wall. In the 0.1.135 test the blocking
        // wall was 0.0-0.1m from the mesh edge in almost every stop (48 at
        // the Roundhouse alone). Each corner moves into the mesh, away from
        // the nearest edge, as far as the floor allows (up to 0.3m) while
        // both legs to it stay in a straight line on the mesh. The mesh is
        // not shrunk: a passage only as wide as the body keeps its corner.
        private static readonly float[] s_CornerPulls = { 0.3f, 0.2f, 0.1f };

        private static void PullCornersOffWalls(Vector3 start, List<Vector3> corners)
        {
            // The last one is the destination itself.
            for (var index = 0; index < corners.Count - 1; index++)
            {
                var corner = corners[index];
                // 0.2.2: the ends of a shortcut stay where the link is.
                if (IsShortcutEnd(corner))
                {
                    continue;
                }
                if (!NavMesh.FindClosestEdge(corner, out var edge, s_Filter)
                    || edge.distance > 0.25f)
                {
                    continue;
                }
                var away = edge.normal;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f)
                {
                    continue;
                }
                away.Normalize();
                var previous = index == 0 ? start : corners[index - 1];
                var next = corners[index + 1];
                foreach (var pull in s_CornerPulls)
                {
                    if (!NavMesh.SamplePosition(corner + away * pull, out var moved, 0.3f, s_Filter)
                        || Mathf.Abs(moved.position.y - corner.y) > 0.3f
                        || !NavMesh.FindClosestEdge(moved.position, out var movedEdge, s_Filter)
                        || movedEdge.distance <= edge.distance + 0.05f
                        || !RaycastAcrossPieces(previous, moved.position, out _)
                        || !RaycastAcrossPieces(moved.position, next, out _))
                    {
                        continue;
                    }
                    corners[index] = moved.position;
                    s_CornersPulled++;
                    break;
                }
            }
        }

        // The mesh is baked with the body's own radius, but in 0.133m voxels
        // and with its outline simplified afterwards: its edge can come
        // 0.2-0.3m closer to a wall than the radius, most at building corners
        // and door frames. In the 0.1.141 test 39 of 50 route stops were the
        // body touching (0.0-0.1m) an object that is in the mesh, while
        // standing on the mesh. Each leg is swept with the body's capsule
        // against the real colliders; where it would touch, a corner is added
        // beside the contact, away from the collider, just as far as needed
        // and still on the mesh. The mesh itself keeps the body's radius.
        private static readonly float[] s_LegBends = { 0.15f, 0.25f, 0.35f };
        private const int MaxLegBendsPerRoute = 16;

        // The body's capsule goes from `from` to `to` without touching a
        // fixed collider (doors excepted: they open).
        // A complete route on the new mesh joins the two points. For the
        // places that pick a destination (firing spot, remembered loot,
        // doorway, local exploration, retreat) and only asked the zombie
        // graph, cut in thousands of pieces. At most 40 queries a second.
        private static NavMeshPath? s_ReachPath;
        private static float s_ReachSecond;
        private static int s_ReachCount;

        // The nearest dry land across water that the bot's own piece of
        // floor does not walk to: rings of 20-300m around it, the straight
        // line crossing water, landing on a bank low enough to climb.
        public static bool TryFindWaterCrossing(Vector3 position, out Vector3 land, out Vector3 shallows, out float swim)
        {
            land = position;
            shallows = position;
            swim = 0f;
            if (!MapFilled || !NavMesh.SamplePosition(Grounded(position), out var start, 3f, s_Filter))
            {
                return false;
            }
            s_ReachPath ??= new NavMeshPath();
            var checks = 0;
            for (var radius = 20f; radius <= 300f; radius += 20f)
            {
                var bestSwim = float.PositiveInfinity;
                for (var angle = 0; angle < 360; angle += 15)
                {
                    var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    var probe = position + direction * radius;
                    if (!NavMesh.SamplePosition(probe, out var hit, 3f, s_Filter)
                        || WaterUtility.isPointUnderwater(hit.position + Vector3.up * 0.3f))
                    {
                        continue;
                    }
                    // Along the line: first water, last water, and no ground
                    // too high to climb out onto.
                    var offset = hit.position - position;
                    offset.y = 0f;
                    var steps = Mathf.CeilToInt(offset.magnitude / 2f);
                    var firstWet = -1;
                    var lastWet = -1;
                    var surface = 0f;
                    for (var step = 1; step < steps; step++)
                    {
                        var point = position + offset * (step / (float)steps);
                        if (!Physics.Raycast(new Vector3(point.x, point.y + 60f, point.z), Vector3.down, out var ground, 120f, SourceMask, QueryTriggerInteraction.Ignore))
                        {
                            continue;
                        }
                        WaterUtility.getUnderwaterInfo(ground.point + Vector3.up * 0.3f, out var wet, out var waterTop);
                        if (wet)
                        {
                            if (firstWet < 0)
                            {
                                firstWet = step;
                            }
                            lastWet = step;
                            surface = waterTop;
                        }
                    }
                    if (firstWet < 0
                        || hit.position.y > surface + 1.3f
                        || hit.position.y < surface - 0.2f)
                    {
                        continue;
                    }
                    var swimLength = (lastWet - firstWet + 1) * offset.magnitude / steps;
                    // Under 120m of water: the swim rescue teleports after 60s.
                    if (swimLength >= bestSwim || swimLength > 120f || ++checks > 60)
                    {
                        continue;
                    }
                    // Walkable from here: not a crossing, the same floor.
                    if (NavMesh.CalculatePath(start.position, hit.position, s_Filter, s_ReachPath)
                        && s_ReachPath.status == NavMeshPathStatus.PathComplete)
                    {
                        continue;
                    }
                    bestSwim = swimLength;
                    land = hit.position;
                    var toLand = offset.normalized;
                    shallows = new Vector3(hit.position.x, surface, hit.position.z) - toLand * 2f;
                    swim = swimLength;
                }
                if (bestSwim < float.PositiveInfinity)
                {
                    return true;
                }
            }
            return false;
        }

        // Where the mesh gets closest to a point it may not reach: the end of
        // the partial route. Trips with no terrain route walked a straight
        // line of points and failed when none of them was reachable (37 of
        // 39 failed trips in the 0.1.150 test), though the mesh reached far
        // toward the destination around the river or the fence.
        private static NavMeshPath? s_PartialPath;

        public static bool TryFindClosestStop(Vector3 from, Vector3 to, out Vector3 stop)
        {
            stop = from;
            Want(from, 1);
            Want(to, 0);
            if (!Enabled || !s_Started || (to - from).GetHorizontalMagnitude() > MaxRouteMeters)
            {
                LastFailure = "longe demais";
                return false;
            }
            if (!IsReadyAlong(from, to))
            {
                LastFailure = "pedaco nao pronto";
                return false;
            }
            if (!NavMesh.SamplePosition(Grounded(from), out var start, 2f, s_Filter))
            {
                LastFailure = "bot fora da malha";
                return false;
            }
            if (!NavMesh.SamplePosition(to, out var end, 40f, s_Filter))
            {
                LastFailure = "destino fora da malha";
                return false;
            }
            s_PartialPath ??= new NavMeshPath();
            if (!NavMesh.CalculatePath(start.position, end.position, s_Filter, s_PartialPath)
                || s_PartialPath.status == NavMeshPathStatus.PathInvalid
                || s_PartialPath.corners.Length == 0)
            {
                LastFailure = "sem rota (status " + s_PartialPath.status + ")";
                return false;
            }
            var corners = s_PartialPath.corners;
            // Stop before the route goes under water: a swim is the water
            // exit's job.
            stop = corners[0];
            for (var index = 1; index < corners.Length; index++)
            {
                if (WaterUtility.isPointUnderwater(corners[index] + Vector3.up * 0.5f))
                {
                    break;
                }
                stop = corners[index];
            }
            return true;
        }

        // 0.2.2x6: for the catalog of high places (SkaliaBotVantageMap).
        public static bool TryTriangulate(out Vector3[] vertices, out int[] indices)
        {
            vertices = Array.Empty<Vector3>();
            indices = Array.Empty<int>();
            if (!Enabled || !MapFilled)
            {
                return false;
            }
            var triangulation = NavMesh.CalculateTriangulation();
            vertices = triangulation.vertices ?? Array.Empty<Vector3>();
            indices = triangulation.indices ?? Array.Empty<int>();
            return indices.Length >= 3;
        }

        // A complete route, ladders included, with no distance cap nor the
        // 40-a-second budget of IsReachable: for the catalog at boot.
        public static bool IsPathComplete(Vector3 from, Vector3 to)
        {
            if (!MapFilled
                || !NavMesh.SamplePosition(from, out var start, 2f, s_Filter)
                || !NavMesh.SamplePosition(to, out var end, 1.5f, s_Filter)
                || Mathf.Abs(end.position.y - to.y) > 2f)
            {
                return false;
            }
            s_CatalogPath ??= new NavMeshPath();
            return NavMesh.CalculatePath(start.position, end.position, s_Filter, s_CatalogPath)
                   && s_CatalogPath.status == NavMeshPathStatus.PathComplete;
        }

        private static NavMeshPath? s_CatalogPath;

        public static bool IsReachable(Vector3 from, Vector3 to)
        {
            if (!MapFilled || (to - from).GetHorizontalMagnitude() > MaxRouteMeters)
            {
                return false;
            }
            var second = Mathf.Floor(Time.realtimeSinceStartup);
            if (second != s_ReachSecond)
            {
                s_ReachSecond = second;
                s_ReachCount = 0;
            }
            // 0.2.2x9: the only reach check now (the zombie mesh answered
            // most of them before).
            if (++s_ReachCount > 120)
            {
                return false;
            }
            if (!NavMesh.SamplePosition(Grounded(from), out var start, 2f, s_Filter)
                || !NavMesh.SamplePosition(to, out var end, 1.5f, s_Filter)
                || Mathf.Abs(end.position.y - to.y) > 2f)
            {
                return false;
            }
            s_ReachPath ??= new NavMeshPath();
            return NavMesh.CalculatePath(start.position, end.position, s_Filter, s_ReachPath)
                   && s_ReachPath.status == NavMeshPathStatus.PathComplete;
        }

        // The capsule touched something low, at step height above the floor
        // under the contact: a step, a curb, a stair riser, which the body
        // walks up.
        private static bool IsStepHit(Vector3 from, Vector3 direction, RaycastHit hit)
        {
            var floor = from + direction * hit.distance;
            return hit.point.y - floor.y < 0.6f;
        }

        public static bool IsBodyClear(Vector3 from, Vector3 to)
        {
            var leg = to - from;
            var length = leg.magnitude;
            if (length < 0.05f)
            {
                return true;
            }
            var lift = PlayerStance.RADIUS + 0.45f;
            return !Physics.CapsuleCast(
                       from + Vector3.up * lift,
                       from + Vector3.up * (PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS),
                       PlayerStance.RADIUS - 0.02f,
                       leg / length,
                       out var hit,
                       length,
                       RayMasks.BLOCK_COLLISION & ~MovingMask,
                       QueryTriggerInteraction.Ignore)
                   || IsDoor(hit.collider)
                   || Mathf.Abs(hit.normal.y) > 0.7f
                   || IsStepHit(from, leg / length, hit)
                   || (Mathf.Abs(leg.y) > 0.3f && Mathf.Abs(leg.y) > length * 0.12f);
        }

        private static void BendLegsOffColliders(Vector3 start, List<Vector3> corners)
        {
            var mask = RayMasks.BLOCK_COLLISION & ~MovingMask;
            var radius = PlayerStance.RADIUS - 0.02f;
            var lift = PlayerStance.RADIUS + 0.45f;
            var height = PlayerMovement.HEIGHT_STAND - PlayerStance.RADIUS;
            var bends = 0;
            var tries = 0;
            for (var index = 0; index < corners.Count && bends < MaxLegBendsPerRoute && tries < 64; index++)
            {
                tries++;
                var from = index == 0 ? start : corners[index - 1];
                var to = corners[index];
                var leg = to - from;
                var length = leg.magnitude;
                if (length < 0.2f)
                {
                    continue;
                }
                var direction = leg / length;
                // Stairs and ramps: the risers face the leg and the mesh
                // follows the steps only roughly. In the 0.1.150 test a route
                // up the stairs of an apartment at (-903, -56) came out with
                // corners 0.5m apart and the bot circled at their foot.
                if (Mathf.Abs(leg.y) > 0.3f && Mathf.Abs(leg.y) > length * 0.12f)
                {
                    continue;
                }
                if (!Physics.CapsuleCast(
                        from + Vector3.up * lift,
                        from + Vector3.up * height,
                        radius,
                        direction,
                        out var hit,
                        length,
                        mask,
                        QueryTriggerInteraction.Ignore)
                    || IsDoor(hit.collider)
                    || Mathf.Abs(hit.normal.y) > 0.7f
                    || IsStepHit(from, direction, hit))
                {
                    continue;
                }
                var flat = new Vector3(direction.x, 0f, direction.z);
                if (flat.sqrMagnitude < 0.01f)
                {
                    continue;
                }
                flat.Normalize();
                // Only the part of the push across the leg: a wall straight
                // ahead is not grazed, the leg goes through it.
                var normal = new Vector3(hit.normal.x, 0f, hit.normal.z);
                var side = normal - flat * Vector3.Dot(normal, flat);
                if (side.magnitude < 0.25f)
                {
                    continue;
                }
                side.Normalize();
                var contact = from + direction * hit.distance;
                var inserted = false;
                foreach (var bend in s_LegBends)
                {
                    if (!NavMesh.SamplePosition(contact + side * bend, out var moved, 0.3f, s_Filter)
                        || Mathf.Abs(moved.position.y - contact.y) > 0.3f
                        || (moved.position - from).GetHorizontalMagnitude() < 0.6f
                        || (moved.position - to).GetHorizontalMagnitude() < 0.6f
                        || Physics.CheckCapsule(
                            moved.position + Vector3.up * lift,
                            moved.position + Vector3.up * height,
                            radius,
                            mask,
                            QueryTriggerInteraction.Ignore)
                        || !RaycastAcrossPieces(from, moved.position, out _)
                        || !RaycastAcrossPieces(moved.position, to, out _))
                    {
                        continue;
                    }
                    var toMoved = moved.position - from;
                    if (toMoved.magnitude > 0.05f
                        && Physics.CapsuleCast(
                            from + Vector3.up * lift,
                            from + Vector3.up * height,
                            radius,
                            toMoved.normalized,
                            out _,
                            toMoved.magnitude,
                            mask,
                            QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    corners.Insert(index, moved.position);
                    bends++;
                    s_LegsBent++;
                    inserted = true;
                    break;
                }
                // The new corner's onward leg is checked on the next turn.
                _ = inserted;
            }
        }

        // The mesh edge cuts the straight line to `to` more than `margin`
        // before reaching it: a wall in between, not a corner grazed at
        // its tip.
        public static bool IsWallBefore(Vector3 from, Vector3 to, float margin)
        {
            if (!s_Started || !NavMesh.SamplePosition(from, out var start, 1f, s_Filter))
            {
                return false;
            }
            if (WhatSeparates(from, start.position) != null)
            {
                // Sampled on the far side of a fence: the bot is off its
                // line for sure.
                return true;
            }
            if (RaycastAcrossPieces(start.position, to, out var hit)
                || (to - hit).GetHorizontalMagnitude() <= margin)
            {
                return false;
            }
            // A bot brushing the mesh edge samples a start on that edge, and
            // the ray "hits" right where it starts: a corner 0.9m away was
            // "not in a straight line" (0.1.141 test). Try again from just
            // inside, toward the corner.
            if ((hit - start.position).GetHorizontalMagnitude() <= 0.2f)
            {
                var toward = to - start.position;
                toward.y = 0f;
                if (toward.magnitude > 0.4f
                    && NavMesh.SamplePosition(start.position + toward.normalized * 0.3f, out var inside, 0.3f, s_Filter)
                    && (inside.position - start.position).GetHorizontalMagnitude() > 0.1f)
                {
                    return !RaycastAcrossPieces(inside.position, to, out var again)
                           && (to - again).GetHorizontalMagnitude() > margin;
                }
            }
            return true;
        }

        public static bool IsStraight(Vector3 from, Vector3 to)
        {
            if (!s_Started
                || Mathf.Abs(to.y - from.y) > 1.5f
                || !NavMesh.SamplePosition(from, out var start, 1.5f, s_Filter))
            {
                return false;
            }
            // A bot pressed against a thin fence is nearer the mesh on the
            // far side of it than on its own (0.2m fence, 0.4m each side):
            // the line was then "straight" from the other side. At 15:29 in
            // the 0.1.141 test the route's detour corners around a metal
            // fence were skipped that way and XXlloqiartyXX, fleeing the
            // burner, pushed into the fence until it died.
            if (WhatSeparates(from, start.position) != null)
            {
                return false;
            }
            return RaycastAcrossPieces(start.position, to, out _);
        }

        // NavMesh.Raycast stops at the edge of the 102m piece it starts in:
        // the pieces are separate meshes joined by links, which a raycast
        // does not follow. In the 0.1.135 test 461 of 1147 "not in a straight
        // line" calls were lines crossing a piece border, not walls. A hit on
        // a border goes on from the next piece. True when the line is clear;
        // otherwise `hit` is where the mesh edge cuts it.
        private static bool RaycastAcrossPieces(Vector3 start, Vector3 to, out Vector3 hit)
        {
            for (var crossing = 0; crossing < 4; crossing++)
            {
                if (!NavMesh.Raycast(start, to, out var edge, s_Filter))
                {
                    hit = to;
                    return true;
                }
                hit = edge.position;
                var direction = to - edge.position;
                direction.y = 0f;
                if (direction.magnitude < 0.05f || !OnPieceBorder(edge.position))
                {
                    return false;
                }
                direction.Normalize();
                if (!NavMesh.SamplePosition(edge.position + direction * (LinkReach + 0.3f), out var next, 0.6f, s_Filter)
                    || (next.position - edge.position).GetHorizontalMagnitude() > LinkReach + 0.6f
                    || Cell(next.position.x) == Cell(edge.position.x - direction.x * 0.3f)
                       && Cell(next.position.z) == Cell(edge.position.z - direction.z * 0.3f))
                {
                    return false;
                }
                start = next.position;
            }
            hit = start;
            return false;
        }

        private static bool OnPieceBorder(Vector3 point)
        {
            if (s_Whole)
            {
                return false;
            }
            var x = point.x / ChunkSize;
            var z = point.z / ChunkSize;
            return Mathf.Abs(x - Mathf.Round(x)) * ChunkSize < BodyRadius + 0.2f
                   || Mathf.Abs(z - Mathf.Round(z)) * ChunkSize < BodyRadius + 0.2f;
        }

        // 0.2.2x9: the route when the mesh has no complete one: as far as it
        // goes toward `to` (whose floor may be up to 8m off the mesh), cut
        // before it goes under water. `stop` is where it ends; the caller
        // decides whether that is near enough. The zombie mesh is no longer
        // asked: its routes start at the ground node under a roof or a tower
        // and end where the bot cannot stand, and the bot walked into the
        // parapet or the fence between them.
        public static bool TryFindReachRoute(Vector3 from, Vector3 to, List<Vector3> output, out Vector3 stop)
        {
            output.Clear();
            stop = from;
            if (!Enabled || !s_Started || s_Path == null)
            {
                LastFailure = "malha nova nao iniciada";
                return false;
            }
            if ((to - from).GetHorizontalMagnitude() > MaxRouteMeters)
            {
                LastFailure = "longe demais";
                return false;
            }
            if (!IsReadyAlong(from, to))
            {
                LastFailure = "pedaco nao pronto";
                return false;
            }
            from = Grounded(from);
            var floor = Grounded(to);
            if (!NavMesh.SamplePosition(from, out var start, 2f, s_Filter))
            {
                LastFailure = "bot fora da malha";
                return false;
            }
            var across = WhatSeparates(from, start.position);
            if (across != null)
            {
                if (!TryFindStartOnThisSide(from, out var thisSide))
                {
                    LastFailure = "inicio " + across;
                    return false;
                }
                start = thisSide;
            }
            if (!NavMesh.SamplePosition(floor, out var end, 2.5f, s_Filter)
                && !NavMesh.SamplePosition(floor, out end, 8f, s_Filter))
            {
                LastFailure = "destino fora da malha (nada a 8m)";
                return false;
            }
            if (!NavMesh.CalculatePath(start.position, end.position, s_Filter, s_Path)
                || s_Path.status == NavMeshPathStatus.PathInvalid
                || s_Path.corners.Length == 0)
            {
                LastFailure = "sem rota (status " + s_Path.status + ")";
                return false;
            }
            var corners = s_Path.corners;
            stop = corners[0];
            for (var index = 1; index < corners.Length; index++)
            {
                if (WaterUtility.isPointUnderwater(corners[index] + Vector3.up * 0.5f)
                    || WaterUtility.isPointUnderwater((corners[index] + corners[index - 1]) * 0.5f + Vector3.up * 0.5f))
                {
                    break;
                }
                output.Add(corners[index]);
                stop = corners[index];
            }
            if (output.Count == 0)
            {
                output.Add(stop);
            }
            PullCornersOffWalls(start.position, output);
            BendLegsOffColliders(start.position, output);
            return true;
        }

        // A complete route on the new mesh from `from` to `to`, without the
        // start corner, or false (LastFailure says why).
        // Why the last TryFindPath failed, for the bot's own log line.
        public static string LastFailure { get; private set; } = string.Empty;
        private const float PartialNearMeters = 2f;

        // The same route without any ladder link; false when only a ladder
        // leads there.
        public static bool TryFindPathWithoutLadders(Vector3 from, Vector3 to, List<Vector3> output)
        {
            s_PathAreaMask = NavMesh.AllAreas & ~(1 << LadderArea);
            try
            {
                return TryFindPath(from, to, output);
            }
            finally
            {
                s_PathAreaMask = NavMesh.AllAreas;
            }
        }

        public static bool TryFindPath(Vector3 from, Vector3 to, List<Vector3> output)
        {
            output.Clear();
            LastFailure = string.Empty;
            Want(from, 1);
            Want(to, 0);
            if (!Enabled
                || !s_Started
                || s_Path == null
                || (to - from).GetHorizontalMagnitude() > MaxRouteMeters
                || !IsReadyAlong(from, to))
            {
                if (Enabled && s_Started)
                {
                    if ((to - from).GetHorizontalMagnitude() > MaxRouteMeters) { s_TooFar++; LastFailure = "longe demais"; }
                    else { s_NotReady++; LastFailure = "pedaco nao pronto"; }
                }
                s_Fallbacks++;
                return false;
            }
            // A bot in the middle of a jump is 2-2.6m above the floor: route
            // from the floor under it (and to the floor under a destination).
            from = Grounded(from);
            to = Grounded(to);
            if (!NavMesh.SamplePosition(from, out var start, 2f, s_Filter)
                || !NavMesh.SamplePosition(to, out var end, 2.5f, s_Filter))
            {
                s_NoSample++;
                LastFailure = !NavMesh.SamplePosition(from, out _, 2f, s_Filter) ? "bot fora da malha" : "destino fora da malha";
                if (s_Logger != null && Time.realtimeSinceStartup >= s_NextOffMeshLogAt)
                {
                    s_NextOffMeshLogAt = Time.realtimeSinceStartup + 20f;
                    var fromOk = NavMesh.SamplePosition(from, out var nearFrom, 6f, s_Filter);
                    var toOk = NavMesh.SamplePosition(to, out var nearTo, 6f, s_Filter);
                    s_Logger.LogInformation(
                        "Malha nova: rota fora da malha; origem ({FromX:0.0}, {FromY:0.0}, {FromZ:0.0}) a {FromDistance} da malha, destino ({ToX:0.0}, {ToY:0.0}, {ToZ:0.0}) a {ToDistance} da malha.",
                        from.x, from.y, from.z,
                        fromOk ? (nearFrom.position - from).magnitude.ToString("0.0") + "m" : "mais de 6m",
                        to.x, to.y, to.z,
                        toOk ? (nearTo.position - to).magnitude.ToString("0.0") + "m" : "mais de 6m");
                    var probe = !NavMesh.SamplePosition(from, out _, 2f, s_Filter) ? from : to;
                    s_Logger.LogInformation("Malha nova: diagnostico em ({X:0.0}, {Y:0.0}, {Z:0.0}): {Report}", probe.x, probe.y, probe.z, Diagnose(probe));
                }
                s_Fallbacks++;
                return false;
            }
            // The start is the mesh point nearest the bot, up to 2m away in
            // any direction: behind the wall it is leaning on, or on the
            // floor below. In the 0.1.123 test 35 of 72 stops were a bot in
            // the Medic building routed from the street behind its wall, and
            // 4 were bots upstairs routed from the floor 5m below.
            var across = WhatSeparates(from, start.position);
            // The nearest mesh point was behind the wall; one a little farther
            // on this side may still be there. Washington test: two bots
            // stood on a stack by Barn_1 at (610, 40, 39) for minutes, every
            // route starting behind the barn wall, and one died there.
            if (across != null && TryFindStartOnThisSide(from, out var thisSide))
            {
                start = thisSide;
                across = null;
            }
            if (across != null)
            {
                s_StartAcross++;
                LastFailure = "inicio " + across;
                if (s_Logger != null && Time.realtimeSinceStartup >= s_NextStartLogAt)
                {
                    s_NextStartLogAt = Time.realtimeSinceStartup + 10f;
                    s_Logger.LogInformation(
                        "Malha nova: bot fora da malha em ({X:0.0}, {Y:0.0}, {Z:0.0}); a malha mais perto fica em ({SX:0.0}, {SY:0.0}, {SZ:0.0}), {Across}. {Standing}.",
                        from.x, from.y, from.z, start.position.x, start.position.y, start.position.z, across, DescribeStanding(from));
                }
                s_Fallbacks++;
                return false;
            }
            var pathFilter = s_Filter;
            pathFilter.areaMask = s_PathAreaMask;
            if (!NavMesh.CalculatePath(start.position, end.position, pathFilter, s_Path)
                || s_Path.status != NavMeshPathStatus.PathComplete)
            {
                if (s_Path.status == NavMeshPathStatus.PathPartial
                    && s_PathAreaMask == NavMesh.AllAreas
                    && TryDropRoute(start.position, end.position, output))
                {
                    s_Drops++;
                    s_Routes++;
                    return true;
                }
                // The mesh reaches within 2m of the destination (an item on
                // a table, a zombie on a rock): walking there beats the zombie
                // mesh, whose routes are the ones that run through walls.
                if (s_Path.status == NavMeshPathStatus.PathPartial && s_Path.corners.Length > 0)
                {
                    var partial = s_Path.corners;
                    var stop = partial[partial.Length - 1];
                    if ((stop - end.position).GetHorizontalMagnitude() <= PartialNearMeters
                        && Mathf.Abs(stop.y - end.position.y) <= 2f)
                    {
                        for (var index = 1; index < partial.Length; index++)
                        {
                            output.Add(partial[index]);
                        }
                        if (output.Count == 0)
                        {
                            output.Add(stop);
                        }
                        PullCornersOffWalls(start.position, output);
                        BendLegsOffColliders(start.position, output);
                        s_Routes++;
                        return true;
                    }
                    LastFailure = "sem rota completa (a malha para a "
                        + (stop - end.position).GetHorizontalMagnitude().ToString("0.0") + "m do destino, em "
                        + stop.x.ToString("0") + ", " + stop.y.ToString("0") + ", " + stop.z.ToString("0") + ")";
                }
                else
                {
                    LastFailure = "sem rota (status " + s_Path.status + ")";
                }
                s_Incomplete++;
                s_Fallbacks++;
                return false;
            }
            var corners = s_Path.corners;
            // The mesh also covers sea and river beds: a route under water is
            // a swim the zombie mesh and the water exit already handle.
            for (var index = 0; index < corners.Length; index++)
            {
                if (WaterUtility.isPointUnderwater(corners[index] + Vector3.up * 0.5f)
                    || (index > 0
                        && WaterUtility.isPointUnderwater((corners[index] + corners[index - 1]) * 0.5f + Vector3.up * 0.5f)))
                {
                    s_Underwater++;
                    LastFailure = "rota debaixo d'agua";
                    s_Fallbacks++;
                    return false;
                }
            }
            for (var index = 1; index < corners.Length; index++)
            {
                output.Add(corners[index]);
            }
            if (output.Count == 0)
            {
                output.Add(end.position);
            }
            PullCornersOffWalls(start.position, output);
            BendLegsOffColliders(start.position, output);
            s_Routes++;
            return true;
        }
    }
}
