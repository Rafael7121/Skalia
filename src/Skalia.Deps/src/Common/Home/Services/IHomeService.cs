using System.Threading.Tasks;
using OpenMod.API.Ioc;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Players.Interacting.Events;

namespace SkaliaDeps
{
    [Service]
    public interface IHomeService
    {
        Task TeleportAsync(UnturnedPlayer player, string? selector);

        Task RenameBedAsync(UnturnedPlayer player, string selector, string newName);

        Task ListBedsAsync(UnturnedPlayer player);

        Task<string> GetBedNameAsync(ulong ownerSteamId, uint bedInstanceId);

        Task HandleBedClaimingAsync(UnturnedPlayerBedClaimingEvent @event);

        Task CancelForCombatAsync(ulong firstPlayerId, ulong secondPlayerId);

        Task HandleDeathAsync(UnturnedPlayer player);

        void HandleDisconnect(UnturnedPlayer player);
    }
}
