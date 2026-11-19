using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Connections.Events;

namespace SkaliaDeps
{
    public sealed class CallVoteConnectionListener :
        IEventListener<UnturnedPlayerConnectedEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>
    {
        private readonly ICallVoteService m_CallVoteService;

        public CallVoteConnectionListener(ICallVoteService callVoteService)
        {
            m_CallVoteService = callVoteService;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            return m_CallVoteService.HandleConnectedAsync(@event.Player);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            return m_CallVoteService.HandleDisconnectedAsync(@event.Player);
        }
    }
}
