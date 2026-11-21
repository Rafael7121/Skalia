using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Players;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("tpa")]
    [CommandDescription("Envia e gerencia pedidos de teleporte entre jogadores.")]
    [CommandSyntax("<jogador|a [jogador]|r [jogador]|c>")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandTpa : UnturnedCommand
    {
        private readonly ITpaService m_TpaService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IConfiguration m_Configuration;
        private readonly IUnturnedUserDirectory m_UserDirectory;

        public CommandTpa(
            ITpaService tpaService,
            IStringLocalizer stringLocalizer,
            IConfiguration configuration,
            IUnturnedUserDirectory userDirectory,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_TpaService = tpaService;
            m_StringLocalizer = stringLocalizer;
            m_Configuration = configuration;
            m_UserDirectory = userDirectory;
        }

        protected override async UniTask OnExecuteAsync()
        {
            // 0.1.0-y17.31: tpa:enabled (o launcher grava antes de subir o servidor).
            if (!m_Configuration.GetValue("tpa:enabled", true))
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} {m_StringLocalizer["messages:common:feature_disabled"].Value}");
                return;
            }

            var user = (UnturnedUser)Context.Actor;

            if (Context.Parameters.Length == 0)
            {
                await PrintUsageAsync();
                return;
            }

            var action = Context.Parameters[0].ToLowerInvariant();
            switch (action)
            {
                case "a":
                case "aceitar":
                case "accept":
                    var acceptedSender = await ResolveOptionalSenderAsync(1);
                    if (!acceptedSender.Success)
                    {
                        return;
                    }

                    await m_TpaService.AcceptAsync(user.Player, acceptedSender.Player);
                    return;

                case "r":
                case "recusar":
                case "decline":
                case "deny":
                    var declinedSender = await ResolveOptionalSenderAsync(1);
                    if (!declinedSender.Success)
                    {
                        return;
                    }

                    await m_TpaService.DeclineAsync(user.Player, declinedSender.Player);
                    return;

                case "c":
                case "cancelar":
                case "cancel":
                    await m_TpaService.CancelAsync(user.Player);
                    return;
            }

            var targetIndex = 0;
            if (action == "enviar" || action == "send")
            {
                if (Context.Parameters.Length < 2)
                {
                    await PrintUsageAsync();
                    return;
                }

                targetIndex = 1;
            }

            var searchedName = string.Join(" ", Context.Parameters.Skip(targetIndex));
            var match = PlayerNameMatcher.Find(searchedName, m_UserDirectory.GetOnlineUsers());
            if (match.IsAmbiguous)
            {
                await PrintAsync(Format("messages:common:player_ambiguous", new
                {
                    Player = searchedName,
                    Players = PlayerNameMatcher.FormatConflicts(match.Conflicts)
                }));
                return;
            }

            if (match.User == null)
            {
                await PrintAsync(Format("messages:common:player_not_found", new { Player = searchedName }));
                return;
            }

            await m_TpaService.SendAsync(user.Player, match.User.Player);
        }

        private async UniTask<(bool Success, UnturnedPlayer? Player)> ResolveOptionalSenderAsync(int startIndex)
        {
            if (Context.Parameters.Length <= startIndex)
            {
                return (true, null);
            }

            var searchedName = string.Join(" ", Context.Parameters.Skip(startIndex));
            var match = PlayerNameMatcher.Find(searchedName, m_UserDirectory.GetOnlineUsers());
            if (match.IsAmbiguous)
            {
                await PrintAsync(Format("messages:common:player_ambiguous", new
                {
                    Player = searchedName,
                    Players = PlayerNameMatcher.FormatConflicts(match.Conflicts)
                }));
                return (false, null);
            }

            if (match.User == null)
            {
                await PrintAsync(Format("messages:common:player_not_found", new { Player = searchedName }));
                return (false, null);
            }

            return (true, match.User.Player);
        }

        private async UniTask PrintUsageAsync()
        {
            await PrintAsync(Format("messages:tpa:usage"));
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;

            return $"{m_StringLocalizer["prefix"].Value} {message}";
        }
    }
}
