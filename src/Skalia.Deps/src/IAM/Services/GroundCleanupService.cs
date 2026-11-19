using System;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OpenMod.API.Ioc;
using SDG.NetTransport;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    [PluginServiceImplementation(Lifetime = ServiceLifetime.Singleton)]
    public sealed class GroundCleanupService : IGroundCleanupService, IDisposable
    {
        private const int MinimumIntervalSeconds = 120;
        private const int DefaultItemsPerFrame = 25;
        private const int MaximumItemsPerFrame = 250;
        private const int InspectedItemsPerRemovalBudget = 10;
        private readonly IConfiguration m_Configuration;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly ILogger<GroundCleanupService> m_Logger;
        private readonly ClientStaticMethod<byte, byte, uint, bool>? m_SendDestroyItem;
        private readonly float m_MinimumItemAgeSeconds;
        private readonly int m_ItemsPerFrame;
        private CancellationTokenSource? m_CancellationTokenSource;
        private DateTimeOffset? m_LastCleanupAt;
        private DateTimeOffset? m_NextCleanupAt;

        public GroundCleanupService(
            IConfiguration configuration,
            IStringLocalizer stringLocalizer,
            ILogger<GroundCleanupService> logger)
        {
            m_Configuration = configuration;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
            m_MinimumItemAgeSeconds = Math.Max(0,
                configuration.GetValue("groundCleanup:minimumItemAgeSeconds", 400));
            m_ItemsPerFrame = Math.Max(1, Math.Min(MaximumItemsPerFrame,
                configuration.GetValue("groundCleanup:itemsPerFrame", DefaultItemsPerFrame)));
            m_SendDestroyItem = typeof(ItemManager)
                .GetField("SendDestroyItem", BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null) as ClientStaticMethod<byte, byte, uint, bool>;
        }

        public void Start()
        {
            Stop();

            if (!m_Configuration.GetValue("groundCleanup:enabled", true))
            {
                m_Logger.LogInformation("Limpeza automatica do chao desativada na configuracao.");
                return;
            }

            if (m_SendDestroyItem == null)
            {
                m_Logger.LogError("Limpeza automatica nao iniciada: metodo de rede do ItemManager nao encontrado.");
                return;
            }

            var configuredInterval = m_Configuration.GetValue("groundCleanup:intervalSeconds", 600);
            var intervalSeconds = Math.Max(MinimumIntervalSeconds, configuredInterval);
            m_LastCleanupAt = null;
            m_NextCleanupAt = DateTimeOffset.UtcNow.AddSeconds(intervalSeconds);
            m_CancellationTokenSource = new CancellationTokenSource();
            RunAsync(intervalSeconds, m_CancellationTokenSource.Token).Forget();
            m_Logger.LogInformation(
                "Limpeza automatica do chao iniciada a cada {Interval}s; somente itens com pelo menos {MinimumAge}s serao removidos em lotes de ate {ItemsPerFrame} por frame.",
                intervalSeconds,
                m_MinimumItemAgeSeconds,
                m_ItemsPerFrame);
        }

        public void Stop()
        {
            if (m_CancellationTokenSource == null)
            {
                return;
            }

            m_CancellationTokenSource.Cancel();
            m_CancellationTokenSource.Dispose();
            m_CancellationTokenSource = null;
            m_LastCleanupAt = null;
            m_NextCleanupAt = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private async UniTaskVoid RunAsync(int intervalSeconds, CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await DelayAsync(intervalSeconds - 60, cancellationToken);
                    Broadcast("messages:cleanup:warning_minute");

                    await DelayAsync(40, cancellationToken);
                    Broadcast("messages:cleanup:warning_seconds", new { Seconds = 20 });

                    await DelayAsync(10, cancellationToken);
                    Broadcast("messages:cleanup:warning_seconds", new { Seconds = 10 });

                    await DelayAsync(10, cancellationToken);
                    var removedItems = await RemoveDroppedItemsIncrementallyAsync(
                        m_MinimumItemAgeSeconds,
                        cancellationToken);
                    m_LastCleanupAt = DateTimeOffset.UtcNow;
                    m_NextCleanupAt = m_LastCleanupAt.Value.AddSeconds(intervalSeconds);
                    Broadcast("messages:cleanup:automatic_complete", new { Count = removedItems });
                    m_Logger.LogInformation(
                        "Limpeza automatica concluida: {Count} item(ns) derrubado(s) removido(s).",
                        removedItems);
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do plugin.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "O ciclo de limpeza automatica do chao foi interrompido.");
            }
        }

        private static async UniTask DelayAsync(int seconds, CancellationToken cancellationToken)
        {
            if (seconds <= 0)
            {
                return;
            }

            await UniTask.Delay(TimeSpan.FromSeconds(seconds), cancellationToken: cancellationToken);
            await UniTask.SwitchToMainThread(cancellationToken);
        }

        public int RemoveDroppedItems()
        {
            // O comando administrativo /clearg continua removendo todos os itens
            // derrubados imediatamente, independentemente da idade.
            var removedItems = RemoveDroppedItemsOlderThan(0);
            m_LastCleanupAt = DateTimeOffset.UtcNow;
            return removedItems;
        }

        private async UniTask<int> RemoveDroppedItemsIncrementallyAsync(
            float minimumAgeSeconds,
            CancellationToken cancellationToken)
        {
            if (ItemManager.regions == null || m_SendDestroyItem == null)
            {
                return 0;
            }

            var removedItems = 0;
            var removedThisFrame = 0;
            var inspectedThisFrame = 0;
            var inspectedItemsPerFrame = Math.Max(
                m_ItemsPerFrame,
                m_ItemsPerFrame * InspectedItemsPerRemovalBudget);
            var now = Time.realtimeSinceStartup;

            for (byte x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    var region = ItemManager.regions[x, y];
                    for (var index = region.items.Count - 1; index >= 0; index--)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // A lista pode mudar enquanto aguardamos o proximo frame.
                        // Reposiciona o indice para continuar a varredura com seguranca.
                        if (index >= region.items.Count)
                        {
                            index = region.items.Count - 1;
                            if (index < 0)
                            {
                                break;
                            }
                        }

                        var itemData = region.items[index];
                        inspectedThisFrame++;
                        if (itemData.isDropped &&
                            now - itemData.lastDropped >= minimumAgeSeconds)
                        {
                            region.items.RemoveAt(index);
                            m_SendDestroyItem.Invoke(
                                ENetReliability.Reliable,
                                Regions.GatherClientConnections(x, y, ItemManager.ITEM_REGIONS),
                                x,
                                y,
                                itemData.instanceID,
                                false);
                            removedItems++;
                            removedThisFrame++;
                        }

                        if (removedThisFrame < m_ItemsPerFrame &&
                            inspectedThisFrame < inspectedItemsPerFrame)
                        {
                            continue;
                        }

                        removedThisFrame = 0;
                        inspectedThisFrame = 0;
                        await UniTask.DelayFrame(1, cancellationToken: cancellationToken);

                        if (ItemManager.regions == null)
                        {
                            return removedItems;
                        }

                        region = ItemManager.regions[x, y];
                    }
                }
            }

            return removedItems;
        }

        public TimeSpan GetAutomaticAnnouncementDelay(DateTimeOffset now)
        {
            const int quietWindowSeconds = 90;
            const int resumeAfterCleanupSeconds = 120;

            if (m_LastCleanupAt.HasValue &&
                now <= m_LastCleanupAt.Value.AddSeconds(quietWindowSeconds))
            {
                return PositiveDelay(
                    m_LastCleanupAt.Value.AddSeconds(resumeAfterCleanupSeconds),
                    now);
            }

            if (m_NextCleanupAt.HasValue &&
                now >= m_NextCleanupAt.Value.AddSeconds(-quietWindowSeconds) &&
                now <= m_NextCleanupAt.Value.AddSeconds(quietWindowSeconds))
            {
                return PositiveDelay(
                    m_NextCleanupAt.Value.AddSeconds(resumeAfterCleanupSeconds),
                    now);
            }

            return TimeSpan.Zero;
        }

        private static TimeSpan PositiveDelay(DateTimeOffset target, DateTimeOffset now)
        {
            var delay = target - now;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        private int RemoveDroppedItemsOlderThan(float minimumAgeSeconds)
        {
            if (ItemManager.regions == null || m_SendDestroyItem == null)
            {
                return 0;
            }

            var removedItems = 0;
            var now = Time.realtimeSinceStartup;
            for (byte x = 0; x < Regions.WORLD_SIZE; x++)
            {
                for (byte y = 0; y < Regions.WORLD_SIZE; y++)
                {
                    var region = ItemManager.regions[x, y];
                    for (var index = region.items.Count - 1; index >= 0; index--)
                    {
                        var itemData = region.items[index];
                        if (!itemData.isDropped)
                        {
                            continue;
                        }

                        if (now - itemData.lastDropped < minimumAgeSeconds)
                        {
                            continue;
                        }

                        region.items.RemoveAt(index);
                        m_SendDestroyItem.Invoke(
                            ENetReliability.Reliable,
                            Regions.GatherClientConnections(x, y, ItemManager.ITEM_REGIONS),
                            x,
                            y,
                            itemData.instanceID,
                            false);
                        removedItems++;
                    }
                }
            }

            return removedItems;
        }

        private void Broadcast(string key, object? arguments = null)
        {
            var prefix = m_StringLocalizer["prefix"].Value;
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            ChatManager.serverSendMessage(
                $"{prefix} {message}",
                ServerChatPalette.BroadcastText,
                useRichTextFormatting: true,
                iconURL: Provider.configData.Browser.Icon);
        }
    }
}
