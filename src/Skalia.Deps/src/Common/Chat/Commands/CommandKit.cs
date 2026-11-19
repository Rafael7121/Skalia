using System;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.Localization;
using OpenMod.Core.Commands;
using OpenMod.Unturned.Commands;
using OpenMod.Unturned.Users;

namespace SkaliaDeps.Commands
{
    [Command("kit")]
    [CommandAlias("kits")]
    [CommandDescription("Informa sobre a disponibilidade de kits no Survival.")]
    [CommandActor(typeof(UnturnedUser))]
    public sealed class CommandKit : UnturnedCommand
    {
        private readonly IStringLocalizer m_StringLocalizer;

        public CommandKit(
            IStringLocalizer stringLocalizer,
            IServiceProvider serviceProvider) : base(serviceProvider)
        {
            m_StringLocalizer = stringLocalizer;
        }

        protected override async UniTask OnExecuteAsync()
        {
            await PrintAsync(
                $"{m_StringLocalizer["prefix"].Value} " +
                m_StringLocalizer["messages:kit:unavailable"].Value);
        }
    }
}
