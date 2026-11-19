using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;

namespace SkaliaDeps
{
    public sealed class RankProgressData
    {
        public Dictionary<ulong, RankPlayerData> Players { get; set; } =
            new Dictionary<ulong, RankPlayerData>();

        public RankProgressData Clone()
        {
            var clone = new RankProgressData();
            foreach (var pair in Players)
            {
                clone.Players[pair.Key] = pair.Value.Clone();
            }

            return clone;
        }
    }

    public sealed class RankPlayerData
    {
        public int SchemaVersion { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString |
            JsonNumberHandling.WriteAsString)]
        public ulong SteamId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public string LastServerId { get; set; } = string.Empty;
        public bool UiVisible { get; set; } = true;
        public DateTime UpdatedAtUtc { get; set; }
        public long RankPoints { get; set; }
        public long ZombieKills { get; set; }
        public long MegaZombieKills { get; set; }
        public long PlayerKills { get; set; }
        public long Deaths { get; set; }
        public long QuestsCompleted { get; set; }
        public long TpaRequestsSent { get; set; }
        public long TpaRequestsAccepted { get; set; }
        public long TotalPlaytimeSeconds { get; set; }
        public long LongestLifeSeconds { get; set; }

        [JsonIgnore]
        public long TotalKills => ZombieKills + MegaZombieKills + PlayerKills;

        public RankPlayerData Clone()
        {
            return new RankPlayerData
            {
                SchemaVersion = SchemaVersion,
                SteamId = SteamId,
                PlayerName = PlayerName,
                LastServerId = LastServerId,
                UiVisible = UiVisible,
                UpdatedAtUtc = UpdatedAtUtc,
                RankPoints = RankPoints,
                ZombieKills = ZombieKills,
                MegaZombieKills = MegaZombieKills,
                PlayerKills = PlayerKills,
                Deaths = Deaths,
                QuestsCompleted = QuestsCompleted,
                TpaRequestsSent = TpaRequestsSent,
                TpaRequestsAccepted = TpaRequestsAccepted,
                TotalPlaytimeSeconds = TotalPlaytimeSeconds,
                LongestLifeSeconds = LongestLifeSeconds
            };
        }
    }
}
