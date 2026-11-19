using System.Threading.Tasks;
using OpenMod.API.Ioc;

namespace SkaliaDeps
{
    [Service]
    public interface IPlayerStatsFileStore
    {
        Task<RankProgressData> LoadAllAsync();

        Task<RankProgressData> EnsurePlayerAsync(
            ulong steamId,
            string playerName,
            string serverId);

        Task<RankProgressData> ApplyDeltaAsync(
            ulong steamId,
            string playerName,
            string serverId,
            long rankPoints = 0,
            long zombieKills = 0,
            long megaZombieKills = 0,
            long playerKills = 0,
            long deaths = 0,
            long questsCompleted = 0,
            long tpaRequestsSent = 0,
            long tpaRequestsAccepted = 0,
            long totalPlaytimeSeconds = 0,
            long longestLifeCandidateSeconds = 0);

        Task<RankProgressData> SetUiVisibleAsync(
            ulong steamId,
            string playerName,
            string serverId,
            bool visible);
    }
}
