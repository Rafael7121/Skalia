using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Interacting.Events;

namespace SkaliaDeps
{
    public sealed class HomeEventsListener : IEventListener<UnturnedPlayerBedClaimingEvent>
    {
        private readonly IHomeService m_HomeService;

        public HomeEventsListener(IHomeService homeService)
        {
            m_HomeService = homeService;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerBedClaimingEvent @event)
        {
            return m_HomeService.HandleBedClaimingAsync(@event);
        }
    }
}
