using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    // A 2m height grid of the whole map, sampled once per map version with a
    // raycast from the sky per cell and cached on disk. The zombie navmesh only
    // covers the towns; between them the bot plans on this grid instead of
    // feeling its way 7m at a time (that walked it down into a river gorge).
    internal static class SkaliaBotTerrainMap
    {
        public const byte FlagHit = 1;
        public const byte FlagWater = 2;
        public const byte FlagObject = 4;
        public const byte FlagSteep = 8;

        private const int FileVersion = 1;
        private const float CellSize = 2f;
        private const float CoarseSize = 4f;
        private const float HeightScale = 8f;
        private const double FrameBudgetMilliseconds = 3.0;
        private const int GroundMask = RayMasks.GROUND | RayMasks.GROUND2 | RayMasks.ENVIRONMENT
            | RayMasks.LARGE | RayMasks.MEDIUM;

        // Coarse planning grid: one 4m cell per 2x2 scan cells.
        private const float MaxRisePerMeter = 0.8f;
        private const float MaxDropPerMeter = 1.1f;
        private const float BlockHeightSpread = 2.5f;
        private const int MaxPlanNodes = 400000;
        private const float PlanMarginMeters = 160f;
        // Leaving Camp Samara, three trips were refused in 2 min because the
        // only way around went outside the 160m strip; the bot then wandered
        // the searched area. A refused plan is retried once on a 500m strip.
        public const float WidePlanMarginMeters = 500f;

        private static bool s_Started;
        private static bool s_Ready;
        private static string? s_Map;
        private static float s_OriginX;
        private static float s_OriginZ;
        private static int s_Width;
        private static short[] s_Heights = Array.Empty<short>();
        private static byte[] s_Flags = Array.Empty<byte>();

        private static int s_CoarseWidth;
        private static float[] s_CoarseHeights = Array.Empty<float>();
        private static bool[] s_CoarseBlocked = Array.Empty<bool>();
        private static bool[] s_CoarseObject = Array.Empty<bool>();
        // 0.2.2x14: water is swum, not a wall (the tester: "os bots nao nadam,
        // ficam presos em ilhas"). A coarse cell with water on 2+ of its 4
        // samples is a water cell. The players' recordings: they swim at
        // 5.21 m/s and sprint at 8.63 m/s (medians), so a meter swum costs
        // 1.66 meters run; they walk in and out where the bottom slopes
        // (out with the ground 0.2m over the floating feet, median), so in
        // and out of the water take the same slope limits as land, with the
        // water cell's bottom as its height.
        private static bool[] s_CoarseWater = Array.Empty<bool>();
        private static readonly System.Collections.Generic.Dictionary<int, float> s_Surface =
            new System.Collections.Generic.Dictionary<int, float>();
        private const float SwimCostFactor = 8.63f / 5.21f;

        public static bool Ready => s_Ready && s_Map == Level.info?.name;

        public static string Status { get; private set; } = "nao iniciada";

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
            RunAsync(map, logger).Forget();
        }

        private static string FilePath(string map) =>
            Path.Combine(
                ReadWrite.PATH,
                "Servers",
                Provider.serverID,
                "SkaliaTerreno_" + map + ".bin");

        private static string LevelHash()
        {
            var hash = Level.hash;
            return hash == null ? "sem-hash" : BitConverter.ToString(hash).Replace("-", string.Empty);
        }

        private static async UniTaskVoid RunAsync(string map, ILogger logger)
        {
            try
            {
                await UniTask.SwitchToMainThread();
                var size = Level.size;
                var width = (int)(size / CellSize);
                var originX = -size / 2f;
                var originZ = -size / 2f;
                var hash = LevelHash();
                var path = FilePath(map);

                var loaded = await Task.Run(() => TryLoad(path, hash, width, originX, originZ));
                await UniTask.SwitchToMainThread();
                if (s_Map != map)
                {
                    return;
                }
                if (loaded != null)
                {
                    Install(map, originX, originZ, width, loaded.Value.heights, loaded.Value.flags);
                    logger.LogInformation(
                        "Mapa de terreno de {Map} carregado do cache ({Cells} celulas de {Size}m).",
                        map,
                        width * width,
                        CellSize);
                    return;
                }

                logger.LogInformation(
                    "Varrendo o terreno de {Map}: grade {Width}x{Width} de {Size}m ({Cells} raycasts, em fatias de {Budget}ms por frame).",
                    map,
                    width,
                    width,
                    CellSize,
                    width * width,
                    FrameBudgetMilliseconds);
                var heights = new short[width * width];
                var flags = new byte[width * width];
                var watch = Stopwatch.StartNew();
                var slice = Stopwatch.StartNew();
                var nextReport = 0.1f;
                for (var z = 0; z < width; z++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        Sample(originX + (x + 0.5f) * CellSize, originZ + (z + 0.5f) * CellSize, out heights[z * width + x], out flags[z * width + x]);
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
                    var progress = (z + 1f) / width;
                    if (progress >= nextReport)
                    {
                        Status = "varrendo " + (progress * 100f).ToString("0") + "%";
                        logger.LogInformation("Varredura do terreno de {Map}: {Progress:0}%.", map, progress * 100f);
                        nextReport += 0.1f;
                    }
                }

                Install(map, originX, originZ, width, heights, flags);
                var water = 0;
                var objects = 0;
                for (var i = 0; i < flags.Length; i++)
                {
                    if ((flags[i] & FlagWater) != 0) water++;
                    if ((flags[i] & FlagObject) != 0) objects++;
                }
                logger.LogInformation(
                    "Varredura do terreno de {Map} concluida em {Seconds:0}s: {Water:0.0}% agua, {Objects:0.0}% sobre objetos/construcoes. Salvando em {Path}.",
                    map,
                    watch.Elapsed.TotalSeconds,
                    water * 100f / flags.Length,
                    objects * 100f / flags.Length,
                    path);
                await Task.Run(() => Save(path, hash, width, originX, originZ, heights, flags));
            }
            catch (Exception exception)
            {
                Status = "falhou";
                logger.LogError(exception, "Falha na varredura do terreno de {Map}.", map);
            }
        }

        private static void Sample(float x, float z, out short height, out byte flags)
        {
            flags = 0;
            height = 0;
            if (!Physics.Raycast(
                    new Vector3(x, 1024f, z),
                    Vector3.down,
                    out var hit,
                    1200f,
                    GroundMask,
                    QueryTriggerInteraction.Ignore))
            {
                return;
            }
            flags |= FlagHit;
            height = (short)Mathf.Clamp(Mathf.RoundToInt(hit.point.y * HeightScale), short.MinValue, short.MaxValue);
            if (hit.normal.y < 0.7f)
            {
                flags |= FlagSteep;
            }
            if (!(hit.collider is TerrainCollider))
            {
                flags |= FlagObject;
            }
            if (WaterUtility.isPointUnderwater(hit.point + Vector3.up * 0.5f))
            {
                flags |= FlagWater;
            }
        }

        private static void Install(string map, float originX, float originZ, int width, short[] heights, byte[] flags)
        {
            s_OriginX = originX;
            s_OriginZ = originZ;
            s_Width = width;
            s_Heights = heights;
            s_Flags = flags;
            BuildCoarse();
            s_Map = map;
            s_Ready = true;
            Status = "pronta";
        }

        private static void BuildCoarse()
        {
            var cw = s_Width / 2;
            var heights = new float[cw * cw];
            var blocked = new bool[cw * cw];
            var objects = new bool[cw * cw];
            var waterCells = new bool[cw * cw];
            for (var cz = 0; cz < cw; cz++)
            {
                for (var cx = 0; cx < cw; cx++)
                {
                    var min = float.MaxValue;
                    var max = float.MinValue;
                    var sum = 0f;
                    var count = 0;
                    var bad = false;
                    var steep = 0;
                    var onObject = false;
                    var wet = 0;
                    var hits = 0;
                    var seabed = 0f;
                    for (var dz = 0; dz < 2; dz++)
                    {
                        for (var dx = 0; dx < 2; dx++)
                        {
                            var i = (cz * 2 + dz) * s_Width + cx * 2 + dx;
                            var f = s_Flags[i];
                            if ((f & FlagHit) != 0)
                            {
                                hits++;
                                seabed += s_Heights[i] / HeightScale;
                            }
                            if ((f & FlagHit) == 0 || (f & FlagWater) != 0)
                            {
                                if ((f & FlagWater) != 0) wet++;
                                bad = true;
                                continue;
                            }
                            if ((f & FlagSteep) != 0) steep++;
                            if ((f & FlagObject) != 0) onObject = true;
                            var h = s_Heights[i] / HeightScale;
                            min = Mathf.Min(min, h);
                            max = Mathf.Max(max, h);
                            sum += h;
                            count++;
                        }
                    }
                    var index = cz * cw + cx;
                    if (wet >= 2 && hits == 4)
                    {
                        // Swum: the bottom's shape does not matter.
                        waterCells[index] = true;
                        blocked[index] = false;
                        heights[index] = seabed / hits;
                        objects[index] = false;
                        continue;
                    }
                    blocked[index] = bad || count == 0 || steep >= 3 || max - min > BlockHeightSpread;
                    heights[index] = count > 0 ? sum / count : 0f;
                    objects[index] = onObject;
                }
            }
            s_CoarseWidth = cw;
            s_CoarseHeights = heights;
            s_CoarseBlocked = blocked;
            s_CoarseObject = objects;
            s_CoarseWater = waterCells;
            s_Surface.Clear();
        }

        // The water surface over a water cell (asked of the game once).
        private static float GetSurface(int index)
        {
            if (s_Surface.TryGetValue(index, out var surface))
            {
                return surface;
            }
            var cw = s_CoarseWidth;
            var point = new Vector3(
                s_OriginX + (index % cw + 0.5f) * CoarseSize,
                s_CoarseHeights[index] + 0.3f,
                s_OriginZ + (index / cw + 0.5f) * CoarseSize);
            WaterUtility.getUnderwaterInfo(point, out var under, out surface);
            if (!under)
            {
                surface = s_CoarseHeights[index] + 0.5f;
            }
            s_Surface[index] = surface;
            return surface;
        }

        private static int CoarseIndex(Vector3 point)
        {
            var cw = s_CoarseWidth;
            var x = Mathf.FloorToInt((point.x - s_OriginX) / CoarseSize);
            var z = Mathf.FloorToInt((point.z - s_OriginZ) / CoarseSize);
            return x < 0 || z < 0 || x >= cw || z >= cw ? -1 : z * cw + x;
        }

        // A point of a planned route that is swum.
        public static bool IsSwimPoint(Vector3 point)
        {
            if (!Ready)
            {
                return false;
            }
            var index = CoarseIndex(point);
            return index >= 0 && s_CoarseWater[index];
        }

        public static bool IsInsideMap(Vector3 point)
        {
            if (!Ready)
            {
                return true;
            }
            var limit = s_OriginX + s_Width * CellSize;
            return point.x >= s_OriginX && point.z >= s_OriginZ && point.x < limit && point.z < s_OriginZ + s_Width * CellSize;
        }

        public static bool TryGetCell(Vector3 point, out float height, out byte flags)
        {
            height = 0f;
            flags = 0;
            if (!Ready)
            {
                return false;
            }
            var x = Mathf.FloorToInt((point.x - s_OriginX) / CellSize);
            var z = Mathf.FloorToInt((point.z - s_OriginZ) / CellSize);
            if (x < 0 || z < 0 || x >= s_Width || z >= s_Width)
            {
                return false;
            }
            var i = z * s_Width + x;
            height = s_Heights[i] / HeightScale;
            flags = s_Flags[i];
            return true;
        }

        private static bool CanStep(int from, int to, float distance)
        {
            if (s_CoarseBlocked[to])
            {
                return false;
            }
            var fromWater = s_CoarseWater[from];
            var toWater = s_CoarseWater[to];
            if (fromWater && toWater)
            {
                return true;
            }
            var dh = s_CoarseHeights[to] - s_CoarseHeights[from];
            return dh <= distance * MaxRisePerMeter && -dh <= distance * MaxDropPerMeter;
        }

        // The grid holds the highest surface. Under a bridge the bot's own
        // cell is the deck 18m above: planned from there, every waypoint
        // was straight overhead and the bot ran circles in the river for
        // 2 min. With a height given, cells far from it are skipped.
        private static bool TrySnap(int cx, int cz, int radius, out int index, float? height = null, bool allowWater = true)
        {
            var cw = s_CoarseWidth;
            index = -1;
            var best = int.MaxValue;
            for (var dz = -radius; dz <= radius; dz++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    var z = cz + dz;
                    if (x < 0 || z < 0 || x >= cw || z >= cw)
                    {
                        continue;
                    }
                    var i = z * cw + x;
                    var d = dx * dx + dz * dz;
                    if (!s_CoarseBlocked[i] && d < best
                        && (allowWater || !s_CoarseWater[i])
                        && (height == null || s_CoarseWater[i] || Mathf.Abs(s_CoarseHeights[i] - height.Value) <= 3f))
                    {
                        best = d;
                        index = i;
                    }
                }
            }
            return index >= 0;
        }

        // A* on the 4m grid inside the box around both ends. Steps that climb
        // more than ~39 degrees or drop more than ~48 degrees are not allowed,
        // water and cells with a 2.5m height spread (walls, cliffs) are blocked,
        // walking over buildings and props costs more than open ground.
        public static bool TryPlan(Vector3 start, Vector3 goal, List<Vector3> path, out string failure, float marginMeters = PlanMarginMeters)
        {
            path.Clear();
            failure = string.Empty;
            if (!Ready)
            {
                failure = "mapa de terreno ainda nao esta pronto (" + Status + ")";
                return false;
            }
            if (!IsInsideMap(goal))
            {
                failure = "destino fora dos limites do mapa";
                return false;
            }
            var cw = s_CoarseWidth;
            int ToCell(float v, float origin) => Mathf.Clamp(Mathf.FloorToInt((v - origin) / CoarseSize), 0, cw - 1);
            var sx = ToCell(start.x, s_OriginX);
            var sz = ToCell(start.z, s_OriginZ);
            var gx = ToCell(goal.x, s_OriginX);
            var gz = ToCell(goal.z, s_OriginZ);
            if (!TrySnap(sx, sz, 4, out var startIndex, start.y))
            {
                failure = "sem chao livre perto da saida";
                return false;
            }
            if (!TrySnap(gx, gz, 12, out var goalIndex, null, false))
            {
                failure = "sem chao livre perto do destino";
                return false;
            }
            var margin = Mathf.CeilToInt(marginMeters / CoarseSize);
            var minX = Mathf.Max(0, Mathf.Min(sx, gx) - margin);
            var maxX = Mathf.Min(cw - 1, Mathf.Max(sx, gx) + margin);
            var minZ = Mathf.Max(0, Mathf.Min(sz, gz) - margin);
            var maxZ = Mathf.Min(cw - 1, Mathf.Max(sz, gz) + margin);
            var bw = maxX - minX + 1;
            var bh = maxZ - minZ + 1;
            if (bw * bh > MaxPlanNodes * 4)
            {
                failure = "distancia grande demais para planejar";
                return false;
            }
            var g = new float[bw * bh];
            var parent = new int[bw * bh];
            var closed = new bool[bw * bh];
            for (var i = 0; i < g.Length; i++)
            {
                g[i] = float.MaxValue;
                parent[i] = -1;
            }
            int Local(int global) => (global / cw - minZ) * bw + (global % cw - minX);
            int Global(int local) => (local / bw + minZ) * cw + local % bw + minX;
            var goalX = goalIndex % cw;
            var goalZ = goalIndex / cw;
            float Heuristic(int global)
            {
                var dx = global % cw - goalX;
                var dz = global / cw - goalZ;
                return Mathf.Sqrt(dx * dx + dz * dz) * CoarseSize;
            }
            var open = new MinHeap();
            var startLocal = Local(startIndex);
            g[startLocal] = 0f;
            open.Push(Heuristic(startIndex), startLocal);
            var expanded = 0;
            var found = false;
            while (open.Count > 0)
            {
                var current = open.Pop();
                if (closed[current])
                {
                    continue;
                }
                closed[current] = true;
                var currentGlobal = Global(current);
                if (currentGlobal == goalIndex)
                {
                    found = true;
                    break;
                }
                if (++expanded > MaxPlanNodes)
                {
                    break;
                }
                var cx = current % bw;
                var cz = current / bw;
                for (var dz = -1; dz <= 1; dz++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dz == 0)
                        {
                            continue;
                        }
                        var nx = cx + dx;
                        var nz = cz + dz;
                        if (nx < 0 || nz < 0 || nx >= bw || nz >= bh)
                        {
                            continue;
                        }
                        var next = nz * bw + nx;
                        if (closed[next])
                        {
                            continue;
                        }
                        var nextGlobal = Global(next);
                        var distance = (dx != 0 && dz != 0 ? 1.4142f : 1f) * CoarseSize;
                        if (!CanStep(currentGlobal, nextGlobal, distance))
                        {
                            continue;
                        }
                        if (dx != 0 && dz != 0
                            && (s_CoarseBlocked[currentGlobal + dx] || s_CoarseBlocked[currentGlobal + dz * cw]))
                        {
                            continue;
                        }
                        var swim = s_CoarseWater[nextGlobal] || s_CoarseWater[currentGlobal];
                        var climb = swim ? 0f : Mathf.Abs(s_CoarseHeights[nextGlobal] - s_CoarseHeights[currentGlobal]);
                        var cost = (swim ? distance * SwimCostFactor : distance) + climb * 2f + (s_CoarseObject[nextGlobal] ? distance * 1.5f : 0f);
                        var tentative = g[current] + cost;
                        if (tentative < g[next])
                        {
                            g[next] = tentative;
                            parent[next] = current;
                            open.Push(tentative + Heuristic(nextGlobal), next);
                        }
                    }
                }
            }
            if (!found)
            {
                failure = expanded > MaxPlanNodes
                    ? "busca de rota excedeu o limite"
                    : "nenhuma rota por terreno (agua, penhasco ou paredes no caminho)";
                return false;
            }

            var cells = new List<int>();
            for (var local = Local(goalIndex); local >= 0; local = parent[local])
            {
                cells.Add(Global(local));
            }
            cells.Reverse();
            // Keep one point every few cells and at every turn.
            for (var i = 0; i < cells.Count; i++)
            {
                var keep = i == 0 || i == cells.Count - 1 || i % 4 == 0;
                if (!keep && i + 1 < cells.Count && i > 0)
                {
                    var a = cells[i - 1];
                    var b = cells[i];
                    var c = cells[i + 1];
                    keep = b - a != c - b;
                }
                // Every water cell next to land is kept: where the bot goes in
                // and where it climbs out.
                if (!keep && i > 0 && i + 1 < cells.Count
                    && (s_CoarseWater[cells[i]] != s_CoarseWater[cells[i - 1]]
                        || s_CoarseWater[cells[i]] != s_CoarseWater[cells[i + 1]]))
                {
                    keep = true;
                }
                if (keep)
                {
                    var cell = cells[i];
                    path.Add(new Vector3(
                        s_OriginX + (cell % cw + 0.5f) * CoarseSize,
                        s_CoarseWater[cell] ? GetSurface(cell) : s_CoarseHeights[cell],
                        s_OriginZ + (cell / cw + 0.5f) * CoarseSize));
                }
            }
            return true;
        }

        private sealed class MinHeap
        {
            private readonly List<(float key, int value)> m_Items = new List<(float, int)>();

            public int Count => m_Items.Count;

            public void Push(float key, int value)
            {
                m_Items.Add((key, value));
                var i = m_Items.Count - 1;
                while (i > 0)
                {
                    var p = (i - 1) / 2;
                    if (m_Items[p].key <= m_Items[i].key)
                    {
                        break;
                    }
                    (m_Items[p], m_Items[i]) = (m_Items[i], m_Items[p]);
                    i = p;
                }
            }

            public int Pop()
            {
                var top = m_Items[0].value;
                var last = m_Items.Count - 1;
                m_Items[0] = m_Items[last];
                m_Items.RemoveAt(last);
                var i = 0;
                while (true)
                {
                    var l = i * 2 + 1;
                    var r = l + 1;
                    var s = i;
                    if (l < m_Items.Count && m_Items[l].key < m_Items[s].key) s = l;
                    if (r < m_Items.Count && m_Items[r].key < m_Items[s].key) s = r;
                    if (s == i)
                    {
                        break;
                    }
                    (m_Items[s], m_Items[i]) = (m_Items[i], m_Items[s]);
                    i = s;
                }
                return top;
            }
        }

        private static (short[] heights, byte[] flags)? TryLoad(string path, string hash, int width, float originX, float originZ)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                using var file = File.OpenRead(path);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new BinaryReader(gzip);
                if (reader.ReadInt32() != FileVersion
                    || reader.ReadString() != hash
                    || reader.ReadInt32() != width
                    || Math.Abs(reader.ReadSingle() - originX) > 0.01f
                    || Math.Abs(reader.ReadSingle() - originZ) > 0.01f
                    || Math.Abs(reader.ReadSingle() - CellSize) > 0.001f)
                {
                    return null;
                }
                var count = width * width;
                var heights = new short[count];
                var bytes = reader.ReadBytes(count * 2);
                if (bytes.Length != count * 2)
                {
                    return null;
                }
                Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
                var flags = reader.ReadBytes(count);
                return flags.Length == count ? ((short[], byte[])?)(heights, flags) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Save(string path, string hash, int width, float originX, float originZ, short[] heights, byte[] flags)
        {
            var temp = path + ".tmp";
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Fastest))
            using (var writer = new BinaryWriter(gzip))
            {
                writer.Write(FileVersion);
                writer.Write(hash);
                writer.Write(width);
                writer.Write(originX);
                writer.Write(originZ);
                writer.Write(CellSize);
                var bytes = new byte[heights.Length * 2];
                Buffer.BlockCopy(heights, 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
                writer.Write(flags);
            }
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temp, path);
        }
    }
}
