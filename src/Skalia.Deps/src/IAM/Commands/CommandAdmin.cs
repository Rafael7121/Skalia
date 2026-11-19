using System;
using Microsoft.Extensions.Localization;
using OpenMod.API.Permissions;
using OpenMod.API.Users;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("admin")]
    [CommandDescription("Concede o cargo Admin sem acesso aos comandos de bot.")]
    [CommandSyntax("<jogador|SteamID>")]
    public sealed class CommandAdmin : AdminPermissionCommandBase
    {
        public CommandAdmin(
            IAdminFeatureAccessService adminFeatureAccessService,
            IPermissionRoleStore roleStore,
            IStringLocalizer stringLocalizer,
            IUnturnedUserDirectory userDirectory,
            IUserManager userManager,
            IServiceProvider serviceProvider) : base(
                adminFeatureAccessService,
                roleStore,
                stringLocalizer,
                userDirectory,
                userManager,
                serviceProvider)
        {
        }

        protected override bool GrantPermission => true;
    }
}
