using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Connections.Events;

namespace SkaliaDeps
{
    public sealed class GodModeConnectionListener :
        IEventListener<UnturnedPlayerConnectedEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>
    {
        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            ReapplySpeedAfterConnectionAsync(@event).Forget();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            GodModeState.ForgetSpeedOverride(@event.Player.SteamId.m_SteamID);
            return Task.CompletedTask;
        }

        private static async UniTaskVoid ReapplySpeedAfterConnectionAsync(
            UnturnedPlayerConnectedEvent @event)
        {
            await UniTask.DelayFrame(1);
            if (GodModeState.IsEnabled(@event.Player.SteamId.m_SteamID))
            {
                GodModeState.ApplySpeed(@event.Player.Player, true);
            }
        }
    }
}
