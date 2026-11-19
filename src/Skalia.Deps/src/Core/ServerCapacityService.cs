using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenMod.API;
using SDG.Unturned;
using Steamworks;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace SkaliaDeps
{
    internal sealed class ServerCapacityService
    {
        private const int HardMaximumPlayers = 100;
        private const int MinimumPlayers = 24;
        private readonly ILogger m_Logger;
        private readonly IRuntime m_Runtime;
        private readonly string m_PluginWorkingDirectory;
        private readonly bool m_Enabled;
        private readonly byte m_MaximumPlayers;
        private readonly bool m_ReloadOnDllChange;
        private bool m_Started;
        private byte m_LastBlockedLimit;
        private FileSystemWatcher? m_AssemblyWatcher;
        private CancellationTokenSource? m_ReloadCancellation;
        private readonly Dictionary<string, (DateTime LastWriteUtc, long Length)>
            m_AssemblyStates = new Dictionary<string, (DateTime, long)>(
                StringComparer.OrdinalIgnoreCase);
        private int m_ReloadScheduled;

        public ServerCapacityService(
            IConfiguration configuration,
            ILogger logger,
            IRuntime runtime,
            string pluginWorkingDirectory)
        {
            m_Logger = logger;
            m_Runtime = runtime;
            m_PluginWorkingDirectory = pluginWorkingDirectory;
            m_Enabled = configuration.GetValue("serverCapacity:enabled", true);
            m_MaximumPlayers = (byte)Clamp(
                configuration.GetValue("serverCapacity:maximumPlayers", HardMaximumPlayers),
                MinimumPlayers,
                HardMaximumPlayers);
            m_ReloadOnDllChange = configuration.GetValue(
                "serverCapacity:reloadOnDllChange",
                true);
        }

        public void Start()
        {
            Stop();
            if (!m_Enabled)
            {
                m_Logger.LogInformation("Expansao automatica de slots desativada.");
                return;
            }

            Provider.onClientConnected += HandleClientConnected;
            Provider.onServerDisconnected += HandleServerDisconnected;
            m_Started = true;
            StartAssemblyWatcher();
            TryAdjustCapacity();
            m_Logger.LogInformation(
                "Expansao automatica de slots ativa: limite atual {Current}, teto {Maximum}, " +
                "reload automatico de DLL {ReloadEnabled}.",
                Provider.maxPlayers,
                m_MaximumPlayers,
                m_ReloadOnDllChange ? "ativo" : "desativado");
        }

        public void Stop()
        {
            if (!m_Started)
            {
                return;
            }

            Provider.onClientConnected -= HandleClientConnected;
            Provider.onServerDisconnected -= HandleServerDisconnected;
            m_Started = false;
            m_LastBlockedLimit = 0;
            StopAssemblyWatcher();
        }

        private void HandleClientConnected()
        {
            TryAdjustCapacity();
        }

        private void HandleServerDisconnected(CSteamID steamId)
        {
            AdjustCapacityAfterDisconnectAsync().Forget();
        }

        private async UniTask AdjustCapacityAfterDisconnectAsync()
        {
            // O evento ocorre antes de Provider remover o jogador da lista.
            // Aguarda um frame para calcular usando a ocupacao efetiva.
            await UniTask.DelayFrame(1);
            if (m_Started)
            {
                TryAdjustCapacity();
            }
        }

        private void TryAdjustCapacity()
        {
            if (!Provider.isServer || Provider.maxPlayers == 0)
            {
                return;
            }

            byte previousLimit = Provider.maxPlayers;
            int connectedPlayers = Provider.clients.Count;
            byte requestedLimit = CalculateTargetLimit(
                previousLimit,
                connectedPlayers,
                m_MaximumPlayers);
            if (requestedLimit == previousLimit)
            {
                return;
            }

            Provider.maxPlayers = requestedLimit;
            byte appliedLimit = Provider.maxPlayers;
            if (requestedLimit > previousLimit && appliedLimit <= previousLimit)
            {
                if (m_LastBlockedLimit != previousLimit)
                {
                    m_LastBlockedLimit = previousLimit;
                    m_Logger.LogWarning(
                        "Nao foi possivel ampliar os slots de {Previous} para {Requested}. " +
                        "Verifique se -MaxPlayers limita o processo na linha de comando.",
                        previousLimit,
                        requestedLimit);
                }

                return;
            }

            m_LastBlockedLimit = 0;
            if (appliedLimit > previousLimit)
            {
                m_Logger.LogInformation(
                    "Capacidade ampliada automaticamente de {Previous} para {Current} " +
                    "ao atingir {Connected} jogadores conectados.",
                    previousLimit,
                    appliedLimit,
                    connectedPlayers);

                // Se alguem entrou na fila enquanto o servidor estava cheio, inicia
                // imediatamente a verificacao agora que existem novos slots.
                Provider.verifyNextPlayerInQueue();
            }
            else if (appliedLimit < previousLimit)
            {
                m_Logger.LogInformation(
                    "Capacidade reduzida automaticamente de {Previous} para {Current} " +
                    "apos a saida de jogadores; ocupacao atual {Connected}.",
                    previousLimit,
                    appliedLimit,
                    connectedPlayers);
            }
        }

        private void StartAssemblyWatcher()
        {
            if (!m_ReloadOnDllChange)
            {
                return;
            }

            string pluginDirectory = Path.GetFullPath(Path.Combine(
                m_PluginWorkingDirectory,
                ".."));
            string addonPath = Path.Combine(pluginDirectory, "SkaliaDeps.dll");
            if (!File.Exists(addonPath))
            {
                m_Logger.LogWarning(
                    "Reload automatico de DLL indisponivel: {AssemblyPath} nao foi encontrado.",
                    addonPath);
                return;
            }

            m_AssemblyStates.Clear();
            foreach (string fileName in new[] { "SkaliaDeps.dll", "SkaliaDeps.dll" })
            {
                var assemblyFile = new FileInfo(Path.Combine(pluginDirectory, fileName));
                if (assemblyFile.Exists)
                {
                    m_AssemblyStates[assemblyFile.FullName] = (
                        assemblyFile.LastWriteTimeUtc,
                        assemblyFile.Length);
                }
            }
            m_ReloadCancellation = new CancellationTokenSource();
            m_AssemblyWatcher = new FileSystemWatcher(
                pluginDirectory,
                "Skalia*.dll")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime |
                               NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            m_AssemblyWatcher.Changed += HandleAssemblyFileChanged;
            m_AssemblyWatcher.Created += HandleAssemblyFileChanged;
            m_AssemblyWatcher.Renamed += HandleAssemblyFileChanged;
        }

        private void StopAssemblyWatcher()
        {
            CancellationTokenSource? cancellation = m_ReloadCancellation;
            m_ReloadCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();

            FileSystemWatcher? watcher = m_AssemblyWatcher;
            m_AssemblyWatcher = null;
            if (watcher != null)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= HandleAssemblyFileChanged;
                watcher.Created -= HandleAssemblyFileChanged;
                watcher.Renamed -= HandleAssemblyFileChanged;
                watcher.Dispose();
            }

            Interlocked.Exchange(ref m_ReloadScheduled, 0);
        }

        private void HandleAssemblyFileChanged(object sender, FileSystemEventArgs eventArgs)
        {
            string fileName = Path.GetFileName(eventArgs.FullPath);
            if (!string.Equals(fileName, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fileName, "SkaliaDeps.dll", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _ = ScheduleReloadAsync();
        }

        private async Task ScheduleReloadAsync()
        {
            CancellationTokenSource? cancellation = m_ReloadCancellation;
            if (!m_Started || cancellation == null ||
                Interlocked.CompareExchange(ref m_ReloadScheduled, 1, 0) != 0)
            {
                return;
            }

            try
            {
                // Aguarda a copia terminar e agrupa os varios eventos emitidos pelo Windows.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
                if (!m_Started)
                {
                    return;
                }

                var changedAssemblies = new List<string>();
                foreach (string assemblyPath in m_AssemblyStates.Keys.ToArray())
                {
                    var assemblyFile = new FileInfo(assemblyPath);
                    assemblyFile.Refresh();
                    var previous = m_AssemblyStates[assemblyPath];
                    if (!assemblyFile.Exists ||
                        (assemblyFile.LastWriteTimeUtc == previous.LastWriteUtc &&
                         assemblyFile.Length == previous.Length))
                    {
                        continue;
                    }

                    m_AssemblyStates[assemblyPath] = (
                        assemblyFile.LastWriteTimeUtc,
                        assemblyFile.Length);
                    changedAssemblies.Add(assemblyFile.Name);
                }
                if (changedAssemblies.Count == 0)
                {
                    return;
                }
                m_Logger.LogWarning(
                    "DLL(s) atualizada(s) detectada(s): {Assemblies}. Disparando openmod reload automaticamente.",
                    string.Join(", ", changedAssemblies));
                await m_Runtime.PerformSoftReloadAsync();
            }
            catch (OperationCanceledException)
            {
                // Esperado durante unload ou durante o proprio reload.
            }
            catch (Exception exception)
            {
                m_Logger.LogError(
                    exception,
                    "Falha ao disparar openmod reload apos atualizar o DLL.");
            }
            finally
            {
                Interlocked.Exchange(ref m_ReloadScheduled, 0);
            }
        }

        internal static byte CalculateTargetLimit(
            byte currentLimit,
            int connectedPlayers,
            byte maximumPlayers)
        {
            if (currentLimit == 0)
            {
                return currentLimit;
            }

            int targetLimit = Math.Min(MinimumPlayers, (int)maximumPlayers);
            while (targetLimit <= connectedPlayers && targetLimit < maximumPlayers)
            {
                targetLimit = Math.Min(maximumPlayers, targetLimit * 2);
            }

            // Nao aumente enquanto o limite atual ainda tem vagas. Esta protecao
            // tambem respeita um MaxPlayers inicial fora dos estagios padrao.
            if (targetLimit > currentLimit && connectedPlayers < currentLimit)
            {
                return currentLimit;
            }

            return (byte)targetLimit;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
