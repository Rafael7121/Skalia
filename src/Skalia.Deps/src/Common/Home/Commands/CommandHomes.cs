using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("homes")]
    [CommandDescription("Lista suas camas ativas e seus nomes.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandHomes : UnturnedCommand
    {
        private readonly IHomeService m_HomeService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IConfiguration m_Configuration;

        public CommandHomes(
            IHomeService homeService,
            IStringLocalizer stringLocalizer,
            IConfiguration configuration,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_HomeService = homeService;
            m_StringLocalizer = stringLocalizer;
            m_Configuration = configuration;
        }

        protected override async UniTask OnExecuteAsync()
        {
            // 0.1.0-y17.31: home:enabled (o launcher grava antes de subir o servidor).
            if (!m_Configuration.GetValue("home:enabled", true))
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} {m_StringLocalizer["messages:common:feature_disabled"].Value}");
                return;
            }

            if (Context.Parameters.Length != 0)
            {
                return;
            }

            await m_HomeService.ListBedsAsync(((UnturnedUser)Context.Actor).Player);
        }
    }
}
