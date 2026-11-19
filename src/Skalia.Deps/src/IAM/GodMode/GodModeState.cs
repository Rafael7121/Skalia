using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;

namespace SkaliaDeps
{
    internal static class GodModeState
    {
        private static readonly object Sync = new object();
        private static readonly HashSet<ulong> EnabledPlayers = new HashSet<ulong>();
        private static readonly Dictionary<ulong, SpeedOverride> SpeedOverrides =
            new Dictionary<ulong, SpeedOverride>();
        private static float s_SpeedMultiplier = 1.5f;

        public static void ConfigureSpeedMultiplier(float multiplier)
        {
            lock (Sync)
            {
                s_SpeedMultiplier = multiplier < 1f ? 1f : multiplier > 3f ? 3f : multiplier;
            }
        }

        public static bool IsEnabled(ulong steamId)
        {
            lock (Sync)
            {
                return EnabledPlayers.Contains(steamId);
            }
        }

        public static bool IsEnabled(PlayerLife life)
        {
            var player = life?.player;
            var owner = player?.channel?.owner;
            return owner != null && IsEnabled(owner.playerID.steamID.m_SteamID);
        }

        public static bool SetEnabled(ulong steamId, bool enabled)
        {
            lock (Sync)
            {
                if (enabled)
                {
                    EnabledPlayers.Add(steamId);
                }
                else
                {
                    EnabledPlayers.Remove(steamId);
                }

                return enabled;
            }
        }

        public static bool Toggle(ulong steamId)
        {
            lock (Sync)
            {
                if (EnabledPlayers.Remove(steamId))
                {
                    return false;
                }

                EnabledPlayers.Add(steamId);
                return true;
            }
        }

        public static void ApplySpeed(Player player, bool enabled)
        {
            var owner = player?.channel?.owner;
            var movement = player?.movement;
            if (owner == null || movement == null)
            {
                return;
            }

            var steamId = owner.playerID.steamID.m_SteamID;
            float multiplier;
            lock (Sync)
            {
                if (enabled)
                {
                    if (!SpeedOverrides.TryGetValue(steamId, out var speedOverride) ||
                        !ReferenceEquals(speedOverride.Movement, movement))
                    {
                        speedOverride = new SpeedOverride(movement, movement.pluginSpeedMultiplier);
                        SpeedOverrides[steamId] = speedOverride;
                    }

                    multiplier = speedOverride.OriginalMultiplier * s_SpeedMultiplier;
                }
                else
                {
                    if (!SpeedOverrides.TryGetValue(steamId, out var speedOverride))
                    {
                        return;
                    }

                    SpeedOverrides.Remove(steamId);
                    multiplier = speedOverride.OriginalMultiplier;
                }
            }

            movement.sendPluginSpeedMultiplier(multiplier);
        }

        public static void ForgetSpeedOverride(ulong steamId)
        {
            lock (Sync)
            {
                SpeedOverrides.Remove(steamId);
            }
        }

        public static void Clear()
        {
            SpeedOverride[] speedOverrides;
            lock (Sync)
            {
                EnabledPlayers.Clear();
                speedOverrides = SpeedOverrides.Values.ToArray();
                SpeedOverrides.Clear();
            }

            foreach (var speedOverride in speedOverrides)
            {
                if (speedOverride.Movement != null)
                {
                    speedOverride.Movement.sendPluginSpeedMultiplier(speedOverride.OriginalMultiplier);
                }
            }
        }

        private sealed class SpeedOverride
        {
            public SpeedOverride(PlayerMovement movement, float originalMultiplier)
            {
                Movement = movement;
                OriginalMultiplier = originalMultiplier;
            }

            public PlayerMovement Movement { get; }

            public float OriginalMultiplier { get; }
        }
    }
}
