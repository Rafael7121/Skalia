using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Players.Connections.Events;

namespace SkaliaDeps
{
    public sealed class AdminFeatureAccessListener :
        IEventListener<UnturnedPlayerConnectedEvent>
    {
        private readonly IAdminFeatureAccessService m_AdminFeatureAccessService;
        private readonly ILogger<AdminFeatureAccessListener> m_Logger;

        public AdminFeatureAccessListener(
            IAdminFeatureAccessService adminFeatureAccessService,
            ILogger<AdminFeatureAccessListener> logger)
        {
            m_AdminFeatureAccessService = adminFeatureAccessService;
            m_Logger = logger;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            // Nao bloqueia o EventBus de conexao aguardando o proximo frame.
            SyncAfterConnectionAsync(@event.Player).Forget();
            return Task.CompletedTask;
        }

        private async UniTask SyncAfterConnectionAsync(UnturnedPlayer player)
        {
            try
            {
                // Aguarda o jogador e o transporte cliente estarem totalmente prontos.
                await UniTask.DelayFrame(1);
                await m_AdminFeatureAccessService.SyncAsync(player);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Nao foi possivel sincronizar os recursos administrativos do jogador conectado.");
            }
        }
    }
}
