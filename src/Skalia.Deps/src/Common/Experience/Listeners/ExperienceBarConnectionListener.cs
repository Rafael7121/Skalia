using System;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenMod.API.Eventing;
using OpenMod.Unturned.Players.Connections.Events;
using OpenMod.Unturned.Players.Life.Events;
using OpenMod.Unturned.Zombies.Events;

namespace SkaliaDeps
{
    public sealed class ExperienceBarConnectionListener :
        IEventListener<UnturnedPlayerConnectedEvent>,
        IEventListener<UnturnedPlayerDisconnectedEvent>,
        IEventListener<UnturnedPlayerSpawnedEvent>,
        IEventListener<UnturnedPlayerDeathEvent>,
        IEventListener<UnturnedZombieDyingEvent>
    {
        private readonly IExperienceBarService m_ExperienceBarService;
        private readonly ILogger<ExperienceBarConnectionListener> m_Logger;

        public ExperienceBarConnectionListener(
            IExperienceBarService experienceBarService,
            ILogger<ExperienceBarConnectionListener> logger)
        {
            m_ExperienceBarService = experienceBarService;
            m_Logger = logger;
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerConnectedEvent @event)
        {
            RegisterAfterConnectionAsync(@event.Player).Forget();
            return Task.CompletedTask;
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerDisconnectedEvent @event)
        {
            await m_ExperienceBarService.UnregisterAsync(@event.Player);
        }

        public Task HandleEventAsync(object? sender, UnturnedPlayerSpawnedEvent @event)
        {
            m_ExperienceBarService.RecordPlayerSpawned(@event.Player);
            return Task.CompletedTask;
        }

        public async Task HandleEventAsync(object? sender, UnturnedPlayerDeathEvent @event)
        {
            await m_ExperienceBarService.RecordPlayerDeathAsync(
                @event.Player,
                @event.Instigator.m_SteamID);
        }

        public async Task HandleEventAsync(object? sender, UnturnedZombieDyingEvent @event)
        {
            if (@event.Instigator == null || @event.IsCancelled)
            {
                return;
            }

            await m_ExperienceBarService.RecordZombieKillAsync(
                @event.Instigator,
                @event.Zombie.Zombie.isMega);
        }

        private async UniTaskVoid RegisterAfterConnectionAsync(
            OpenMod.Unturned.Players.UnturnedPlayer player)
        {
            try
            {
                await UniTask.DelayFrame(1);
                await m_ExperienceBarService.RegisterAsync(player);
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception,
                    "Nao foi possivel exibir o HUD de rank para o jogador conectado.");
            }
        }
    }
}
