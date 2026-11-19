using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;
using Steamworks;

namespace SkaliaDeps
{
    [Service]
    public interface ITpaService
    {
        Task SendAsync(UnturnedPlayer sender, UnturnedPlayer receiver);

        Task AcceptAsync(UnturnedPlayer receiver, UnturnedPlayer? requestedSender);

        Task DeclineAsync(UnturnedPlayer receiver, UnturnedPlayer? requestedSender);

        Task CancelAsync(UnturnedPlayer player);

        Task RegisterCombatAsync(UnturnedPlayer victim, CSteamID attacker);

        int GetCombatSecondsRemaining(UnturnedPlayer player);

        Task HandleDeathAsync(UnturnedPlayer player);

        Task HandleDisconnectAsync(UnturnedPlayer player);
    }
}
