using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;

namespace SkaliaDeps
{
    [Service]
    public interface ICallVoteService
    {
        void Start();

        void Stop();

        Task CastVoteAsync(UnturnedPlayer player, CallVoteType type);

        Task HandleConnectedAsync(UnturnedPlayer player);

        Task HandleDisconnectedAsync(UnturnedPlayer player);
    }
}
