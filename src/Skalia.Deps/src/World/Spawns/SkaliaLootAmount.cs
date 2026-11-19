using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // 0.1.0-x19: loot above what the game allows.
    // ItemManager keeps spawns x Items/Spawn_Chance items in each region, and
    // Spawn_Chance stops at 1 (every loot point taken; respawnItems also keeps
    // 2m between items). On Normal (0.35) the launcher's 3x is already 1. The
    // launcher writes lootAmount:spawnFactor = difficulty's chance x the
    // chosen multiplier (10x on Normal: 3.5 items per loot point); this tops
    // each region up to that, with items from the point's own spawn table
    // dropped as natural loot (not "dropped": the ground cleanup and the
    // dropped despawn time leave them alone), and refills a region at the
    // game's own Items/Respawn_Time.
    internal static class SkaliaLootAmount
    {
        private const int RegionsPerFrame = 16;
        private const float PointSpreadMeters = 0.3f;

        private static ILogger? s_Logger;
        private static float s_SpawnFactor;
        private static int s_Generation;
        private static bool s_Running;
        private static float[,]? s_LastTopUp;

        internal static void Configure(IConfiguration configuration, ILogger logger)
        {
            s_Logger = logger;
            s_SpawnFactor = Mathf.Clamp(configuration.GetValue("lootAmount:spawnFactor", 0f), 0f, 5f);
        }

        internal static void Start()
        {
            if (s_Running || s_SpawnFactor <= 0f)
            {
                return;
            }
            var chance = Provider.modeConfigData?.Items?.Spawn_Chance ?? 1f;
            if (s_SpawnFactor <= chance + 0.001f)
            {
                s_Logger?.LogInformation(
                    "Loot extra desligado: {Factor:0.##} item(ns) por ponto de loot, o jogo ja poe {Chance:0.##}.",
                    s_SpawnFactor,
                    chance);
                return;
            }
            s_Running = true;
            s_LastTopUp = new float[Regions.WORLD_SIZE, Regions.WORLD_SIZE];
            for (var x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (var y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    s_LastTopUp[x, y] = float.NegativeInfinity;
                }
            }
            RunAsync(++s_Generation).Forget();
        }

        internal static void Stop()
        {
            s_Running = false;
            s_Generation++;
        }

        private static async UniTaskVoid RunAsync(int generation)
        {
            var chance = Provider.modeConfigData?.Items?.Spawn_Chance ?? 1f;
            var firstSweep = true;
            var added = 0;
            var startedAt = Time.realtimeSinceStartup;
            var x = 0;
            var y = 0;
            while (generation == s_Generation)
            {
                await UniTask.Yield(PlayerLoopTiming.Update);
                if (generation != s_Generation)
                {
                    return;
                }
                if (!Provider.isServer || !Level.isLoaded || Level.info == null
                    || Level.info.type == ELevelType.ARENA
                    || ItemManager.regions == null || LevelItems.spawns == null)
                {
                    continue;
                }
                for (var step = 0; step < RegionsPerFrame; step++)
                {
                    try
                    {
                        added += TopUpRegion((byte)x, (byte)y);
                    }
                    catch (Exception exception)
                    {
                        s_Logger?.LogWarning(exception, "Loot extra: erro na regiao {X},{Y}.", x, y);
                    }
                    if (++x < Regions.WORLD_SIZE)
                    {
                        continue;
                    }
                    x = 0;
                    if (++y < Regions.WORLD_SIZE)
                    {
                        continue;
                    }
                    y = 0;
                    if (firstSweep)
                    {
                        firstSweep = false;
                        s_Logger?.LogInformation(
                            "Loot extra: {Added} itens a mais no mapa em {Seconds:0}s ({Factor:0.##} por ponto de loot; o jogo poe {Chance:0.##}). Repoe a cada {Respawn:0}s por regiao.",
                            added,
                            Time.realtimeSinceStartup - startedAt,
                            s_SpawnFactor,
                            chance,
                            Provider.modeConfigData?.Items?.Respawn_Time ?? 0f);
                    }
                }
            }
        }

        private static int TopUpRegion(byte x, byte y)
        {
            var spawns = LevelItems.spawns[x, y];
            var region = ItemManager.regions[x, y];
            if (spawns == null || spawns.Count == 0 || region == null || s_LastTopUp == null)
            {
                return 0;
            }
            var now = Time.realtimeSinceStartup;
            var respawn = Provider.modeConfigData?.Items?.Respawn_Time ?? 60f;
            if (now - s_LastTopUp[x, y] < respawn)
            {
                return 0;
            }
            // Counted like respawnItems: every item lying in the region.
            var missing = (int)(spawns.Count * s_SpawnFactor) - region.items.Count;
            if (missing <= 0)
            {
                return 0;
            }
            // Spread over the points: start at a random one and go round, so
            // the first fill gives every point about the same number.
            var start = UnityEngine.Random.Range(0, spawns.Count);
            var added = 0;
            for (var i = 0; i < missing && i < spawns.Count * 4; i++)
            {
                var spawnpoint = spawns[(start + i) % spawns.Count];
                if (!SafezoneManager.checkPointValid(spawnpoint.point))
                {
                    continue;
                }
                var id = LevelItems.getItem(spawnpoint);
                if (!(Assets.find(EAssetType.ITEM, id) is ItemAsset))
                {
                    continue;
                }
                var point = spawnpoint.point;
                point.x += UnityEngine.Random.Range(-PointSpreadMeters, PointSpreadMeters);
                point.z += UnityEngine.Random.Range(-PointSpreadMeters, PointSpreadMeters);
                var before = region.items.Count;
                ItemManager.dropItem(new Item(id, EItemOrigin.WORLD), point, false, false, false);
                if (region.items.Count > before)
                {
                    added++;
                }
            }
            // Also when nothing landed here (an item can fall into the next
            // region): the region waits Respawn_Time like the game's own.
            s_LastTopUp[x, y] = now;
            return added;
        }
    }
}
