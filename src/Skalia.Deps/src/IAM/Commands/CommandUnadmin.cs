using System;
using Microsoft.Extensions.Localization;
using OpenMod.API.Permissions;
using OpenMod.API.Users;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("unadmin")]
    [CommandDescription("Remove o cargo Admin concedido pelo addon.")]
    [CommandSyntax("<jogador|SteamID>")]
    public sealed class CommandUnadmin : AdminPermissionCommandBase
    {
        public CommandUnadmin(
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

        protected override bool GrantPermission => false;
    }
}
