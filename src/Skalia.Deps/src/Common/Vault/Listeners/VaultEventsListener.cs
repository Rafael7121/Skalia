using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Connections.Events;
using OpenMod.Unturned.Players.Inventory.Events;

namespace SkaliaDeps
{
    public sealed class VaultEventsListener :
        IEventListener<UnturnedPlayerClosedStorageEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>
    {
        private readonly IVaultService m_VaultService;

        public VaultEventsListener(IVaultService vaultService)
        {
            m_VaultService = vaultService;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerClosedStorageEvent @event)
        {
            return m_VaultService.SaveIfOpenAsync(@event.Player);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            return m_VaultService.SaveAndCloseSessionAsync(@event.Player);
        }
    }
}
