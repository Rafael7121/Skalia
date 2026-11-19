using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("vault")]
    [CommandDescription("Abre seu armazenamento privado e persistente.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandVault : UnturnedCommand
    {
        private readonly IVaultService m_VaultService;
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandVault(
            IVaultService vaultService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_VaultService = vaultService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length != 0)
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} {m_StringLocalizer["messages:vault:usage"].Value}");
                return;
            }

            var user = (UnturnedUser)Context.Actor;
            await m_VaultService.OpenAsync(user.Player);
        }
    }
}
