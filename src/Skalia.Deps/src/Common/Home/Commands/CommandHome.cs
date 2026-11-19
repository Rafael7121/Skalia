using System;
using Cysharp.Threading.Tasks;
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

        public CommandHome(
            IHomeService homeService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_HomeService = homeService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
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
