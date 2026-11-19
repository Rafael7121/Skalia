using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;
using LoggingAbstraction = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    internal static class ZombieSpawnScalingService
    {
        private sealed class RegionExpansion
        {
            public List<ZombieSpawnpoint> Spawnpoints = null!;
            public int OriginalSpawnpointCount;
            public FlagData FlagData = null!;
            public byte OriginalMaxZombies;
        }

        private sealed class RegionSpawnSettings
        {
            public float Multiplier;
            public int MegasPerRegion;
        }

        private sealed class MapSpawnSettings
        {
            public float DefaultMultiplier;
            public int DefaultMegasPerRegion;
            public readonly Dictionary<byte, RegionSpawnSettings> Regions =
                new Dictionary<byte, RegionSpawnSettings>();
        }

        private static readonly List<RegionExpansion> s_Expansions =
            new List<RegionExpansion>();
        private static readonly Dictionary<string, MapSpawnSettings>
            s_MapSettings =
                new Dictionary<string, MapSpawnSettings>(
                    StringComparer.OrdinalIgnoreCase);

        private static LoggingAbstraction? s_Logger;
        private static List<ZombieSpawnpoint>[]? s_AppliedSpawnArray;
        private static bool s_Enabled;
        private static float s_GeneralMultiplier = 1f;
        private static int s_MegasPerRegion = 1;
        private static bool s_GuaranteeMegas;
        private static float[]? s_NextGuaranteeCheck;

        private const float VanillaMegaCooldownSeconds = 600f;
        private const float GuaranteeCheckIntervalSeconds = 5f;

        internal static void Configure(
            IConfiguration configuration,
            LoggingAbstraction logger)
        {
            s_Logger = logger;
            s_Enabled = configuration.GetValue(
                "zombieSpawns:enabled", true);
            s_GeneralMultiplier = Mathf.Clamp(configuration.GetValue(
                "zombieSpawns:generalMultiplier", 1f), 1f, 4f);
            s_MegasPerRegion = Mathf.Clamp(configuration.GetValue(
                "zombieSpawns:megasPerRegion", 1), 1, 5);
            s_GuaranteeMegas = configuration.GetValue(
                "zombieSpawns:guaranteeMegas", false);
            s_NextGuaranteeCheck = null;

            s_MapSettings.Clear();
            foreach (IConfigurationSection mapSection in configuration
                .GetSection("zombieSpawns:maps")
                .GetChildren())
            {
                var mapSettings = new MapSpawnSettings
                {
                    DefaultMultiplier = Mathf.Clamp(
                        mapSection.GetValue(
                            "defaultMultiplier",
                            s_GeneralMultiplier),
                        1f,
                        4f),
                    DefaultMegasPerRegion = Mathf.Clamp(
                        mapSection.GetValue(
                            "defaultMegasPerRegion",
                            s_MegasPerRegion),
                        1,
                        5)
                };

                foreach (IConfigurationSection regionSection in mapSection
                    .GetSection("navmeshes")
                    .GetChildren())
                {
                    if (!byte.TryParse(
                        regionSection.Key,
                        out byte regionIndex))
                    {
                        logger.LogWarning(
                            "Navmesh invalido em zombieSpawns:maps:{Map}:navmeshes: {Region}.",
                            mapSection.Key,
                            regionSection.Key);
                        continue;
                    }

                    mapSettings.Regions[regionIndex] =
                        new RegionSpawnSettings
                        {
                            Multiplier = Mathf.Clamp(
                                regionSection.GetValue(
                                    "multiplier",
                                    mapSettings.DefaultMultiplier),
                                1f,
                                4f),
                            MegasPerRegion = Mathf.Clamp(
                                regionSection.GetValue(
                                    "megasPerRegion",
                                    mapSettings.DefaultMegasPerRegion),
                                1,
                                5)
                        };
                }

                s_MapSettings[mapSection.Key] = mapSettings;
                logger.LogInformation(
                    "Escala de zumbis para {Map}: padrao x{Multiplier:0.##}, {Megas} Mega(s), {Overrides} navmesh(es) com override.",
                    mapSection.Key,
                    mapSettings.DefaultMultiplier,
                    mapSettings.DefaultMegasPerRegion,
                    mapSettings.Regions.Count);
            }

            logger.LogInformation(
                "Escala global de zumbis configurada: x{GeneralMultiplier:0.##}, {MegasPerRegion} Mega(s) por regiao, Mega garantido: {Guarantee}.",
                s_Enabled ? s_GeneralMultiplier : 1f,
                s_Enabled ? s_MegasPerRegion : 1,
                s_Enabled && s_GuaranteeMegas);
        }

        internal static void ApplyToLoadedLevel()
        {
            if (!s_Enabled
                || Level.info == null
                || Level.info.type != ELevelType.SURVIVAL
                || LevelZombies.zombies == null
                || LevelNavigation.flagData == null)
            {
                return;
            }

            List<ZombieSpawnpoint>[] spawnArray = LevelZombies.zombies;
            if (ReferenceEquals(s_AppliedSpawnArray, spawnArray))
            {
                return;
            }

            // A troca de mapa substitui os arrays do LevelZombies. Os registros
            // anteriores deixam de fazer parte do nivel ativo e podem ser descartados.
            s_Expansions.Clear();
            s_AppliedSpawnArray = spawnArray;

            int originalTotal = 0;
            int expandedTotal = 0;
            int regionCount = Math.Min(
                spawnArray.Length,
                LevelNavigation.flagData.Count);

            for (int bound = 0; bound < regionCount; bound++)
            {
                List<ZombieSpawnpoint> spawnpoints = spawnArray[bound];
                FlagData flagData = LevelNavigation.flagData[bound];
                int originalCount = spawnpoints.Count;
                if (originalCount == 0)
                {
                    continue;
                }

                float multiplier = GetMultiplierForRegion((byte) bound);
                var expansion = new RegionExpansion
                {
                    Spawnpoints = spawnpoints,
                    OriginalSpawnpointCount = originalCount,
                    FlagData = flagData,
                    OriginalMaxZombies = flagData.maxZombies
                };
                s_Expansions.Add(expansion);

                int targetCount = Mathf.CeilToInt(
                    originalCount * multiplier);
                for (int index = originalCount; index < targetCount; index++)
                {
                    int sourceIndex = (index - originalCount) % originalCount;
                    int cloneLayer =
                        ((index - originalCount) / originalCount) + 1;
                    ZombieSpawnpoint source = spawnpoints[sourceIndex];
                    Vector3 offset = GetSpawnOffset(sourceIndex, cloneLayer);
                    spawnpoints.Add(new ZombieSpawnpoint(
                        source.type,
                        source.point + offset));
                }

                flagData.maxZombies = (byte) Mathf.Clamp(
                    Mathf.CeilToInt(
                        expansion.OriginalMaxZombies * multiplier),
                    0,
                    byte.MaxValue);

                originalTotal += originalCount;
                expandedTotal += spawnpoints.Count;
            }

            s_Logger?.LogInformation(
                "Spawns de zumbis ampliados somente em memoria no mapa {Map}: {OriginalCount} -> {ExpandedCount} pontos em {RegionCount} regioes.",
                Provider.map,
                originalTotal,
                expandedTotal,
                s_Expansions.Count);
        }

        internal static void EnsureConfiguredMegaCount(
            ZombieManager manager,
            byte bound)
        {
            int megasPerRegion = GetMegasForRegion(bound);
            if (!s_Enabled
                || (megasPerRegion <= 1 && !s_GuaranteeMegas)
                || Level.info == null
                || Level.info.type != ELevelType.SURVIVAL
                || bound >= ZombieManager.regions.Length
                || bound >= LevelZombies.zombies.Length
                || bound >= LevelNavigation.flagData.Count
                || !LevelNavigation.flagData[bound].spawnZombies)
            {
                return;
            }

            ZombieRegion region = ZombieManager.regions[bound];
            if (region.hasBeacon)
            {
                return;
            }

            List<ZombieSpawnpoint> megaSpawnpoints = CollectMegaSpawnpoints(bound);
            if (megaSpawnpoints.Count == 0)
            {
                return;
            }

            int aliveMegas = CountAliveMegas(region);
            if (aliveMegas >= megasPerRegion)
            {
                return;
            }
            for (int index = aliveMegas;
                 index < megasPerRegion;
                 index++)
            {
                ZombieSpawnpoint spawn =
                    megaSpawnpoints[index % megaSpawnpoints.Count];
                ZombieTable table = LevelZombies.tables[spawn.type];
                byte shirt = RollClothing(table, 0);
                byte pants = RollClothing(table, 1);
                byte hat = RollClothing(table, 2);
                byte gear = RollClothing(table, 3);

                int cloneLayer = (index / megaSpawnpoints.Count) + 1;
                Vector3 point = spawn.point
                    + GetSpawnOffset(index, cloneLayer)
                    + new Vector3(0f, 0.5f, 0f);

                manager.addZombie(
                    bound,
                    spawn.type,
                    (byte) EZombieSpeciality.MEGA,
                    shirt,
                    pants,
                    hat,
                    gear,
                    (byte) UnityEngine.Random.Range(0, 4),
                    (byte) UnityEngine.Random.Range(0, 3),
                    point,
                    UnityEngine.Random.Range(0f, 360f),
                    false);
            }

            region.hasMega = true;
            region.lastMega = Time.realtimeSinceStartup;
        }

        // Roda depois de cada respawnZombies vanilla. O vanilla so cria Mega quando
        // o sorteio cai num spawnpoint de Mega, o que pode levar horas. Aqui, quando
        // a regiao nao tem Mega vivo e o cooldown vanilla de 10 minutos ja passou,
        // um zumbi morto da regiao e revivido como Mega num spawnpoint de Mega.
        internal static void TryGuaranteeMega(byte bound)
        {
            if (!s_Enabled
                || !s_GuaranteeMegas
                || Level.info == null
                || Level.info.type != ELevelType.SURVIVAL
                || ZombieManager.regions == null
                || LevelZombies.zombies == null
                || LevelNavigation.flagData == null
                || bound >= ZombieManager.regions.Length
                || bound >= LevelZombies.zombies.Length
                || bound >= LevelNavigation.flagData.Count
                || !LevelNavigation.flagData[bound].spawnZombies)
            {
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (s_NextGuaranteeCheck == null
                || s_NextGuaranteeCheck.Length != ZombieManager.regions.Length)
            {
                s_NextGuaranteeCheck = new float[ZombieManager.regions.Length];
            }

            if (now < s_NextGuaranteeCheck[bound])
            {
                return;
            }

            s_NextGuaranteeCheck[bound] = now + GuaranteeCheckIntervalSeconds;

            ZombieRegion region = ZombieManager.regions[bound];
            if (region.hasBeacon
                || region.hasMega
                || now - region.lastMega <= VanillaMegaCooldownSeconds
                || region.zombies.Count == 0
                || CountAliveMegas(region) > 0)
            {
                return;
            }

            int maxBossZombies = LevelNavigation.flagData[bound].maxBossZombies;
            if (maxBossZombies >= 0
                && EZombieSpeciality.MEGA.IsBoss()
                && region.aliveBossZombieCount >= maxBossZombies)
            {
                return;
            }

            List<ZombieSpawnpoint> megaSpawnpoints = CollectMegaSpawnpoints(bound);
            if (megaSpawnpoints.Count == 0)
            {
                return;
            }

            Zombie? deadZombie = null;
            foreach (Zombie zombie in region.zombies)
            {
                if (zombie != null && zombie.isDead)
                {
                    deadZombie = zombie;
                    break;
                }
            }

            if (deadZombie == null)
            {
                return;
            }

            ZombieSpawnpoint spawn = megaSpawnpoints[
                UnityEngine.Random.Range(0, megaSpawnpoints.Count)];
            foreach (Zombie zombie in region.zombies)
            {
                if (zombie != null
                    && !zombie.isDead
                    && (zombie.transform.position - spawn.point).sqrMagnitude < 4f)
                {
                    return;
                }
            }

            ZombieTable table = LevelZombies.tables[spawn.type];
            table.GetSpawnClothingParameters(
                out byte shirt,
                out byte pants,
                out byte hat,
                out byte gear);

            region.lastMega = now;
            region.hasMega = true;
            deadZombie.sendRevive(
                spawn.type,
                (byte) EZombieSpeciality.MEGA,
                shirt,
                pants,
                hat,
                gear,
                spawn.point + new Vector3(0f, 0.5f, 0f),
                UnityEngine.Random.Range(0f, 360f));

            s_Logger?.LogInformation(
                "Mega garantido na regiao {Region} em {Position}.",
                bound,
                spawn.point);
        }

        internal static bool CanAddAnotherMega(ZombieRegion region)
        {
            int regionIndex = Array.IndexOf(
                ZombieManager.regions,
                region);
            if (regionIndex < 0 || regionIndex > byte.MaxValue)
            {
                return false;
            }

            int megasPerRegion =
                GetMegasForRegion((byte) regionIndex);
            if (!s_Enabled
                || megasPerRegion <= 1
                || region.hasBeacon)
            {
                return false;
            }

            int aliveMegas = CountAliveMegas(region);

            // Quando todos morrerem, preserva o cooldown vanilla de 10 minutos.
            // Enquanto pelo menos um estiver vivo, repoe apenas ate a quantidade
            // configurada.
            return aliveMegas > 0 && aliveMegas < megasPerRegion;
        }

        internal static void Clear()
        {
            foreach (RegionExpansion expansion in s_Expansions)
            {
                if (expansion.Spawnpoints.Count
                    > expansion.OriginalSpawnpointCount)
                {
                    expansion.Spawnpoints.RemoveRange(
                        expansion.OriginalSpawnpointCount,
                        expansion.Spawnpoints.Count
                            - expansion.OriginalSpawnpointCount);
                }

                expansion.FlagData.maxZombies =
                    expansion.OriginalMaxZombies;
            }

            s_Expansions.Clear();
            s_MapSettings.Clear();
            s_AppliedSpawnArray = null;
            s_Enabled = false;
            s_GeneralMultiplier = 1f;
            s_MegasPerRegion = 1;
            s_GuaranteeMegas = false;
            s_NextGuaranteeCheck = null;
            s_Logger = null;
        }

        private static float GetMultiplierForRegion(byte regionIndex)
        {
            if (s_MapSettings.TryGetValue(
                    Provider.map,
                    out MapSpawnSettings mapSettings))
            {
                return mapSettings.Regions.TryGetValue(
                    regionIndex,
                    out RegionSpawnSettings regionSettings)
                    ? regionSettings.Multiplier
                    : mapSettings.DefaultMultiplier;
            }

            return s_GeneralMultiplier;
        }

        private static int GetMegasForRegion(byte regionIndex)
        {
            if (s_MapSettings.TryGetValue(
                    Provider.map,
                    out MapSpawnSettings mapSettings))
            {
                return mapSettings.Regions.TryGetValue(
                    regionIndex,
                    out RegionSpawnSettings regionSettings)
                    ? regionSettings.MegasPerRegion
                    : mapSettings.DefaultMegasPerRegion;
            }

            return s_MegasPerRegion;
        }

        private static List<ZombieSpawnpoint> CollectMegaSpawnpoints(byte bound)
        {
            var megaSpawnpoints = new List<ZombieSpawnpoint>();
            foreach (ZombieSpawnpoint spawnpoint in LevelZombies.zombies[bound])
            {
                if (spawnpoint.type < LevelZombies.tables.Count
                    && LevelZombies.tables[spawnpoint.type].isMega
                    && SafezoneManager.checkPointValid(spawnpoint.point))
                {
                    megaSpawnpoints.Add(spawnpoint);
                }
            }

            return megaSpawnpoints;
        }

        private static int CountAliveMegas(ZombieRegion region)
        {
            int count = 0;
            foreach (Zombie zombie in region.zombies)
            {
                if (zombie != null && !zombie.isDead && zombie.isMega)
                {
                    count++;
                }
            }

            return count;
        }

        private static Vector3 GetSpawnOffset(
            int index,
            int cloneLayer)
        {
            float angle = (index * 137.50776f) * Mathf.Deg2Rad;
            float radius = 1.5f * cloneLayer;
            return new Vector3(
                Mathf.Cos(angle) * radius,
                0f,
                Mathf.Sin(angle) * radius);
        }

        private static byte RollClothing(
            ZombieTable table,
            int slotIndex)
        {
            ZombieSlot slot = table.slots[slotIndex];
            if (slot.table.Count == 0
                || UnityEngine.Random.value >= slot.chance)
            {
                return byte.MaxValue;
            }

            return (byte) UnityEngine.Random.Range(
                0,
                slot.table.Count);
        }
    }
}
