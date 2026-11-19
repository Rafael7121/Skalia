using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("spy")]
    [CommandDescription("Solicita uma captura de tela nativa de um jogador.")]
    [CommandSyntax("<jogador>")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandSpy : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IUnturnedUserDirectory m_UserDirectory;

        public CommandSpy(
            IStringLocalizer stringLocalizer,
            IUnturnedUserDirectory userDirectory,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
            m_UserDirectory = userDirectory;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length == 0)
            {
                await PrintAsync(Format("messages:administration:spy_usage"));
                return;
            }

            var searchedName = string.Join(" ", Context.Parameters);
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

            var actor = (UnturnedUser)Context.Actor;
            await UniTask.SwitchToMainThread();
            match.User.Player.Player.sendScreenshot(actor.SteamId);

            await PrintAsync(Format("messages:administration:spy_complete", new
            {
                Player = match.User.Player.SteamPlayer.playerID.characterName
            }));
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return PrivateMessageFormatter.AppendNotice(
                m_StringLocalizer,
                $"{m_StringLocalizer["prefix"].Value} {message}");
        }
    }
}
