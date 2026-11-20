using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Pathfinding;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    // 0.1.0-y17.26: key doors (the tester, 20/11: on Arid "ele tem que seguir
    // ordem de deadzone e ir pegando as chaves de acesso"; "outros mapas
    // curated tbm seguem a logica do arid"). Nothing is written per map: a
    // door of any map that asks for an item (its interactability condition,
    // what the game checks for a player) is a key door, and the item is its
    // key.
    // - The door opens only with the key in the bag (SkaliaBotStructureMap.Open);
    //   before, the force call opened it for a bot with no key.
    // - A key the map spawns is picked up, one of each.
    // - A locked door met on the way to loot (the item behind it, the route
    //   through it) makes its key the next goal: the bot goes to the spawn
    //   points of the key's tables, takes it, comes back and opens the door.
    // - On Arid each key lies behind the door of the one before (the 2023
    //   guide: AG-17 -> RS2 -> AG-18 -> AG-19 -> SACR-2 -> AG-19-MS), so the
    //   order follows by itself: reaching a key's spawn hits the previous
    //   door, which asks for the previous key. The AG-19 deadzone is entered
    //   by the rules of SkaliaBotRadiationY.
    // Not done: doors sealed with rubble (Rescue Saw or explosives) and keys
    // only an NPC or a quest gives.
    internal sealed partial class SkaliaBotIdleSimulation
    {
        private const float KeyGoalSeconds = 900f;
        private const float LockedDoorLogSeconds = 120f;

        private sealed class KeyGoal
        {
            public BotDoor Door = null!;
            public float Until;
        }

        private static Dictionary<ushort, List<Vector3>>? s_KeySpawns;
        private readonly Dictionary<ushort, KeyGoal> m_KeyGoals = new Dictionary<ushort, KeyGoal>();
        private readonly Dictionary<BotDoor, float> m_LockedDoorLoggedAt = new Dictionary<BotDoor, float>();
        private readonly HashSet<ushort> m_KeysWithoutSpawn = new HashSet<ushort>();

        private static bool IsMapKeyItem(ushort id)
        {
            return SkaliaBotStructureMap.Ready && SkaliaBotStructureMap.KeyItems.Contains(id);
        }

        private static Dictionary<ushort, List<Vector3>> GetKeySpawns()
        {
            if (s_KeySpawns != null)
            {
                return s_KeySpawns;
            }
            if (!SkaliaBotStructureMap.Ready)
            {
                return new Dictionary<ushort, List<Vector3>>();
            }
            s_KeySpawns = new Dictionary<ushort, List<Vector3>>();
            var keys = SkaliaBotStructureMap.KeyItems;
            var tables = LevelItems.tables;
            if (keys.Count == 0 || tables == null || LevelItems.spawns == null)
            {
                return s_KeySpawns;
            }
            // The tables that can give each key (exact, SkaliaBotFullLootY).
            var tablesByKey = new Dictionary<int, List<ushort>>();
            for (var index = 0; index < tables.Count; index++)
            {
                if (tables[index] == null)
                {
                    continue;
                }
                var shares = new Dictionary<ushort, float>();
                try
                {
                    AddItemTableShares(shares, tables[index]);
                }
                catch (System.Exception)
                {
                    continue;
                }
                foreach (var key in keys)
                {
                    if (shares.ContainsKey(key))
                    {
                        if (!tablesByKey.TryGetValue(index, out var list))
                        {
                            list = new List<ushort>();
                            tablesByKey[index] = list;
                        }
                        list.Add(key);
                    }
                }
            }
            for (byte x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    var spawns = LevelItems.spawns[x, y];
                    if (spawns == null)
                    {
                        continue;
                    }
                    foreach (var spawn in spawns)
                    {
                        if (!tablesByKey.TryGetValue(spawn.type, out var given))
                        {
                            continue;
                        }
                        foreach (var key in given)
                        {
                            if (!s_KeySpawns.TryGetValue(key, out var points))
                            {
                                points = new List<Vector3>();
                                s_KeySpawns[key] = points;
                            }
                            points.Add(spawn.point);
                        }
                    }
                }
            }
            return s_KeySpawns;
        }

        private static string KeyName(ushort id)
        {
            return (Assets.find(EAssetType.ITEM, id) as ItemAsset)?.itemName ?? id.ToString();
        }

        private void NoteLockedDoor(BotDoor door, float now)
        {
            var missing = new List<string>();
            foreach (var key in door.Keys)
            {
                if (CountCarried(key) > 0)
                {
                    continue;
                }
                missing.Add(KeyName(key));
                if (!m_KeysWithoutSpawn.Contains(key))
                {
                    m_KeyGoals[key] = new KeyGoal { Door = door, Until = now + KeyGoalSeconds };
                }
            }
            if (m_LockedDoorLoggedAt.TryGetValue(door, out var loggedAt) && now - loggedAt < LockedDoorLogSeconds)
            {
                return;
            }
            m_LockedDoorLoggedAt[door] = now;
            BotLog(LogLevel.Information,
                "Chaves: {Door} em {Position} esta trancada; precisa de {Keys}.",
                door.Name,
                FormatPosition(door.Center),
                missing.Count > 0 ? string.Join(", ", missing) : "condicao do mapa que nao e item");
        }

        private void NoteDoorOpened(BotDoor door)
        {
            if (door.Keys.Count == 0)
            {
                return;
            }
            foreach (var key in door.Keys)
            {
                m_KeyGoals.Remove(key);
            }
            BotLog(LogLevel.Information,
                "Chaves: abriu {Door} em {Position} com {Keys}.",
                door.Name,
                FormatPosition(door.Center),
                string.Join(", ", door.Keys.ConvertAll(KeyName)));
        }

        // The key, or the door once the key is in the bag. True when an area
        // was chosen.
        private bool TryPickKeyDestination(
            Vector3 position,
            float now,
            GraphNode originNode,
            ref Vector3 chosen,
            ref string chosenKey,
            ref int candidateCount,
            ref int blacklistedCount,
            ref int missingNodeCount,
            ref int disconnectedCount)
        {
            if (m_KeyGoals.Count == 0)
            {
                return false;
            }
            var expired = new List<ushort>();
            foreach (var entry in m_KeyGoals)
            {
                if (now >= entry.Value.Until || entry.Value.Door.Transform == null || !entry.Value.Door.IsClosed)
                {
                    expired.Add(entry.Key);
                }
            }
            foreach (var key in expired)
            {
                m_KeyGoals.Remove(key);
            }
            // With the key: back to its door.
            foreach (var entry in m_KeyGoals)
            {
                var door = entry.Value.Door;
                if (CountCarried(entry.Key) == 0 || !door.CanOpen(m_Player))
                {
                    continue;
                }
                var before = candidateCount;
                m_ExplorationWeightTotal = 0f;
                ConsiderExplorationCandidate(position, door.Center, "porta " + door.Name, 0f, TravelMaximumMeters, now,
                    originNode, ref chosen, ref m_InterestAreaName, ref chosenKey, ref candidateCount,
                    ref blacklistedCount, ref missingNodeCount, ref disconnectedCount, false);
                if (candidateCount > before)
                {
                    BotLog(LogLevel.Information,
                        "Chaves: com {Key} na mochila; voltando para abrir {Door} em {Position}.",
                        KeyName(entry.Key),
                        door.Name,
                        FormatPosition(door.Center));
                    return true;
                }
            }
            // Without it: the nearest spawn point of the key.
            var spawns = GetKeySpawns();
            var dropped = new List<ushort>();
            foreach (var entry in m_KeyGoals)
            {
                if (CountCarried(entry.Key) > 0)
                {
                    continue;
                }
                if (!spawns.TryGetValue(entry.Key, out var points) || points.Count == 0)
                {
                    dropped.Add(entry.Key);
                    continue;
                }
                points.Sort((a, b) => (a - position).sqrMagnitude.CompareTo((b - position).sqrMagnitude));
                foreach (var point in points)
                {
                    var before = candidateCount;
                    m_ExplorationWeightTotal = 0f;
                    ConsiderExplorationCandidate(position, point, "chave " + KeyName(entry.Key), 0f, TravelMaximumMeters, now,
                        originNode, ref chosen, ref m_InterestAreaName, ref chosenKey, ref candidateCount,
                        ref blacklistedCount, ref missingNodeCount, ref disconnectedCount, false);
                    if (candidateCount > before)
                    {
                        BotLog(LogLevel.Information,
                            "Chaves: indo buscar {Key} ({Points} ponto(s) de loot dela no mapa) a {Distance:0}m, para abrir {Door}.",
                            KeyName(entry.Key),
                            points.Count,
                            (point - position).GetHorizontalMagnitude(),
                            entry.Value.Door.Name);
                        return true;
                    }
                }
            }
            foreach (var key in dropped)
            {
                m_KeyGoals.Remove(key);
                m_KeysWithoutSpawn.Add(key);
                BotLog(LogLevel.Information,
                    "Chaves: {Key} nao nasce em nenhuma tabela de loot do mapa (quest ou NPC); deixando a porta de lado.",
                    KeyName(key));
            }
            return false;
        }
    }
}
