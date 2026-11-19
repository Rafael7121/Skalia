using Cysharp.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface IExperienceBarService
    {
        void Start();
        UniTask StopAsync();
        UniTask RegisterAsync(UnturnedPlayer player);
        UniTask UnregisterAsync(UnturnedPlayer player);
        void RecordPlayerSpawned(UnturnedPlayer player);
        UniTask RecordZombieKillAsync(UnturnedPlayer player, bool isMega);
        UniTask RecordPlayerDeathAsync(UnturnedPlayer victim, ulong killerId);
        UniTask SyncOnlinePlayersAsync();
        UniTask<bool> ToggleUiAsync(UnturnedPlayer player);
    }
}
