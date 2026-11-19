using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Connections.Events;
using OpenMod.Unturned.Players.Life.Events;

namespace SkaliaDeps
{
    public sealed class TpaEventsListener :
        IEventListener<UnturnedPlayerDamagedEvent>,
        IEventListener<UnturnedPlayerDeathEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>
    {
        private readonly ITpaService m_TpaService;
        private readonly IHomeService m_HomeService;

        public TpaEventsListener(ITpaService tpaService, IHomeService homeService)
        {
            m_TpaService = tpaService;
            m_HomeService = homeService;
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerDamagedEvent @event)
        {
            if (@event.DamageAmount == 0)
            {
                return;
            }

            await m_TpaService.RegisterCombatAsync(@event.Player, @event.Killer);
            await m_HomeService.CancelForCombatAsync(
                @event.Player.SteamId.m_SteamID,
                @event.Killer.m_SteamID);
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            m_HomeService.HandleDisconnect(@event.Player);
            await m_TpaService.HandleDisconnectAsync(@event.Player);
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerDeathEvent @event)
        {
            await m_TpaService.HandleDeathAsync(@event.Player);
            await m_HomeService.HandleDeathAsync(@event.Player);
        }
    }
}
