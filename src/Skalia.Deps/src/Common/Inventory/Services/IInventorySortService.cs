using Cysharp.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface IInventorySortService
    {
        void Start();
        void Stop();
        UniTask SortAsync(UnturnedPlayer player);
        UniTask SyncUiForOnlinePlayersAsync();
    }
}
