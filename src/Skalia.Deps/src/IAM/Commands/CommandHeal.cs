using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;
using SDG.Unturned;

namespace SkaliaDeps.Commands
{
    [Command("heal")]
    [CommandDescription("Restaura todos os status de um jogador para 100%.")]
    [CommandSyntax("[jogador]")]
    public sealed class CommandHeal : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IUnturnedUserDirectory m_UserDirectory;

        public CommandHeal(
            IStringLocalizer stringLocalizer,
            IUnturnedUserDirectory userDirectory,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
            m_UserDirectory = userDirectory;
        }

        protected override async UniTask OnExecuteAsync()
        {
            var actor = Context.Actor as UnturnedUser;
            UnturnedUser? target = actor;

            if (Context.Parameters.Length == 0 && target == null)
            {
                await PrintAsync(Format("messages:administration:heal_console_usage"));
                return;
            }

            if (Context.Parameters.Length > 0)
            {
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

                target = match.User;
            }

            await UniTask.SwitchToMainThread();
            // target somente pode permanecer nulo quando o terminal omite o alvo,
            // situação tratada antes da busca.
            if (target == null)
            {
                return;
            }

            var life = target.Player.Player.life;
            if (!life.IsAlive)
            {
                await PrintAsync(Format("messages:administration:heal_dead", new
                {
                    Player = GetPlayerName(target)
                }));
                return;
            }

            life.askHeal(100, true, true);
            life.serverModifyFood(100f - life.food);
            life.serverModifyWater(100f - life.water);
            life.serverModifyVirus(100f - life.virus);
            life.serverModifyStamina(100f - life.stamina);
            life.simulatedModifyOxygen(100f - life.oxygen);

            await PrintAsync(Format("messages:administration:heal_complete", new
            {
                Player = GetPlayerName(target)
            }));
        }

        private static string GetPlayerName(UnturnedUser user)
        {
            return user.Player.SteamPlayer.playerID.characterName;
        }

        private string Format(string key, object? arguments = null)
        {
            var message = arguments == null
                ? m_StringLocalizer[key].Value
                : m_StringLocalizer[key, arguments].Value;
            return PrivateMessageFormatter.AppendNoticeIfPlayer(
                m_StringLocalizer,
                $"{m_StringLocalizer["prefix"].Value} {message}",
                Context.Actor);
        }
    }
}
