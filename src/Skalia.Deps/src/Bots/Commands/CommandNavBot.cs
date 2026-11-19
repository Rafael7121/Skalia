using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Cysharp.Threading.Tasks;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using SDG.Unturned;
using UnityEngine;

namespace SkaliaDeps.Commands
{
    // Intencionalmente sem descricao ou sintaxe: nao documentar este comando.
    [Command("navbot")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandNavBot : UnturnedCommand
    {
        private static readonly HashSet<ulong> s_EnabledViewers = new HashSet<ulong>();
        private readonly ISkaliaBotService m_BotService;

        public CommandNavBot(
            ISkaliaBotService botService,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_BotService = botService;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 1
                || !bool.TryParse(Context.Parameters[0].ToString(), out var enabled))
            {
                await PrintAsync("Uso: /navbot true [nome do bot] | /navbot false");
                return;
            }
            var botName = Context.Parameters.Length > 1
                ? string.Join(" ", Context.Parameters.Skip(1))
                : null;

            var user = (UnturnedUser)Context.Actor;
            var steamId = user.SteamId.m_SteamID;
            if (!enabled)
            {
                s_EnabledViewers.Remove(steamId);
                await PrintAsync("Navbot desativado.");
                return;
            }

            // Re-enabling restarts the monitor so a different bot can be selected.
            s_EnabledViewers.Remove(steamId);
            await UniTask.Yield();
            s_EnabledViewers.Add(steamId);
            MonitorAsync(steamId, botName).Forget();
            await PrintAsync("Navbot ativado. Use /navbot false para desativar.");
        }

        private async UniTaskVoid MonitorAsync(ulong steamId, string? botName)
        {
            string? previousSignature = null;
            try
            {
                while (s_EnabledViewers.Contains(steamId))
                {
                    await UniTask.SwitchToMainThread();
                    if (!Provider.clients.Any(client => client.playerID.steamID.m_SteamID == steamId))
                    {
                        // The viewer left; stop instead of printing to a missing player.
                        s_EnabledViewers.Remove(steamId);
                        return;
                    }

                    var snapshot = await m_BotService.GetNavigationSnapshotAsync(botName);
                    if (snapshot == null)
                    {
                        await PrintAsync("Bot ou navegacao indisponivel.");
                        s_EnabledViewers.Remove(steamId);
                        return;
                    }

                    var signature = CreateSignature(snapshot);
                    if (!string.Equals(signature, previousSignature, StringComparison.Ordinal))
                    {
                        previousSignature = signature;
                        await PrintSnapshotAsync(snapshot);
                    }

                    await UniTask.Delay(TimeSpan.FromSeconds(1));
                }
            }
            catch (Exception)
            {
                s_EnabledViewers.Remove(steamId);
            }
        }

        private async UniTask PrintSnapshotAsync(BotNavigationSnapshot snapshot)
        {
            await PrintAsync(
                $"Estado={snapshot.State} Pos={Format(snapshot.Position)} "
                + $"Destino={Format(snapshot.Destination)} Pontos={snapshot.Points.Count} "
                + $"Atual={snapshot.CurrentPointIndex + 1} Pendente={snapshot.PathPending}");

            if (snapshot.Points.Count == 0)
            {
                await PrintAsync("Rota direta: nenhum ponto intermediario calculado.");
                return;
            }

            const int maximumDisplayedPoints = 20;
            var displayed = Mathf.Min(snapshot.Points.Count, maximumDisplayedPoints);
            for (var index = 0; index < displayed; index++)
            {
                var marker = index == snapshot.CurrentPointIndex ? " ->" : "";
                await PrintAsync($"{marker} P{index + 1}: {Format(snapshot.Points[index])}");
            }

            if (snapshot.Points.Count > displayed)
            {
                await PrintAsync($"... {snapshot.Points.Count - displayed} ponto(s) adicional(is).");
            }
        }

        private static string CreateSignature(BotNavigationSnapshot snapshot)
        {
            return string.Join("|",
                snapshot.State,
                snapshot.CurrentPointIndex.ToString(CultureInfo.InvariantCulture),
                snapshot.Points.Count.ToString(CultureInfo.InvariantCulture),
                Mathf.RoundToInt(snapshot.Destination.x).ToString(CultureInfo.InvariantCulture),
                Mathf.RoundToInt(snapshot.Destination.z).ToString(CultureInfo.InvariantCulture),
                snapshot.PathPending.ToString());
        }

        private static string Format(Vector3 point)
        {
            return $"({point.x:0.0}, {point.y:0.0}, {point.z:0.0})";
        }
    }
}
