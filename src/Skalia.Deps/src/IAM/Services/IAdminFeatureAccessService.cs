using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface IAdminFeatureAccessService
    {
        Task SyncAsync(UnturnedPlayer player);

        Task SyncOnlinePlayersAsync();

        Task SetEnabledAsync(UnturnedPlayer player, bool enabled);

        Task RevokeAdvantagesAsync(UnturnedPlayer player);
    }
}
