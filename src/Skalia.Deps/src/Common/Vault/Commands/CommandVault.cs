using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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
        private readonly IConfiguration m_Configuration;

        public CommandVault(
            IVaultService vaultService,
            IStringLocalizer stringLocalizer,
            IConfiguration configuration,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_VaultService = vaultService;
            m_StringLocalizer = stringLocalizer;
            m_Configuration = configuration;
        }

        protected override async UniTask OnExecuteAsync()
        {
            // 0.1.0-y17.31: vault:enabled (o launcher grava antes de subir o servidor).
            if (!m_Configuration.GetValue("vault:enabled", true))
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} {m_StringLocalizer["messages:common:feature_disabled"].Value}");
                return;
            }

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
