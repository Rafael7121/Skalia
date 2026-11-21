using System;
using System.IO;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SDG.Unturned;

namespace SkaliaDeps
{
    /// <summary>
    /// Comunicacao simples com o launcher SKALIA por arquivos na pasta do perfil
    /// (Servers/&lt;id&gt;): skalia.loading enquanto a malha nova dos bots e
    /// montada, skalia.ready quando o mapa e a malha terminam e
    /// skalia.stop para salvar e desligar o servidor.
    /// </summary>
    internal static class SkaliaLauncherBridge
    {
        private static bool s_Running;

        private static string ProfileDirectory =>
            Path.Combine(ReadWrite.PATH, "Servers", Provider.serverID ?? "Skalia");

        public static void Start(ILogger logger)
        {
            if (s_Running)
            {
                return;
            }
            s_Running = true;
            WatchStopFileAsync(logger).Forget();
        }

        public static void Stop()
        {
            s_Running = false;
        }

        // 0.1.0-y17.33: while the bots' map is prepared, skalia.loading holds
        // the share done (0-100, one line); MarkReady removes it.
        public static void WriteLoading(int percent, ILogger logger)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(ProfileDirectory, "skalia.loading"),
                    Math.Max(0, Math.Min(100, percent)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SKALIA: nao foi possivel avisar o launcher do progresso da preparacao dos bots.");
            }
        }

        public static void MarkReady(ILogger logger)
        {
            try
            {
                File.Delete(Path.Combine(ProfileDirectory, "skalia.loading"));
            }
            catch
            {
            }
            try
            {
                File.WriteAllText(
                    Path.Combine(ProfileDirectory, "skalia.ready"),
                    DateTime.UtcNow.ToString("o"));
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "SKALIA: nao foi possivel avisar o launcher que o servidor esta pronto.");
            }
        }

        private static async UniTask WatchStopFileAsync(ILogger logger)
        {
            while (s_Running)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(2), ignoreTimeScale: true);
                if (!s_Running)
                {
                    return;
                }
                string stopFile = Path.Combine(ProfileDirectory, "skalia.stop");
                bool requested;
                try
                {
                    requested = File.Exists(stopFile);
                    if (requested)
                    {
                        File.Delete(stopFile);
                    }
                }
                catch
                {
                    continue;
                }
                if (!requested)
                {
                    continue;
                }
                await UniTask.SwitchToMainThread();
                logger.LogInformation("SKALIA: o launcher pediu para salvar e desligar o servidor.");
                try
                {
                    File.Delete(Path.Combine(ProfileDirectory, "skalia.ready"));
                }
                catch
                {
                }
                Provider.shutdown(0, "SKALIA");
                return;
            }
        }
    }
}
