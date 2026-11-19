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
    [Command("clear")]
    [CommandDescription("Limpa o inventário de um jogador.")]
    [CommandSyntax("[jogador]")]
    public sealed class CommandClear : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IUnturnedUserDirectory m_UserDirectory;

        public CommandClear(
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
                await PrintAsync(Format("messages:administration:clear_inventory_console_usage"));
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

            var removedItems = ClearInventory(target);
            await PrintAsync(Format("messages:administration:clear_inventory_complete", new
            {
                Count = removedItems,
                Player = GetPlayerName(target)
            }));
        }

        private static int ClearInventory(UnturnedUser user)
        {
            var inventory = user.Player.Player.inventory;
            var removedItems = 0;

            // STORAGE e AREA representam interfaces externas abertas pelo jogador,
            // portanto somente as páginas que pertencem ao inventário são limpas.
            for (byte page = 0; page < PlayerInventory.STORAGE; page++)
            {
                var itemCount = inventory.getItemCount(page);
                for (var index = itemCount - 1; index >= 0; index--)
                {
                    inventory.removeItem(page, (byte)index);
                    removedItems++;
                }
            }

            return removedItems;
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
