using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // Ramps, stairs, doors and gaps the zombie navmesh doesn't connect, marked by
    // hand with /passagem. Each link is walked straight from one end to the other.
    internal readonly struct BotPassageLink
    {
        public BotPassageLink(Vector3 a, Vector3 b)
        {
            A = a;
            B = b;
        }

        public Vector3 A { get; }
        public Vector3 B { get; }
    }

    internal static class SkaliaBotPassageLinks
    {
        private static readonly List<BotPassageLink> s_Links = new List<BotPassageLink>();
        private static string? s_LoadedMap;
        private static readonly Dictionary<Vector3, uint?> s_AreaCache = new Dictionary<Vector3, uint?>();

        public static IReadOnlyList<BotPassageLink> Links
        {
            get
            {
                EnsureLoaded();
                return s_Links;
            }
        }

        public static int Add(Vector3 a, Vector3 b)
        {
            EnsureLoaded();
            s_Links.Add(new BotPassageLink(a, b));
            Save();
            return s_Links.Count;
        }

        public static bool RemoveAt(int index)
        {
            EnsureLoaded();
            if (index < 0 || index >= s_Links.Count)
            {
                return false;
            }
            s_Links.RemoveAt(index);
            Save();
            return true;
        }

        public static string FilePath =>
            System.IO.Path.Combine(
                ReadWrite.PATH,
                "Servers",
                Provider.serverID,
                "SkaliaPassagens_" + (Level.info?.name ?? "mapa") + ".txt");

        private static void EnsureLoaded()
        {
            var map = Level.info?.name ?? "mapa";
            if (s_LoadedMap == map)
            {
                return;
            }
            s_LoadedMap = map;
            s_Links.Clear();
            s_AreaCache.Clear();
            try
            {
                if (!File.Exists(FilePath))
                {
                    return;
                }
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split(';');
                    if (parts.Length != 6)
                    {
                        continue;
                    }
                    var values = new float[6];
                    var valid = true;
                    for (var index = 0; index < 6 && valid; index++)
                    {
                        valid = float.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]);
                    }
                    // Both ends marked on the same spot: nothing to cross.
                    if (valid
                        && (new Vector3(values[0], values[1], values[2])
                            - new Vector3(values[3], values[4], values[5])).sqrMagnitude >= 0.09f)
                    {
                        s_Links.Add(new BotPassageLink(
                            new Vector3(values[0], values[1], values[2]),
                            new Vector3(values[3], values[4], values[5])));
                    }
                }
            }
            catch (Exception)
            {
                // A broken file only means no ramps; /passagem rewrites it.
            }
        }

        private static void Save()
        {
            var lines = new List<string>();
            foreach (var link in s_Links)
            {
                lines.Add(string.Join(";", new[]
                {
                    link.A.x, link.A.y, link.A.z, link.B.x, link.B.y, link.B.z
                }.ConvertAll(value => value.ToString("0.###", CultureInfo.InvariantCulture))));
            }
            File.WriteAllLines(FilePath, lines);
        }

        private static string[] ConvertAll(this float[] values, Func<float, string> convert)
        {
            var result = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                result[index] = convert(values[index]);
            }
            return result;
        }

        // The graph is static: the region of a marked end never changes.
        public static uint? GetCachedArea(Vector3 point)
        {
            if (!s_AreaCache.TryGetValue(point, out var area))
            {
                area = GetArea(point);
                s_AreaCache[point] = area;
            }
            return area;
        }

        public static Vector3? GetNearestMeshPoint(Vector3 point)
        {
            if (AstarPath.active == null)
            {
                return null;
            }
            var nearest = AstarPath.active.GetNearest(point, NNConstraint.Walkable);
            return nearest.node == null ? (Vector3?)null : nearest.position;
        }

        public static uint? GetArea(Vector3 point)
        {
            if (AstarPath.active == null)
            {
                return null;
            }
            var nearest = AstarPath.active.GetNearest(point, NNConstraint.Walkable);
            return nearest.node?.Area;
        }
    }
}
