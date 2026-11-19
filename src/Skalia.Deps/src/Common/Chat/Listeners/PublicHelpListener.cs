using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.API.Eventing;
using OpenMod.API.Permissions;
using OpenMod.Core.Commands.Events;
using OpenMod.Unturned.Users;

namespace SkaliaDeps
{
    public sealed class PublicHelpListener : IEventListener<CommandExecutingEvent>
    {
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IPermissionChecker m_PermissionChecker;

        public PublicHelpListener(
            IStringLocalizer stringLocalizer,
            IPermissionChecker permissionChecker)
        {
            m_StringLocalizer = stringLocalizer;
            m_PermissionChecker = permissionChecker;
        }

        public async Task HandleEventAsync(object? sender, CommandExecutingEvent @event)
        {
            if (!(@event.Actor is UnturnedUser user) ||
                !string.Equals(@event.CommandContext.CommandRegistration?.Name, "help",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            @event.IsCancelled = true;
            var prefix = m_StringLocalizer["prefix"].Value;
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:header"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:tpa"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:home"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:vault"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:inventory_sort"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:call_vote"].Value}");

            var fullHelp = await m_PermissionChecker.CheckPermissionAsync(
                user,
                "Skalia.Deps:help.full");
            if (fullHelp != PermissionGrantResult.Grant)
            {
                return;
            }

            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:admin_header"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:admin_players"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:admin_items"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:admin_world"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:admin_server"].Value}");

            // A negacao explicita no cargo Admin faz com que somente o
            // superadmin com permissao global direta veja esta ultima secao.
            var botHelp = await m_PermissionChecker.CheckPermissionAsync(
                user,
                "Skalia.Deps:commands.addbot");
            if (botHelp != PermissionGrantResult.Grant)
            {
                return;
            }

            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:bot_header"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:bot_creation"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:bot_control"].Value}");
            await user.Player.PrintMessageAsync(
                $"{prefix} {m_StringLocalizer["messages:help:bot_diagnostics"].Value}");
        }
    }
}
