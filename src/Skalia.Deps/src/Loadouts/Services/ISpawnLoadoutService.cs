using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface ISpawnLoadoutService
    {
        Task HandleSpawnAsync(UnturnedPlayer player);

        Task HandleDeathAsync(UnturnedPlayer player);
    }
}
