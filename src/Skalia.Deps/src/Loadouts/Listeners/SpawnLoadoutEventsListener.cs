using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Life.Events;

namespace SkaliaDeps
{
    public sealed class SpawnLoadoutEventsListener :
        IEventListener<UnturnedPlayerSpawnedEvent>,
        IEventListener<UnturnedPlayerDeathEvent>
    {
        private readonly ISpawnLoadoutService m_SpawnLoadoutService;

        public SpawnLoadoutEventsListener(ISpawnLoadoutService spawnLoadoutService)
        {
            m_SpawnLoadoutService = spawnLoadoutService;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerSpawnedEvent @event)
        {
            return m_SpawnLoadoutService.HandleSpawnAsync(@event.Player);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDeathEvent @event)
        {
            return m_SpawnLoadoutService.HandleDeathAsync(@event.Player);
        }
    }
}
