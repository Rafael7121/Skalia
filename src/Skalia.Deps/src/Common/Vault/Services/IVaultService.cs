using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface IVaultService
    {
        Task OpenAsync(UnturnedPlayer player);

        Task SaveAndCloseSessionAsync(UnturnedPlayer player);

        Task SaveIfOpenAsync(UnturnedPlayer player);

        Task ShutdownAsync();
    }
}
