using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps
{
    public sealed class AutomaticAnnouncementService : IDisposable
    {
        private const int MinimumIntervalSeconds = 30;
        private readonly IConfiguration m_Configuration;
        private readonly IGroundCleanupService m_GroundCleanupService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly Microsoft.Extensions.Logging.ILogger m_Logger;
        private CancellationTokenSource? m_CancellationTokenSource;
        private string[] m_Messages = Array.Empty<string>();
        private int m_NextMessageIndex;

        public AutomaticAnnouncementService(
            IConfiguration configuration,
            IGroundCleanupService groundCleanupService,
            IStringLocalizer stringLocalizer,
            Microsoft.Extensions.Logging.ILogger logger)
        {
            m_Configuration = configuration;
            m_GroundCleanupService = groundCleanupService;
            m_StringLocalizer = stringLocalizer;
            m_Logger = logger;
        }

        public void Start()
        {
            Stop();

            if (!m_Configuration.GetValue("automaticMessages:enabled", true))
            {
                m_Logger.LogInformation("Mensagens automaticas globais desativadas na configuracao.");
                return;
            }

            m_Messages = m_Configuration
                .GetSection("automaticMessages:messages")
                .GetChildren()
                .Select(section => section.Value?.Trim())
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message!)
                .ToArray();

            if (m_Messages.Length == 0)
            {
                m_Logger.LogWarning("Mensagens automaticas ativadas, mas nenhuma mensagem foi configurada.");
                return;
            }

            var configuredInterval = m_Configuration.GetValue("automaticMessages:intervalSeconds", 300);
            var intervalSeconds = Math.Max(MinimumIntervalSeconds, configuredInterval);
            if (intervalSeconds != configuredInterval)
            {
                m_Logger.LogWarning(
                    "Intervalo de mensagens automaticas ajustado de {Configured}s para o minimo de {Minimum}s.",
                    configuredInterval,
                    MinimumIntervalSeconds);
            }

            m_NextMessageIndex = 0;
            m_CancellationTokenSource = new CancellationTokenSource();
            RunAsync(intervalSeconds, m_CancellationTokenSource.Token).Forget();
            m_Logger.LogInformation(
                "Mensagens automaticas globais iniciadas: {Count} mensagens, intervalo de {Interval}s.",
                m_Messages.Length,
                intervalSeconds);
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
                    await UniTask.Delay(
                        TimeSpan.FromSeconds(intervalSeconds),
                        cancellationToken: cancellationToken);
                    await UniTask.SwitchToMainThread(cancellationToken);

                    await WaitForCleanupQuietPeriodAsync(cancellationToken);

                    if (Provider.clients.Count == 0)
                    {
                        continue;
                    }

                    var prefix = m_StringLocalizer["prefix"].Value;
                    var message = m_Messages[m_NextMessageIndex];
                    m_NextMessageIndex = (m_NextMessageIndex + 1) % m_Messages.Length;

                    ChatManager.serverSendMessage(
                        $"{prefix} {message}",
                        ServerChatPalette.BroadcastText,
                        useRichTextFormatting: true,
                        iconURL: Provider.configData.Browser.Icon);
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do plugin.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(exception, "O ciclo de mensagens automaticas foi interrompido.");
            }
        }

        private async UniTask WaitForCleanupQuietPeriodAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var delay = m_GroundCleanupService.GetAutomaticAnnouncementDelay(DateTimeOffset.UtcNow);
                if (delay <= TimeSpan.Zero)
                {
                    return;
                }

                m_Logger.LogInformation(
                    "Dica automatica adiada por {Seconds}s para nao coincidir com a limpeza do chao.",
                    (int)Math.Ceiling(delay.TotalSeconds));
                await UniTask.Delay(delay, cancellationToken: cancellationToken);
                await UniTask.SwitchToMainThread(cancellationToken);
            }
        }
    }
}
