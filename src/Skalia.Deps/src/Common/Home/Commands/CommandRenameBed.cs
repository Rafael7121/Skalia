using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("renomear")]
    [CommandDescription("Renomeia uma das suas camas reivindicadas.")]
    [CommandSyntax("<cama> <novo nome>")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandRenameBed : UnturnedCommand
    {
        private readonly IHomeService m_HomeService;
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandRenameBed(
            IHomeService homeService,
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_HomeService = homeService;
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            if (Context.Parameters.Length < 2)
            {
                await PrintAsync(
                    $"{m_StringLocalizer["prefix"].Value} " +
                    m_StringLocalizer["messages:rename_bed:usage"].Value);
                return;
            }

            var user = (UnturnedUser)Context.Actor;
            await m_HomeService.RenameBedAsync(
                user.Player,
                Context.Parameters[0],
                string.Join(" ", Context.Parameters.Skip(1)));
        }
    }
}
