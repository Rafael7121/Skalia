using System.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Chat.Events;
using OpenMod.Unturned.Players.Connections.Events;
using OpenMod.Unturned.Players.Crafting.Events;
using OpenMod.Unturned.Players.Equipment.Events;
using OpenMod.Unturned.Players.Inventory.Events;
using OpenMod.Unturned.Players.UI.Events;
using OpenMod.Unturned.Players.Voice.Events;
using OpenMod.Unturned.Vehicles.Events;

namespace SkaliaDeps
{
    public sealed class PlayerActivityListener :
        IEventListener<UnturnedPlayerConnectedEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>,
        IEventListener<UnturnedPlayerChattingEvent>,
        IEventListener<UnturnedPlayerCraftingEvent>,
        IEventListener<UnturnedPlayerItemEquippedEvent>,
        IEventListener<UnturnedPlayerInventoryUpdatedEvent>,
        IEventListener<UnturnedPlayerButtonClickedEvent>,
        IEventListener<UnturnedPlayerTextInputtedEvent>,
        IEventListener<UnturnedPlayerTalkingUpdatedEvent>,
        IEventListener<UnturnedPlayerEnteredVehicleEvent>,
        IEventListener<UnturnedPlayerExitedVehicleEvent>
    {
        private readonly IPlayerActivityService m_PlayerActivityService;

        public PlayerActivityListener(IPlayerActivityService playerActivityService)
        {
            m_PlayerActivityService = playerActivityService;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            m_PlayerActivityService.RegisterPlayer(@event.Player.SteamPlayer.player);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            m_PlayerActivityService.UnregisterPlayer(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerChattingEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerCraftingEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerItemEquippedEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerInventoryUpdatedEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerButtonClickedEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerTextInputtedEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerTalkingUpdatedEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerEnteredVehicleEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerExitedVehicleEvent @event)
        {
            MarkActivity(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        private void MarkActivity(ulong playerId)
        {
            m_PlayerActivityService.MarkActivity(playerId);
        }
    }
}
