using Cysharp.Threading.Tasks;
using OpenMod.API.Ioc;

namespace SkaliaDeps
{
    [Service]
    public interface ISkaliaBotService
    {
        void Start();

        // 0.1.0-y17.33: the maps the bots need (the new navmesh above all),
        // started at the level's load instead of at the first bot.
        void PrepareMaps();

        UniTask StopAsync();

        UniTask<int> KickAllAsync();

        UniTask<int> CallAsync(string selector);

        UniTask<int> PlayAsync(string selector);

        UniTask<int> SetStandbyAllAsync();

        UniTask<int> SetStandbyAsync(string selector);

        UniTask<bool> AddBotAsync(ulong steamId, string name);

        UniTask<bool> SendChatAsync(string botName, string message);

        UniTask<BotNavigationSnapshot?> GetNavigationSnapshotAsync(string? botName);
    }
}
