using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("home")]
    [CommandDescription("Teleporta para uma das suas camas reivindicadas.")]
    [CommandSyntax("[slot]")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandHome : UnturnedCommand
    {
        private readonly IHomeService m_HomeService;
        private readonly IStringLocalizer m_StringLocalizer;
        private readonly IConfiguration m_Configuration;

        public CommandHome(
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

            var user = (UnturnedUser)Context.Actor;
            if (Context.Parameters.Length > 1)
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} {m_StringLocalizer["messages:home:usage"].Value}");
                return;
            }

            await m_HomeService.TeleportAsync(
                user.Player,
                Context.Parameters.Length == 1 ? Context.Parameters[0] : null);
        }
    }
}
