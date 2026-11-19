using Cysharp.Threading.Tasks;
using OpenMod.API.Ioc;

namespace SkaliaDeps
{
    [Service]
    public interface ISkaliaBotService
    {
        void Start();

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
